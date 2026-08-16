using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RDPVault;

public static class InstallerService
{
    public static string InstallDir => AppPaths.InstallDir;
    public static string InstalledExe => AppPaths.InstalledExe;

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RDPVault";
    private const string ProgId = "RDPVault.Link";
    private const string LegacyExt = ".rdpvlink";

    public static bool IsInstalledLocation()
    {
        string currentExe = Environment.ProcessPath ?? "";
        return !string.IsNullOrEmpty(currentExe) &&
               currentExe.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ install

    public static void InstallWithProgress(Action<string> log)
    {
        string currentExe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(currentExe)) throw new Exception("Could not resolve the current executable path.");

        log($"Creating installation directory: {InstallDir}");
        Directory.CreateDirectory(InstallDir);

        log("Checking for a running copy...");
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledExe)))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                if (p.MainModule != null &&
                    p.MainModule.FileName.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase))
                {
                    log($"Closing running copy (PID {p.Id})...");
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch { /* another user's process - not ours to touch */ }
        }

        if (!currentExe.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            log($"Copying program files to: {InstalledExe}");
            File.Copy(currentExe, InstalledExe, true);
        }

        log("Registering the uninstaller with Programs and Features...");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
        {
            key.SetValue("DisplayName", "RDP Vault (Encrypted Connection Manager)");
            key.SetValue("DisplayIcon", InstalledExe);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            key.SetValue("DisplayVersion", "1.1.0.0");
            key.SetValue("Publisher", "RDPVault Open Source");
            key.SetValue("EstimatedSize", new FileInfo(InstalledExe).Length / 1024, RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        // Issue #6: make legacy .rdpvlink files from v1.0.0 actually open.
        log("Registering the .rdpvlink file type...");
        try { RegisterFileAssociation(InstalledExe); }
        catch (Exception ex) { log($"  (skipped: {ex.Message})"); }

        // Issue #16: real IShellLink shortcuts, and failures are reported now.
        log("Creating the Desktop shortcut...");
        TryShortcut(log, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RDP Vault.lnk"));

        log("Creating the Start Menu shortcut...");
        string startMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RDP Vault");
        Directory.CreateDirectory(startMenuDir);
        TryShortcut(log, Path.Combine(startMenuDir, "RDP Vault.lnk"));

        log("Verifying the installation...");
        if (!File.Exists(InstalledExe)) throw new Exception("Post-install check failed: the executable is missing.");
        log("Done.");
    }

    private static void TryShortcut(Action<string> log, string path)
    {
        try
        {
            ShellLink.Create(path, InstalledExe, "", "Encrypted RDP Connection Manager", InstalledExe);
        }
        catch (Exception ex)
        {
            log($"  WARNING: could not create {Path.GetFileName(path)} - {ex.Message}");
        }
    }

    private static void RegisterFileAssociation(string exePath)
    {
        using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{LegacyExt}"))
            ext.SetValue("", ProgId);

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "RDP Vault Connection");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{exePath}\",0");
            using (var cmd = progId.CreateSubKey(@"shell\open\command"))
                cmd.SetValue("", $"\"{exePath}\" --launch \"%1\"");
        }
    }

    private static void UnregisterFileAssociation()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{LegacyExt}", false); } catch { }
    }

    public static void LaunchInstalledAndExit()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = InstalledExe, UseShellExecute = false });
        }
        catch { }
        Environment.Exit(0);
    }

    // ================================================================ uninstall

    /// <summary>Is there a vault inside the install directory that would be destroyed?</summary>
    public static bool InstalledVaultExists() => File.Exists(AppPaths.InstalledVaultPath);

    /// <summary>
    /// ISSUE #1 - THE WORST BUG IN THE PROJECT.
    ///
    /// The old uninstaller ended with:
    ///     rmdir /S /Q "%LOCALAPPDATA%\RDPVault"
    /// and the vault lives in exactly that directory. Choosing "Uninstall" in
    /// Programs and Features - or the QuietUninstallString, which runs with no UI
    /// whatsoever - permanently deleted every stored credential with no warning, no
    /// confirmation, no backup and (see issue #2) no way to recover.
    ///
    /// Now: the vault is copied out to Documents\RDP Vault Backups BEFORE anything
    /// is removed, unless the user explicitly asks for it to be destroyed. The
    /// rescued path is returned so the UI can show it.
    /// </summary>
    public static string? UninstallWithProgress(Action<string> log, bool keepVault)
    {
        string? rescuedTo = null;
        try
        {
            if (keepVault && InstalledVaultExists())
            {
                log("Rescuing your vault before removing the program...");
                rescuedTo = RescueVault();
                log($"  Your vault has been copied to: {rescuedTo}");
                log("  Keep this file. It still needs your master password or Recovery Code.");
            }
            else if (!keepVault && InstalledVaultExists())
            {
                log("Securely erasing the vault at your request...");
                ShredFile(AppPaths.InstalledVaultPath);
                ShredFile(AppPaths.InstalledVaultPath + AppPaths.BackupSuffix);
                ShredFile(AppPaths.InstalledVaultPath + AppPaths.TempSuffix);
            }

            log("Removing the Programs and Features entry...");
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false); } catch { }

            log("Removing the .rdpvlink file type...");
            UnregisterFileAssociation();

            log("Removing shortcuts...");
            string desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RDP Vault.lnk");
            try { if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut); } catch { }

            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RDP Vault");
            try { if (Directory.Exists(startMenuDir)) Directory.Delete(startMenuDir, true); } catch { }

            log("Removing program files...");
            string currentExe = Environment.ProcessPath ?? "";
            if (File.Exists(currentExe))
            {
                // A running exe cannot delete itself; hand the last step to cmd.exe.
                // The vault has already been rescued or deliberately shredded above.
                string cmd = $"/C choice /C Y /N /D Y /T 2 & Del /F /Q \"{currentExe}\" & rmdir /S /Q \"{InstallDir}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmd,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                });
            }

            log("RDP Vault has been uninstalled.");
            return rescuedTo;
        }
        catch (Exception ex)
        {
            log($"ERROR: {ex.Message}");
            return rescuedTo;
        }
    }

    private static string RescueVault()
    {
        Directory.CreateDirectory(AppPaths.RescueDir);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string dest = Path.Combine(AppPaths.RescueDir, $"vault-{stamp}{Path.GetExtension(AppPaths.VaultFileName)}");
        File.Copy(AppPaths.InstalledVaultPath, dest, overwrite: false);

        string bak = AppPaths.InstalledVaultPath + AppPaths.BackupSuffix;
        if (File.Exists(bak))
        {
            try { File.Copy(bak, dest + AppPaths.BackupSuffix, overwrite: false); } catch { }
        }
        return dest;
    }

    private static void ShredFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            long len = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] noise = System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                    (int)Math.Min(Math.Max(len, 1), 1 << 20));
                long written = 0;
                while (written < len)
                {
                    int chunk = (int)Math.Min(noise.Length, len - written);
                    fs.Write(noise, 0, chunk);
                    written += chunk;
                }
                fs.Flush(true);
            }
            File.Delete(path);
        }
        catch { }
    }
}
