using System.Text.Json;
using Jellyfin.Plugin.Stinger.Model;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.Data;

/// <summary>
/// JSON-file persistence for per-item stinger results, under the server data folder.
/// </summary>
public class StingerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _dirPath;
    private readonly string _filePath;
    private readonly ILogger<StingerStore> _logger;
    private readonly object _lock = new();
    private Dictionary<Guid, StingerResult>? _cache;

    public StingerStore(IApplicationPaths applicationPaths, ILogger<StingerStore> logger)
    {
        _dirPath = Path.Combine(applicationPaths.DataPath, "stinger");
        _filePath = Path.Combine(_dirPath, "results.json");
        _logger = logger;
    }

    public StingerResult? Get(Guid itemId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(itemId, out var result) ? result : null;
        }
    }

    public IReadOnlyList<StingerResult> GetAll()
    {
        lock (_lock)
        {
            return Load().Values.ToList();
        }
    }

    public void Set(StingerResult result)
    {
        lock (_lock)
        {
            var data = Load();
            data[result.ItemId] = result;
            Save(data);
        }
    }

    public void Remove(Guid itemId)
    {
        lock (_lock)
        {
            var data = Load();
            if (data.Remove(itemId))
            {
                Save(data);
            }
        }
    }

    private Dictionary<Guid, StingerResult> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<StingerResult>>(json, JsonOptions);
                _cache = list?.ToDictionary(r => r.ItemId) ?? new Dictionary<Guid, StingerResult>();
                return _cache;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load stinger results from {Path}; starting empty", _filePath);
        }

        _cache = new Dictionary<Guid, StingerResult>();
        return _cache;
    }

    private void Save(Dictionary<Guid, StingerResult> data)
    {
        Directory.CreateDirectory(_dirPath);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data.Values.ToList(), JsonOptions));
        File.Move(tmp, _filePath, overwrite: true);
    }
}
