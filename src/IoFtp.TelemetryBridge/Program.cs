using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IoFtp.Core.Models;
using IoFtp.Core.Transport;

var configPath = Path.Combine(AppContext.BaseDirectory, "FluxTelemetry.json");
var options = TelemetryOptions.LoadOrCreate(configPath);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(settings =>
{
    settings.SingleLine = true;
    settings.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(options.ListenUrl);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TelemetryState>();
builder.Services.AddHostedService<IoFtpPoller>();

var app = builder.Build();
app.MapGet("/info", () => Results.Json(new
{
    name = "FluxTelemetry",
    version = "0.1.0",
    source = "ioFTPD client who",
    poll_interval_ms = options.PollIntervalMs
}));
app.MapGet("/health", (TelemetryState state) => Results.Json(state.Health));
app.MapGet("/activity", (TelemetryState state) => Results.Json(state.Snapshot));
app.MapGet("/metrics", (TelemetryState state) => Results.Json(state.Metrics));
app.MapGet("/events", async (HttpContext context, TelemetryState state) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";
    long revision = -1;
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, options.PollIntervalMs / 2)));
    while (!context.RequestAborted.IsCancellationRequested && await timer.WaitForNextTickAsync(context.RequestAborted))
    {
        var snapshot = state.Snapshot;
        if (snapshot.Revision == revision) continue;
        revision = snapshot.Revision;
        await context.Response.WriteAsync($"event: activity\ndata: {JsonSerializer.Serialize(snapshot)}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

Console.WriteLine($"FluxTelemetry listening on {options.ListenUrl}");
Console.WriteLine($"ioFTPD source: {options.Host}:{options.Port}; command: {options.SiteCommand}");
await app.RunAsync();

internal sealed record TelemetryOptions(
    string Host = "127.0.0.1",
    int Port = 5420,
    string Username = "",
    string Password = "",
    string Protocol = "AUTH_TLS",
    bool AllowInvalidCertificate = true,
    int PollIntervalMs = 500,
    string ListenUrl = "http://127.0.0.1:55478",
    string SiteCommand = "SITE FLUXWHO",
    string SnapshotPath = "FluxTelemetry-activity.json",
    string LegacySitesIni = @"C:\ioFTPD\ioGUI\sites.ini")
{
    public static TelemetryOptions LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var stored = JsonSerializer.Deserialize<TelemetryOptions>(File.ReadAllText(path), new JsonSerializerOptions
                { PropertyNameCaseInsensitive = true }) ?? new TelemetryOptions();
            var password = Unprotect(stored.Password);
            var effective = stored with { Password = password };
            var changed = !string.IsNullOrEmpty(stored.Password) &&
                          !stored.Password.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase);
            var legacyPath = string.IsNullOrWhiteSpace(effective.LegacySitesIni)
                ? @"C:\ioFTPD\ioGUI\sites.ini"
                : effective.LegacySitesIni;
            if (!string.Equals(legacyPath, effective.LegacySitesIni, StringComparison.Ordinal))
            {
                effective = effective with { LegacySitesIni = legacyPath };
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(effective.Username) && TryLoadLegacySite(legacyPath, out var legacy))
            {
                effective = effective with
                {
                    Host = legacy.Host,
                    Port = legacy.Port,
                    Username = legacy.Username,
                    Password = legacy.Password
                };
                changed = true;
            }
            if (changed) Save(path, effective with { Password = Protect(effective.Password) });
            return effective;
        }
        var value = new TelemetryOptions();
        Save(path, value);
        return value;
    }

    private static void Save(string path, TelemetryOptions value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private static string Protect(string value) => string.IsNullOrEmpty(value) ? value
        : "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    private static string Unprotect(string value)
    {
        if (!value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)) return value;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    private static bool TryLoadLegacySite(string path, out (string Host, int Port, string Username, string Password) site)
    {
        site = default;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        var values = File.ReadLines(path)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        values.TryGetValue("user", out var username);
        values.TryGetValue("pass", out var password);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;
        values.TryGetValue("host", out var host);
        values.TryGetValue("port", out var portText);
        site = (string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host,
            int.TryParse(portText, out var port) ? port : 5420, username, password);
        return true;
    }

    [JsonIgnore]
    public ConnectionProfile Profile => new(Guid.NewGuid(), "ioFTPD", Host, Port, Username,
        Protocol.ToUpperInvariant() switch
        {
            "FTP" or "NONE" => TransferProtocol.Ftp,
            "IMPLICIT" or "IMPLICIT_TLS" => TransferProtocol.FtpsImplicit,
            _ => TransferProtocol.FtpsExplicit
        }, Password, AllowInvalidCertificate, DirectoryListingMode.ListOnly);
}

