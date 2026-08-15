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

            UpdateHelloUI();
        }

        private void UpdateHelloUI()
        {
            bool hasHello = SessionManager.Current.HelloSealAvailable();
            var btn = this.FindControl<Button>("BtnToggleHello");
            var txt = this.FindControl<TextBlock>("TxtHelloStatus");
            
            if (hasHello)
            {
                btn.Content = "Disable TPM Unlock";
                btn.Classes.Remove("Accent");
                txt.Text = "Active: This machine is hardware-bound.";
                txt.Foreground = Avalonia.Media.Brushes.MediumSeaGreen;
            }
            else
            {
                btn.Content = "Enable TPM Unlock";
                btn.Classes.Add("Accent");
                txt.Text = "Not enrolled on this machine.";
                txt.Foreground = Avalonia.Media.Brushes.Gray;
            }
        }

        private async void BtnToggleHello_Click(object? sender, RoutedEventArgs e)
        {
            var btn = this.FindControl<Button>("BtnToggleHello");
            btn.IsEnabled = false;

            try
            {
                if (SessionManager.Current.HelloSealAvailable())
                {
                    SessionManager.Current.DisableHelloSeal();
                }
                else
                {
                    bool ok = await SessionManager.Current.EnableHelloSealAsync();
                    if (!ok)
                    {
                        var txt = this.FindControl<TextBlock>("TxtHelloStatus");
                        txt.Text = "Failed to enroll. User cancelled or hardware unsupported.";
                        txt.Foreground = Avalonia.Media.Brushes.IndianRed;
                        return;
                    }
                }
                
                SessionManager.Current.Save();
                UpdateHelloUI();
            }
            finally
            {
                btn.IsEnabled = true;
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
