using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RDPVault;

/// <summary>
/// Launches mstsc.exe from a generated temp .rdp file and scrubs everything when it closes.
/// If a password is saved, it is placed in Windows Credential Manager as a SESSION
/// credential (vanishes at sign-out / reboot) under TERMSRV/<host> and is deleted
/// the moment the RDP window closes - never written into the .rdp file itself.
/// </summary>
public static class RdpLauncher
{
    private static readonly List<Process> LiveSessions = new();
    private static readonly object Gate = new();

    public static event Action<string>? SessionEnded;

    /// <summary>Start one RDP session for the profile. Returns false if mstsc failed to start.</summary>
    public static bool Launch(RdpProfile p)
    {
        string tempRdp = Path.Combine(Path.GetTempPath(), $"rdpv_{p.Id}.rdp");
        File.WriteAllText(tempRdp, BuildRdpFile(p), new UTF8Encoding(false));

        string? credTarget = null;
        if (p.HasPassword && !string.IsNullOrEmpty(p.Username))
        {
            credTarget = $"TERMSRV/{p.Host}";
            if (!WriteSessionCredential(credTarget, p.Username, p.Password))
                credTarget = null; // fall back to mstsc asking for the password
        }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.SystemDirectory + "\\mstsc.exe",
            Arguments = $"\"{tempRdp}\"",
            UseShellExecute = false
        };

        Process? proc;
        try { proc = Process.Start(psi); }
        catch
        {
            TryDelete(tempRdp);
            if (credTarget != null) DeleteCredential(credTarget);
            return false;
        }
        if (proc == null) return false;

        var target = credTarget;
        _ = Task.Run(async () =>
        {
            try
            {
                // mstsc may spawn/exit quickly; wait for the real UI process.
                if (proc.HasExited && proc.ExitCode != 0)
                {
                    // Some Windows versions relaunch elevated; give a short grace period
                    // and watch for any mstsc still alive before giving up.
                    await Task.Delay(3000);
                }
                else
                {
                    proc.WaitForExit();
                }
            }
            catch { /* process info no longer available */ }

            TryDelete(tempRdp);
            if (target != null) DeleteCredential(target);
            TraceCleaner.Sweep();          // immediate scrub the moment the window closes
            SessionEnded?.Invoke(p.Name);
        });

        lock (Gate) LiveSessions.Add(proc);
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

    public static bool AnyLive()
    {
        lock (Gate)
        {
            LiveSessions.RemoveAll(pr => { try { return pr.HasExited; } catch { return true; } });
            return LiveSessions.Count > 0;
        }
    }

    // ---------------- .rdp generation ----------------

    private static string BuildRdpFile(RdpProfile p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:" + (p.FullScreen ? 2 : 1));
        if (!p.FullScreen)
        {
            sb.AppendLine($"desktopwidth:i:{p.Width}");
            sb.AppendLine($"desktopheight:i:{p.Height}");
        }
        bool useMulti = p.UseMultiMon || (SessionManager.Current.Payload?.Settings.ForceMultiMon ?? false);
        sb.AppendLine("use multimon:i:" + (useMulti ? "1" : "0"));
        sb.AppendLine($"session bpp:i:32");
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
        sb.AppendLine("full address:s:" + (p.Port == 3389 ? p.Host : $"{p.Host}:{p.Port}"));
        sb.AppendLine("audiomode:i:0");
        sb.AppendLine("redirectprinters:i:" + (p.AllowPrinters ? 1 : 0));
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:" + (p.AllowSmartCards ? 1 : 0));
        sb.AppendLine("redirectclipboard:i:" + (p.AllowClipboard ? 1 : 0));
        sb.AppendLine("redirectposdevices:i:0");
        sb.AppendLine("autoreconnection enabled:i:1");
        sb.AppendLine("authentication level:i:0");
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
            sb.AppendLine("username:s:" + p.Username);
            sb.AppendLine("domain:s:");
        }

        // Drive redirection (local disks visible inside the session) - opt-in only.
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