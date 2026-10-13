using System.Text;

namespace HatzLogCollector.Pii;

public sealed record Segment(int Id, string Text);

/// <summary>Where a slice of a chunk came from, so a hit can be attributed to its segment.</summary>
public sealed record ChunkSpan(int SegmentId, int ChunkStart, int Length);

public sealed record Chunk(string Text, IReadOnlyList<ChunkSpan> Spans);

/// <summary>
/// Packs segments into documents no longer than <c>maxLength</c>. Azure bills each document as
/// at least one 1,000-character record, so many tiny documents cost more than a few full ones.
/// Segments are joined with a blank line; long segments are split at whitespace.
/// </summary>
public static class TextChunker
{
    private const string Separator = "\n\n";

    public static IReadOnlyList<Chunk> Pack(IEnumerable<Segment> segments, int maxLength)
    {
        if (maxLength < 16) throw new ArgumentOutOfRangeException(nameof(maxLength));

        var chunks = new List<Chunk>();
        var buffer = new StringBuilder();
        var spans = new List<ChunkSpan>();

        void Flush()
        {
            if (buffer.Length == 0) return;
            chunks.Add(new Chunk(buffer.ToString(), spans.ToArray()));
            buffer.Clear();
            spans.Clear();
        }

        foreach (var segment in segments)
        {
            var text = segment.Text;
            var position = 0;
            while (position < text.Length)
            {
                var remaining = text.Length - position;

                // Avoid starting a long segment in a nearly-full chunk and splitting it needlessly.
                if (buffer.Length > 0 && buffer.Length + Separator.Length + Math.Min(remaining, 200) > maxLength)
                    Flush();

                if (buffer.Length > 0) buffer.Append(Separator);

                var room = maxLength - buffer.Length;
                var take = Math.Min(room, remaining);
                if (take < remaining) take = BackToBoundary(text, position, take);

                spans.Add(new ChunkSpan(segment.Id, buffer.Length, take));
                buffer.Append(text, position, take);
                position += take;

                if (position < text.Length) Flush(); // chunk is full
            }
        }

        Flush();
        return chunks;
    }

    private static int BackToBoundary(string text, int position, int take)
    {
        // Prefer a whitespace break in the last quarter of the window.
        var floor = position + take * 3 / 4;
        for (var i = position + take - 1; i > floor; i--)
        {
            if (char.IsWhiteSpace(text[i])) return i + 1 - position;
        }

        // Hard split: never cut a surrogate pair in half.
        if (take > 1 && char.IsHighSurrogate(text[position + take - 1])) take--;
        return take;
    }
}
