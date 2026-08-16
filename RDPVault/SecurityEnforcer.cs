using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace RDPVault;

public enum BitLockerStatus { Unknown, Encrypted, NotEncrypted }

public readonly struct FailureOutcome
{
    public bool VaultDestroyed { get; init; }
    public int AttemptsUsed { get; init; }
    public int AttemptsAllowed { get; init; }
    public bool SelfDestructArmed { get; init; }
    public TimeSpan Cooldown { get; init; }
}

/// <summary>
/// Brute-force handling. See issue #4.
///
/// OLD BEHAVIOUR (broken in both directions):
///   * the counter lived in a plaintext file "rdpvault_fails.json" sitting next to
///     the vault, so an attacker just deleted it between attempts -> zero protection;
///   * the threshold read Payload?.Settings, which is ALWAYS null while locked, so
///     the user's configured value was silently ignored and 20 was always used;
///   * Settings accepted 0 or 1, so one typo could permanently delete the vault;
///   * it was on by default, and the vault had no recovery path.
///
/// NEW BEHAVIOUR:
///   * the counter lives inside vault.rdpv itself, so it cannot be reset without
///     touching the file the attacker is trying to open;
///   * the always-on defence is a non-destructive escalating delay;
///   * self-destruct is OFF by default, must be opted into, is clamped to >= 5
///     attempts, and cannot be armed until a Recovery Code exists.
/// </summary>
public static class SecurityEnforcer
{
    /// <summary>Legacy plaintext counter from v1. Removed on sight.</summary>
    private static string LegacyFailsFile => Path.Combine(AppPaths.ExeDir, "rdpvault_fails.json");

    public static void RemoveLegacyState()
    {
        try { if (File.Exists(LegacyFailsFile)) File.Delete(LegacyFailsFile); } catch { }
    }

    /// <summary>
    /// How long the user must wait before the next attempt is accepted.
    /// 0-4 failures: no delay. Then 2s, 4s, 8s ... capped at 60s.
    /// </summary>
    public static TimeSpan CooldownRemaining(VaultFile file)
    {
        if (!file.Policy.ThrottleEnabled) return TimeSpan.Zero;
        int n = file.Fails.Count;
        if (n < 5) return TimeSpan.Zero;

        double seconds = Math.Min(60, Math.Pow(2, Math.Min(n - 4, 6)));
        DateTime readyAt = file.Fails.LastFailUtc.AddSeconds(seconds);
        TimeSpan left = readyAt - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Records one wrong password/recovery code and persists it inside the vault envelope.</summary>
    public static FailureOutcome RecordFailure(VaultFile file, string vaultPath)
    {
        var now = DateTime.UtcNow;
        var policy = file.Policy;

        if (file.Fails.FirstFailUtc == DateTime.MinValue ||
            (now - file.Fails.FirstFailUtc).TotalMinutes > policy.WindowMinutes)
        {
            file.Fails.Count = 1;
            file.Fails.FirstFailUtc = now;
        }
        else
        {
            file.Fails.Count++;
        }
        file.Fails.LastFailUtc = now;

        bool destroy = policy.SelfDestructEnabled && file.Fails.Count >= policy.MaxAttempts;

        if (destroy)
        {
            ShredVault(vaultPath);
        }
        else
        {
            // Persisting the counter must never take the vault with it.
            try { VaultCrypto.SaveEnvelopeOnly(file, vaultPath); } catch { }
        }

        return new FailureOutcome
        {
            VaultDestroyed = destroy,
            AttemptsUsed = file.Fails.Count,
            AttemptsAllowed = policy.MaxAttempts,
            SelfDestructArmed = policy.SelfDestructEnabled,
            Cooldown = CooldownRemaining(file)
        };
    }

    public static void ClearFailures(VaultFile file, string vaultPath)
    {
        if (file.Fails.Count == 0 && file.Fails.FirstFailUtc == DateTime.MinValue) return;
        file.Fails = new FailState();
        try { VaultCrypto.SaveEnvelopeOnly(file, vaultPath); } catch { }
    }

    /// <summary>
    /// Overwrite-then-delete the vault and every copy of it. Only ever reached when
    /// the user explicitly armed self-destruct.
    /// </summary>
    private static void ShredVault(string vaultPath)
    {
        foreach (string path in new[] { vaultPath,
                                        vaultPath + AppPaths.BackupSuffix,
                                        vaultPath + AppPaths.TempSuffix })
        {
            try
            {
                if (!File.Exists(path)) continue;
                long len = new FileInfo(path).Length;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    byte[] noise = RandomNumberGenerator.GetBytes((int)Math.Min(len, 1 << 20));
                    long written = 0;
                    while (written < len)
                    {
                        int chunk = (int)Math.Min(noise.Length, len - written);
                        fs.Write(noise, 0, chunk);
                        written += chunk;
                    }
                    fs.Flush(true);
                }
                File.Delete(path);
            }
            catch { /* a failed shred must not stop the remaining deletions */ }
        }
    }

    // ---------------------------------------------------------------- BitLocker

    /// <summary>
    /// Issue #7: previously this method existed but was NEVER CALLED, while the
    /// Settings screen showed an "Enforce BitLocker" checkbox. It is now called on
    /// unlock and drives a visible warning. It is a warning, not a block - the app
    /// cannot encrypt the user's drive for them, and refusing to open their own
    /// vault would be worse than telling them the truth.
    ///
    /// manage-bde output is localised, so anything we cannot confidently parse is
    /// reported as Unknown rather than guessed at.
    /// </summary>
    public static BitLockerStatus CheckDrive(string driveLetter)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "manage-bde",
                Arguments = $"-status {driveLetter} -protectionaspoint",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return BitLockerStatus.Unknown;

            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return BitLockerStatus.Unknown; }

            if (output.Contains("Protection On", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Percentage Encrypted: 100", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Fully Encrypted", StringComparison.OrdinalIgnoreCase))
                return BitLockerStatus.Encrypted;

            if (output.Contains("Protection Off", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Fully Decrypted", StringComparison.OrdinalIgnoreCase))
                return BitLockerStatus.NotEncrypted;

            return BitLockerStatus.Unknown;
        }
        catch { return BitLockerStatus.Unknown; }
    }
}
