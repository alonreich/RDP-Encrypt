using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace RDPVault;

/// <summary>
/// Launches mstsc.exe from a generated temp .rdp file and scrubs everything when it closes.
/// If a password is saved, it is placed in Windows Credential Manager as a SESSION
/// credential (vanishes at sign-out / reboot) and deleted the moment the RDP window
/// closes - it is never written into the .rdp file itself.
/// </summary>
public static class RdpLauncher
{
    private static readonly List<Process> LiveSessions = new();
    private static readonly object Gate = new();

    public static event Action<string>? SessionEnded;
    public static event Action<string>? SessionStarted;
    public static event Action<string>? LaunchFailed;

    /// <summary>Start one RDP session for the profile. Returns false if mstsc failed to start.</summary>
    public static bool Launch(RdpProfile p)
    {
        SessionManager.Current.Touch();   // issue #11: connecting is activity

        string tempRdp = Path.Combine(Path.GetTempPath(), $"rdpv_{p.Id}.rdp");
        try
        {
            File.WriteAllText(tempRdp, BuildRdpFile(p), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LaunchFailed?.Invoke($"Could not prepare the connection file: {ex.Message}");
            return false;
        }

        // Issue #18b: mstsc looks the credential up by the exact "full address" string.
        // Writing only TERMSRV/{host} meant saved passwords were never found for any
        // profile on a non-default port. Both spellings are now written and removed.
        var credTargets = new List<string>();
        if (p.HasPassword && !string.IsNullOrEmpty(p.Username))
        {
            foreach (string target in CredentialTargets(p))
                if (WriteSessionCredential(target, p.Username, p.Password))
                    credTargets.Add(target);
            // If none could be written, mstsc simply prompts - graceful degradation.
        }

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "mstsc.exe"),
            Arguments = $"\"{tempRdp}\"",
            UseShellExecute = false
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            TryDelete(tempRdp);
            foreach (string t in credTargets) DeleteCredential(t);
            LaunchFailed?.Invoke($"Windows could not start Remote Desktop: {ex.Message}");
            return false;
        }
        if (proc == null)
        {
            TryDelete(tempRdp);
            foreach (string t in credTargets) DeleteCredential(t);
            LaunchFailed?.Invoke("Windows could not start Remote Desktop.");
            return false;
        }

        lock (Gate)
        {
            LiveSessions.RemoveAll(pr => { try { return pr.HasExited; } catch { return true; } });
            LiveSessions.Add(proc);
        }
        SessionStarted?.Invoke(p.Name);

        string profileName = p.Name;
        _ = Task.Run(async () =>
        {
            try
            {
                if (proc.HasExited && proc.ExitCode != 0)
                {
                    // Some Windows builds relaunch mstsc; give a short grace period.
                    await Task.Delay(3000);
                }
                else
                {
                    proc.WaitForExit();
                }
            }
            catch { /* process info no longer available */ }

            // ISSUE #9 - REMOVED ON PURPOSE.
            // The old code re-read the temp .rdp after mstsc exited and copied
            // redirectclipboard / redirectdrives / redirectprinters / redirectsmartcards
            // back into the saved profile, then wrote the vault. mstsc rewrites that
            // file whenever the user ticks a box in its own dialog, so a single
            // "share my local drives" tick silently and permanently enabled drive
            // redirection on the stored profile. The profile is the user's setting;
            // a transient session must never edit it.

            TryDelete(tempRdp);
            foreach (string t in credTargets) DeleteCredential(t);
            TraceCleaner.Sweep();
            SessionEnded?.Invoke(profileName);
        });

