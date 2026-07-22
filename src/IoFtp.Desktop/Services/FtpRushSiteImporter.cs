using System.Text;
using System.Text.Json;
using System.IO;
using System.Xml.Linq;
using IoFtp.Core.Models;

namespace IoFtp.Desktop.Services;

internal sealed record FtpRushImportedSite(ConnectionProfile Profile, string GroupPath);

internal static class FtpRushSiteImporter
{
    public static IReadOnlyList<FtpRushImportedSite> Import(string path)
    {
        if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) return ImportLegacyXml(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("RootItem", out var root))
            throw new InvalidDataException("The file is not a supported FTPRush site.json file.");
        var result = new List<FtpRushImportedSite>();
        Walk(root, "", result);
        return result;
    }

    private static IReadOnlyList<FtpRushImportedSite> ImportLegacyXml(string path)
    {
        var document = XDocument.Load(path, LoadOptions.None);
        var result = new List<FtpRushImportedSite>();
        if (document.Root is null) throw new InvalidDataException("The FTPRush XML file has no root element.");
        WalkLegacy(document.Root, "", result);
        return result;
    }

    private static void WalkLegacy(XElement node, string parentPath, List<FtpRushImportedSite> result)
    {
        var name = node.Attribute("NAME")?.Value?.Trim();
        var host = LegacyValue(node, "HOST");
        var isSite = !string.IsNullOrWhiteSpace(host) || node.Attribute("UID") is not null;
        if (isSite && !string.IsNullOrWhiteSpace(host))
        {
            var port = int.TryParse(LegacyValue(node, "PORT", "FTPPORT"), out var parsedPort) ? parsedPort : 21;
            var protocol = port == 22 ? TransferProtocol.Sftp : port == 990 ? TransferProtocol.FtpsImplicit : TransferProtocol.Ftp;
            var remotePath = LegacyValue(node, "REMOTEPATH", "REMOTE_PATH", "PATH", "DEFAULTREMOTEPATH").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(remotePath)) remotePath = "/";
            if (!remotePath.StartsWith('/')) remotePath = "/" + remotePath;
            var profile = new ConnectionProfile(Guid.NewGuid(), string.IsNullOrWhiteSpace(name) ? host : name, host, port,
                LegacyValue(node, "USERNAME", "USER"), protocol, "", false, DirectoryListingMode.StatThenList,
                new SiteOptions(BasePath: remotePath));
            result.Add(new(profile, parentPath));
        }
        var nextPath = !isSite && !string.IsNullOrWhiteSpace(name)
            ? string.IsNullOrWhiteSpace(parentPath) ? name : $"{parentPath} / {name}"
            : parentPath;
        foreach (var child in node.Elements().Where(child => !IsLegacyValueElement(child.Name.LocalName)))
            WalkLegacy(child, nextPath, result);
    }

    private static string LegacyValue(XElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var element = node.Elements().FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (element is not null) return element.Value.Trim();
            var attribute = node.Attributes().FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null) return attribute.Value.Trim();
        }
        return "";
    }

    private static bool IsLegacyValueElement(string name) => name.Equals("HOST", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PORT", StringComparison.OrdinalIgnoreCase) || name.Equals("FTPPORT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("USERNAME", StringComparison.OrdinalIgnoreCase) || name.Equals("USER", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PASSWORD", StringComparison.OrdinalIgnoreCase) || name.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("REMOTEPATH", StringComparison.OrdinalIgnoreCase) || name.Equals("REMOTE_PATH", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PATH", StringComparison.OrdinalIgnoreCase) || name.Equals("DEFAULTREMOTEPATH", StringComparison.OrdinalIgnoreCase);

    private static void Walk(JsonElement node, string parentPath, List<FtpRushImportedSite> result)
    {
        var nodeName = Text(node, "Name");
        var groupPath = string.IsNullOrWhiteSpace(nodeName) ? parentPath : string.IsNullOrWhiteSpace(parentPath) ? nodeName : $"{parentPath} / {nodeName}";
        if (node.TryGetProperty("Server", out var server) && server.ValueKind == JsonValueKind.Object)
        {
            var protocolNumber = Number(server, "Protocol", 1);
            var protocol = protocolNumber switch
            {
                1 => Number(server, "FTPEnryptMode", 0) switch
                {
                    1 => TransferProtocol.FtpsExplicit,
                    2 => TransferProtocol.FtpsImplicit,
                    _ => TransferProtocol.Ftp
                },
                2 => TransferProtocol.Sftp,
                _ => (TransferProtocol?)null
            };
            var host = Text(server, "Host");
            if (protocol is not null && !string.IsNullOrWhiteSpace(host))
            {
                var name = Text(server, "Name");
                if (string.IsNullOrWhiteSpace(name)) name = nodeName;
                if (string.IsNullOrWhiteSpace(name)) name = host;
                var port = Number(server, "Port", protocol == TransferProtocol.Sftp ? 22 : protocol == TransferProtocol.FtpsImplicit ? 990 : 21);
                var remotePath = Text(server, "DefaultRemotePath").Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(remotePath)) remotePath = "/";
                if (!remotePath.StartsWith('/')) remotePath = "/" + remotePath;
                var password = DecodeBase64(Text(server, "Base64Password"));
                var profile = new ConnectionProfile(Guid.NewGuid(), name, host, port, Text(server, "Username"), protocol.Value,
                    password, false, DirectoryListingMode.StatThenList, new SiteOptions(BasePath: remotePath));
                result.Add(new(profile, parentPath));
            }
        }
        if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Walk(child, groupPath, result);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Number(JsonElement element, string name, int fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static string DecodeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return ""; }
    }
}
