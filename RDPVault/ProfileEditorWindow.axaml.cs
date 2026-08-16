using System;
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
        target.AllowClipboard = Profile.AllowClipboard;
        target.AllowDrives = Profile.AllowDrives;
        target.AllowPrinters = Profile.AllowPrinters;
        target.AllowSmartCards = Profile.AllowSmartCards;
        target.AllowUnverifiedServer = Profile.AllowUnverifiedServer;
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

        Profile.Name = (TxtName.Text ?? "").Trim();
        Profile.Host = (TxtHost.Text ?? "").Trim();
        Profile.Port = int.TryParse((TxtPort.Text ?? "").Trim(), out int port) ? port : 3389;
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

        Profile.AllowClipboard = ChkClipboard.IsChecked ?? false;
        Profile.AllowDrives = ChkDrives.IsChecked ?? false;
        Profile.AllowPrinters = ChkPrinters.IsChecked ?? false;
        Profile.AllowSmartCards = ChkSmartCards.IsChecked ?? false;
        Profile.AllowUnverifiedServer = ChkAllowUnverified.IsChecked ?? false;

        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
