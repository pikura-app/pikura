namespace Pikura.Core.Services;

/// <summary>
/// Generates the detached "remove all Pikura data" script: waits for the app
/// process to exit, then deletes the data directories — but never the user's
/// download root (downloaded artwork is user data we keep).
/// </summary>
public static class DataCleanupScript
{
    /// <summary>All data dirs the app owns, per OS. Never includes the download root.</summary>
    public static IReadOnlyList<string> GetDataDirs()
    {
        var dirs = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pikura"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pikura"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(home, "Library", "Application Support", "Pikura"));
            dirs.Add(Path.Combine(home, "Library", "Caches", "Pikura"));
            dirs.Add(Path.Combine(home, ".config", "Pikura"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(home, ".config", "Pikura"));
            dirs.Add(Path.Combine(home, ".local", "share", "Pikura"));
            dirs.Add(Path.Combine(home, ".cache", "Pikura"));
        }
        return dirs;
    }

    /// <summary>
    /// Writes the cleanup script (bat on Windows, sh otherwise) that waits for
    /// <paramref name="pid"/> to exit and removes every dir in <paramref name="dirs"/>.
    /// Returns the script path.
    /// </summary>
    public static string WriteScript(int pid, IReadOnlyList<string> dirs)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(Path.GetTempPath(), "pikura_cleanup.bat");
            var lines = new List<string>
            {
                "@echo off",
                "timeout /t 2 /nobreak >nul",
                ":waitloop",
                $"tasklist /fi \"pid eq {pid}\" 2>nul | find \"{pid}\" >nul",
                "if not errorlevel 1 (",
                "    timeout /t 1 /nobreak >nul",
                "    goto waitloop",
                ")",
            };
            foreach (var d in dirs) lines.Add($"if exist \"{d}\" rmdir /s /q \"{d}\"");
            lines.Add("del \"%~f0\"");
            File.WriteAllText(script, string.Join(Environment.NewLine, lines));
            return script;
        }
        else
        {
            var script = Path.Combine(Path.GetTempPath(), "pikura_cleanup.sh");
            var lines = new List<string> { "#!/bin/sh", $"while kill -0 {pid} 2>/dev/null; do sleep 1; done" };
            foreach (var d in dirs) lines.Add($"rm -rf \"{d}\"");
            lines.Add("rm -f \"$0\"");
            File.WriteAllText(script, string.Join('\n', lines) + '\n');
            return script;
        }
    }

    /// <summary>Convenience: write the script for this process and the real data dirs.</summary>
    public static string WriteForCurrentProcess() =>
        WriteScript(Environment.ProcessId, GetDataDirs());
}
