using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace IoFtp.Desktop.Services;

internal sealed record UpdateCheckResult(string CurrentVersion, string LatestVersion, string ReleaseUrl, bool UpdateAvailable, bool FromCache, string? Error = null);

internal sealed class UpdateCheckService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Khazaztroph/FluxFTP/releases/latest";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private readonly string _cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxFTP", "update-check.json");

    public static string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var value = informational?.Split('+')[0] ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
            return value.TrimStart('v', 'V');
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;
        var cached = LoadCache();
        if (!force && cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < CacheLifetime)
            return MakeResult(current, cached.LatestVersion, cached.ReleaseUrl, true);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"FluxFTP/{current}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.GetAsync(LatestReleaseUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var latest = json.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? current;
            var url = json.RootElement.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() ?? "" : "";
            SaveCache(new(DateTimeOffset.UtcNow, latest, url));
            return MakeResult(current, latest, url, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (cached is not null) return MakeResult(current, cached.LatestVersion, cached.ReleaseUrl, true, exception.Message);
            return new(current, "", "", false, false, exception.Message);
        }
    }

    private static UpdateCheckResult MakeResult(string current, string latest, string url, bool fromCache, string? error = null) =>
        new(current, latest, url, Compare(latest, current) > 0, fromCache, error);

    private static int Compare(string left, string right)
    {
        static Version Parse(string value)
        {
            var core = value.TrimStart('v', 'V').Split('-', '+')[0];
            return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0);
        }
        return Parse(left).CompareTo(Parse(right));
    }

    private UpdateCache? LoadCache()
    {
        try { return File.Exists(_cachePath) ? JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(_cachePath)) : null; }
        catch { return null; }
    }

    private void SaveCache(UpdateCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache));
        }
        catch { }
    }

    private sealed record UpdateCache(DateTimeOffset CheckedAt, string LatestVersion, string ReleaseUrl);
}
