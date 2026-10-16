using System;
using Android.Content;

namespace RDPVault.Android.Platform;

public static class AppPrefs
{
    private const string StoreName = "rdpvault_ui_prefs";

    public const string KeyAutoTypePromptSuppressed = "autotype_prompt_suppressed";
    public const string KeyAllowScreenshots = "allow_screenshots";
    public const string KeyPreflightEnabled = "preflight_enabled";
    public const string KeyLastExportUtcTicks = "last_export_utc_ticks";
    public const string KeyLastVaultChangeUtcTicks = "last_vault_change_utc_ticks";
    public const string KeyBackupReminderDismissedTicks = "backup_reminder_dismissed_ticks";
    public const string KeyHasSeenGestureHelp = "has_seen_gesture_help";

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

    public static string GetString(string key, string fallback = "")
    {
        try { return Store?.GetString(key, fallback) ?? fallback; }
        catch { return fallback; }
    }

    public static void SetString(string key, string value)
    {
        try
        {
            var editor = Store?.Edit();
            editor?.PutString(key, value);
            editor?.Apply();
        }
        catch { }
    }

    public static void MarkVaultChanged() => SetLong(KeyLastVaultChangeUtcTicks, DateTime.UtcNow.Ticks);
    public static void MarkVaultExported() => SetLong(KeyLastExportUtcTicks, DateTime.UtcNow.Ticks);

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
