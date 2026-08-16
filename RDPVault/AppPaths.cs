using System;
using System.IO;

namespace RDPVault;

/// <summary>
/// SINGLE SOURCE OF TRUTH for every path the app touches.
/// Issue #5: three different files used to hard-code "vault.dat" while the real
/// vault was "vault.rdpv". Nothing may hard-code a vault file name again.
/// </summary>
public static class AppPaths
{
    public const string VaultFileName = "vault.rdpv";
    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";

    /// <summary>Directory the running executable lives in.</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>The vault that belongs to the running executable (portable or installed).</summary>
    public static string VaultPath => Path.Combine(ExeDir, VaultFileName);

    /// <summary>Where an installed copy lives.</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDPVault");

    public static string InstalledExe => Path.Combine(InstallDir, "RDPVault.exe");

    /// <summary>The vault of the *installed* copy (used by the installer/uninstaller only).</summary>
    public static string InstalledVaultPath => Path.Combine(InstallDir, VaultFileName);

    /// <summary>
    /// Where the uninstaller rescues the vault to instead of deleting it (issue #1).
    /// </summary>
    public static string RescueDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RDP Vault Backups");

    public static bool VaultExists(string path) => File.Exists(path);
}
