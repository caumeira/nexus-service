using System.IO;
using System.Runtime.InteropServices;

namespace Qos.Service.Platform;

/// <summary>
/// Routes Console.Out and Console.Error through a TextWriter that also writes
/// to a rotating service.log file under per-platform LocalAppData. Captures the
/// existing 170+ Console.Error.WriteLine call sites without touching them, so
/// crash context survives off the user's machine without provider-by-provider
/// migration to ILogger&lt;T&gt;.
///
/// Rotation: when service.log exceeds <see cref="MaxFileSizeBytes"/>, the file
/// is renamed to service.log.1 (overwriting any prior .1) and a fresh
/// service.log is started. Only one prior generation is kept; this is "best
/// effort" recovery, not audit logging.
/// </summary>
public static class ServiceLog
{
    public const long MaxFileSizeBytes = 5 * 1024 * 1024;

    private static readonly object Lock = new();
    private static StreamWriter? _writer;
    private static string? _path;

    public static string? LogFilePath => _path;

    public static void Initialize()
    {
        try
        {
            var dir = ResolveLogsDir();
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "service.log");
            RotateIfTooLarge(_path);
            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };

            Console.SetOut(new TeeTextWriter(Console.Out, _writer, isError: false));
            Console.SetError(new TeeTextWriter(Console.Error, _writer, isError: true));
            Console.Out.WriteLine($"[service-log] writing to {_path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[service-log] init failed: {ex.Message}");
        }
    }

    private static string ResolveLogsDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            return Path.Combine(home, "Library", "Logs", "qOS");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            return Path.Combine(home, ".local", "state", "qos", "logs");
        }
        // Windows: machine-scope logs under %ProgramData% so the LocalSystem
        // service can write them and an admin can inspect them post-incident.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "qOS", "logs");
    }

    private static void RotateIfTooLarge(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var fi = new FileInfo(path);
            if (fi.Length < MaxFileSizeBytes)
                return;
            var rotated = path + ".1";
            if (File.Exists(rotated))
                File.Delete(rotated);
            File.Move(path, rotated);
        }
        catch { /* best effort */ }
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly TextWriter _file;
        private readonly bool _isError;

        public TeeTextWriter(TextWriter primary, TextWriter file, bool isError)
        {
            _primary = primary;
            _file = file;
            _isError = isError;
        }

        public override System.Text.Encoding Encoding => _primary.Encoding;

        public override void Write(char value)
        {
            _primary.Write(value);
            lock (Lock)
            {
                _file.Write(value);
            }
        }

        public override void Write(string? value)
        {
            _primary.Write(value);
            lock (Lock)
            {
                _file.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            _primary.WriteLine(value);
            lock (Lock)
            {
                var prefix = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {(_isError ? "ERR" : "INF")} ";
                _file.WriteLine(prefix + (value ?? string.Empty));
            }
        }

        public override void WriteLine()
        {
            _primary.WriteLine();
            lock (Lock)
            {
                _file.WriteLine();
            }
        }
    }
}
