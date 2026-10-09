using System;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
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
public class RdpSessionActivity : AppCompatActivity, TextureView.ISurfaceTextureListener, View.IOnTouchListener
{
    private FreeRdpSession? _session;
    private TextureView? _textureView;
    private volatile bool _isSurfaceAvailable;
    private Bitmap? _frameBitmap;
    private byte[]? _pixelBuffer;
    private readonly object _renderLock = new();

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
    private bool _isRightClickArmed = false;
    private bool _isDragging = false;
    private long _lastTapTime = 0;
    private float _lastTouchX = 0;
    private float _lastTouchY = 0;
    private float _touchStartX = 0;
    private float _touchStartY = 0;
    private long _touchStartTime = 0;
    private bool _hasMoved = false;
    private bool _isTwoFingerGesture = false;
    private bool _isScaling = false;
    private float _prevFocusX = 0;
    private float _prevFocusY = 0;
    private ScaleGestureDetector? _scaleDetector;

    // UI elements
    private FrameLayout? _rootLayout;
    private View? _topControls;
    private LinearLayout? _topBar;
    private View? _collapsedHandle;
    private LinearLayout? _modifierDrawer;
    private View? _cursorView;
    private View? _scrollWheelBar;
    private View? _loadingOverlay;
    private ProgressBar? _progressBar;
    private TextView? _statusText;
    private Button? _topModeBtn;
    private Button? _clickFlipperBtn;

    // Smart collapse handler
    private readonly Handler _collapseHandler = new(Looper.MainLooper!);
    private Action? _collapseRunnable;

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

        UpdateScreenMetrics();

