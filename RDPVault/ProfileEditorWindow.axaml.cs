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
            this.FindControl<TextBox>("TxtUsername").Text = profile.Username;
            this.FindControl<TextBox>("TxtPassword").Text = profile.Password;
            this.FindControl<CheckBox>("ChkMultiMon").IsChecked = profile.UseMultiMon;
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            Profile.Name = this.FindControl<TextBox>("TxtName").Text ?? "Unnamed";
            Profile.Host = this.FindControl<TextBox>("TxtHost").Text ?? "localhost";
            Profile.Username = this.FindControl<TextBox>("TxtUsername").Text ?? "";
            Profile.Password = this.FindControl<TextBox>("TxtPassword").Text ?? "";
            Profile.UseMultiMon = this.FindControl<CheckBox>("ChkMultiMon").IsChecked == true;
            Close(true);
        }

        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }
    }
}
