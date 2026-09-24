using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RDPVault;

public partial class ProfileEditorWindow : Window
{
    public RdpProfile Profile { get; }

    public ProfileEditorWindow() : this(null) { }

    public ProfileEditorWindow(RdpProfile? existing)
    {
        InitializeComponent();
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

        // Unified Display & Monitor Mode
        if (Profile.MultiMonOverride == TriStateOverride.InheritGlobal && Profile.FullScreenOverride == TriStateOverride.InheritGlobal)
        {
            CmbDisplayMode.SelectedIndex = 0;
        }
        else if (Profile.MultiMonOverride == TriStateOverride.Enabled || Profile.UseMultiMon)
        {
            CmbDisplayMode.SelectedIndex = 1;
        }
        else if (Profile.FullScreenOverride == TriStateOverride.Enabled || Profile.FullScreen)
        {
            CmbDisplayMode.SelectedIndex = 2;
        }
        else
        {
            CmbDisplayMode.SelectedIndex = (Profile.Width, Profile.Height) switch
            {
                (1920, 1080) => 3,
                (1600, 900) => 4,
                (1366, 768) => 5,
                (1280, 1024) => 6,
                (1280, 800) => 7,
                (1024, 768) => 8,
                (800, 600) => 9,
                _ => 3
            };
        }

        UpdateDisplayModeDesc();
        CmbDisplayMode.SelectionChanged += (_, _) => UpdateDisplayModeDesc();

        CmbCertWarnings.SelectedIndex = (int)Profile.SuppressCertWarningsOverride;

        // Wake-on-LAN
        ChkEnableWol.IsChecked = Profile.EnableWol;
        TxtWolMac.Text = Profile.WolMacAddress;
        TxtWolBroadcast.Text = (string.IsNullOrWhiteSpace(Profile.WolBroadcastIp) || Profile.WolBroadcastIp == "255.255.255.255") ? "" : Profile.WolBroadcastIp;
        TxtWolPort.Text = Profile.WolPort > 0 ? Profile.WolPort.ToString() : "9";
        TxtWolWait.Text = Profile.WolWaitSeconds >= 0 ? Profile.WolWaitSeconds.ToString() : "5";
        PnlWolDetails.IsEnabled = Profile.EnableWol;
        ChkEnableWol.IsCheckedChanged += (_, _) => PnlWolDetails.IsEnabled = ChkEnableWol.IsChecked == true;

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
        ChkAllowUnverified.IsChecked = Profile.AllowUnverifiedServer;
    }

    private void UpdateDisplayModeDesc()
    {
        var settings = SessionManager.Current.Payload?.Settings;
        int idx = CmbDisplayMode.SelectedIndex;
        TxtDisplayModeDesc.Text = idx switch
        {
            0 => $"Inherits global vault settings (currently: {(settings?.DefaultUseMultiMon == true ? "All Monitors" : (settings?.DefaultFullScreen == true ? "Single Monitor Full Screen" : "Windowed"))}).",
            1 => "Spans Remote Desktop across all physical monitors in full screen.",
            2 => "Opens Remote Desktop on a single monitor in full screen.",
            3 => "Opens Remote Desktop in a 1920 x 1080 window.",
            4 => "Opens Remote Desktop in a 1600 x 900 window.",
            5 => "Opens Remote Desktop in a 1366 x 768 window.",
            6 => "Opens Remote Desktop in a 1280 x 1024 window.",
            7 => "Opens Remote Desktop in a 1280 x 800 window.",
            8 => "Opens Remote Desktop in a 1024 x 768 window.",
            9 => "Opens Remote Desktop in an 800 x 600 window.",
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

        // Map simplified Display & Monitor mode
        int dispIdx = CmbDisplayMode.SelectedIndex;
        if (dispIdx == 0) // Default (Inherit Global)
        {
            Profile.FullScreenOverride = TriStateOverride.InheritGlobal;
            Profile.MultiMonOverride = TriStateOverride.InheritGlobal;
            Profile.UseMultiMon = false;
            Profile.FullScreen = true;
        }
        else if (dispIdx == 1) // All Monitors
        {
            Profile.FullScreenOverride = TriStateOverride.Enabled;
            Profile.MultiMonOverride = TriStateOverride.Enabled;
            Profile.UseMultiMon = true;
            Profile.FullScreen = true;
        }
        else if (dispIdx == 2) // Single Monitor Full Screen
        {
            Profile.FullScreenOverride = TriStateOverride.Enabled;
            Profile.MultiMonOverride = TriStateOverride.Disabled;
            Profile.UseMultiMon = false;
            Profile.FullScreen = true;
        }
        else // Windowed presets
        {
            Profile.FullScreenOverride = TriStateOverride.Disabled;
            Profile.MultiMonOverride = TriStateOverride.Disabled;
            Profile.UseMultiMon = false;
            Profile.FullScreen = false;
            (Profile.Width, Profile.Height) = dispIdx switch
            {
                3 => (1920, 1080),
                4 => (1600, 900),
                5 => (1366, 768),
                6 => (1280, 1024),
                7 => (1280, 800),
                8 => (1024, 768),
                9 => (800, 600),
                _ => (1920, 1080)
            };
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
        Profile.AllowUnverifiedServer = ChkAllowUnverified.IsChecked ?? false;

        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
