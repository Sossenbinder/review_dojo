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

        var endpoint = new Uri(_http.BaseAddress!, "chat/completions");
        using var resp = await _http.PostAsJsonAsync("chat/completions", body, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new GeneratorException(
                $"LLM server returned {(int)resp.StatusCode} from {endpoint}. " +
                $"Check that OpenAI:BaseUrl points at the OpenAI-compatible base (usually ends in /v1) and the model is loaded. " +
                $"Response: {Truncate(raw)}");

        JsonElement root;
        try { root = JsonDocument.Parse(raw).RootElement; }
        catch (JsonException)
        {
            throw new GeneratorException(
                $"LLM server at {endpoint} did not return JSON (is OpenAI:BaseUrl missing the /v1 path?). Response: {Truncate(raw)}");
        }

        if (root.TryGetProperty("error", out var err))
            throw new GeneratorException($"LLM server reported an error: {Truncate(err.ToString())}");

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new GeneratorException(
                $"LLM response from {endpoint} had no choices — check OpenAI:BaseUrl (should end in /v1) and the model id. Response: {Truncate(raw)}");

        return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
