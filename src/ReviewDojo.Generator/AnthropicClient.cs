using System.Net.Http.Json;
using System.Text.Json;

namespace ReviewDojo.Generator;

public class AnthropicClient : IAnthropicClient
{
    private readonly HttpClient _http;

    public AnthropicClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.anthropic.com/");
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
        if (!_http.DefaultRequestHeaders.Contains("x-api-key"))
            _http.DefaultRequestHeaders.Add("x-api-key", key);
        if (!_http.DefaultRequestHeaders.Contains("anthropic-version"))
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            model = request.Model,
            max_tokens = request.MaxTokens,
            system = request.System,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };

        using var resp = await _http.PostAsJsonAsync("v1/messages", body, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        // content is an array of blocks; return the first text block.
        foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                return block.GetProperty("text").GetString() ?? "";
        }
        return "";
    }
}
