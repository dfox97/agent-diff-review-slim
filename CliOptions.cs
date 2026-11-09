using System.Net;

namespace AgentDiffReview;

public sealed record CliOptions(
    string Repository, string? Base, string? Output, string Format,
    IPAddress Listen, int Port, Uri? PublicUrl, string Browser,
    int TimeoutSeconds, int GraceSeconds, bool Help)
{
    public const string Usage = """
        Usage: diff-review-slim open [base] [options]

          --repo <path>             Checkout to review (default: current directory)
          --base <branch>           Committed merge-base..HEAD comparison
          --out <path>              Write feedback atomically to a caller-owned path
          --format markdown|json    Final feedback format (default: markdown)
          --port <0..65535>          Local port; 0 chooses a free port (default)
          --listen <IP>             Bind address (default: 127.0.0.1)
          --public-url <URL>        Private forwarded http://localhost:<port>/ URL
          --browser edge|chrome|none  Best-effort local launch (Windows: edge)
          --no-open                 Alias for --browser none
          --timeout-seconds <n>     Overall deadline, 1..3600 (default: 570)
          --grace-seconds <n>       Reconnect grace, 1..30 (default: 5)
          -h, --help                Show help

        stdout contains only submitted non-empty feedback. Cancel/close/empty/error
        exits non-zero without feedback. URLs and diagnostics go to stderr.
        Non-loopback binding is explicit: keep the host-forwarded port private.
        Claude hook mode and history/all-files scopes are not implemented yet.
        """;

    public static CliOptions Parse(string[] args)
    {
        string repo = Directory.GetCurrentDirectory(), format = "markdown";
        string? @base = null, output = null;
        IPAddress listen = IPAddress.Loopback;
        int port = 0, timeout = 570, grace = 5;
        Uri? publicUrl = null;
        string browser = OperatingSystem.IsWindows() ? "edge" : "none";
        bool help = false;
        int start = args.Length > 0 && args[0] == "open" ? 1 : 0;
        for (int i = start; i < args.Length; i++)
        {
            string arg = args[i];
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {arg}.");
                return args[i];
            }
            switch (arg)
            {
                case "-h": case "--help": help = true; break;
                case "--repo": repo = Value(); break;
                case "--base":
                    if (@base is not null) throw new ArgumentException("Specify the base only once.");
                    @base = Value(); break;
                case "--out": output = Value(); break;
                case "--format": format = Value(); break;
                case "--port": port = Number(Value(), 0, 65535, arg); break;
                case "--timeout-seconds": timeout = Number(Value(), 1, 3600, arg); break;
                case "--grace-seconds": grace = Number(Value(), 1, 30, arg); break;
                case "--listen":
                    if (!IPAddress.TryParse(Value(), out var parsedListen)) throw new ArgumentException("Invalid listen IP.");
                    listen = parsedListen;
                    break;
                case "--public-url":
                    if (!Uri.TryCreate(Value(), UriKind.Absolute, out publicUrl) ||
                        publicUrl.Scheme != "http" || !publicUrl.IsLoopback ||
                        publicUrl.UserInfo.Length != 0 || publicUrl.Query.Length != 0 ||
                        publicUrl.Fragment.Length != 0 || publicUrl.AbsolutePath != "/")
                        throw new ArgumentException("Public URL must be a private http://localhost:<port>/ URL without credentials, path, query or fragment.");
                    break;
                case "--no-open": browser = "none"; break;
                case "--browser": browser = Value(); break;
                default:
                    if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
                    if (@base is not null) throw new ArgumentException("Unexpected extra positional argument.");
                    if (arg is "hook" or "clip") throw new ArgumentException($"{arg} mode is not implemented yet; use open.");
                    @base = arg;
                    break;
            }
        }
        if (format is not ("markdown" or "json")) throw new ArgumentException("Format must be markdown or json.");
        if (browser is not ("edge" or "chrome" or "none")) throw new ArgumentException("Browser must be edge, chrome or none.");
        return new(Path.GetFullPath(repo), @base, output is null ? null : Path.GetFullPath(output),
            format, listen, port, publicUrl, browser, timeout, grace, help);
    }

    private static int Number(string value, int min, int max, string name) =>
        int.TryParse(value, out int n) && n >= min && n <= max
            ? n : throw new ArgumentException($"{name} must be between {min} and {max}.");
}
