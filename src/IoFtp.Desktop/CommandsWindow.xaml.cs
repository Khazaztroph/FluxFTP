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
    private readonly string _siteName;
    private readonly string _selectedPath;
    private readonly bool _selectedIsDirectory;
    private readonly Func<Task>? _refreshDirectory;
    private readonly Func<string, Dictionary<string, string>, bool, Task>? _scriptEvent;
    private readonly List<CommandPreset> _presets;

    public CommandsWindow(IRemoteSession session, string siteName, string selectedPath, bool selectedIsDirectory, Func<Task>? refreshDirectory = null, Func<string, Dictionary<string, string>, bool, Task>? scriptEvent = null)
    {
        InitializeComponent(); _session = session; _siteName = siteName; _selectedPath = selectedPath; _selectedIsDirectory = selectedIsDirectory; _refreshDirectory = refreshDirectory; _scriptEvent = scriptEvent;
        var selectedName = Path.GetFileName(selectedPath.TrimEnd('/'));
        SelectionText.Text = $"Site: {siteName}    Selected: {(selectedPath.Length == 0 ? "none" : selectedPath)}";
        _presets =
        [
            new("ioFTPD / PRE selected release", ["SITE PRE %d[Pre Type: ie. mp3, divx] %f", "LIST"]),
            new("ioFTPD/glFTPD / TAGLINE", ["SITE TAGLINE %d[New Tagline:]"]),
            new("ioFTPD/glFTPD / Show NFO", ["&window", "SITE NFO"]),
            new("ioFTPD/glFTPD / Who is online", ["SITE WHO"]),
            new("ioFTPD/glFTPD / Site rules", ["SITE RULES"]),
            new("ioFTPD / Requests", ["SITE REQUESTS"]),
            new("ioFTPD/glFTPD / Search", ["SITE SEARCH %d[Search text:]"]),
            new("ioFTPD/glFTPD / Nukes", ["SITE NUKES"]),
            new("ioFTPD/glFTPD / Latest uploads", ["SITE NEW"]),
            new("ioFTPD/glFTPD / SITE HELP", ["SITE HELP"]),
            .. CreateGlFtpdPresets(),
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
                if (runInsideSelection && preParts.Length > 2)
                {
                    var validation = SectionReleaseValidator.Validate(preParts[2], preVariables["name"]);
                    if (!validation.Accepted && validation.Mode == SectionValidationMode.Block)
                        throw new InvalidOperationException($"PRE blocked: {validation.Message}");
                    if (!validation.Accepted && validation.Mode == SectionValidationMode.Warning &&
                        MessageBox.Show($"{validation.Message}{Environment.NewLine}{Environment.NewLine}Continue with PRE?",
                            "Section precheck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        throw new OperationCanceledException("PRE cancelled by section precheck.");
                    if (!validation.Accepted)
                        OutputBox.AppendText($"Section precheck warning ({_siteName}): {validation.Message}{Environment.NewLine}{Environment.NewLine}");
                }
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

    private static IEnumerable<CommandPreset> CreateGlFtpdPresets()
    {
        // Shared ioFTPD/glFTPD commands are declared above. Keep this list to
        // glFTPD-only commands so the preset menu never contains duplicates.
        (string Name, string Command)[] commands =
        [
            ("Files / DUPE search", "SITE DUPE %d[Search text:]"),
            ("Files / FDUPE search", "SITE FDUPE %d[Search text:]"),
            ("Files / CHMOD", "SITE CHMOD %d[Mode, e.g. 755:] %f"),
            ("Files / LOCATE", "SITE LOCATE %d[Filename:]") ,
            ("Files / NUKE selected", "SITE NUKE %f %d[Multiplier:] %d[Reason:]") ,
            ("Files / UNNUKE selected", "SITE UNNUKE %f %d[Message:]") ,
            ("Files / Recent unnukes", "SITE UNNUKES"),
            ("Files / REQUEST", "SITE REQUEST %d[Request or blank to list:]") ,
            ("Files / REQFILLED", "SITE REQFILLED %d[Request number:]") ,
            ("Files / UNDUPE", "SITE UNDUPE %d[Filename:]") ,
            ("Files / PREDUPE", "SITE PREDUPE %d[Filename:]") ,
            ("Files / WIPE selected", "SITE WIPE -r %f"),
            ("Files / XDUPE mode", "SITE XDUPE %d[Mode:]") ,

            ("Groups / CHGADMIN", "SITE CHGADMIN %d[User:] %d[Group:]") ,
            ("Groups / CHGRP", "SITE CHGRP %d[User:] %d[Group:]") ,
            ("Groups / GADDUSER", "SITE GADDUSER %d[Group:] %d[User:] %d[Password:]") ,
            ("Groups / GINFO", "SITE GINFO %d[Group:]") ,
            ("Groups / My groups", "SITE GROUP"),
            ("Groups / Join or leave group", "SITE GROUP %d[Group:]") ,
            ("Groups / Available groups", "SITE GROUPS"),
            ("Groups / Group info", "SITE GRP %d[Group:]") ,
            ("Groups / GRPCHANGE help", "SITE GRPCHANGE"),
            ("Groups / Add group", "SITE GRPADD %d[Group:] %d[Description:]") ,
            ("Groups / Delete group", "SITE GRPDEL %d[Group:]") ,
            ("Groups / Rename group", "SITE GRPREN %d[Old group:] %d[New group:]") ,
            ("Groups / Group description", "SITE GRPNFO %d[Group:] %d[Description:]") ,

            ("Users / Add user", "SITE ADDUSER %d[User:] %d[Password:]") ,
            ("Users / Add IP", "SITE ADDIP %d[User:] %d[ident@ip:]") ,
            ("Users / CHANGE help", "SITE CHANGE"),
            ("Users / Change password", "SITE CHPASS %d[User:] %d[New password:]") ,
            ("Users / Change own password", "SITE PASSWD %d[New password:]") ,
            ("Users / Delete IP", "SITE DELIP %d[User:] %d[ident@ip:]") ,
            ("Users / Delete user", "SITE DELUSER %d[User:]") ,
            ("Users / Emulate user", "SITE EMULATE %d[User:]") ,
            ("Users / My flags", "SITE FLAGS"),
            ("Users / User flags", "SITE FLAGS %d[User:]") ,
            ("Users / Give credits", "SITE GIVE %d[User:] %d[Amount, e.g. 100M:] %d[Message:]") ,
            ("Users / Take credits", "SITE TAKE %d[User:] %d[Amount, e.g. 100M:] %d[Message:]") ,
            ("Users / Kick user", "SITE KICK %d[User:]") ,
            ("Users / Kill PID", "SITE KILL %d[PID:]") ,
            ("Users / Purge deleted", "SITE PURGE %d[User or blank for all:]") ,
            ("Users / Re-add user", "SITE READD %d[User or blank to list:]") ,
            ("Users / Rename user", "SITE RENUSER %d[Old user:] %d[New user:]") ,
            ("Users / Raw userfile", "SITE SHOW %d[User:]") ,
            ("Users / User details", "SITE USER %d[User or blank:]") ,
            ("Users / List users", "SITE USERS"),
            ("Users / Last on", "SITE LASTON %d[Options or blank:]") ,
            ("Users / Seen", "SITE SEEN %d[User:]") ,
            ("Users / Detailed online users", "SITE SWHO"),

            ("Logs / Error log", "SITE ERRLOG %d[Number/search or blank:]") ,
            ("Logs / Failed logins", "SITE LOGINS %d[Number/search or blank:]") ,
            ("Logs / Request log", "SITE REQLOG %d[Number/search or blank:]") ,
            ("Logs / Sysop log", "SITE SYSLOG %d[Number/search or blank:]") ,
            ("Logs / Update dirlog", "SITE UPDATE %d[Directory string:]") ,

            ("Stats / All-time downloads", "SITE ALDN %d[Options or blank:]") ,
            ("Stats / All-time uploads", "SITE ALUP %d[Options or blank:]") ,
            ("Stats / Daily downloads", "SITE DAYDN %d[Options or blank:]") ,
            ("Stats / Daily uploads", "SITE DAYUP %d[Options or blank:]") ,
            ("Stats / Monthly downloads", "SITE MONTHDN %d[Options or blank:]") ,
            ("Stats / Monthly uploads", "SITE MONTHUP %d[Options or blank:]") ,
            ("Stats / Nuke top", "SITE NUKETOP %d[Options or blank:]") ,
            ("Stats / Weekly downloads", "SITE WKDN %d[Options or blank:]") ,
            ("Stats / Weekly uploads", "SITE WKUP %d[Options or blank:]") ,
            ("Stats / All-time group uploads", "SITE GPAL %d[Options or blank:]") ,
            ("Stats / Monthly group uploads", "SITE GPMONTHUP %d[Options or blank:]") ,
            ("Stats / Monthly group downloads", "SITE GPMONTHDN %d[Options or blank:]") ,
            ("Stats / Weekly group uploads", "SITE GPWK %d[Options or blank:]") ,
            ("Stats / Weekly group downloads", "SITE GPWD %d[Options or blank:]") ,
            ("Stats / All-time group downloads", "SITE GPAD %d[Options or blank:]") ,
            ("Stats / User statistics", "SITE STATS %d[User or blank:]") ,
            ("Stats / Total traffic", "SITE TRAFFIC"),

            ("Misc / Aliases", "SITE ALIAS"),
            ("Misc / CD paths", "SITE CDPATH"),
            ("Misc / Colors", "SITE COLOR %d[on, off or show:]") ,
            ("Misc / Idle settings", "SITE IDLE %d[Seconds or blank:]") ,
            ("Misc / Message variables", "SITE MSG"),
            ("Misc / Oneliner", "SITE ONEL %d[Text or blank to show:]") ,
            ("Misc / Status", "SITE STAT"),
            ("Misc / Server time", "SITE TIME"),
            ("Misc / glFTPD version", "SITE VERS"),
            ("Misc / Welcome screen", "SITE WELCOME"),

            ("pzs-ng / Invite", "SITE INVITE"),
            ("pzs-ng / Rescan selected", "SITE RESCAN"),
            ("pzs-ng / Audio sort", "SITE AUDIOSORT"),
            ("Custom / NFOVIEW", "SITE NFOVIEW"),
            ("Custom / NFOX", "SITE NFOX"),
            ("Custom / RAR details", "SITE RARDTL"),
            ("Custom / RAR test", "SITE RARTEST"),
            ("Custom / RESCAN2", "SITE RESCAN2"),
            ("Custom / ZIP check", "SITE ZIPCHK"),
            ("Custom / ZIP list", "SITE ZIPLIST"),
            ("Custom / Unzip", "SITE UNZIP")
        ];

        return commands.Select(item => new CommandPreset($"glFTPD / {item.Name}", [item.Command]));
    }
    private sealed record CommandPreset(string Name, IReadOnlyList<string> Commands, bool Imported = false) { public override string ToString() => Name; }
}
