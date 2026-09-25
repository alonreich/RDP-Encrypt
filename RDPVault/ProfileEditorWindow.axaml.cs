using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RDPVault;

public partial class ProfileEditorWindow : Window
{
    public RdpProfile Profile { get; }

    public ProfileEditorWindow() : this(null) { }

    public ProfileEditorWindow(RdpProfile? existing)
    {
        InitializeComponent();

        AddHandler(InputElement.PointerPressedEvent, (_, _) => SessionManager.Current.Touch(),
                   RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyDownEvent, (_, _) => SessionManager.Current.Touch(),
                   RoutingStrategies.Tunnel, handledEventsToo: true);

        Title = existing == null ? "Add Profile" : "Edit Profile";
        Profile = existing?.Clone() ?? new RdpProfile();
        LoadProfileToUI();
    }

    public void ApplyTo(RdpProfile target)
    {
        target.Name = Profile.Name;
        target.Host = Profile.Host;
        target.Port = Profile.Port;
        target.Username = Profile.Username;
        target.Password = Profile.Password;
        target.GatewayHost = Profile.GatewayHost;
        target.UseMultiMon = Profile.UseMultiMon;
        target.FullScreen = Profile.FullScreen;
        target.Width = Profile.Width;
        target.Height = Profile.Height;
        target.SuppressCertWarningsOverride = Profile.SuppressCertWarningsOverride;
        target.FullScreenOverride = Profile.FullScreenOverride;
        target.MultiMonOverride = Profile.MultiMonOverride;
        target.AllowClipboardOverride = Profile.AllowClipboardOverride;
        target.EnableWol = Profile.EnableWol;
        target.WolMacAddress = Profile.WolMacAddress;
        target.WolBroadcastIp = Profile.WolBroadcastIp;
        target.WolPort = Profile.WolPort;
        target.WolWaitSeconds = Profile.WolWaitSeconds;
        target.EnableIcmpKnock = Profile.EnableIcmpKnock;
        target.KnockProtocol = Profile.KnockProtocol;
        target.KnockTcpPort = Profile.KnockTcpPort;
        target.KnockDelaySeconds = Profile.KnockDelaySeconds;
        target.IcmpKnockSignature = Profile.IcmpKnockSignature;
        target.AllowClipboard = Profile.AllowClipboard;
        target.AllowDrives = Profile.AllowDrives;
        target.AllowPrinters = Profile.AllowPrinters;
        target.AllowSmartCards = Profile.AllowSmartCards;
        target.AllowUnverifiedServer = Profile.AllowUnverifiedServer;
        target.CertThumbprint = Profile.CertThumbprint;
        target.ResolutionPreset = Profile.ResolutionPreset;
        target.Notes = Profile.Notes;
    }

