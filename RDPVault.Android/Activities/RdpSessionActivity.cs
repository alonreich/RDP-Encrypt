using System;
using System.Collections.Generic;
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
    LaunchMode = LaunchMode.SingleTask,
    TaskAffinity = "com.rdpvault.app.rdpsession",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Density | ConfigChanges.FontScale,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ScreenOrientation = ScreenOrientation.Sensor,
    Theme = "@style/Theme.AppCompat.NoActionBar.FullScreen"
)]
public class RdpSessionActivity : AppCompatActivity, TextureView.ISurfaceTextureListener, View.IOnTouchListener
{
    private FreeRdpSession? _session;
    private RdpSessionConfig? _config;
    private TextureView? _textureView;
    private volatile bool _isSurfaceAvailable;
    private Bitmap? _frameBitmap;
    private byte[]? _pixelBuffer;
    private readonly object _renderLock = new();
    private bool _isExplicitDisconnect = false;

    private float _scale = 1.0f;
    private float _panX = 0f;
    private float _panY = 0f;
    private int _screenWidth;
    private int _screenHeight;
    private int _imeHeight = 0;

    private float _cursorX = 960f;
    private float _cursorY = 540f;
    private bool _directTouchMode = false;
    private bool _isRightClickArmed = false;
    private bool _isDragging = false;
    private bool _isPotentialDoubleClick = false;
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

    private bool _isTextCaretActive = false;
    private Bitmap? _arrowCursorBitmap;
    private Bitmap? _ibeamCursorBitmap;

    private FrameLayout? _rootLayout;
    private View? _topControls;
    private LinearLayout? _topBar;
    private Button? _collapsedHandle;
    private ImageView? _cursorView;
    private View? _scrollWheelBar;
    private View? _loadingOverlay;
    private ProgressBar? _progressBar;
    private TextView? _statusText;
    private Button? _topModeBtn;
    private Button? _clickFlipperBtn;
    private Button? _floatingKeyboardBtn;
    private View? _helpOverlay;

    private LinearLayout? _keyboardAssistantBar;
    private EditText? _hiddenInput;

    private enum ModifierMode { Inactive, Latched, Locked }
    private ModifierMode _ctrlMode = ModifierMode.Inactive;
    private ModifierMode _altMode = ModifierMode.Inactive;
    private ModifierMode _shiftMode = ModifierMode.Inactive;
    private ModifierMode _winMode = ModifierMode.Inactive;
    private Button? _btnCtrl;
    private Button? _btnAlt;
    private Button? _btnShift;
    private Button? _btnWin;

    private readonly Handler _collapseHandler = new(Looper.MainLooper!);
    private Action? _collapseRunnable;

    private readonly Handler _edgeScrollHandler = new(Looper.MainLooper!);
    private bool _isEdgeScrolling = false;
    private float _edgeScrollSpeedX = 0f;
    private float _edgeScrollSpeedY = 0f;

    private readonly Handler _longPressHandler = new(Looper.MainLooper!);
    private Action? _longPressRunnable;
    private bool _isLongPressTriggered = false;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

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

        ViewCompat.SetOnApplyWindowInsetsListener(_rootLayout, new InsetListener(this));

