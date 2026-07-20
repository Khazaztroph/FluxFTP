using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using IoFtp.Desktop.Models;

namespace IoFtp.Desktop.Services;

internal sealed class GlobalSettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxFTP", "settings.json");
    private readonly string _oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ioFTP", "settings.json");
    public GlobalSettings Load()
    {
        try
        {
            if (File.Exists(_path)) return Unprotect(JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(_path)) ?? new());
            if (!File.Exists(_oldPath)) return new();
            var migrated = Unprotect(JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(_oldPath)) ?? new()); Save(migrated); return migrated;
        }
        catch { return new(); }
    }
    public void Save(GlobalSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temporary = _path + ".tmp";
        var protectedSettings = settings with { ApiPassword = Protect(settings.ApiPassword), ProxyPassword = Protect(settings.ProxyPassword) };
        File.WriteAllText(temporary, JsonSerializer.Serialize(protectedSettings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)) return value;
        return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
    private static string UnprotectValue(string value)
    {
        if (!value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)) return value;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), null, DataProtectionScope.CurrentUser)); } catch { return ""; }
    }
    private static GlobalSettings Unprotect(GlobalSettings settings) => settings with { ApiPassword = UnprotectValue(settings.ApiPassword), ProxyPassword = UnprotectValue(settings.ProxyPassword) };
}
