using System.Text.Json;

namespace HatzLogCollector.Pii;

public sealed record TextPart(string Origin, string Text);

/// <summary>
/// Pulls scannable text out of one element of an invocation's <c>content</c> array.
/// Chat messages are {role, content:[{type, ...}]}; block types seen so far are
/// "content" (text), "tool_call" (args.code) and "tool_result" (content).
/// Workflow step records have no verified shape yet, so anything that is not a chat message
/// is walked generically and every string value is scanned.
/// </summary>
public static class ContentTextExtractor
{
    public static string MessageId(JsonElement item, int index) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()!
            : $"item-{index}";

    public static IEnumerable<TextPart> Extract(JsonElement item, bool includeToolOutput)
    {
        if (item.ValueKind != JsonValueKind.Object) yield break;

        var isChatMessage = item.TryGetProperty("role", out var roleEl)
                            && roleEl.ValueKind == JsonValueKind.String
                            && item.TryGetProperty("content", out var blocks)
                            && blocks.ValueKind == JsonValueKind.Array;
        if (!isChatMessage)
        {
            foreach (var text in WalkStrings(item)) yield return new TextPart("step", text);
            yield break;
        }

        var role = item.GetProperty("role").GetString()!;
        foreach (var block in item.GetProperty("content").EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "content";

            switch (type)
            {
                case "content":
                    if (TryString(block, "content", out var text)) yield return new TextPart(role, text);
                    break;

                case "tool_call":
                    if (!includeToolOutput || !block.TryGetProperty("args", out var args)) break;
                    if (args.ValueKind == JsonValueKind.Object && TryString(args, "code", out var code))
                        yield return new TextPart("tool_call", code);
                    else if (args.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        yield return new TextPart("tool_call", args.GetRawText());
                    break;

                case "tool_result":
                    if (includeToolOutput && TryString(block, "content", out var result))
                        yield return new TextPart("tool_result", result);
                    break;

                default:
                    if (TryString(block, "content", out var other)) yield return new TextPart(type, other);
                    break;
            }
        }
    }

    private static bool TryString(JsonElement e, string name, out string value)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
        {
            value = v.GetString()!;
            return true;
        }
        value = "";
        return false;
    }

    private static IEnumerable<string> WalkStrings(JsonElement e, string? key = null)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.GetString();
                if (s is { Length: >= 3 } && !IsBookkeepingKey(key)) yield return s;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    foreach (var s2 in WalkStrings(p.Value, p.Name)) yield return s2;
                break;
            case JsonValueKind.Array:
                foreach (var el in e.EnumerateArray())
                    foreach (var s3 in WalkStrings(el, key)) yield return s3;
                break;
        }
    }

    private static bool IsBookkeepingKey(string? key) =>
        key != null && (key.Equals("id", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("type", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("status", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("model_name", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith("_at", StringComparison.OrdinalIgnoreCase));
}
