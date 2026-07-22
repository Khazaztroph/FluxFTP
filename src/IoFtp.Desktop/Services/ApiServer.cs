using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IoFtp.Core.Models;
using IoFtp.Core.Transport;
using IoFtp.Desktop.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace IoFtp.Desktop.Services;

internal sealed class ApiServer : IAsyncDisposable
{
    private WebApplication? _app;

    public async Task StartAsync(GlobalSettings settings, Func<IReadOnlyList<TransferJobInfo>> getJobs,
        Func<ApiTransferRequest, Task<object>> startTransfer, Func<ApiDownloadRequest, Task<object>> startDownload, Action<Guid> removeJob, Action<Guid> resetJob)
    {
        if (!settings.EnableHttpsApi) return;
        var certificate = LoadOrCreateCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(
            settings.ApiLocalhostOnly ? IPAddress.Loopback : IPAddress.Any, settings.HttpsApiPort,
            listen => listen.UseHttps(certificate)));
        _app = builder.Build();

        _app.Use(async (context, next) =>
        {
            if (!IsAuthorized(context, settings.ApiPassword))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"FluxFTP\"";
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
            await next();
        });

        _app.MapGet("/info", () => Results.Json(new { name = "FluxFTP", version = "1.0.0", api = "cbftp-compatible", tls = true }));
        _app.MapGet("/sites", () => Results.Json(new ProfileStore().Load().Select(ToApiSite)));
        _app.MapGet("/sites/{name}", (string name) => FindSite(name) is { } site ? Results.Json(ToApiSite(site)) : Results.NotFound(new { error = "Site not found" }));
        _app.MapPost("/sites", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SiteRequest>();
            if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Addresses is not { Count: > 0 }) return Results.BadRequest(new { error = "name and addresses are required" });
            var profiles = new ProfileStore().Load().ToList();
            if (profiles.Any(item => item.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new { error = "Site already exists" });
            var profile = CreateProfile(request);
            profiles.Add(profile); new ProfileStore().Save(profiles);
            return Results.Created($"/sites/{Uri.EscapeDataString(profile.Name)}", ToApiSite(profile));
        });
        _app.MapPatch("/sites/{name}", async (string name, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SiteRequest>();
            var profiles = new ProfileStore().Load().ToList();
            var index = profiles.FindIndex(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return Results.NotFound(new { error = "Site not found" });
            profiles[index] = PatchProfile(profiles[index], request ?? new());
            new ProfileStore().Save(profiles); return Results.Json(ToApiSite(profiles[index]));
        });
        _app.MapDelete("/sites/{name}", (string name) =>
        {
            var profiles = new ProfileStore().Load().ToList();
            var removed = profiles.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Results.NotFound(new { error = "Site not found" });
            new ProfileStore().Save(profiles); return Results.Ok(new { deleted = name });
        });
        _app.MapGet("/sections", () => Results.Json(new SectionStore().Load().Select(ToApiSection)));
        _app.MapGet("/sections/{name}", (string name) =>
        {
            var section = FindSection(name); return section is null ? Results.NotFound(new { error = "Section not found" }) : Results.Json(ToApiSection(section));
        });
        _app.MapPost("/sections", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SectionRequest>();
            if (request is null || string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "name is required" });
            var sections = new SectionStore().Load(); if (sections.Any(item => item.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))) return Results.Conflict(new { error = "Section already exists" });
            var section = new SectionDefinition(request.Name, new(StringComparer.OrdinalIgnoreCase), request.Hotkey ?? 0); sections.Add(section); new SectionStore().Save(sections);
            return Results.Created($"/sections/{Uri.EscapeDataString(section.Name)}", ToApiSection(section));
        });
        _app.MapPatch("/sections/{name}", async (string name, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SectionRequest>() ?? new(); var sections = new SectionStore().Load();
            var index = sections.FindIndex(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); if (index < 0) return Results.NotFound(new { error = "Section not found" });
            sections[index] = sections[index] with { Name = request.Name ?? sections[index].Name, Hotkey = request.Hotkey ?? sections[index].Hotkey };
            new SectionStore().Save(sections); return Results.Json(ToApiSection(sections[index]));
        });
        _app.MapDelete("/sections/{name}", (string name) =>
        {
            var sections = new SectionStore().Load(); var removed = sections.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Results.NotFound(new { error = "Section not found" }); new SectionStore().Save(sections); return Results.Ok(new { deleted = name });
        });
        _app.MapGet("/sites/{site}/sections", (string site) => Results.Json(new SectionStore().Load()
            .Select(section => new { section, path = SitePath(section, site) }).Where(item => item.path is not null)
            .Select(item => new { name = item.section.Name, path = item.path })));
        _app.MapGet("/sites/{site}/sections/{name}", (string site, string name) =>
        {
            var section = FindSection(name); var path = section is null ? null : SitePath(section, site);
            return path is null ? Results.NotFound(new { error = "Site section not found" }) : Results.Json(new { name = section!.Name, path });
        });
        _app.MapPost("/sites/{site}/sections", async (string site, HttpContext context) =>
        {
            if (FindSite(site) is null) return Results.NotFound(new { error = "Site not found" });
            var request = await context.Request.ReadFromJsonAsync<SectionRequest>(); if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Path)) return Results.BadRequest(new { error = "name and path are required" });
            var sections = new SectionStore().Load(); var index = sections.FindIndex(item => item.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { sections.Add(new(request.Name, new(StringComparer.OrdinalIgnoreCase) { [site] = request.Path })); index = sections.Count - 1; }
            else SetSitePath(sections[index], site, request.Path);
            new SectionStore().Save(sections); return Results.Created($"/sites/{Uri.EscapeDataString(site)}/sections/{Uri.EscapeDataString(request.Name)}", new { name = sections[index].Name, path = request.Path });
        });
        _app.MapPatch("/sites/{site}/sections/{name}", async (string site, string name, HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SectionRequest>() ?? new(); var sections = new SectionStore().Load();
            var index = sections.FindIndex(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); if (index < 0 || SitePath(sections[index], site) is null) return Results.NotFound(new { error = "Site section not found" });
            if (!string.IsNullOrWhiteSpace(request.Path)) SetSitePath(sections[index], site, request.Path); new SectionStore().Save(sections);
            return Results.Json(new { name = sections[index].Name, path = SitePath(sections[index], site) });
        });
        _app.MapDelete("/sites/{site}/sections/{name}", (string site, string name) =>
        {
            var sections = new SectionStore().Load(); var index = sections.FindIndex(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || !RemoveSitePath(sections[index], site)) return Results.NotFound(new { error = "Site section not found" });
            new SectionStore().Save(sections); return Results.Ok(new { deleted = name, site });
        });
        _app.MapGet("/path", async (string site, string path, int? timeout) =>
        {
            var profile = FindSite(site); if (profile is null) return Results.NotFound(new { error = "Site not found" });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeout ?? 60, 1, 300)));
            await using var session = new FtpRemoteSession(); await session.ConnectAsync(profile, cancellation.Token);
            path = ResolvePath(site, path); var entries = await session.ListAsync(path, cancellation.Token);
            return Results.Json(entries.Select(entry => new { name = entry.Name, path = entry.FullPath, dir = entry.IsDirectory, size = entry.Size, modified = entry.ModifiedAt, attributes = entry.Attributes }));
        });
        _app.MapGet("/file", async (string site, string path, int? timeout) =>
        {
            var profile = FindSite(site); if (profile is null) return Results.NotFound(new { error = "Site not found" });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeout ?? 60, 1, 300)));
            await using var session = new FtpRemoteSession(); await session.ConnectAsync(profile, cancellation.Token); path = ResolvePath(site, path);
            await using var output = new MemoryStream(); await session.DownloadAsync(path, output, 0, null, cancellation.Token);
            if (output.Length > 500 * 1024) return Results.BadRequest(new { error = "File exceeds 500 KiB" });
            return Results.Bytes(output.ToArray(), "application/octet-stream", Path.GetFileName(path));
        });
        _app.MapPost("/raw", async (RawRequest request) =>
        {
            var names = request.SitesAll ? new ProfileStore().Load().Select(site => site.Name).ToArray() : request.Sites ?? [];
            var results = new List<object>();
            foreach (var name in names)
            {
                var profile = FindSite(name); if (profile is null) { results.Add(new { site = name, error = "Site not found" }); continue; }
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(request.Timeout ?? 10, 1, 300)));
                await using var session = new FtpRemoteSession();
                try
                {
                    await session.ConnectAsync(profile, cancellation.Token);
                    var rawPath = request.PathSection is not null ? ResolvePath(name, request.PathSection) : request.Path;
                    if (!string.IsNullOrWhiteSpace(rawPath)) await session.ExecuteCommandAsync($"CWD {rawPath}", cancellation.Token);
                    var response = await session.ExecuteCommandAsync(request.Command ?? "", cancellation.Token);
                    results.Add(new { site = name, code = response.StatusCode, message = response.Message });
                }
                catch (Exception exception) { results.Add(new { site = name, error = exception.Message }); }
            }
            return Results.Json(new { results });
        });
        _app.MapGet("/transferjobs", () => Results.Json(getJobs()));
        _app.MapPost("/transferjobs", async (ApiTransferRequest request) => Results.Json(await startTransfer(request)));
        _app.MapPost("/downloads", async (ApiDownloadRequest request) => Results.Json(await startDownload(request)));
        _app.MapPost("/transferjobs/{id:guid}/abort", (Guid id) => { removeJob(id); return Results.Ok(new { aborted = id }); });
        _app.MapPost("/transferjobs/{id:guid}/reset", (Guid id) => { resetJob(id); return Results.Ok(new { reset = id }); });

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync(); await _app.DisposeAsync(); _app = null;
    }

    private static bool IsAuthorized(HttpContext context, string password)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            var supplied = decoded[(decoded.IndexOf(':') + 1)..];
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(password));
        }
        catch { return false; }
    }

    private static ConnectionProfile? FindSite(string name) => new ProfileStore().Load().FirstOrDefault(site => site.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static SectionDefinition? FindSection(string name) => new SectionStore().Load().FirstOrDefault(section => section.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static string? SitePath(SectionDefinition section, string site) => section.SitePaths.FirstOrDefault(pair => pair.Key.Equals(site, StringComparison.OrdinalIgnoreCase)).Value;
    private static void SetSitePath(SectionDefinition section, string site, string path) { RemoveSitePath(section, site); section.SitePaths[site] = path; }
    private static bool RemoveSitePath(SectionDefinition section, string site) { var key = section.SitePaths.Keys.FirstOrDefault(key => key.Equals(site, StringComparison.OrdinalIgnoreCase)); return key is not null && section.SitePaths.Remove(key); }
    private static string ResolvePath(string site, string path) => path.StartsWith('/') ? path : FindSection(path) is { } section && SitePath(section, site) is { } sectionPath ? sectionPath : path;
    private static object ToApiSection(SectionDefinition section) => new { name = section.Name, hotkey = section.Hotkey, num_jobs = 0,
        sites = section.SitePaths.Select(pair => new { site = pair.Key, path = pair.Value }) };
    private static object ToApiSite(ConnectionProfile profile) => new { name = profile.Name, addresses = profile.EffectiveAddresses.Select(address => address.ToString()).ToArray(), user = profile.Username,
        base_path = profile.EffectiveOptions.BasePath, tls_mode = profile.Protocol == TransferProtocol.FtpsImplicit ? "IMPLICIT" : profile.Protocol == TransferProtocol.FtpsExplicit ? "AUTH_TLS" : "NONE",
        list_command = profile.ListingMode switch { DirectoryListingMode.Auto => "AUTO", DirectoryListingMode.ListOnly => "LIST", _ => "STAT_L" }, max_logins = profile.EffectiveOptions.MaxSlots,
        max_sim_up = profile.EffectiveOptions.MaxUploadSlots, max_sim_down = profile.EffectiveOptions.MaxDownloadSlots,
        priority = profile.EffectiveOptions.Priority, needs_pret = profile.EffectiveOptions.NeedsPret,
        cepr_supported = profile.EffectiveOptions.CeprSupported, use_xdupe = profile.EffectiveOptions.UseXdupe };
    private static ConnectionProfile CreateProfile(SiteRequest request)
    {
        var protocol = ParseTls(request.TlsMode); var defaultPort = protocol == TransferProtocol.FtpsImplicit ? 990 : 21;
        if (!SiteEndpoint.TryParse(request.Addresses![0], defaultPort, out var primary)) throw new ArgumentException("Invalid primary site address.");
        var alternates = request.Addresses.Skip(1).Select(value => SiteEndpoint.TryParse(value, defaultPort, out var endpoint) ? endpoint : throw new ArgumentException($"Invalid site address: {value}"));
        var options = new SiteOptions(request.MaxLogins ?? 3, request.MaxSimUp ?? 3, request.MaxSimDown ?? 2,
            request.Priority ?? 0, BasePath: request.BasePath ?? "/", NeedsPret: request.NeedsPret ?? false, CeprSupported: request.CeprSupported ?? false,
            UseXdupe: request.UseXdupe ?? false);
        return new(Guid.NewGuid(), request.Name!, primary.Host, primary.Port, request.User ?? "anonymous", protocol,
            request.Password ?? "", ListingMode: ParseListingMode(request.ListCommand),
            Options: options, AlternateAddresses: string.Join(' ', alternates));
    }
    private static ConnectionProfile PatchProfile(ConnectionProfile profile, SiteRequest request)
    {
        var host = profile.Host; var port = profile.Port; var alternateAddresses = profile.AlternateAddresses;
        if (request.Addresses is { Count: > 0 })
        {
            if (!SiteEndpoint.TryParse(request.Addresses[0], profile.Port, out var primary)) throw new ArgumentException("Invalid primary site address.");
            host = primary.Host; port = primary.Port;
            alternateAddresses = string.Join(' ', request.Addresses.Skip(1).Select(value => SiteEndpoint.TryParse(value, port, out var endpoint) ? endpoint : throw new ArgumentException($"Invalid site address: {value}")));
        }
        var options = profile.EffectiveOptions with { BasePath = request.BasePath ?? profile.EffectiveOptions.BasePath, MaxSlots = request.MaxLogins ?? profile.EffectiveOptions.MaxSlots,
            MaxUploadSlots = request.MaxSimUp ?? profile.EffectiveOptions.MaxUploadSlots, MaxDownloadSlots = request.MaxSimDown ?? profile.EffectiveOptions.MaxDownloadSlots,
            Priority = request.Priority ?? profile.EffectiveOptions.Priority, NeedsPret = request.NeedsPret ?? profile.EffectiveOptions.NeedsPret,
            CeprSupported = request.CeprSupported ?? profile.EffectiveOptions.CeprSupported,
            UseXdupe = request.UseXdupe ?? profile.EffectiveOptions.UseXdupe };
        return profile with { Name = request.Name ?? profile.Name, Host = host, Port = port, AlternateAddresses = alternateAddresses, Username = request.User ?? profile.Username,
            Password = request.Password ?? profile.Password, Protocol = request.TlsMode is null ? profile.Protocol : ParseTls(request.TlsMode),
            ListingMode = request.ListCommand is null ? profile.ListingMode : ParseListingMode(request.ListCommand), Options = options };
    }
    private static void ParseAddress(string address, out string host, out int? port)
    { var separator = address.LastIndexOf(':'); if (separator > 0 && int.TryParse(address[(separator + 1)..], out var parsed)) { host = address[..separator]; port = parsed; } else { host = address; port = null; } }
    private static TransferProtocol ParseTls(string? value) => value?.ToUpperInvariant() switch { "IMPLICIT" => TransferProtocol.FtpsImplicit, "NONE" => TransferProtocol.Ftp, _ => TransferProtocol.FtpsExplicit };
    private static DirectoryListingMode ParseListingMode(string? value) => value?.ToUpperInvariant() switch
    {
        "LIST" => DirectoryListingMode.ListOnly,
        "STAT_L" or "STAT-L" => DirectoryListingMode.StatThenList,
        _ => DirectoryListingMode.Auto
    };

    private static X509Certificate2 LoadOrCreateCertificate()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxFTP"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "api-certificate.pfx");
        var oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ioFTP", "api-certificate.pfx");
        if (!File.Exists(path) && File.Exists(oldPath)) File.Copy(oldPath, path);
        if (File.Exists(path)) return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);
        using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5)); File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);
    }

    private sealed record SiteRequest(string? Name = null, List<string>? Addresses = null, string? User = null, string? Password = null,
        [property: JsonPropertyName("base_path")] string? BasePath = null,
        [property: JsonPropertyName("tls_mode")] string? TlsMode = null,
        [property: JsonPropertyName("list_command")] string? ListCommand = null,
        [property: JsonPropertyName("max_logins")] int? MaxLogins = null,
        [property: JsonPropertyName("max_sim_up")] int? MaxSimUp = null,
        [property: JsonPropertyName("max_sim_down")] int? MaxSimDown = null, int? Priority = null,
        [property: JsonPropertyName("needs_pret")] bool? NeedsPret = null,
        [property: JsonPropertyName("cepr_supported")] bool? CeprSupported = null,
        [property: JsonPropertyName("use_xdupe")] bool? UseXdupe = null);
    private sealed record RawRequest(string? Command = null, string[]? Sites = null,
        [property: JsonPropertyName("sites_all")] bool SitesAll = false, string? Path = null,
        [property: JsonPropertyName("path_section")] string? PathSection = null, int? Timeout = null);
    private sealed record SectionRequest(string? Name = null, string? Path = null, int? Hotkey = null);
}

internal sealed record ApiTransferRequest(
    [property: JsonPropertyName("src_site")] string? SrcSite = null,
    [property: JsonPropertyName("src_path")] string? SrcPath = null,
    [property: JsonPropertyName("src_section")] string? SrcSection = null,
    [property: JsonPropertyName("dst_site")] string? DstSite = null,
    [property: JsonPropertyName("dst_path")] string? DstPath = null,
    [property: JsonPropertyName("dst_section")] string? DstSection = null,
    string? Name = null);

internal sealed record ApiDownloadRequest(
    string? Site = null,
    [property: JsonPropertyName("remote_path")] string? RemotePath = null,
    [property: JsonPropertyName("remote_section")] string? RemoteSection = null,
    [property: JsonPropertyName("local_path")] string? LocalPath = null,
    bool Recursive = true);
