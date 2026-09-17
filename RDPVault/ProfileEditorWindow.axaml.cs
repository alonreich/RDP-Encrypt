using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RDPVault;

public partial class ProfileEditorWindow : Window
{
    /// <summary>
    /// The working copy. Editing never touches the stored profile until the caller
    /// applies it, so Cancel genuinely cancels.
    /// </summary>
    public RdpProfile Profile { get; }

    public ProfileEditorWindow() : this(null) { }

    public ProfileEditorWindow(RdpProfile? existing)
    {
        InitializeComponent();
        Title = existing == null ? "Add Profile" : "Edit Profile";
        Profile = existing?.Clone() ?? new RdpProfile();
        LoadProfileToUI();
    }

    /// <summary>Copies the edited values onto the real stored profile (keeping its Id).</summary>
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
        target.EnableWol = Profile.EnableWol;
        target.WolMacAddress = Profile.WolMacAddress;
        target.WolBroadcastIp = Profile.WolBroadcastIp;
        target.WolPort = Profile.WolPort;
        target.WolWaitSeconds = Profile.WolWaitSeconds;
        target.AllowClipboard = Profile.AllowClipboard;
        target.AllowDrives = Profile.AllowDrives;
        target.AllowPrinters = Profile.AllowPrinters;
        target.AllowSmartCards = Profile.AllowSmartCards;
        target.AllowUnverifiedServer = Profile.AllowUnverifiedServer;
        target.CertThumbprint = Profile.CertThumbprint;   // issue #2: keep the accepted certificate pin
        target.Notes = Profile.Notes;
    }

    private void LoadProfileToUI()
    {
        TxtName.Text = Profile.Name;
        TxtHost.Text = Profile.Host;
        TxtPort.Text = Profile.Port.ToString();
        TxtUsername.Text = Profile.Username;
        TxtPassword.Text = Profile.Password;
        TxtGateway.Text = Profile.GatewayHost;

        if (Profile.UseMultiMon) CmbResolution.SelectedIndex = 1;
        else if (Profile.FullScreen) CmbResolution.SelectedIndex = 0;
        else CmbResolution.SelectedIndex = (Profile.Width, Profile.Height) switch
        {
            (1920, 1080) => 2,
            (1600, 900) => 3,
            (1366, 768) => 4,
            (1280, 1024) => 5,
            (1280, 800) => 6,
            (1024, 768) => 7,
            (800, 600) => 8,
            _ => 0
        };

        CmbFullScreen.SelectedIndex = (int)Profile.FullScreenOverride;
        CmbMultiMon.SelectedIndex = (int)Profile.MultiMonOverride;
        CmbCertWarnings.SelectedIndex = (int)Profile.SuppressCertWarningsOverride;

        ChkEnableWol.IsChecked = Profile.EnableWol;
        TxtWolMac.Text = Profile.WolMacAddress;
        TxtWolBroadcast.Text = string.IsNullOrWhiteSpace(Profile.WolBroadcastIp) ? "255.255.255.255" : Profile.WolBroadcastIp;
        TxtWolPort.Text = Profile.WolPort > 0 ? Profile.WolPort.ToString() : "9";
        TxtWolWait.Text = Profile.WolWaitSeconds >= 0 ? Profile.WolWaitSeconds.ToString() : "5";
        PnlWolDetails.IsEnabled = Profile.EnableWol;
        ChkEnableWol.IsCheckedChanged += (_, _) => PnlWolDetails.IsEnabled = ChkEnableWol.IsChecked == true;

        ChkClipboard.IsChecked = Profile.AllowClipboard;
        ChkDrives.IsChecked = Profile.AllowDrives;
        ChkPrinters.IsChecked = Profile.AllowPrinters;
        ChkSmartCards.IsChecked = Profile.AllowSmartCards;
        ChkAllowUnverified.IsChecked = Profile.AllowUnverifiedServer;
    }

    /// <summary>Issue #14: the password was displayed in clear text in a plain TextBox.</summary>
    private void ChkShowPassword_Click(object? sender, RoutedEventArgs e)
        => TxtPassword.PasswordChar = ChkShowPassword.IsChecked == true ? '\0' : '•';

    /// <summary>
    /// Issue #15: there was no validation at all. An empty host silently became
    /// "localhost", an empty name saved blank, and any integer was accepted as a
    /// port - including 0 and 99999, which produce a .rdp file mstsc refuses.
    /// </summary>
    private bool Validate(out string error)
    {
        string name = (TxtName.Text ?? "").Trim();
        string host = (TxtHost.Text ?? "").Trim();
        string portText = (TxtPort.Text ?? "").Trim();

        if (name.Length == 0) { error = "Give this profile a name so you can recognise it in the list."; return false; }
        if (host.Length == 0) { error = "Enter the host name or IP address to connect to."; return false; }
        if (host.Contains(' ')) { error = "A host name cannot contain spaces."; return false; }

        if (portText.Length == 0) portText = "3389";
        if (!int.TryParse(portText, out int port) || port < 1 || port > 65535)
        {
            error = "The port must be a whole number between 1 and 65535.";
            return false;
        }

        if (ChkEnableWol.IsChecked == true)
        {
            string mac = (TxtWolMac.Text ?? "").Trim();
            string cleaned = new string(mac.Where(Uri.IsHexDigit).ToArray());
            if (cleaned.Length != 12)
            {
                error = "Wake-on-LAN requires a valid 12-digit MAC address (e.g. 00:11:22:33:44:55).";
                return false;
            }

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
        if (!Validate(out string error))
        {
            TxtError.Text = error;
            TxtError.IsVisible = true;
            return;
        }
        TxtError.IsVisible = false;

        string newHost = (TxtHost.Text ?? "").Trim();
        int newPort = int.TryParse((TxtPort.Text ?? "").Trim(), out int port) ? port : 3389;

        // Issue #2: a certificate pin is taken against a specific address. If the user
        // repoints this profile at a different host or port, the old approval is
        // meaningless and must not be replayed - drop it so they are asked again.
        // NOTE: this has to happen while Profile.Host / Profile.Port still hold the
        // OLD values, i.e. before the assignments below.
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

        int idx = CmbResolution.SelectedIndex;
        Profile.UseMultiMon = idx == 1;
        Profile.FullScreen = idx is 0 or 1;
        (Profile.Width, Profile.Height) = idx switch
        {
            2 => (1920, 1080),
            3 => (1600, 900),
            4 => (1366, 768),
            5 => (1280, 1024),
            6 => (1280, 800),
            7 => (1024, 768),
            8 => (800, 600),
            _ => (Profile.Width, Profile.Height)
        };

        Profile.FullScreenOverride = (TriStateOverride)Math.Clamp(CmbFullScreen.SelectedIndex, 0, 2);
        Profile.MultiMonOverride = (TriStateOverride)Math.Clamp(CmbMultiMon.SelectedIndex, 0, 2);
        Profile.SuppressCertWarningsOverride = (TriStateOverride)Math.Clamp(CmbCertWarnings.SelectedIndex, 0, 2);

        Profile.EnableWol = ChkEnableWol.IsChecked == true;
        Profile.WolMacAddress = (TxtWolMac.Text ?? "").Trim();
        Profile.WolBroadcastIp = string.IsNullOrWhiteSpace(TxtWolBroadcast.Text) ? "255.255.255.255" : TxtWolBroadcast.Text.Trim();
        Profile.WolPort = int.TryParse((TxtWolPort.Text ?? "").Trim(), out int wp) ? wp : 9;
        Profile.WolWaitSeconds = int.TryParse((TxtWolWait.Text ?? "").Trim(), out int ww) ? ww : 5;

        Profile.AllowClipboard = ChkClipboard.IsChecked ?? false;
        Profile.AllowDrives = ChkDrives.IsChecked ?? false;
        Profile.AllowPrinters = ChkPrinters.IsChecked ?? false;
        Profile.AllowSmartCards = ChkSmartCards.IsChecked ?? false;
        Profile.AllowUnverifiedServer = ChkAllowUnverified.IsChecked ?? false;

        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