        _rootLayout = new FrameLayout(this)
        {
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent)
        };
        _rootLayout.SetBackgroundColor(Color.Black);

        // TextureView provides seamless hardware-accelerated rendering and rotation sync
        _textureView = new TextureView(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent,
                FrameLayout.LayoutParams.MatchParent)
        };
        _textureView.SurfaceTextureListener = this;
        _textureView.SetOnTouchListener(this);
        _rootLayout.AddView(_textureView);

        // Floating cursor indicator (virtual mouse pointer)
        _cursorView = CreateCursorView();
        _rootLayout.AddView(_cursorView);

        // Virtual scroll wheel edge slider
        _scrollWheelBar = CreateScrollWheelBar();
        _rootLayout.AddView(_scrollWheelBar);

        // Top controls container with smart auto-collapsing
        _topControls = CreateTopControls();
        _rootLayout.AddView(_topControls);

        // Desktop modifier drawer (anchored below top controls)
        _modifierDrawer = CreateModifierDrawer();
        _rootLayout.AddView(_modifierDrawer);

        // Connecting progress overlay
        _loadingOverlay = CreateLoadingOverlay(out _progressBar, out _statusText);
        _rootLayout.AddView(_loadingOverlay);

        SetContentView(_rootLayout);

        _scaleDetector = new ScaleGestureDetector(this, new ScaleListener(this));

        _collapseRunnable = () =>
        {
            if (_modifierDrawer != null && _modifierDrawer.Visibility == ViewStates.Visible) return;
            if (_topBar != null && _collapsedHandle != null)
            {
                _topBar.Visibility = ViewStates.Gone;
                _collapsedHandle.Visibility = ViewStates.Visible;
            }
        };

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

    private void UpdateScreenMetrics()
    {
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

        _session.Start();
    }

    private void OnSessionConnected()
    {
        RdpSessionBridge.IsConnected = true;
        RdpSessionBridge.ConnectedProfile = _session?.Config.Profile;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionConnected(_session?.Config.Profile);

        // Start Foreground Service notification strictly after successful connection
        if (_session != null)
        {
            var serviceIntent = new Intent(this, typeof(RdpSessionService));
            serviceIntent.SetAction(RdpSessionService.ActionStartSession);
            serviceIntent.PutExtra(RdpSessionService.ExtraProfileName, _session.Config.ProfileName);
            serviceIntent.PutExtra(RdpSessionService.ExtraProfileHost, _session.Config.Host);
            serviceIntent.PutExtra(RdpSessionService.ExtraProfilePort, _session.Config.Port);
            StartService(serviceIntent);
        }

        RunOnUiThread(() =>
        {
            if (_loadingOverlay != null) _loadingOverlay.Visibility = ViewStates.Gone;
            if (_progressBar != null) _progressBar.Visibility = ViewStates.Gone;
            if (_statusText != null) _statusText.Visibility = ViewStates.Gone;
            Toast.MakeText(this, "Connected", ToastLength.Short)?.Show();
            UpdateCursorPosition();
            RedrawCurrentFrame();
            ScheduleCollapseTimer();
        });
    }

    private void OnSessionConnectionFailed(string reason)
    {
        TearDownSessionNotification();
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
        TearDownSessionNotification();
        RdpSessionBridge.IsConnected = false;
        RdpSessionBridge.ConnectedProfile = null;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionEnded();
        RunOnUiThread(() =>
        {
            Finish();
        });
    }

    private void TearDownSessionNotification()
    {
        try
        {
            var endServiceIntent = new Intent(this, typeof(RdpSessionService));
            endServiceIntent.SetAction(RdpSessionService.ActionEndSession);
            StartService(endServiceIntent);
        }
        catch { }
    }

    private void OnFramebufferUpdated(int x, int y, int width, int height, IntPtr buffer, int stride)
    {
        if (!_isSurfaceAvailable || buffer == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0) return;

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
                    using var byteBuffer = Java.Nio.ByteBuffer.Wrap(_pixelBuffer, 0, pixelBytes);
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
                    using var byteBuffer = Java.Nio.ByteBuffer.Wrap(packed);
                    _frameBitmap.CopyPixelsFromBuffer(byteBuffer);
                }

                RenderBitmapToView(_frameBitmap);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("OnFramebufferUpdated error: " + ex.Message);
        }
    }

    /// <summary>
    /// Interactive frame renderer that draws the cached desktop bitmap onto TextureView canvas
    /// with strict aspect ratio matching, centered letterboxing/pillarboxing, and boundary-clamped pan/zoom.
    /// </summary>
    private void RenderBitmapToView(Bitmap? bmp)
    {
        if (bmp == null || bmp.IsRecycled || !_isSurfaceAvailable || _textureView == null) return;

        lock (_renderLock)
        {
            Canvas? canvas = null;
            try
            {
                canvas = _textureView.LockCanvas();
                if (canvas == null) return;

                int canvasW = canvas.Width;
                int canvasH = canvas.Height;
                if (canvasW <= 0 || canvasH <= 0) return;

                _screenWidth = canvasW;
                _screenHeight = canvasH;

                canvas.DrawColor(Color.Black);

                int rw = bmp.Width;
                int rh = bmp.Height;
                if (rw <= 0 || rh <= 0) return;

                float scaleX = (float)canvasW / rw;
                float scaleY = (float)canvasH / rh;
                float baseScale = Math.Min(scaleX, scaleY);

                float fitW = rw * baseScale;
                float fitH = rh * baseScale;
                float offsetX = (canvasW - fitW) / 2f;
                float offsetY = (canvasH - fitH) / 2f;

                float currentW = fitW * _scale;
                float currentH = fitH * _scale;

                if (currentW <= canvasW)
                {
                    _panX = 0f;
                }
                else
                {
                    float maxPanX = (currentW - canvasW) / 2f;
                    _panX = Math.Clamp(_panX, -maxPanX, maxPanX);
                }

                if (currentH <= canvasH)
                {
                    _panY = 0f;
                }
                else
                {
                    float maxPanY = (currentH - canvasH) / 2f;
                    _panY = Math.Clamp(_panY, -maxPanY, maxPanY);
                }

                using var matrix = new Matrix();
                matrix.PostScale(baseScale * _scale, baseScale * _scale);
                matrix.PostTranslate(offsetX + _panX, offsetY + _panY);

                using var paint = new Paint { FilterBitmap = true, AntiAlias = true };
                canvas.DrawBitmap(bmp, matrix, paint);
            }
            catch (Exception ex)
            {
                AppLog.Warn("RenderBitmapToView: " + ex.Message);
            }
            finally
            {
                if (canvas != null)
                {
                    try { _textureView.UnlockCanvasAndPost(canvas); } catch { }
                }
            }
        }
    }

    private void RedrawCurrentFrame()
    {
        if (_frameBitmap != null && !_frameBitmap.IsRecycled)
        {
            RenderBitmapToView(_frameBitmap);
        }
    }

    // TextureView.ISurfaceTextureListener implementation
    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        _isSurfaceAvailable = true;
        if (width > 0 && height > 0)
        {
            _screenWidth = width;
            _screenHeight = height;
        }
        RedrawCurrentFrame();
        UpdateCursorPosition();
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _screenWidth = width;
            _screenHeight = height;
        }
        _scale = 1.0f;
        _panX = 0f;
        _panY = 0f;
        RedrawCurrentFrame();
        UpdateCursorPosition();
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        _isSurfaceAvailable = false;
        return true;
    }

    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyImmersiveFullscreen();

        _scale = 1.0f;
        _panX = 0f;
        _panY = 0f;

        UpdateScreenMetrics();
        RedrawCurrentFrame();
        UpdateCursorPosition();
    }

    // Touch and Gesture Interaction Engine
    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e == null || _session == null || !_session.IsConnected) return false;

        // Native physical mouse pass-through
        if (e.GetToolType(0) == MotionEventToolType.Mouse || e.IsFromSource(InputSourceType.Mouse))
        {
            return HandleNativeMouseTouch(e);
        }

        _scaleDetector?.OnTouchEvent(e);

        int action = (int)e.ActionMasked;
        int pointerCount = e.PointerCount;
        float x = e.GetX();
        float y = e.GetY();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        switch (action)
        {
            case (int)MotionEventActions.Down:
                _touchStartTime = now;
                _touchStartX = x;
                _touchStartY = y;
                _lastTouchX = x;
                _lastTouchY = y;
                _hasMoved = false;
                _isTwoFingerGesture = false;

                if (_directTouchMode)
                {
                    ScreenToRemote(x, y, out _cursorX, out _cursorY);
                    ushort cx = (ushort)_cursorX;
                    ushort cy = (ushort)_cursorY;
                    _session.SendMouseMove(cx, cy);

                    ushort downFlag = _isRightClickArmed ? NativeFreeRdp.PTR_FLAGS_BUTTON2 : NativeFreeRdp.PTR_FLAGS_BUTTON1;
                    if (_isRightClickArmed)
                    {
                        _isRightClickArmed = false;
                        RunOnUiThread(UpdateClickFlipperUi);
                    }

                    _session.SendMouseButton(downFlag, true, cx, cy);
                    _isDragging = true;
                }
                else
                {
                    // Double-tap and hold initiates drag in trackpad mode
                    if (now - _lastTapTime < 300)
                    {
                        _isDragging = true;
                        TriggerHaptic();
                        ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                        _session.SendMouseMove(cx, cy);
                        _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, true, cx, cy);
                    }
                }
                break;

            case (int)MotionEventActions.PointerDown:
                if (pointerCount >= 2)
                {
                    _isTwoFingerGesture = true;
                    _prevFocusX = _scaleDetector?.FocusX ?? x;
                    _prevFocusY = _scaleDetector?.FocusY ?? y;
                }
                break;

            case (int)MotionEventActions.Move:
                float dx = x - _lastTouchX;
                float dy = y - _lastTouchY;
                _lastTouchX = x;
                _lastTouchY = y;

                float distFromStart = (float)Math.Sqrt(Math.Pow(x - _touchStartX, 2) + Math.Pow(y - _touchStartY, 2));
                if (distFromStart > 12)
                {
                    _hasMoved = true;
                }

                if (pointerCount == 1 && !_isTwoFingerGesture)
                {
                    if (_directTouchMode)
                    {
                        ScreenToRemote(x, y, out _cursorX, out _cursorY);
                        ushort cx = (ushort)_cursorX;
                        ushort cy = (ushort)_cursorY;
                        _session.SendMouseMove(cx, cy);
                    }
                    else
                    {
                        // Relative virtual trackpad with smooth acceleration
                        float speed = (float)Math.Sqrt(dx * dx + dy * dy);
                        float accel = speed > 20f ? 1.6f : 1.15f;
                        _cursorX += dx * accel;
                        _cursorY += dy * accel;
                        _cursorX = Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        _cursorY = Math.Clamp(_cursorY, 0, _session.RemoteHeight);

                        ushort mx = (ushort)_cursorX;
                        ushort my = (ushort)_cursorY;
                        _session.SendMouseMove(mx, my);
                        UpdateCursorPosition();

                        // Edge auto-scroll in zoomed-in mode
                        if (_scale > 1.05f)
                        {
                            CheckAndHandleEdgeAutoScroll();
                        }
                    }
                }
                else if (pointerCount >= 2)
                {
                    _hasMoved = true;
                    if (!_isScaling)
                    {
                        float focusX = _scaleDetector?.FocusX ?? x;
                        float focusY = _scaleDetector?.FocusY ?? y;
                        float fdx = focusX - _prevFocusX;
                        float fdy = focusY - _prevFocusY;
                        _prevFocusX = focusX;
                        _prevFocusY = focusY;

                        if (_scale > 1.05f)
                        {
                            // Two-finger drag pans the zoomed remote desktop viewport
                            _panX += fdx;
                            _panY += fdy;
                            RedrawCurrentFrame();
                            UpdateCursorPosition();
                        }
                        else
                        {
                            // Two-finger vertical swipe acts as mouse scroll wheel
                            if (Math.Abs(fdy) > 16)
                            {
                                bool up = fdy > 0;
                                ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                                ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                                _session.SendMouseWheel(up, 120, cx, cy);
                            }
                        }
                    }
                }
                break;

            case (int)MotionEventActions.PointerUp:
                if (_isTwoFingerGesture && !_hasMoved && !_isScaling)
                {
                    long duration = now - _touchStartTime;
                    if (duration < 350)
                    {
                        // Two-finger quick tap = Right Click
                        TriggerHaptic();
                        ushort rx, ry;
                        if (_directTouchMode)
                        {
                            ScreenToRemote(x, y, out float rxf, out float ryf);
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
                    }
                }
                break;

            case (int)MotionEventActions.Up:
                if (_directTouchMode)
                {
                    ScreenToRemote(x, y, out _cursorX, out _cursorY);
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
                    else if (!_isTwoFingerGesture && !_hasMoved)
                    {
                        // Quick single tap without movement = Click
                        long duration = now - _touchStartTime;
                        if (duration < 300)
                        {
                            TriggerHaptic();
                            ushort buttonFlag = _isRightClickArmed ? NativeFreeRdp.PTR_FLAGS_BUTTON2 : NativeFreeRdp.PTR_FLAGS_BUTTON1;
                            if (_isRightClickArmed)
                            {
                                _isRightClickArmed = false;
                                RunOnUiThread(UpdateClickFlipperUi);
                            }

                            ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                            ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                            _session.SendMouseMove(cx, cy);
                            _session.SendMouseButton(buttonFlag, true, cx, cy);
                            Task.Delay(35).ContinueWith(_ =>
                            {
                                _session?.SendMouseButton(buttonFlag, false, cx, cy);
                            });
                            _lastTapTime = now;
                        }
                    }
                }
                _isTwoFingerGesture = false;
                _hasMoved = false;
                break;

            case (int)MotionEventActions.Cancel:
                if (_isDragging)
                {
                    _isDragging = false;
                    ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                    ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                    _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                }
                _isTwoFingerGesture = false;
                _hasMoved = false;
                break;
        }

        return true;
    }

    private bool HandleNativeMouseTouch(MotionEvent e)
    {
        ScreenToRemote(e.GetX(), e.GetY(), out _cursorX, out _cursorY);
        ushort cx = (ushort)_cursorX;
        ushort cy = (ushort)_cursorY;
        _session?.SendMouseMove(cx, cy);
        UpdateCursorPosition();

        if (e.Action == MotionEventActions.Down)
        {
            if (e.ButtonState.HasFlag(MotionEventButtonState.Primary))
            {
                _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, true, cx, cy);
            }
            if (e.ButtonState.HasFlag(MotionEventButtonState.Secondary))
            {
                _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON2, true, cx, cy);
            }
        }
        else if (e.Action == MotionEventActions.Up)
        {
            _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
            _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON2, false, cx, cy);
        }
        return true;
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && _session != null && _session.IsConnected)
        {
            if (e.IsFromSource(InputSourceType.Mouse) || e.GetToolType(0) == MotionEventToolType.Mouse)
            {
                if (e.Action == MotionEventActions.Scroll)
                {
                    float vScroll = e.GetAxisValue(Axis.Vscroll);
                    if (Math.Abs(vScroll) > 0.01f)
                    {
                        bool up = vScroll > 0;
                        ScreenToRemote(e.GetX(), e.GetY(), out float rx, out float ry);
                        _session.SendMouseWheel(up, (ushort)(Math.Abs(vScroll) * 120), (ushort)rx, (ushort)ry);
                        return true;
                    }
                }
                else if (e.Action == MotionEventActions.HoverMove)
                {
                    ScreenToRemote(e.GetX(), e.GetY(), out _cursorX, out _cursorY);
                    _session.SendMouseMove((ushort)_cursorX, (ushort)_cursorY);
                    UpdateCursorPosition();
                    return true;
                }
            }
        }
        return base.OnGenericMotionEvent(e);
    }

    /// <summary>
    /// Intuitively glides the zoomed-in desktop viewport when the virtual cursor touches within
    /// 28dp of the screen bezel, strictly bounded by the actual remote PC screen boundaries.
    /// </summary>
    private void CheckAndHandleEdgeAutoScroll()
    {
        if (_scale <= 1.05f || _session == null || !_session.IsConnected) return;

        RemoteToScreen(_cursorX, _cursorY, out float sx, out float sy);
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        float edgeMargin = 28f * density;

        int rw = _session.RemoteWidth > 0 ? _session.RemoteWidth : 1920;
        int rh = _session.RemoteHeight > 0 ? _session.RemoteHeight : 1080;

        float scaleX = (float)_screenWidth / rw;
        float scaleY = (float)_screenHeight / rh;
        float baseScale = Math.Min(scaleX, scaleY);
        float fitW = rw * baseScale;
        float fitH = rh * baseScale;

        float maxPanX = Math.Max(0, (fitW * _scale - _screenWidth) / 2f);
        float maxPanY = Math.Max(0, (fitH * _scale - _screenHeight) / 2f);

        float panDeltaX = 0f;
        float panDeltaY = 0f;

        // Check horizontal screen edges
        if (sx < edgeMargin)
        {
            float dist = edgeMargin - sx;
            float speed = (float)Math.Min(24f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 20f);
            if (_panX < maxPanX) panDeltaX = speed;
        }
        else if (sx > _screenWidth - edgeMargin)
        {
            float dist = sx - (_screenWidth - edgeMargin);
            float speed = (float)Math.Min(24f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 20f);
            if (_panX > -maxPanX) panDeltaX = -speed;
        }

        // Check vertical screen edges
        if (sy < edgeMargin)
        {
            float dist = edgeMargin - sy;
            float speed = (float)Math.Min(24f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 20f);
            if (_panY < maxPanY) panDeltaY = speed;
        }
        else if (sy > _screenHeight - edgeMargin)
        {
            float dist = sy - (_screenHeight - edgeMargin);
            float speed = (float)Math.Min(24f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 20f);
            if (_panY > -maxPanY) panDeltaY = -speed;
        }

        if (panDeltaX != 0f || panDeltaY != 0f)
        {
            _panX = Math.Clamp(_panX + panDeltaX, -maxPanX, maxPanX);
            _panY = Math.Clamp(_panY + panDeltaY, -maxPanY, maxPanY);
            RedrawCurrentFrame();
            UpdateCursorPosition();
        }
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
        int sizePx = Math.Max(28, (int)(22 * density));
        var cursor = new ImageView(this)
        {
            Clickable = false,
            Focusable = false,
            LayoutParameters = new FrameLayout.LayoutParams(sizePx, sizePx)
        };

        var bmp = Bitmap.CreateBitmap(sizePx, sizePx, Bitmap.Config.Argb8888!);
        using var canvas = new Canvas(bmp);
        using var path = new global::Android.Graphics.Path();
        float scale = sizePx / 24f;
        path.MoveTo(0 * scale, 0 * scale);
        path.LineTo(0 * scale, 20 * scale);
        path.LineTo(6 * scale, 15 * scale);
        path.LineTo(11 * scale, 23 * scale);
        path.LineTo(14 * scale, 21 * scale);
        path.LineTo(9 * scale, 13 * scale);
        path.LineTo(16 * scale, 13 * scale);
        path.Close();

        using var fillPaint = new Paint { Color = Color.White, AntiAlias = true };
        fillPaint.SetStyle(Paint.Style.Fill);
        using var strokePaint = new Paint { Color = Color.Black, AntiAlias = true, StrokeWidth = Math.Max(2f, 2f * scale) };
        strokePaint.SetStyle(Paint.Style.Stroke);

        canvas.DrawPath(path, fillPaint);
        canvas.DrawPath(path, strokePaint);

        cursor.SetImageBitmap(bmp);
        return cursor;
    }

    private View CreateScrollWheelBar()
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        int barW = (int)(20 * density);
        int barH = (int)(160 * density);

        var bar = new View(this);
        var lp = new FrameLayout.LayoutParams(barW, barH)
        {
            Gravity = GravityFlags.Right | GravityFlags.CenterVertical,
            RightMargin = (int)(6 * density)
        };
        bar.LayoutParameters = lp;

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(10 * density);
        bg.SetColor(Color.Argb(80, 255, 255, 255));
        bg.SetStroke((int)(1 * density), Color.Argb(40, 0, 0, 0));
        bar.Background = bg;

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
                if (Math.Abs(delta) > 12)
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
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        int safeTop = (int)(28 * density);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            var insets = WindowManager?.CurrentWindowMetrics?.WindowInsets?.GetInsetsIgnoringVisibility(
                WindowInsetsCompat.Type.StatusBars() | WindowInsetsCompat.Type.DisplayCutout());
            if (insets != null && insets.Top > safeTop)
            {
                safeTop = insets.Top + (int)(4 * density);
            }
        }

        var container = new FrameLayout(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
                TopMargin = safeTop
            },
            Elevation = 20f
        };

        // Collapsed handle (mini-tab)
        int handleW = (int)(46 * density);
        int handleH = (int)(18 * density);
        var handle = new Button(this)
        {
            Text = "☰",
            TextSize = 10,
            LayoutParameters = new FrameLayout.LayoutParams(handleW, handleH)
            {
                Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal
            },
            Visibility = ViewStates.Gone
        };
        handle.SetPadding(0, 0, 0, 0);
        var handleBg = new GradientDrawable();
        handleBg.SetShape(ShapeType.Rectangle);
        handleBg.SetCornerRadius(9 * density);
        handleBg.SetColor(Color.Argb(160, 20, 20, 25));
        handleBg.SetStroke((int)(1 * density), Color.Argb(80, 255, 255, 255));
        handle.Background = handleBg;
        handle.SetTextColor(Color.White);
        handle.Click += (_, _) =>
        {
            if (_topBar != null && _collapsedHandle != null)
            {
                _topBar.Visibility = ViewStates.Visible;
                _collapsedHandle.Visibility = ViewStates.Gone;
                ScheduleCollapseTimer();
            }
        };
        _collapsedHandle = handle;
        container.AddView(_collapsedHandle);

        // Expanded full top bar
        var topBar = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal
            }
        };
        topBar.SetPadding((int)(8 * density), (int)(4 * density), (int)(8 * density), (int)(4 * density));

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(18 * density);
        bg.SetColor(Color.Argb(215, 20, 20, 25));
        bg.SetStroke((int)(1 * density), Color.Argb(80, 255, 255, 255));
        topBar.Background = bg;

        int btnHeight = (int)(32 * density);
        int padH = (int)(10 * density);

        // 1. Mode toggle (Mouse Pointer / Touchpad)
        _topModeBtn = CreatePillButton(_directTouchMode ? "👆 Touchpad" : "🖱 Pointer", btnHeight, padH, () =>
        {
            ToggleInputMode();
            ScheduleCollapseTimer();
        });
        topBar.AddView(_topModeBtn);

        // 2. Click Flipper Button (Left Click / Right Click)
        _clickFlipperBtn = CreatePillButton("🖱 Left Click", btnHeight, padH, () =>
        {
            ToggleClickMode();
            ScheduleCollapseTimer();
        });
        var lpFlip = (LinearLayout.LayoutParams)_clickFlipperBtn.LayoutParameters!;
        lpFlip.LeftMargin = (int)(6 * density);
        topBar.AddView(_clickFlipperBtn);

        // 3. Regular Keyboard toggle
        var kbdBtn = CreatePillButton("⌨ Keyboard", btnHeight, padH, () =>
        {
            ToggleSoftKeyboard();
            ScheduleCollapseTimer();
        });
        var lpKbd = (LinearLayout.LayoutParams)kbdBtn.LayoutParameters!;
        lpKbd.LeftMargin = (int)(6 * density);
        topBar.AddView(kbdBtn);

        // 4. Special Keys drawer toggle
        var keysBtn = CreatePillButton("☰ Keys", btnHeight, padH, () =>
        {
            if (_modifierDrawer != null)
            {
                bool willOpen = _modifierDrawer.Visibility != ViewStates.Visible;
                _modifierDrawer.Visibility = willOpen ? ViewStates.Visible : ViewStates.Gone;
                if (willOpen)
                {
                    if (_collapseRunnable != null)
                    {
                        _collapseHandler.RemoveCallbacks(_collapseRunnable);
                    }
                }
                else
                {
                    ScheduleCollapseTimer();
                }
            }
        });
        var lpKeys = (LinearLayout.LayoutParams)keysBtn.LayoutParameters!;
        lpKeys.LeftMargin = (int)(6 * density);
        topBar.AddView(keysBtn);

        // 5. Disconnect button (with confirmation guard)
        var discBtn = CreatePillButton("✕ Disconnect", btnHeight, (int)(12 * density), () =>
        {
            ShowDisconnectConfirmation();
        }, isDanger: true);
        var lpDisc = (LinearLayout.LayoutParams)discBtn.LayoutParameters!;
        lpDisc.LeftMargin = (int)(6 * density);
        topBar.AddView(discBtn);

        _topBar = topBar;
        container.AddView(_topBar);

        return container;
    }

    private void ScheduleCollapseTimer()
    {
        if (_collapseRunnable == null) return;
        _collapseHandler.RemoveCallbacks(_collapseRunnable);
        _collapseHandler.PostDelayed(_collapseRunnable, 3500);
    }

    private Button CreatePillButton(string text, int height, int padH, Action onClick, bool isDanger = false)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        var btn = new Button(this)
        {
            Text = text,
            TextSize = 10,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, height)
        };
        btn.SetPadding(padH, 0, padH, 0);

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(16 * density);
        bg.SetColor(isDanger ? Color.Argb(200, 160, 30, 30) : Color.Argb(180, 45, 45, 55));
        btn.Background = bg;
        btn.SetTextColor(Color.White);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void ToggleInputMode()
    {
        _directTouchMode = !_directTouchMode;
        string modeLabel = _directTouchMode ? "👆 Touchpad" : "🖱 Pointer";
        if (_topModeBtn != null) _topModeBtn.Text = modeLabel;
        UpdateCursorPosition();
        Toast.MakeText(this, _directTouchMode ? "Direct Touchpad Mode" : "Virtual Pointer Mode", ToastLength.Short)?.Show();
    }

    private void ToggleClickMode()
    {
        _isRightClickArmed = !_isRightClickArmed;
        UpdateClickFlipperUi();
        TriggerHaptic();
        if (_isRightClickArmed)
        {
            Toast.MakeText(this, "Right Click Armed (Next touch will right click)", ToastLength.Short)?.Show();
        }
    }

    private void UpdateClickFlipperUi()
    {
        if (_clickFlipperBtn == null) return;
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        _clickFlipperBtn.Text = _isRightClickArmed ? "🖱 Right Click" : "🖱 Left Click";

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(16 * density);
        bg.SetColor(_isRightClickArmed ? Color.Argb(245, 255, 179, 0) : Color.Argb(180, 45, 45, 55));
        _clickFlipperBtn.Background = bg;
        _clickFlipperBtn.SetTextColor(_isRightClickArmed ? Color.Black : Color.White);
    }

    private LinearLayout CreateModifierDrawer()
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        int safeTop = (int)(28 * density);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            var insets = WindowManager?.CurrentWindowMetrics?.WindowInsets?.GetInsetsIgnoringVisibility(
                WindowInsetsCompat.Type.StatusBars() | WindowInsetsCompat.Type.DisplayCutout());
            if (insets != null && insets.Top > safeTop)
            {
                safeTop = insets.Top + (int)(4 * density);
            }
        }
        int drawerTopMargin = safeTop + (int)(46 * density);

        var drawer = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            Visibility = ViewStates.Gone,
            Elevation = 25f,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
                TopMargin = drawerTopMargin
            }
        };
        drawer.SetPadding((int)(10 * density), (int)(8 * density), (int)(10 * density), (int)(8 * density));

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(14 * density);
        bg.SetColor(Color.Argb(240, 18, 18, 24));
        bg.SetStroke((int)(1 * density), Color.Argb(100, 255, 255, 255));
        drawer.Background = bg;

        int keyH = (int)(32 * density);

        // Row 1: Modifier and Special Keys
        var row1 = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        };
        AddDrawerKey(row1, "Esc", keyH, () => SendScanCode(0x01));
        AddDrawerKey(row1, "Tab", keyH, () => SendScanCode(0x0F));
        AddToggleKeyButton(row1, "Ctrl", keyH, ref _ctrlActive, 0x1D);
        AddToggleKeyButton(row1, "Alt", keyH, ref _altActive, 0x38);
        AddToggleKeyButton(row1, "Shift", keyH, ref _shiftActive, 0x2A);
        AddToggleKeyButton(row1, "Win", keyH, ref _winActive, 0x5B, extended: true);
        AddDrawerKey(row1, "Ctrl+Alt+Del", keyH, SendCtrlAltDel, isAccent: true);
        drawer.AddView(row1);

        // Row 2: F1 - F12 inside a HorizontalScrollView
        var fScroll = new HorizontalScrollView(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = (int)(6 * density)
            },
            HorizontalScrollBarEnabled = false
        };
        var rowF = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Horizontal };
        ushort[] fCodes = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (int i = 0; i < 12; i++)
        {
            ushort code = fCodes[i];
            AddDrawerKey(rowF, $"F{i + 1}", keyH, () => SendScanCode(code));
        }
        fScroll.AddView(rowF);
        drawer.AddView(fScroll);

        return drawer;
    }

    private void AddDrawerKey(LinearLayout row, string label, int height, Action action, bool isAccent = false)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        var btn = new Button(this)
        {
            Text = label,
            TextSize = 10,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, height)
            {
                RightMargin = (int)(4 * density)
            }
        };
        btn.SetPadding((int)(8 * density), 0, (int)(8 * density), 0);

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(8 * density);
        bg.SetColor(isAccent ? Color.Argb(200, 180, 50, 40) : Color.Argb(180, 40, 40, 48));
        btn.Background = bg;
        btn.SetTextColor(Color.White);
        btn.Click += (_, _) => action();
        row.AddView(btn);
    }

    private Button AddToggleKeyButton(LinearLayout row, string label, int height, ref bool flagRef, ushort code, bool extended = false)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        var btn = new Button(this)
        {
            Text = label,
            TextSize = 10,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, height)
            {
                RightMargin = (int)(4 * density)
            }
        };
        btn.SetPadding((int)(8 * density), 0, (int)(8 * density), 0);

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(8 * density);
        bg.SetColor(Color.Argb(180, 40, 40, 48));
        btn.Background = bg;
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

            var activeBg = new GradientDrawable();
            activeBg.SetShape(ShapeType.Rectangle);
            activeBg.SetCornerRadius(8 * density);
            activeBg.SetColor(active ? Color.Argb(230, 0, 120, 215) : Color.Argb(180, 40, 40, 48));
            btn.Background = activeBg;

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

    public override void OnBackPressed()
    {
        if (_modifierDrawer != null && _modifierDrawer.Visibility == ViewStates.Visible)
        {
            _modifierDrawer.Visibility = ViewStates.Gone;
            ScheduleCollapseTimer();
            return;
        }

        ShowDisconnectConfirmation();
    }

    private void ShowDisconnectConfirmation()
    {
        var builder = new AndroidX.AppCompat.App.AlertDialog.Builder(this);
        builder.SetTitle("Disconnect Remote Desktop?");
        builder.SetMessage($"Do you want to disconnect from {_session?.Config.ProfileName ?? "the remote computer"}?");
        builder.SetPositiveButton("Disconnect", (_, _) =>
        {
            _session?.Disconnect();
            Finish();
        });
        builder.SetNegativeButton("Keep Connected", (_, _) => { });
        builder.Show();
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
        TearDownSessionNotification();
        if (_collapseRunnable != null)
        {
            _collapseHandler.RemoveCallbacks(_collapseRunnable);
        }
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

        public override bool OnScaleBegin(ScaleGestureDetector detector)
        {
            _act._isScaling = true;
            return true;
        }

        public override bool OnScale(ScaleGestureDetector detector)
        {
            float factor = detector.ScaleFactor;
            if (factor <= 0 || float.IsNaN(factor) || float.IsInfinity(factor)) return true;

            float oldScale = _act._scale;
            float newScale = Math.Clamp(oldScale * factor, 1.0f, 5.0f);
            if (Math.Abs(newScale - oldScale) < 0.001f) return true;

            float focusX = detector.FocusX;
            float focusY = detector.FocusY;

            // Zoom around touch focal point
            float scaleRatio = newScale / oldScale;
            float screenCenterX = _act._screenWidth / 2f;
            float screenCenterY = _act._screenHeight / 2f;

            _act._panX = (focusX - screenCenterX) - scaleRatio * (focusX - screenCenterX - _act._panX);
            _act._panY = (focusY - screenCenterY) - scaleRatio * (focusY - screenCenterY - _act._panY);
            _act._scale = newScale;

            _act.RedrawCurrentFrame();
            _act.UpdateCursorPosition();
            return true;
        }

        public override void OnScaleEnd(ScaleGestureDetector detector)
        {
            _act._isScaling = false;
        }
    }

    private class CustomTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        private readonly Func<View?, MotionEvent, bool> _handler;
        public CustomTouchListener(Func<View?, MotionEvent, bool> handler) => _handler = handler;
        public bool OnTouch(View? v, MotionEvent? e) => e != null && _handler(v, e);
    }
}