        _textureView = new TextureView(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent,
                FrameLayout.LayoutParams.MatchParent)
        };
        _textureView.SurfaceTextureListener = this;
        _textureView.SetOnTouchListener(this);
        _rootLayout.AddView(_textureView);

        _hiddenInput = CreateHiddenInput();
        _rootLayout.AddView(_hiddenInput);

        _cursorView = CreateCursorView();
        _rootLayout.AddView(_cursorView);

        _keyboardAssistantBar = CreateKeyboardAssistantBar();
        _rootLayout.AddView(_keyboardAssistantBar);

        _topControls = CreateTopControls();
        _rootLayout.AddView(_topControls);

        _loadingOverlay = CreateLoadingOverlay(out _progressBar, out _statusText);
        _rootLayout.AddView(_loadingOverlay);

        SetContentView(_rootLayout);

        _scaleDetector = new ScaleGestureDetector(this, new ScaleListener(this));

        _collapseRunnable = () =>
        {
            if (_keyboardAssistantBar != null && _keyboardAssistantBar.Visibility == ViewStates.Visible) return;
            if (_imeHeight > 0) return;
            if (_topBar != null && _collapsedHandle != null)
            {
                _topBar.Visibility = ViewStates.Gone;
                _collapsedHandle.Visibility = ViewStates.Visible;
            }
        };

        if (RdpSessionBridge.ActiveSession != null && RdpSessionBridge.ActiveSession.IsConnected)
        {
            _session = RdpSessionBridge.ActiveSession;
            _config = _session.Config;
            _cursorX = _session.RemoteWidth / 2f;
            _cursorY = _session.RemoteHeight / 2f;

            BindSessionEvents(_session);

            if (_loadingOverlay != null) _loadingOverlay.Visibility = ViewStates.Gone;
            if (_progressBar != null) _progressBar.Visibility = ViewStates.Gone;
            if (_statusText != null) _statusText.Visibility = ViewStates.Gone;

            _session.RequestRedraw();
            UpdateCursorPosition();
            RedrawCurrentFrame();
            ScheduleCollapseTimer();
            CheckAndShowFirstSessionGuide();
        }
        else
        {
            var config = RdpSessionBridge.ConsumePendingConfig();
            if (config == null)
            {
                Toast.MakeText(this, "No active session to resume.", ToastLength.Short)?.Show();
                Finish();
                return;
            }

            _config = config;
            _cursorX = config.Width / 2f;
            _cursorY = config.Height / 2f;

            InitializeAndConnect(config);
        }
    }

    private void BindSessionEvents(FreeRdpSession session)
    {
        session.Connected += OnSessionConnected;
        session.ConnectionFailed += OnSessionConnectionFailed;
        session.Disconnected += OnSessionDisconnected;
        session.FramebufferUpdated += OnFramebufferUpdated;
        session.PointerTypeChanged += OnPointerTypeChanged;
    }

    private void UnbindSessionEvents(FreeRdpSession session)
    {
        session.Connected -= OnSessionConnected;
        session.ConnectionFailed -= OnSessionConnectionFailed;
        session.Disconnected -= OnSessionDisconnected;
        session.FramebufferUpdated -= OnFramebufferUpdated;
        session.PointerTypeChanged -= OnPointerTypeChanged;
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        ApplyImmersiveFullscreen();
        if (_session != null && _session.IsConnected)
        {
            if (_loadingOverlay != null) _loadingOverlay.Visibility = ViewStates.Gone;
            _session.RequestRedraw();
            UpdateCursorPosition();
            RedrawCurrentFrame();
            ScheduleCollapseTimer();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyImmersiveFullscreen();
        if (_session != null && _session.IsConnected)
        {
            if (_loadingOverlay != null) _loadingOverlay.Visibility = ViewStates.Gone;
            _session.RequestRedraw();
            UpdateCursorPosition();
            RedrawCurrentFrame();
            ScheduleCollapseTimer();
        }
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
        if (_session != null)
        {
            UnbindSessionEvents(_session);
            _session.Dispose();
            _session = null;
        }

        if (_loadingOverlay != null) _loadingOverlay.Visibility = ViewStates.Visible;
        if (_progressBar != null) _progressBar.Visibility = ViewStates.Visible;
        if (_statusText != null)
        {
            _statusText.Visibility = ViewStates.Visible;
            _statusText.Text = $"Connecting to {config.ProfileName}...";
        }

        _session = new FreeRdpSession(config);
        RdpSessionBridge.ActiveSession = _session;

        BindSessionEvents(_session);
        _session.Start();
    }

    private void OnSessionConnected()
    {
        RdpSessionBridge.IsConnected = true;
        RdpSessionBridge.ConnectedProfile = _session?.Config.Profile;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionConnected(_session?.Config.Profile);

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
            Toast.MakeText(this, "Connected to Remote Desktop", ToastLength.Short)?.Show();
            UpdateCursorPosition();
            RedrawCurrentFrame();
            ScheduleCollapseTimer();
            CheckAndShowFirstSessionGuide();
        });
    }

    private void OnSessionConnectionFailed(string reason)
    {
        _isExplicitDisconnect = true;
        TearDownSessionNotification();
        RdpSessionBridge.IsConnected = false;
        RdpSessionBridge.ConnectedProfile = null;
        RdpSessionBridge.ActiveSession = null;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionEnded();

        RunOnUiThread(() =>
        {
            if (_loadingOverlay != null) _loadingOverlay.Visibility = ViewStates.Gone;
            ShowConnectionFailedDialog(reason);
        });
    }

    private void ShowConnectionFailedDialog(string reason)
    {
        string guidance = "Could not establish connection to the remote computer.";
        if (reason.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("logon", StringComparison.OrdinalIgnoreCase))
        {
            guidance = "Authentication failed.\nPlease verify that your Windows username and password are correct in the profile.";
        }
        else if (reason.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                 reason.Contains("reach", StringComparison.OrdinalIgnoreCase) ||
                 reason.Contains("connect", StringComparison.OrdinalIgnoreCase))
        {
            guidance = "Connection timed out.\nEnsure the remote workstation is powered on, connected to the local network or VPN, and listening on the configured RDP port.";
        }
        else if (reason.Contains("cert", StringComparison.OrdinalIgnoreCase) ||
                 reason.Contains("nla", StringComparison.OrdinalIgnoreCase) ||
                 reason.Contains("tls", StringComparison.OrdinalIgnoreCase))
        {
            guidance = "Security negotiation or NLA failed.\nVerify Network Level Authentication and TLS certificate settings on the host PC.";
        }

        var builder = new AndroidX.AppCompat.App.AlertDialog.Builder(this);
        builder.SetTitle("Remote Connection Failed");
        builder.SetMessage($"{guidance}\n\nTechnical details: {reason}");
        builder.SetCancelable(false);
        builder.SetPositiveButton("Retry Connection", (_, _) =>
        {
            if (_config != null)
            {
                InitializeAndConnect(_config);
            }
            else
            {
                Finish();
            }
        });
        builder.SetNegativeButton("Return to Vault", (_, _) =>
        {
            Finish();
        });
        builder.Show();
    }

    private void OnSessionDisconnected()
    {
        bool wasExplicit = _isExplicitDisconnect;
        _isExplicitDisconnect = true;
        TearDownSessionNotification();
        RdpSessionBridge.IsConnected = false;
        RdpSessionBridge.ConnectedProfile = null;
        RdpSessionBridge.ActiveSession = null;
        RdpSessionBridge.SessionStateChanged?.Invoke();
        MainActivity.Instance?.OnSessionEnded();
        RunOnUiThread(() =>
        {
            if (wasExplicit)
            {
                Finish();
            }
            else
            {
                var builder = new AndroidX.AppCompat.App.AlertDialog.Builder(this);
                builder.SetTitle("Connection Closed");
                builder.SetMessage("The remote desktop session has ended or the connection was interrupted.");
                builder.SetCancelable(false);
                builder.SetPositiveButton("Reconnect", (_, _) =>
                {
                    if (_config != null)
                    {
                        InitializeAndConnect(_config);
                    }
                    else
                    {
                        Finish();
                    }
                });
                builder.SetNegativeButton("Back to Vault", (_, _) =>
                {
                    Finish();
                });
                builder.Show();
            }
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

    private void OnPointerTypeChanged(uint pointerType)
    {
        bool isText = (pointerType == 0x7F01);
        if (_isTextCaretActive != isText)
        {
            _isTextCaretActive = isText;
            RunOnUiThread(() =>
            {
                if (_cursorView != null)
                {
                    _cursorView.SetImageBitmap(_isTextCaretActive ? _ibeamCursorBitmap : _arrowCursorBitmap);
                }
            });
        }
    }

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

                int visibleH = _imeHeight > 0 ? Math.Max(100, canvasH - _imeHeight) : canvasH;

                float scaleX = (float)canvasW / rw;
                float scaleY = (float)visibleH / rh;
                float baseScale = Math.Min(scaleX, scaleY);

                float currentW = rw * baseScale * _scale;
                float currentH = rh * baseScale * _scale;

                CalculateViewportBounds(out float maxPanX, out float maxPanY);

                _panX = Math.Clamp(_panX, -maxPanX, maxPanX);
                _panY = Math.Clamp(_panY, -maxPanY, maxPanY);

                float tx = (canvasW - currentW) / 2f + _panX;
                float ty = (visibleH - currentH) / 2f + _panY;

                using var matrix = new Matrix();
                matrix.PostScale(baseScale * _scale, baseScale * _scale);
                matrix.PostTranslate(tx, ty);

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

    private void CalculateViewportBounds(out float maxPanX, out float maxPanY)
    {
        int rw = _session?.RemoteWidth > 0 ? _session.RemoteWidth : 1920;
        int rh = _session?.RemoteHeight > 0 ? _session.RemoteHeight : 1080;

        int visibleH = _imeHeight > 0 ? Math.Max(100, _screenHeight - _imeHeight) : _screenHeight;

        float scaleX = (float)_screenWidth / rw;
        float scaleY = (float)visibleH / rh;
        float baseScale = Math.Min(scaleX, scaleY);

        float currentW = rw * baseScale * _scale;
        float currentH = rh * baseScale * _scale;

        maxPanX = currentW > _screenWidth ? (currentW - _screenWidth) / 2f : 0f;
        maxPanY = currentH > visibleH ? (currentH - visibleH) / 2f : (visibleH - currentH) / 2f;
    }

    private void RedrawCurrentFrame()
    {
        if (_frameBitmap != null && !_frameBitmap.IsRecycled)
        {
            RenderBitmapToView(_frameBitmap);
        }
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        _isSurfaceAvailable = true;
        if (width > 0 && height > 0)
        {
            _screenWidth = width;
            _screenHeight = height;
        }
        _session?.RequestRedraw();
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

    public bool OnTouch(View? v, MotionEvent? e)
    {
        if (e == null || _session == null || !_session.IsConnected) return false;

        ResetCollapseTimer();

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
                _isLongPressTriggered = false;

                if (_longPressRunnable != null) _longPressHandler.RemoveCallbacks(_longPressRunnable);
                _longPressRunnable = () =>
                {
                    if (_hasMoved || _isTwoFingerGesture || _isScaling || _session == null || !_session.IsConnected) return;
                    _isLongPressTriggered = true;
                    TriggerHaptic();

                    ushort rx, ry;
                    if (_directTouchMode)
                    {
                        ScreenToRemote(_touchStartX, _touchStartY, out float rxf, out float ryf);
                        rx = (ushort)rxf;
                        ry = (ushort)ryf;
                        _cursorX = rx;
                        _cursorY = ry;
                    }
                    else
                    {
                        rx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ry = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                    }

                    _session.SendMouseMove(rx, ry);
                    _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON2, true, rx, ry);
                    Task.Delay(45).ContinueWith(_ =>
                    {
                        _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON2, false, rx, ry);
                    });
                    CheckAndAutoReleaseLatchedModifiers();
                };
                _longPressHandler.PostDelayed(_longPressRunnable, 450);

                long tapInterval = now - _lastTapTime;
                if (tapInterval < 300)
                {
                    _isPotentialDoubleClick = true;
                }
                else
                {
                    _isPotentialDoubleClick = false;
                }

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
                break;

            case (int)MotionEventActions.PointerDown:
                if (_longPressRunnable != null) _longPressHandler.RemoveCallbacks(_longPressRunnable);
                if (pointerCount >= 2)
                {
                    _isTwoFingerGesture = true;
                    _isPotentialDoubleClick = false;
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
                if (distFromStart > 14)
                {
                    _hasMoved = true;
                    if (_longPressRunnable != null) _longPressHandler.RemoveCallbacks(_longPressRunnable);
                    if (_isPotentialDoubleClick && !_directTouchMode)
                    {
                        _isDragging = true;
                        _isPotentialDoubleClick = false;
                        TriggerHaptic();
                        ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                        _session.SendMouseMove(cx, cy);
                        _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, true, cx, cy);
                    }
                }

                if (pointerCount == 1 && !_isTwoFingerGesture)
                {
                    if (_directTouchMode)
                    {
                        ScreenToRemote(x, y, out _cursorX, out _cursorY);
                        ushort cx = (ushort)_cursorX;
                        ushort cy = (ushort)_cursorY;
                        _session.SendMouseMove(cx, cy);
                        CheckAndHandleEdgeAutoScroll();
                    }
                    else
                    {
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

                        CheckAndHandleEdgeAutoScroll();
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

                        CalculateViewportBounds(out float maxPanX, out float maxPanY);

                        if (_scale > 1.05f || maxPanX > 0 || maxPanY > 0)
                        {
                            _panX = Math.Clamp(_panX + fdx, -maxPanX, maxPanX);
                            _panY = Math.Clamp(_panY + fdy, -maxPanY, maxPanY);
                            RedrawCurrentFrame();
                            UpdateCursorPosition();
                        }
                        else
                        {
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
                        CheckAndAutoReleaseLatchedModifiers();
                    }
                }
                break;

            case (int)MotionEventActions.Up:
                StopEdgeScroll();
                if (_longPressRunnable != null) _longPressHandler.RemoveCallbacks(_longPressRunnable);

                if (_isLongPressTriggered)
                {
                    _isLongPressTriggered = false;
                    _isDragging = false;
                    _isTwoFingerGesture = false;
                    _hasMoved = false;
                    _isPotentialDoubleClick = false;
                    break;
                }

                if (_directTouchMode)
                {
                    ScreenToRemote(x, y, out _cursorX, out _cursorY);
                    ushort cx = (ushort)_cursorX;
                    ushort cy = (ushort)_cursorY;
                    _session.SendMouseMove(cx, cy);
                    _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                    _isDragging = false;

                    if (!_hasMoved && (now - _touchStartTime < 350))
                    {
                        TriggerHaptic();
                    }
                    CheckAndAutoReleaseLatchedModifiers();
                }
                else
                {
                    if (_isDragging)
                    {
                        _isDragging = false;
                        ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                        _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                        CheckAndAutoReleaseLatchedModifiers();
                    }
                    else if (_isPotentialDoubleClick && !_hasMoved)
                    {
                        _isPotentialDoubleClick = false;
                        TriggerHaptic();
                        ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                        ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                        _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, true, cx, cy);
                        Task.Delay(35).ContinueWith(_ =>
                        {
                            _session?.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                        });
                        _lastTapTime = 0;
                        CheckAndAutoReleaseLatchedModifiers();
                    }
                    else if (!_isTwoFingerGesture && !_hasMoved)
                    {
                        long duration = now - _touchStartTime;
                        if (duration < 350)
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

                            CheckAndAutoReleaseLatchedModifiers();
                        }
                    }
                }
                _isTwoFingerGesture = false;
                _hasMoved = false;
                _isPotentialDoubleClick = false;
                break;

            case (int)MotionEventActions.Cancel:
                StopEdgeScroll();
                if (_isDragging)
                {
                    _isDragging = false;
                    ushort cx = (ushort)Math.Clamp(_cursorX, 0, _session.RemoteWidth);
                    ushort cy = (ushort)Math.Clamp(_cursorY, 0, _session.RemoteHeight);
                    _session.SendMouseButton(NativeFreeRdp.PTR_FLAGS_BUTTON1, false, cx, cy);
                }
                _isTwoFingerGesture = false;
                _hasMoved = false;
                _isPotentialDoubleClick = false;
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
            CheckAndAutoReleaseLatchedModifiers();
        }
        return true;
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && _session != null && _session.IsConnected)
        {
            ResetCollapseTimer();
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

    private void CheckAndHandleEdgeAutoScroll()
    {
        if (_session == null || !_session.IsConnected)
        {
            StopEdgeScroll();
            return;
        }

        CalculateViewportBounds(out float maxPanX, out float maxPanY);
        if (maxPanX <= 0 && maxPanY <= 0)
        {
            StopEdgeScroll();
            return;
        }

        RemoteToScreen(_cursorX, _cursorY, out float sx, out float sy);
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        float edgeMargin = 32f * density;

        int visibleW = _screenWidth;
        int visibleH = _imeHeight > 0 ? Math.Max(100, _screenHeight - _imeHeight) : _screenHeight;

        float speedX = 0f;
        float speedY = 0f;

        if (sx < edgeMargin && maxPanX > 0)
        {
            float dist = edgeMargin - sx;
            speedX = (float)Math.Min(28f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 24f);
        }
        else if (sx > visibleW - edgeMargin && maxPanX > 0)
        {
            float dist = sx - (visibleW - edgeMargin);
            speedX = -(float)Math.Min(28f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 24f);
        }

        if (sy < edgeMargin && maxPanY > 0)
        {
            float dist = edgeMargin - sy;
            speedY = (float)Math.Min(28f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 24f);
        }
        else if (sy > visibleH - edgeMargin && maxPanY > 0)
        {
            float dist = sy - (visibleH - edgeMargin);
            speedY = -(float)Math.Min(28f, 4f + Math.Pow(dist / edgeMargin, 1.5) * 24f);
        }

        if (speedX != 0f || speedY != 0f)
        {
            StartEdgeScroll(speedX, speedY);
        }
        else
        {
            StopEdgeScroll();
        }
    }

    private void StartEdgeScroll(float speedX, float speedY)
    {
        _edgeScrollSpeedX = speedX;
        _edgeScrollSpeedY = speedY;
        if (!_isEdgeScrolling)
        {
            _isEdgeScrolling = true;
            _edgeScrollHandler.Post(EdgeScrollStep);
        }
    }

    private void StopEdgeScroll()
    {
        _isEdgeScrolling = false;
        _edgeScrollSpeedX = 0f;
        _edgeScrollSpeedY = 0f;
    }

    private void EdgeScrollStep()
    {
        if (!_isEdgeScrolling || _session == null || !_session.IsConnected)
        {
            _isEdgeScrolling = false;
            return;
        }

        CalculateViewportBounds(out float maxPanX, out float maxPanY);
        if (maxPanX <= 0 && maxPanY <= 0)
        {
            _isEdgeScrolling = false;
            return;
        }

        float oldPanX = _panX;
        float oldPanY = _panY;

        if (_edgeScrollSpeedX != 0f && maxPanX > 0)
        {
            _panX = Math.Clamp(_panX + _edgeScrollSpeedX, -maxPanX, maxPanX);
        }
        if (_edgeScrollSpeedY != 0f && maxPanY > 0)
        {
            _panY = Math.Clamp(_panY + _edgeScrollSpeedY, -maxPanY, maxPanY);
        }

        if (Math.Abs(_panX - oldPanX) > 0.1f || Math.Abs(_panY - oldPanY) > 0.1f)
        {
            RedrawCurrentFrame();
            UpdateCursorPosition();
            _edgeScrollHandler.PostDelayed(EdgeScrollStep, 16);
        }
        else
        {
            _isEdgeScrolling = false;
        }
    }

    private void ScreenToRemote(float sx, float sy, out float rx, out float ry)
    {
        int rw = _session?.RemoteWidth > 0 ? _session.RemoteWidth : 1920;
        int rh = _session?.RemoteHeight > 0 ? _session.RemoteHeight : 1080;
        int visibleH = _imeHeight > 0 ? Math.Max(100, _screenHeight - _imeHeight) : _screenHeight;

        float scaleX = (float)_screenWidth / rw;
        float scaleY = (float)visibleH / rh;
        float baseScale = Math.Min(scaleX, scaleY);

        float currentW = rw * baseScale * _scale;
        float currentH = rh * baseScale * _scale;

        float tx = (_screenWidth - currentW) / 2f + _panX;
        float ty = (visibleH - currentH) / 2f + _panY;

        rx = (sx - tx) / (baseScale * _scale);
        ry = (sy - ty) / (baseScale * _scale);

        rx = Math.Clamp(rx, 0, rw);
        ry = Math.Clamp(ry, 0, rh);
    }

    private void RemoteToScreen(float rx, float ry, out float sx, out float sy)
    {
        int rw = _session?.RemoteWidth > 0 ? _session.RemoteWidth : 1920;
        int rh = _session?.RemoteHeight > 0 ? _session.RemoteHeight : 1080;
        int visibleH = _imeHeight > 0 ? Math.Max(100, _screenHeight - _imeHeight) : _screenHeight;

        float scaleX = (float)_screenWidth / rw;
        float scaleY = (float)visibleH / rh;
        float baseScale = Math.Min(scaleX, scaleY);

        float currentW = rw * baseScale * _scale;
        float currentH = rh * baseScale * _scale;

        float tx = (_screenWidth - currentW) / 2f + _panX;
        float ty = (visibleH - currentH) / 2f + _panY;

        sx = rx * (baseScale * _scale) + tx;
        sy = ry * (baseScale * _scale) + ty;
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

    private ImageView CreateCursorView()
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        int sizePx = Math.Max(28, (int)(22 * density));
        var cursor = new ImageView(this)
        {
            Clickable = false,
            Focusable = false,
            LayoutParameters = new FrameLayout.LayoutParams(sizePx, sizePx)
        };

        _arrowCursorBitmap = Bitmap.CreateBitmap(sizePx, sizePx, Bitmap.Config.Argb8888!);
        using (var canvas = new Canvas(_arrowCursorBitmap))
        using (var path = new global::Android.Graphics.Path())
        {
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
        }

        _ibeamCursorBitmap = Bitmap.CreateBitmap(sizePx, sizePx, Bitmap.Config.Argb8888!);
        using (var canvas = new Canvas(_ibeamCursorBitmap))
        {
            float scale = sizePx / 24f;
            using var paint = new Paint { Color = Color.White, AntiAlias = true, StrokeWidth = 2.5f * scale };
            using var stroke = new Paint { Color = Color.Black, AntiAlias = true, StrokeWidth = 4.5f * scale };

            canvas.DrawLine(5 * scale, 2 * scale, 17 * scale, 2 * scale, stroke);
            canvas.DrawLine(5 * scale, 22 * scale, 17 * scale, 22 * scale, stroke);
            canvas.DrawLine(11 * scale, 2 * scale, 11 * scale, 22 * scale, stroke);

            canvas.DrawLine(5 * scale, 2 * scale, 17 * scale, 2 * scale, paint);
            canvas.DrawLine(5 * scale, 22 * scale, 17 * scale, 22 * scale, paint);
            canvas.DrawLine(11 * scale, 2 * scale, 11 * scale, 22 * scale, paint);
        }

        cursor.SetImageBitmap(_arrowCursorBitmap);
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
            ResetCollapseTimer();
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

    private Button CreateFloatingKeyboardButton()
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        int btnSize = (int)(42 * density);

        var btn = new Button(this)
        {
            Text = "⌨",
            TextSize = 14,
            LayoutParameters = new FrameLayout.LayoutParams(btnSize, btnSize)
            {
                Gravity = GravityFlags.Right | GravityFlags.Bottom,
                RightMargin = (int)(12 * density),
                BottomMargin = (int)(44 * density)
            },
            Elevation = 25f
        };
        btn.SetPadding(0, 0, 0, 0);

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Oval);
        bg.SetColor(Color.Argb(210, 28, 28, 36));
        bg.SetStroke((int)(1.5f * density), Color.Argb(160, 0, 120, 215));
        btn.Background = bg;
        btn.SetTextColor(Color.White);

        btn.Click += (_, _) =>
        {
            ToggleSoftKeyboard();
            ResetCollapseTimer();
        };

        return btn;
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

        int handleW = (int)(56 * density);
        int handleH = (int)(24 * density);
        var handle = new Button(this)
        {
            Text = "⌨ ☰",
            TextSize = 9,
            LayoutParameters = new FrameLayout.LayoutParams(handleW, handleH)
            {
                Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
                TopMargin = (int)(4 * density)
            },
            Visibility = ViewStates.Gone
        };
        handle.SetPadding(0, 0, 0, 0);
        var handleBg = new GradientDrawable();
        handleBg.SetShape(ShapeType.Rectangle);
        handleBg.SetCornerRadius(12 * density);
        handleBg.SetColor(Color.Argb(200, 20, 20, 28));
        handleBg.SetStroke((int)(1 * density), Color.Argb(160, 0, 120, 215));
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
        bg.SetColor(Color.Argb(225, 18, 18, 24));
        bg.SetStroke((int)(1 * density), Color.Argb(90, 255, 255, 255));
        topBar.Background = bg;

        int btnHeight = (int)(32 * density);
        int padH = (int)(10 * density);

        _topModeBtn = CreatePillButton(_directTouchMode ? "👆 Direct Touch" : "🖱 Trackpad", btnHeight, padH, () =>
        {
            ToggleInputMode();
            ScheduleCollapseTimer();
        });
        topBar.AddView(_topModeBtn);

        _clickFlipperBtn = CreatePillButton("🖱 Left Click", btnHeight, padH, () =>
        {
            ToggleClickMode();
            ScheduleCollapseTimer();
        });
        var lpFlip = (LinearLayout.LayoutParams)_clickFlipperBtn.LayoutParameters!;
        lpFlip.LeftMargin = (int)(5 * density);
        topBar.AddView(_clickFlipperBtn);

        var kbdBtn = CreatePillButton("⌨ Keyboard", btnHeight, padH, () =>
        {
            ToggleSoftKeyboard();
            ScheduleCollapseTimer();
        });
        var lpKbd = (LinearLayout.LayoutParams)kbdBtn.LayoutParameters!;
        lpKbd.LeftMargin = (int)(5 * density);
        topBar.AddView(kbdBtn);

        var keysBtn = CreatePillButton("☰ Keys", btnHeight, padH, () =>
        {
            ToggleKeyboardAssistantBar();
            ScheduleCollapseTimer();
        });
        var lpKeys = (LinearLayout.LayoutParams)keysBtn.LayoutParameters!;
        lpKeys.LeftMargin = (int)(5 * density);
        topBar.AddView(keysBtn);

        var helpBtn = CreatePillButton("?", btnHeight, (int)(8 * density), () =>
        {
            ShowFirstSessionGuide();
        });
        var lpHelp = (LinearLayout.LayoutParams)helpBtn.LayoutParameters!;
        lpHelp.LeftMargin = (int)(5 * density);
        topBar.AddView(helpBtn);

        var discBtn = CreatePillButton("✕ Disconnect", btnHeight, (int)(10 * density), () =>
        {
            ShowDisconnectConfirmation();
        }, isDanger: true);
        var lpDisc = (LinearLayout.LayoutParams)discBtn.LayoutParameters!;
        lpDisc.LeftMargin = (int)(5 * density);
        topBar.AddView(discBtn);

        _topBar = topBar;
        container.AddView(_topBar);

        return container;
    }

    private void ResetCollapseTimer()
    {
        ScheduleCollapseTimer();
    }

    private void ScheduleCollapseTimer()
    {
        if (_collapseRunnable == null) return;
        _collapseHandler.RemoveCallbacks(_collapseRunnable);
        if (_imeHeight > 0 || (_keyboardAssistantBar != null && _keyboardAssistantBar.Visibility == ViewStates.Visible)) return;
        _collapseHandler.PostDelayed(_collapseRunnable, 4500);
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
        bg.SetColor(isDanger ? Color.Argb(200, 160, 30, 30) : Color.Argb(180, 42, 42, 52));
        btn.Background = bg;
        btn.SetTextColor(Color.White);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void ToggleInputMode()
    {
        _directTouchMode = !_directTouchMode;
        string modeLabel = _directTouchMode ? "👆 Direct Touch" : "🖱 Trackpad";
        if (_topModeBtn != null) _topModeBtn.Text = modeLabel;
        UpdateCursorPosition();
        Toast.MakeText(this, _directTouchMode ? "Direct Touch Mode (Tap directly on screen)" : "Trackpad Mode (Glide finger to steer mouse)", ToastLength.Short)?.Show();
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
        bg.SetColor(_isRightClickArmed ? Color.Argb(245, 255, 179, 0) : Color.Argb(180, 42, 42, 52));
        _clickFlipperBtn.Background = bg;
        _clickFlipperBtn.SetTextColor(_isRightClickArmed ? Color.Black : Color.White);
    }

    private EditText CreateHiddenInput()
    {
        var edit = new CustomInputEditText(this);
        return edit;
    }

    private class CustomInputEditText : EditText
    {
        private readonly RdpSessionActivity _activity;

        public CustomInputEditText(RdpSessionActivity activity) : base(activity)
        {
            _activity = activity;
            LayoutParameters = new FrameLayout.LayoutParams(1, 1);
            Alpha = 0f;
            Focusable = true;
            FocusableInTouchMode = true;
            InputType = global::Android.Text.InputTypes.ClassText |
                        global::Android.Text.InputTypes.TextVariationNormal |
                        global::Android.Text.InputTypes.TextFlagNoSuggestions;
            ImeOptions = ImeAction.None;
        }

        public override IInputConnection? OnCreateInputConnection(EditorInfo? outAttrs)
        {
            var baseConn = base.OnCreateInputConnection(outAttrs);
            if (baseConn == null) return null;
            return new RdpInputConnection(baseConn, true, _activity);
        }
    }

    private class RdpInputConnection : InputConnectionWrapper
    {
        private readonly RdpSessionActivity _activity;

        public RdpInputConnection(IInputConnection target, bool mutable, RdpSessionActivity activity)
            : base(target, mutable)
        {
            _activity = activity;
        }

        public override bool DeleteSurroundingText(int beforeLength, int afterLength)
        {
            for (int i = 0; i < beforeLength; i++)
            {
                _activity.SendScanCode(0x0E); // Backspace
            }
            _activity.CheckAndAutoReleaseLatchedModifiers();
            return true;
        }

        public override bool CommitText(Java.Lang.ICharSequence? text, int newCursorPosition)
        {
            string? s = text?.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                foreach (char c in s)
                {
                    if (c == '\n' || c == '\r')
                    {
                        _activity.SendScanCode(0x1C);
                    }
                    else
                    {
                        _activity._session?.SendUnicodeChar(c);
                    }
                }
                _activity.CheckAndAutoReleaseLatchedModifiers();
            }
            return true;
        }

        public override bool SendKeyEvent(KeyEvent? e)
        {
            if (e != null && e.Action == KeyEventActions.Down)
            {
                if (e.KeyCode == Keycode.Del)
                {
                    _activity.SendScanCode(0x0E); // Backspace
                    _activity.CheckAndAutoReleaseLatchedModifiers();
                    return true;
                }
                if (e.KeyCode == Keycode.Enter)
                {
                    _activity.SendScanCode(0x1C); // Enter
                    _activity.CheckAndAutoReleaseLatchedModifiers();
                    return true;
                }
            }
            return base.SendKeyEvent(e);
        }
    }

    private LinearLayout CreateKeyboardAssistantBar()
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;

        var bar = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            Visibility = ViewStates.Gone,
            Elevation = 30f,
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                (int)(40 * density))
            {
                Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal,
                BottomMargin = 0
            }
        };
        bar.SetPadding((int)(6 * density), (int)(4 * density), (int)(6 * density), (int)(4 * density));

        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetColor(Color.Argb(235, 16, 16, 22));
        bg.SetStroke((int)(1 * density), Color.Argb(100, 255, 255, 255));
        bar.Background = bg;

        var scroll = new HorizontalScrollView(this)
        {
            HorizontalScrollBarEnabled = false,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent)
        };

        var row = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            LayoutParameters = new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.MatchParent)
        };

        int keyH = (int)(32 * density);

        AddAssistantKey(row, "Esc", keyH, () => SendScanCode(0x01));
        AddAssistantKey(row, "Tab", keyH, () => SendScanCode(0x0F));

        _btnCtrl = AddToggleModifierButton(row, "Ctrl", keyH, ref _ctrlMode, 0x1D);
        _btnAlt = AddToggleModifierButton(row, "Alt", keyH, ref _altMode, 0x38);
        _btnShift = AddToggleModifierButton(row, "Shift", keyH, ref _shiftMode, 0x2A);
        _btnWin = AddToggleModifierButton(row, "Win", keyH, ref _winMode, 0x5B, extended: true);

        AddAssistantKey(row, "←", keyH, () => SendScanCode(0x4B, extended: true));
        AddAssistantKey(row, "↑", keyH, () => SendScanCode(0x48, extended: true));
        AddAssistantKey(row, "↓", keyH, () => SendScanCode(0x50, extended: true));
        AddAssistantKey(row, "→", keyH, () => SendScanCode(0x4D, extended: true));
        AddAssistantKey(row, "Del", keyH, () => SendScanCode(0x53, extended: true));

        ushort[] fCodes = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (int i = 0; i < 12; i++)
        {
            ushort code = fCodes[i];
            AddAssistantKey(row, $"F{i + 1}", keyH, () => SendScanCode(code));
        }

        AddAssistantKey(row, "Ctrl+Alt+Del", keyH, SendCtrlAltDel, isAccent: true);
        AddAssistantKey(row, "✕ Clear", keyH, ReleaseAllModifiers);
        AddAssistantKey(row, "⌨▼", keyH, HideSoftKeyboard);

        scroll.AddView(row);
        bar.AddView(scroll);

        return bar;
    }

    private void AddAssistantKey(LinearLayout row, string label, int height, Action action, bool isAccent = false)
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
        bg.SetCornerRadius(6 * density);
        bg.SetColor(isAccent ? Color.Argb(210, 180, 50, 40) : Color.Argb(190, 36, 36, 44));
        btn.Background = bg;
        btn.SetTextColor(Color.White);
        btn.Click += (_, _) =>
        {
            ResetCollapseTimer();
            action();
            CheckAndAutoReleaseLatchedModifiers();
        };
        row.AddView(btn);
    }

    private Button AddToggleModifierButton(LinearLayout row, string label, int height, ref ModifierMode modeRef, ushort code, bool extended = false)
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
        UpdateModifierButtonStyle(btn, ModifierMode.Inactive);

        btn.Click += (_, _) =>
        {
            ResetCollapseTimer();
            ModifierMode current = GetModifierMode(label);
            ModifierMode next = current switch
            {
                ModifierMode.Inactive => ModifierMode.Latched,
                ModifierMode.Latched => ModifierMode.Locked,
                _ => ModifierMode.Inactive
            };

            SetModifierMode(label, next);

            if (next == ModifierMode.Inactive)
            {
                _session?.SendKeyboardScanCode(code, false, extended);
            }
            else
            {
                _session?.SendKeyboardScanCode(code, true, extended);
            }

            UpdateModifierButtonStyle(btn, next);
        };

        row.AddView(btn);
        return btn;
    }

    private ModifierMode GetModifierMode(string label) => label switch
    {
        "Ctrl" => _ctrlMode,
        "Alt" => _altMode,
        "Shift" => _shiftMode,
        "Win" => _winMode,
        _ => ModifierMode.Inactive
    };

    private void SetModifierMode(string label, ModifierMode mode)
    {
        switch (label)
        {
            case "Ctrl": _ctrlMode = mode; break;
            case "Alt": _altMode = mode; break;
            case "Shift": _shiftMode = mode; break;
            case "Win": _winMode = mode; break;
        }
    }

    private void UpdateModifierButtonStyle(Button btn, ModifierMode mode)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        var bg = new GradientDrawable();
        bg.SetShape(ShapeType.Rectangle);
        bg.SetCornerRadius(6 * density);

        switch (mode)
        {
            case ModifierMode.Latched:
                bg.SetColor(Color.Argb(240, 0, 120, 215));
                btn.SetTextColor(Color.White);
                break;
            case ModifierMode.Locked:
                bg.SetColor(Color.Argb(250, 255, 179, 0));
                btn.SetTextColor(Color.Black);
                break;
            default:
                bg.SetColor(Color.Argb(190, 36, 36, 44));
                btn.SetTextColor(Color.White);
                break;
        }
        btn.Background = bg;
    }

    private void UpdateModifierButtonsUi()
    {
        if (_btnCtrl != null) UpdateModifierButtonStyle(_btnCtrl, _ctrlMode);
        if (_btnAlt != null) UpdateModifierButtonStyle(_btnAlt, _altMode);
        if (_btnShift != null) UpdateModifierButtonStyle(_btnShift, _shiftMode);
        if (_winMode != ModifierMode.Inactive && _btnWin != null) UpdateModifierButtonStyle(_btnWin, _winMode);
    }

    private void CheckAndAutoReleaseLatchedModifiers()
    {
        bool changed = false;
        if (_ctrlMode == ModifierMode.Latched)
        {
            _ctrlMode = ModifierMode.Inactive;
            _session?.SendKeyboardScanCode(0x1D, false, false);
            changed = true;
        }
        if (_altMode == ModifierMode.Latched)
        {
            _altMode = ModifierMode.Inactive;
            _session?.SendKeyboardScanCode(0x38, false, false);
            changed = true;
        }
        if (_shiftMode == ModifierMode.Latched)
        {
            _shiftMode = ModifierMode.Inactive;
            _session?.SendKeyboardScanCode(0x2A, false, false);
            changed = true;
        }
        if (_winMode == ModifierMode.Latched)
        {
            _winMode = ModifierMode.Inactive;
            _session?.SendKeyboardScanCode(0x5B, false, true);
            changed = true;
        }

        if (changed)
        {
            RunOnUiThread(UpdateModifierButtonsUi);
        }
    }

    private void ReleaseAllModifiers()
    {
        _ctrlMode = ModifierMode.Inactive;
        _altMode = ModifierMode.Inactive;
        _shiftMode = ModifierMode.Inactive;
        _winMode = ModifierMode.Inactive;

        _session?.SendKeyboardScanCode(0x1D, false, false);
        _session?.SendKeyboardScanCode(0x38, false, false);
        _session?.SendKeyboardScanCode(0x2A, false, false);
        _session?.SendKeyboardScanCode(0x5B, false, true);

        UpdateModifierButtonsUi();
        Toast.MakeText(this, "Modifiers Released", ToastLength.Short)?.Show();
    }

    private void SendScanCode(ushort code, bool extended = false)
    {
        _session?.SendKeyboardScanCode(code, true, extended);
        _session?.SendKeyboardScanCode(code, false, extended);
    }

    private void SendCtrlAltDel()
    {
        _session?.SendKeyboardScanCode(0x1D, true, false);
        _session?.SendKeyboardScanCode(0x38, true, false);
        _session?.SendKeyboardScanCode(0x53, true, true);
        _session?.SendKeyboardScanCode(0x53, false, true);
        _session?.SendKeyboardScanCode(0x38, false, false);
        _session?.SendKeyboardScanCode(0x1D, false, false);
    }

    public void ShowSoftKeyboard()
    {
        _hiddenInput?.RequestFocus();
        var imm = (InputMethodManager?)GetSystemService(InputMethodService);
        imm?.ShowSoftInput(_hiddenInput, ShowFlags.Forced);
        if (_keyboardAssistantBar != null)
        {
            _keyboardAssistantBar.Visibility = ViewStates.Visible;
        }
        ResetCollapseTimer();
    }

    public void HideSoftKeyboard()
    {
        var imm = (InputMethodManager?)GetSystemService(InputMethodService);
        if (_hiddenInput != null)
        {
            imm?.HideSoftInputFromWindow(_hiddenInput.WindowToken, HideSoftInputFlags.None);
        }
        if (_keyboardAssistantBar != null)
        {
            _keyboardAssistantBar.Visibility = ViewStates.Gone;
        }
        ResetCollapseTimer();
    }

    private void ToggleSoftKeyboard()
    {
        if (_imeHeight > 0)
        {
            HideSoftKeyboard();
        }
        else
        {
            ShowSoftKeyboard();
        }
    }

    private void ToggleKeyboardAssistantBar()
    {
        if (_keyboardAssistantBar == null) return;
        bool willOpen = _keyboardAssistantBar.Visibility != ViewStates.Visible;
        _keyboardAssistantBar.Visibility = willOpen ? ViewStates.Visible : ViewStates.Gone;
        ResetCollapseTimer();
    }

    public void OnImeInsetsChanged(int imeHeight, bool isImeVisible)
    {
        _imeHeight = imeHeight;
        if (_keyboardAssistantBar != null)
        {
            var lp = (FrameLayout.LayoutParams)_keyboardAssistantBar.LayoutParameters!;
            lp.BottomMargin = imeHeight;
            _keyboardAssistantBar.LayoutParameters = lp;

            if (isImeVisible)
            {
                _keyboardAssistantBar.Visibility = ViewStates.Visible;
            }
        }

        if (imeHeight > 0)
        {
            RemoteToScreen(_cursorX, _cursorY, out float sx, out float sy);
            int visibleH = Math.Max(100, _screenHeight - imeHeight - 40);
            if (sy > visibleH)
            {
                float overflow = sy - visibleH;
                CalculateViewportBounds(out _, out float maxPanY);
                _panY = Math.Clamp(_panY - overflow, -maxPanY, maxPanY);
                RedrawCurrentFrame();
                UpdateCursorPosition();
            }
        }
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e == null) return base.DispatchKeyEvent(e);

        if (e.Action == KeyEventActions.Down)
        {
            ResetCollapseTimer();
            Keycode kc = e.KeyCode;

            if (kc == Keycode.Enter || kc == Keycode.NumpadEnter)
            {
                SendScanCode(0x1C);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }

            if (kc == Keycode.Del)
            {
                SendScanCode(0x0E);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }

            if (kc == Keycode.ForwardDel)
            {
                SendScanCode(0x53, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }

            if (kc == Keycode.Tab)
            {
                SendScanCode(0x0F);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }

            if (kc == Keycode.Escape)
            {
                SendScanCode(0x01);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }

            if (kc == Keycode.DpadUp)
            {
                SendScanCode(0x48, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
            if (kc == Keycode.DpadDown)
            {
                SendScanCode(0x50, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
            if (kc == Keycode.DpadLeft)
            {
                SendScanCode(0x4B, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
            if (kc == Keycode.DpadRight)
            {
                SendScanCode(0x4D, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
            if (kc == Keycode.Home)
            {
                SendScanCode(0x47, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
            if (kc == Keycode.MoveEnd)
            {
                SendScanCode(0x4F, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
            if (kc == Keycode.PageUp)
            {
                SendScanCode(0x49, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
            if (kc == Keycode.PageDown)
            {
                SendScanCode(0x51, extended: true);
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }

            if (_ctrlMode != ModifierMode.Inactive || _altMode != ModifierMode.Inactive || _winMode != ModifierMode.Inactive)
            {
                if (TryMapKeycodeToScancode(kc, out ushort scancode, out bool ext))
                {
                    SendScanCode(scancode, ext);
                    CheckAndAutoReleaseLatchedModifiers();
                    return true;
                }
            }

            int unicode = e.GetUnicodeChar(e.MetaState);
            if (unicode > 0)
            {
                if (unicode == 10 || unicode == 13)
                {
                    SendScanCode(0x1C);
                }
                else
                {
                    _session?.SendUnicodeChar((char)unicode);
                }
                CheckAndAutoReleaseLatchedModifiers();
                return true;
            }
        }
        else if (e.Action == KeyEventActions.Multiple && !string.IsNullOrEmpty(e.Characters))
        {
            foreach (char c in e.Characters)
            {
                if (c == '\n' || c == '\r') SendScanCode(0x1C);
                else _session?.SendUnicodeChar(c);
            }
            CheckAndAutoReleaseLatchedModifiers();
            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    private static bool TryMapKeycodeToScancode(Keycode kc, out ushort scancode, out bool extended)
    {
        extended = false;
        scancode = kc switch
        {
            Keycode.A => 0x1E,
            Keycode.B => 0x30,
            Keycode.C => 0x2E,
            Keycode.D => 0x20,
            Keycode.E => 0x12,
            Keycode.F => 0x21,
            Keycode.G => 0x22,
            Keycode.H => 0x23,
            Keycode.I => 0x17,
            Keycode.J => 0x24,
            Keycode.K => 0x25,
            Keycode.L => 0x26,
            Keycode.M => 0x32,
            Keycode.N => 0x31,
            Keycode.O => 0x18,
            Keycode.P => 0x19,
            Keycode.Q => 0x10,
            Keycode.R => 0x13,
            Keycode.S => 0x1F,
            Keycode.T => 0x14,
            Keycode.U => 0x16,
            Keycode.V => 0x2F,
            Keycode.W => 0x11,
            Keycode.X => 0x2D,
            Keycode.Y => 0x15,
            Keycode.Z => 0x2C,
            Keycode.Num0 => 0x0B,
            Keycode.Num1 => 0x02,
            Keycode.Num2 => 0x03,
            Keycode.Num3 => 0x04,
            Keycode.Num4 => 0x05,
            Keycode.Num5 => 0x06,
            Keycode.Num6 => 0x07,
            Keycode.Num7 => 0x08,
            Keycode.Num8 => 0x09,
            Keycode.Num9 => 0x0A,
            Keycode.Space => 0x39,
            _ => 0
        };
        return scancode != 0;
    }

    public override void OnBackPressed()
    {
        if (_helpOverlay != null && _helpOverlay.Visibility == ViewStates.Visible)
        {
            _helpOverlay.Visibility = ViewStates.Gone;
            return;
        }

        if (_imeHeight > 0)
        {
            HideSoftKeyboard();
            return;
        }

        if (_keyboardAssistantBar != null && _keyboardAssistantBar.Visibility == ViewStates.Visible)
        {
            _keyboardAssistantBar.Visibility = ViewStates.Gone;
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
            _isExplicitDisconnect = true;
            _session?.Disconnect();
            Finish();
        });
        builder.SetNegativeButton("Keep Connected", (_, _) => { });
        builder.Show();
    }

    private void CheckAndShowFirstSessionGuide()
    {
        if (!AppPrefs.GetBool(AppPrefs.KeyHasSeenGestureHelp, false))
        {
            ShowFirstSessionGuide();
        }
    }

    private void ShowFirstSessionGuide()
    {
        if (_helpOverlay != null)
        {
            _helpOverlay.Visibility = ViewStates.Visible;
            return;
        }

        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;

        var overlay = new FrameLayout(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent),
            Elevation = 40f
        };
        overlay.SetBackgroundColor(Color.Argb(210, 8, 8, 12));

        var card = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Vertical,
            LayoutParameters = new FrameLayout.LayoutParams(
                (int)Math.Min(_screenWidth * 0.9f, 440 * density),
                ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = GravityFlags.Center
            }
        };
        card.SetPadding((int)(20 * density), (int)(18 * density), (int)(20 * density), (int)(18 * density));

        var cardBg = new GradientDrawable();
        cardBg.SetShape(ShapeType.Rectangle);
        cardBg.SetCornerRadius(16 * density);
        cardBg.SetColor(Color.Argb(245, 24, 24, 32));
        cardBg.SetStroke((int)(1.5f * density), Color.Argb(160, 0, 120, 215));
        card.Background = cardBg;

        var title = new TextView(this)
        {
            Text = "REMOTE DESKTOP GESTURES",
            TextSize = 14,
            Typeface = Typeface.DefaultBold
        };
        title.SetTextColor(Color.White);
        card.AddView(title);

        AddGuideItem(card, "👆 Single Tap", "Left Click on remote desktop");
        AddGuideItem(card, "👆...👆 Double Tap", "Double Click / Open application or folder");
        AddGuideItem(card, "✌ Two-Finger Tap", "Right Click context menu");
        AddGuideItem(card, "🤏 Pinch to Zoom", "Zoom in & out (1.0x to 5.0x)");
        AddGuideItem(card, "↔ Slide near Edge", "Smooth continuous auto-scroll");
        AddGuideItem(card, "⌨ Floating / Bottom Bar", "Soft keyboard and modifier keys (Ctrl, Alt, Shift, F1-F12)");

        var btnGotIt = new Button(this)
        {
            Text = "Got it",
            TextSize = 12,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                (int)(38 * density))
            {
                TopMargin = (int)(14 * density)
            }
        };
        var btnBg = new GradientDrawable();
        btnBg.SetShape(ShapeType.Rectangle);
        btnBg.SetCornerRadius(8 * density);
        btnBg.SetColor(Color.Argb(255, 0, 120, 215));
        btnGotIt.Background = btnBg;
        btnGotIt.SetTextColor(Color.White);
        btnGotIt.Click += (_, _) =>
        {
            AppPrefs.SetBool(AppPrefs.KeyHasSeenGestureHelp, true);
            overlay.Visibility = ViewStates.Gone;
        };
        card.AddView(btnGotIt);

        overlay.AddView(card);
        _helpOverlay = overlay;
        _rootLayout?.AddView(_helpOverlay);
    }

    private void AddGuideItem(LinearLayout card, string gesture, string desc)
    {
        float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
        var row = new LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal,
            LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = (int)(8 * density)
            }
        };

        var tvGesture = new TextView(this)
        {
            Text = gesture,
            TextSize = 11,
            Typeface = Typeface.DefaultBold,
            LayoutParameters = new LinearLayout.LayoutParams((int)(140 * density), ViewGroup.LayoutParams.WrapContent)
        };
        tvGesture.SetTextColor(Color.ParseColor("#8AB4F8"));

        var tvDesc = new TextView(this)
        {
            Text = desc,
            TextSize = 11,
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        };
        tvDesc.SetTextColor(Color.ParseColor("#D0D0D8"));

        row.AddView(tvGesture);
        row.AddView(tvDesc);
        card.AddView(row);
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
        StopEdgeScroll();

        if (_collapseRunnable != null)
        {
            _collapseHandler.RemoveCallbacks(_collapseRunnable);
        }

        if (_session != null)
        {
            UnbindSessionEvents(_session);

            if (_isExplicitDisconnect || !_session.IsConnected)
            {
                TearDownSessionNotification();
                RdpSessionBridge.IsConnected = false;
                RdpSessionBridge.ConnectedProfile = null;
                _session.Dispose();
                _session = null;
                RdpSessionBridge.ActiveSession = null;
                RdpSessionBridge.SessionStateChanged?.Invoke();
                MainActivity.Instance?.OnSessionEnded();
            }
        }

        _frameBitmap?.Recycle();
        _frameBitmap = null;
        _arrowCursorBitmap?.Recycle();
        _arrowCursorBitmap = null;
        _ibeamCursorBitmap?.Recycle();
        _ibeamCursorBitmap = null;
        _pixelBuffer = null;
        base.OnDestroy();
    }

    private class InsetListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private readonly RdpSessionActivity _act;
        public InsetListener(RdpSessionActivity act) => _act = act;

        public WindowInsetsCompat? OnApplyWindowInsets(View? v, WindowInsetsCompat? insets)
        {
            if (insets != null)
            {
                var imeInsets = insets.GetInsets(WindowInsetsCompat.Type.Ime());
                bool isImeVisible = insets.IsVisible(WindowInsetsCompat.Type.Ime());
                _act.OnImeInsetsChanged(imeInsets?.Bottom ?? 0, isImeVisible);
            }
            return insets;
        }
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
            float scaleRatio = newScale / oldScale;

            float screenCenterX = _act._screenWidth / 2f;
            int visibleH = _act._imeHeight > 0 ? Math.Max(100, _act._screenHeight - _act._imeHeight) : _act._screenHeight;
            float screenCenterY = visibleH / 2f;

            _act._panX = (focusX - screenCenterX) - scaleRatio * (focusX - screenCenterX - _act._panX);
            _act._panY = (focusY - screenCenterY) - scaleRatio * (focusY - screenCenterY - _act._panY);
            _act._scale = newScale;

            _act.CalculateViewportBounds(out float maxPanX, out float maxPanY);
            _act._panX = Math.Clamp(_act._panX, -maxPanX, maxPanX);
            _act._panY = Math.Clamp(_act._panY, -maxPanY, maxPanY);

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
