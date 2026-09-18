using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
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

    /// <summary>
    /// Checks whether the current process is running with elevated administrator privileges.
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Automatically requests elevation via the Windows UAC prompt without requiring the user
    /// to manually right-click 'Run as administrator'. If elevation is approved, spawns the elevated
    /// process and terminates the current process cleanly.
    /// </summary>
    public static bool TryElevate(string[]? args = null)
    {
        if (IsAdministrator()) return false;

        args ??= Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Any(a => string.Equals(a, "--no-elevate", StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(currentExe))
                return false;

            var argList = args.Where(a => !string.Equals(a, "--no-elevate", StringComparison.OrdinalIgnoreCase)).ToList();
            if (!argList.Any(a => string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase)))
                argList.Add("--elevated");

            string arguments = string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            var psi = new ProcessStartInfo
            {
                FileName = currentExe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                Environment.Exit(0);
                return true;
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled / declined the UAC prompt (ERROR_CANCELLED).
            // Return false to gracefully continue in user-level mode without crashing.
            return false;
        }
        catch
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Fast, non-blocking termination of running RDPVault instances to release file locks and mutexes.
    /// Exits in milliseconds if no other instances are active. If an elevated process cannot be killed
    /// due to lack of privileges, triggers UAC privilege escalation.
    /// </summary>
    public static void KillRunningInstances(bool excludeCurrent = true, Action<string>? log = null)
    {
        int currentPid = Environment.ProcessId;
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("RDPVault");
        }
        catch
        {
            return;
        }

        var targets = processes.Where(p => !excludeCurrent || p.Id != currentPid).ToList();
        if (targets.Count == 0) return;

        bool needTaskKillFallback = false;
        bool accessDenied = false;

        foreach (var p in targets)
        {
            try
            {
                log?.Invoke($"Closing running copy (PID {p.Id})...");
                p.Kill(entireProcessTree: true);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                accessDenied = true;
                needTaskKillFallback = true;
            }
            catch
            {
                needTaskKillFallback = true;
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }

        if (accessDenied && !IsAdministrator())
        {
            if (TryElevate()) return;
        }

        if (needTaskKillFallback)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/F /FI \"PID ne {currentPid}\" /IM RDPVault.exe /T",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                proc?.WaitForExit(1000);
            }
            catch { }
        }

        // Fast poll: wait up to 400ms in 25ms intervals, exiting immediately when all PIDs are gone
        for (int i = 0; i < 16; i++)
        {
            try
            {
                var remaining = Process.GetProcessesByName("RDPVault")
                    .Where(p => !excludeCurrent || p.Id != currentPid).ToArray();
                bool anyLeft = remaining.Length > 0;
                foreach (var r in remaining) { try { r.Dispose(); } catch { } }
                if (!anyLeft) break;
            }
            catch { break; }
            Thread.Sleep(25);
        }
    }

    // ================================================================ install

    public static void InstallWithProgress(Action<string> log)
    {
        string currentExe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(currentExe)) throw new Exception("Could not resolve the current executable path.");

        log($"Creating installation directory: {InstallDir}");
        Directory.CreateDirectory(InstallDir);

        log("Checking for running copies and releasing process locks...");
        KillRunningInstances(excludeCurrent: true, log);

        if (!currentExe.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase))
        {
            log($"Copying program files to: {InstalledExe}");
            const int maxRetries = 5;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    File.Copy(currentExe, InstalledExe, true);
                    break;
                }
                catch (UnauthorizedAccessException) when (!IsAdministrator())
                {
                    log("Elevation required to overwrite existing installation. Requesting UAC...");
                    if (TryElevate(new[] { "--setup" }))
                        return;
                    throw;
                }
                catch (IOException ex)
                {
                    if (attempt == maxRetries)
                        throw new Exception($"Failed to copy executable to {InstalledExe} after {maxRetries} attempts: {ex.Message}", ex);

                    log($"File is locked, retrying copy ({attempt}/{maxRetries})...");
                    KillRunningInstances(excludeCurrent: true, log);
                    Thread.Sleep(200 * attempt);
                }
            }
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
            log("Checking for running copies and releasing process locks...");
            KillRunningInstances(excludeCurrent: true, log);

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
            bool isInstalledExe = IsInstalledLocation();
            if (isInstalledExe)
            {
                // A running installed exe cannot delete itself; hand the last step to cmd.exe.
                // The vault has already been rescued or deliberately shredded above.
                string cmd = $"/C choice /C Y /N /D Y /T 2 & Del /F /Q \"{InstalledExe}\" & rmdir /S /Q \"{InstallDir}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmd,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                });
            }
            else
            {
                // Running from portable or external location: only delete installed files, NEVER current portable exe!
                try { if (File.Exists(InstalledExe)) File.Delete(InstalledExe); } catch { }
                try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); } catch { }
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
