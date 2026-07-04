using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReviewDojo.Generator;

// Calls any OpenAI-compatible chat-completions endpoint (LM Studio, Ollama, vLLM, ...).
// baseUrl is the OpenAI-style base, e.g. "http://localhost:1234/v1". apiKey is optional
// (LM Studio ignores it; some servers require a bearer token).
public class OpenAiCompatibleClient : IAnthropicClient
{
    private readonly HttpClient _http;

    public OpenAiCompatibleClient(HttpClient http, string baseUrl, string? apiKey = null)
    {
        _http = http;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("OpenAI base URL is not set (e.g. http://localhost:1234/v1).");
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        _http.BaseAddress = new Uri(baseUrl);
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var messages = new List<object> { new { role = "system", content = request.System } };
        foreach (var m in request.Messages)
            messages.Add(new { role = m.Role, content = m.Content });

        var body = new
        {
            model = request.Model,
            messages,
            temperature = 0.2,
            max_tokens = request.MaxTokens,
            stream = false,
        };

        using var resp = await _http.PostAsJsonAsync("chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