    private void LoadProfileToUI()
    {
        var settings = SessionManager.Current.Payload?.Settings;

        TxtName.Text = Profile.Name;
        TxtHost.Text = Profile.Host;
        TxtPort.Text = Profile.Port > 0 ? Profile.Port.ToString() : "3389";
        TxtUsername.Text = Profile.Username;
        TxtPassword.Text = Profile.Password;
        TxtGateway.Text = Profile.GatewayHost;

        // Port Knocking
        ChkEnableKnock.IsChecked = Profile.EnableIcmpKnock;
        PnlKnockDetails.IsVisible = Profile.EnableIcmpKnock;
        ChkEnableKnock.IsCheckedChanged += (_, _) => PnlKnockDetails.IsVisible = ChkEnableKnock.IsChecked == true;

        bool isTcp = string.Equals(Profile.KnockProtocol, "TCP", StringComparison.OrdinalIgnoreCase);
        CmbKnockProtocol.SelectedIndex = isTcp ? 1 : 0;
        PnlKnockTcp.IsVisible = isTcp;
        PnlKnockIcmp.IsVisible = !isTcp;

        CmbKnockProtocol.SelectionChanged += (_, _) =>
        {
            bool tcpSelected = CmbKnockProtocol.SelectedIndex == 1;
            PnlKnockTcp.IsVisible = tcpSelected;
            PnlKnockIcmp.IsVisible = !tcpSelected;
        };

        TxtIcmpSignature.Text = Profile.IcmpKnockSignature;
        BtnGenerateIcmpSignature.Click += (_, _) => TxtIcmpSignature.Text = IcmpKnock.GenerateSignature();
        TxtKnockTcpPort.Text = Profile.KnockTcpPort > 0 ? Profile.KnockTcpPort.ToString() : "7777";
        TxtKnockDelay.Text = Profile.KnockDelaySeconds >= 0 ? Profile.KnockDelaySeconds.ToString() : "2";

        // Unified Display & Monitor Mode (Streamlined 3-option picker)
        if (Profile.MultiMonOverride == TriStateOverride.Enabled || Profile.UseMultiMon || Profile.ResolutionPreset == "MultiMon")
        {
            CmbDisplayMode.SelectedIndex = 1;
        }
        else if (Profile.ResolutionPreset == "Custom" || (Profile.Width > 0 && Profile.Height > 0 && (Profile.Width != 1920 || Profile.Height != 1080)))
        {
            CmbDisplayMode.SelectedIndex = 2;
            TxtCustomWidth.Text = Profile.Width > 0 ? Profile.Width.ToString() : "1920";
            TxtCustomHeight.Text = Profile.Height > 0 ? Profile.Height.ToString() : "1080";
        }
        else
        {
            // Default 1080p Standard Landscape Full Screen
            CmbDisplayMode.SelectedIndex = 0;
        }

        PnlCustomRes.IsVisible = CmbDisplayMode.SelectedIndex == 2;
        UpdateDisplayModeDesc();
        CmbDisplayMode.SelectionChanged += (_, _) =>
        {
            PnlCustomRes.IsVisible = CmbDisplayMode.SelectedIndex == 2;
            UpdateDisplayModeDesc();
        };

        CmbCertWarnings.SelectedIndex = (int)Profile.SuppressCertWarningsOverride;

        // Wake-on-LAN: collapsible disclosure
        ChkEnableWol.IsChecked = Profile.EnableWol;
        TxtWolMac.Text = Profile.WolMacAddress;
        TxtWolBroadcast.Text = (string.IsNullOrWhiteSpace(Profile.WolBroadcastIp) || Profile.WolBroadcastIp == "255.255.255.255") ? "" : Profile.WolBroadcastIp;
        TxtWolPort.Text = Profile.WolPort > 0 ? Profile.WolPort.ToString() : "9";
        TxtWolWait.Text = Profile.WolWaitSeconds >= 0 ? Profile.WolWaitSeconds.ToString() : "5";
        PnlWolDetails.IsVisible = Profile.EnableWol;
        ChkEnableWol.IsCheckedChanged += (_, _) => PnlWolDetails.IsVisible = ChkEnableWol.IsChecked == true;

        TxtWolMac.LostFocus += (_, _) =>
        {
            if (MacAddressHelper.TryNormalizeMac(TxtWolMac.Text, out string formatted, out _))
            {
                TxtWolMac.Text = formatted;
            }
        };

        // Local resources
        ChkClipboard.IsChecked = Profile.ResolveAllowClipboard(settings);
        ChkDrives.IsChecked = Profile.AllowDrives;
        ChkPrinters.IsChecked = Profile.AllowPrinters;
        ChkSmartCards.IsChecked = Profile.AllowSmartCards;
    }

    private void UpdateDisplayModeDesc()
    {
        int idx = CmbDisplayMode.SelectedIndex;
        TxtDisplayModeDesc.Text = idx switch
        {
            0 => "Opens Remote Desktop in 1920 x 1080 (1080p Full HD) full screen.",
            1 => "Spans Remote Desktop across all physical monitors in full screen.",
            2 => "Opens Remote Desktop in a window sized to your custom width and height.",
            _ => ""
        };
    }

    private void ChkShowPassword_Click(object? sender, RoutedEventArgs e)
        => TxtPassword.PasswordChar = ChkShowPassword.IsChecked == true ? '\0' : '•';

