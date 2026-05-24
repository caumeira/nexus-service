using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Qos.Service.Serialization;

namespace Qos.Service.Persistence;

/// <summary>
/// File-backed QosSettings store.
/// Path: ~/Library/Application Support/Qos/settings.json on macOS,
///       %LOCALAPPDATA%/Qos/settings.json on Windows,
///       $XDG_CONFIG_HOME/Qos/settings.json (or ~/.config/Qos) on Linux.
///
/// Concurrency: a single global lock around load/save. Updates mutate the in-memory
/// doc synchronously, but the disk write is coalesced to a short debounce window so
/// lighting slider drags (10-20 Hz) don't serialize and fsync the whole settings
/// JSON on every frame. Dispose() flushes any pending write.
///
/// Atomic write: serialize -> write to settings.json.tmp -> File.Replace into place.
/// If the destination doesn't exist yet (first run), File.Move handles that path.
/// </summary>
public sealed class JsonConfigStore : IConfigStore, IDisposable
{
    private const int FlushDebounceMs = 300;

    private readonly object _lock = new();
    private QosSettings? _cached;
    private Timer? _flushTimer;
    private bool _dirty;
    private bool _disposed;

    public JsonConfigStore()
    {
        SettingsPath = ResolveSettingsPath();
    }

    // Test-only ctor: lets unit tests point the store at a throwaway path
    // instead of clobbering the user's real settings.json. Prod wiring uses
    // the parameterless ctor registered in DI.
    internal JsonConfigStore(string settingsPath)
    {
        SettingsPath = settingsPath;
    }

    public string SettingsPath { get; }

    public QosSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!File.Exists(SettingsPath))
            {
                _cached = new QosSettings();
                Persist(_cached);
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                _cached = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.QosSettings) ?? new QosSettings();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[qos-service] settings.json corrupted, starting fresh: {ex.Message}");
                _cached = new QosSettings();
            }

            return _cached;
        }
    }

    public event Action? OnChanged;

    public void Update(Action<QosSettings> mutator)
    {
        lock (_lock)
        {
            var doc = _cached ?? Load();
            mutator(doc);
            _dirty = true;
            ScheduleFlushLocked();
        }
        try
        { OnChanged?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"[config-store] OnChanged handler threw: {ex.Message}"); }
    }

    public void Reload()
    {
        lock (_lock)
        { _cached = null; _dirty = false; }
    }

    /// <summary>Force any pending debounced write to run now. Safe to call from any thread.</summary>
    public void FlushNow() => FlushPending();

    private void ScheduleFlushLocked()
    {
        if (_disposed)
        {
            return;
        }
        _flushTimer ??= new Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
        _flushTimer.Change(FlushDebounceMs, Timeout.Infinite);
    }

    private void FlushPending()
    {
        // Serialize under the lock (produces a string - fast) so no other Update
        // can mutate the doc mid-serialization. Do the disk write outside the
        // lock so sliders aren't blocked on fsync.
        string? json;
        lock (_lock)
        {
            if (!_dirty || _cached is null)
            {
                return;
            }
            json = JsonSerializer.Serialize(_cached, PersistenceJsonContext.Default.QosSettings);
            _dirty = false;
        }

        try
        { WriteAtomic(json); }
        catch (Exception ex) { Console.Error.WriteLine($"[config-store] flush failed: {ex.Message}"); }
    }

    private void Persist(QosSettings doc)
    {
        // Used only for the first-run synchronous write (Load sees no settings
        // file and writes defaults immediately). Runtime updates go through the
        // debounced flush path.
        var json = JsonSerializer.Serialize(doc, PersistenceJsonContext.Default.QosSettings);
        WriteAtomic(json);
    }

    private void WriteAtomic(string json)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        AtomicJsonFile.Write(SettingsPath, json);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
        }
        // Ensure the last in-memory update hits disk on shutdown.
        FlushPending();
    }

    private static string ResolveSettingsPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Qos", "settings.json");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Machine-scope: settings belong to the LocalSystem service, not the
            // logged-in user. CommonApplicationData = %ProgramData%.
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Qos", "settings.json");
        }

        // Linux / others
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Qos", "settings.json");
    }

}
