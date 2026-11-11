using System.Text.Json;

namespace HusSkjerm;

public sealed class DataOptions
{
    public string Directory { get; set; } = "data";
}

public sealed class ConfigStore
{
    private readonly object _lock = new();
    private readonly string _file;
    private PanelConfig _current;

    public ConfigStore(Microsoft.Extensions.Options.IOptions<DataOptions> options)
    {
        var dir = Path.GetFullPath(options.Value.Directory);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.Directory.CreateDirectory(ImagesDirectory = Path.Combine(dir, "images"));
        DataDirectory = dir;
        _file = Path.Combine(dir, "config.json");
        _current = Load();
    }

    public string DataDirectory { get; }
    public string ImagesDirectory { get; }

    public PanelConfig Current { get { lock (_lock) return _current; } }

    public bool ImageExists(string name) =>
        name == Path.GetFileName(name) && File.Exists(Path.Combine(ImagesDirectory, name));

    public PanelConfig Save(PanelConfig config)
    {
        lock (_lock)
        {
            config.Normalize(ImageExists);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, PanelConfig.Json));
            File.Move(tmp, _file, overwrite: true);
            return _current = config;
        }
    }

    private PanelConfig Load()
    {
        try
        {
            if (File.Exists(_file))
                return JsonSerializer.Deserialize<PanelConfig>(File.ReadAllText(_file), PanelConfig.Json)!.Normalize(ImageExists);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            File.Copy(_file, _file + ".broken", overwrite: true);
        }
        var config = PanelConfig.CreateDefault();
        File.WriteAllText(_file, JsonSerializer.Serialize(config, PanelConfig.Json));
        return config;
    }
}
