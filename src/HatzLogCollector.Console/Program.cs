using Azure;
using Azure.AI.TextAnalytics;
using Azure.Identity;
using Azure.Storage.Blobs;
using HatzLogCollector;
using HatzLogCollector.Collection;
using HatzLogCollector.Hatz;
using HatzLogCollector.Pii;
using HatzLogCollector.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

const string Usage = """
    hatzlog - collect Hatz.ai compliance logs and scan them for PII

    Commands
      check-key Test each source's API key against a few endpoints and say what the result means (key is never printed)
      tenants   List tenants and their compliance settings for each configured source (no data is written)
      collect   Archive invocations (with content) to storage
      scan      Run Azure AI Language PII detection over archived content and write reports

    Options
      --config <file>    Config file (default: appsettings.json, plus appsettings.local.json if present)
      --source <name>    Limit to one configured source
      --tenant <id>      Limit to a tenant id (repeatable)
      --from <date>      collect: manual range start (UTC) - does NOT move the watermark
                         scan:    range start (default: 7 days ago)
      --to <date>        range end (UTC, default: now)
      --dry-run          collect: list and count only; fetch and write nothing

    Examples
      hatzlog check-key
      hatzlog tenants
      hatzlog collect
      hatzlog collect --tenant tenant_abc123 --from 2026-09-01 --to 2026-09-30
      hatzlog scan --from 2026-10-01
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

var command = args[0].ToLowerInvariant();
var opts = CliOptions.Parse(args.Skip(1).ToArray());
if (opts.Error != null)
{
    Console.Error.WriteLine(opts.Error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}

var configPath = opts.Config ?? "appsettings.json";
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file '{configPath}' not found. Copy appsettings.sample.json to appsettings.json and edit it.");
    return 2;
}

var config = new ConfigurationBuilder()
    .AddJsonFile(Path.GetFullPath(configPath), optional: false)
    .AddJsonFile(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, "appsettings.local.json"), optional: true)
    .AddEnvironmentVariables("HATZLC_")
    .Build();

var collectorOptions = config.GetSection("Collector").Get<CollectorOptions>() ?? new CollectorOptions();
var storageOptions = config.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
var piiOptions = config.GetSection("Pii").Get<PiiOptions>() ?? new PiiOptions();

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
var logger = loggerFactory.CreateLogger("hatzlog");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var store = CreateStore(storageOptions);
    switch (command)
    {
        case "check-key":
            return await CheckKeyAsync();
        case "tenants":
            return await ListTenantsAsync();
        case "collect":
            return await CollectAsync();
        case "scan":
            return await ScanAsync();
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            Console.Error.WriteLine(Usage);
            return 2;
    }

    async Task<int> CheckKeyAsync()
    {
        var failed = false;
        foreach (var source in collectorOptions.Sources.Where(s => opts.Source == null || s.Name.Equals(opts.Source, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"Source: {source.Name}  (variable {source.ApiKeyRef})");
            var raw = Environment.GetEnvironmentVariable(source.ApiKeyRef);
            if (string.IsNullOrEmpty(raw))
            {
                Console.WriteLine("  Variable is not set in THIS process. If you launched from Rider, put it in the run configuration's");
                Console.WriteLine("  environment variables: a shell export is not inherited by apps started from the Dock.");
                failed = true;
                continue;
            }

            var key = EnvSecretResolver.Clean(raw);
            Console.WriteLine($"  Key length: {key.Length} characters" + (key.Length != raw.Length ? $" ({raw.Length - key.Length} stripped: surrounding whitespace or quotes)" : ""));
            if (key.Any(char.IsWhiteSpace)) Console.WriteLine("  WARNING: the key contains whitespace inside it. It is probably truncated or pasted with extra text.");

            var api = HatzApiClient.Create(collectorOptions.HatzBaseUrl, key, logger);
            var probes = new (string Label, string Path)[]
            {
                ("any valid key       GET chat/models", "chat/models"),
                ("MSP admin key       GET admin/tenants/msp", "admin/tenants/msp"),
                ("tenant listing      GET admin/tenants", "admin/tenants?limit=1"),
                ("compliance access   GET compliance/invocations", "compliance/invocations?limit=1"),
            };
            var codes = new List<System.Net.HttpStatusCode>();
            foreach (var (label, path) in probes)
            {
                var code = await api.ProbeAsync(path, cts.Token);
                codes.Add(code);
                Console.WriteLine($"  {label,-52} {(int)code} {code}");
            }

            Console.WriteLine("  Reading: " + Interpret(codes));
            if (codes.Any(c => c != System.Net.HttpStatusCode.OK)) failed = true;
        }
        return failed ? 1 : 0;

        static string Interpret(IReadOnlyList<System.Net.HttpStatusCode> c)
        {
            bool Is(int i, System.Net.HttpStatusCode code) => c[i] == code;
            if (c.All(x => x == System.Net.HttpStatusCode.Unauthorized))
                return "Hatz does not recognise this key at all (401 everywhere). It is wrong, revoked, or not the secret value. Create a fresh key and re-copy it.";
            if (Is(0, System.Net.HttpStatusCode.OK) && c.Skip(1).Any(x => x is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
                return "The key is valid but lacks admin and/or compliance permission. It must belong to a user with the right role (admin endpoints are marked Beta by Hatz).";
            if (c.All(x => x == System.Net.HttpStatusCode.OK))
                return "Key is valid with admin and compliance access.";
            return "Mixed results; compare the statuses above. Any 5xx is Hatz-side, so retry later.";
        }
    }

    async Task<int> ListTenantsAsync()
    {
        var secrets = new EnvSecretResolver();
        var failed = false;
        foreach (var source in collectorOptions.Sources.Where(s => opts.Source == null || s.Name.Equals(opts.Source, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"Source: {source.Name}");
            try
            {
                var api = HatzApiClient.Create(collectorOptions.HatzBaseUrl, await secrets.GetAsync(source.ApiKeyRef, cts.Token), logger);
                var tenants = source.TenantIds is { Count: > 0 }
                    ? source.TenantIds.Select(id => new TenantInfo(id, "", null)).ToList()
                    : source.DiscoverTenants ? (await api.ListTenantsAsync(cts.Token)).ToList() : new List<TenantInfo>();
                foreach (var t in tenants)
                {
                    var s = await api.GetComplianceSettingsAsync(t.Id, cts.Token);
                    var detail = s == null
                        ? "settings unreadable with this key (403/404)"
                        : $"invocations={Flag(s.InvocationsViewEnabled)} content={Flag(s.InvocationsViewContentEnabled)} audit={Flag(s.AuditEventsViewEnabled)} fileEgress={s.FileEgressPolicy}";
                    Console.WriteLine($"  {t.Id}  {t.Name}  [{t.Status}]  {detail}");
                }
                if (tenants.Count == 0) Console.WriteLine("  (no tenants: set TenantIds or DiscoverTenants)");
            }
            catch (Exception ex) when (ex is HatzApiException or InvalidOperationException)
            {
                failed = true;
                Console.WriteLine($"  ERROR: {ex.Message}");
                if (ex is HatzApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
                    Console.WriteLine("  Run 'hatzlog check-key' to see whether the key is invalid or just lacks admin access.");
            }
        }
        return failed ? 1 : 0;
    }

    async Task<int> CollectAsync()
    {
        var runner = new CollectionRunner(
            collectorOptions,
            new EnvSecretResolver(),
            key => HatzApiClient.Create(collectorOptions.HatzBaseUrl, key, logger),
            store,
            logger);

        var request = new CollectRequest(opts.From, opts.To, opts.Tenants.Count > 0 ? opts.Tenants : null, opts.Source, opts.DryRun);
        if (opts.DryRun) Console.WriteLine("Dry run: listing only, nothing is fetched or written.");
        var results = await runner.RunAsync(request, cts.Token);

        Console.WriteLine();
        Console.WriteLine($"{"tenant",-28} {"status",-20} {"listed",7} {"new",7} {"have",7} {"meta-only",9} {"failed",7}");
        foreach (var r in results)
        {
            Console.WriteLine($"{Trim(r.TenantId, 28),-28} {r.Status,-20} {r.Listed,7} {r.Archived,7} {r.AlreadyArchived,7} {r.MetadataOnly,9} {r.Failed,7}");
            if (r.Reason != null) Console.WriteLine($"    {r.Reason}");
        }
        if (results.Any(r => r.Reason?.Contains("401") == true))
            Console.WriteLine("\nHatz rejected a key (401). Run 'hatzlog check-key' to see what it can and cannot access.");
        return results.Any(r => r.Status is TenantRunStatus.Failed or TenantRunStatus.CompletedWithErrors) ? 1 : 0;
    }

    async Task<int> ScanAsync()
    {
        var detector = CreateDetector(piiOptions);
        var service = new PiiScanService(store, detector, piiOptions, logger);
        var to = opts.To ?? DateTimeOffset.UtcNow;
        var from = opts.From ?? to.AddDays(-7);

        var containers = new List<string>();
        if (opts.Tenants.Count > 0)
            containers.AddRange(opts.Tenants.Select(ArchivePaths.TenantContainer));
        else
            await foreach (var c in store.ListContainersAsync("hatz-", cts.Token))
                if (c != ArchivePaths.StateContainer && c != ArchivePaths.ReportsContainer) containers.Add(c);

        if (containers.Count == 0)
        {
            Console.Error.WriteLine("No archive containers found. Run 'collect' first.");
            return 1;
        }

        var runId = ReportWriter.NewRunId(DateTimeOffset.UtcNow);
        var anyErrors = false;
        Console.WriteLine($"{"tenant",-28} {"sessions",8} {"findings",8} {"high",5} {"chunk-err",9}  report");
        foreach (var container in containers)
        {
            var output = await service.ScanContainerAsync(container, from, to, cts.Token);
            var prefix = await ReportWriter.WriteAsync(store, output, runId, from, to, cts.Token);
            var high = output.Findings.Count(f => f.Severity == nameof(Severity.High));
            Console.WriteLine($"{Trim(output.TenantId ?? container, 28),-28} {output.Counters.Sessions,8} {output.Findings.Count,8} {high,5} {output.Counters.ChunkErrors,9}  {ArchivePaths.ReportsContainer}/{prefix}");
            if (output.Counters.InvocationsWithoutContent > 0)
                Console.WriteLine($"    note: {output.Counters.InvocationsWithoutContent} invocation(s) had no content and were not scanned");
            if (output.Counters.TruncatedSessions > 0)
                Console.WriteLine($"    note: {output.Counters.TruncatedSessions} session(s) exceeded MaxCharsPerSession and were truncated");
            anyErrors |= output.Counters.ChunkErrors > 0 || output.Counters.UnreadableBlobs > 0;
        }
        return anyErrors ? 1 : 0;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 2;
}

static IArchiveStore CreateStore(StorageOptions o)
{
    if (o.Mode.Equals("Local", StringComparison.OrdinalIgnoreCase))
        return new LocalArchiveStore(o.LocalRoot);

    if (!o.Mode.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Storage:Mode must be 'Local' or 'AzureBlob', not '{o.Mode}'.");

    if (!string.IsNullOrEmpty(o.ConnectionStringEnv))
    {
        var cs = Environment.GetEnvironmentVariable(o.ConnectionStringEnv);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException($"Environment variable '{o.ConnectionStringEnv}' is not set.");
        return new AzureBlobArchiveStore(new BlobServiceClient(cs));
    }

    if (string.IsNullOrEmpty(o.AccountUrl))
        throw new InvalidOperationException("Storage:AccountUrl (or ConnectionStringEnv) is required for AzureBlob mode.");
    return new AzureBlobArchiveStore(new BlobServiceClient(new Uri(o.AccountUrl), new DefaultAzureCredential()));
}

static IPiiDetector CreateDetector(PiiOptions o)
{
    if (string.IsNullOrEmpty(o.LanguageEndpoint))
        throw new InvalidOperationException("Pii:LanguageEndpoint is required for 'scan'.");
    var endpoint = new Uri(o.LanguageEndpoint);

    TextAnalyticsClient client;
    if (!string.IsNullOrEmpty(o.LanguageKeyEnv))
    {
        var key = Environment.GetEnvironmentVariable(o.LanguageKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Environment variable '{o.LanguageKeyEnv}' is not set.");
        client = new TextAnalyticsClient(endpoint, new AzureKeyCredential(key));
    }
    else
    {
        client = new TextAnalyticsClient(endpoint, new DefaultAzureCredential());
    }
    return new AzureLanguagePiiDetector(client, o.Language);
}

static string Flag(bool value) => value ? "on" : "OFF";
static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

internal sealed class CliOptions
{
    public string? Config { get; private set; }
    public string? Source { get; private set; }
    public List<string> Tenants { get; } = new();
    public DateTimeOffset? From { get; private set; }
    public DateTimeOffset? To { get; private set; }
    public bool DryRun { get; private set; }
    public string? Error { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            try
            {
                switch (args[i])
                {
                    case "--config": o.Config = Next(); break;
                    case "--source": o.Source = Next(); break;
                    case "--tenant": o.Tenants.Add(Next()); break;
                    case "--from": o.From = ParseDate(Next()); break;
                    case "--to": o.To = ParseDate(Next()); break;
                    case "--dry-run": o.DryRun = true; break;
                    default: o.Error = $"Unknown option '{args[i]}'."; return o;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                o.Error = ex.Message;
                return o;
            }
        }
        return o;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
}
