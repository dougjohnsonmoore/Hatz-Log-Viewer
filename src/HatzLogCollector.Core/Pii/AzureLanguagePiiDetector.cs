using Azure;
using Azure.AI.TextAnalytics;

namespace HatzLogCollector.Pii;

/// <summary>
/// Azure AI Language PII detection (synchronous API). Documented limits: 5 documents per request,
/// 5,120 text elements per document. Text elements never exceed UTF-16 length, so capping documents
/// at 5,000 UTF-16 units is always within the limit.
/// </summary>
public sealed class AzureLanguagePiiDetector : IPiiDetector
{
    private readonly TextAnalyticsClient _client;
    private readonly string _language;

    public AzureLanguagePiiDetector(TextAnalyticsClient client, string language = "en")
    {
        _client = client;
        _language = language;
    }

    public int MaxBatchSize => 5;
    public int MaxDocumentLength => 5000;

    public async Task<IReadOnlyList<PiiDocumentResult>> DetectAsync(IReadOnlyList<string> documents, CancellationToken ct)
    {
        try
        {
            var response = await _client.RecognizePiiEntitiesBatchAsync(documents, _language, options: null, ct).ConfigureAwait(false);
            var results = new List<PiiDocumentResult>(documents.Count);
            foreach (var doc in response.Value)
            {
                if (doc.HasError)
                {
                    results.Add(PiiDocumentResult.Fail($"{doc.Error.ErrorCode}: {doc.Error.Message}"));
                    continue;
                }
                results.Add(PiiDocumentResult.Ok(doc.Entities
                    .Select(e => new DetectedEntity(e.Text, e.Category.ToString(), e.SubCategory, e.ConfidenceScore, e.Offset, e.Length))
                    .ToList()));
            }
            return results;
        }
        catch (RequestFailedException ex)
        {
            // The SDK has already retried transient failures. Report every document in the batch as failed
            // so the scan can finish and the gap is visible in the run counters.
            var error = $"HTTP {ex.Status} {ex.ErrorCode}";
            return documents.Select(_ => PiiDocumentResult.Fail(error)).ToList();
        }
    }
}
