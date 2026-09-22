using System;
using Android.Content;

namespace RDPVault.Android.Platform;

/// <summary>
/// Device-local, non-secret preferences.
///
/// IMPORTANT: nothing secret is ever written here. Master keys, profile passwords and
/// recovery codes live ONLY inside vault.rdpv (encrypted) or inside the Android Keystore.
/// This store holds UI choices that must survive a vault lock (e.g. "don't ask me about
/// Auto-Type again") and therefore cannot live inside the encrypted payload.
/// </summary>
public static class AppPrefs
{
    private const string StoreName = "rdpvault_ui_prefs";

    // Keys
    public const string KeyAutoTypePromptSuppressed = "autotype_prompt_suppressed";
    public const string KeyAllowScreenshots = "allow_screenshots";
    public const string KeyPreflightEnabled = "preflight_enabled";
    public const string KeyLastExportUtcTicks = "last_export_utc_ticks";
    public const string KeyLastVaultChangeUtcTicks = "last_vault_change_utc_ticks";
    public const string KeyBackupReminderDismissedTicks = "backup_reminder_dismissed_ticks";

    private static ISharedPreferences? Store
    {
        get
        {
            try
            {
                var ctx = global::Android.App.Application.Context;
                return ctx?.GetSharedPreferences(StoreName, FileCreationMode.Private);
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool GetBool(string key, bool fallback = false)
    {
        try { return Store?.GetBoolean(key, fallback) ?? fallback; }
        catch { return fallback; }
    }

    public static void SetBool(string key, bool value)
    {
        try
        {
            var editor = Store?.Edit();
            editor?.PutBoolean(key, value);
            editor?.Apply();
        }
        catch { }
    }

    public static long GetLong(string key, long fallback = 0)
    {
        try { return Store?.GetLong(key, fallback) ?? fallback; }
        catch { return fallback; }
    }

    public static void SetLong(string key, long value)
    {
        try
        {
            var editor = Store?.Edit();
            editor?.PutLong(key, value);
            editor?.Apply();
        }
        catch { }
    }

    public static void MarkVaultChanged() => SetLong(KeyLastVaultChangeUtcTicks, DateTime.UtcNow.Ticks);
    public static void MarkVaultExported() => SetLong(KeyLastExportUtcTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// True when the vault has been modified since the last export AND the user has not
    /// dismissed the reminder since that modification. Drives the "your vault only exists
    /// on this phone" banner (uninstall = permanent data loss).
    /// </summary>
    public static bool ShouldNagAboutBackup()
    {
        long changed = GetLong(KeyLastVaultChangeUtcTicks);
        if (changed == 0) return false;
        if (GetLong(KeyLastExportUtcTicks) >= changed) return false;
        if (GetLong(KeyBackupReminderDismissedTicks) >= changed) return false;
        return true;
    }

    public static void DismissBackupReminder() => SetLong(KeyBackupReminderDismissedTicks, DateTime.UtcNow.Ticks);
}
