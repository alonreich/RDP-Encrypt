using System;
using System.IO;

namespace RDPVault;

public readonly struct ShortcutResult
{
    public bool Ok { get; init; }
    public string Path { get; init; }
    public string Message { get; init; }
}

public static class ShortcutGenerator
{
    public static ShortcutResult CreateDesktopShortcut(RdpProfile profile, bool overwrite)
    {
        string exe = Environment.ProcessPath ?? AppPaths.InstalledExe;
        if (!File.Exists(exe))
            return new ShortcutResult { Ok = false, Message = "Could not locate RDPVault.exe to point the shortcut at." };

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string baseName = SafeFileName(string.IsNullOrWhiteSpace(profile.Name) ? "RDP Connection" : profile.Name);
        string path = Path.Combine(desktop, baseName + ".lnk");

        if (File.Exists(path) && !overwrite)
            return new ShortcutResult { Ok = false, Path = path, Message = "exists" };

        try
        {
            ShellLink.Create(
                shortcutPath: path,
                targetPath: exe,
                arguments: $"--launch {profile.Id}",
                description: "Open this Remote Desktop connection via RDP Vault",
                iconPath: exe);
            return new ShortcutResult { Ok = true, Path = path, Message = $"Shortcut created on your desktop: {baseName}.lnk" };
        }
        catch (Exception ex)
        {
            return new ShortcutResult { Ok = false, Path = path, Message = $"Could not create the shortcut: {ex.Message}" };
        }
    }

    private static string SafeFileName(string name)
    {
        string cleaned = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (cleaned.Length == 0) cleaned = "RDP Connection";
        return cleaned.Length > 80 ? cleaned.Substring(0, 80) : cleaned;
    }
}
