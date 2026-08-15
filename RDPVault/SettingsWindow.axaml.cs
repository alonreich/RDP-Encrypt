using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RDPVault
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            var settings = SessionManager.Current.Payload?.Settings;
            if (settings != null)
            {
                this.FindControl<TextBox>("TxtLockMinutes").Text = settings.LockMinutes.ToString();
                this.FindControl<CheckBox>("ChkKillSessions").IsChecked = settings.KillSessionsOnUsbRemoval;
                this.FindControl<CheckBox>("ChkForceMultiMon").IsChecked = settings.ForceMultiMon;
                this.FindControl<CheckBox>("ChkEnforceBitLocker").IsChecked = settings.EnforceBitLocker;
                this.FindControl<CheckBox>("ChkRequireFido2").IsChecked = settings.RequireFido2;
                this.FindControl<TextBox>("TxtSelfDestructAttempts").Text = settings.SelfDestructFailedAttempts.ToString();
                this.FindControl<TextBox>("TxtSelfDestructWindow").Text = settings.SelfDestructWindowMinutes.ToString();
            }
        }
        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            var settings = SessionManager.Current.Payload?.Settings;
            if (settings != null)
            {
                if (int.TryParse(this.FindControl<TextBox>("TxtLockMinutes").Text, out int lockM)) settings.LockMinutes = lockM;
                settings.KillSessionsOnUsbRemoval = this.FindControl<CheckBox>("ChkKillSessions").IsChecked == true;
                settings.ForceMultiMon = this.FindControl<CheckBox>("ChkForceMultiMon").IsChecked == true;
                settings.EnforceBitLocker = this.FindControl<CheckBox>("ChkEnforceBitLocker").IsChecked == true;
                settings.RequireFido2 = this.FindControl<CheckBox>("ChkRequireFido2").IsChecked == true;
                if (int.TryParse(this.FindControl<TextBox>("TxtSelfDestructAttempts").Text, out int sda)) settings.SelfDestructFailedAttempts = sda;
                if (int.TryParse(this.FindControl<TextBox>("TxtSelfDestructWindow").Text, out int sdw)) settings.SelfDestructWindowMinutes = sdw;
                SessionManager.Current.Save();
            }
            Close();
        }
        private void BtnCancel_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
