using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentDiffReview;

public sealed record ReviewComment(string Id, string FileId, string Side, int? StartLine, int? EndLine, string Body);
public sealed record Submission(string OverallNote, List<ReviewComment> Comments);
public sealed record TerminalResult(int ExitCode, string Status, string Message);
public sealed record FeedbackComment(string Id, string? OldPath, string? NewPath, string Scope,
    string? Commit, string Side, int? StartLine, int? EndLine, string Body, string? OldHash, string? NewHash);
public sealed record Feedback(int Version, string SessionId, bool Submitted, string Repository,
    string? Base, string? MergeBase, string? Head, string OverallNote, IReadOnlyList<FeedbackComment> Comments);

public sealed class ReviewSession(GitRepository repository, int graceSeconds = 5)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxRequestBytes = 256_000;
    private readonly object state = new();
    private readonly TaskCompletionSource<TerminalResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalResult? terminal;
    private long connectionVersion;
    private bool connected;
    public GitRepository Repository { get; } = repository;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public Task<TerminalResult> Completion => completion.Task;
    public bool IsTerminal { get { lock (state) return terminal is not null; } }

    public bool Authenticate(string token) => token.Length == Token.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token));

    public object Metadata() => new
    {
        sessionId = Id, repository = Repository.Root, Repository.Base, Repository.MergeBase,
        Repository.Head, scope = "git-diff", files = Repository.Files, graceSeconds,
        capturePolicy = "Files are frozen on first load. Restart to review newer changes."
    };

    public long Connect()
    {
        lock (state)
        {
            if (terminal is not null) throw new InvalidOperationException("Review has ended.");
            connected = true;
            return ++connectionVersion;
        }
    }

    public void Disconnect(long version)
    {
        lock (state)
        {
            if (version != connectionVersion || terminal is not null) return;
            connected = false;
        }
        _ = CancelAfterGraceAsync(version);
    }

    private async Task CancelAfterGraceAsync(long version)
    {
        await Task.Delay(TimeSpan.FromSeconds(graceSeconds));
        lock (state)
        {
            if (version != connectionVersion || connected || terminal is not null) return;
            terminal = new(1, "cancelled", "Review window disconnected.");
            completion.TrySetResult(terminal);
        }
    }

    public bool Cancel(string message = "Review cancelled.", bool acknowledge = true)
    {
        lock (state)
        {
            if (terminal is not null) return false;
            terminal = new(1, "cancelled", message);
            if (acknowledge) completion.TrySetResult(terminal);
            return true;
        }
    }

    public Feedback Validate(Submission submission)
    {
        if (submission.OverallNote is null || submission.Comments is null)
            throw new ArgumentException("Overall note and comments are required.");
        if (submission.OverallNote.Length > 16_000 || submission.Comments.Count > 128)
            throw new ArgumentException("Review exceeds note/comment limits.");
        var comments = new List<FeedbackComment>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comment in submission.Comments)
        {
            if (comment is null || string.IsNullOrWhiteSpace(comment.Id) || comment.Id.Length > 80 ||
                !ids.Add(comment.Id) || comment.FileId is null || comment.Body is null || comment.Body.Length > 8_000)
                throw new ArgumentException("Invalid comment or duplicate comment ID.");
            if (!Repository.TryFile(comment.FileId, out var file)) throw new ArgumentException("Unknown comment file.");
            var captured = Repository.Captured(comment.FileId);
            if (comment.Side == "file")
            {
                if (comment.StartLine is not null || comment.EndLine is not null)
                    throw new ArgumentException("File comments cannot have line numbers.");
            }
            else
            {
                if (comment.Side is not ("old" or "new") || comment.StartLine is not > 0 ||
                    comment.EndLine is not > 0 || comment.EndLine < comment.StartLine ||
                    captured is null || captured.Omission is not null)
                    throw new ArgumentException("Invalid inline comment anchor.");
                var lines = captured.Rows.Select(r => comment.Side == "old" ? r.OldLine : r.NewLine)
                    .Where(n => n.HasValue).Select(n => n!.Value).ToHashSet();
                if (comment.EndLine - comment.StartLine > GitDiff.MaxRows ||
                    !lines.Contains(comment.StartLine.Value) || !lines.Contains(comment.EndLine.Value))
                    throw new ArgumentException("Comment line range is outside the captured file.");
            }
            if (comment.Body.Trim().Length == 0) continue;
            comments.Add(new(comment.Id, file!.OldPath, file.NewPath, "git-diff",
                Repository.MergeBase is null ? null : Repository.Head, comment.Side, comment.StartLine,
                comment.EndLine, comment.Body.Trim(), captured?.OldHash, captured?.NewHash));
        }
        return new(1, Id, true, Repository.Root, Repository.Base, Repository.MergeBase, Repository.Head,
            submission.OverallNote.Trim(), comments);
    }

    public async Task<TerminalResult> SubmitAsync(Submission submission, Func<Feedback, Task> write)
    {
        var feedback = Validate(submission);
        bool empty = feedback.OverallNote.Length == 0 && feedback.Comments.Count == 0;
        lock (state)
        {
            if (terminal is not null) throw new InvalidOperationException("Review already completed.");
            terminal = empty ? new(1, "empty", "No feedback submitted.") : new(0, "submitted", "Review submitted.");
        }
        if (!empty)
        {
            try { await write(feedback); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Could not write final feedback: " + e.Message);
                lock (state) terminal = new(1, "error", "Could not write final feedback.");
            }
        }
        lock (state) return terminal;
    }

    // Called after the terminal HTTP response completes, so shutdown does not race its acknowledgement.
    public void AcknowledgeTerminal()
    {
        lock (state) { if (terminal is not null) completion.TrySetResult(terminal); }
    }

    public static string Markdown(Feedback feedback)
    {
        var lines = new List<string> { "Please address the following feedback", "" };
        if (feedback.OverallNote.Length > 0) { lines.Add(feedback.OverallNote); lines.Add(""); }
        for (int i = 0; i < feedback.Comments.Count; i++)
        {
            var comment = feedback.Comments[i];
            string path = comment.OldPath is not null && comment.NewPath is not null && comment.OldPath != comment.NewPath
                ? DisplayPath(comment.OldPath) + " -> " + DisplayPath(comment.NewPath)
                : DisplayPath(comment.NewPath ?? comment.OldPath ?? "(unknown file)");
            string location = comment.Side == "file" ? path : path + ":" + comment.StartLine +
                (comment.EndLine != comment.StartLine ? "-" + comment.EndLine : "") + " (" + comment.Side + ")";
            lines.Add($"{i + 1}. [git diff] {location}");
            lines.AddRange(comment.Body.Replace("\r\n", "\n").Split('\n').Select(line => "   " + line));
            lines.Add("");
        }
        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    private static string DisplayPath(string path) => path.Any(char.IsControl)
        ? JsonSerializer.Serialize(path) : path;

    public static async Task WriteAsync(Feedback feedback, CliOptions options)
    {
        string output = options.Format == "json" ? JsonSerializer.Serialize(feedback, Json) + "\n" : Markdown(feedback);
        if (Encoding.UTF8.GetByteCount(output) > MaxRequestBytes * 2)
            throw new IOException("Formatted feedback exceeds the output limit.");
        if (options.Output is null)
        {
            await Console.Out.WriteAsync(output);
            await Console.Out.FlushAsync();
            return;
        }
        string directory = Path.GetDirectoryName(options.Output)!;
        string temporary = Path.Combine(directory, ".diff-review-slim-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var streamOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, streamOptions))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(output);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            File.Move(temporary, options.Output, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
