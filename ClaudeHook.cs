using System.Text.Json;

namespace AgentDiffReview;

/// <summary>Adapter for the same UserPromptExpansion contract as Agent Diff Review.</summary>
public static class ClaudeHook
{
    public static async Task<int> RunAsync(string[] args, Func<CliOptions, Func<Feedback, Task>, Task<int>> review)
    {
        object response;
        try
        {
            var options = CliOptions.Parse(["open", .. args]);
            if (options.Help) { Console.WriteLine(CliOptions.Usage); return 0; }
            if (options.Output is not null || options.Format != "markdown" || options.Base is not null)
                throw new ArgumentException("Hook mode takes the base from command_args and returns JSON on stdout; do not use --base, --out or --format json.");
            if (options.TimeoutSeconds > 570)
                throw new ArgumentException("Hook deadline must be at most 570 seconds, below the configured 600-second hook timeout.");

            using var input = JsonDocument.Parse(await ReadInputAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            var root = input.RootElement;
            string name = root.GetProperty("command_name").GetString() ?? "";
            if (name != "diff-review" && !name.EndsWith(":diff-review", StringComparison.Ordinal))
            {
                Console.WriteLine("{}"); // An unrelated command must not open a reviewer.
                return 0;
            }
            if (root.GetProperty("hook_event_name").GetString() != "UserPromptExpansion")
                throw new ArgumentException("Expected the UserPromptExpansion hook contract.");
            string? repository = root.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null;
            string? baseRef = root.TryGetProperty("command_args", out var commandArgs) ? commandArgs.GetString()?.Trim() : null;
            options = options with
            {
                Repository = repository is null ? options.Repository : Path.GetFullPath(repository),
                Base = string.IsNullOrEmpty(baseRef) ? null : baseRef
            };

            // Capture feedback in memory, not stdout. Even a late shutdown failure must
            // block expansion rather than leak a partial/successful-looking prompt.
            string feedback = "";
            int code = await review(options, value =>
            {
                string markdown = ReviewSession.Markdown(value);
                if (System.Text.Encoding.UTF8.GetByteCount(markdown) > ReviewSession.MaxOutputBytes)
                    throw new IOException("Formatted feedback exceeds the output limit.");
                feedback = markdown.Trim();
                return Task.CompletedTask;
            });
            response = code == 0 && feedback.Length > 0
                ? (object)new { hookSpecificOutput = new { hookEventName = "UserPromptExpansion", additionalContext = feedback } }
                : Block("Review cancelled, closed, empty, timed out or failed; no feedback was sent.");
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or System.ComponentModel.Win32Exception or FormatException or
                                          JsonException or KeyNotFoundException or OperationCanceledException or TimeoutException)
        {
            Console.Error.WriteLine("Claude hook failed: " + error.Message);
            // Do not copy raw diagnostics (which may contain credential-bearing URLs)
            // into Claude's context or the hook's block reason.
            response = Block("Slim review could not complete. Check the hook configuration and stderr; no feedback was sent.");
        }
        Console.WriteLine(JsonSerializer.Serialize(response));
        return 0; // A handled block is a successful hook response, not feedback success.
    }

    private static object Block(string reason) => new { decision = "block", reason };

    private static async Task<string> ReadInputAsync()
    {
        var input = new System.Text.StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await Console.In.ReadAsync(buffer)) > 0)
        {
            if (input.Length + count > 64000) throw new ArgumentException("Hook input is too large.");
            input.Append(buffer, 0, count);
        }
        return input.ToString();
    }
}
