using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Threading;

namespace RDPVault;

/// <summary>
/// Small shared dialogs, built in code so there is one consistent look and no extra
/// XAML/InitializeComponent pairs to keep in sync.
///
/// Responsive dialog layout with bounded dimensions, scrollable body and pinned action buttons (Issue #8).
/// </summary>
public static class Dialogs
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#0E0E10"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#141417"));
    private static readonly IBrush Border = new SolidColorBrush(Color.Parse("#2E2E35"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#EDEDED"));
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8A8A93"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#005FB8"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#B80000"));
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#2FBF71"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E8A030"));

    private static Window Shell(string title, double width, double height)
        => new()
        {
            Title = title,
            Width = width,
            MinWidth = Math.Min(380, width),
            MaxWidth = Math.Max(720, width * 1.5),
            MinHeight = Math.Min(160, height),
            MaxHeight = 850,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Bg,
            Foreground = Text,
            FontFamily = new FontFamily("Inter, Arial"),
            ShowInTaskbar = false
        };

    private static Grid CreateDialogContent(Control scrollableBody, Control bottomActionRow)
    {
        var root = new Grid
        {
            Margin = new Avalonia.Thickness(24),
            RowDefinitions = new RowDefinitions("*,Auto")
        };

        var scroller = new ScrollViewer
        {
            Content = scrollableBody,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 580
        };

        Grid.SetRow(scroller, 0);
        Grid.SetRow(bottomActionRow, 1);
        bottomActionRow.Margin = new Avalonia.Thickness(0, 16, 0, 0);

        root.Children.Add(scroller);
        root.Children.Add(bottomActionRow);
        return root;
    }

    private static TextBlock Label(string text, double size = 13, IBrush? brush = null, bool bold = false)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = brush ?? Text,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap
        };

    private static Button Btn(string text, bool accent = false, bool danger = false)
        => new()
        {
            Content = text,
            Padding = new Avalonia.Thickness(16, 8),
            CornerRadius = new Avalonia.CornerRadius(4),
            FontWeight = FontWeight.SemiBold,
            Foreground = Text,
            BorderBrush = Border,
            BorderThickness = new Avalonia.Thickness(1),
            Background = danger ? Danger : accent ? Accent : new SolidColorBrush(Color.Parse("#1A1A1E"))
        };

    private static TextBox Field(string watermark, bool password = false)
        => new()
        {
            Watermark = watermark,
            PasswordChar = password ? '•' : '\0',
            Background = Panel,
            BorderBrush = Border,
            Foreground = Text,
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(10, 8)
        };

    // ============================================================ confirm / message

    /// <summary>Issue #12: nothing destructive happens without this.</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message,
                                          string confirmText = "Continue", bool danger = false,
                                          string? typeToConfirm = null)
    {
        var w = Shell(title, 460, typeToConfirm == null ? 230 : 290);
        var confirm = Btn(confirmText, accent: !danger, danger: danger);
        var cancel = Btn("Cancel");
        var typed = Field($"Type {typeToConfirm} to confirm");

        confirm.IsEnabled = typeToConfirm == null;
        if (typeToConfirm != null)
        {
            typed.TextChanged += (_, _) =>
                confirm.IsEnabled = string.Equals(typed.Text?.Trim(), typeToConfirm, StringComparison.OrdinalIgnoreCase);
        }

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Label(title, 17, danger ? Danger : Text, bold: true));
        body.Children.Add(Label(message, 13, Dim));
        if (typeToConfirm != null) body.Children.Add(typed);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        row.Children.Add(cancel);
        row.Children.Add(confirm);
        w.Content = CreateDialogContent(body, row);

        confirm.Click += (_, _) => w.Close(true);
        cancel.Click += (_, _) => w.Close(false);
        return w.ShowDialog<bool>(owner);
    }

    public static Task MessageAsync(Window owner, string title, string message, bool isError = false)
    {
        var w = Shell(title, 460, 240);
        var close = Btn("OK", accent: true);
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Label(title, 17, isError ? Danger : Text, bold: true));
        body.Children.Add(Label(message, 13, Dim));
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { close }
        };
        w.Content = CreateDialogContent(body, row);
        close.Click += (_, _) => w.Close(true);
        return w.ShowDialog<bool>(owner);
    }

    // ============================================================ create vault (issue #3)

    public static Task<string?> CreateVaultAsync(Window owner)
    {
        var w = Shell("Create your vault", 470, 420);
        var pw1 = Field("Master password", password: true);
        var pw2 = Field("Type it again", password: true);
        var strength = Label("", 12, Dim);
        var error = Label("", 12, Danger);
        var create = Btn("Create vault", accent: true);
        var cancel = Btn("Cancel");
        create.IsEnabled = false;

        void Validate()
        {
            string a = pw1.Text ?? "", b = pw2.Text ?? "";
            strength.Text = a.Length == 0 ? "" : $"Strength: {Describe(a)}";
            strength.Foreground = a.Length >= 14 ? Ok : a.Length >= 10 ? Warn : Dim;

            if (a.Length > 0 && a.Length < 10) { error.Text = "Use at least 10 characters."; create.IsEnabled = false; return; }
            if (b.Length > 0 && a != b) { error.Text = "The two passwords do not match."; create.IsEnabled = false; return; }
            error.Text = "";
            create.IsEnabled = a.Length >= 10 && a == b;
        }

        pw1.TextChanged += (_, _) => Validate();
        pw2.TextChanged += (_, _) => Validate();
        pw2.KeyDown += (_, e) => { if (e.Key == Key.Enter && create.IsEnabled) w.Close(pw1.Text); };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Label("CREATE YOUR VAULT", 18, Text, bold: true));
        body.Children.Add(Label(
            "This password encrypts everything in the vault. Nobody - including this app - can read or reset it. " +
            "If you lose it, your only other way in is the Recovery Code shown on the next screen.",
            12, Dim));
        body.Children.Add(pw1);
        body.Children.Add(pw2);
        body.Children.Add(strength);
        body.Children.Add(error);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        row.Children.Add(cancel);
        row.Children.Add(create);
        w.Content = CreateDialogContent(body, row);

        create.Click += (_, _) => w.Close(pw1.Text);
        cancel.Click += (_, _) => w.Close(null);
        return w.ShowDialog<string?>(owner);
    }

    private static string Describe(string pw)
    {
        int score = 0;
        if (pw.Length >= 10) score++;
        if (pw.Length >= 14) score++;
        if (pw.Length >= 20) score++;
        bool letters = false, digits = false, other = false;
        foreach (char c in pw)
        {
            if (char.IsLetter(c)) letters = true;
            else if (char.IsDigit(c)) digits = true;
            else other = true;
        }
        if (letters && digits) score++;
        if (other) score++;
        return score switch
        {
            <= 1 => "weak",
            2 or 3 => "reasonable",
            4 => "strong",
            _ => "very strong"
        };
    }

    // ============================================================ recovery code (issue #2)

    /// <summary>Shows a freshly generated Recovery Code and refuses to close until it is acknowledged.</summary>
    public static Task ShowRecoveryCodeAsync(Window owner, string code, bool mustAcknowledge = true)
    {
        var w = Shell("Your Recovery Code", 560, 430);
        var done = Btn("I have saved it", accent: true);
        done.IsEnabled = !mustAcknowledge;

        bool acknowledged = !mustAcknowledge;
        w.Closing += (_, e) => { if (!acknowledged) e.Cancel = true; };

        var codeBox = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 16,
            Background = Panel,
            BorderBrush = Border,
            Foreground = Ok,
            Padding = new Avalonia.Thickness(12),
            MinHeight = 80
        };

        var ack = new CheckBox
        {
            Content = "I have written this code down or saved it somewhere safe",
            Foreground = Text,
            IsVisible = mustAcknowledge
        };
        ack.IsCheckedChanged += (_, _) =>
        {
            acknowledged = ack.IsChecked == true;
            done.IsEnabled = acknowledged;
        };

        var status = Label("", 12, Dim);
        var copy = Btn("Copy for 60 seconds");

        DispatcherTimer? clipTimer = null;

        void ClearClipboard()
        {
            clipTimer?.Stop();
            clipTimer = null;
            try
            {
                var clip = TopLevel.GetTopLevel(w)?.Clipboard;
                if (clip != null) _ = clip.SetTextAsync("");
            }
            catch { }
        }

        copy.Click += async (_, _) =>
        {
            try
            {
                var clip = TopLevel.GetTopLevel(w)?.Clipboard;
                if (clip == null) { status.Text = "Could not access the clipboard."; return; }
                await clip.SetTextAsync(code);
                status.Text = "Copied. The clipboard is cleared again in 60 seconds - paste it somewhere safe now. " +
                              "Windows clipboard history (Win+V) may still hold a copy.";

                clipTimer?.Stop();
                clipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                clipTimer.Tick += (_, _) =>
                {
                    ClearClipboard();
                    status.Text = "The clipboard has been cleared.";
                };
                clipTimer.Start();
            }
            catch { status.Text = "Could not access the clipboard."; }
        };

        w.Closed += (_, _) => ClearClipboard();

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Label("YOUR RECOVERY CODE", 18, Text, bold: true));
        body.Children.Add(Label(
            "This is the only other way into your vault if you forget the master password. " +
            "Write it on paper and keep it somewhere safe. It is not stored anywhere you can read it again - " +
            "you can only replace it with a new one.",
            12, Dim));
        body.Children.Add(codeBox);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        tools.Children.Add(copy);
        body.Children.Add(tools);
        body.Children.Add(status);
        body.Children.Add(ack);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { done }
        };
        w.Content = CreateDialogContent(body, row);

        done.Click += (_, _) => { acknowledged = true; w.Close(true); };
        return w.ShowDialog<bool>(owner);
    }

    /// <summary>Lock-screen entry point for "I forgot my master password".</summary>
    public static Task<string?> AskRecoveryCodeAsync(Window owner)
    {
        var w = Shell("Unlock with a Recovery Code", 520, 300);
        var box = Field("XXXX-XXXX-XXXX-XXXX-...");
        box.FontFamily = new FontFamily("Consolas, Courier New, monospace");
        var hint = Label("", 12, Dim);
        var unlock = Btn("Unlock", accent: true);
        var cancel = Btn("Cancel");
        unlock.IsEnabled = false;

        box.TextChanged += (_, _) =>
        {
            string norm = RecoveryCode.Normalize(box.Text ?? "");
            bool ok = RecoveryCode.LooksWellFormed(box.Text ?? "");
            unlock.IsEnabled = ok;
            hint.Text = norm.Length == 0 ? "" : $"{norm.Length} of 52 characters";
            hint.Foreground = ok ? Ok : Dim;
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && unlock.IsEnabled) w.Close(box.Text); };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Label("UNLOCK WITH A RECOVERY CODE", 17, Text, bold: true));
        body.Children.Add(Label(
            "Type the code exactly as printed. Dashes, spaces and upper/lower case do not matter.",
            12, Dim));
        body.Children.Add(box);
        body.Children.Add(hint);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        row.Children.Add(cancel);
        row.Children.Add(unlock);
        w.Content = CreateDialogContent(body, row);

        unlock.Click += (_, _) => w.Close(box.Text);
        cancel.Click += (_, _) => w.Close(null);
        return w.ShowDialog<string?>(owner);
    }

    // ============================================================ change password (issue #2)

    public static Task<(string Old, string New)?> ChangePasswordAsync(Window owner, bool requireOldPassword = true)
    {
        var w = Shell("Change master password", 470, requireOldPassword ? 400 : 360);
        var oldPw = Field("Current master password", password: true);
        oldPw.IsVisible = requireOldPassword;
        var new1 = Field("New master password", password: true);
        var new2 = Field("Type the new one again", password: true);
        var error = Label("", 12, Danger);
        var save = Btn("Change password", accent: true);
        var cancel = Btn("Cancel");
        save.IsEnabled = false;

        void Validate()
        {
            string o = oldPw.Text ?? "", a = new1.Text ?? "", b = new2.Text ?? "";
            if (a.Length > 0 && a.Length < 10) { error.Text = "Use at least 10 characters."; save.IsEnabled = false; return; }
            if (b.Length > 0 && a != b) { error.Text = "The two new passwords do not match."; save.IsEnabled = false; return; }
            error.Text = "";
            save.IsEnabled = (!requireOldPassword || o.Length > 0) && a.Length >= 10 && a == b;
        }

        oldPw.TextChanged += (_, _) => Validate();
        new1.TextChanged += (_, _) => Validate();
        new2.TextChanged += (_, _) => Validate();

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Label("CHANGE MASTER PASSWORD", 17, Text, bold: true));
        body.Children.Add(Label(
            requireOldPassword
                ? "Every Windows Hello quick unlock will be switched off and must be set up again on each PC. " +
                  "Your Recovery Code keeps working."
                : "You unlocked this vault with your Recovery Code, so the old master password is not needed. " +
                  "Every Windows Hello quick unlock will be switched off and must be set up again on each PC. " +
                  "Your Recovery Code keeps working.",
            12, Dim));
        body.Children.Add(oldPw);
        body.Children.Add(new1);
        body.Children.Add(new2);
        body.Children.Add(error);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        row.Children.Add(cancel);
        row.Children.Add(save);
        w.Content = CreateDialogContent(body, row);

        save.Click += (_, _) => w.Close(((string, string)?)(oldPw.Text ?? "", new1.Text ?? ""));
        cancel.Click += (_, _) => w.Close(null);
        return w.ShowDialog<(string Old, string New)?>(owner);
    }
}
