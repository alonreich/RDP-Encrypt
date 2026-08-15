using System;
using System.IO;

namespace RDPVault
{
    public static class ShortcutGenerator
    {
        public static void CreateDesktopShortcut(RdpProfile profile)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string safeName = string.Join("_", profile.Name.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(desktop, $"{safeName}.rdpvlink");
            
            File.WriteAllText(path, $"TargetProfileId={profile.Id}");
        }
    }
}
