using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// In-memory-ish <see cref="IConfigStore"/> test double: an immediate
/// (non-debounced) file-backed store used as a dependency by tests that just
/// need a working config store (lighting, fan profiles). It is NOT a stand-in
/// for testing JsonConfigStore itself — that is covered against the real store
/// by JsonConfigStoreDebounceTests, JsonConfigStoreCorruptLoadTests, and
/// ConfigRoundTripIntegrationTests.
/// </summary>
internal sealed class TestableConfigStore : IConfigStore
{
    private readonly string _path;
    private NexusSettings? _cached;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TestableConfigStore(string path)
    {
        _path = path;
    }

    public string SettingsPath => _path;

    public NexusSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            if (!File.Exists(_path))
            {
                _cached = new NexusSettings();
                Persist(_cached);
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(_path);
                _cached = JsonSerializer.Deserialize<NexusSettings>(json, JsonOptions) ?? new NexusSettings();
            }
            catch
            {
                _cached = new NexusSettings();
            }

            return _cached;
        }
    }

    public void Update(Action<NexusSettings> mutator)
    {
        lock (_lock)
        {
            var doc = _cached ?? Load();
            mutator(doc);
            Persist(doc);
        }
        OnChanged?.Invoke();
    }

    public void Reload() { lock (_lock) _cached = null; }

    public void FlushNow() { }

    public event Action? OnChanged;

    private void Persist(NexusSettings doc)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        var tmpPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        File.WriteAllText(tmpPath, json);

        if (File.Exists(_path))
            File.Replace(tmpPath, _path, null);
        else
            File.Move(tmpPath, _path);
    }
}
