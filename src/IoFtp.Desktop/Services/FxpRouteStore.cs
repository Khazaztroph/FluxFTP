using System.IO;
using System.Text.Json;

namespace IoFtp.Desktop.Services;

internal sealed record LearnedFxpRoute(Guid SourceSiteId, Guid DestinationSiteId, bool Reverse);

internal sealed class FxpRouteStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxFTP", "fxp-routes.json");

    public HashSet<(Guid Source, Guid Destination)> LoadReverseRoutes()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return (JsonSerializer.Deserialize<List<LearnedFxpRoute>>(File.ReadAllText(_path)) ?? [])
                .Where(route => route.Reverse)
                .Select(route => (route.SourceSiteId, route.DestinationSiteId))
                .ToHashSet();
        }
        catch { return []; }
    }

    public void SaveReverseRoutes(IEnumerable<(Guid Source, Guid Destination)> routes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var values = routes.OrderBy(route => route.Source).ThenBy(route => route.Destination)
            .Select(route => new LearnedFxpRoute(route.Source, route.Destination, true)).ToList();
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }
}
