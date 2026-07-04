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
}
