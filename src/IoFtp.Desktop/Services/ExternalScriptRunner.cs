using System.Diagnostics;
using System.IO;

namespace IoFtp.Desktop.Services;

internal sealed record ScriptRunResult(string Name, int ExitCode, string Output, string Error);

internal sealed class ExternalScriptRunner
{
    public async Task<IReadOnlyList<ScriptRunResult>> RunEventAsync(string eventName, IReadOnlyDictionary<string, string>? variables = null, bool includeDisabled = false, Guid? onlyId = null)
    {
        var scripts = new ExternalScriptStore().Load().Where(script => script.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase) && (includeDisabled || script.Enabled) && (onlyId is null || script.Id == onlyId)).ToList();
        var results = new List<ScriptRunResult>();
        foreach (var script in scripts)
        {
            var result = await RunAsync(script, variables ?? new Dictionary<string, string>()); results.Add(result);
            if (result.ExitCode != 0 && script.BlockOnFailure) throw new InvalidOperationException($"External script '{script.Name}' failed with exit code {result.ExitCode}: {result.Error}");
        }
        return results;
    }

    public async Task<ScriptRunResult> RunAsync(ExternalScriptDefinition script, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(script.FileName) || !File.Exists(script.FileName)) return new(script.Name, -1, "", "Script file was not found.");
        string Expand(string value) { foreach (var pair in variables) value = value.Replace($"%{pair.Key}%", pair.Value, StringComparison.OrdinalIgnoreCase); return value; }
        var file = script.FileName; var arguments = Expand(script.Arguments); string executable; string finalArguments;
        if (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) { executable = "powershell.exe"; finalArguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\" {arguments}"; }
        else if (file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) { executable = "cmd.exe"; finalArguments = $"/d /c \"\"{file}\" {arguments}\""; }
        else { executable = file; finalArguments = arguments; }
        var start = new ProcessStartInfo(executable, finalArguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = string.IsNullOrWhiteSpace(script.WorkingDirectory) ? Path.GetDirectoryName(file)! : Expand(script.WorkingDirectory) };
        using var process = Process.Start(start); if (process is null) return new(script.Name, -1, "", "Process could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(script.TimeoutSeconds, 1, 3600)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } return new(script.Name, -2, await output, "Script timed out."); }
        return new(script.Name, process.ExitCode, await output, await error);
    }
}
