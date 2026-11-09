using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentDiffReview;

public sealed record ReviewFile(string Id, string Status, string? OldPath, string? NewPath, string? Omission);
public sealed record DiffRow(string Kind, int? OldLine, int? NewLine, string Text,
    bool OldNoNewline = false, bool NewNoNewline = false);
public sealed record FileDiff(string FileId, IReadOnlyList<DiffRow> Rows, string? Omission,
    string? OldHash, string? NewHash, bool OldCrlf = false, bool NewCrlf = false)
{
    [JsonIgnore] public long MemoryCost => Rows.Sum(r => 128L + r.Text.Length * 2L) + 1024;
}
public sealed record GitResult(int Code, byte[] Output, string Error)
{
    public string Text => GitDiff.Utf8.GetString(Output);
}

public static class GitDiff
{
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public const int MaxFileBytes = 1_000_000;
    public const int MaxRows = 20_000;
    public const long MaxRetainedBytes = 32_000_000;

    public static async Task<GitResult> RunAsync(string directory, IEnumerable<string> arguments,
        CancellationToken cancellation = default, int maxBytes = 8_000_000)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "--no-pager", "--literal-pathspecs", "-c", "color.ui=false",
                     "-c", "core.fsmonitor=false", "-c", "diff.suppressBlankEmpty=false" }.Concat(arguments))
            start.ArgumentList.Add(arg);
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
        using var registration = deadline.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        async Task<byte[]> ReadBounded(Stream stream, int limit)
        {
            try
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, deadline.Token)) > 0)
                {
                    if (buffer.Length + count > limit) throw new IOException("Git output exceeded the review limit.");
                    buffer.Write(chunk, 0, count);
                }
                return buffer.ToArray();
            }
            catch { deadline.Cancel(); throw; }
        }
        var stdout = ReadBounded(process.StandardOutput.BaseStream, maxBytes);
        var stderr = ReadBounded(process.StandardError.BaseStream, 64_000);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
        return new(process.ExitCode, await stdout, Encoding.UTF8.GetString(await stderr));
    }

    public static IReadOnlyList<(string Status, string? OldPath, string? NewPath)> ParseNames(string text)
    {
        if (text.Length > 0 && !text.EndsWith('\0')) throw new InvalidDataException("Git paths must be NUL-terminated.");
        var fields = text.Split('\0');
        var result = new List<(string, string?, string?)>();
        int i = 0;
        while (i < fields.Length && fields[i].Length != 0)
        {
            string status = fields[i++];
            if (i >= fields.Length || fields[i].Length == 0) throw new InvalidDataException("Malformed Git path output.");
            string first = fields[i++];
            if (status[0] is 'R' or 'C')
            {
                if (i >= fields.Length || fields[i].Length == 0) throw new InvalidDataException("Missing rename destination.");
                result.Add((status[0] == 'R' ? "renamed" : "copied", first, fields[i++]));
            }
            else result.Add(status[0] switch
            {
                'A' => ("added", null, first), 'D' => ("deleted", first, null),
                'M' => ("modified", first, first), 'T' => ("type-changed", first, first),
                'U' => ("unmerged", first, first),
                _ => throw new InvalidDataException("Unsupported Git status.")
            });
            if (result.Count > 10_000) throw new IOException("Too many files for one review (limit: 10,000).");
        }
        return result;
    }

    public static IReadOnlyList<DiffRow> ParsePatch(string patch)
    {
        var rows = new List<DiffRow>();
        int oldLine = 0, newLine = 0, oldLeft = 0, newLeft = 0;
        foreach (string line in patch.Split('\n'))
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (oldLeft != 0 || newLeft != 0) throw new InvalidDataException("Truncated Git hunk.");
                var match = Regex.Match(line, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (!match.Success) throw new InvalidDataException("Invalid Git hunk header.");
                oldLine = int.Parse(match.Groups[1].Value);
                oldLeft = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                newLine = int.Parse(match.Groups[3].Value);
                newLeft = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                if ((oldLeft > 0 && oldLine == 0) || (newLeft > 0 && newLine == 0))
                    throw new InvalidDataException("Invalid Git line origin.");
                continue;
            }
            if (line == "\\ No newline at end of file" && rows.Count > 0)
            {
                var previous = rows[^1];
                rows[^1] = previous with { OldNoNewline = previous.OldLine.HasValue, NewNoNewline = previous.NewLine.HasValue };
                continue;
            }
            if (oldLeft == 0 && newLeft == 0) continue; // patch metadata, never a source of filenames
            if (line.Length == 0) throw new InvalidDataException("Truncated Git hunk line.");
            DiffRow row = line[0] switch
            {
                ' ' when oldLeft > 0 && newLeft > 0 => new("context", oldLine++, newLine++, line[1..]),
                '-' when oldLeft > 0 => new("deleted", oldLine++, null, line[1..]),
                '+' when newLeft > 0 => new("added", null, newLine++, line[1..]),
                _ => throw new InvalidDataException("Invalid Git hunk line.")
            };
            if (row.OldLine.HasValue) oldLeft--;
            if (row.NewLine.HasValue) newLeft--;
            rows.Add(row);
            if (rows.Count > MaxRows) throw new IOException($"Diff exceeds {MaxRows:N0} rows.");
        }
        if (oldLeft != 0 || newLeft != 0) throw new InvalidDataException("Truncated Git patch.");
        return rows;
    }

    public static string SafeWorkingPath(string root, string relative)
    {
        var parts = relative.Split('/');
        if (Path.IsPathRooted(relative) || parts.Any(p => p is "" or "." or ".." ||
            p.Equals(".git", StringComparison.OrdinalIgnoreCase)) || relative.Contains('\0') ||
            (OperatingSystem.IsWindows() && parts.Any(p => p.Contains('\\') || p.Contains(':') || p.EndsWith('.') || p.EndsWith(' '))))
            throw new IOException("Unsafe or unsupported repository path.");
        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Path is outside the repository.");
        string current = root;
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symlinks/reparse points are omitted, including ancestor directories.");
        }
        return full;
    }
}

