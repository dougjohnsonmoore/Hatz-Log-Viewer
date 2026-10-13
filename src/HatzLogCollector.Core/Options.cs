namespace HatzLogCollector;

public sealed class CollectorOptions
{
    public string HatzBaseUrl { get; set; } = "https://ai.hatz.ai/v1/";

    /// <summary>One entry per API key: the MSP master key and/or per-client keys.</summary>
    public List<SourceOptions> Sources { get; set; } = new();

    /// <summary>How far back the very first run for a tenant looks.</summary>
    public int InitialLookbackDays { get; set; } = 30;

    /// <summary>Collection proceeds in windows of this size so progress is checkpointed.</summary>
    public int WindowHours { get; set; } = 24;

    /// <summary>Each run re-reads this far before the watermark; archived items are skipped by id.</summary>
    public int OverlapMinutes { get; set; } = 15;

    /// <summary>Hatz allows 1-1000.</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>Content needs one API call per invocation.</summary>
    public int MaxParallelDetailRequests { get; set; } = 4;
}

public sealed class SourceOptions
{
    public string Name { get; set; } = "";

    /// <summary>Reference resolved by <see cref="ISecretResolver"/> (env var name, or Key Vault secret name).</summary>
    public string ApiKeyRef { get; set; } = "";

    /// <summary>List tenants via GET /admin/tenants (needs an MSP-level key).</summary>
    public bool DiscoverTenants { get; set; }

    /// <summary>Explicit tenant ids. Used instead of discovery when set.</summary>
    public List<string>? TenantIds { get; set; }

    public List<string>? ExcludeTenantIds { get; set; }
}

public sealed class StorageOptions
{
    /// <summary>"Local" (dev / one-off audits) or "AzureBlob".</summary>
    public string Mode { get; set; } = "Local";

    public string LocalRoot { get; set; } = "./archive";

    /// <summary>https://&lt;account&gt;.blob.core.windows.net - uses DefaultAzureCredential.</summary>
    public string? AccountUrl { get; set; }

    /// <summary>Name of an env var holding a connection string (e.g. Azurite). Takes precedence over AccountUrl.</summary>
    public string? ConnectionStringEnv { get; set; }
}

public sealed class PiiOptions
{
    /// <summary>Language resource endpoint (custom subdomain required for Entra auth).</summary>
    public string? LanguageEndpoint { get; set; }

    /// <summary>Env var holding the Language key. If empty, DefaultAzureCredential is used.</summary>
    public string? LanguageKeyEnv { get; set; }

    public string Language { get; set; } = "en";

    public double MinConfidence { get; set; } = 0.8;

    /// <summary>Null means the default list. Set to an empty array to ignore nothing.</summary>
    public List<string>? IgnoredCategories { get; set; }

    public static readonly string[] DefaultIgnoredCategories = { "DateTime", "PersonType", "Organization" };

    public IReadOnlyCollection<string> EffectiveIgnoredCategories => IgnoredCategories ?? DefaultIgnoredCategories.ToList();

    /// <summary>Scan tool-call code and tool output as well as chat text.</summary>
    public bool IncludeToolOutput { get; set; } = true;

    /// <summary>Per-session text cap (cost control). Truncation is reported.</summary>
    public int MaxCharsPerSession { get; set; } = 250_000;

    public int MaxParallelRequests { get; set; } = 4;

    /// <summary>Category name to High/Medium/Low, overriding the built-in heuristics.</summary>
    public Dictionary<string, string>? SeverityOverrides { get; set; }
}

public static class ConfigGuard
{
    /// <summary>Rejects values still carrying the REPLACE_ME markers from appsettings.sample.json.</summary>
    public static void RejectPlaceholder(string? value, string setting)
    {
        if (value != null && value.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{setting} is still the sample placeholder ('{value}'). Edit your config file.");
    }
}
