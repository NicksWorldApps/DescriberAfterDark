using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ImageDescriber.Core;

namespace ImageDescriber.App;

/// <summary>
/// Installs a downloaded update. The running program can't replace its own files, so this unpacks the
/// new version to a temporary folder and starts a small helper script that waits for the program to
/// close, copies the new files over the old ones, and starts the program again.
/// Settings in %APPDATA% are untouched, so they carry over.
/// </summary>
internal static class UpdateInstaller
{
    public const string UpdatedArgument = "--updated";
    public const string UpdateFailedArgument = "--update-failed";

    public static string WorkFolder => Path.Combine(Path.GetTempPath(), "DescriberAfterDarkUpdate");

    /// <summary>Removes leftovers from a previous update. Called at start-up.</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(WorkFolder)) Directory.Delete(WorkFolder, recursive: true);
        }
        catch
        {
            // Still in use or locked; it'll be cleaned up next time.
        }
    }

    /// <summary>
    /// Downloads and unpacks the update, then starts the helper that swaps the files.
    /// The caller must exit the program as soon as this returns.
    /// </summary>
    public static async Task PrepareAndLaunchAsync(UpdateChecker checker, UpdateInfo update, IProgress<int> progress, CancellationToken ct)
    {
        CleanUp();
        Directory.CreateDirectory(WorkFolder);
        var zipPath = Path.Combine(WorkFolder, "update.zip");
        var unpacked = Path.Combine(WorkFolder, "files");

        await checker.DownloadAsync(update, zipPath, progress, ct);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, unpacked, overwriteFiles: true), ct);

        // The program might be at the top of the zip or inside one folder.
        var newExe = Directory.GetFiles(unpacked, "DescriberAfterDark.exe", SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new InvalidOperationException("The downloaded update doesn't contain DescriberAfterDark.exe.");
        var source = Path.GetDirectoryName(newExe)!;

        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DescriberAfterDark.exe");
        var appFolder = Path.GetDirectoryName(exePath)!;

        var script = Path.Combine(WorkFolder, "install-update.cmd");
        File.WriteAllText(script, BuildScript(Environment.ProcessId, source, appFolder, exePath), Encoding.ASCII);

        var start = new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        if (CanWriteTo(appFolder))
        {
            start.UseShellExecute = false;
        }
        else
        {
            // Installed somewhere protected, like Program Files: ask Windows for permission to copy.
            start.UseShellExecute = true;
            start.Verb = "runas";
        }
        Process.Start(start);
    }

    private static string BuildScript(int pid, string source, string appFolder, string exePath)
    {
        // Robocopy treats a trailing backslash before a quote as an escape, so strip it.
        source = source.TrimEnd('\\');
        appFolder = appFolder.TrimEnd('\\');
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("rem Describer After Dark update helper: waits for the program to close, copies the new version in, restarts it.");
        sb.AppendLine("set /a tries=0");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
        sb.AppendLine("if errorlevel 1 goto copy");
        sb.AppendLine("set /a tries+=1");
        sb.AppendLine("if %tries% geq 60 goto failed");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("goto wait");
        sb.AppendLine(":copy");
        sb.AppendLine($"robocopy \"{source}\" \"{appFolder}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul");
        sb.AppendLine("if errorlevel 8 goto failed");
        sb.AppendLine($"start \"\" \"{exePath}\" {UpdatedArgument}");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":failed");
        sb.AppendLine($"start \"\" \"{exePath}\" {UpdateFailedArgument}");
        sb.AppendLine("exit /b 1");
        return sb.ToString();
    }

    private static bool CanWriteTo(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
