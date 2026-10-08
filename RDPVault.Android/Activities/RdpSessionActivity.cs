using System;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using RDPVault.Android.Platform;
using RDPVault.Android.Rdp;
using RDPVault.Android.Services;

namespace RDPVault.Android.Activities;

[Activity(
    Label = "Remote Desktop",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.FontScale,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ScreenOrientation = ScreenOrientation.Sensor,
    Theme = "@style/Theme.AppCompat.NoActionBar.FullScreen"
)]
public class RdpSessionActivity : AppCompatActivity, ISurfaceHolderCallback, View.IOnTouchListener
{
    private FreeRdpSession? _session;
    private SurfaceView? _surfaceView;
    private ISurfaceHolder? _surfaceHolder;
    private Bitmap? _frameBitmap;
    private byte[]? _pixelBuffer;

    // Viewport transform
    private float _scale = 1.0f;
    private float _panX = 0f;
    private float _panY = 0f;
    private int _screenWidth;
    private int _screenHeight;

    // Virtual mouse pointer & trackpad state
    private float _cursorX = 960f;
    private float _cursorY = 540f;
    private bool _directTouchMode = false;
    private bool _isDragging = false;
    private long _lastTapTime = 0;
    private float _lastTouchX = 0;
    private float _lastTouchY = 0;
    private int _touchPointerCount = 0;
    private ScaleGestureDetector? _scaleDetector;

    // UI elements
    private FrameLayout? _rootLayout;
    private LinearLayout? _modifierDrawer;
    private View? _cursorView;
    private View? _scrollWheelBar;
    private View? _loadingOverlay;
    private ProgressBar? _progressBar;
    private TextView? _statusText;
    private Button? _topModeBtn;
    private Button? _drawerModeBtn;

