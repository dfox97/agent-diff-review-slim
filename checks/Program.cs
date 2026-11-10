#:project ../AgentDiffReview.csproj
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property TreatWarningsAsErrors=true
// PublishAot=false keeps restore inside NuGet.Config's cleared package sources:
// AOT/self-contained runtime packs are feed-only, and the checks need no packages.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentDiffReview;

int passed = 0, failed = 0, skipped = 0;
async Task Check(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine("PASS " + name); passed++; }
    catch (SkipCheck e) { Console.WriteLine("SKIP " + name + ": " + e.Message); skipped++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e); failed++; }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

await Check("CLI parsing and private forwarding constraints", () =>
{
    var options = CliOptions.Parse(["open", "main", "--no-open", "--port", "12345", "--format", "json"]);
    Assert(options.Base == "main" && options.Port == 12345 && options.Browser == "none" && options.Format == "json");
    Throws<ArgumentException>(() => CliOptions.Parse(["--base"]));
    Throws<ArgumentException>(() => CliOptions.Parse(["open", "main", "other"]));
    Throws<ArgumentException>(() => CliOptions.Parse(["--public-url", "https://example.com/"]));
    Throws<ArgumentException>(() => CliOptions.Parse(["--public-url", "http://localhost:1234/path"]));
    Throws<ArgumentException>(() => CliOptions.Parse(["--public-url", "http://user@localhost:1234/"]));
    Throws<ArgumentException>(() => CliOptions.Parse(["--port", "70000"]));
    Assert(CliOptions.Parse(["--public-url", "http://localhost:4321/"]).PublicUrl!.Port == 4321);
    return Task.CompletedTask;
});

await Check("NUL-delimited status parsing preserves awkward paths", () =>
{
    var files = GitDiff.ParseNames("M\0a\tb\nc.txt\0R100\0old name\0new name\0D\0deleted\0A\0added\0");
    Assert(files.Count == 4 && files[0].NewPath == "a\tb\nc.txt");
    Assert(files[1].OldPath == "old name" && files[1].NewPath == "new name");
    Assert(files[2].NewPath is null && files[3].OldPath is null);
    Throws<InvalidDataException>(() => GitDiff.ParseNames("R100\0old\0"));
    return Task.CompletedTask;
});

await Check("Patch old/new anchors, CRLF and missing final newline", () =>
{
    var rows = GitDiff.ParsePatch("diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1,2 +1,3 @@\n same\r\n-old\n\\ No newline at end of file\n+new\n+last\n\\ No newline at end of file\n");
    Assert(rows.Count == 4 && rows[0].Text == "same\r" && rows[0].OldLine == 1 && rows[0].NewLine == 1);
    Assert(rows[1].OldLine == 2 && rows[1].NewLine is null && rows[1].OldNoNewline);
    Assert(rows[3].NewLine == 3 && rows[3].NewNoNewline);
    var add = GitDiff.ParsePatch("@@ -0,0 +1 @@\n+x\n");
    Assert(add.Single().OldLine is null && add.Single().NewLine == 1);
    var deleted = GitDiff.ParsePatch("@@ -1 +0,0 @@\n-x\n");
    Assert(deleted.Single().OldLine == 1 && deleted.Single().NewLine is null);
    Throws<InvalidDataException>(() => GitDiff.ParsePatch("@@ -1,2 +1,2 @@\n only one\n"));
    Throws<InvalidDataException>(() => GitDiff.ParsePatch("@@ invalid @@\n"));
    return Task.CompletedTask;
});

