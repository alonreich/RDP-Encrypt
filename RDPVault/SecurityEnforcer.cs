using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;

namespace RDPVault;

public static class SecurityEnforcer
{
    private static readonly string FailsFile = Path.Combine(AppContext.BaseDirectory, "rdpvault_fails.json");
    
    public class FailState { public int Count { get; set; } = 0; public DateTime FirstFail { get; set; } = DateTime.MinValue; }

    public static void RecordFailedAttempt(int maxAttempts, int windowMinutes, string vaultPath)
    {
        var state = new FailState();
        if (File.Exists(FailsFile))
        {
            try { state = JsonSerializer.Deserialize<FailState>(File.ReadAllText(FailsFile)) ?? new FailState(); } catch { }
        }
        
        if ((DateTime.UtcNow - state.FirstFail).TotalMinutes > windowMinutes)
        {
            state.Count = 1;
            state.FirstFail = DateTime.UtcNow;
        }
        else
        {
            state.Count++;
        }
        
        File.WriteAllText(FailsFile, JsonSerializer.Serialize(state));
        
        if (state.Count >= maxAttempts)
        {
            // Self Destruct
            if (File.Exists(vaultPath)) File.Delete(vaultPath);
            File.Delete(FailsFile);
            Environment.Exit(-1);
        }
    }
    
    public static void ClearFailedAttempts()
    {
        if (File.Exists(FailsFile)) File.Delete(FailsFile);
    }
    
    public static bool IsBitLockerEnabled(string driveLetter)
    {
        try
        {
            var p = new Process();
            p.StartInfo.FileName = "manage-bde";
            p.StartInfo.Arguments = $"-status {driveLetter}";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Contains("Fully Encrypted") || output.Contains("Percentage Encrypted: 100%");
        }
        catch { return false; }
    }
}
