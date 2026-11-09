/** Optional local testing adapter. Pi supplies its own TypeScript loader and UI APIs.
 * No package.json, npm installation, model tool or dependency in the .NET app.
 */
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { wrapTextWithAnsi } from "@earendil-works/pi-tui";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { join } from "node:path";
import { StringDecoder } from "node:string_decoder";
import { fileURLToPath } from "node:url";

type ReviewResult = { code: number; output: string; diagnostic: string; cancelled: boolean };

function startReview(cwd: string, dll: string, base: string | undefined, onUrl: (url: string) => void) {
  const args = [dll, "open", "--repo", cwd];
  if (base) args.push("--base", base);
  if (process.env.DIFF_REVIEW_SLIM_NO_OPEN === "1") args.push("--no-open");
  const child = spawn("dotnet", args, {
    cwd, stdio: ["ignore", "pipe", "pipe"], windowsHide: true,
    detached: process.platform !== "win32",
  });
  const chunks: Buffer[] = [];
  const decoder = new StringDecoder("utf8");
  let bytes = 0, errorBytes = 0, lineBuffer = "", diagnostic = "";
  let url: URL | undefined, cancelled = false, exited = false, cancellationSent = false;
  let failure: string | undefined;
  let killTimer: ReturnType<typeof setTimeout> | undefined;

  function killOwnedTree() {
    if (exited || !child.pid) return;
    if (process.platform === "win32") {
      const killer = spawn(join(process.env.SystemRoot || "C:\\Windows", "System32", "taskkill.exe"),
        ["/PID", String(child.pid), "/T", "/F"], { windowsHide: true, stdio: "ignore" });
      killer.on("error", () => { if (!exited) child.kill(); });
    } else {
      try { process.kill(-child.pid, "SIGKILL"); }
      catch { if (!exited) child.kill("SIGKILL"); }
    }
  }
  function requestCancel() {
    if (!url || cancellationSent || exited) return;
    cancellationSent = true;
    const endpoint = new URL("/api/cancel", url);
    // The CLI owns its Git children. Prefer graceful cancellation; forced fallback
    // is restricted to this adapter's process tree, never an Edge/Chrome process.
    void fetch(endpoint, { method: "POST", headers: {
      "X-Review-Token": url.hash.slice(1), "Content-Type": "application/json",
    }, body: "{}", signal: AbortSignal.timeout(3000) }).catch(() => {});
  }
  function stop() {
    if (exited) return;
    cancelled = true;
    killTimer ??= setTimeout(killOwnedTree, 5000);
    requestCancel();
  }
  function readLine(line: string) {
    if (line.startsWith("Review URL: ")) {
      try {
        const parsed = new URL(line.slice(12));
        if (parsed.protocol !== "http:" || !["127.0.0.1", "localhost", "[::1]"].includes(parsed.hostname) ||
            parsed.username || parsed.password || parsed.pathname !== "/" || parsed.search ||
            !/^#[a-f0-9]{64}$/.test(parsed.hash)) throw new Error("Invalid reviewer URL");
        url = parsed;
        if (cancelled) requestCancel(); else onUrl(parsed.href);
      } catch { failure = "Reviewer emitted an invalid local URL."; stop(); }
    } else if (line.trim()) {
      diagnostic = line.replace(/[\x00-\x1f\x7f]/g, " ").slice(0, 500);
    }
  }
  child.stdout.on("data", (chunk: Buffer) => {
    bytes += chunk.length;
    if (bytes > 512000) { failure = "Reviewer feedback exceeded the adapter limit."; stop(); }
    else chunks.push(chunk);
  });
  child.stderr.on("data", (chunk: Buffer) => {
    errorBytes += chunk.length;
    if (errorBytes > 64000) { failure = "Reviewer diagnostics exceeded the adapter limit."; stop(); return; }
    lineBuffer += decoder.write(chunk);
    const lines = lineBuffer.split("\n");
    lineBuffer = lines.pop()!;
    for (const line of lines) readLine(line.replace(/\r$/, ""));
  });
  const result = new Promise<ReviewResult>(resolve => {
    child.on("error", () => { failure = "Could not start dotnet. Check the .NET 10 installation and PATH."; });
    child.on("close", code => {
      exited = true;
      clearTimeout(killTimer);
      lineBuffer += decoder.end();
      if (lineBuffer) readLine(lineBuffer.replace(/\r$/, ""));
      resolve({ code: failure ? 1 : code ?? 1, output: failure || cancelled ? "" : Buffer.concat(chunks).toString("utf8"),
        diagnostic: failure || diagnostic, cancelled });
    });
  });
  return { result, stop, get url() { return url?.href; } };
}

export default function (pi: ExtensionAPI) {
  let active: ReturnType<typeof startReview> | undefined;

  pi.registerCommand("diff-review-slim", {
    description: "Review with the .NET/browser CLI; insert feedback as a draft, never auto-send. Optional: <base-branch>",
    handler: async (args, ctx) => {
      if (!ctx.hasUI) return;
      if (active) { ctx.ui.notify("A slim review is already open.", "warning"); return; }
      if (!ctx.isIdle()) { ctx.ui.notify("Wait for pi to become idle before reviewing.", "warning"); return; }
      const dll = process.env.DIFF_REVIEW_SLIM_DLL || fileURLToPath(new URL("../artifacts/publish/diff-review-slim.dll", import.meta.url));
      if (!existsSync(dll)) {
        ctx.ui.notify("Publish slim first: dotnet publish AgentDiffReview.csproj -c Release -o artifacts/publish. Or set DIFF_REVIEW_SLIM_DLL to a published DLL.", "error");
        return;
      }
      let redraw = () => {};
      const handle = startReview(ctx.cwd, dll, args.trim() || undefined, url => {
        if (active !== handle) return;
        // Notifications are UI-only, not model messages or session entries.
        // RPC forwards this private URL as a UI record; clients must not log it.
        // Never write directly to stdout or add it to model context.
        ctx.ui.notify("Slim review URL (private): " + url, "info");
        redraw();
      });
      active = handle;
      ctx.ui.setStatus("diff-review-slim", "Reviewing in browser…");
      try {
        const result = ctx.mode === "tui"
          ? await ctx.ui.custom<ReviewResult>((tui, theme, keys, done) => {
              redraw = () => tui.requestRender();
              void handle.result.then(done);
              return {
                render(width: number) {
                  return [theme.fg("accent", "Waiting for slim review"),
                    "Use the browser to add comments and finish, or press Esc to cancel.",
                    handle.url || "Starting the .NET reviewer…"]
                    .flatMap(line => wrapTextWithAnsi(line, width));
                },
                handleInput(data: string) {
                  if (keys.matches(data, "tui.select.cancel")) handle.stop();
                },
                invalidate() {},
                dispose() { handle.stop(); },
              };
            })
          : await handle.result;
        if (active !== handle) return; // session shutdown/reload invalidated this context
        if (result.code === 0 && !result.cancelled && result.output.trim()) {
          const existing = ctx.ui.getEditorText();
          ctx.ui.setEditorText(existing.trim() ? existing.trimEnd() + "\n\n" + result.output : result.output);
          ctx.ui.notify("Review feedback inserted as a draft. Edit it and press Enter only when ready.", "info");
        } else {
          ctx.ui.notify(result.diagnostic || "Review cancelled; editor unchanged.",
            result.diagnostic.startsWith("Review failed:") || !result.cancelled && result.diagnostic.startsWith("Could not") ? "error" : "info");
        }
      } catch {
        handle.stop();
        await handle.result;
        if (active === handle) ctx.ui.notify("Slim review failed; editor unchanged.", "error");
      } finally {
        redraw = () => {};
        if (active === handle) { active = undefined; ctx.ui.setStatus("diff-review-slim", undefined); }
      }
    },
  });

  pi.registerCommand("diff-review-slim-cancel", {
    description: "Cancel this session's active slim review without inserting or sending feedback",
    handler: async (_args, ctx) => {
      const handle = active;
      if (!handle) { ctx.ui.notify("No slim review is open.", "info"); return; }
      handle.stop();
      await handle.result;
    },
  });

  pi.on("session_shutdown", async () => {
    const handle = active;
    active = undefined;
    handle?.stop();
    await handle?.result;
  });
}
