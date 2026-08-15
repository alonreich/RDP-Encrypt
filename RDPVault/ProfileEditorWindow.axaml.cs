using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RDPVault
{
    public partial class ProfileEditorWindow : Window
    {
        public RdpProfile Profile { get; } = new RdpProfile();

        public ProfileEditorWindow()
        {
            InitializeComponent();
        }

        public ProfileEditorWindow(RdpProfile profile)
        {
            InitializeComponent();
            Profile = profile;
            this.FindControl<TextBox>("TxtName").Text = profile.Name;
            this.FindControl<TextBox>("TxtHost").Text = profile.Host;
            this.FindControl<TextBox>("TxtPort").Text = profile.Port.ToString();
            this.FindControl<TextBox>("TxtUsername").Text = profile.Username;
            this.FindControl<TextBox>("TxtPassword").Text = profile.Password;
            
            var cmb = this.FindControl<ComboBox>("CmbResolution");
            if (profile.UseMultiMon) cmb.SelectedIndex = 1;
            else if (profile.FullScreen) cmb.SelectedIndex = 0;
            else if (profile.Width == 1920 && profile.Height == 1080) cmb.SelectedIndex = 2;
            else if (profile.Width == 1600 && profile.Height == 900) cmb.SelectedIndex = 3;
            else if (profile.Width == 1366 && profile.Height == 768) cmb.SelectedIndex = 4;
            else if (profile.Width == 1280 && profile.Height == 1024) cmb.SelectedIndex = 5;
            else if (profile.Width == 1280 && profile.Height == 800) cmb.SelectedIndex = 6;
            else if (profile.Width == 1024 && profile.Height == 768) cmb.SelectedIndex = 7;
            else if (profile.Width == 800 && profile.Height == 600) cmb.SelectedIndex = 8;
            else cmb.SelectedIndex = 0;

            this.FindControl<CheckBox>("ChkClipboard").IsChecked = profile.AllowClipboard;
            this.FindControl<CheckBox>("ChkDrives").IsChecked = profile.AllowDrives;
            this.FindControl<CheckBox>("ChkPrinters").IsChecked = profile.AllowPrinters;
            this.FindControl<CheckBox>("ChkSmartCards").IsChecked = profile.AllowSmartCards;
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            Profile.Name = this.FindControl<TextBox>("TxtName").Text ?? "Unnamed";
            Profile.Host = this.FindControl<TextBox>("TxtHost").Text ?? "localhost";
            if (int.TryParse(this.FindControl<TextBox>("TxtPort").Text, out int port)) Profile.Port = port;
            Profile.Username = this.FindControl<TextBox>("TxtUsername").Text ?? "";
            Profile.Password = this.FindControl<TextBox>("TxtPassword").Text ?? "";
            
            int idx = this.FindControl<ComboBox>("CmbResolution").SelectedIndex;
            Profile.UseMultiMon = idx == 1;
            Profile.FullScreen = idx == 0 || idx == 1;
            
            if (idx == 2) { Profile.Width = 1920; Profile.Height = 1080; }
            else if (idx == 3) { Profile.Width = 1600; Profile.Height = 900; }
            else if (idx == 4) { Profile.Width = 1366; Profile.Height = 768; }
            else if (idx == 5) { Profile.Width = 1280; Profile.Height = 1024; }
            else if (idx == 6) { Profile.Width = 1280; Profile.Height = 800; }
            else if (idx == 7) { Profile.Width = 1024; Profile.Height = 768; }
            else if (idx == 8) { Profile.Width = 800; Profile.Height = 600; }

            Profile.AllowClipboard = this.FindControl<CheckBox>("ChkClipboard").IsChecked ?? false;
            Profile.AllowDrives = this.FindControl<CheckBox>("ChkDrives").IsChecked ?? false;
            Profile.AllowPrinters = this.FindControl<CheckBox>("ChkPrinters").IsChecked ?? false;
            Profile.AllowSmartCards = this.FindControl<CheckBox>("ChkSmartCards").IsChecked ?? false;

            Close(true);
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}
