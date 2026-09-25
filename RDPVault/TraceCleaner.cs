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
public readonly struct SweepReport
{
    public int ItemsCleaned { get; init; }
    public int ItemsLocked { get; init; }
    public bool AllClean => ItemsLocked == 0;
}

public static class TraceCleaner
{
    private static readonly object Gate = new();
    private static SweepScope _scope = SweepScope.OwnHostsOnly;
    private static string[] _hosts = Array.Empty<string>();
    private static int _itemsCleaned;
    private static int _itemsLocked;

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

    private static bool MentionsOurHost(string text) => MentionsTargetHost(text, null);

    private static bool MentionsTargetHost(string text, string[]? explicitHosts)
    {
        if (explicitHosts != null && explicitHosts.Length > 0)
            return explicitHosts.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));

        var (_, hosts) = Snapshot();
        return hosts.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- public entry points ----------------

    /// <summary>Standard sweep: everything except other saved TERMSRV credentials.</summary>
    public static SweepReport Sweep()
    {
        _itemsCleaned = 0;
        _itemsLocked = 0;
        RegistryHistory();
        DefaultRdpFile();
        JumpLists();
        RecentItems();
        TempLaunchers();
        if (Everything) { UserAssist(); Prefetch(); }
        return new SweepReport { ItemsCleaned = _itemsCleaned, ItemsLocked = _itemsLocked };
    }

    /// <summary>
    /// Issue #1: Cleans specific target hosts deterministically, even if ForgetHosts() was called when the vault locked.
    /// </summary>
    public static SweepReport SweepHosts(IEnumerable<string> hosts)
    {
        _itemsCleaned = 0;
        _itemsLocked = 0;
        string[] targetHosts = hosts.Where(h => !string.IsNullOrWhiteSpace(h))
                                    .Select(h => h.Trim())
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToArray();
        if (targetHosts.Length == 0) return new SweepReport();

        RegistryHistory(targetHosts);
        DefaultRdpFile(targetHosts);
        JumpLists(targetHosts);
        RecentItems(targetHosts);
        TempLaunchers();
        DeleteSavedRdpCredentials(targetHosts);
        return new SweepReport { ItemsCleaned = _itemsCleaned, ItemsLocked = _itemsLocked };
    }

    /// <summary>Sweep + delete every saved RDP credential on this PC.</summary>
    public static SweepReport DeepSweep()
    {
        var report = Sweep();
        DeleteSavedRdpCredentials();
        return report;
    }

    // ---------------- registry history ----------------

    private static void RegistryHistory(string[]? explicitHosts = null)
    {
        TryRun(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Terminal Server Client", writable: true);
            if (key == null) return;

            var liveHosts = RdpLauncher.GetLiveHosts();

            if (Everything && (explicitHosts == null || explicitHosts.Length == 0))
            {
                foreach (var name in key.GetValueNames())
                {
                    if (name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = key.GetValue(name)?.ToString() ?? "";
                        if (!liveHosts.Any(h => val.Contains(h, StringComparison.OrdinalIgnoreCase)))
                            key.DeleteValue(name, throwOnMissingValue: false);
                    }
                }

                using (var def = key.OpenSubKey("Default", writable: true))
                {
                    if (def != null)
                    {
                        foreach (var name in def.GetValueNames())
                        {
                            string val = def.GetValue(name)?.ToString() ?? "";
                            if (!liveHosts.Any(h => val.Contains(h, StringComparison.OrdinalIgnoreCase)))
                                def.DeleteValue(name, throwOnMissingValue: false);
                        }
                    }
                }

                using (var servers = key.OpenSubKey("Servers", writable: true))
                {
                    if (servers != null)
                    {
                        foreach (string sub in servers.GetSubKeyNames())
                        {
                            if (!liveHosts.Any(h => sub.Contains(h, StringComparison.OrdinalIgnoreCase)))
                                servers.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                        }
                    }
                }
                return;
            }

            // Scoped: legacy MRU values are keyed by name but hold the host as data.
            foreach (var name in key.GetValueNames())
            {
                if (!name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase)) continue;
                string val = key.GetValue(name)?.ToString() ?? "";
                if (liveHosts.Any(h => val.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
                if (MentionsTargetHost(val, explicitHosts))
                    key.DeleteValue(name, throwOnMissingValue: false);
            }

            // Modern mstsc: address-box history lives under "Default" as MRU0..MRUn.
            using (var def = key.OpenSubKey("Default", writable: true))
            {
                if (def != null)
                    foreach (var name in def.GetValueNames())
                    {
                        string val = def.GetValue(name)?.ToString() ?? "";
                        if (liveHosts.Any(h => val.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
                        if (MentionsTargetHost(def.GetValue(name)?.ToString() ?? "", explicitHosts))
                            def.DeleteValue(name, throwOnMissingValue: false);
                    }
            }

            // Modern mstsc: one subkey per host, holding UsernameHint etc.
            using (var servers = key.OpenSubKey("Servers", writable: true))
            {
                if (servers != null)
                    foreach (string sub in servers.GetSubKeyNames())
                    {
                        if (liveHosts.Any(h => sub.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
                        if (MentionsTargetHost(sub, explicitHosts))
                            servers.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                    }
            }
        });
    }

    // ---------------- files ----------------

    private static void DefaultRdpFile(string[]? explicitHosts = null)
    {
        TryRun(() =>
        {
            foreach (string path in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Default.rdp"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Default.rdp")
                     })
            {
                if (!File.Exists(path)) continue;
                if (!Everything && !MentionsTargetHost(File.ReadAllText(path), explicitHosts)) continue;
                TryDeleteFile(path);
            }
        });
    }

    private static void JumpLists(string[]? explicitHosts = null)
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
                        if (!MentionsTargetHost(asText, explicitHosts)) continue;
                    }
                    TryDeleteFile(file);
                }
                catch { _itemsLocked++; }
            }
        });
    }

    private static void RecentItems(string[]? explicitHosts = null)
    {
        TryRun(() =>
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "Microsoft", "Windows", "Recent");
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.GetFiles(dir, "*.rdp*"))
            {
                string name = Path.GetFileName(file);
                bool ours = name.StartsWith("rdpv_", StringComparison.OrdinalIgnoreCase) || MentionsTargetHost(name, explicitHosts);
                if (!Everything && !ours) continue;
                TryDeleteFile(file);
            }
        });
    }

    /// <summary>Our own temporary launcher files (%TEMP%\rdpv_*.rdp). Active sessions are preserved until exit.</summary>
    public static void TempLaunchers()
    {
        TryRun(() =>
        {
            var liveTempFiles = RdpLauncher.GetLiveTempFiles();
            foreach (string file in Directory.GetFiles(Path.GetTempPath(), "rdpv_*.rdp"))
            {
                if (liveTempFiles.Contains(file)) continue;
                TryDeleteFile(file);
            }
        });
    }

    /// <summary>
    /// Issue #7: Removes any .net single-file extraction directories left in %TEMP%.
    /// </summary>
    public static void CleanBundleResidue()
    {
        TryRun(() =>
        {
            string netTemp = Path.Combine(Path.GetTempPath(), ".net", "RDPVault");
            if (Directory.Exists(netTemp))
            {
                try { Directory.Delete(netTemp, recursive: true); } catch { }
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

    public static void DeleteSavedRdpCredentials(string[]? explicitHosts = null)
    {
        TryRun(() =>
        {
            if (!CredEnumerateW(null, 0, out int count, out IntPtr pCreds)) return;
            try
            {
                var liveHosts = RdpLauncher.GetLiveHosts();
                IntPtr[] creds = new IntPtr[count];
                Marshal.Copy(pCreds, creds, 0, count);

                foreach (IntPtr p in creds)
                {
                    var c = Marshal.PtrToStructure<CREDENTIAL>(p);
                    string? target = Marshal.PtrToStringUni(c.TargetName);
                    if (target == null) continue;
                    if (!target.StartsWith("TERMSRV/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (liveHosts.Any(h => target.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!Everything && !MentionsTargetHost(target, explicitHosts)) continue;
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _itemsCleaned++;
            }
        }
        catch (IOException)
        {
            _itemsLocked++;
        }
        catch (UnauthorizedAccessException)
        {
            _itemsLocked++;
        }
        catch { }
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
