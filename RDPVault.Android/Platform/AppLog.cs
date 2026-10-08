using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Android.Content;
using Android.Runtime;

namespace RDPVault.Android.Platform;

public static class AppLog
{
    private const string Tag = "RDPVault";
    private static readonly object _sync = new();
    private static string? _logFilePath;
    private static readonly ConcurrentQueue<string> _recent = new();
    private const int MaxRecent = 500;

    public static void Initialize(Context context)
    {
        try
        {
            var dir = context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                _logFilePath = Path.Combine(dir, "rdpvault.log");
            }
        }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Error("Unhandled AppDomain exception: " + e.ExceptionObject);
        };

        AndroidEnvironment.UnhandledExceptionRaiser += (s, e) =>
        {
            Error("Unhandled AndroidEnvironment exception: " + e.Exception);
        };
    }

    public static string LogPath => _logFilePath ?? "";

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            switch (level)
            {
                case "ERROR":
                    global::Android.Util.Log.Error(Tag, message);
                    break;
                case "WARN":
                    global::Android.Util.Log.Warn(Tag, message);
                    break;
                default:
                    global::Android.Util.Log.Info(Tag, message);
                    break;
            }

            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            _recent.Enqueue(line);
            while (_recent.Count > MaxRecent) _recent.TryDequeue(out _);

            if (_logFilePath != null)
            {
                lock (_sync)
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
        }
        catch { }
    }

    public static string GetAllLogs()
    {
        try
        {
            if (_logFilePath != null && File.Exists(_logFilePath))
            {
                return File.ReadAllText(_logFilePath);
            }
        }
        catch { }
        return string.Join(Environment.NewLine, _recent);
    }
}
