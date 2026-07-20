using System.IO;
using System.Text.Json;

namespace IoFtp.Desktop.Services;

internal sealed record SectionDefinition(string Name, Dictionary<string, string> SitePaths, int Hotkey = 0);

internal sealed class SectionStore
{
    private static readonly object Gate = new();
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "FluxFTP-sections.json");
    private readonly string _oldPath = Path.Combine(AppContext.BaseDirectory, "ioFTP-sections.json");

    public List<SectionDefinition> Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(_path)) return JsonSerializer.Deserialize<List<SectionDefinition>>(File.ReadAllText(_path)) ?? [];
                if (File.Exists(_oldPath))
                {
                    var migrated = JsonSerializer.Deserialize<List<SectionDefinition>>(File.ReadAllText(_oldPath)) ?? [];
                    SaveCore(migrated); return migrated;
                }
                var imported = ImportIoFtpdSections();
                if (imported.Count > 0) SaveCore(imported);
                return imported;
            }
            catch { return []; }
        }
    }

    public void Save(IEnumerable<SectionDefinition> sections)
    {
        lock (Gate) SaveCore(sections);
    }

    private void SaveCore(IEnumerable<SectionDefinition> sections)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(sections, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }

    private static List<SectionDefinition> ImportIoFtpdSections()
    {
        const string iniPath = @"C:\ioFTPD\system\ioFTPD.ini";
        if (!File.Exists(iniPath)) return [];
        var result = new List<SectionDefinition>(); var inSections = false;
        foreach (var raw in File.ReadLines(iniPath))
        {
            var line = raw.Trim();
            if (line.Equals("[Sections]", StringComparison.OrdinalIgnoreCase)) { inSections = true; continue; }
            if (!inSections) continue;
            if (line.StartsWith('[')) break;
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            var equals = line.IndexOf('='); if (equals <= 0) continue;
            var name = line[..equals].Trim(); if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line[(equals + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = fields.LastOrDefault(field => field.StartsWith('/'))?.TrimEnd('*').TrimEnd('/');
            if (string.IsNullOrWhiteSpace(path)) continue;
            var existing = result.FindIndex(section => section.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) result.Add(new(name, new(StringComparer.OrdinalIgnoreCase) { ["ioFTPD"] = path }));
            else result[existing].SitePaths["ioFTPD"] = path;
        }
        return result;
    }
}
