using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RDPVault;

/// <summary>
/// Removes every local trace mstsc.exe leaves behind:
/// - Registry "Terminal Server Client" history (servers, usernames, MRU)
/// - Documents\Default.rdp
/// - Taskbar / Start jump list entries mentioning mstsc
/// - Recent items (*.rdp, *.rdp.lnk)
/// - Explorer UserAssist execution counters (ROT13-encoded "mstsc.exe")
/// - Prefetch files (if permission allows)
/// - Saved RDP credentials TERMSRV/* in Windows Credential Manager ("deep sweep")
/// </summary>
public static class TraceCleaner
{
    // ---------------- public entry points ----------------

    /// <summary>Standard sweep: everything except other TERMSRV credentials.</summary>
    public static void Sweep()
    {
        RegistryHistory();
        DefaultRdpFile();
        JumpLists();
        RecentItems();
        TempLaunchers();
        UserAssist();
        Prefetch();
    }

    /// <summary>Standard sweep + delete every saved RDP credential on this PC.</summary>
    public static void DeepSweep()
    {
        Sweep();
        DeleteSavedRdpCredentials();
    }

    // ---------------- registry history ----------------

    private static void RegistryHistory()
    {
        TryRun(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Terminal Server Client", writable: true);
            if (key == null) return;

            // Old mstsc versions: MRU0..MRU9 values directly under the key
            foreach (var name in key.GetValueNames())
                if (name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue(name, throwOnMissingValue: false);

            // Modern versions: "Servers" subkey with one child per host
            key.DeleteSubKeyTree("Servers", throwOnMissingSubKey: false);
            key.DeleteSubKeyTree("Default", throwOnMissingSubKey: false);
        });
    }

    // ---------------- files ----------------

    private static void DefaultRdpFile()
    {
        TryRun(() =>
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string path = Path.Combine(docs, "Default.rdp");
            if (File.Exists(path)) File.Delete(path);
            // Fallback for machines where MyDocuments is redirected oddly.
            string alt = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Default.rdp");
            if (File.Exists(alt)) File.Delete(alt);
        });
    }

    private static void JumpLists()
    {
        TryRun(() =>
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "Microsoft", "Windows", "Recent", "AutomaticDestinations");
            if (!Directory.Exists(dir)) return;

            byte[] probe = Encoding.Unicode.GetBytes("mstsc"); // UTF-16LE
            foreach (string file in Directory.GetFiles(dir, "*.automaticDestinations-ms"))
            {
                try
                {
                    byte[] data = File.ReadAllBytes(file);
                    if (IndexOf(data, probe) >= 0) File.Delete(file);
                }
                catch { /* file locked by Explorer - skipped this round */ }
            }
        });
    }

    private static void RecentItems()
    {
        TryRun(() =>
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "Microsoft", "Windows", "Recent");
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir, "*.rdp*"))
            {
                try { File.Delete(file); } catch { }
            }
        });
    }

    /// <summary>Our own temporary launcher files (%TEMP%\rdpv_*.rdp).</summary>
    public static void TempLaunchers()
    {
        TryRun(() =>
        {
            string dir = Path.GetTempPath();
            foreach (string file in Directory.GetFiles(dir, "rdpv_*.rdp"))
            {
                try { File.Delete(file); } catch { }
            }
        });
    }

    // ---------------- UserAssist (execution counters) ----------------

    private static void UserAssist()
    {
        TryRun(() =>
        {
            using var ua = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\UserAssist", writable: true);
            if (ua == null) return;

            foreach (string guid in ua.GetSubKeyNames())
            {
                using var count = ua.OpenSubKey(Path.Combine(guid, "Count"), writable: true);
                if (count == null) continue;
                foreach (string value in count.GetValueNames())
                {
                    if (Rot13(value).Contains("mstsc", StringComparison.OrdinalIgnoreCase))
                        count.DeleteValue(value, throwOnMissingValue: false);
                }
            }
        });
    }

    private static void Prefetch()
    {
        TryRun(() =>
        {
            foreach (string file in Directory.GetFiles(@"C:\Windows\Prefetch", "MSTSC.EXE-*.pf"))
            {
                try { File.Delete(file); } catch { } // needs admin; ignore silently
            }
        });
    }

    // ---------------- Windows Credential Manager: TERMSRV/* ----------------

    public static void DeleteSavedRdpCredentials()
    {
        TryRun(() =>
        {
            if (!CredEnumerateW(null, 0, out int count, out IntPtr pCreds)) return;
            try
            {
                IntPtr[] creds = new IntPtr[count];
                Marshal.Copy(pCreds, creds, 0, count);

                foreach (IntPtr p in creds)
                {
                    var c = Marshal.PtrToStructure<CREDENTIAL>(p);
                    string? target = Marshal.PtrToStringUni(c.TargetName);
                    if (target != null &&
                        target.StartsWith("TERMSRV/", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = CredDeleteW(target, c.Type, 0);
                    }
                }
            }
            finally { CredFree(pCreds); }
        });
    }

    // ---------------- helpers ----------------

    private static void TryRun(Action a)
    {
        try { a(); } catch { /* never let cleanup crash the app */ }
    }

    private static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (ch is >= 'a' and <= 'z') sb.Append((char)('a' + (ch - 'a' + 13) % 26));
            else if (ch is >= 'A' and <= 'Z') sb.Append((char)('A' + (ch - 'A' + 13) % 26));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerateW(string? filter, int flags, out int count, out IntPtr pCredentials);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int reserved);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr cred);
}