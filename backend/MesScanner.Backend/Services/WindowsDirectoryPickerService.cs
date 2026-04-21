using System.Diagnostics;

namespace MesScanner.Backend.Services;

public class WindowsDirectoryPickerService
{
    public string? PickDirectory(string title)
    {
        var safeTitle = (title ?? "选择目录").Replace("'", "''");
        var psScript =
            "Add-Type -AssemblyName System.Windows.Forms; " +
            "Add-Type -AssemblyName System.Drawing; " +
            $"$dlg = New-Object System.Windows.Forms.FolderBrowserDialog; $dlg.Description = '{safeTitle}'; $dlg.ShowNewFolderButton = $true; " +
            "$owner = New-Object System.Windows.Forms.Form; " +
            "$owner.TopMost = $true; $owner.ShowInTaskbar = $false; $owner.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual; " +
            "$owner.Location = New-Object System.Drawing.Point(-32000,-32000); $owner.Size = New-Object System.Drawing.Size(1,1); " +
            "$owner.Show(); $owner.Activate(); " +
            "try { if ($dlg.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dlg.SelectedPath } } finally { $owner.Close(); $owner.Dispose(); $dlg.Dispose(); }";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -STA -Command \"{psScript}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit(30_000);

        if (process.ExitCode != 0) return null;
        if (!string.IsNullOrWhiteSpace(error)) return null;
        if (string.IsNullOrWhiteSpace(output)) return null;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? null : lines[^1].Trim();
    }
}
