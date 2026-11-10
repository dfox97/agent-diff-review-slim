using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

namespace AgentDiffReview;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Help) { Console.WriteLine(CliOptions.Usage); return 0; }
            return await RunAsync(options);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Review cancelled or deadline exceeded."); return 1; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
                                      InvalidOperationException or System.ComponentModel.Win32Exception or FormatException)
        {
            Console.Error.WriteLine("Review failed: " + e.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(CliOptions options)
    {
        using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ReviewSession? session = null;
        ConsoleCancelEventHandler interrupt = (_, e) =>
        {
            e.Cancel = true;
            session?.Cancel("Review cancelled from the terminal.");
            operation.Cancel();
        };
        Console.CancelKeyPress += interrupt;
        try
        {
            var repository = await GitRepository.OpenAsync(options.Repository, options.Base, operation.Token);
            session = new(repository, options.GraceSeconds);
            using var deadline = operation.Token.Register(() => session.Cancel("Review deadline exceeded or process cancelled."));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = "Production"
            });
            builder.Configuration.Sources.Clear(); // launch-time options, not inherited hosting settings
            builder.Logging.ClearProviders();      // stdout is reserved for final review feedback
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.AddServerHeader = false;
                server.Limits.MaxRequestBodySize = ReviewSession.MaxRequestBytes;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                server.Listen(options.Listen, options.Port);
            });
            await using var app = builder.Build();
            using var stopping = app.Lifetime.ApplicationStopping.Register(() =>
            {
                session.Cancel("Reviewer process stopped.");
                operation.Cancel();
            });
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store, no-transform";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                bool trusted;
                lock (hosts)
                {
                    trusted = hosts.Contains(context.Request.Host.Value ?? "") &&
                        (!context.Request.Headers.TryGetValue("Origin", out var origin) || origins.Contains(origin.ToString())) &&
                        context.Request.Headers["Sec-Fetch-Site"] != "cross-site";
                }
                if (!trusted) { context.Response.StatusCode = 403; return; }
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    if (!session.Authenticate(context.Request.Headers["X-Review-Token"].ToString()))
                    { context.Response.StatusCode = 403; return; }
                    if (session.IsTerminal) { context.Response.StatusCode = 410; return; }
                    if (context.Request.Method == "POST" && !context.Request.HasJsonContentType())
                    { context.Response.StatusCode = 415; return; }
                    if (context.Request.ContentLength > ReviewSession.MaxRequestBytes)
                    {
                        // Do not reuse a connection whose rejected body cannot be drained within the limit.
                        context.Response.Headers.Connection = "close";
                        context.Response.StatusCode = 413;
                        return;
                    }
                }
                try { await next(context); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || operation.IsCancellationRequested)
                { context.Abort(); }
                catch (Exception error) when (error is ArgumentException or JsonException or BadHttpRequestException or IOException or InvalidOperationException)
                {
                    if (context.Response.HasStarted) { context.Abort(); return; }
                    context.Response.StatusCode = error switch
                    {
                        BadHttpRequestException bad => bad.StatusCode,
                        IOException => 503,
                        InvalidOperationException => 409,
                        _ => 400
                    };
                    await context.Response.WriteAsJsonAsync(new { error = error is JsonException ? "Malformed JSON request." : error.Message });
                }
            });
            // Assets are discovered from the published wwwroot instead of being allowlisted by hand:
            // adding a browser file cannot silently 404, and an unknown extension fails startup.
            var assetTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".html"] = "text/html", [".js"] = "text/javascript", [".css"] = "text/css"
            };
            string assetRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!File.Exists(Path.Combine(assetRoot, "index.html")))
                throw new IOException("Missing published browser assets; build/publish the application first.");
            foreach (string file in Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories))
            {
                string assetName = Path.GetRelativePath(assetRoot, file).Replace('\\', '/');
                if (!assetTypes.TryGetValue(Path.GetExtension(file), out string? contentType))
                    throw new IOException("Unsupported published browser asset: " + assetName);
                app.MapGet("/" + assetName, () => Results.File(file, contentType));
                if (assetName == "index.html") app.MapGet("/", () => Results.File(file, contentType));
            }
            app.MapGet("/api/session", () => Results.Json(session.Metadata(), ReviewSession.Json));
            app.MapGet("/api/files/{id}", async (string id, HttpContext context) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, context.RequestAborted);
                return Results.Json(await repository.LoadAsync(id, linked.Token), ReviewSession.Json);
            });
            app.MapGet("/api/lifetime", async (HttpContext context) =>
            {
                long version = session.Connect();
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                try
                {
                    context.Response.ContentType = "text/plain";
                    context.Response.Headers["X-Accel-Buffering"] = "no";
                    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                    await context.Response.WriteAsync("connected\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(Timeout.Infinite, lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                finally { session.Disconnect(version); }
            });
            app.MapPost("/api/preview", async (HttpContext context) =>
            {
                // Single source of truth for feedback Markdown: the browser Copy action
                // reuses the exact formatter that produces final stdout.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, context.RequestAborted);
                var submission = await context.Request.ReadFromJsonAsync<Submission>(ReviewSession.Json, linked.Token)
                    ?? throw new ArgumentException("A submission body is required.");
                return Results.Text(ReviewSession.Markdown(session.Validate(submission)), "text/markdown");
            });
            app.MapPost("/api/submit", async (HttpContext context) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, context.RequestAborted);
                var submission = await context.Request.ReadFromJsonAsync<Submission>(ReviewSession.Json, linked.Token)
                    ?? throw new ArgumentException("A submission body is required.");
                var result = await session.SubmitAsync(submission,
                    feedback => ReviewSession.WriteAsync(feedback, options, operation.Token), operation.Token);
                try
                {
                    context.Response.StatusCode = result.Status == "error" ? 500 : 200;
                    await context.Response.WriteAsJsonAsync(result, ReviewSession.Json, linked.Token);
                    await context.Response.CompleteAsync().WaitAsync(linked.Token);
                }
                finally { session.AcknowledgeTerminal(); }
            });
            app.MapPost("/api/cancel", async (HttpContext context) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, context.RequestAborted);
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: linked.Token);
                if (body.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object.");
                if (!session.Cancel(acknowledge: false)) { context.Response.StatusCode = 409; return; }
                try
                {
                    await context.Response.WriteAsJsonAsync(new { status = "cancelled", message = "Review cancelled." }, linked.Token);
                    await context.Response.CompleteAsync().WaitAsync(linked.Token);
                }
                finally { session.AcknowledgeTerminal(); }
            });

            await app.StartAsync(operation.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new IOException("The reviewer did not expose a listening address.");
            var bound = new Uri(addresses.Single());
            var local = new UriBuilder("http", IPAddress.IsLoopback(options.Listen) ? options.Listen.ToString() : "127.0.0.1", bound.Port).Uri;
            var visible = options.PublicUrl ?? local;
            lock (hosts)
            {
                foreach (var url in new[] { local, visible, new Uri($"http://localhost:{bound.Port}/") })
                    hosts.Add(url.Authority);
                foreach (string authority in hosts) origins.Add("http://" + authority);
            }
            string launchUrl = visible.AbsoluteUri + "#" + session.Token;
            Console.Error.WriteLine("Review URL: " + launchUrl);
            if (!IPAddress.IsLoopback(options.Listen))
                Console.Error.WriteLine("Explicit non-loopback bind: ensure the host-forwarded port is private/loopback.");
            Console.Error.WriteLine("Close the browser to cancel; reconnect grace: " + options.GraceSeconds + " seconds.");
            OpenBrowser(options, launchUrl);
            var outcome = await session.Completion;
            Console.Error.WriteLine(outcome.Message);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stop.Token);
            return outcome.ExitCode;
        }
        finally { Console.CancelKeyPress -= interrupt; }
    }

    private static void OpenBrowser(CliOptions options, string url)
    {
        if (options.Browser == "none") return;
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Automatic host-browser launch is not configured here; open the review URL on the host.");
            return;
        }
        try
        {
            var start = new ProcessStartInfo(options.Browser == "edge" ? "msedge.exe" : "chrome.exe") { UseShellExecute = true };
            start.ArgumentList.Add("--app=" + url);
            using var process = Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Console.Error.WriteLine("Browser launch unavailable; open the review URL manually."); }
    }
}
