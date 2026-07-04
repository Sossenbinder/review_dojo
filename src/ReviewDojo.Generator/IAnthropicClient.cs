namespace ReviewDojo.Generator;

public interface IAnthropicClient
{
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}
