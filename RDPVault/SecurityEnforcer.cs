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

public static class SecurityEnforcer
{
    private static string LegacyFailsFile => Path.Combine(AppPaths.ExeDir, "rdpvault_fails.json");

    public static void RemoveLegacyState()
    {
        try { if (File.Exists(LegacyFailsFile)) File.Delete(LegacyFailsFile); } catch { }
    }

    private static long _lastFailureTick = 0;
    private static double _inProcessCooldownSeconds = 0;

    public static TimeSpan CooldownRemaining(VaultFile file)
    {
        if (!file.Policy.ThrottleEnabled) return TimeSpan.Zero;
        int n = file.Fails.Count;
        if (n < 5) return TimeSpan.Zero;

        double seconds = Math.Min(60, Math.Pow(2, Math.Min(n - 4, 6)));

        TimeSpan monotonicLeft = TimeSpan.Zero;
        if (_lastFailureTick > 0 && _inProcessCooldownSeconds > 0)
        {
            double elapsed = Stopwatch.GetElapsedTime(_lastFailureTick).TotalSeconds;
            if (elapsed < _inProcessCooldownSeconds)
                monotonicLeft = TimeSpan.FromSeconds(_inProcessCooldownSeconds - elapsed);
        }

        DateTime readyAt = file.Fails.LastFailUtc.AddSeconds(seconds);
        TimeSpan wallClockLeft = readyAt - DateTime.UtcNow;
        if (wallClockLeft < TimeSpan.Zero) wallClockLeft = TimeSpan.Zero;

        return monotonicLeft > wallClockLeft ? monotonicLeft : wallClockLeft;
    }

    public static FailureOutcome RecordFailure(VaultFile file, string vaultPath)
    {
        var now = DateTime.UtcNow;
        var policy = file.Policy;

        bool clockRolledBack = file.Fails.LastFailUtc != DateTime.MinValue && now < file.Fails.LastFailUtc;

        if (!clockRolledBack && (file.Fails.FirstFailUtc == DateTime.MinValue ||
            (now - file.Fails.FirstFailUtc).TotalMinutes > policy.WindowMinutes))
        {
            if (_lastFailureTick == 0 || Stopwatch.GetElapsedTime(_lastFailureTick).TotalMinutes > policy.WindowMinutes)
            {
                file.Fails.Count = 1;
                file.Fails.FirstFailUtc = now;
            }
            else
            {
                file.Fails.Count++;
            }
        }
        else
        {
            file.Fails.Count++;
        }

        file.Fails.LastFailUtc = clockRolledBack ? file.Fails.LastFailUtc.AddSeconds(1) : now;

        _lastFailureTick = Stopwatch.GetTimestamp();
        _inProcessCooldownSeconds = file.Fails.Count < 5 ? 0 : Math.Min(60, Math.Pow(2, Math.Min(file.Fails.Count - 4, 6)));

        bool destroy = policy.SelfDestructEnabled && file.Fails.Count >= policy.MaxAttempts;

        if (destroy)
        {
            ShredVault(vaultPath);
        }
        else
        {
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
            catch {}
        }
    }


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
