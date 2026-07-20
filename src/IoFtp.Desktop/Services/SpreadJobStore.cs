using System.IO;
using System.Text.Json;

namespace IoFtp.Desktop.Services;

internal sealed record SpreadJobDefinition(string Name, string Section, string SourceSite, string TargetSites, bool Enabled = true);

internal sealed class SpreadJobStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "FluxFTP-spreadjobs.json");
    private readonly string _oldPath = Path.Combine(AppContext.BaseDirectory, "ioFTP-spreadjobs.json");
    public List<SpreadJobDefinition> Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<List<SpreadJobDefinition>>(File.ReadAllText(_path)) ?? [];
            if (!File.Exists(_oldPath)) return [];
            var migrated = JsonSerializer.Deserialize<List<SpreadJobDefinition>>(File.ReadAllText(_oldPath)) ?? [];
            Save(migrated); return migrated;
        }
        catch { return []; }
    }
    public void Save(IEnumerable<SpreadJobDefinition> jobs)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }
}
