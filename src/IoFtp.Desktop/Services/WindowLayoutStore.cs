using System.IO;
using System.Text.Json;

namespace IoFtp.Desktop.Services;

internal sealed record WindowLayout(double Left, double Top, double Width, double Height, bool Maximized,
    double LeftPaneWidth, bool QueueVisible, double QueueHeight, bool LogVisible, double LogHeight);

internal sealed class WindowLayoutStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxFTP", "window-layout.json");
    private readonly string _oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ioFTP", "window-layout.json");
    public WindowLayout? Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(_path));
            if (!File.Exists(_oldPath)) return null;
            var migrated = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(_oldPath)); if (migrated is not null) Save(migrated); return migrated;
        }
        catch { return null; }
    }
    public void Save(WindowLayout layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }
}
