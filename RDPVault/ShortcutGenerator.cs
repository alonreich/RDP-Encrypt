using System;
using System.IO;

namespace RDPVault;

public readonly struct ShortcutResult
{
    public bool Ok { get; init; }
    public string Path { get; init; }
    public string Message { get; init; }
}

/// <summary>
/// ISSUE #6.
///
/// What was broken: the app wrote a plain text file "&lt;Name&gt;.rdpvlink" onto the
/// desktop containing "TargetProfileId=&lt;guid&gt;". Nothing ever registered the
/// .rdpvlink extension with Windows and nothing ever passed --launch, so double
/// clicking it produced the "How do you want to open this file?" dialog. The whole
/// one-click-connect feature never worked. The file also sat on the desktop in
/// cleartext advertising both the app and the profile name, which works against
/// the app's own no-trace promise.
///
/// What it does now: writes a real Windows shortcut (.lnk) whose target is
/// RDPVault.exe with `--launch &lt;profileId&gt;`. That needs no file association at
/// all, works the moment it is created, and contains no host name - only an opaque
/// GUID. InstallerService additionally registers the legacy .rdpvlink extension so
/// shortcuts made by v1.0.0 finally start working too.
/// </summary>
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
