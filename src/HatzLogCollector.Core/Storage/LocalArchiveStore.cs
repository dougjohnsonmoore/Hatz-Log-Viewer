using System.Runtime.CompilerServices;
using System.Text;

namespace HatzLogCollector.Storage;

/// <summary>
/// File-system store for development and one-off audits. The files hold client prompts and responses:
/// keep the root on an encrypted volume and delete it when the audit is done.
/// </summary>
public sealed class LocalArchiveStore : IArchiveStore
{
    private readonly string _root;

    public LocalArchiveStore(string root) => _root = Path.GetFullPath(root);

    public Task<bool> ExistsAsync(string container, string path, CancellationToken ct) =>
        Task.FromResult(File.Exists(Resolve(container, path)));

    public async Task<bool> WriteAsync(string container, string path, string content, bool overwrite, CancellationToken ct)
    {
        var full = Resolve(container, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!overwrite && File.Exists(full)) return false;

        // Write-then-move so a crash never leaves a truncated archive file behind.
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            File.Move(temp, full, overwrite);
            return true;
        }
        catch (IOException) when (!overwrite && File.Exists(full))
        {
            File.Delete(temp);
            return false;
        }
    }

    public async Task<string?> ReadAsync(string container, string path, CancellationToken ct)
    {
        var full = Resolve(container, path);
        return File.Exists(full) ? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false) : null;
    }

    public async IAsyncEnumerable<string> ListAsync(
        string container, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = Path.Combine(_root, container);
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (relative.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            if (relative.StartsWith(prefix, StringComparison.Ordinal)) yield return relative;
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListContainersAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(prefix, StringComparison.Ordinal)) yield return name;
        }
        await Task.CompletedTask;
    }

    private string Resolve(string container, string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, container, path));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path escapes the archive root.");
        return full;
    }
}