await Check("Unborn repository includes staged, untracked and empty files", async () =>
{
    using var fixture = await Fixture.Create();
    await fixture.Write("staged.txt", "staged\n"); await fixture.Git("add", "--", "staged.txt");
    await fixture.Write("- [x] café.txt", "untracked"); await fixture.Write("empty.txt", "");
    var repo = await GitRepository.OpenAsync(fixture.Path, null);
    Assert(repo.Head is null && repo.Files.Count == 3);
    foreach (var file in repo.Files)
    {
        var diff = await repo.LoadAsync(file.Id);
        Assert(diff.Omission is null && diff.Rows.All(r => r.OldLine is null));
        if (file.NewPath == "empty.txt") Assert(diff.Rows.Count == 0);
        if (file.NewPath == "- [x] café.txt") Assert(diff.Rows.Single().NewNoNewline);
    }
});

await Check("Working-tree comparison combines staged/unstaged changes and freezes capture", async () =>
{
    using var fixture = await Fixture.Create();
    await fixture.Write("file.txt", "one\nold\n"); await fixture.Commit();
    await fixture.Write("file.txt", "one\nstaged\n"); await fixture.Git("add", "--", "file.txt");
    await fixture.Write("file.txt", "one\nworking\n");
    var repo = await GitRepository.OpenAsync(fixture.Path, null);
    var diff = await repo.LoadAsync(repo.Files.Single().Id);
    Assert(diff.Omission is null, diff.Omission ?? "");
    Assert(diff.Rows.Any(r => r.Kind == "added" && r.Text == "working"));
    Assert(!diff.Rows.Any(r => r.Text == "staged"));
    await fixture.Write("file.txt", "newer content after capture\n");
    Assert(ReferenceEquals(diff, await repo.LoadAsync(repo.Files.Single().Id)), "Captured rows must not silently change");
    var indexed = await GitRepository.OpenAsync(fixture.Path, null);
    await fixture.Write("file.txt", "changed again, with a different size\n");
    Assert((await indexed.LoadAsync(indexed.Files.Single().Id)).Omission!.Contains("changed since indexing"));
});

await Check("Base comparisons use committed HEAD, not dirty working contents", async () =>
{
    using var fixture = await Fixture.Create();
    await fixture.Write("file.txt", "root\nold\n"); await fixture.Commit(); await fixture.Git("branch", "base");
    await fixture.Write("file.txt", "root\ncommitted\n"); await fixture.Commit();
    await fixture.Write("file.txt", "root\nDIRTY\n"); await fixture.Write("untracked.txt", "not a branch change\n");
    var repo = await GitRepository.OpenAsync(fixture.Path, "base");
    Assert(repo.MergeBase is not null && repo.Files.Count == 1);
    var diff = await repo.LoadAsync(repo.Files.Single().Id);
    Assert(diff.Omission is null, diff.Omission ?? "");
    Assert(diff.Rows.Any(r => r.Kind == "added" && r.Text == "committed"));
    Assert(!diff.Rows.Any(r => r.Text.Contains("DIRTY")));
    await fixture.Git("update-ref", "refs/remotes/origin/remote-base", repo.MergeBase!);
    Assert((await GitRepository.OpenAsync(fixture.Path, "remote-base")).MergeBase == repo.MergeBase);
});

await Check("Indexing respects repository CRLF normalization while patches preserve captured bytes", async () =>
{
    using var fixture = await Fixture.Create();
    await fixture.Git("config", "core.autocrlf", "true");
    await fixture.Write("crlf.txt", "one\r\ntwo\r\n"); await fixture.Commit();
    Assert((await GitRepository.OpenAsync(fixture.Path, null)).Files.Count == 0,
        "A clean CRLF checkout was falsely indexed as modified");
    await fixture.Write("crlf.txt", "one\r\nchanged\r\n");
    var repo = await GitRepository.OpenAsync(fixture.Path, null);
    var diff = await repo.LoadAsync(repo.Files.Single().Id);
    Assert(diff.Omission is null && diff.NewCrlf && !diff.OldCrlf);
    Assert(diff.Rows.Any(r => r.NewLine == 2 && r.Text == "changed\r"));
});

