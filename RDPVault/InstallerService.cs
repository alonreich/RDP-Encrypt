using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RDPVault;

public static class InstallerService
{
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDPVault");
    public static string InstalledExe => Path.Combine(InstallDir, "RDPVault.exe");
    
    public static bool IsInstalledLocation()
    {
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        return currentExe.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Install()
    {
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(currentExe)) return;

        // 1. Create Directory
        if (!Directory.Exists(InstallDir)) Directory.CreateDirectory(InstallDir);

        // 2. Copy Executable
        File.Copy(currentExe, InstalledExe, true);

        // 3. Register in Appwiz.cpl
        string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RDPVault";
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(uninstallKey))
        {
            key.SetValue("DisplayName", "RDP Vault (Encrypted Connection Manager)");
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            key.SetValue("DisplayVersion", "1.0.0.0");
            key.SetValue("Publisher", "RDPVault Open Source");
            key.SetValue("EstimatedSize", (new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        // 4. Create Desktop & Start Menu Shortcuts via WScript.Shell COM object
        CreateShortcut(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RDP Vault.lnk"),
            InstalledExe, "Encrypted RDP Connection Manager");

        string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RDP Vault");
        if (!Directory.Exists(startMenuDir)) Directory.CreateDirectory(startMenuDir);
        CreateShortcut(
            Path.Combine(startMenuDir, "RDP Vault.lnk"),
            InstalledExe, "Encrypted RDP Connection Manager");

        // 5. Launch the installed executable and exit current
        Process.Start(new ProcessStartInfo
        {
            FileName = InstalledExe,
            UseShellExecute = false
        });
        Environment.Exit(0);
    }

    public static void Uninstall()
    {
        try
        {
            // 1. Unregister Appwiz.cpl
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\RDPVault", false);

            // 2. Delete Shortcuts
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RDP Vault.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);

            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RDP Vault");
            if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, true);

            // 3. Initiate Self-Destruct CMD script (since we cannot delete the running exe)
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (File.Exists(currentExe))
            {
                string cmd = $"/C choice /C Y /N /D Y /T 2 & Del /F /Q \"{currentExe}\" & rmdir /S /Q \"{InstallDir}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmd,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                });
            }
        }
        catch { }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
            shortcut.Description = description;
            shortcut.Save();
        }
        catch { }
    }
}
