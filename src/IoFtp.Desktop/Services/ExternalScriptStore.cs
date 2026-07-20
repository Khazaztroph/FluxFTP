using System.IO;
using System.Text.Json;

namespace IoFtp.Desktop.Services;

internal static class ScriptEvents
{
    public static readonly string[] All = ["Manual", "OnConnect", "OnDisconnect", "BeforeTransfer", "AfterTransfer", "TransferFailed", "BeforePre", "AfterPre"];
}

internal sealed record ExternalScriptDefinition(Guid Id, string Name, string Event, string FileName, string Arguments = "", string WorkingDirectory = "", int TimeoutSeconds = 30, bool Enabled = false, bool BlockOnFailure = false);

internal sealed class ExternalScriptStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "FluxFTP-scripts.json");
    public List<ExternalScriptDefinition> Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<ExternalScriptDefinition>>(File.ReadAllText(_path)) ?? [] : []; }
        catch { return []; }
    }
    public void Save(IEnumerable<ExternalScriptDefinition> scripts)
    {
        var temporary = _path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(scripts, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, _path, true);
    }
}
