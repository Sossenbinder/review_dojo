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
    private readonly bool _jsonMode;

    public OpenAiCompatibleClient(HttpClient http, string baseUrl, string? apiKey = null, bool jsonMode = true)
    {
        _http = http;
        _jsonMode = jsonMode;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("OpenAI base URL is not set (e.g. http://localhost:1234/v1).");
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        _http.BaseAddress = new Uri(baseUrl);
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    // Constrains the server to emit JSON matching the generator's output shape. This is the
    // strongest defense against weak models emitting raw newlines / malformed JSON. `bugs` is
    // optional so one schema fits both generator steps (step 1 returns only files).
    private static readonly object SchemaFormat = new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "reviewdojo_output",
            strict = false,
            schema = new
            {
                type = "object",
                properties = new
                {
                    files = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new { path = new { type = "string" }, after = new { type = "string" } },
                            required = new[] { "path", "after" },
                        },
                    },
                    bugs = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string" },
                                anchor = new { type = "string" },
                                category = new { type = "string" },
                                severity = new { type = "string" },
                                description = new { type = "string" },
                            },
                            required = new[] { "path", "anchor", "category", "severity", "description" },
                        },
                    },
                },
                required = new[] { "files" },
            },
        },
    };

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var messages = new List<object> { new { role = "system", content = request.System } };
        foreach (var m in request.Messages)
            messages.Add(new { role = m.Role, content = m.Content });

        var (resp, raw) = await PostAsync(messages, request, withSchema: _jsonMode, ct);

        // If the server rejects structured output, retry once as plain text so the client still
        // works against servers that don't support response_format.
        if (!resp.IsSuccessStatusCode && _jsonMode && (int)resp.StatusCode == 400
            && raw.Contains("response_format", StringComparison.OrdinalIgnoreCase))
        {
            resp.Dispose();
            (resp, raw) = await PostAsync(messages, request, withSchema: false, ct);
        }

        using (resp)
        {
            var endpoint = new Uri(_http.BaseAddress!, "chat/completions");
            if (!resp.IsSuccessStatusCode)
                throw new GeneratorException(
                    $"LLM server returned {(int)resp.StatusCode} from {endpoint}. " +
                    $"Check OpenAI:BaseUrl (usually ends in /v1) and that the model is loaded. Response: {Truncate(raw)}");

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
    }

    private async Task<(HttpResponseMessage Resp, string Raw)> PostAsync(
        List<object> messages, LlmRequest request, bool withSchema, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["temperature"] = 0.2,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = false,
        };
        if (withSchema)
            body["response_format"] = SchemaFormat;

        var resp = await _http.PostAsJsonAsync("chat/completions", body, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        return (resp, raw);
    }

    static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
