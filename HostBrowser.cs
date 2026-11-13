namespace AgentDiffReview;

internal static class HostBrowser
{
    internal static bool IsWsl =>
        Environment.GetEnvironmentVariable("WSL_INTEROP") is not null ||
        Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
        File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop");

    internal static string? WslExecutable(string browser)
    {
        // Find the Windows drive through Windows/System32/cmd.exe on WSL's PATH.
        // Walking up from System32 also supports custom mounts such as /c.
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(Path.Combine(directory, "cmd.exe"))) continue;
            string root = Path.GetFullPath(Path.Combine(directory, "..", ".."));
            string application = browser == "edge"
                ? "Microsoft/Edge/Application/msedge.exe"
                : "Google/Chrome/Application/chrome.exe";
            foreach (string programFiles in new[] { "Program Files", "Program Files (x86)" })
            {
                string executable = Path.Combine(root, programFiles, application);
                if (File.Exists(executable)) return executable;
            }
        }
        return null;
    }
}
