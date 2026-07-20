using System.IO;
using System.Windows;
using System.Windows.Controls;
using IoFtp.Core.Abstractions;
using IoFtp.Desktop.Services;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace IoFtp.Desktop;

public partial class CommandsWindow : Window
{
    private readonly IRemoteSession _session;
    private readonly string _selectedPath;
    private readonly bool _selectedIsDirectory;
    private readonly Func<Task>? _refreshDirectory;
    private readonly Func<string, Dictionary<string, string>, bool, Task>? _scriptEvent;
    private readonly List<CommandPreset> _presets;

    public CommandsWindow(IRemoteSession session, string siteName, string selectedPath, bool selectedIsDirectory, Func<Task>? refreshDirectory = null, Func<string, Dictionary<string, string>, bool, Task>? scriptEvent = null)
    {
        InitializeComponent(); _session = session; _selectedPath = selectedPath; _selectedIsDirectory = selectedIsDirectory; _refreshDirectory = refreshDirectory; _scriptEvent = scriptEvent;
        var selectedName = Path.GetFileName(selectedPath.TrimEnd('/'));
        SelectionText.Text = $"Site: {siteName}    Selected: {(selectedPath.Length == 0 ? "none" : selectedPath)}";
        _presets =
        [
            new("ioFTPD / PRE selected release", ["SITE PRE %d[Pre Type: ie. mp3, divx] %f", "LIST"]),
            new("ioFTPD / TAGLINE", ["SITE TAGLINE %d[New Tagline:]"]),
            new("ioFTPD / Show NFO", ["&window", "SITE NFO"]),
            new("ioFTPD / Who is online", ["SITE WHO"]),
            new("ioFTPD / Site rules", ["SITE RULES"]),
            new("ioFTPD / Requests", ["SITE REQUESTS"]),
            new("ioFTPD / Search", ["SITE SEARCH %d[Search text:]"]),
            new("ioFTPD / Nukes", ["SITE NUKES"]),
            new("ioFTPD / Latest uploads", ["SITE NEW"]),
            new("ioFTPD / SITE HELP", ["SITE HELP"]),
            new("Raw command", [""])
        ];
        PresetBox.ItemsSource = _presets; PresetBox.SelectedIndex = 0;
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    { if (PresetBox.SelectedItem is CommandPreset preset) { CommandBox.Text = string.Join(Environment.NewLine, preset.Commands); CommandBox.Focus(); CommandBox.CaretIndex = CommandBox.Text.Length; } }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "FTPRush commands (RushCmd.xml)|RushCmd.xml|XML files (*.xml)|*.xml", Title = "Import FTPRush command pack" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var imported = FtpRushCommandImporter.Import(picker.FileName);
            _presets.RemoveAll(item => item.Imported);
            _presets.AddRange(imported.Select(item => new CommandPreset(item.DisplayName, item.Lines, true)));
            PresetBox.Items.Refresh(); PresetBox.SelectedIndex = imported.Count > 0 ? _presets.Count - imported.Count : 0;
            OutputBox.AppendText($"Imported {imported.Count} FTPRush commands from {Path.GetFileName(picker.FileName)}.{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception exception) { OutputBox.AppendText($"Import failed: {exception.Message}{Environment.NewLine}"); }
    }

    private async void LoadSiteHelp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _session.ExecuteCommandAsync("SITE HELP", timeout.Token);
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SITE", "HELP", "COMMAND", "COMMANDS", "AVAILABLE", "END", "SYNTAX", "USE" };
            var names = Regex.Matches(result.Message, @"(?<![A-Z0-9_-])([A-Z][A-Z0-9_-]{1,})(?=\s|$)")
                .Select(match => match.Groups[1].Value).Where(name => !ignored.Contains(name) && !int.TryParse(name, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var existing = _presets.Select(preset => preset.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                if (existing.Add($"ioFTPD / {name}")) _presets.Insert(_presets.Count - 1, new CommandPreset($"ioFTPD / {name}", [$"SITE {name}"], true));
            PresetBox.Items.Refresh(); OutputBox.AppendText($"Loaded {names.Count} commands reported by SITE HELP.{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception exception) { OutputBox.AppendText($"SITE HELP failed: {exception.Message}{Environment.NewLine}"); }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IsEnabled = false; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var commands = await ExpandCommandsAsync(CommandBox.Text);
            foreach (var command in commands)
            {
                if (command.Equals("LIST", StringComparison.OrdinalIgnoreCase))
                { if (_refreshDirectory is not null) await _refreshDirectory(); continue; }
                var runInsideSelection = _selectedIsDirectory && command.StartsWith("SITE PRE ", StringComparison.OrdinalIgnoreCase);
                var preParts = command.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                var preVariables = new Dictionary<string, string> { ["site"] = SelectionText.Text, ["path"] = _selectedPath, ["name"] = Path.GetFileName(_selectedPath.TrimEnd('/')), ["section"] = preParts.Length > 2 ? preParts[2] : "", ["status"] = "Starting" };
                if (runInsideSelection && _scriptEvent is not null) await _scriptEvent("BeforePre", preVariables, false);
                if (runInsideSelection)
                {
                    await _session.ExecuteCommandAsync($"CWD {_selectedPath}", timeout.Token);
                    OutputBox.AppendText($"Working directory: {_selectedPath}{Environment.NewLine}");
                }
                RemoteCommandResult result;
                try { result = await _session.ExecuteCommandAsync(command, timeout.Token); }
                finally
                {
                    if (runInsideSelection)
                    {
                        var parent = _selectedPath[..Math.Max(1, _selectedPath.TrimEnd('/').LastIndexOf('/'))];
                        await _session.ExecuteCommandAsync($"CWD {parent}", timeout.Token);
                    }
                }
                OutputBox.AppendText($"> {SafeDisplay(command)}{Environment.NewLine}{result.Message}{Environment.NewLine}{Environment.NewLine}");
                if (runInsideSelection && _scriptEvent is not null) { preVariables["status"] = "Completed"; await _scriptEvent("AfterPre", preVariables, true); }
            }
            OutputBox.ScrollToEnd();
        }
        catch (Exception exception) { OutputBox.AppendText($"Error: {exception.Message}{Environment.NewLine}{Environment.NewLine}"); }
        finally { IsEnabled = true; }
    }

    private async Task<IReadOnlyList<string>> ExpandCommandsAsync(string script)
    {
        var selectedName = Path.GetFileName(_selectedPath.TrimEnd('/')); var parent = _selectedPath[..Math.Max(0, _selectedPath.LastIndexOf('/') + 1)];
        var lines = script.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('&') && !line.StartsWith('/')).ToList();
        for (var index = 0; index < lines.Count; index++)
        {
            lines[index] = lines[index].Replace("%f", selectedName, StringComparison.OrdinalIgnoreCase).Replace("%p", parent, StringComparison.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(lines[index], @"%d\[(?:\(([^)]+)\)|([^\]]+))\]", RegexOptions.IgnoreCase))
            {
                var label = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var dialog = new CommandParameterWindow(label) { Owner = this };
                if (dialog.ShowDialog() != true) throw new OperationCanceledException("Command cancelled.");
                lines[index] = lines[index].Replace(match.Value, dialog.Value, StringComparison.OrdinalIgnoreCase);
            }
        }
        await Task.CompletedTask; return lines;
    }

    private static string SafeDisplay(string command)
    {
        var verb = command.Trim().Split(' ', 2)[0];
        return verb.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? "PASS ********" : command;
    }
    private sealed record CommandPreset(string Name, IReadOnlyList<string> Commands, bool Imported = false) { public override string ToString() => Name; }
}
