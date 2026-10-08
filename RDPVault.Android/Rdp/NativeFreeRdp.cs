using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RDPVault.Android.Platform;

namespace RDPVault.Android.Rdp;

/// <summary>
/// Native P/Invoke bindings and high-level manager for the embedded FreeRDP engine.
/// Connects, authenticates, and renders the remote desktop directly in-memory.
/// </summary>
public static class NativeFreeRdp
{
    private const string LibFreeRdp = "freerdp2";
    private const string LibFreeRdpClient = "freerdp-client2";
    private const string LibWinPR = "winpr2";

    static NativeFreeRdp()
    {
        EnsureNativeLibrariesLoaded();
    }

    private static bool _librariesLoaded;

    /// <summary>
    /// Explicitly preloads native libraries in topological dependency order to ensure
    /// Android dynamic linker resolves transitive symbols without runtime failure.
    /// </summary>
    public static void EnsureNativeLibrariesLoaded()
    {
        if (_librariesLoaded) return;
        try
        {
            Java.Lang.JavaSystem.LoadLibrary("crypto");
            Java.Lang.JavaSystem.LoadLibrary("ssl");
            Java.Lang.JavaSystem.LoadLibrary("winpr2");
            Java.Lang.JavaSystem.LoadLibrary("freerdp2");
            try { Java.Lang.JavaSystem.LoadLibrary("freerdp-client2"); } catch { }
            _librariesLoaded = true;
            global::Android.Util.Log.Info("RDPVault", "FreeRDP native libraries loaded successfully.");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "FreeRDP native library preload warning: " + ex.Message);
        }
    }

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

    // GDI 32bpp formats
    public const uint CLRCONV_ALPHA = 0x00000004;
    public const uint PIXEL_FORMAT_BGRX32 = 0x20040888;
    public const uint PIXEL_FORMAT_BGRA32 = 0x20048888;
    public const uint PIXEL_FORMAT_RGBA32 = 0x20038888;
    public const uint PIXEL_FORMAT_RGBX32 = 0x20030888;

    // Delegates for native callbacks
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool PreConnectDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool PostConnectDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PostDisconnectDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool AuthenticateDelegate(IntPtr instance, ref IntPtr username, ref IntPtr password, ref IntPtr domain);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint VerifyCertificateDelegate(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string commonName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string subject,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string issuer,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fingerprint,
        bool hostMismatch);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint VerifyCertificateExDelegate(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        ushort port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string commonName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string subject,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string issuer,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fingerprint,
        uint flags);

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
    public static extern bool freerdp_settings_set_value_for_name(
        IntPtr settings,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string val);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_settings_set_string(IntPtr settings, nuint key, [MarshalAs(UnmanagedType.LPUTF8Str)] string? val);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_settings_set_uint32(IntPtr settings, nuint key, uint val);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool freerdp_settings_set_bool(IntPtr settings, nuint key, bool val);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint freerdp_settings_get_uint32(IntPtr settings, nuint key);

    [DllImport(LibFreeRdp, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr freerdp_settings_get_string(IntPtr settings, nuint key);

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

    [DllImport(LibFreeRdpClient, CallingConvention = CallingConvention.Cdecl)]
    public static extern int freerdp_client_settings_parse_command_line(
        IntPtr settings,
        int argc,
        [In] IntPtr[] argv,
        [MarshalAs(UnmanagedType.Bool)] bool allowUnknown);
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
    private readonly NativeFreeRdp.AuthenticateDelegate _authenticate;
    private readonly NativeFreeRdp.VerifyCertificateDelegate _verifyCert;
    private readonly NativeFreeRdp.VerifyCertificateExDelegate _verifyCertEx;
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
        _authenticate = OnAuthenticate;
        _verifyCert = OnVerifyCertificate;
        _verifyCertEx = OnVerifyCertificateEx;
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
            NativeFreeRdp.EnsureNativeLibrariesLoaded();

            _instance = NativeFreeRdp.freerdp_new();
            if (_instance == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to allocate FreeRDP native instance.");
            }

            // Write callbacks into freerdp struct
            // Offsets (64-bit pointers):
            // PreConnect=48, PostConnect=49, Authenticate=50, VerifyCert=51, PostDisconnect=55, VerifyCertEx=66
            Marshal.WriteIntPtr(_instance, 48 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_preConnect));
            Marshal.WriteIntPtr(_instance, 49 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_postConnect));
            Marshal.WriteIntPtr(_instance, 50 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_authenticate));
            Marshal.WriteIntPtr(_instance, 51 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_verifyCert));
            Marshal.WriteIntPtr(_instance, 55 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_postDisconnect));
            Marshal.WriteIntPtr(_instance, 66 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_verifyCertEx));

            // Context initialization
            bool contextOk = NativeFreeRdp.freerdp_context_new(_instance);
            if (!contextOk)
            {
                throw new InvalidOperationException("Failed to initialize FreeRDP context via freerdp_context_new.");
            }

            _context = Marshal.ReadIntPtr(_instance, 0); // instance->context
            if (_context == IntPtr.Zero)
            {
                throw new InvalidOperationException("FreeRDP context pointer is NULL after freerdp_context_new.");
            }

            // Resolve settings pointer
            _settings = ResolveSettings(_instance, _context);
            if (_settings == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to locate rdpSettings pointer in FreeRDP instance/context.");
            }

            _input = ResolveInput(_instance, _context);
            _update = ResolveUpdate(_instance, _context);

            // Configure connection settings
            ApplySettings();

            // Connect
            bool ok = NativeFreeRdp.freerdp_connect(_instance);
            if (!ok || _stopRequested)
            {
                uint err = _context != IntPtr.Zero ? NativeFreeRdp.freerdp_get_last_error(_context) : 0;
                IntPtr errStrPtr = NativeFreeRdp.freerdp_get_last_error_string(err);
                string errStr = errStrPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(errStrPtr) ?? "" : "";
                failureReason = string.IsNullOrWhiteSpace(errStr)
                    ? $"Connection failed (0x{err:X}). Target: {Config.Host}:{Config.Port}."
                    : $"Connection failed ({errStr}, 0x{err:X}). Target: {Config.Host}:{Config.Port}.";
                global::Android.Util.Log.Error("RDPVault", failureReason);
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
            global::Android.Util.Log.Error("RDPVault", "FreeRDP session loop error: " + ex);
            ConnectionFailed?.Invoke(failureReason);
        }
        finally
        {
            if (Config.PasswordChars != null)
            {
                Array.Clear(Config.PasswordChars, 0, Config.PasswordChars.Length);
            }
            IsConnected = false;
            CleanupNative();
            Disconnected?.Invoke();
        }
    }

    private static IntPtr ResolveSettings(IntPtr instance, IntPtr context)
    {
        // In FreeRDP 2.x, rdpContext.settings is at offset 40, and rdp_freerdp.settings is at offset 18
        if (context != IntPtr.Zero)
        {
            IntPtr ptr = Marshal.ReadIntPtr(context, 40 * IntPtr.Size);
            if (ptr != IntPtr.Zero) return ptr;
        }

        if (instance != IntPtr.Zero)
        {
            IntPtr ptr = Marshal.ReadIntPtr(instance, 18 * IntPtr.Size);
            if (ptr != IntPtr.Zero) return ptr;
        }

        // Defensive scan in context
        if (context != IntPtr.Zero)
        {
            for (int i = 35; i <= 45; i++)
            {
                IntPtr candidate = Marshal.ReadIntPtr(context, i * IntPtr.Size);
                if (candidate != IntPtr.Zero && CanReadSettings(candidate))
                {
                    return candidate;
                }
            }
        }

        // Defensive scan in instance
        if (instance != IntPtr.Zero)
        {
            for (int i = 14; i <= 22; i++)
            {
                IntPtr candidate = Marshal.ReadIntPtr(instance, i * IntPtr.Size);
                if (candidate != IntPtr.Zero && CanReadSettings(candidate))
                {
                    return candidate;
                }
            }
        }

        return IntPtr.Zero;
    }

    private static bool CanReadSettings(IntPtr ptr)
    {
        try
        {
            int key = NativeFreeRdp.freerdp_settings_get_key_for_name("FreeRDP_ServerPort");
            if (key < 0) key = NativeFreeRdp.freerdp_settings_get_key_for_name("ServerPort");
            if (key < 0) key = NativeFreeRdp.FreeRDP_ServerPort;

            uint port = NativeFreeRdp.freerdp_settings_get_uint32(ptr, (nuint)key);
            if (port > 0 && port <= 65535) return true;

            int direct = Marshal.ReadInt32(ptr, NativeFreeRdp.FreeRDP_ServerPort * IntPtr.Size);
            return direct > 0 && direct <= 65535;
        }
        catch { }
        return false;
    }

    private static IntPtr ResolveInput(IntPtr instance, IntPtr context)
    {
        if (context != IntPtr.Zero)
        {
            IntPtr ptr = Marshal.ReadIntPtr(context, 38 * IntPtr.Size);
            if (ptr != IntPtr.Zero) return ptr;
        }
        if (instance != IntPtr.Zero)
        {
            IntPtr ptr = Marshal.ReadIntPtr(instance, 16 * IntPtr.Size);
            if (ptr != IntPtr.Zero) return ptr;
        }
        return IntPtr.Zero;
    }

    private static IntPtr ResolveUpdate(IntPtr instance, IntPtr context)
    {
        if (context != IntPtr.Zero)
        {
            IntPtr ptr = Marshal.ReadIntPtr(context, 39 * IntPtr.Size);
            if (ptr != IntPtr.Zero) return ptr;
        }
        if (instance != IntPtr.Zero)
        {
            IntPtr ptr = Marshal.ReadIntPtr(instance, 17 * IntPtr.Size);
            if (ptr != IntPtr.Zero) return ptr;
        }
        return IntPtr.Zero;
    }

    private static void SetSettingString(IntPtr settings, string name, nuint fallbackKey, string? val)
    {
        if (settings == IntPtr.Zero || val == null) return;
        string fName = name.StartsWith("FreeRDP_") ? name : "FreeRDP_" + name;
        string sName = name.StartsWith("FreeRDP_") ? name[8..] : name;

        try { NativeFreeRdp.freerdp_settings_set_value_for_name(settings, fName, val); } catch { }
        try { NativeFreeRdp.freerdp_settings_set_value_for_name(settings, sName, val); } catch { }

        int dyn = -1;
        try { dyn = NativeFreeRdp.freerdp_settings_get_key_for_name(fName); } catch { }
        if (dyn < 0) { try { dyn = NativeFreeRdp.freerdp_settings_get_key_for_name(sName); } catch { } }

        nuint key = dyn >= 0 ? (nuint)dyn : fallbackKey;
        try { NativeFreeRdp.freerdp_settings_set_string(settings, key, val); } catch { }
    }

    private static void SetSettingUint(IntPtr settings, string name, nuint fallbackKey, uint val)
    {
        if (settings == IntPtr.Zero) return;
        string fName = name.StartsWith("FreeRDP_") ? name : "FreeRDP_" + name;
        string sName = name.StartsWith("FreeRDP_") ? name[8..] : name;

        try { NativeFreeRdp.freerdp_settings_set_value_for_name(settings, fName, val.ToString()); } catch { }
        try { NativeFreeRdp.freerdp_settings_set_value_for_name(settings, sName, val.ToString()); } catch { }

        int dyn = -1;
        try { dyn = NativeFreeRdp.freerdp_settings_get_key_for_name(fName); } catch { }
        if (dyn < 0) { try { dyn = NativeFreeRdp.freerdp_settings_get_key_for_name(sName); } catch { } }

        nuint key = dyn >= 0 ? (nuint)dyn : fallbackKey;
        try { NativeFreeRdp.freerdp_settings_set_uint32(settings, key, val); } catch { }

        if (fallbackKey > 0)
        {
            try
            {
                Marshal.WriteInt32(settings, (int)fallbackKey * IntPtr.Size, (int)val);
            }
            catch { }
        }
    }

    private static void SetSettingBool(IntPtr settings, string name, nuint fallbackKey, bool val)
    {
        if (settings == IntPtr.Zero) return;
        string fName = name.StartsWith("FreeRDP_") ? name : "FreeRDP_" + name;
        string sName = name.StartsWith("FreeRDP_") ? name[8..] : name;

        try { NativeFreeRdp.freerdp_settings_set_value_for_name(settings, fName, val ? "TRUE" : "FALSE"); } catch { }
        try { NativeFreeRdp.freerdp_settings_set_value_for_name(settings, sName, val ? "TRUE" : "FALSE"); } catch { }

        int dyn = -1;
        try { dyn = NativeFreeRdp.freerdp_settings_get_key_for_name(fName); } catch { }
        if (dyn < 0) { try { dyn = NativeFreeRdp.freerdp_settings_get_key_for_name(sName); } catch { } }

        nuint key = dyn >= 0 ? (nuint)dyn : fallbackKey;
        try { NativeFreeRdp.freerdp_settings_set_bool(settings, key, val); } catch { }
    }

    public static (string User, string Domain) SplitUserAndDomain(string? rawUser, string? rawDomain)
    {
        string u = rawUser?.Trim() ?? "";
        string d = rawDomain?.Trim() ?? "";

        if (u.Contains('\\'))
        {
            var parts = u.Split('\\', 2);
            if (string.IsNullOrWhiteSpace(d))
            {
                d = parts[0].Trim();
            }
            u = parts[1].Trim();
        }
        else if (u.Contains('@'))
        {
            var parts = u.Split('@', 2);
            u = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(d))
            {
                d = parts[1].Trim();
            }
        }

        return (u, d);
    }

    private static void ParseCommandLineArgs(
        IntPtr settings,
        string host,
        int port,
        int width,
        int height,
        string? user,
        string? domain,
        char[]? passwordChars,
        bool allowClipboard,
        bool suppressCert,
        string? gateway)
    {
        if (settings == IntPtr.Zero) return;
        try
        {
            var args = new List<string> { "rdpvault" };
            if (port > 0)
            {
                args.Add($"/v:{host}:{port}");
                args.Add($"/port:{port}");
            }
            else
            {
                args.Add($"/v:{host}");
            }

            if (!string.IsNullOrEmpty(user)) args.Add($"/u:{user}");
            if (!string.IsNullOrEmpty(domain)) args.Add($"/d:{domain}");
            if (passwordChars != null && passwordChars.Length > 0)
            {
                args.Add($"/p:{new string(passwordChars)}");
            }
            if (width > 0 && height > 0) args.Add($"/size:{width}x{height}");
            args.Add(allowClipboard ? "+clipboard" : "-clipboard");
            if (suppressCert) args.Add("/cert:ignore");
            if (!string.IsNullOrEmpty(gateway)) args.Add($"/g:{gateway}");

            IntPtr[] argvPointers = new IntPtr[args.Count];
            try
            {
                for (int i = 0; i < args.Count; i++)
                {
                    argvPointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                }
                int status = NativeFreeRdp.freerdp_client_settings_parse_command_line(settings, args.Count, argvPointers, false);
                global::Android.Util.Log.Info("RDPVault", $"FreeRDP parse_command_line: status={status}, args=[/v:{host}:{port}, /u:{user}, /d:{domain}]");
            }
            finally
            {
                for (int i = 0; i < argvPointers.Length; i++)
                {
                    if (argvPointers[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(argvPointers[i]);
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "parse_command_line warning: " + ex.Message);
        }
    }

    private void ApplySettingsTo(IntPtr settings)
    {
        if (settings == IntPtr.Zero) return;

        // Clean target host (strip port or brackets if present)
        string targetHost = Config.Host.Trim();
        int targetPort = Config.Port > 0 ? Config.Port : 3389;
        if (targetHost.StartsWith('[') && targetHost.Contains(']'))
        {
            int end = targetHost.IndexOf(']');
            targetHost = targetHost.Substring(1, end - 1);
        }
        else if (targetHost.Contains(':') && targetHost.IndexOf(':') == targetHost.LastIndexOf(':'))
        {
            var parts = targetHost.Split(':');
            targetHost = parts[0];
            if (int.TryParse(parts[1], out int p) && p > 0) targetPort = p;
        }

        var (cleanUser, cleanDomain) = SplitUserAndDomain(Config.Username, Config.Domain);

        // 1. Invoke FreeRDP client CLI parser (sets /v:host:port, /u:user, /d:domain, /p:password)
        ParseCommandLineArgs(settings, targetHost, targetPort, RemoteWidth, RemoteHeight, cleanUser, cleanDomain, Config.PasswordChars, Config.AllowClipboard, Config.SuppressCertWarnings, Config.GatewayHost);

        // 2. Explicitly apply settings properties
        SetSettingString(settings, "ServerHostname", NativeFreeRdp.FreeRDP_ServerHostname, targetHost);
        SetSettingUint(settings, "ServerPort", NativeFreeRdp.FreeRDP_ServerPort, (uint)targetPort);

        if (!string.IsNullOrEmpty(cleanUser))
        {
            SetSettingString(settings, "Username", NativeFreeRdp.FreeRDP_Username, cleanUser);
        }

        if (!string.IsNullOrEmpty(cleanDomain))
        {
            SetSettingString(settings, "Domain", NativeFreeRdp.FreeRDP_Domain, cleanDomain);
        }
        else
        {
            SetSettingString(settings, "Domain", NativeFreeRdp.FreeRDP_Domain, "");
        }

        // Stream password directly in RAM and wipe immediately upon consumption
        if (Config.PasswordChars != null && Config.PasswordChars.Length > 0)
        {
            byte[] passBytes = Encoding.UTF8.GetBytes(Config.PasswordChars);
            try
            {
                string passStr = Encoding.UTF8.GetString(passBytes);
                SetSettingString(settings, "Password", NativeFreeRdp.FreeRDP_Password, passStr);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passBytes);
            }
        }

        // Dimensions and color
        SetSettingUint(settings, "DesktopWidth", NativeFreeRdp.FreeRDP_DesktopWidth, (uint)RemoteWidth);
        SetSettingUint(settings, "DesktopHeight", NativeFreeRdp.FreeRDP_DesktopHeight, (uint)RemoteHeight);
        SetSettingUint(settings, "ColorDepth", NativeFreeRdp.FreeRDP_ColorDepth, 32);

        // Strict default deny for clipboard sharing
        SetSettingBool(settings, "RedirectClipboard", NativeFreeRdp.FreeRDP_RedirectClipboard, Config.AllowClipboard);

        // Prohibit dynamic display resizing (avoid MS-RDPEDISP extension channel mismatch)
        SetSettingBool(settings, "SupportDisplayControl", NativeFreeRdp.FreeRDP_SupportDisplayControl, false);
        SetSettingBool(settings, "DynamicResolutionUpdate", NativeFreeRdp.FreeRDP_DynamicResolutionUpdate, false);
        SetSettingBool(settings, "SmartSizing", NativeFreeRdp.FreeRDP_SmartSizing, false);

        // Remote audio playback redirection enabled
        SetSettingBool(settings, "AudioPlayback", NativeFreeRdp.FreeRDP_AudioPlayback, true);

        // Certificate warning suppression
        SetSettingBool(settings, "IgnoreCertificate", NativeFreeRdp.FreeRDP_IgnoreCertificate, Config.SuppressCertWarnings);

        // RD Gateway if specified
        if (!string.IsNullOrWhiteSpace(Config.GatewayHost))
        {
            string gwHost = Config.GatewayHost.Trim();
            int gwPort = Config.GatewayPort > 0 ? Config.GatewayPort : 443;
            if (gwHost.Contains(':') && !gwHost.StartsWith('['))
            {
                var gwParts = gwHost.Split(':');
                gwHost = gwParts[0];
                if (int.TryParse(gwParts[1], out int gp) && gp > 0) gwPort = gp;
            }

            SetSettingString(settings, "GatewayHostname", NativeFreeRdp.FreeRDP_GatewayHostname, gwHost);
            SetSettingUint(settings, "GatewayPort", NativeFreeRdp.FreeRDP_GatewayPort, (uint)gwPort);
            SetSettingUint(settings, "GatewayUsageMethod", NativeFreeRdp.FreeRDP_GatewayUsageMethod, 1);
        }

        uint readBack = NativeFreeRdp.freerdp_settings_get_uint32(settings, (nuint)NativeFreeRdp.FreeRDP_ServerPort);
        int directMem = Marshal.ReadInt32(settings, NativeFreeRdp.FreeRDP_ServerPort * IntPtr.Size);
        global::Android.Util.Log.Info("RDPVault", $"FreeRDP settings applied: Host={targetHost}, Port={targetPort} (readBack={readBack}, directMem={directMem}), User={cleanUser}, Domain={cleanDomain}");
    }

    private void ApplySettings()
    {
        ApplySettingsTo(_settings);
    }

    private bool OnPreConnect(IntPtr instance)
    {
        IntPtr s = IntPtr.Zero;
        if (instance != IntPtr.Zero)
        {
            s = Marshal.ReadIntPtr(instance, 18 * IntPtr.Size);
        }
        if (s == IntPtr.Zero && _context != IntPtr.Zero)
        {
            s = Marshal.ReadIntPtr(_context, 40 * IntPtr.Size);
        }
        if (s == IntPtr.Zero) s = _settings;

        if (s != IntPtr.Zero)
        {
            ApplySettingsTo(s);
        }
        return true;
    }

    private bool OnAuthenticate(IntPtr instance, ref IntPtr username, ref IntPtr password, ref IntPtr domain)
    {
        try
        {
            var (cleanUser, cleanDomain) = SplitUserAndDomain(Config.Username, Config.Domain);

            if (!string.IsNullOrEmpty(cleanUser))
            {
                username = Marshal.StringToCoTaskMemUTF8(cleanUser);
            }
            if (!string.IsNullOrEmpty(cleanDomain))
            {
                domain = Marshal.StringToCoTaskMemUTF8(cleanDomain);
            }
            if (Config.PasswordChars != null && Config.PasswordChars.Length > 0)
            {
                password = Marshal.StringToCoTaskMemUTF8(new string(Config.PasswordChars));
            }
            global::Android.Util.Log.Info("RDPVault", $"FreeRDP OnAuthenticate provided: User={cleanUser}, Domain={cleanDomain}, HasPassword={(Config.PasswordChars != null && Config.PasswordChars.Length > 0)}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RDPVault", "OnAuthenticate warning: " + ex.Message);
        }
        return true;
    }

    private bool OnPostConnect(IntPtr instance)
    {
        // Initialize GDI 32bpp framebuffer with RGBA32 format matching Android bitmap buffer
        bool gdiOk = NativeFreeRdp.gdi_init(instance, NativeFreeRdp.PIXEL_FORMAT_RGBA32);
        if (!gdiOk)
        {
            gdiOk = NativeFreeRdp.gdi_init(instance, NativeFreeRdp.PIXEL_FORMAT_BGRA32);
        }
        if (!gdiOk)
        {
            gdiOk = NativeFreeRdp.gdi_init(instance, NativeFreeRdp.PIXEL_FORMAT_BGRX32);
        }
        AppLog.Info($"FreeRDP gdi_init status: {gdiOk}");

        IntPtr update = _update;
        if (update == IntPtr.Zero && _context != IntPtr.Zero)
        {
            update = Marshal.ReadIntPtr(_context, 39 * IntPtr.Size);
        }
        if (update == IntPtr.Zero && instance != IntPtr.Zero)
        {
            update = Marshal.ReadIntPtr(instance, 17 * IntPtr.Size);
        }

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
        if (Config.SuppressCertWarnings) return 2; // Accept for session
        if (!string.IsNullOrEmpty(Config.PinnedFingerprint) &&
            string.Equals(Config.PinnedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return 1; // Accept and match pinned
        }
        return 0; // Reject
    }

    private uint OnVerifyCertificateEx(
        IntPtr instance,
        string host,
        ushort port,
        string commonName,
        string subject,
        string issuer,
        string fingerprint,
        uint flags)
    {
        if (Config.SuppressCertWarnings) return 2; // Accept for session
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

                if (w > 0 && h > 0 && buffer != IntPtr.Zero && stride > 0)
                {
                    RemoteWidth = w;
                    RemoteHeight = h;
                    FramebufferUpdated?.Invoke(0, 0, w, h, buffer, stride);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("OnEndPaint exception: " + ex.Message);
        }

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
