using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RDPVault.Android.Rdp;

/// <summary>
/// Native P/Invoke bindings and high-level manager for the embedded FreeRDP engine.
/// Connects, authenticates, and renders the remote desktop directly in-memory.
/// </summary>
public static class NativeFreeRdp
{
    private const string LibFreeRdp = "freerdp2";
    private const string LibWinPR = "winpr2";

    // FreeRDP setting keys (stable FreeRDP 2.x)
    public const int FreeRDP_ServerPort = 19;
    public const int FreeRDP_ServerHostname = 20;
    public const int FreeRDP_Username = 21;
    public const int FreeRDP_Password = 22;
    public const int FreeRDP_Domain = 23;
    public const int FreeRDP_DesktopWidth = 129;
    public const int FreeRDP_DesktopHeight = 130;
    public const int FreeRDP_ColorDepth = 131;
    public const int FreeRDP_AudioPlayback = 714;
    public const int FreeRDP_IgnoreCertificate = 1408;
    public const int FreeRDP_SmartSizing = 1551;
    public const int FreeRDP_DynamicResolutionUpdate = 1558;
    public const int FreeRDP_GatewayPort = 1985;
    public const int FreeRDP_GatewayHostname = 1986;
    public const int FreeRDP_GatewayUsername = 1987;
    public const int FreeRDP_GatewayPassword = 1988;
    public const int FreeRDP_GatewayDomain = 1989;
    public const int FreeRDP_GatewayUsageMethod = 1990;
    public const int FreeRDP_RedirectClipboard = 4800;
    public const int FreeRDP_SupportDisplayControl = 5185;

    // Pointer event flags (RDP protocol)
    public const ushort PTR_FLAGS_HWHEEL = 0x0400;
    public const ushort PTR_FLAGS_WHEEL = 0x0200;
    public const ushort PTR_FLAGS_WHEEL_NEGATIVE = 0x0100;
    public const ushort PTR_FLAGS_MOVE = 0x0800;
    public const ushort PTR_FLAGS_DOWN = 0x8000;
    public const ushort PTR_FLAGS_BUTTON1 = 0x1000; // Left button
    public const ushort PTR_FLAGS_BUTTON2 = 0x2000; // Right button
    public const ushort PTR_FLAGS_BUTTON3 = 0x4000; // Middle button

    // Keyboard flags
    public const ushort KBD_FLAGS_EXTENDED = 0x0100;
    public const ushort KBD_FLAGS_DOWN = 0x4000;
    public const ushort KBD_FLAGS_RELEASE = 0x8000;

    // GDI 32bpp format
    public const uint CLRCONV_ALPHA = 0x00000004;
    public const uint PIXEL_FORMAT_BGRX32 = 0x20040888;

    // Delegates for native callbacks
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool PreConnectDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool PostConnectDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PostDisconnectDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool AuthenticateDelegate(IntPtr instance, out IntPtr username, out IntPtr password, out IntPtr domain);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint VerifyCertificateDelegate(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string commonName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string subject,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string issuer,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fingerprint,
        bool hostMismatch);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool BeginPaintDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool EndPaintDelegate(IntPtr context);

