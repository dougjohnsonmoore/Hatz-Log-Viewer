namespace HatzLogCollector;

public interface ISecretResolver
{
    Task<string> GetAsync(string reference, CancellationToken ct);
}

/// <summary>Reads secrets from environment variables. Keys never live in config files.</summary>
public sealed class EnvSecretResolver : ISecretResolver
{
    public Task<string> GetAsync(string reference, CancellationToken ct)
    {
        var value = Environment.GetEnvironmentVariable(reference);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Environment variable '{reference}' is not set.");
        return Task.FromResult(value);
    }
}
