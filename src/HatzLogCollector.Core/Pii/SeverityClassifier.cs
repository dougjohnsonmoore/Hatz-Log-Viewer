namespace HatzLogCollector.Pii;

/// <summary>
/// Maps an Azure PII category to a triage severity. These are heuristics, not a compliance
/// determination: override per category via PiiOptions.SeverityOverrides to match your standard.
/// </summary>
public sealed class SeverityClassifier
{
    // Substring match, case-insensitive. Identifiers, financial accounts and credentials.
    private static readonly string[] HighMarkers =
    {
        "social", "creditcard", "bank", "routing", "passport", "license", "taxpayer", "taxid",
        "nationalid", "identitycard", "health", "medical", "password", "connectionstring",
        "accountkey", "authkey", "sas", "secret", "token", "apikey",
    };

    private static readonly HashSet<string> Low = new(StringComparer.OrdinalIgnoreCase) { "Person", "URL" };

    private readonly Dictionary<string, Severity> _overrides;

    public SeverityClassifier(IReadOnlyDictionary<string, string>? overrides = null)
    {
        _overrides = new Dictionary<string, Severity>(StringComparer.OrdinalIgnoreCase);
        if (overrides == null) return;
        foreach (var (category, value) in overrides)
        {
            if (!Enum.TryParse<Severity>(value, ignoreCase: true, out var severity))
                throw new InvalidOperationException($"Invalid severity '{value}' for category '{category}'. Use High, Medium or Low.");
            _overrides[category] = severity;
        }
    }

    public Severity Classify(string category)
    {
        if (_overrides.TryGetValue(category, out var overridden)) return overridden;
        if (Low.Contains(category)) return Severity.Low;
        var lower = category.ToLowerInvariant();
        if (HighMarkers.Any(lower.Contains)) return Severity.High;
        return Severity.Medium; // Email, PhoneNumber, Address, IPAddress and anything unrecognised
    }
}