await Check("Rename/delete identity and unchanged full-file rows", async () =>
{
    using var fixture = await Fixture.Create();
    await fixture.Write("old name.txt", "same\nlast"); await fixture.Write("deleted.txt", "gone\n"); await fixture.Commit();
    await fixture.Git("mv", "--", "old name.txt", "new name.txt"); File.Delete(System.IO.Path.Combine(fixture.Path, "deleted.txt"));
    var repo = await GitRepository.OpenAsync(fixture.Path, null);
    var renamed = repo.Files.Single(f => f.Status == "renamed");
    Assert(renamed.OldPath == "old name.txt" && renamed.NewPath == "new name.txt");
    var diff = await repo.LoadAsync(renamed.Id);
    Assert(diff.Rows.Count == 2 && diff.Rows.All(r => r.Kind == "context") && diff.Rows[^1].NewNoNewline);
    var deleted = await repo.LoadAsync(repo.Files.Single(f => f.Status == "deleted").Id);
    Assert(deleted.Rows.Single().OldLine == 1 && deleted.Rows.Single().NewLine is null);
});

await Check("Binary, non-UTF-8, oversized content and traversal are explicit omissions", async () =>
{
    using var fixture = await Fixture.Create();
    await File.WriteAllBytesAsync(System.IO.Path.Combine(fixture.Path, "binary.dat"), [1, 0, 2]);
    await File.WriteAllBytesAsync(System.IO.Path.Combine(fixture.Path, "encoding.txt"), [255, 254, 128]);
    await File.WriteAllBytesAsync(System.IO.Path.Combine(fixture.Path, "large.txt"), new byte[GitDiff.MaxFileBytes + 1]);
    var repo = await GitRepository.OpenAsync(fixture.Path, null);
    Assert(repo.Files.Count == 3);
    foreach (var file in repo.Files) Assert((await repo.LoadAsync(file.Id)).Omission is not null);
    Throws<IOException>(() => GitDiff.SafeWorkingPath(fixture.Path, "../outside"));
    Throws<IOException>(() => GitDiff.SafeWorkingPath(fixture.Path, ".git/config"));
    Throws<IOException>(() => GitDiff.SafeWorkingPath(fixture.Path, "a/../../outside"));
    if (OperatingSystem.IsWindows()) Throws<IOException>(() => GitDiff.SafeWorkingPath(fixture.Path, "a\\..\\outside"));
});

await Check("Ancestor and final-component symlinks cannot be read", async () =>
{
    using var fixture = await Fixture.Create();
    using var outside = await Fixture.Create();
    await outside.Write("secret.txt", "not reviewable\n");
    string link = System.IO.Path.Combine(fixture.Path, "link");
    try { Directory.CreateSymbolicLink(link, outside.Path); }
    catch (Exception e) when (e is UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException)
    { throw new SkipCheck("OS symlink-creation permission unavailable; traversal checks run separately."); }
    try { Throws<IOException>(() => GitDiff.SafeWorkingPath(fixture.Path, "link/secret.txt")); }
    finally { Directory.Delete(link); }
    string fileLink = System.IO.Path.Combine(fixture.Path, "file-link");
    File.CreateSymbolicLink(fileLink, System.IO.Path.Combine(outside.Path, "secret.txt"));
    try { Throws<IOException>(() => GitDiff.SafeWorkingPath(fixture.Path, "file-link")); }
    finally { File.Delete(fileLink); }
});

