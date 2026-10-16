using System;
using System.IO;

namespace RDPVault;

public static class AppPaths
{
    public const string VaultFileName = "vault.rdpv";
    public const string BackupSuffix = ".bak";
    public const string TempSuffix = ".tmp";
    public const string BeforeRestoreSuffix = ".before-restore";

    public static string ExeDir => AppContext.BaseDirectory;

    public static string VaultPath => Path.Combine(ExeDir, VaultFileName);

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDPVault");

    public static string InstalledExe => Path.Combine(InstallDir, "RDPVault.exe");

    public static string InstalledVaultPath => Path.Combine(InstallDir, VaultFileName);

    public static string PendingCertsPath => Path.Combine(InstallDir, "pending_certs.dat");

    public static string RescueDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RDP Vault Backups");

    public static bool VaultExists(string path) => File.Exists(path);
}
