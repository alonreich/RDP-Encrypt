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

    public static void InstallWithProgress(Action<string> log)
    {
        string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrEmpty(currentExe)) throw new Exception("Could not resolve current executable path.");

        // 1. Create Directory
        log($"Ensuring installation directory exists: {InstallDir}");
        if (!Directory.Exists(InstallDir)) Directory.CreateDirectory(InstallDir);
        System.Threading.Thread.Sleep(500); // Artificial delay so user can read

        // 2. Copy Executable
        log("Checking for running instances...");
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledExe)))
        {
            try
            {
                if (p.MainModule != null && p.MainModule.FileName.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase))
                {
                    log($"Killing running instance (PID: {p.Id})...");
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { /* Ignore access denied for other processes */ }
        }

        log($"Copying core executable to: {InstalledExe}");
        File.Copy(currentExe, InstalledExe, true);
        System.Threading.Thread.Sleep(500);

        // 3. Register in Appwiz.cpl
        log("Writing Windows Registry uninstallation keys...");
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
        System.Threading.Thread.Sleep(500);

        // 4. Create Desktop & Start Menu Shortcuts via WScript.Shell COM object
        log("Generating WScript.Shell COM object...");
        log("Creating Desktop shortcut...");
        CreateShortcut(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RDP Vault.lnk"),
            InstalledExe, "Encrypted RDP Connection Manager");

        log("Creating Start Menu shortcut...");
        string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RDP Vault");
        if (!Directory.Exists(startMenuDir)) Directory.CreateDirectory(startMenuDir);
        CreateShortcut(
            Path.Combine(startMenuDir, "RDP Vault.lnk"),
            InstalledExe, "Encrypted RDP Connection Manager");
            
        System.Threading.Thread.Sleep(500);
        log("Validating deployment integrity...");
        if (!File.Exists(InstalledExe)) throw new Exception("Post-install validation failed. Executable missing.");
        log("Finalizing Setup...");
    }

    public static void LaunchInstalledAndExit()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = InstalledExe,
            UseShellExecute = false
        });
        Environment.Exit(0);
    }

    public static void UninstallWithProgress(Action<string> log)
    {
        try
        {
            // 1. Unregister Appwiz.cpl
            log("Removing appwiz.cpl (Programs and Features) registration...");
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\RDPVault", false);
            System.Threading.Thread.Sleep(500);

            // 2. Delete Shortcuts
            log("Deleting Desktop shortcut...");
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RDP Vault.lnk");
            if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);
            System.Threading.Thread.Sleep(500);

            log("Deleting Start Menu shortcut...");
            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RDP Vault");
            if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, true);
            System.Threading.Thread.Sleep(500);

            // 3. Initiate Self-Destruct CMD script
            log("Preparing self-destruct payload to obliterate installation directory...");
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            System.Threading.Thread.Sleep(1000);
            log("RDP Vault has been successfully uninstalled.");
            log("The application will now terminate and shred itself from the disk.");
            System.Threading.Thread.Sleep(2000);

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
        catch (Exception ex)
        {
            log($"ERROR: {ex.Message}");
        }
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
