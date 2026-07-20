using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using IoFtp.Core.Models;

namespace IoFtp.Desktop.Services;

internal sealed class ProfileStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "FluxFTP-sites.ini");
    private readonly string _oldIniPath = Path.Combine(AppContext.BaseDirectory, "ioFTP-sites.ini");
    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ioFTP", "sites.json");

    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (File.Exists(_path)) return LoadIni(_path);
        if (File.Exists(_oldIniPath))
        {
            var profiles = LoadIni(_oldIniPath); Save(profiles); return profiles;
        }
        if (!File.Exists(_legacyPath)) return [];

        try
        {
            var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(_legacyPath)) ?? [];
            Save(profiles);
            return profiles;
        }
        catch (JsonException) { return []; }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        var text = new StringBuilder();
        text.AppendLine("; FluxFTP saved sites");
        text.AppendLine("; Passwords are protected for the current Windows user.");

        foreach (var profile in profiles)
        {
            var options = profile.EffectiveOptions;
            text.AppendLine().AppendLine($"[site:{profile.Id}]");
            Write(text, "Name", profile.Name);
            Write(text, "Host", profile.Host);
            Write(text, "Port", profile.Port);
            Write(text, "Username", profile.Username);
            Write(text, "Password", Protect(profile.Password));
            Write(text, "Protocol", profile.Protocol);
            Write(text, "AllowInvalidCertificate", profile.AllowInvalidCertificate);
            Write(text, "ListingMode", profile.ListingMode);
            Write(text, "MaxSlots", options.MaxSlots);
            Write(text, "MaxUploadSlots", options.MaxUploadSlots);
            Write(text, "MaxDownloadSlots", options.MaxDownloadSlots);
            Write(text, "Priority", options.Priority);
            Write(text, "AllowUpload", options.AllowUpload);
            Write(text, "AllowDownload", options.AllowDownload);
            Write(text, "StayLoggedIn", options.StayLoggedIn);
            Write(text, "BasePath", options.BasePath);
            Write(text, "PreferTlsTransfers", options.PreferTlsTransfers);
            Write(text, "ForceBinaryMode", options.ForceBinaryMode);
            Write(text, "MaxIdleSeconds", options.MaxIdleSeconds);
            Write(text, "BlockTransfersFrom", options.BlockTransfersFrom);
            Write(text, "BlockTransfersTo", options.BlockTransfersTo);
            Write(text, "SecureFileListings", options.SecureFileListings);
        }

        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, text.ToString(), new UTF8Encoding(false));
        File.Move(temporaryPath, _path, true);
    }

    private static IReadOnlyList<ConnectionProfile> LoadIni(string path)
    {
        var result = new List<ConnectionProfile>();
        Dictionary<string, string>? values = null;
        Guid id = Guid.Empty;

        void AddCurrent()
        {
            if (values is null || id == Guid.Empty) return;
            var options = new SiteOptions(
                Int(values, "MaxSlots", 2), Int(values, "MaxUploadSlots", 2), Int(values, "MaxDownloadSlots", 2),
                Int(values, "Priority"), Bool(values, "AllowUpload", true), Bool(values, "AllowDownload", true),
                Bool(values, "StayLoggedIn"), Get(values, "BasePath", "/"), Bool(values, "PreferTlsTransfers", true),
                Bool(values, "ForceBinaryMode", true), Int(values, "MaxIdleSeconds", 60),
                Get(values, "BlockTransfersFrom"), Get(values, "BlockTransfersTo"), Bool(values, "SecureFileListings", true));
            result.Add(new ConnectionProfile(id, Get(values, "Name", "Site"), Get(values, "Host"), Int(values, "Port", 21),
                Get(values, "Username"), EnumValue(values, "Protocol", TransferProtocol.Ftp), Unprotect(Get(values, "Password")),
                Bool(values, "AllowInvalidCertificate"), EnumValue(values, "ListingMode", DirectoryListingMode.StatThenList), options));
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            if (line.StartsWith("[site:", StringComparison.OrdinalIgnoreCase) && line.EndsWith(']'))
            {
                AddCurrent();
                Guid.TryParse(line[6..^1], out id);
                values = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var separator = line.IndexOf('=');
            if (values is not null && separator > 0) values[line[..separator].Trim()] = Decode(line[(separator + 1)..].Trim());
        }
        AddCurrent();
        return result;
    }

    private static void Write(StringBuilder target, string key, object? value) =>
        target.Append(key).Append('=').AppendLine(Encode(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
    private static string Encode(string value) => Uri.EscapeDataString(value);
    private static string Decode(string value) { try { return Uri.UnescapeDataString(value); } catch { return value; } }
    private static string Get(Dictionary<string, string> values, string key, string fallback = "") => values.GetValueOrDefault(key, fallback);
    private static int Int(Dictionary<string, string> values, string key, int fallback = 0) => int.TryParse(Get(values, key), out var value) ? value : fallback;
    private static bool Bool(Dictionary<string, string> values, string key, bool fallback = false) => bool.TryParse(Get(values, key), out var value) ? value : fallback;
    private static T EnumValue<T>(Dictionary<string, string> values, string key, T fallback) where T : struct, Enum => Enum.TryParse<T>(Get(values, key), true, out var value) ? value : fallback;

    private static string Protect(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(encrypted);
    }

    private static string Unprotect(string value)
    {
        if (!value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)) return value;
        try
        {
            var decrypted = ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }
}