    // Native P/Invoke functions
    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr freerdp_new();

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern void freerdp_free(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_context_new(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern void freerdp_context_free(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_connect(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_disconnect(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_abort_connect(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_shall_disconnect(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint freerdp_get_event_handles(IntPtr context, [In, Out] IntPtr[] events, uint count);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_check_event_handles(IntPtr context);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool gdi_init(IntPtr instance, uint format);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern void gdi_free(IntPtr instance);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern int freerdp_settings_get_key_for_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_settings_set_string(IntPtr settings, int key, [MarshalAs(UnmanagedType.LPUTF8Str)] string? val);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_settings_set_uint32(IntPtr settings, int key, uint val);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_settings_set_bool(IntPtr settings, int key, bool val);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_input_send_mouse_event(IntPtr input, ushort flags, ushort x, ushort y);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_input_send_extended_mouse_event(IntPtr input, ushort flags, ushort x, ushort y);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_input_send_keyboard_event(IntPtr input, ushort flags, ushort code);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_input_send_keyboard_event_ex(IntPtr input, bool down, bool repeat, uint rdpScanCode);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_input_send_unicode_keyboard_event(IntPtr input, ushort flags, ushort code);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint freerdp_get_last_error(IntPtr context);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr freerdp_get_last_error_string(uint code);

    [DllImport(LibWinPR, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint WaitForMultipleObjects(uint nCount, [In] IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);
}

/// <summary>
/// High-level managed session running the native FreeRDP connection loop.
/// Streams credentials into RAM, clears them with CryptographicOperations.ZeroMemory immediately,
/// and fires render updates on incoming frame buffers.
/// </summary>
public sealed class FreeRdpSession : IDisposable
{
    private IntPtr _instance = IntPtr.Zero;
    private IntPtr _context = IntPtr.Zero;
    private IntPtr _settings = IntPtr.Zero;
    private IntPtr _input = IntPtr.Zero;
    private IntPtr _update = IntPtr.Zero;

    private readonly NativeFreeRdp.PreConnectDelegate _preConnect;
    private readonly NativeFreeRdp.PostConnectDelegate _postConnect;
    private readonly NativeFreeRdp.PostDisconnectDelegate _postDisconnect;
    private readonly NativeFreeRdp.VerifyCertificateDelegate _verifyCert;
    private readonly NativeFreeRdp.BeginPaintDelegate _beginPaint;
    private readonly NativeFreeRdp.EndPaintDelegate _endPaint;

    private volatile bool _isDisposed;
    private volatile bool _stopRequested;
    private Task? _sessionTask;

    public event Action? Connected;
    public event Action<string>? ConnectionFailed;
    public event Action? Disconnected;
    public event Action<int, int, int, int, IntPtr, int>? FramebufferUpdated;
    public event Action<string>? RemoteClipboardReceived;

    public RdpSessionConfig Config { get; }
    public bool IsConnected { get; private set; }
    public int RemoteWidth { get; private set; }
    public int RemoteHeight { get; private set; }

    public FreeRdpSession(RdpSessionConfig config)
    {
        Config = config;
        RemoteWidth = config.Width > 0 ? config.Width : 1920;
        RemoteHeight = config.Height > 0 ? config.Height : 1080;

        _preConnect = OnPreConnect;
        _postConnect = OnPostConnect;
        _postDisconnect = OnPostDisconnect;
        _verifyCert = OnVerifyCertificate;
        _beginPaint = OnBeginPaint;
        _endPaint = OnEndPaint;
    }

    public void Start()
    {
        _sessionTask = Task.Run(RunSessionLoop);
    }

    private void RunSessionLoop()
    {
        string? failureReason = null;
        try
        {
            _instance = NativeFreeRdp.freerdp_new();
            if (_instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to allocate FreeRDP native instance.");
            }

            // Write callbacks into freerdp struct
            // Offsets: PreConnect=384 (48*8), PostConnect=392 (49*8), VerifyCert=408 (51*8), PostDisconnect=440 (55*8)
            Marshal.WriteIntPtr(_instance, 48 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_preConnect));
            Marshal.WriteIntPtr(_instance, 49 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_postConnect));
            Marshal.WriteIntPtr(_instance, 51 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_verifyCert));
            Marshal.WriteIntPtr(_instance, 55 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_postDisconnect));

            // Context size and initialization
            NativeFreeRdp.freerdp_context_new(_instance);
            _context = Marshal.ReadIntPtr(_instance); // offset 0
            _settings = Marshal.ReadIntPtr(_instance, 18 * IntPtr.Size); // offset 18
            _input = Marshal.ReadIntPtr(_instance, 16 * IntPtr.Size); // offset 16
            _update = Marshal.ReadIntPtr(_instance, 17 * IntPtr.Size); // offset 17

            // Configure connection settings
            ApplySettings();

            // Connect
            bool ok = NativeFreeRdp.freerdp_connect(_instance);
            if (!ok || _stopRequested)
            {
                uint err = _context != IntPtr.Zero ? NativeFreeRdp.freerdp_get_last_error(_context) : 0;
                failureReason = $"Connection failed (Code 0x{err:X}). Check host address and network reachability.";
                ConnectionFailed?.Invoke(failureReason);
                return;
            }

            IsConnected = true;
            Connected?.Invoke();

            // Event pump loop
            IntPtr[] handles = new IntPtr[64];
            while (!_stopRequested && !NativeFreeRdp.freerdp_shall_disconnect(_instance))
            {
                uint count = NativeFreeRdp.freerdp_get_event_handles(_context, handles, 64);
                if (count == 0) break;

                uint status = NativeFreeRdp.WaitForMultipleObjects(count, handles, false, 40);
                if (!NativeFreeRdp.freerdp_check_event_handles(_context))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            ConnectionFailed?.Invoke(failureReason);
        }
        finally
        {
            IsConnected = false;
            CleanupNative();
            Disconnected?.Invoke();
        }
    }

    private void ApplySettings()
    {
        if (_settings == IntPtr.Zero) return;

        NativeFreeRdp.freerdp_settings_set_string(_settings, NativeFreeRdp.FreeRDP_ServerHostname, Config.Host);
        NativeFreeRdp.freerdp_settings_set_uint32(_settings, NativeFreeRdp.FreeRDP_ServerPort, (uint)(Config.Port > 0 ? Config.Port : 3389));

        if (!string.IsNullOrEmpty(Config.Username))
        {
            NativeFreeRdp.freerdp_settings_set_string(_settings, NativeFreeRdp.FreeRDP_Username, Config.Username);
        }

        if (!string.IsNullOrEmpty(Config.Domain))
        {
            NativeFreeRdp.freerdp_settings_set_string(_settings, NativeFreeRdp.FreeRDP_Domain, Config.Domain);
        }

        // Stream password directly in RAM and wipe immediately upon consumption
        if (Config.PasswordChars != null && Config.PasswordChars.Length > 0)
        {
            byte[] passBytes = Encoding.UTF8.GetBytes(Config.PasswordChars);
            try
            {
                string passStr = Encoding.UTF8.GetString(passBytes);
                NativeFreeRdp.freerdp_settings_set_string(_settings, NativeFreeRdp.FreeRDP_Password, passStr);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passBytes);
                Array.Clear(Config.PasswordChars, 0, Config.PasswordChars.Length);
            }
        }

        // Dimensions and color
        NativeFreeRdp.freerdp_settings_set_uint32(_settings, NativeFreeRdp.FreeRDP_DesktopWidth, (uint)RemoteWidth);
        NativeFreeRdp.freerdp_settings_set_uint32(_settings, NativeFreeRdp.FreeRDP_DesktopHeight, (uint)RemoteHeight);
        NativeFreeRdp.freerdp_settings_set_uint32(_settings, NativeFreeRdp.FreeRDP_ColorDepth, 32);

        // Architectural invariants:
        // 1. Strict default deny for clipboard sharing
        NativeFreeRdp.freerdp_settings_set_bool(_settings, NativeFreeRdp.FreeRDP_RedirectClipboard, Config.AllowClipboard);

        // 2. Prohibit dynamic display resizing (MS-RDPEDISP extension channel disabled)
        NativeFreeRdp.freerdp_settings_set_bool(_settings, NativeFreeRdp.FreeRDP_SupportDisplayControl, false);
        NativeFreeRdp.freerdp_settings_set_bool(_settings, NativeFreeRdp.FreeRDP_DynamicResolutionUpdate, false);
        NativeFreeRdp.freerdp_settings_set_bool(_settings, NativeFreeRdp.FreeRDP_SmartSizing, false);

        // 3. Remote audio playback redirection enabled
        NativeFreeRdp.freerdp_settings_set_bool(_settings, NativeFreeRdp.FreeRDP_AudioPlayback, true);

        // 4. Certificate warning suppression
        NativeFreeRdp.freerdp_settings_set_bool(_settings, NativeFreeRdp.FreeRDP_IgnoreCertificate, Config.SuppressCertWarnings);

        // RD Gateway if specified
        if (!string.IsNullOrWhiteSpace(Config.GatewayHost))
        {
            NativeFreeRdp.freerdp_settings_set_string(_settings, NativeFreeRdp.FreeRDP_GatewayHostname, Config.GatewayHost);
            NativeFreeRdp.freerdp_settings_set_uint32(_settings, NativeFreeRdp.FreeRDP_GatewayPort, (uint)(Config.GatewayPort > 0 ? Config.GatewayPort : 443));
            NativeFreeRdp.freerdp_settings_set_uint32(_settings, NativeFreeRdp.FreeRDP_GatewayUsageMethod, 1);
        }
    }

    private bool OnPreConnect(IntPtr instance) => true;

    private bool OnPostConnect(IntPtr instance)
    {
        // Initialize GDI 32bpp framebuffer
        NativeFreeRdp.gdi_init(instance, NativeFreeRdp.CLRCONV_ALPHA);

        IntPtr update = Marshal.ReadIntPtr(instance, 17 * IntPtr.Size);
        if (update != IntPtr.Zero)
        {
            // Hook BeginPaint and EndPaint in rdp_update (BeginPaint=72, EndPaint=80)
            Marshal.WriteIntPtr(update, 72, Marshal.GetFunctionPointerForDelegate(_beginPaint));
            Marshal.WriteIntPtr(update, 80, Marshal.GetFunctionPointerForDelegate(_endPaint));
        }

        return true;
    }

    private void OnPostDisconnect(IntPtr instance) { }

    private uint OnVerifyCertificate(
        IntPtr instance,
        string commonName,
        string subject,
        string issuer,
        string fingerprint,
        bool hostMismatch)
    {
        if (Config.SuppressCertWarnings)
        {
            return 2; // Accept for session
        }

        if (!string.IsNullOrEmpty(Config.PinnedFingerprint) &&
            string.Equals(Config.PinnedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return 1; // Accept and match pinned
        }

        return 0; // Reject
    }

    private bool OnBeginPaint(IntPtr context) => true;

    private bool OnEndPaint(IntPtr context)
    {
        if (_isDisposed || context == IntPtr.Zero) return true;

        try
        {
            // context->gdi is at offset 33 (33 * 8 = 264)
            IntPtr gdi = Marshal.ReadIntPtr(context, 33 * IntPtr.Size);
            if (gdi != IntPtr.Zero)
            {
                int w = Marshal.ReadInt32(gdi, 8);
                int h = Marshal.ReadInt32(gdi, 12);
                int stride = Marshal.ReadInt32(gdi, 16);
                IntPtr buffer = Marshal.ReadIntPtr(gdi, 64);

                if (w > 0 && h > 0 && buffer != IntPtr.Zero)
                {
                    RemoteWidth = w;
                    RemoteHeight = h;
                    FramebufferUpdated?.Invoke(0, 0, w, h, buffer, stride);
                }
            }
        }
        catch { }

        return true;
    }

    public void SendMouseMove(ushort x, ushort y)
    {
        if (_input == IntPtr.Zero || !IsConnected) return;
        NativeFreeRdp.freerdp_input_send_mouse_event(_input, NativeFreeRdp.PTR_FLAGS_MOVE, x, y);
    }

    public void SendMouseButton(ushort buttonFlag, bool down, ushort x, ushort y)
    {
        if (_input == IntPtr.Zero || !IsConnected) return;
        ushort flags = (ushort)(buttonFlag | (down ? NativeFreeRdp.PTR_FLAGS_DOWN : 0));
        NativeFreeRdp.freerdp_input_send_mouse_event(_input, flags, x, y);
    }

    public void SendMouseWheel(bool up, ushort step, ushort x, ushort y)
    {
        if (_input == IntPtr.Zero || !IsConnected) return;
        ushort flags = (ushort)(NativeFreeRdp.PTR_FLAGS_WHEEL | (up ? 0 : NativeFreeRdp.PTR_FLAGS_WHEEL_NEGATIVE) | (step & 0xFF));
        NativeFreeRdp.freerdp_input_send_mouse_event(_input, flags, x, y);
    }

    public void SendKeyboardScanCode(ushort code, bool down, bool extended)
    {
        if (_input == IntPtr.Zero || !IsConnected) return;
        ushort flags = (ushort)((down ? NativeFreeRdp.KBD_FLAGS_DOWN : NativeFreeRdp.KBD_FLAGS_RELEASE) | (extended ? NativeFreeRdp.KBD_FLAGS_EXTENDED : 0));
        NativeFreeRdp.freerdp_input_send_keyboard_event(_input, flags, code);
    }

    public void SendUnicodeChar(char c)
    {
        if (_input == IntPtr.Zero || !IsConnected) return;
        NativeFreeRdp.freerdp_input_send_unicode_keyboard_event(_input, 0, (ushort)c);
        NativeFreeRdp.freerdp_input_send_unicode_keyboard_event(_input, NativeFreeRdp.KBD_FLAGS_RELEASE, (ushort)c);
    }

    public void Disconnect()
    {
        if (_stopRequested) return;
        _stopRequested = true;

        if (_instance != IntPtr.Zero)
        {
            try { NativeFreeRdp.freerdp_abort_connect(_instance); } catch { }
            try { NativeFreeRdp.freerdp_disconnect(_instance); } catch { }
        }
    }

    private void CleanupNative()
    {
        if (_instance != IntPtr.Zero)
        {
            try { NativeFreeRdp.gdi_free(_instance); } catch { }
            try { NativeFreeRdp.freerdp_context_free(_instance); } catch { }
            try { NativeFreeRdp.freerdp_free(_instance); } catch { }
            _instance = IntPtr.Zero;
            _context = IntPtr.Zero;
            _settings = IntPtr.Zero;
            _input = IntPtr.Zero;
            _update = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Disconnect();
    }
}