public sealed class GitRepository
{
    private sealed record Stamp(long Size, long Modified);
    private sealed record Entry(ReviewFile File, Stamp? WorkingStamp);
    private readonly Dictionary<string, Entry> entries = [];
    private readonly Dictionary<string, FileDiff> loaded = [];
    private readonly SemaphoreSlim loadGate = new(1);
    private long retained;
    public string Root { get; private set; } = "";
    public string? Head { get; private set; }
    public string? Base { get; private set; }
    public string? MergeBase { get; private set; }
    public IReadOnlyList<ReviewFile> Files => entries.Values.Select(e => e.File).ToArray();

    public static async Task<GitRepository> OpenAsync(string directory, string? requestedBase, CancellationToken ct = default)
    {
        var repository = new GitRepository();
        var root = await GitDiff.RunAsync(directory, ["rev-parse", "--show-toplevel"], ct);
        if (root.Code != 0) throw new IOException("The launch directory is not a Git working tree.");
        repository.Root = root.Text.TrimEnd('\r', '\n');
        var head = await GitDiff.RunAsync(repository.Root, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], ct);
        repository.Head = head.Code == 0 ? head.Text.Trim() : null;
        repository.Base = requestedBase;
        if (requestedBase is not null)
        {
            if (repository.Head is null) throw new IOException("A base comparison requires a committed HEAD.");
            string? baseId = null;
            foreach (string candidate in new[] { requestedBase, "refs/remotes/origin/" + requestedBase })
            {
                var resolved = await GitDiff.RunAsync(repository.Root,
                    ["rev-parse", "--verify", "--quiet", "--end-of-options", candidate + "^{commit}"], ct);
                if (resolved.Code == 0) { baseId = resolved.Text.Trim(); break; }
            }
            if (baseId is null) throw new IOException("Cannot resolve the requested base locally or on origin; no fetch is performed.");
            var merge = await GitDiff.RunAsync(repository.Root, ["merge-base", baseId, repository.Head], ct);
            if (merge.Code != 0) throw new IOException("The base and HEAD have no available merge base.");
            repository.MergeBase = merge.Text.Trim();
        }
        var changes = new Dictionary<string, (string Status, string? OldPath, string? NewPath)>(StringComparer.Ordinal);
        if (repository.Head is not null)
        {
            string[] revisions = repository.MergeBase is null ? [repository.Head] : [repository.MergeBase, repository.Head];
            var names = await GitDiff.RunAsync(repository.Root,
                new[] { "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--find-renames" }
                    .Concat(revisions).Append("--"), ct);
            if (names.Code != 0) throw new IOException("Git could not index the comparison.");
            foreach (var change in GitDiff.ParseNames(names.Text))
            {
                string key = change.NewPath ?? change.OldPath!;
                if (!changes.TryGetValue(key, out var prior) || prior.Status != "unmerged") changes[key] = change;
            }
        }
        if (repository.MergeBase is null)
        {
            string[] args = repository.Head is null
                ? ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]
                : ["ls-files", "--others", "--exclude-standard", "-z"];
            var paths = await GitDiff.RunAsync(repository.Root, args, ct);
            if (paths.Code != 0) throw new IOException("Git could not index untracked files.");
            foreach (string path in paths.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                changes.TryAdd(path, ("added", null, path));
        }
        if (changes.Count > 10_000) throw new IOException("Too many files for one review (limit: 10,000).");
        foreach (var change in changes.Values.OrderBy(c => c.NewPath ?? c.OldPath, StringComparer.Ordinal))
        {
            string? omission = change.Status == "unmerged" ? "Unmerged files are not supported in this first slice." : null;
            Stamp? stamp = null;
            if (repository.MergeBase is null && change.NewPath is not null)
            {
                try { stamp = ReadStamp(GitDiff.SafeWorkingPath(repository.Root, change.NewPath)); }
                catch (IOException e) { omission = e.Message; }
                catch (UnauthorizedAccessException) { omission = "Working file is not accessible."; }
            }
            string id = "f" + repository.entries.Count;
            repository.entries.Add(id, new(new(id, change.Status, change.OldPath, change.NewPath, omission), stamp));
        }
        return repository;
    }

    public bool TryFile(string id, out ReviewFile? file)
    {
        file = entries.TryGetValue(id, out var entry) ? entry.File : null;
        return file is not null;
    }

    public FileDiff? Captured(string id)
    {
        lock (loaded) return loaded.GetValueOrDefault(id);
    }

    public async Task<FileDiff> LoadAsync(string id, CancellationToken ct = default)
    {
        if (!entries.TryGetValue(id, out var entry)) throw new ArgumentException("Unknown file ID.");
        await loadGate.WaitAsync(ct);
        try
        {
            lock (loaded) { if (loaded.TryGetValue(id, out var existing)) return existing; }
            if (retained >= GitDiff.MaxRetainedBytes) throw new IOException("Session memory limit reached; start a smaller review. Existing anchors are retained.");
            FileDiff diff;
            try
            {
                if (entry.File.Omission is not null) throw new IOException(entry.File.Omission);
                byte[] oldBytes = await RevisionAsync(MergeBase ?? Head, entry.File.OldPath, ct);
                byte[] newBytes;
                if (MergeBase is not null) newBytes = await RevisionAsync(Head, entry.File.NewPath, ct);
                else if (entry.File.NewPath is null) newBytes = [];
                else
                {
                    string path = GitDiff.SafeWorkingPath(Root, entry.File.NewPath);
                    if (ReadStamp(path) != entry.WorkingStamp) throw new IOException("Working file changed since indexing; restart the review.");
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > GitDiff.MaxFileBytes) throw new IOException("File exceeds the 1,000,000 byte limit.");
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int n;
                    while ((n = await stream.ReadAsync(chunk, ct)) > 0)
                    {
                        if (buffer.Length + n > GitDiff.MaxFileBytes) throw new IOException("File exceeds the review limit.");
                        buffer.Write(chunk, 0, n);
                    }
                    newBytes = buffer.ToArray();
                    if (ReadStamp(GitDiff.SafeWorkingPath(Root, entry.File.NewPath)) != entry.WorkingStamp)
                        throw new IOException("Working file changed while loading; restart the review.");
                }
                string oldText = TextContent(oldBytes), newText = TextContent(newBytes);
                IReadOnlyList<DiffRow> rows;
                if (oldBytes.AsSpan().SequenceEqual(newBytes))
                {
                    var lines = oldText.Split('\n');
                    int count = lines.Length - (oldText.EndsWith('\n') ? 1 : 0);
                    rows = oldText.Length == 0 ? [] : Enumerable.Range(0, count)
                        .Select(i => new DiffRow("context", i + 1, i + 1, lines[i],
                            i == count - 1 && !oldText.EndsWith('\n'), i == count - 1 && !newText.EndsWith('\n'))).ToArray();
                }
                else rows = await MakePatchAsync(oldBytes, newBytes, ct);
                diff = new(id, rows, null, Hash(oldBytes), Hash(newBytes), oldText.Contains("\r\n"), newText.Contains("\r\n"));
            }
            catch (IOException e) { diff = new(id, [], e.Message, null, null); }
            catch (DecoderFallbackException) { diff = new(id, [], "Non-UTF-8 content is omitted.", null, null); }
            catch (UnauthorizedAccessException) { diff = new(id, [], "File is not accessible.", null, null); }
            if (retained + diff.MemoryCost > GitDiff.MaxRetainedBytes)
                throw new IOException("Session memory limit reached; existing captured files and comments are retained. Restart for a smaller review.");
            retained += diff.MemoryCost;
            lock (loaded) loaded.Add(id, diff);
            return diff;
        }
        finally { loadGate.Release(); }
    }

    private static Stamp ReadStamp(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0)
            throw new IOException("Not a regular working file (directories/submodules are omitted).");
        return new(file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private async Task<byte[]> RevisionAsync(string? revision, string? path, CancellationToken ct)
    {
        if (path is null) return [];
        if (revision is null) throw new IOException("No original commit exists for this path.");
        var tree = await GitDiff.RunAsync(Root, ["ls-tree", "-z", revision, "--", path], ct, 16_384);
        if (tree.Code != 0 || tree.Output.Length == 0) throw new IOException("The committed path is unavailable.");
        string header = tree.Text.Split('\t')[0];
        string[] fields = header.Split(' ');
        if (fields.Length != 3 || fields[0] is not ("100644" or "100755") || fields[1] != "blob")
            throw new IOException("Committed symlinks/submodules/unsupported entries are omitted.");
        var size = await GitDiff.RunAsync(Root, ["cat-file", "-s", fields[2]], ct, 1024);
        if (size.Code != 0 || !long.TryParse(size.Text.Trim(), out long bytes) || bytes > GitDiff.MaxFileBytes)
            throw new IOException("Committed file is unavailable or exceeds 1,000,000 bytes.");
        var content = await GitDiff.RunAsync(Root, ["cat-file", "blob", fields[2]], ct, GitDiff.MaxFileBytes);
        if (content.Code != 0) throw new IOException("Cannot read committed content.");
        return content.Output;
    }

    private static string TextContent(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) throw new IOException("Binary content is omitted.");
        if (bytes.Count(b => b == '\n') > GitDiff.MaxRows) throw new IOException("File exceeds the 20,000 line limit.");
        return GitDiff.Utf8.GetString(bytes);
    }

    private async Task<IReadOnlyList<DiffRow>> MakePatchAsync(byte[] oldBytes, byte[] newBytes, CancellationToken ct)
    {
        string temporary = Directory.CreateTempSubdirectory("diff-review-slim-").FullName;
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string oldFile = Path.Combine(temporary, "old"), newFile = Path.Combine(temporary, "new");
            await File.WriteAllBytesAsync(oldFile, oldBytes, ct);
            await File.WriteAllBytesAsync(newFile, newBytes, ct);
            var patch = await GitDiff.RunAsync(Root, ["-c", "core.autocrlf=false", "diff", "--no-index", "--no-ext-diff", "--no-textconv",
                "--no-color", "--text", "--unified=2147483647", "--", oldFile, newFile], ct);
            if (patch.Code is not (0 or 1)) throw new IOException("Git could not produce the captured-file patch.");
            return GitDiff.ParsePatch(patch.Text);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
