using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDPVault;

public static class SystemPromptFocus
{
    private static IntPtr _owner;

    public static void SetOwner(IntPtr hwnd) => _owner = hwnd;

    public static IDisposable Begin() => new Scope(_owner);


    private sealed class Scope : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _watchdog;
        private readonly List<IntPtr> _pinned = new();

        public Scope(IntPtr owner)
        {
            if (owner != IntPtr.Zero)
            {
                try
                {
                    ShowWindow(owner, SW_SHOW);
                    SetForegroundWindow(owner);
                }
                catch { }
            }

            try { AllowSetForegroundWindow(ASFW_ANY); } catch { }

            _watchdog = Task.Run(() => WatchAsync(_cts.Token));
        }

        private async Task WatchAsync(CancellationToken token)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
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
            catch {}
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _watchdog.Wait(500); } catch { }

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


    private static readonly string[] BrokerProcesses =
    {
        "credentialuibroker",
        "logonui",
        "consent",
        "shellexperiencehost"
    };

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


    public static void ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hwnd, SW_SHOW);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(hwnd);

            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint thisThread = GetCurrentThreadId();

            bool attached = fgThread != 0 && fgThread != thisThread &&
                            AttachThreadInput(thisThread, fgThread, true);
            try
            {
                if (!SetForegroundWindow(hwnd))
                    SwitchToThisWindow(hwnd, true);
                SetActiveWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, fgThread, false);
            }
        }
        catch { }
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            ShowWindow(hwnd, SW_SHOW);

            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(hwnd);

            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint thisThread = GetCurrentThreadId();

            bool attached = fgThread != 0 && fgThread != thisThread &&
                            AttachThreadInput(thisThread, fgThread, true);
            try
            {
                if (!SetForegroundWindow(hwnd))
                    SwitchToThisWindow(hwnd, true);
                SetActiveWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, fgThread, false);
            }
        }
        catch { }
    }


    private const int ASFW_ANY = -1;
    private const int SW_SHOW = 5;
    private const int SW_MINIMIZE = 6;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public static void SendToBackground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hwnd, SW_MINIMIZE);
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }
        catch { }
    }

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
