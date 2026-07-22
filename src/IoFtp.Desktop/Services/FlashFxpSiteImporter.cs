using System.IO;
using System.Xml.Linq;
using IoFtp.Core.Models;

namespace IoFtp.Desktop.Services;

internal static class FlashFxpSiteImporter
{
    public static IReadOnlyList<FtpRushImportedSite> Import(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        if (document.Root?.Name.LocalName.Equals("SITES", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("The file is not a supported FlashFXP Sites.ftp export.");
        var result = new List<FtpRushImportedSite>();
        foreach (var site in document.Root.Elements())
        {
            var host = Value(site, "ADDRESS");
            if (string.IsNullOrWhiteSpace(host)) continue;
            var protocolText = Value(site, "PROTOCOL");
            var ssl = Value(site, "SSL");
            var protocol = protocolText.Equals("SFTP", StringComparison.OrdinalIgnoreCase)
                ? TransferProtocol.Sftp
                : ssl.Contains("IMPLICIT", StringComparison.OrdinalIgnoreCase)
                    ? TransferProtocol.FtpsImplicit
                    : ssl.Contains("AUTH TLS", StringComparison.OrdinalIgnoreCase) || ssl.Contains("EXPLICIT", StringComparison.OrdinalIgnoreCase)
                        ? TransferProtocol.FtpsExplicit
                        : TransferProtocol.Ftp;
            var port = int.TryParse(Value(site, "PORT"), out var parsedPort) ? parsedPort
                : protocol == TransferProtocol.Sftp ? 22 : protocol == TransferProtocol.FtpsImplicit ? 990 : 21;
            var remotePath = Value(site, "REMOTEPATH").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(remotePath)) remotePath = "/";
            if (!remotePath.StartsWith('/')) remotePath = "/" + remotePath;
            var name = site.Attribute("NAME")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = host;
            var profile = new ConnectionProfile(Guid.NewGuid(), name, host, port, Value(site, "USERNAME"), protocol,
                Value(site, "PASSWORD"), false, DirectoryListingMode.StatThenList, new SiteOptions(BasePath: remotePath));
            result.Add(new(profile, Value(site, "GROUP")));
        }
        return result;
    }

    private static string Value(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
}