internal sealed class IoFtpPoller(TelemetryOptions options, TelemetryState state, ILogger<IoFtpPoller> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FtpRemoteSession? session = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try
            {
                if (string.IsNullOrWhiteSpace(options.Username))
                    throw new InvalidOperationException("Set Username and Password in FluxTelemetry.json.");
                session ??= new FtpRemoteSession();
                if (!session.IsConnected)
                {
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await session.ConnectAsync(options.Profile, connectTimeout.Token);
                }
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var response = await session.ExecuteCommandAsync(options.SiteCommand, pollTimeout.Token);
                if (response.StatusCode is < 200 or >= 300)
                    throw new IOException($"{options.SiteCommand} returned {response.StatusCode}: {response.Message}");
                var entries = FluxWhoParser.Parse(response.Message);
                await state.UpdateAsync(entries, options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                state.SetError(exception.Message);
                logger.LogWarning("Telemetry poll failed: {Message}", exception.Message);
                if (session is not null)
                {
                    await session.DisposeAsync();
                    session = null;
                }
            }

            var remaining = TimeSpan.FromMilliseconds(Math.Max(100, options.PollIntervalMs)) - (DateTimeOffset.UtcNow - started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, stoppingToken);
        }
        if (session is not null) await session.DisposeAsync();
    }
}

internal sealed class TelemetryState
{
    private readonly object _gate = new();
    private ActivitySnapshot _snapshot = new(0, DateTimeOffset.MinValue, []);
    private string? _lastError;
    public ActivitySnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public object Health { get { lock (_gate) return new { ok = _lastError is null, updated_at = _snapshot.UpdatedAt, sessions = _snapshot.Sessions.Count, error = _lastError }; } }
    public object Metrics
    {
        get
        {
            lock (_gate)
            {
                var uploads = _snapshot.Sessions.Where(item => item.Direction == "upload").ToList();
                var downloads = _snapshot.Sessions.Where(item => item.Direction == "download").ToList();
                return new { updated_at = _snapshot.UpdatedAt, online = _snapshot.Sessions.Count,
                    uploads = uploads.Count, downloads = downloads.Count,
                    upload_bytes_per_second = uploads.Sum(item => item.SpeedBytesPerSecond),
                    download_bytes_per_second = downloads.Sum(item => item.SpeedBytesPerSecond),
                    total_bytes_per_second = _snapshot.Sessions.Sum(item => item.SpeedBytesPerSecond) };
            }
        }
    }
    public async Task UpdateAsync(IReadOnlyList<ActivitySession> sessions, TelemetryOptions options, CancellationToken token)
    {
        ActivitySnapshot snapshot;
        lock (_gate)
        {
            snapshot = new ActivitySnapshot(_snapshot.Revision + 1, DateTimeOffset.UtcNow, sessions);
            _snapshot = snapshot; _lastError = null;
        }
        var path = Path.IsPathRooted(options.SnapshotPath) ? options.SnapshotPath : Path.Combine(AppContext.BaseDirectory, options.SnapshotPath);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot), token);
        File.Move(temporary, path, true);
    }
    public void SetError(string error) { lock (_gate) _lastError = error; }
}

internal static class FluxWhoParser
{
    public static IReadOnlyList<ActivitySession> Parse(string response) => response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Length > 4 && int.TryParse(line[..3], out _) ? line[4..] : line)
        .Where(line => line.StartsWith("fluxwho|", StringComparison.OrdinalIgnoreCase))
        .Select(line => line.Split('|'))
        .Where(parts => parts.Length >= 12)
        .Select(parts => new ActivitySession(parts[1], parts[2], Direction(parts[3]), Speed(parts[4]), Number(parts[5]),
            parts[6], parts[7], parts[8], parts[9], parts[10], parts[11]))
        .Where(session => !IsTelemetryPoll(session))
        .ToList();
    private static bool IsTelemetryPoll(ActivitySession session) =>
        session.Action.Trim().Equals("SITE FLUXWHO", StringComparison.OrdinalIgnoreCase);
    private static long Number(string value) => long.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static long Speed(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        var number = new string(normalized.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return 0;
        var unit = normalized[number.Length..].Trim().ToLowerInvariant();
        var multiplier = unit.StartsWith("g") ? 1024d * 1024 * 1024
            : unit.StartsWith("m") ? 1024d * 1024
            : unit.StartsWith("b") ? 1d
            : 1024d; // ioFTPD TRANSFERSPEED without a suffix is KiB/s.
        return Math.Max(0, (long)(amount * multiplier));
    }
    private static string Direction(string status) => status switch { "1" => "download", "2" => "upload", _ => "idle" };
}

internal sealed record ActivitySnapshot(long Revision, DateTimeOffset UpdatedAt, IReadOnlyList<ActivitySession> Sessions);
internal sealed record ActivitySession(string Cid, string User, string Direction, long SpeedBytesPerSecond,
    long TransferredBytes, string Action, string VirtualPath, string DataPath, string Status, string Ip, string DataIp);
