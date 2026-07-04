using System.Net;
using ReviewDojo.Generator;
using Xunit;

public class OpenAiCompatibleClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"files\\\":[]}\"}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    [Fact]
    public async Task PostsToChatCompletions_AndReturnsMessageContent()
    {
        var stub = new StubHandler();
        var client = new OpenAiCompatibleClient(new HttpClient(stub), "http://localhost:1234/v1", null);

        var res = await client.CompleteAsync(new LlmRequest(
            "local-model", "SYSTEM PROMPT", new[] { new LlmMessage("user", "the user content") }));

        Assert.Equal("{\"files\":[]}", res);
        Assert.EndsWith("/v1/chat/completions", stub.Last!.RequestUri!.ToString());
        Assert.Contains("\"model\":\"local-model\"", stub.LastBody);
        Assert.Contains("SYSTEM PROMPT", stub.LastBody);
        Assert.Contains("the user content", stub.LastBody);
    }

    [Fact]
    public async Task SendsBearerAuth_WhenApiKeyProvided()
    {
        var stub = new StubHandler();
        var client = new OpenAiCompatibleClient(new HttpClient(stub), "http://localhost:1234/v1/", "secret-key");
        await client.CompleteAsync(new LlmRequest("m", "s", new[] { new LlmMessage("user", "u") }));
        Assert.Equal("Bearer", stub.Last!.Headers.Authorization?.Scheme);
        Assert.Equal("secret-key", stub.Last!.Headers.Authorization?.Parameter);
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code; private readonly string _body;
        public FixedHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body) });
    }

    [Fact]
    public async Task Non200_ThrowsClearError_HintingV1()
    {
        // Simulates hitting the wrong path (no /v1) — server returns 404 with a non-JSON body.
        var client = new OpenAiCompatibleClient(
            new HttpClient(new FixedHandler(HttpStatusCode.NotFound, "Not Found")), "http://localhost:1234", null);
        var ex = await Assert.ThrowsAsync<GeneratorException>(() =>
            client.CompleteAsync(new LlmRequest("m", "s", new[] { new LlmMessage("user", "u") })));
        Assert.Contains("/v1", ex.Message);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task MissingChoices_ThrowsClearError()
    {
        var client = new OpenAiCompatibleClient(
            new HttpClient(new FixedHandler(HttpStatusCode.OK, "{\"object\":\"list\"}")), "http://localhost:1234/v1", null);
        var ex = await Assert.ThrowsAsync<GeneratorException>(() =>
            client.CompleteAsync(new LlmRequest("m", "s", new[] { new LlmMessage("user", "u") })));
        Assert.Contains("no choices", ex.Message);
    }
}