await Check("Submission validation, formatting, output identity and once-only completion", async () =>
{
    using var fixture = await Fixture.Create();
    await fixture.Write("file.txt", "old\n"); await fixture.Commit(); await fixture.Write("file.txt", "new\n");
    var repo = await GitRepository.OpenAsync(fixture.Path, null);
    string id = repo.Files.Single().Id; await repo.LoadAsync(id);
    var session = new ReviewSession(repo);
    var comment = new ReviewComment("c1", id, "new", 1, 1, "  Fix this.\nAnd this.  ");
    var submission = new Submission("  Overall note.  ", [comment]);
    Assert(session.Authenticate(session.Token) && !session.Authenticate(new string('x', 64)) && !session.Authenticate(""));
    var feedback = session.Validate(submission);
    Assert(feedback.Comments.Single().NewPath == "file.txt" && feedback.Comments.Single().NewHash?.Length == 64);
    Assert(ReviewSession.Markdown(feedback) == "Please address the following feedback\n\nOverall note.\n\n1. [git diff] file.txt:1 (new)\n   Fix this.\n   And this.\n");
    string json = JsonSerializer.Serialize(feedback, ReviewSession.Json);
    Assert(json.Contains("\"version\":1") && json.Contains("\"newPath\":\"file.txt\""));
    Throws<ArgumentException>(() => session.Validate(submission with { Comments = [comment with { FileId = "unknown" }] }));
    Throws<ArgumentException>(() => session.Validate(submission with { Comments = [comment with { StartLine = 100 }] }));
    Throws<ArgumentException>(() => session.Validate(submission with { Comments = [comment with { Side = "invalid" }] }));
    Throws<ArgumentException>(() => session.Validate(submission with { Comments = [comment, comment] }));
    Throws<ArgumentException>(() => session.Validate(submission with { Comments = [comment with { Body = new string('a', 8001) }] }));
    Throws<ArgumentException>(() => session.Validate(new(null!, null!)));
    int writes = 0;
    var outcome = await session.SubmitAsync(submission, _ => { writes++; return Task.CompletedTask; });
    Assert(outcome.ExitCode == 0 && !session.Completion.IsCompleted && !session.Cancel());
    try { await session.SubmitAsync(submission, _ => { writes++; return Task.CompletedTask; }); throw new Exception("Accepted duplicate submission"); }
    catch (InvalidOperationException) { }
    session.AcknowledgeTerminal();
    Assert(writes == 1 && (await session.Completion).Status == "submitted");
    var empty = new ReviewSession(repo);
    Assert((await empty.SubmitAsync(new(" ", []), _ => throw new Exception("Empty review must not write"))).ExitCode == 1);
    var failedWrite = new ReviewSession(repo);
    Assert((await failedWrite.SubmitAsync(submission, _ => throw new IOException("simulated output failure"))).Status == "error");
});

await Check("Atomic file output, JSON and failure cleanup", async () =>
{
    using var fixture = await Fixture.Create();
    var feedback = new Feedback(1, "session", true, fixture.Path, null, null, null, "Note", []);
    string destination = System.IO.Path.Combine(fixture.Path, "feedback.json");
    await File.WriteAllTextAsync(destination, "previous");
    var options = CliOptions.Parse(["--out", destination, "--format", "json"]);
    await ReviewSession.WriteAsync(feedback, options);
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
    Assert(document.RootElement.GetProperty("overallNote").GetString() == "Note");
    Assert(!Directory.EnumerateFiles(fixture.Path, ".diff-review-slim-*.tmp").Any());
    try { await ReviewSession.WriteAsync(feedback, options with { Output = System.IO.Path.Combine(fixture.Path, "missing", "out") }); throw new Exception("Expected output failure"); }
    catch (DirectoryNotFoundException) { }
});

await Check("Disconnect cancels; reconnect invalidates an older disconnect timer", async () =>
{
    using var fixture = await Fixture.Create();
    var repo = await GitRepository.OpenAsync(fixture.Path, null);
    var closed = new ReviewSession(repo, 1);
    closed.Disconnect(closed.Connect());
    Assert((await closed.Completion.WaitAsync(TimeSpan.FromSeconds(4))).ExitCode == 1);
    var reload = new ReviewSession(repo, 1);
    long first = reload.Connect(); reload.Disconnect(first);
    long second = reload.Connect();
    await Task.Delay(1200);
    Assert(!reload.IsTerminal, "Reload incorrectly cancelled the review");
    reload.Disconnect(first); await Task.Delay(100);
    Assert(!reload.IsTerminal, "An obsolete connection cancelled the new one");
    reload.Disconnect(second);
    Assert((await reload.Completion.WaitAsync(TimeSpan.FromSeconds(4))).Status == "cancelled");
});