    private bool Validate(out string error, out ConnectionEndpoint endpoint)
    {
        endpoint = default;
        string name = (TxtName.Text ?? "").Trim();
        string host = (TxtHost.Text ?? "").Trim();
        string portText = (TxtPort.Text ?? "").Trim();

        if (name.Length == 0) { error = "Give this profile a name so you can recognise it in the list."; return false; }
        if (host.Length == 0) { error = "Enter the host name or IP address to connect to."; return false; }

        if (portText.Length == 0) portText = "3389";
        if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
        {
            error = "The port must be a whole number between 1 and 65535.";
            return false;
        }

        if (!ConnectionEndpoint.TryParse(host, out endpoint, out string epErr, port))
        {
            error = epErr;
            return false;
        }

        if (ChkEnableKnock.IsChecked == true)
        {
            bool isTcp = CmbKnockProtocol.SelectedIndex == 1;
            if (isTcp)
            {
                string tcpText = (TxtKnockTcpPort.Text ?? "").Trim();
                if (!int.TryParse(tcpText, out int knockPort) || knockPort < 1 || knockPort > 65535)
                {
                    error = "Knock TCP port must be a whole number between 1 and 65535.";
                    return false;
                }
            }
            else
            {
                try
                {
                    _ = IcmpKnock.ParseSignature(TxtIcmpSignature.Text ?? "");
                }
                catch (ArgumentException ex) { error = ex.Message; return false; }
            }

            string delayText = (TxtKnockDelay.Text ?? "").Trim();
            if (!int.TryParse(delayText, out int delaySec) || delaySec < 0 || delaySec > 300)
            {
                error = "Knock delay must be a number of seconds between 0 and 300.";
                return false;
            }
        }

        if (ChkEnableWol.IsChecked == true)
        {
            if (!MacAddressHelper.TryNormalizeMac(TxtWolMac.Text, out string formattedMac, out string macError))
            {
                error = macError;
                return false;
            }
            TxtWolMac.Text = formattedMac;

            string wolPortText = (TxtWolPort.Text ?? "").Trim();
            if (!int.TryParse(wolPortText, out int wolPort) || wolPort < 1 || wolPort > 65535)
            {
                error = "Wake-on-LAN port must be a whole number between 1 and 65535.";
                return false;
            }

            string wolWaitText = (TxtWolWait.Text ?? "").Trim();
            if (!int.TryParse(wolWaitText, out int waitSec) || waitSec < 0 || waitSec > 300)
            {
                error = "Wake-on-LAN wait time must be between 0 and 300 seconds.";
                return false;
            }
        }

        if (CmbDisplayMode.SelectedIndex == 2)
        {
            string wText = (TxtCustomWidth.Text ?? "").Trim();
            string hText = (TxtCustomHeight.Text ?? "").Trim();
            if (!int.TryParse(wText, out int w) || w < 640 || w > 7680)
            {
                error = "Custom width must be between 640 and 7680 pixels.";
                return false;
            }
            if (!int.TryParse(hText, out int h) || h < 480 || h > 4320)
            {
                error = "Custom height must be between 480 and 4320 pixels.";
                return false;
            }
        }

        error = "";
        return true;
    }

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        if (!Validate(out string error, out var endpoint))
        {
            TxtError.Text = error;
            TxtError.IsVisible = true;
            return;
        }
        TxtError.IsVisible = false;

        string newHost = endpoint.Host;
        int newPort = endpoint.Port; // Authoritative separate port

        if (!string.Equals(Profile.Host, newHost, StringComparison.OrdinalIgnoreCase) ||
            Profile.Port != newPort)
        {
            Profile.CertThumbprint = "";
        }

        Profile.Name = (TxtName.Text ?? "").Trim();
        Profile.Host = newHost;
        Profile.Port = newPort;
        Profile.Username = (TxtUsername.Text ?? "").Trim();
        Profile.Password = TxtPassword.Text ?? "";
        Profile.GatewayHost = (TxtGateway.Text ?? "").Trim();

