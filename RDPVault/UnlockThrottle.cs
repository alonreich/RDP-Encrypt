using System.Diagnostics;

namespace RDPVault;

/// <summary>Non-destructive Android password throttling. The counter stays in the encrypted-file envelope.</summary>
public sealed class UnlockThrottle
{
    private long _lastFailureTick;
    private double _lastDelay;
    public static double DelaySeconds(int failures) => failures < 5 ? 0 : Math.Min(60, Math.Pow(2, Math.Min(failures - 4, 6)));

    public TimeSpan Remaining(VaultFile file)
    {
        double delay = DelaySeconds(file.Fails.Count);
        double wall = (file.Fails.LastFailUtc.AddSeconds(delay) - DateTime.UtcNow).TotalSeconds;
        double monotonic = _lastFailureTick == 0 ? 0 : _lastDelay - Stopwatch.GetElapsedTime(_lastFailureTick).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, Math.Max(Math.Min(wall, 60), monotonic)));
    }

    public void Record(VaultFile file, string path)
    {
        var now = DateTime.UtcNow;
        if (file.Fails.FirstFailUtc == DateTime.MinValue ||
            now >= file.Fails.LastFailUtc && (now - file.Fails.FirstFailUtc).TotalMinutes > file.Policy.WindowMinutes)
            file.Fails = new FailState { FirstFailUtc = now };
        file.Fails.Count = Math.Min(file.Fails.Count, 100000) + 1;
        file.Fails.LastFailUtc = now;
        _lastFailureTick = Stopwatch.GetTimestamp();
        _lastDelay = DelaySeconds(file.Fails.Count);
        VaultCrypto.SaveEnvelopeOnly(file, path);
    }

    public void Clear(VaultFile file, string path)
    {
        _lastFailureTick = 0;
        _lastDelay = 0;
        if (file.Fails.Count == 0) return;
        file.Fails = new FailState();
        VaultCrypto.SaveEnvelopeOnly(file, path);
    }
}
