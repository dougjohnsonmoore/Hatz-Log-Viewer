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
        var value = Clean(Environment.GetEnvironmentVariable(reference));
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"Environment variable '{reference}' is not set or is empty.");
        return Task.FromResult(value);
    }

    /// <summary>
    /// Trims whitespace and one pair of surrounding quotes. IDE run configurations and shell exports
    /// commonly leave these on a pasted key, and the API gives no hint that the key was malformed.
    /// </summary>
    public static string Clean(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            value = value[1..^1].Trim();
        return value;
    }
}
