using System.Text.Json.Serialization;

namespace IoFtp.Core.Models;

public enum TransferProtocol
{
    Ftp,
    FtpsExplicit,
    FtpsImplicit,
    Sftp
}

public enum DirectoryListingMode
{
    StatThenList,
    StatOnly,
    ListOnly
}

public enum ProxyType { None, Socks4, Socks5, HttpConnect }

public sealed record ProxyConfiguration(ProxyType Type = ProxyType.None, string Host = "", int Port = 0,
    string Username = "", string Password = "", bool ProxyDns = true, bool UseForData = true);

public sealed record SiteOptions(
    int MaxSlots = 2,
    int MaxUploadSlots = 2,
    int MaxDownloadSlots = 2,
    int Priority = 0,
    bool AllowUpload = true,
    bool AllowDownload = true,
    bool StayLoggedIn = false,
    string BasePath = "/",
    bool PreferTlsTransfers = true,
    bool ForceBinaryMode = true,
    int MaxIdleSeconds = 60,
    string BlockTransfersFrom = "",
    string BlockTransfersTo = "",
    bool SecureFileListings = true);

public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Username,
    TransferProtocol Protocol,
    [property: JsonIgnore] string Password = "",
    bool AllowInvalidCertificate = false,
    DirectoryListingMode ListingMode = DirectoryListingMode.StatThenList,
    SiteOptions? Options = null,
    ProxyConfiguration? Proxy = null)
{
    [JsonIgnore] public SiteOptions EffectiveOptions => Options ?? new SiteOptions();
}
