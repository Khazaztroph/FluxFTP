using System.IO;
using System.Text.Json;

namespace IoFtp.Desktop.Services;

internal sealed record SiteBookmark(string Name, string Path, string SiteName = "");

internal sealed class BookmarkStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "FluxFTP-bookmarks.json");

    public List<SiteBookmark> Load()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<SiteBookmark>>(File.ReadAllText(_path)) ?? []; }
        catch { return []; }
    }

    public void Save(IEnumerable<SiteBookmark> bookmarks)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(bookmarks, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }
}
