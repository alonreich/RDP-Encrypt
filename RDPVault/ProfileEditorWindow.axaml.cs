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
            LoadProfileToUI();
        }

        public ProfileEditorWindow(RdpProfile profile)
        {
            InitializeComponent();
            Profile = profile;
            LoadProfileToUI();
        }

        private void LoadProfileToUI()
        {
            this.FindControl<TextBox>("TxtName").Text = Profile.Name;
            this.FindControl<TextBox>("TxtHost").Text = Profile.Host;
            this.FindControl<TextBox>("TxtPort").Text = Profile.Port.ToString();
            this.FindControl<TextBox>("TxtUsername").Text = Profile.Username;
            this.FindControl<TextBox>("TxtPassword").Text = Profile.Password;
            
            var cmb = this.FindControl<ComboBox>("CmbResolution");
            if (Profile.UseMultiMon) cmb.SelectedIndex = 1;
            else if (Profile.FullScreen) cmb.SelectedIndex = 0;
            else if (Profile.Width == 1920 && Profile.Height == 1080) cmb.SelectedIndex = 2;
            else if (Profile.Width == 1600 && Profile.Height == 900) cmb.SelectedIndex = 3;
            else if (Profile.Width == 1366 && Profile.Height == 768) cmb.SelectedIndex = 4;
            else if (Profile.Width == 1280 && Profile.Height == 1024) cmb.SelectedIndex = 5;
            else if (Profile.Width == 1280 && Profile.Height == 800) cmb.SelectedIndex = 6;
            else if (Profile.Width == 1024 && Profile.Height == 768) cmb.SelectedIndex = 7;
            else if (Profile.Width == 800 && Profile.Height == 600) cmb.SelectedIndex = 8;
            else cmb.SelectedIndex = 0;

            this.FindControl<CheckBox>("ChkClipboard").IsChecked = Profile.AllowClipboard;
            this.FindControl<CheckBox>("ChkDrives").IsChecked = Profile.AllowDrives;
            this.FindControl<CheckBox>("ChkPrinters").IsChecked = Profile.AllowPrinters;
            this.FindControl<CheckBox>("ChkSmartCards").IsChecked = Profile.AllowSmartCards;
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