    // Modifier states
    private bool _ctrlActive = false;
    private bool _altActive = false;
    private bool _shiftActive = false;
    private bool _winActive = false;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Keep screen awake continuously during remote session
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        ApplyImmersiveFullscreen();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            var bounds = WindowManager?.CurrentWindowMetrics?.Bounds;
            _screenWidth = bounds?.Width() ?? Resources?.DisplayMetrics?.WidthPixels ?? 1920;
            _screenHeight = bounds?.Height() ?? Resources?.DisplayMetrics?.HeightPixels ?? 1080;
        }
        else
        {
            _screenWidth = Resources?.DisplayMetrics?.WidthPixels ?? 1920;
            _screenHeight = Resources?.DisplayMetrics?.HeightPixels ?? 1080;
        }

        _rootLayout = new FrameLayout(this)
        {
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent)
        };
        _rootLayout.SetBackgroundColor(Color.Black);
        _rootLayout.SetOnTouchListener(this);

        _surfaceView = new SurfaceView(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent,
                FrameLayout.LayoutParams.MatchParent)
        };
        _surfaceView.Holder?.AddCallback(this);
        _surfaceView.SetOnTouchListener(this);
        _rootLayout.AddView(_surfaceView);

        // Floating cursor indicator (virtual mouse pointer)
        _cursorView = CreateCursorView();
        _rootLayout.AddView(_cursorView);

        // Virtual scroll wheel edge slider
        _scrollWheelBar = CreateScrollWheelBar();
        _rootLayout.AddView(_scrollWheelBar);

        // Desktop modifier drawer
        _modifierDrawer = CreateModifierDrawer();
        _rootLayout.AddView(_modifierDrawer);

        // Top controls bar (Keys + Mode toggle)
        var topControls = CreateTopControls();
        _rootLayout.AddView(topControls);

        // Connecting progress overlay
        _loadingOverlay = CreateLoadingOverlay(out _progressBar, out _statusText);
        _rootLayout.AddView(_loadingOverlay);

        SetContentView(_rootLayout);

        _scaleDetector = new ScaleGestureDetector(this, new ScaleListener(this));

        // Consume in-memory configuration without exposing to Intent extras
        var config = RdpSessionBridge.ConsumePendingConfig();
        if (config == null)
        {
            Toast.MakeText(this, "Session configuration expired.", ToastLength.Short)?.Show();
            Finish();
            return;
        }

        _cursorX = config.Width / 2f;
        _cursorY = config.Height / 2f;

        InitializeAndConnect(config);
    }

    private void ApplyImmersiveFullscreen()
    {
        try
        {
            if (Window != null)
            {
                var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
                if (controller != null)
                {
                    controller.Hide(WindowInsetsCompat.Type.SystemBars());
                    controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
                }
                else
                {
#pragma warning disable CS0618
                    Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                        SystemUiFlags.ImmersiveSticky |
                        SystemUiFlags.HideNavigation |
                        SystemUiFlags.Fullscreen |
                        SystemUiFlags.LayoutHideNavigation |
                        SystemUiFlags.LayoutFullscreen |
                        SystemUiFlags.LayoutStable);
#pragma warning restore CS0618
                }
            }
        }
        catch { }
    }

    private void InitializeAndConnect(RdpSessionConfig config)
    {
        _session = new FreeRdpSession(config);
        RdpSessionBridge.ActiveSession = _session;

        _session.Connected += OnSessionConnected;
        _session.ConnectionFailed += OnSessionConnectionFailed;
        _session.Disconnected += OnSessionDisconnected;
        _session.FramebufferUpdated += OnFramebufferUpdated;

        // Start Foreground Service notification
        var serviceIntent = new Intent(this, typeof(RdpSessionService));
        serviceIntent.SetAction(RdpSessionService.ActionStartSession);
        serviceIntent.PutExtra(RdpSessionService.ExtraProfileName, config.ProfileName);
        serviceIntent.PutExtra(RdpSessionService.ExtraProfileHost, config.Host);
        serviceIntent.PutExtra(RdpSessionService.ExtraProfilePort, config.Port);
        StartService(serviceIntent);

        _session.Start();
    }

    private void OnSessionConnected()
    {
        RdpSessionBridge.IsConnected = true;
        RdpSessionBridge.ConnectedProfile = _session?.Config.Profile;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionConnected(_session?.Config.Profile);
        RunOnUiThread(() =>
        {
            if (_loadingOverlay != null) _loadingOverlay.Visibility = ViewStates.Gone;
            if (_progressBar != null) _progressBar.Visibility = ViewStates.Gone;
            if (_statusText != null) _statusText.Visibility = ViewStates.Gone;
            Toast.MakeText(this, "Connected", ToastLength.Short)?.Show();
            UpdateCursorPosition();
        });
    }

    private void OnSessionConnectionFailed(string reason)
    {
        RdpSessionBridge.IsConnected = false;
        RdpSessionBridge.ConnectedProfile = null;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionEnded();
        RunOnUiThread(() =>
        {
            Toast.MakeText(this, $"Connection failed: {reason}", ToastLength.Long)?.Show();
            Finish();
        });
    }

    private void OnSessionDisconnected()
    {
        RdpSessionBridge.IsConnected = false;
        RdpSessionBridge.ConnectedProfile = null;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionEnded();
        RunOnUiThread(() =>
        {
            Finish();
        });
    }

    private void OnFramebufferUpdated(int x, int y, int width, int height, IntPtr buffer, int stride)
    {
        var holder = _surfaceHolder;
        if (holder?.Surface?.IsValid != true || buffer == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0) return;

        try
        {
            int requiredBytes = stride * height;
            int pixelBytes = width * height * 4;

            if (_pixelBuffer == null || _pixelBuffer.Length != requiredBytes)
            {
                _pixelBuffer = new byte[requiredBytes];
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, _pixelBuffer, 0, requiredBytes);

            if (_frameBitmap == null || _frameBitmap.Width != width || _frameBitmap.Height != height || _frameBitmap.IsRecycled)
            {
                _frameBitmap?.Recycle();
                _frameBitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
            }

            if (_frameBitmap != null)
            {
                if (stride == width * 4)
                {
                    var byteBuffer = Java.Nio.ByteBuffer.Wrap(_pixelBuffer, 0, pixelBytes);
                    _frameBitmap.CopyPixelsFromBuffer(byteBuffer);
                }
                else
                {
                    byte[] packed = new byte[pixelBytes];
                    int rowBytes = width * 4;
                    for (int row = 0; row < height; row++)
                    {
                        Buffer.BlockCopy(_pixelBuffer, row * stride, packed, row * rowBytes, rowBytes);
                    }
                    var byteBuffer = Java.Nio.ByteBuffer.Wrap(packed);
                    _frameBitmap.CopyPixelsFromBuffer(byteBuffer);
                }

                var canvas = holder.LockCanvas();
                if (canvas != null)
                {
                    try
                    {
                        int canvasW = canvas.Width;
                        int canvasH = canvas.Height;
                        if (canvasW > 0 && canvasH > 0)
                        {
                            _screenWidth = canvasW;
                            _screenHeight = canvasH;
                        }

                        canvas.DrawColor(Color.Black);

                        float scaleX = (float)canvasW / width;
                        float scaleY = (float)canvasH / height;
                        float baseScale = Math.Min(scaleX, scaleY);

                        float fitW = width * baseScale;
                        float fitH = height * baseScale;
                        float offsetX = (canvasW - fitW) / 2f;
                        float offsetY = (canvasH - fitH) / 2f;

                        // Clamp pan offsets so the desktop cannot drift away or be pushed off-screen
                        if (_scale <= 1.0f)
                        {
                            _panX = 0f;
                            _panY = 0f;
                        }
                        else
                        {
                            float maxPanX = Math.Max(0, (fitW * _scale - canvasW) / 2f);
                            float maxPanY = Math.Max(0, (fitH * _scale - canvasH) / 2f);
                            _panX = Math.Clamp(_panX, -maxPanX, maxPanX);
                            _panY = Math.Clamp(_panY, -maxPanY, maxPanY);
                        }

                        var matrix = new Matrix();
                        matrix.PostScale(baseScale * _scale, baseScale * _scale);
                        matrix.PostTranslate(offsetX + _panX, offsetY + _panY);

                        var paint = new Paint { FilterBitmap = true, AntiAlias = true };
                        canvas.DrawBitmap(_frameBitmap, matrix, paint);
                    }
                    finally
                    {
                        holder.UnlockCanvasAndPost(canvas);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("OnFramebufferUpdated error: " + ex.Message);
        }
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        _surfaceHolder = holder;
    }

    public void SurfaceChanged(ISurfaceHolder holder, [global::Android.Runtime.GeneratedEnum] Format format, int width, int height)
    {
        _surfaceHolder = holder;
        if (width > 0 && height > 0)
        {
            _screenWidth = width;
            _screenHeight = height;
        }
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _surfaceHolder = null;
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyImmersiveFullscreen();

        // Screen rotated: reset scale and pan so the desktop neatly centers and fits to screen
        _scale = 1.0f;
        _panX = 0f;
        _panY = 0f;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            var bounds = WindowManager?.CurrentWindowMetrics?.Bounds;
            if (bounds != null && bounds.Width() > 0 && bounds.Height() > 0)
            {
                _screenWidth = bounds.Width();
                _screenHeight = bounds.Height();
            }
        }
        else
        {
            var dm = Resources?.DisplayMetrics;
            if (dm != null && dm.WidthPixels > 0 && dm.HeightPixels > 0)
            {
                _screenWidth = dm.WidthPixels;
                _screenHeight = dm.HeightPixels;
            }
        }

        UpdateCursorPosition();
    }

    // Touch and Gesture Interaction Engine
    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e == null || _session == null || !_session.IsConnected) return false;

        _scaleDetector?.OnTouchEvent(e);
        int action = (int)e.ActionMasked;
        _touchPointerCount = e.PointerCount;

        float tx = e.GetX();
        float ty = e.GetY();

        if (_touchPointerCount >= 2 && action == (int)MotionEventActions.PointerDown)
        {
            // Two-finger tap = Right Click
            TriggerHaptic();
            ushort rx, ry;
            if (_directTouchMode)
            {
                ScreenToRemote(tx, ty, out float rxf, out float ryf);
                rx = (ushort)rxf;
                ry = (ushort)ryf;
            }
            else
            {
                rx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                ry = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
            }

            _session.SendMouseMove(rx, ry);
            _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON2, true, rx, ry);
            Task.Delay(35).ContinueWith(_ =>
            {
                _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON2, false, rx, ry);
            });
            return true;
        }

        switch (action)
        {
            case (int)MotionEventActions.Down:
                _lastTouchX = tx;
                _lastTouchY = ty;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (_directTouchMode)
                {
                    ScreenToRemote(tx, ty, out _cursorX, out _cursorY);
                    ushort cx = (ushort)_cursorX;
                    ushort cy = (ushort)_cursorY;
                    _session.SendMouseMove(cx, cy);
                    _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, true, cx, cy);
                    _isDragging = true;
                }
                else
                {
                    if (now - _lastTapTime < 300)
                    {
                        // Double tap and hold = Left Click Drag
                        _isDragging = true;
                        TriggerHaptic();
                        ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                        _session.SendMouseMove(cx, cy);
                        _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, true, cx, cy);
                    }
                    _lastTapTime = now;
                }
                break;

            case (int)MotionEventActions.Move:
                float dx = tx - _lastTouchX;
                float dy = ty - _lastTouchY;
                _lastTouchX = tx;
                _lastTouchY = ty;

                if (_touchPointerCount == 1)
                {
                    if (_directTouchMode)
                    {
                        ScreenToRemote(tx, ty, out _cursorX, out _cursorY);
                    }
                    else
                    {
                        // Relative virtual trackpad with acceleration
                        float speed = (float)Math.Sqrt(dx * dx + dy * dy);
                        float accel = speed > 15f ? 1.6f : 1.1f;
                        _cursorX += dx * accel;
                        _cursorY += dy * accel;
                    }

                    _cursorX = Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                    _cursorY = Math.Clamp(_cursorY, 0, _session.RemoteHeight);

                    ushort mx = (ushort)_cursorX;
                    ushort my = (ushort)_cursorY;
                    _session.SendMouseMove(mx, my);
                    UpdateCursorPosition();
                }
                else if (_touchPointerCount == 2 && !_scaleDetector!.IsInProgress && _scale > 1.05f)
                {
                    // Two finger drag = Panning (only active when zoomed in)
                    _panX += dx;
                    _panY += dy;
                }
                break;

            case (int)MotionEventActions.Up:
            case (int)MotionEventActions.Cancel:
                if (_directTouchMode)
                {
                    ScreenToRemote(tx, ty, out _cursorX, out _cursorY);
                    ushort cx = (ushort)_cursorX;
                    ushort cy = (ushort)_cursorY;
                    _session.SendMouseMove(cx, cy);
                    _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                    _isDragging = false;
                }
                else
                {
                    if (_isDragging)
                    {
                        _isDragging = false;
                        ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                        _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                    }
                    else
                    {
                        // Single tap = Left Click
                        TriggerHaptic();
                        ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                        _session.SendMouseMove(cx, cy);
                        _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, true, cx, cy);
                        Task.Delay(35).ContinueWith(_ =>
                        {
                            _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                        });
                    }
                }
                break;
        }

        return true;
    }

    private void ScreenToRemote(float sx, float sy, out float rx, out float ry)
    {
        int rw = _session?.RemoteWidth > 0 ? _session.RemoteWidth : 1920;
        int rh = _session?.RemoteHeight > 0 ? _session.RemoteHeight : 1080;

        float scaleX = (float)_screenWidth / rw;
        float scaleY = (float)_screenHeight / rh;
        float baseScale = Math.Min(scaleX, scaleY);

        float fitW = rw * baseScale;
        float fitH = rh * baseScale;
        float offsetX = (_screenWidth - fitW) / 2f + _panX;
        float offsetY = (_screenHeight - fitH) / 2f + _panY;

        rx = (sx - offsetX) / (baseScale * _scale);
        ry = (sy - offsetY) / (baseScale * _scale);

        rx = Math.Clamp(rx, 0, rw);
        ry = Math.Clamp(ry, 0, rh);
    }

    private void RemoteToScreen(float rx, float ry, out float sx, out float sy)
    {
        int rw = _session?.RemoteWidth > 0 ? _session.RemoteWidth : 1920;
        int rh = _session?.RemoteHeight > 0 ? _session.RemoteHeight : 1080;

        float scaleX = (float)_screenWidth / rw;
        float scaleY = (float)_screenHeight / rh;
        float baseScale = Math.Min(scaleX, scaleY);

        float fitW = rw * baseScale;
        float fitH = rh * baseScale;
        float offsetX = (_screenWidth - fitW) / 2f + _panX;
        float offsetY = (_screenHeight - fitH) / 2f + _panY;

        sx = rx * (baseScale * _scale) + offsetX;
        sy = ry * (baseScale * _scale) + offsetY;
    }

    private void UpdateCursorPosition()
    {
        if (_cursorView == null) return;
        RemoteToScreen(_cursorX, _cursorY, out float sx, out float sy);
        _cursorView.TranslationX = sx;
        _cursorView.TranslationY = sy;
        _cursorView.Visibility = _directTouchMode ? ViewStates.Gone : ViewStates.Visible;
    }

    private void TriggerHaptic()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var vibrator = (Vibrator?)GetSystemService(VibratorService);
                vibrator?.Vibrate(VibrationEffect.CreatePredefined(VibrationEffect.EffectClick));
            }
            else
            {
                var vibrator = (Vibrator?)GetSystemService(VibratorService);
                vibrator?.Vibrate(15);
            }
        }
        catch { }
    }

    // UI Controls Factory
    private View CreateCursorView()
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        int sizePx = Math.Max(32, (int)(24 * density));
        var cursor = new ImageView(this)
        {
            Clickable = false,
            Focusable = false,
            LayoutParameters = new FrameLayout.LayoutParams(sizePx, sizePx)
        };

        var bmp = Bitmap.CreateBitmap(sizePx, sizePx, Bitmap.Config.Argb8888!);
        var canvas = new Canvas(bmp);
        var path = new global::Android.Graphics.Path();
        float scale = sizePx / 24f;
        path.MoveTo(0 * scale, 0 * scale);
        path.LineTo(0 * scale, 20 * scale);
        path.LineTo(6 * scale, 15 * scale);
        path.LineTo(11 * scale, 23 * scale);
        path.LineTo(14 * scale, 21 * scale);
        path.LineTo(9 * scale, 13 * scale);
        path.LineTo(16 * scale, 13 * scale);
        path.Close();

        var fillPaint = new Paint { Color = Color.White, AntiAlias = true };
        fillPaint.SetStyle(Paint.Style.Fill);
        var strokePaint = new Paint { Color = Color.Black, AntiAlias = true, StrokeWidth = Math.Max(2f, 2f * scale) };
        strokePaint.SetStyle(Paint.Style.Stroke);

        canvas.DrawPath(path, fillPaint);
        canvas.DrawPath(path, strokePaint);

        cursor.SetImageBitmap(bmp);
        return cursor;
    }

    private View CreateScrollWheelBar()
    {
        var bar = new View(this);
        var lp = new FrameLayout.LayoutParams(40, 300)
        {
            Gravity = GravityFlags.Right | GravityFlags.CenterVertical,
            RightMargin = 8
        };
        bar.LayoutParameters = lp;
        bar.SetBackgroundColor(Color.Argb(80, 255, 255, 255));

        float lastY = 0;
        bar.SetOnTouchListener(new CustomTouchListener((v, e) =>
        {
            if (e.Action == MotionEventActions.Down)
            {
                lastY = e.GetY();
                return true;
            }
            if (e.Action == MotionEventActions.Move)
            {
                float delta = e.GetY() - lastY;
                if (Math.Abs(delta) > 16)
                {
                    bool up = delta < 0;
                    TriggerHaptic();
                    ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session?.RemoteWidth ?? 1920);
                    ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session?.RemoteHeight ?? 1080);
                    _session?.SendMouseWheel(up, 120, cx, cy);
                    lastY = e.GetY();
                }
                return true;
            }
            return false;
        }));

        return bar;
    }

    private View CreateTopControls()
    {
        var container = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
                TopMargin = 8
            }
        };

        var keysBtn = new Button(this)
        {
            Text = "☰ Keys",
            TextSize = 11,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, 80)
        };
        keysBtn.SetBackgroundColor(Color.Argb(180, 20, 20, 25));
        keysBtn.SetTextColor(Color.White);
        keysBtn.Click += (_, _) =>
        {
            if (_modifierDrawer != null)
            {
                _modifierDrawer.Visibility = _modifierDrawer.Visibility == ViewStates.Visible
                    ? ViewStates.Gone
                    : ViewStates.Visible;
            }
        };
        container.AddView(keysBtn);

        _topModeBtn = new Button(this)
        {
            Text = _directTouchMode ? "👆 Touch" : "🖱 Trackpad",
            TextSize = 11,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, 80)
            {
                LeftMargin = 8
            }
        };
        _topModeBtn.SetBackgroundColor(Color.Argb(180, 20, 20, 25));
        _topModeBtn.SetTextColor(Color.White);
        _topModeBtn.Click += (_, _) => ToggleInputMode();
        container.AddView(_topModeBtn);

        return container;
    }

    private void ToggleInputMode()
    {
        _directTouchMode = !_directTouchMode;
        string modeLabel = _directTouchMode ? "👆 Touch" : "🖱 Trackpad";
        if (_topModeBtn != null) _topModeBtn.Text = modeLabel;
        if (_drawerModeBtn != null) _drawerModeBtn.Text = _directTouchMode ? "Touch Mode" : "Trackpad Mode";
        UpdateCursorPosition();
        Toast.MakeText(this, _directTouchMode ? "Direct Touch Mode" : "Virtual Trackpad Mode", ToastLength.Short)?.Show();
    }

    private LinearLayout CreateModifierDrawer()
    {
        var drawer = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Visibility = ViewStates.Gone,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top
            }
        };
        drawer.SetPadding(12, 12, 12, 12);
        drawer.SetBackgroundColor(Color.Argb(230, 14, 14, 16));

        // Row 1: Esc, Tab, Ctrl, Alt, Shift, Win, CAD, Disconnect
        var row1 = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        AddKeyButton(row1, "Esc", () => SendScanCode(0x01));
        AddKeyButton(row1, "Tab", () => SendScanCode(0x0F));
        var btnCtrl = AddToggleKeyButton(row1, "Ctrl", ref _ctrlActive, 0x1D);
        var btnAlt = AddToggleKeyButton(row1, "Alt", ref _altActive, 0x38);
        var btnShift = AddToggleKeyButton(row1, "Shift", ref _shiftActive, 0x2A);
        var btnWin = AddToggleKeyButton(row1, "Win", ref _winActive, 0x5B, extended: true);
        AddKeyButton(row1, "Ctrl+Alt+Del", SendCtrlAltDel);

        var btnDisc = new Button(this) { Text = "✕ Disconnect", TextSize = 11 };
        btnDisc.SetTextColor(Color.Red);
        btnDisc.SetBackgroundColor(Color.Argb(180, 50, 20, 20));
        btnDisc.Click += (_, _) =>
        {
            _session?.Disconnect();
            Finish();
        };
        row1.AddView(btnDisc);
        drawer.AddView(row1);

        // Row 2: F1 - F12
        var row2 = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        ushort[] fCodes = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (int i = 0; i < 12; i++)
        {
            ushort code = fCodes[i];
            AddKeyButton(row2, $"F{i + 1}", () => SendScanCode(code));
        }
        drawer.AddView(row2);

        // Row 3: Soft keyboard, Mouse Mode toggle, Zoom 1:1 Reset
        var row3 = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        AddKeyButton(row3, "⌨ Keyboard", ToggleSoftKeyboard);
        _drawerModeBtn = new Button(this)
        {
            Text = _directTouchMode ? "Touch Mode" : "Trackpad Mode",
            TextSize = 10
        };
        _drawerModeBtn.SetBackgroundColor(Color.Argb(180, 35, 35, 42));
        _drawerModeBtn.SetTextColor(Color.White);
        _drawerModeBtn.Click += (_, _) => ToggleInputMode();
        row3.AddView(_drawerModeBtn);

        AddKeyButton(row3, "🔍 1:1 Reset", () =>
        {
            _scale = 1.0f;
            _panX = 0f;
            _panY = 0f;
        });
        drawer.AddView(row3);

        return drawer;
    }

    private void AddKeyButton(LinearLayout row, string label, Action action)
    {
        var btn = new Button(this) { Text = label, TextSize = 10 };
        btn.SetBackgroundColor(Color.Argb(180, 35, 35, 42));
        btn.SetTextColor(Color.White);
        btn.Click += (_, _) => action();
        row.AddView(btn);
    }

    private Button AddToggleKeyButton(LinearLayout row, string label, ref bool flagRef, ushort code, bool extended = false)
    {
        var btn = new Button(this) { Text = label, TextSize = 10 };
        btn.SetBackgroundColor(Color.Argb(180, 35, 35, 42));
        btn.SetTextColor(Color.White);

        btn.Click += (_, _) =>
        {
            if (label == "Ctrl") _ctrlActive = !_ctrlActive;
            else if (label == "Alt") _altActive = !_altActive;
            else if (label == "Shift") _shiftActive = !_shiftActive;
            else if (label == "Win") _winActive = !_winActive;

            bool active = (label == "Ctrl" && _ctrlActive) ||
                          (label == "Alt" && _altActive) ||
                          (label == "Shift" && _shiftActive) ||
                          (label == "Win" && _winActive);

            btn.SetBackgroundColor(active ? Color.Argb(220, 0, 95, 184) : Color.Argb(180, 35, 35, 42));
            _session?.SendKeyboardScanCode(code, active, extended);
        };
        row.AddView(btn);
        return btn;
    }

    private void SendScanCode(ushort code, bool extended = false)
    {
        _session?.SendKeyboardScanCode(code, true, extended);
        _session?.SendKeyboardScanCode(code, false, extended);
    }

    private void SendCtrlAltDel()
    {
        _session?.SendKeyboardScanCode(0x1D, true, false); // Ctrl down
        _session?.SendKeyboardScanCode(0x38, true, false); // Alt down
        _session?.SendKeyboardScanCode(0x53, true, true);  // Del down
        _session?.SendKeyboardScanCode(0x53, false, true); // Del up
        _session?.SendKeyboardScanCode(0x38, false, false);// Alt up
        _session?.SendKeyboardScanCode(0x1D, false, false);// Ctrl up
    }

    private void ToggleSoftKeyboard()
    {
        var imm = (InputMethodManager?)GetSystemService(InputMethodService);
        imm?.ToggleSoftInput(ShowFlags.Forced, HideSoftInputFlags.ImplicitOnly);
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e != null && e.Action == KeyEventActions.Multiple && !string.IsNullOrEmpty(e.Characters))
        {
            foreach (char c in e.Characters)
            {
                _session?.SendUnicodeChar(c);
            }
            return true;
        }

        if (e != null && e.Action == KeyEventActions.Down)
        {
            int unicode = e.GetUnicodeChar(e.MetaState);
            if (unicode > 0)
            {
                _session?.SendUnicodeChar((char)unicode);
                return true;
            }
        }
        return base.DispatchKeyEvent(e);
    }

    private View CreateLoadingOverlay(out ProgressBar? pb, out TextView? tv)
    {
        var layout = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Center
            }
        };
        pb = new ProgressBar(this) { Indeterminate = true };
        tv = new TextView(this)
        {
            Text = "Connecting to remote workstation...",
            TextSize = 14
        };
        tv.SetTextColor(Color.White);
        layout.AddView(pb);
        layout.AddView(tv);
        return layout;
    }

    protected override void OnDestroy()
    {
        RdpSessionBridge.IsConnected = false;
        RdpSessionBridge.ConnectedProfile = null;
        _session?.Dispose();
        _session = null;
        RdpSessionBridge.ActiveSession = null;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionEnded();
        _frameBitmap?.Recycle();
        _frameBitmap = null;
        _pixelBuffer = null;
        base.OnDestroy();
    }

    private class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly RdpSessionActivity _act;
        public ScaleListener(RdpSessionActivity act) => _act = act;

        public override bool OnScale(ScaleGestureDetector detector)
        {
            _act._scale *= detector.ScaleFactor;
            _act._scale = Math.Clamp(_act._scale, 1.0f, 4.0f);
            return true;
        }
    }

    private class CustomTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly Func<View?, MotionEvent, bool> _handler;
        public CustomTouchListener(Func<View?, MotionEvent, bool> handler) => _handler = handler;
        public bool OnTouch(View? v, MotionEvent? e) => e != null && _handler(v, e);
    }
}