await Check("HTTP security, lazy load, held connection and successful clean stdout", async () =>
{
    using var fixture = await Fixture.Create(); await fixture.Write("file.txt", "new\n");
    await using var host = await Reviewer.Start(fixture.Path);
    Assert((await host.Get("/api/session", token: "wrong")).StatusCode == HttpStatusCode.Forbidden);
    Assert((await host.Get("/api/session", origin: "http://evil.example")).StatusCode == HttpStatusCode.Forbidden);
    Assert((await host.Get("/api/session", host: "evil.example")).StatusCode == HttpStatusCode.Forbidden);
    Assert((await host.Get("/README.md")).StatusCode == HttpStatusCode.NotFound);
    // Assets are served from a directory scan, so presence in the published wwwroot is enough.
    Assert(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "wwwroot")).Length > 2,
        "Published browser assets are missing from the harness output.");
    Assert((await host.Get("/")).IsSuccessStatusCode);
    Assert((await host.Get("/api/files/unknown")).StatusCode == HttpStatusCode.BadRequest);
    using (var nonJson = await host.Post("/api/cancel", "{}", "text/plain")) Assert(nonJson.StatusCode == HttpStatusCode.UnsupportedMediaType);
    using (var oversized = await host.Post("/api/submit", new string('x', 256001))) Assert(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge);
    using (var malformed = await host.Post("/api/submit", "{")) Assert(malformed.StatusCode == HttpStatusCode.BadRequest);
    using var metadata = JsonDocument.Parse(await (await host.Get("/api/session")).Content.ReadAsStringAsync());
    string id = metadata.RootElement.GetProperty("files")[0].GetProperty("id").GetString()!;
    using var diff = await host.Get("/api/files/" + id);
    Assert(diff.IsSuccessStatusCode && diff.Headers.CacheControl!.NoStore);
    using var lifetime = await host.Lifetime();
    var payload = new Submission("HTTP feedback", [new("c1", id, "new", 1, 1, "Line feedback")]);
    string previewText;
    using (var preview = await host.Post("/api/preview", JsonSerializer.Serialize(payload, ReviewSession.Json)))
    {
        Assert(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        previewText = await preview.Content.ReadAsStringAsync();
    }
    using var submitted = await host.Post("/api/submit", JsonSerializer.Serialize(payload, ReviewSession.Json));
    Assert(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());
    var result = await host.Wait();
    Assert(result.Code == 0 && result.Output.Contains("HTTP feedback") && result.Output.Contains("file.txt:1 (new)"));
    Assert(!result.Output.Contains("Review URL") && !result.Error.Contains("Line feedback"));
    // The browser Copy action reuses the server formatter, so preview and stdout agree.
    Assert(previewText == result.Output, "Copy preview diverged from final stdout:" + Environment.NewLine + previewText);
});

await Check("HTTP reload reconnects; closing the actual lifetime stream cancels the process", async () =>
{
    using var fixture = await Fixture.Create(); await fixture.Write("file.txt", "new\n");
    await using var host = await Reviewer.Start(fixture.Path);
    var first = await host.Lifetime(); first.Dispose();
    using var second = await host.Lifetime();
    await Task.Delay(1400);
    Assert((await host.Get("/api/session")).IsSuccessStatusCode, "Reload cancelled the live review");
    second.Dispose();
    var result = await host.Wait();
    Assert(result.Code == 1 && result.Output.Length == 0);
});

await Check("HTTP Cancel, empty submission and overall deadline produce no feedback", async () =>
{
    using var fixture = await Fixture.Create();
    foreach (string action in new[] { "cancel", "empty", "deadline" })
    {
        await using var host = await Reviewer.Start(fixture.Path, action == "deadline" ? 2 : 20);
        if (action != "deadline")
        {
            using var response = await host.Post(action == "cancel" ? "/api/cancel" : "/api/submit",
                action == "cancel" ? "{}" : "{\"overallNote\":\" \",\"comments\":[]}");
            Assert(response.IsSuccessStatusCode);
        }
        var result = await host.Wait();
        Assert(result.Code == 1 && result.Output.Length == 0, action + " produced feedback or wrong exit status");
    }
});

