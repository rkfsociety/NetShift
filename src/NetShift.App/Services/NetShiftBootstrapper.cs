using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NetShift.App.Services;

namespace NetShift.App;

internal static class NetShiftBootstrapper
{
    public static bool TryStartInstalledCopy(string[] args)
    {
        if (args.Length > 0 || NetShiftUpdateService.Repository is null || !HasEmbeddedDnsService())
            return false;

        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(source) || !Path.GetFileName(source).Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NetShift");
        var installedExecutable = Path.Combine(installDirectory, "NetShift.exe");
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(installedExecutable), StringComparison.OrdinalIgnoreCase))
        {
            EnsureShortcuts(installedExecutable, installDirectory);
            MigrateAppAutoStart(installedExecutable);
            return false;
        }

        Directory.CreateDirectory(installDirectory);
        var stagingPath = installedExecutable + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, stagingPath, overwrite: true);
            File.Move(stagingPath, installedExecutable, overwrite: true);
        }
        catch (IOException) when (File.Exists(installedExecutable))
        {
            // An already-running installation can keep its image mapped. Continue with that copy.
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }

        EnsureShortcuts(installedExecutable, installDirectory);
        MigrateAppAutoStart(installedExecutable);
        Process.Start(new ProcessStartInfo(installedExecutable) { WorkingDirectory = installDirectory, UseShellExecute = true });
        return true;
    }

    public static bool HasEmbeddedDnsService() =>
        typeof(NetShiftBootstrapper).Assembly.GetManifestResourceInfo("NetShift.App.Payload.NetShift.DnsService.exe") is not null;

    private static void MigrateAppAutoStart(string installedExecutable)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey?.GetValue("NetShift") is not string command || string.IsNullOrWhiteSpace(command)) return;
            var configuredPath = command.Trim().Trim('"');
            if (Path.GetFileName(configuredPath).Equals("NetShift.exe", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFullPath(configuredPath).Equals(Path.GetFullPath(installedExecutable), StringComparison.OrdinalIgnoreCase))
                runKey.SetValue("NetShift", $"\"{installedExecutable}\"", RegistryValueKind.String);
        }
        catch
        {
            // Installing the app and its shortcuts does not depend on the optional user startup setting.
        }
    }

    private static void EnsureShortcuts(string executable, string workingDirectory)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Exception? desktopError = null;
        try { EnsureShortcut(desktop, "NetShift.lnk", executable, workingDirectory); }
        catch (Exception error) { desktopError = error; }
        try { EnsureShortcut(Path.Combine(programs, "NetShift"), "NetShift.lnk", executable, workingDirectory); }
        catch { }

        if (desktopError is not null)
            NetShift.App.ThemedDialog.Show(null,
                $"Приложение установлено, но ярлык на рабочем столе создать не удалось: {desktopError.Message}",
                "NetShift", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    private static void EnsureShortcut(string directory, string fileName, string executable, string workingDirectory)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            Directory.CreateDirectory(directory);
            var shortcutPath = Path.Combine(directory, fileName);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) throw new InvalidOperationException("Windows не предоставил компонент создания ярлыков.");
            shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Не удалось создать Windows Script Host.");
            dynamic shellObject = shell;
            shortcut = shellObject.CreateShortcut(shortcutPath);
            dynamic shortcutObject = shortcut;
            shortcutObject.TargetPath = executable;
            shortcutObject.WorkingDirectory = workingDirectory;
            shortcutObject.Description = "Управление DNS и zapret";
            shortcutObject.IconLocation = executable;
            shortcutObject.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
