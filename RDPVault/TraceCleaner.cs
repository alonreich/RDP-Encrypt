using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RDPVault;

/// <summary>
/// Removes the local traces mstsc.exe leaves behind.
///
/// ISSUE #20 - SCOPING.
/// The old cleaner ran unconditionally on every unlock, lock and exit and deleted
/// the machine's ENTIRE Terminal Server Client history, every *.rdp* shortcut in
/// Recent, and the mstsc UserAssist/prefetch records - including connections the
/// user made outside this app, from their own saved .rdp files. That is silent,
/// irreversible collateral damage.
///
/// The cleaner now defaults to SweepScope.OwnHostsOnly: it only removes entries it
/// can positively tie to a host stored in this vault, plus its own temp launchers.
/// SweepScope.Everything restores the original scorched-earth behaviour for users
/// who want it, and the Settings screen spells out what that means.
///
/// Every step is wrapped so cleanup can never crash or block the app.
/// </summary>
public static class TraceCleaner
{
    private static readonly object Gate = new();
    private static SweepScope _scope = SweepScope.OwnHostsOnly;
    private static string[] _hosts = Array.Empty<string>();

    public static void Configure(SweepScope scope, IEnumerable<string> hosts)
    {
        lock (Gate)
        {
            _scope = scope;
            _hosts = hosts.Where(h => !string.IsNullOrWhiteSpace(h))
                          .Select(h => h.Trim())
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToArray();
        }
    }

    /// <summary>Called on lock/exit so host names are not retained in memory (issue #20).</summary>
    public static void ForgetHosts()
    {
        lock (Gate) _hosts = Array.Empty<string>();
    }

    private static (SweepScope scope, string[] hosts) Snapshot()
    {
        lock (Gate) return (_scope, _hosts);
    }

    private static bool Everything => Snapshot().scope == SweepScope.Everything;

    private static bool MentionsOurHost(string text)
    {
        var (_, hosts) = Snapshot();
        return hosts.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- public entry points ----------------

    /// <summary>Standard sweep: everything except other saved TERMSRV credentials.</summary>
    public static void Sweep()
    {
        RegistryHistory();
        DefaultRdpFile();
        JumpLists();
        RecentItems();
        TempLaunchers();
        if (Everything) { UserAssist(); Prefetch(); }
    }

    /// <summary>Sweep + delete every saved RDP credential on this PC.</summary>
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

            if (Everything)
            {
                foreach (var name in key.GetValueNames())
                    if (name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase))
                        key.DeleteValue(name, throwOnMissingValue: false);

                key.DeleteSubKeyTree("Servers", throwOnMissingSubKey: false);
                key.DeleteSubKeyTree("Default", throwOnMissingSubKey: false);
                return;
            }

            // Scoped: legacy MRU values are keyed by name but hold the host as data.
            foreach (var name in key.GetValueNames())
            {
                if (!name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase)) continue;
                if (MentionsOurHost(key.GetValue(name)?.ToString() ?? ""))
                    key.DeleteValue(name, throwOnMissingValue: false);
            }

            // Modern mstsc: address-box history lives under "Default" as MRU0..MRUn.
            using (var def = key.OpenSubKey("Default", writable: true))
            {
                if (def != null)
                    foreach (var name in def.GetValueNames())
                        if (MentionsOurHost(def.GetValue(name)?.ToString() ?? ""))
                            def.DeleteValue(name, throwOnMissingValue: false);
            }

            // Modern mstsc: one subkey per host, holding UsernameHint etc.
            using (var servers = key.OpenSubKey("Servers", writable: true))
            {
                if (servers != null)
                    foreach (string sub in servers.GetSubKeyNames())
                        if (MentionsOurHost(sub))
                            servers.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            }
        });
    }

    // ---------------- files ----------------

    private static void DefaultRdpFile()
    {
        TryRun(() =>
        {
            foreach (string path in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Default.rdp"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Default.rdp")
                     })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    // Scoped: Default.rdp records the LAST host used. Only remove it
                    // when that host is one of ours.
                    if (!Everything && !MentionsOurHost(File.ReadAllText(path))) continue;
                    File.Delete(path);
                }
                catch { }
            }
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
                    if (IndexOf(data, probe) < 0) continue;

                    if (!Everything)
                    {
                        string asText = Encoding.Unicode.GetString(data);
                        if (!MentionsOurHost(asText)) continue;
                    }
                    File.Delete(file);
                }
                catch { /* locked by Explorer - skipped this round */ }
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
                try
                {
                    string name = Path.GetFileName(file);
                    bool ours = name.StartsWith("rdpv_", StringComparison.OrdinalIgnoreCase) || MentionsOurHost(name);
                    if (!Everything && !ours) continue;
                    File.Delete(file);
                }
                catch { }
            }
        });
    }

    /// <summary>Our own temporary launcher files (%TEMP%\rdpv_*.rdp). Always removed.</summary>
    public static void TempLaunchers()
    {
        TryRun(() =>
        {
            foreach (string file in Directory.GetFiles(Path.GetTempPath(), "rdpv_*.rdp"))
            {
                try { File.Delete(file); } catch { }
            }
        });
    }

    // ---------------- UserAssist / prefetch (SweepScope.Everything only) ----------------
    // These record only THAT mstsc ran, never which host was contacted, and they are
    // shared with the user's non-vault usage. Scoped mode deliberately leaves them.

    private static void UserAssist()
    {
        TryRun(() =>
        {
            using var ua = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\UserAssist", writable: true);
            if (ua == null) return;

            foreach (string guid in ua.GetSubKeyNames())
            {
                using var count = ua.OpenSubKey(guid + @"\Count", writable: true);
                if (count == null) continue;
                foreach (string value in count.GetValueNames())
                    if (Rot13(value).Contains("mstsc", StringComparison.OrdinalIgnoreCase))
                        count.DeleteValue(value, throwOnMissingValue: false);
            }
        });
    }

    private static void Prefetch()
    {
        TryRun(() =>
        {
            string prefetch = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            if (!Directory.Exists(prefetch)) return;
            foreach (string file in Directory.GetFiles(prefetch, "MSTSC.EXE-*.pf"))
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
                    if (target == null) continue;
                    if (!target.StartsWith("TERMSRV/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Everything && !MentionsOurHost(target)) continue;
                    _ = CredDeleteW(target, c.Type, 0);
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