Console.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped");
return failed == 0 ? 0 : 1;

sealed class SkipCheck(string message) : Exception(message);

sealed class Fixture : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("diff-review-check-").FullName;
    public static async Task<Fixture> Create()
    {
        var fixture = new Fixture();
        await fixture.Git("init", "-q", "-b", "main");
        await fixture.Git("config", "user.name", "Diff Review Checks");
        await fixture.Git("config", "user.email", "checks@example.invalid");
        await fixture.Git("config", "commit.gpgSign", "false");
        await fixture.Git("config", "core.autocrlf", "false");
        await fixture.Git("config", "core.hooksPath", System.IO.Path.Combine(fixture.Path, ".git", "no-hooks"));
        return fixture;
    }
    public Task Write(string path, string content) => File.WriteAllTextAsync(System.IO.Path.Combine(Path, path), content, new UTF8Encoding(false));
    public async Task Git(params string[] args)
    {
        var result = await GitDiff.RunAsync(Path, args);
        if (result.Code != 0) throw new Exception("Fixture Git operation failed: " + result.Error);
    }
    public async Task Commit() { await Git("add", "-A"); await Git("commit", "-qm", "fixture"); }
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}

sealed class Reviewer : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly HttpClient client = new(new SocketsHttpHandler { MaxResponseDrainSize = 0 }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Uri address;
    private readonly string token;
    private Reviewer(Process process, Uri address, string token)
    {
        this.process = process; this.address = address; this.token = token;
        output = process.StandardOutput.ReadToEndAsync(); error = process.StandardError.ReadToEndAsync();
    }
    public static async Task<Reviewer> Start(string repository, int timeoutSeconds = 20)
    {
        string assembly = typeof(AgentDiffReview.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { assembly, "open", "--repo", repository, "--no-open", "--grace-seconds", "1", "--timeout-seconds", timeoutSeconds.ToString() }) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        try
        {
            while (await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is { } line)
            {
                if (!line.StartsWith("Review URL: ", StringComparison.Ordinal)) continue;
                var url = new Uri(line[12..]);
                return new(process, new Uri(url.GetLeftPart(UriPartial.Authority)), url.Fragment[1..]);
            }
            throw new Exception("Reviewer exited before printing its URL");
        }
        catch { if (!process.HasExited) process.Kill(true); process.Dispose(); throw; }
    }
    private HttpRequestMessage Request(HttpMethod method, string path, string? tokenOverride = null)
    {
        var message = new HttpRequestMessage(method, new Uri(address, path));
        message.Headers.Add("X-Review-Token", tokenOverride ?? token);
        return message;
    }
    public async Task<HttpResponseMessage> Get(string path, string? token = null, string? origin = null, string? host = null)
    {
        using var message = Request(HttpMethod.Get, path, token);
        if (origin is not null) message.Headers.Add("Origin", origin);
        if (host is not null) message.Headers.Host = host;
        return await client.SendAsync(message);
    }
    public async Task<HttpResponseMessage> Post(string path, string body, string type = "application/json")
    {
        using var message = Request(HttpMethod.Post, path);
        message.Content = new StringContent(body, Encoding.UTF8, type);
        return await client.SendAsync(message);
    }
    public async Task<HttpResponseMessage> Lifetime()
    {
        using var message = Request(HttpMethod.Get, "/api/lifetime");
        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync();
        if (await stream.ReadAsync(new byte[10]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 0) throw new Exception("Lifetime stream ended before acknowledgement");
        return response;
    }
    public async Task<(int Code, string Output, string Error)> Wait()
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return (process.ExitCode, await output, await error);
    }
    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        process.Dispose();
    }
}