        return true;
    }

    /// <summary>Kill every mstsc.exe this app launched (USB pulled / user request).</summary>
    public static void KillAll()
    {
        lock (Gate)
        {
            foreach (var proc in LiveSessions)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { }
            }
            LiveSessions.Clear();
        }
    }

    public static int LiveCount()
    {
        lock (Gate)
        {
            LiveSessions.RemoveAll(pr => { try { return pr.HasExited; } catch { return true; } });
            return LiveSessions.Count;
        }
    }

    public static bool AnyLive() => LiveCount() > 0;

    // ---------------- .rdp generation ----------------

    private static string BuildRdpFile(RdpProfile p)
    {
        bool useMulti = p.UseMultiMon || (SessionManager.Current.Payload?.Settings.ForceMultiMon ?? false);

        // Issue #10: 2 = refuse to connect when the server's identity cannot be
        // verified. 1 = warn but allow, used only when the user explicitly opts in
        // for a host with a self-signed certificate. 0 (silently accept anything,
        // the old hard-coded value) is no longer reachable.
        int authLevel = p.AllowUnverifiedServer ? 1 : 2;

        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:" + (p.FullScreen ? 2 : 1));
        if (!p.FullScreen)
        {
            sb.AppendLine($"desktopwidth:i:{p.Width}");
            sb.AppendLine($"desktopheight:i:{p.Height}");
        }
        sb.AppendLine("use multimon:i:" + (useMulti ? "1" : "0"));
        sb.AppendLine("session bpp:i:32");
        sb.AppendLine($"winposstr:s:0,1,0,0,{Math.Max(800, p.Width)},{Math.Max(600, p.Height)}");
        sb.AppendLine("compression:i:1");
        sb.AppendLine("keyboardhook:i:2");
        sb.AppendLine("audiocapturemode:i:0");
        sb.AppendLine("videoplaybackmode:i:1");
        sb.AppendLine("connection type:i:7");
        sb.AppendLine("networkautodetect:i:1");
        sb.AppendLine("bandwidthautodetect:i:1");
        sb.AppendLine("displayconnectionbar:i:1");
        sb.AppendLine("disable wallpaper:i:0");
        sb.AppendLine("allow font smoothing:i:1");
        sb.AppendLine("allow desktop composition:i:1");
        sb.AppendLine("disable full window drag:i:0");
        sb.AppendLine("disable menu anims:i:0");
        sb.AppendLine("disable themes:i:0");
        sb.AppendLine("disable cursor setting:i:0");
        sb.AppendLine("bitmapcachepersistenable:i:1");
        sb.AppendLine("full address:s:" + FullAddress(p));
        sb.AppendLine("audiomode:i:0");
        sb.AppendLine("redirectprinters:i:" + (p.AllowPrinters ? 1 : 0));
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:" + (p.AllowSmartCards ? 1 : 0));
        sb.AppendLine("redirectclipboard:i:" + (p.AllowClipboard ? 1 : 0));
        sb.AppendLine("redirectposdevices:i:0");
        sb.AppendLine("autoreconnection enabled:i:1");
        sb.AppendLine("authentication level:i:" + authLevel);
        sb.AppendLine("enablecredsspsupport:i:1");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("negotiate security layer:i:1");
        sb.AppendLine("remoteapplicationmode:i:0");
        sb.AppendLine("alternate shell:s:");
        sb.AppendLine("shell working directory:s:");
        sb.AppendLine("gatewayhostname:s:" + p.GatewayHost);
        sb.AppendLine("gatewayusagemethod:i:" + (string.IsNullOrEmpty(p.GatewayHost) ? 0 : 1));
        sb.AppendLine("gatewaycredentialssource:i:4");
        sb.AppendLine("gatewayprofileusagemethod:i:" + (string.IsNullOrEmpty(p.GatewayHost) ? 0 : 1));
        sb.AppendLine("promptcredentialonce:i:0");
        sb.AppendLine("use redirection server name:i:0");
        if (!string.IsNullOrEmpty(p.Username))
        {
            sb.AppendLine("username:s:" + p.Username);   // password is NEVER written here
            sb.AppendLine("domain:s:");
        }

        if (p.AllowDrives)
        {
            sb.AppendLine("drivestoredirect:s:*");
            sb.AppendLine("redirectdrives:i:1");
        }
        else
        {
            sb.AppendLine("redirectdrives:i:0");
        }

        sb.AppendLine("pcb:s:");            // no connection bookkeeping id
        sb.AppendLine("disableremoteappcapscheck:i:1");
        return sb.ToString();
    }

    private static string FullAddress(RdpProfile p) => p.Port == 3389 ? p.Host : $"{p.Host}:{p.Port}";

    private static IEnumerable<string> CredentialTargets(RdpProfile p)
    {
        yield return $"TERMSRV/{p.Host}";
        if (p.Port != 3389) yield return $"TERMSRV/{p.Host}:{p.Port}";
    }

    // ---------------- session credential via CredWrite ----------------

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_SESSION = 1;

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
    private static extern bool CredWriteW(ref CREDENTIAL cred, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int reserved);

    private static bool WriteSessionCredential(string target, string user, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
        IntPtr userPtr = Marshal.StringToHGlobalUni(user);
        IntPtr targetPtr = Marshal.StringToHGlobalUni(target);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_SESSION, // lives only until logoff; we delete sooner
                UserName = userPtr
            };
            return CredWriteW(ref c, 0);
        }
        catch { return false; }
        finally
        {
            // Scrub the unmanaged copy of the password before releasing it.
            try { for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0); } catch { }
            Marshal.FreeHGlobal(blobPtr);
            Marshal.FreeHGlobal(userPtr);
            Marshal.FreeHGlobal(targetPtr);
            Array.Clear(blob);
        }
    }

    private static void DeleteCredential(string target)
    {
        try { CredDeleteW(target, CRED_TYPE_GENERIC, 0); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
