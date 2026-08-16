using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDPVault;

/// <summary>
/// ISSUE #21 - the Windows Hello / TPM prompt opened behind the app and without
/// focus, so touching the fingerprint sensor did nothing until the user first
/// clicked the dialog. That defeats the entire point of a one-touch unlock.
///
/// WHY IT HAPPENS
/// KeyCredentialManager.RequestSignAsync / RequestCreateAsync render their prompt
/// in a separate broker process (CredentialUIBroker.exe / LogonUI.exe). Unlike
/// UserConsentVerifier, KeyCredentialManager exposes NO interop interface for
/// passing an owner HWND, so the broker window is never parented to our window and
/// Windows' foreground-lock rules routinely leave it behind whatever had focus.
///
/// WHAT THIS DOES - three layers, cheapest first:
///   1. Activate our own window, so our process is the foreground process at the
///      moment of the call.
///   2. AllowSetForegroundWindow(ASFW_ANY) - explicitly hands the foreground right
///      to whichever process shows the prompt. This alone fixes it on most systems.
///   3. A watchdog that polls for the broker's top-level window and, while the
///      operation is pending, keeps forcing it topmost and focused using the
///      AttachThreadInput technique. It stops the instant the prompt closes or the
///      scope is disposed, and the window is released from topmost on the way out.
///
/// The watchdog only ever touches windows belonging to the known credential broker
/// processes, or windows with the credential dialog's class name. It never
/// manipulates arbitrary windows.
/// </summary>
public static class SystemPromptFocus
{
    private static IntPtr _owner;

    /// <summary>Called by any window that may trigger a system credential prompt.</summary>
    public static void SetOwner(IntPtr hwnd) => _owner = hwnd;

    /// <summary>
    /// Wrap every KeyCredentialManager call in this. Dispose as soon as the
    /// operation finishes - success or failure.
    /// </summary>
    public static IDisposable Begin() => new Scope(_owner);

    // ------------------------------------------------------------------ scope

    private sealed class Scope : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _watchdog;
        private readonly List<IntPtr> _pinned = new();

        public Scope(IntPtr owner)
        {
            // 1. Make sure WE are the foreground process right now, otherwise
            //    step 2 is a no-op as far as Windows is concerned.
            if (owner != IntPtr.Zero)
            {
                try
                {
                    ShowWindow(owner, SW_SHOW);
                    SetForegroundWindow(owner);
                }
                catch { }
            }

            // 2. Grant the foreground right to whichever process shows the prompt.
            try { AllowSetForegroundWindow(ASFW_ANY); } catch { }

            // 3. Keep it in front until we are disposed.
            _watchdog = Task.Run(() => WatchAsync(_cts.Token));
        }

        private async Task WatchAsync(CancellationToken token)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);   // hard stop; never spin forever
            try
            {
                while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    IntPtr prompt = FindCredentialPrompt();
                    if (prompt != IntPtr.Zero)
                    {
                        if (!_pinned.Contains(prompt)) _pinned.Add(prompt);
                        if (GetForegroundWindow() != prompt) ForceForeground(prompt);
                    }
                    await Task.Delay(120, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* focus assistance must never break the unlock */ }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _watchdog.Wait(500); } catch { }

            // Release anything we pinned, in case the prompt is still on screen
            // (e.g. the user cancelled our await but Windows kept the dialog).
            foreach (IntPtr h in _pinned)
            {
                try
                {
                    if (IsWindow(h))
                        SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0,
                                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { }
            }

            _cts.Dispose();
        }
    }

    // ------------------------------------------------------------------ locating the prompt

    /// <summary>Processes Windows uses to host the Hello / credential prompt.</summary>
    private static readonly string[] BrokerProcesses =
    {
        "credentialuibroker",
        "logonui",
        "consent",
        "shellexperiencehost"
    };

    /// <summary>Window class of the "Windows Security" credential dialog.</summary>
    private const string CredentialDialogClass = "Credential Dialog Xaml Host";

    private static IntPtr FindCredentialPrompt()
    {
        IntPtr found = IntPtr.Zero;
        uint selfPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == selfPid) return true;

                if (ClassNameOf(hwnd) == CredentialDialogClass)
                {
                    found = hwnd;
                    return false;
                }

                string proc = ProcessNameOf(pid);
                if (proc.Length > 0 && Array.IndexOf(BrokerProcesses, proc) >= 0)
                {
                    // Broker processes also own invisible helper windows; require
                    // something with actual size on screen.
                    if (GetWindowRect(hwnd, out RECT r) && (r.Right - r.Left) > 120 && (r.Bottom - r.Top) > 80)
                    {
                        found = hwnd;
                        return false;
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : "";
    }

    private static readonly Dictionary<uint, string> ProcessNameCache = new();

    private static string ProcessNameOf(uint pid)
    {
        lock (ProcessNameCache)
        {
            if (ProcessNameCache.TryGetValue(pid, out string? cached)) return cached;
        }

        string name = "";
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName.ToLowerInvariant(); }
        catch { }

        lock (ProcessNameCache)
        {
            if (ProcessNameCache.Count > 64) ProcessNameCache.Clear();
            ProcessNameCache[pid] = name;
        }
        return name;
    }

    // ------------------------------------------------------------------ forcing focus

    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            ShowWindow(hwnd, SW_SHOW);

            // Pin it above everything for as long as the prompt is up - the user
            // asked for the authentication window to stay on top until it resolves.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(hwnd);

            // Windows refuses SetForegroundWindow from a process that does not own
            // the foreground. Attaching our input queue to the current foreground
            // thread is the standard, documented-by-practice way around that.
            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint thisThread = GetCurrentThreadId();

            bool attached = fgThread != 0 && fgThread != thisThread &&
                            AttachThreadInput(thisThread, fgThread, true);
            try
            {
                if (!SetForegroundWindow(hwnd))
                    SwitchToThisWindow(hwnd, true);   // last resort
                SetActiveWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, fgThread, false);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ P/Invoke

    private const int ASFW_ANY = -1;
    private const int SW_SHOW = 5;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
}