        // Map simplified Display & Monitor mode (3 clean options)
        int dispIdx = CmbDisplayMode.SelectedIndex;
        if (dispIdx == 1) // MultiMon
        {
            Profile.ResolutionPreset = "MultiMon";
            Profile.FullScreenOverride = TriStateOverride.Enabled;
            Profile.MultiMonOverride = TriStateOverride.Enabled;
            Profile.UseMultiMon = true;
            Profile.FullScreen = true;
        }
        else if (dispIdx == 2) // Custom
        {
            Profile.ResolutionPreset = "Custom";
            Profile.FullScreenOverride = TriStateOverride.Disabled;
            Profile.MultiMonOverride = TriStateOverride.Disabled;
            Profile.UseMultiMon = false;
            Profile.FullScreen = false;
            int.TryParse((TxtCustomWidth.Text ?? "").Trim(), out int cw);
            int.TryParse((TxtCustomHeight.Text ?? "").Trim(), out int ch);
            Profile.Width = cw > 0 ? cw : 1920;
            Profile.Height = ch > 0 ? ch : 1080;
        }
        else // 1080p Standard Full Screen [Default]
        {
            Profile.ResolutionPreset = "1920x1080";
            Profile.Width = 1920;
            Profile.Height = 1080;
            Profile.FullScreenOverride = TriStateOverride.Enabled;
            Profile.MultiMonOverride = TriStateOverride.Disabled;
            Profile.UseMultiMon = false;
            Profile.FullScreen = true;
        }

        Profile.SuppressCertWarningsOverride = (TriStateOverride)Math.Clamp(CmbCertWarnings.SelectedIndex, 0, 2);

        // Port Knocking
        Profile.EnableIcmpKnock = ChkEnableKnock.IsChecked == true;
        Profile.KnockProtocol = CmbKnockProtocol.SelectedIndex == 1 ? "TCP" : "ICMP";
        Profile.IcmpKnockSignature = (TxtIcmpSignature.Text ?? "").Trim();
        Profile.KnockTcpPort = int.TryParse((TxtKnockTcpPort.Text ?? "").Trim(), out int kp) ? kp : 7777;
        Profile.KnockDelaySeconds = int.TryParse((TxtKnockDelay.Text ?? "").Trim(), out int kd) ? kd : 2;

        // WOL
        Profile.EnableWol = ChkEnableWol.IsChecked == true;
        if (Profile.EnableWol && MacAddressHelper.TryNormalizeMac(TxtWolMac.Text, out string normMac, out _))
        {
            Profile.WolMacAddress = normMac;
            TxtWolMac.Text = normMac;
        }
        else
        {
            Profile.WolMacAddress = (TxtWolMac.Text ?? "").Trim();
        }
        Profile.WolBroadcastIp = string.IsNullOrWhiteSpace(TxtWolBroadcast.Text) ? "255.255.255.255" : TxtWolBroadcast.Text.Trim();
        Profile.WolPort = int.TryParse((TxtWolPort.Text ?? "").Trim(), out int wp) ? wp : 9;
        Profile.WolWaitSeconds = int.TryParse((TxtWolWait.Text ?? "").Trim(), out int ww) ? ww : 5;

        // Resources
        var globalSettings = SessionManager.Current.Payload?.Settings;
        bool globalClipboard = globalSettings?.DefaultAllowClipboard ?? true;
        bool profileClipboard = ChkClipboard.IsChecked ?? true;

        Profile.AllowClipboard = profileClipboard;
        Profile.AllowClipboardOverride = profileClipboard == globalClipboard
            ? TriStateOverride.InheritGlobal
            : (profileClipboard ? TriStateOverride.Enabled : TriStateOverride.Disabled);

        Profile.AllowDrives = ChkDrives.IsChecked ?? false;
        Profile.AllowPrinters = ChkPrinters.IsChecked ?? false;
        Profile.AllowSmartCards = ChkSmartCards.IsChecked ?? false;
        Profile.AllowUnverifiedServer = Profile.SuppressCertWarningsOverride == TriStateOverride.Enabled;

        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
