namespace ReviewDojo.Generator;

public record LlmMessage(string Role, string Content);

public record LlmRequest(string Model, string System, IReadOnlyList<LlmMessage> Messages, int MaxTokens = 16000);
