using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using IoFtp.Core.Abstractions;
using IoFtp.Core.Models;
using IoFtp.Core.Transport;
using IoFtp.Desktop.Models;
using IoFtp.Desktop.Services;
using IoFtp.Engine.Abstractions;
using IoFtp.Engine.Models;
using IoFtp.Engine.Scheduling;
using ComboBox = System.Windows.Controls.ComboBox;
using ListView = System.Windows.Controls.ListView;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;

namespace IoFtp.Desktop;

public partial class MainWindow : Window
{
    private FtpRemoteSession? _remoteSession;
    private FtpRemoteSession? _leftRemoteSession;
    private ConnectionProfile? _leftProfile;
    private ConnectionProfile? _rightProfile;
    private string _localDirectory = Environment.CurrentDirectory;
    private string _rightLocalDirectory = Environment.CurrentDirectory;
    private string _leftRemoteDirectory = "/";
    private string _remoteDirectory = "/";
    private readonly ObservableCollection<QueueEntryView> _queue = [];
    private readonly GlobalTransferEngine _engine;
    private readonly GlobalSettingsStore _settingsStore = new();
    private readonly WindowLayoutStore _layoutStore = new();
    private ApiServer? _apiServer;
    private GlobalSettings _settings;
    private readonly string _queuePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxFTP", "queue.json");
    private readonly string _oldQueuePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ioFTP", "queue.json");
    private GridLength _visibleQueueHeight = new(190);
    private GridLength _visibleLogHeight = new(150);
    private string _leftSortProperty = "Name";
    private string _rightSortProperty = "Name";
    private ListSortDirection _leftSortDirection = ListSortDirection.Ascending;
    private ListSortDirection _rightSortDirection = ListSortDirection.Ascending;
    private Point _dragStart;
    private bool _reloadingQuickSites;
    private readonly SemaphoreSlim _leftNavigationGate = new(1, 1);
    private readonly SemaphoreSlim _rightNavigationGate = new(1, 1);
    private readonly System.Windows.Forms.NotifyIcon _trayIcon = new();
    private readonly System.Windows.Threading.DispatcherTimer _legendTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ExternalScriptRunner _scriptRunner = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private int _legendOffset;
    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        LogText.Text = $"FluxFTP {UpdateCheckService.CurrentVersion} started.{Environment.NewLine}No network connections have been opened.";
        _engine = new GlobalTransferEngine(new DesktopTransferExecutor(this));
        _engine.ConfigureLocalSlots(_settings.MaxLocalDownloadSlots, _settings.MaxLocalUploadSlots);
        _engine.StateChanged += Engine_StateChanged;
        QueueList.ItemsSource = _queue;
        ReloadQuickSites(LeftQuickSites);
        ReloadQuickSites(RightQuickSites);
        LoadQueue();
        if (!string.IsNullOrWhiteSpace(_settings.LocalDownloadPath) && Directory.Exists(_settings.LocalDownloadPath)) _localDirectory = _settings.LocalDownloadPath;
        if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(_localDirectory);
        Loaded += async (_, _) => { RestoreWindowLayout(); await RestartApiServerAsync(); if (_settings.CheckForUpdatesAtStartup) await CheckForUpdatesAsync(); };
        ConfigureTrayIcon();
        StateChanged += MainWindow_StateChanged;
        _legendTimer.Tick += (_, _) => UpdateLegendBar();
        _legendTimer.Start(); UpdateLegendBar();
    }

    private void LoadLocalDirectory(string directory)
    {
        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            LocalList.ItemsSource = Directory.EnumerateFileSystemEntries(fullDirectory)
                .Take(100)
                .Select(path =>
                {
                    var isDirectory = Directory.Exists(path);
                    var modified = isDirectory
                        ? Directory.GetLastWriteTime(path)
                        : File.GetLastWriteTime(path);
                    var size = isDirectory ? "Folder" : FormatSize(new FileInfo(path).Length);
                    return new LocalEntryView(Path.GetFileName(path), size, modified.ToString("yyyy-MM-dd HH:mm"), File.GetAttributes(path).ToString(), path, isDirectory);
                })
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _localDirectory = fullDirectory;
            LocalPath.Text = fullDirectory;
        }
        catch (Exception exception)
        {
            LogText.AppendText($"{Environment.NewLine}Local browse error: {exception.Message}");
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private async void SiteManager_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SiteManagerWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedProfile is not null)
            await ConnectAsync(dialog.SelectedProfile);
    }

    private async void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog(quickConnect: true) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Profile is not null)
            await ConnectAsync(dialog.Profile);
    }

    private async Task ConnectAsync(ConnectionProfile profile)
        => await ConnectPaneAsync(profile, false);

    private async Task ConnectPaneAsync(ConnectionProfile profile, bool left)
    {
        profile = ApplyGlobalProxy(profile);
        ConnectionStatus.Text = $"Connecting to {profile.Host}:{profile.Port}…";
        if (left) LeftSiteTitle.Text = $"REMOTE — {profile.Name.ToUpperInvariant()}";
        else RemoteSiteTitle.Text = $"REMOTE — {profile.Name.ToUpperInvariant()}";
        LogText.AppendText($"{Environment.NewLine}Connecting with {profile.Protocol} to {profile.Host}:{profile.Port}…");
        try
        {
            var session = left ? _leftRemoteSession : _remoteSession;
            if (session is not null) await session.DisposeAsync();
            session = new FtpRemoteSession();
            if (left) { _leftRemoteSession = session; _leftProfile = profile; LeftMode.SelectedIndex = 1; }
            else { _remoteSession = session; _rightProfile = profile; RightMode.SelectedIndex = 1; }
            var options = profile.EffectiveOptions;
            _engine.RegisterOrUpdateSite(new SitePolicy(profile.Id, profile.Name,
                MaxSlots: options.MaxSlots,
                MaxDownloads: options.AllowDownload ? options.MaxDownloadSlots : 0,
                MaxUploads: options.AllowUpload ? options.MaxUploadSlots : 0,
                Priority: options.Priority,
                BlockedSources: ResolveSiteNames(options.BlockTransfersFrom),
                BlockedTargets: ResolveSiteNames(options.BlockTransfersTo)));
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                await session.ConnectAsync(profile, connectTimeout.Token);
            new ProfileStore().PromoteAddress(profile.Id, session.ConnectedHost, session.ConnectedPort);
            ConnectionStatus.Text = $"Connected: {session.ConnectedHost}:{session.ConnectedPort}; loading files…";
            LogText.AppendText($"{Environment.NewLine}TLS login succeeded. Loading directory with {DescribeListingMode(profile.ListingMode, session.Capabilities)}…");
            LogText.ScrollToEnd();
            IReadOnlyList<IoFtp.Core.Abstractions.RemoteEntry> entries;
            using (var listTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                entries = await session.ListAsync(options.BasePath, listTimeout.Token);
            if (left) ShowLeftRemoteEntries(options.BasePath, entries); else ShowRemoteEntries(options.BasePath, entries);
            ConnectionStatus.Text = $"Connected: {session.ConnectedHost}:{session.ConnectedPort}";
            LogText.AppendText($"{Environment.NewLine}Connected. {entries.Count} remote entries received.");
            LogText.AppendText($"{Environment.NewLine}Capabilities: {string.Join(", ", session.Capabilities.OrderBy(value => value))}");
            await RunScriptsAsync("OnConnect", new() { ["site"] = profile.Name, ["host"] = session.ConnectedHost, ["path"] = options.BasePath, ["status"] = "Connected" }, true);
        }
        catch (Exception exception)
        {
            ConnectionStatus.Text = "Connection failed";
            if (left) LocalList.ItemsSource = null; else RemoteList.ItemsSource = null;
            LogText.AppendText($"{Environment.NewLine}Connection failed: {FriendlyMessage(exception)}");
        }
        finally { LogText.ScrollToEnd(); }
    }

    private ConnectionProfile ApplyGlobalProxy(ConnectionProfile profile) => _settings.ProxyType == ProxyType.None ? profile with { Proxy = null } : profile with
    {
        Proxy = new ProxyConfiguration(_settings.ProxyType, _settings.ProxyHost, _settings.ProxyPort, _settings.ProxyUsername, _settings.ProxyPassword, _settings.ProxyDns, _settings.ProxyDataConnections)
    };

    private static IReadOnlySet<Guid> ResolveSiteNames(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new HashSet<Guid>();
        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ProfileStore().Load().Where(profile => names.Contains(profile.Name)).Select(profile => profile.Id).ToHashSet();
    }

    private async void ConnectLeft_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SiteManagerWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedProfile is not null) await ConnectPaneAsync(dialog.SelectedProfile, true);
    }

    private async void ConnectRight_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SiteManagerWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedProfile is not null) await ConnectPaneAsync(dialog.SelectedProfile, false);
    }

    private void QuickSites_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is ComboBox combo) ReloadQuickSites(combo);
    }

    private void ReloadQuickSites(ComboBox combo)
    {
        _reloadingQuickSites = true;
        var selectedId = (combo.SelectedItem as QuickSiteChoice)?.Profile?.Id;
        var choices = new List<QuickSiteChoice> { new("Quick Connect…", null) };
        choices.AddRange(new ProfileStore().Load().Select(profile => new QuickSiteChoice(profile.Name, profile)));
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(choice => choice.Profile?.Id == selectedId) ?? choices[0];
        _reloadingQuickSites = false;
    }

    private async void LeftQuickSites_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingQuickSites && LeftQuickSites.SelectedItem is QuickSiteChoice { Profile: { } profile }) await ConnectPaneAsync(profile, true);
    }

    private async void RightQuickSites_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingQuickSites && RightQuickSites.SelectedItem is QuickSiteChoice { Profile: { } profile }) await ConnectPaneAsync(profile, false);
    }

    private async void DisconnectLeft_Click(object sender, RoutedEventArgs e)
    {
        if (_leftProfile is not null) await RunScriptsAsync("OnDisconnect", new() { ["site"] = _leftProfile.Name, ["host"] = _leftProfile.Host, ["path"] = _leftRemoteDirectory, ["status"] = "Disconnected" }, true);
        if (_leftRemoteSession is not null) { await _leftRemoteSession.DisposeAsync(); _leftRemoteSession = null; }
        if (_leftProfile is not null) _engine.DisconnectSite(_leftProfile.Id);
        LeftQuickSites.SelectedIndex = 0;
        LocalList.ItemsSource = null; _leftRemoteDirectory = "/"; LocalPath.Text = "/";
        LeftSiteTitle.Text = _leftProfile is null ? "REMOTE SITE" : $"REMOTE — {_leftProfile.Name.ToUpperInvariant()} (DISCONNECTED)";
        ConnectionStatus.Text = "Remote disconnected";
        LogText.AppendText($"{Environment.NewLine}Remote disconnected."); LogText.ScrollToEnd();
    }

    private async void DisconnectRight_Click(object sender, RoutedEventArgs e)
    {
        if (_rightProfile is not null) await RunScriptsAsync("OnDisconnect", new() { ["site"] = _rightProfile.Name, ["host"] = _rightProfile.Host, ["path"] = _remoteDirectory, ["status"] = "Disconnected" }, true);
        if (_remoteSession is not null) { await _remoteSession.DisposeAsync(); _remoteSession = null; }
        if (_rightProfile is not null) _engine.DisconnectSite(_rightProfile.Id);
        RightQuickSites.SelectedIndex = 0;
        RemoteList.ItemsSource = null; _remoteDirectory = "/"; RemotePath.Text = "/";
        RemoteSiteTitle.Text = _rightProfile is null ? "REMOTE SITE" : $"REMOTE — {_rightProfile.Name.ToUpperInvariant()} (DISCONNECTED)";
        ConnectionStatus.Text = "Remote disconnected";
        LogText.AppendText($"{Environment.NewLine}Remote disconnected."); LogText.ScrollToEnd();
    }

    private void CommandsLeft_Click(object sender, RoutedEventArgs e)
    {
        if (LeftMode.SelectedIndex != 1 || _leftRemoteSession?.IsConnected != true) { MessageBox.Show("Connect Remote first.", "Commands"); return; }
        var selectedItem = LocalList.SelectedItem as LocalEntryView;
        var selected = selectedItem?.FullPath ?? _leftRemoteDirectory;
        new CommandsWindow(_leftRemoteSession, _leftProfile?.Name ?? "Remote", selected, selectedItem?.IsDirectory ?? false, () => NavigateLeftRemoteAsync(_leftRemoteDirectory), RunScriptsAsync) { Owner = this }.Show();
    }

    private void CommandsRight_Click(object sender, RoutedEventArgs e)
    {
        if (RightMode.SelectedIndex != 1 || _remoteSession?.IsConnected != true) { MessageBox.Show("Connect Remote first.", "Commands"); return; }
        var selectedItem = RemoteList.SelectedItem as RemoteEntryView;
        var selected = selectedItem?.FullPath ?? _remoteDirectory;
        new CommandsWindow(_remoteSession, _rightProfile?.Name ?? "Remote", selected, selectedItem?.IsDirectory ?? false, () => NavigateRemoteAsync(_remoteDirectory), RunScriptsAsync) { Owner = this }.Show();
    }

    private void LeftMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (LeftMode.SelectedIndex == 0) { LeftSiteTitle.Text = "LOCAL"; LoadLocalDirectory(_localDirectory); }
        else
        {
            LeftSiteTitle.Text = _leftProfile is null ? "REMOTE SITE" : $"REMOTE — {_leftProfile.Name.ToUpperInvariant()}";
            LocalPath.Text = _leftRemoteDirectory;
            if (_leftRemoteSession?.IsConnected == true) _ = NavigateLeftRemoteAsync(_leftRemoteDirectory);
            else LocalList.ItemsSource = null;
        }
    }

    private void RightMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (RightMode.SelectedIndex == 0) { RemoteSiteTitle.Text = "LOCAL"; LoadRightLocalDirectory(_rightLocalDirectory); }
        else
        {
            RemoteSiteTitle.Text = _rightProfile is null ? "REMOTE SITE" : $"REMOTE — {_rightProfile.Name.ToUpperInvariant()}";
            RemotePath.Text = _remoteDirectory;
            if (_remoteSession?.IsConnected == true) _ = NavigateRemoteAsync(_remoteDirectory);
            else RemoteList.ItemsSource = null;
        }
    }

    private void ShowLeftRemoteEntries(string path, IReadOnlyList<RemoteEntry> entries)
    {
        _leftRemoteDirectory = NormalizeRemotePath(path); LocalPath.Text = _leftRemoteDirectory;
        LocalList.ItemsSource = entries.Select(entry => new LocalEntryView(entry.Name,
            entry.IsDirectory ? "Folder" : entry.Size is { } size ? FormatSize(size) : "—",
            entry.ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—", entry.Attributes, entry.FullPath, entry.IsDirectory))
            .OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void LeftHeader_Click(object sender, RoutedEventArgs e) =>
        SortList(LocalList, (GridViewColumnHeader)sender, ref _leftSortProperty, ref _leftSortDirection,
            LeftNameHeader, LeftModifiedHeader);

    private void RightHeader_Click(object sender, RoutedEventArgs e) =>
        SortList(RemoteList, (GridViewColumnHeader)sender, ref _rightSortProperty, ref _rightSortDirection,
            RightNameHeader, RightModifiedHeader);

    private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep Size, Modified and Attributes visible at every DPI setting;
        // the Name column receives the remaining panel width.
        var width = Math.Max(120, e.NewSize.Width - 90 - 150 - 110 - 24);
        if (ReferenceEquals(sender, LocalList)) LeftNameColumn.Width = width;
        else if (ReferenceEquals(sender, RemoteList)) RightNameColumn.Width = width;
    }

    private static void SortList(ListView list, GridViewColumnHeader header, ref string currentProperty,
        ref ListSortDirection currentDirection, params GridViewColumnHeader[] headers)
    {
        var property = header.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(property)) return;
        currentDirection = currentProperty == property && currentDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        currentProperty = property;
        var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(property, currentDirection));
        foreach (var item in headers)
        {
            var label = item.Tag?.ToString() is "Modified" or "DisplayModified" ? "Modified" : "Name";
            item.Content = item == header ? $"{label} {(currentDirection == ListSortDirection.Ascending ? "▲" : "▼")}" : label;
        }
    }

    private async Task NavigateLeftRemoteAsync(string path)
    {
        if (_leftRemoteSession?.IsConnected != true) return;
        await _leftNavigationGate.WaitAsync();
        try
        {
            if (_leftRemoteSession?.IsConnected != true) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var entries = await _leftRemoteSession.ListAsync(NormalizeRemotePath(path), timeout.Token);
            ShowLeftRemoteEntries(path, entries);
        }
        catch (Exception exception) { LogText.AppendText($"{Environment.NewLine}Remote: {FriendlyMessage(exception)}"); }
        finally { _leftNavigationGate.Release(); }
    }

    private void LoadRightLocalDirectory(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            RemoteList.ItemsSource = Directory.EnumerateFileSystemEntries(full).Take(100).Select(path =>
            {
                var folder = Directory.Exists(path); var modified = folder ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                return new RemoteEntryView(Path.GetFileName(path), folder ? "Folder" : FormatSize(new FileInfo(path).Length), modified.ToString("yyyy-MM-dd HH:mm"), File.GetAttributes(path).ToString(), path, folder);
            }).OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _rightLocalDirectory = full; RemotePath.Text = full;
        }
        catch (Exception exception) { LogText.AppendText($"{Environment.NewLine}Local browse error: {exception.Message}"); }
    }

    private void ShowRemoteEntries(string path, IReadOnlyList<RemoteEntry> entries)
    {
        _remoteDirectory = NormalizeRemotePath(path);
        RemotePath.Text = _remoteDirectory;
        RemoteList.ItemsSource = entries.Select(entry => new RemoteEntryView(
            entry.Name,
            entry.IsDirectory ? "Folder" : entry.Size is { } size ? FormatSize(size) : "—",
            entry.ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—",
            entry.Attributes,
            entry.FullPath,
            entry.IsDirectory))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task NavigateRemoteAsync(string path)
    {
        if (_remoteSession?.IsConnected != true) return;
        await _rightNavigationGate.WaitAsync();
        var normalized = NormalizeRemotePath(path);
        ConnectionStatus.Text = $"Loading {normalized}…";
        try
        {
            if (_remoteSession?.IsConnected != true) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var entries = await _remoteSession.ListAsync(normalized, timeout.Token);
            ShowRemoteEntries(normalized, entries);
            ConnectionStatus.Text = $"Connected — {entries.Count} entries";
        }
        catch (Exception exception)
        {
            ConnectionStatus.Text = "Directory load failed";
            LogText.AppendText($"{Environment.NewLine}Could not open {normalized}: {FriendlyMessage(exception)}");
            LogText.ScrollToEnd();
        }
        finally { _rightNavigationGate.Release(); }
    }

    private void LocalList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocalList.SelectedItem is not LocalEntryView { IsDirectory: true } entry) return;
        if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(entry.FullPath); else _ = NavigateLeftRemoteAsync(entry.FullPath);
    }

    private async void RemoteList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteList.SelectedItem is not RemoteEntryView { IsDirectory: true } entry) return;
        if (RightMode.SelectedIndex == 0) LoadRightLocalDirectory(entry.FullPath); else await NavigateRemoteAsync(entry.FullPath);
    }

    private void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        if (LeftMode.SelectedIndex == 0) { var parent = Directory.GetParent(_localDirectory); if (parent is not null) LoadLocalDirectory(parent.FullName); }
        else _ = NavigateLeftRemoteAsync(RemoteParent(_leftRemoteDirectory));
    }

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (RightMode.SelectedIndex == 0) { var parent = Directory.GetParent(_rightLocalDirectory); if (parent is not null) LoadRightLocalDirectory(parent.FullName); }
        else await NavigateRemoteAsync(RemoteParent(_remoteDirectory));
    }

    private async void LocalPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(LocalPath.Text); else await NavigateLeftRemoteAsync(LocalPath.Text); e.Handled = true; }
    }

    private async void RemotePath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { if (RightMode.SelectedIndex == 0) LoadRightLocalDirectory(RemotePath.Text); else await NavigateRemoteAsync(RemotePath.Text); e.Handled = true; }
    }

    private static string NormalizeRemotePath(string path)
    {
        var parts = new Stack<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..") { if (parts.Count > 0) parts.Pop(); }
            else if (part != ".") parts.Push(part);
        }
        return "/" + string.Join('/', parts.Reverse());
    }

    private static string RemoteParent(string path)
    {
        var normalized = NormalizeRemotePath(path); var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is not RemoteEntryView entry) return;
        if (entry.IsDirectory)
        {
            if (RightMode.SelectedIndex == 1 && LeftMode.SelectedIndex == 1 && _remoteSession is not null && _leftRemoteSession is not null)
                await QueueRemoteDirectoryAsync(_remoteSession, _leftRemoteSession, entry.FullPath,
                    NormalizeRemotePath($"{_leftRemoteDirectory}/{entry.Name}"), TransferDirection.RelayRightToLeft);
            return;
        }
        QueueEntryView queueEntry;
        if (RightMode.SelectedIndex == 1 && LeftMode.SelectedIndex == 0)
        {
            var destination = Path.Combine(_localDirectory, entry.Name);
            if (File.Exists(destination) && MessageBox.Show($"Replace '{entry.Name}'?", "Transfer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            queueEntry = AddQueue(entry.Name, entry.FullPath, destination, TransferDirection.Download);
        }
        else if (RightMode.SelectedIndex == 0 && LeftMode.SelectedIndex == 1)
            queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_leftRemoteDirectory}/{entry.Name}"), TransferDirection.UploadToLeft);
        else if (RightMode.SelectedIndex == 1 && LeftMode.SelectedIndex == 1)
            queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_leftRemoteDirectory}/{entry.Name}"), TransferDirection.RelayRightToLeft);
        else return;
        Schedule(queueEntry);
        if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(_localDirectory); else await NavigateLeftRemoteAsync(_leftRemoteDirectory);
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (LocalList.SelectedItem is not LocalEntryView entry) return;
        if (entry.IsDirectory)
        {
            if (LeftMode.SelectedIndex == 1 && RightMode.SelectedIndex == 1 && _leftRemoteSession is not null && _remoteSession is not null)
                await QueueRemoteDirectoryAsync(_leftRemoteSession, _remoteSession, entry.FullPath,
                    NormalizeRemotePath($"{_remoteDirectory}/{entry.Name}"), TransferDirection.RelayLeftToRight);
            return;
        }
        QueueEntryView queueEntry;
        if (LeftMode.SelectedIndex == 0 && RightMode.SelectedIndex == 1)
            queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_remoteDirectory}/{entry.Name}"), TransferDirection.Upload);
        else if (LeftMode.SelectedIndex == 1 && RightMode.SelectedIndex == 0)
        {
            var destination = Path.Combine(_rightLocalDirectory, entry.Name);
            if (File.Exists(destination) && MessageBox.Show($"Replace '{entry.Name}'?", "Transfer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            queueEntry = AddQueue(entry.Name, entry.FullPath, destination, TransferDirection.DownloadFromLeft);
        }
        else if (LeftMode.SelectedIndex == 1 && RightMode.SelectedIndex == 1)
            queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_remoteDirectory}/{entry.Name}"), TransferDirection.RelayLeftToRight);
        else return;
        Schedule(queueEntry);
        if (RightMode.SelectedIndex == 0) LoadRightLocalDirectory(_rightLocalDirectory); else await NavigateRemoteAsync(_remoteDirectory);
    }

    private void TransferLeftNow_Click(object sender, RoutedEventArgs e) => Upload_Click(sender, e);
    private void TransferRightNow_Click(object sender, RoutedEventArgs e) => Download_Click(sender, e);
    private void CopyNameLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) Clipboard.SetText(item.Name); }
    private void CopyPathLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) Clipboard.SetText(item.FullPath); }
    private void CopyNameRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) Clipboard.SetText(item.Name); }
    private void CopyPathRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) Clipboard.SetText(item.FullPath); }
    private async void RefreshLeft_Click(object sender, RoutedEventArgs e) { if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(_localDirectory); else await NavigateLeftRemoteAsync(_leftRemoteDirectory); }
    private async void RefreshRight_Click(object sender, RoutedEventArgs e) { if (RightMode.SelectedIndex == 0) LoadRightLocalDirectory(_rightLocalDirectory); else await NavigateRemoteAsync(_remoteDirectory); }

    private async void CreateFolderLeft_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync(true);
    private async void CreateFolderRight_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync(false);
    private async Task CreateFolderAsync(bool left)
    {
        var dialog = new CommandParameterWindow("New folder name:") { Owner = this }; if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        try
        {
            if (left && LeftMode.SelectedIndex == 0) Directory.CreateDirectory(Path.Combine(_localDirectory, dialog.Value));
            else if (!left && RightMode.SelectedIndex == 0) Directory.CreateDirectory(Path.Combine(_rightLocalDirectory, dialog.Value));
            else
            {
                var session = left ? _leftRemoteSession : _remoteSession; var directory = left ? _leftRemoteDirectory : _remoteDirectory;
                if (session?.IsConnected != true) return; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await session.ExecuteCommandAsync($"MKD {NormalizeRemotePath($"{directory}/{dialog.Value}")}", timeout.Token);
            }
            if (left) RefreshLeft_Click(this, new RoutedEventArgs()); else RefreshRight_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) { MessageBox.Show(FriendlyMessage(exception), "Create folder", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RenameLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) await RenameEntryAsync(true, item.Name, item.FullPath); }
    private async void RenameRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) await RenameEntryAsync(false, item.Name, item.FullPath); }
    private async Task RenameEntryAsync(bool left, string oldName, string oldPath)
    {
        var dialog = new CommandParameterWindow("New name:", oldName) { Owner = this }; if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value) || dialog.Value == oldName) return;
        try
        {
            if ((left && LeftMode.SelectedIndex == 0) || (!left && RightMode.SelectedIndex == 0))
            {
                var destination = Path.Combine(Path.GetDirectoryName(oldPath)!, dialog.Value);
                if (Directory.Exists(oldPath)) Directory.Move(oldPath, destination); else File.Move(oldPath, destination);
            }
            else
            {
                var session = left ? _leftRemoteSession : _remoteSession; if (session?.IsConnected != true) return;
                var destination = NormalizeRemotePath($"{RemoteParent(oldPath)}/{dialog.Value}"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await session.ExecuteCommandAsync($"RNFR {oldPath}", timeout.Token); await session.ExecuteCommandAsync($"RNTO {destination}", timeout.Token);
            }
            if (left) RefreshLeft_Click(this, new RoutedEventArgs()); else RefreshRight_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) { MessageBox.Show(FriendlyMessage(exception), "Rename", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void DeleteLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) await DeleteEntryAsync(true, item.Name, item.FullPath, item.IsDirectory); }
    private async void DeleteRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) await DeleteEntryAsync(false, item.Name, item.FullPath, item.IsDirectory); }
    private async Task DeleteEntryAsync(bool left, string name, string path, bool directory)
    {
        var warning = directory
            ? $"Permanently delete '{name}' and everything inside it?\n\nThis recursive operation cannot be undone."
            : $"Permanently delete '{name}'?";
        if (MessageBox.Show(warning, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if ((left && LeftMode.SelectedIndex == 0) || (!left && RightMode.SelectedIndex == 0)) { if (directory) Directory.Delete(path, true); else File.Delete(path); }
            else
            {
                var session = left ? _leftRemoteSession : _remoteSession; if (session?.IsConnected != true) return;
                using var timeout = new CancellationTokenSource(directory ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(20));
                if (directory) await DeleteRemoteTreeAsync(session, path, timeout.Token);
                else await session.ExecuteCommandAsync($"DELE {path}", timeout.Token);
            }
            if (left) RefreshLeft_Click(this, new RoutedEventArgs()); else RefreshRight_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) { MessageBox.Show(FriendlyMessage(exception), "Delete", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static async Task DeleteRemoteTreeAsync(FtpRemoteSession session, string root, CancellationToken cancellationToken)
    {
        var directories = new Stack<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop(); directories.Push(directory);
            foreach (var child in await session.ListAsync(directory, cancellationToken))
            {
                if (child.Name is "." or "..") continue;
                if (child.IsDirectory) pending.Push(child.FullPath);
                else await session.ExecuteCommandAsync($"DELE {child.FullPath}", cancellationToken);
            }
        }
        while (directories.Count > 0)
            await session.ExecuteCommandAsync($"RMD {directories.Pop()}", cancellationToken);
    }

    private async void ChmodLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) await ChmodAsync(true, item.FullPath); }
    private async void ChmodRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) await ChmodAsync(false, item.FullPath); }
    private async Task ChmodAsync(bool left, string path)
    {
        if ((left && LeftMode.SelectedIndex == 0) || (!left && RightMode.SelectedIndex == 0)) { MessageBox.Show("CHMOD is available for remote entries.", "Attributes"); return; }
        var dialog = new CommandParameterWindow("UNIX mode (for example 755):", "755") { Owner = this }; if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        try
        {
            var session = left ? _leftRemoteSession : _remoteSession; if (session?.IsConnected != true) return; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.ExecuteCommandAsync($"SITE CHMOD {dialog.Value.Trim()} {path}", timeout.Token);
            if (left) await NavigateLeftRemoteAsync(_leftRemoteDirectory); else await NavigateRemoteAsync(_remoteDirectory);
        }
        catch (Exception exception) { MessageBox.Show(FriendlyMessage(exception), "Attributes", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async Task QueueRemoteDirectoryAsync(FtpRemoteSession source, FtpRemoteSession destination,
        string sourceRoot, string destinationRoot, TransferDirection direction)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var pending = new Stack<(string Source, string Destination)>();
        var files = new List<(RemoteEntry Entry, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        var fileCount = 0;
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            await EnsureRemoteDirectoryAsync(destination, folder.Destination, timeout.Token);
            foreach (var child in await source.ListAsync(folder.Source, timeout.Token))
            {
                var target = NormalizeRemotePath($"{folder.Destination}/{child.Name}");
                if (child.IsDirectory) pending.Push((child.FullPath, target));
                else if (!ShouldSkip(child.Name)) { files.Add((child, target)); fileCount++; }
            }
        }
        foreach (var file in files.OrderBy(file => PriorityRank(file.Entry.Name)).ThenBy(file => file.Entry.Name, StringComparer.OrdinalIgnoreCase))
            Schedule(AddQueue(file.Entry.Name, file.Entry.FullPath, file.Destination, direction, file.Entry.Size ?? 0));
        LogText.AppendText($"{Environment.NewLine}Queued remote folder {sourceRoot}: {fileCount} files.");
        LogText.ScrollToEnd();
    }

    private int PriorityRank(string name)
    {
        var patterns = _settings.PriorityPatterns.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < patterns.Length; index++)
            if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(patterns[index], name, true)) return index;
        return patterns.Length;
    }

    private bool ShouldSkip(string name)
    {
        var patterns = _settings.SkipPatterns.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return patterns.Any(pattern => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, true));
    }

    private static async Task EnsureRemoteDirectoryAsync(FtpRemoteSession session, string path, CancellationToken token)
    {
        var current = "";
        foreach (var part in NormalizeRemotePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            try { await session.ExecuteCommandAsync($"MKD {current}", token); } catch { }
        }
    }

    private void QueueLeft_Click(object sender, RoutedEventArgs e) => Upload_Click(sender, e);
    private void QueueRight_Click(object sender, RoutedEventArgs e) => Download_Click(sender, e);

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);
    private void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListView list && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListViewItem item)
            item.IsSelected = true;
    }
    private void LocalList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && LocalList.SelectedItem is not null &&
            (e.GetPosition(this) - _dragStart).Length > SystemParameters.MinimumHorizontalDragDistance)
            DragDrop.DoDragDrop(LocalList, "ioftp-left", DragDropEffects.Copy);
    }
    private void RemoteList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && RemoteList.SelectedItem is not null &&
            (e.GetPosition(this) - _dragStart).Length > SystemParameters.MinimumHorizontalDragDistance)
            DragDrop.DoDragDrop(RemoteList, "ioftp-right", DragDropEffects.Copy);
    }
    private void LocalList_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.Text) as string == "ioftp-right") Download_Click(sender, e); }
    private void RemoteList_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.Text) as string == "ioftp-left") Upload_Click(sender, e); }

    private QueueEntryView AddQueue(string name, string source, string destination, TransferDirection direction, long totalBytes = 0, Guid? sourceProfileId = null)
    {
        var entry = new QueueEntryView(name, source, destination, direction, totalBytes: totalBytes) { SourceProfileId = sourceProfileId }; _queue.Add(entry); SaveQueue(); UpdateQueueStatus(); return entry;
    }

    private void Schedule(QueueEntryView entry)
    {
        var (sourceSite, destinationSite) = SitesFor(entry.Direction);
        sourceSite ??= entry.SourceProfileId;
        entry.State = "Queued";
        _engine.Enqueue([new TransferWorkItem(entry.Id, entry.Id, entry.Name, sourceSite, destinationSite,
            entry.Source, entry.Destination, entry.TotalBytes, QueuedAt: DateTimeOffset.UtcNow)]);
        SaveQueue(); UpdateQueueStatus();
    }

    private (Guid? Source, Guid? Destination) SitesFor(TransferDirection direction) => direction switch
    {
        TransferDirection.Download => (_rightProfile?.Id, null),
        TransferDirection.Upload => (null, _rightProfile?.Id),
        TransferDirection.UploadToLeft => (null, _leftProfile?.Id),
        TransferDirection.DownloadFromLeft => (_leftProfile?.Id, null),
        TransferDirection.RelayLeftToRight => (_leftProfile?.Id, _rightProfile?.Id),
        TransferDirection.RelayRightToLeft => (_rightProfile?.Id, _leftProfile?.Id),
        TransferDirection.ApiDownload => (null, null),
        _ => (null, null)
    };

    private async Task ExecuteScheduledAsync(QueueEntryView entry, CancellationToken cancellationToken)
    {
        var needsLeft = entry.Direction is TransferDirection.UploadToLeft or TransferDirection.DownloadFromLeft or TransferDirection.RelayLeftToRight or TransferDirection.RelayRightToLeft;
        var needsRight = entry.Direction is TransferDirection.Download or TransferDirection.Upload or TransferDirection.RelayLeftToRight or TransferDirection.RelayRightToLeft;
        var apiProfile = entry.Direction == TransferDirection.ApiDownload ? new ProfileStore().Load().FirstOrDefault(profile => profile.Id == entry.SourceProfileId) : null;
        if ((needsLeft && _leftProfile is null) || (needsRight && _rightProfile is null) || (entry.Direction == TransferDirection.ApiDownload && apiProfile is null))
            throw new InvalidOperationException("A required site is not connected.");
        FtpRemoteSession? leftWorker = null; FtpRemoteSession? rightWorker = null;
        FtpRemoteSession? apiWorker = null;
        try
        {
            entry.State = "Transferring";
            SaveQueue();
            await RunScriptsAsync("BeforeTransfer", TransferScriptVariables(entry, "Starting"), false);
            if (needsLeft) leftWorker = await CreateWorkerAsync(_leftProfile!, cancellationToken);
            if (needsRight) rightWorker = await CreateWorkerAsync(_rightProfile!, cancellationToken);
            if (apiProfile is not null) apiWorker = await CreateWorkerAsync(ApplyGlobalProxy(apiProfile), cancellationToken);
            var progress = new Progress<long>(bytes =>
            {
                entry.BytesTransferred = bytes;
                if (DateTime.UtcNow - entry.LastPersistedAt >= TimeSpan.FromSeconds(1))
                { entry.LastPersistedAt = DateTime.UtcNow; SaveQueue(); }
            });
            if (entry.Direction is TransferDirection.Download or TransferDirection.DownloadFromLeft or TransferDirection.ApiDownload)
            {
                var session = entry.Direction == TransferDirection.ApiDownload ? apiWorker! : entry.Direction == TransferDirection.Download ? rightWorker! : leftWorker!;
                var partial = entry.Destination + ".ioftp-part";
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, true);
                output.Seek(0, SeekOrigin.End);
                entry.BytesTransferred = output.Length;
                await session.DownloadAsync(entry.Source, output, output.Length, progress, cancellationToken);
                File.Move(partial, entry.Destination, true);
            }
            else if (entry.Direction is TransferDirection.Upload or TransferDirection.UploadToLeft)
            {
                var session = entry.Direction == TransferDirection.Upload ? rightWorker! : leftWorker!;
                await EnsureRemoteDirectoryAsync(session, RemoteParent(entry.Destination), cancellationToken);
                await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                var offset = Math.Min(entry.BytesTransferred, input.Length); input.Seek(offset, SeekOrigin.Begin);
                await session.UploadAsync(entry.Destination, input, offset, progress, cancellationToken);
            }
            else
            {
                var sourceSession = entry.Direction == TransferDirection.RelayLeftToRight ? leftWorker! : rightWorker!;
                var destinationSession = entry.Direction == TransferDirection.RelayLeftToRight ? rightWorker! : leftWorker!;
                await EnsureRemoteDirectoryAsync(destinationSession, RemoteParent(entry.Destination), cancellationToken);
                var directFxpAvailable = destinationSession.Capabilities.Contains("CPSV") ||
                    (sourceSession.Capabilities.Contains("SSCN") && destinationSession.Capabilities.Contains("SSCN"));
                if (directFxpAvailable)
                {
                    try
                    {
                        LogText.AppendText($"{Environment.NewLine}Attempting direct secure FXP: {entry.Name}");
                        var fxpStartedAt = DateTime.UtcNow;
                        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var destinationProfile = entry.Direction == TransferDirection.RelayLeftToRight ? _rightProfile! : _leftProfile!;
                        var monitor = MonitorFxpAsync(destinationProfile, entry, monitorCancellation.Token);
                        try { await sourceSession.FxpToAsync(destinationSession, entry.Source, entry.Destination, cancellationToken); }
                        finally
                        {
                            monitorCancellation.Cancel();
                            // Progress monitoring must never affect the transfer itself.
                            try { await monitor; } catch (Exception) { }
                        }
                        if (entry.TotalBytes > 0) entry.BytesTransferred = entry.TotalBytes;
                        // Keep the final observed speed visible. Very small files can
                        // finish before ioFTPD publishes a WHO sample, so calculate a
                        // useful average for those instead of displaying a dash.
                        var elapsed = Math.Max((DateTime.UtcNow - fxpStartedAt).TotalSeconds, 0.001);
                        if (entry.SpeedBytesPerSecond <= 0 && entry.TotalBytes > 0)
                            entry.SpeedBytesPerSecond = (long)(entry.TotalBytes / elapsed);
                        LogText.AppendText($"{Environment.NewLine}Direct FXP completed via {sourceSession.LastFxpNegotiation}: {entry.Name}");
                        entry.State = "Completed";
                        await RunScriptsAsync("AfterTransfer", TransferScriptVariables(entry, "Completed"), true);
                        return;
                    }
                    catch (FtpCommandException exception) when (exception.StatusCode == 553 && DestinationUsesXdupe(entry))
                    {
                        throw;
                    }
                    catch (Exception fxpException)
                    {
                        LogText.AppendText($"{Environment.NewLine}Direct FXP rejected ({FriendlyMessage(fxpException)}). Reconnecting for client relay…");
                        if (leftWorker is not null) await leftWorker.DisposeAsync();
                        if (rightWorker is not null) await rightWorker.DisposeAsync();
                        leftWorker = await CreateWorkerAsync(_leftProfile!, cancellationToken);
                        rightWorker = await CreateWorkerAsync(_rightProfile!, cancellationToken);
                        sourceSession = entry.Direction == TransferDirection.RelayLeftToRight ? leftWorker : rightWorker;
                        destinationSession = entry.Direction == TransferDirection.RelayLeftToRight ? rightWorker : leftWorker;
                    }
                }
                else LogText.AppendText($"{Environment.NewLine}SSCN is not advertised by both servers; using client relay.");

                var temporary = Path.Combine(Path.GetTempPath(), $"ioftp-fxp-{Guid.NewGuid():N}.part");
                try
                {
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                        await sourceSession.DownloadAsync(entry.Source, file, 0, progress, cancellationToken);
                    await using var fileInput = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                    entry.BytesTransferred = 0;
                    await destinationSession.UploadAsync(entry.Destination, fileInput, 0, progress, cancellationToken);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            entry.State = "Completed";
            LogText.AppendText($"{Environment.NewLine}Transfer completed: {entry.Name}");
            await RunScriptsAsync("AfterTransfer", TransferScriptVariables(entry, "Completed"), true);
        }
        catch (FtpCommandException exception) when (exception.StatusCode == 553 && DestinationUsesXdupe(entry))
        {
            ApplyXdupeReply(entry, exception.Message);
            entry.BytesTransferred = entry.TotalBytes;
            entry.State = "Completed";
            LogText.AppendText($"{Environment.NewLine}XDUPE skipped existing remote file: {entry.Name}");
            await RunScriptsAsync("AfterTransfer", TransferScriptVariables(entry, "XDUPE skipped"), true);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            entry.State = "Paused";
            LogText.AppendText($"{Environment.NewLine}Transfer paused: {entry.Name}");
            throw;
        }
        catch (Exception exception)
        {
            entry.State = "Failed";
            LogText.AppendText($"{Environment.NewLine}Transfer failed ({entry.Name}): {FriendlyMessage(exception)}");
            await RunScriptsAsync("TransferFailed", TransferScriptVariables(entry, FriendlyMessage(exception)), true);
            throw;
        }
        finally
        {
            if (leftWorker is not null) await leftWorker.DisposeAsync();
            if (rightWorker is not null) await rightWorker.DisposeAsync();
            if (apiWorker is not null) await apiWorker.DisposeAsync();
            SaveQueue(); UpdateQueueStatus(); LogText.ScrollToEnd();
        }
    }

    private bool DestinationUsesXdupe(QueueEntryView entry)
    {
        var profile = entry.Direction switch
        {
            TransferDirection.Upload or TransferDirection.RelayLeftToRight => _rightProfile,
            TransferDirection.UploadToLeft or TransferDirection.RelayRightToLeft => _leftProfile,
            _ => null
        };
        return profile?.EffectiveOptions.UseXdupe == true;
    }

    private void ApplyXdupeReply(QueueEntryView current, string response)
    {
        var duplicates = Regex.Matches(response, @"X-DUPE\s*:\s*([^\r\n]+)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Trim().Trim('"'))
            .Where(name => name.Length > 0)
            .Select(name => name.Replace('\\', '/').Split('/').Last())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (duplicates.Count == 0) return;

        foreach (var queued in _queue.Where(item => item.Id != current.Id && item.State == "Queued" &&
                     item.Direction == current.Direction && duplicates.Contains(item.Name)).ToList())
        {
            _engine.Remove(queued.Id);
            queued.BytesTransferred = queued.TotalBytes;
            queued.State = "Completed";
            LogText.AppendText($"{Environment.NewLine}XDUPE skipped queued duplicate: {queued.Name}");
        }
    }

    private Dictionary<string, string> TransferScriptVariables(QueueEntryView entry, string status)
    {
        var sites = SitesFor(entry.Direction); var profiles = new ProfileStore().Load(); var sourceId = sites.Source ?? entry.SourceProfileId;
        return new()
        {
            ["name"] = entry.Name, ["source"] = entry.Source, ["destination"] = entry.Destination, ["path"] = entry.Destination,
            ["status"] = status, ["direction"] = entry.Direction.ToString(),
            ["source_site"] = profiles.FirstOrDefault(profile => profile.Id == sourceId)?.Name ?? "Local",
            ["destination_site"] = profiles.FirstOrDefault(profile => profile.Id == sites.Destination)?.Name ?? "Local"
        };
    }

    private async Task RunScriptsAsync(string eventName, Dictionary<string, string> variables, bool ignoreFailure)
    {
        try
        {
            foreach (var result in await _scriptRunner.RunEventAsync(eventName, variables))
            {
                LogText.AppendText($"{Environment.NewLine}Script [{eventName}] {result.Name}: exit {result.ExitCode}");
                if (!string.IsNullOrWhiteSpace(result.Output)) LogText.AppendText($"{Environment.NewLine}{result.Output.TrimEnd()}");
                if (!string.IsNullOrWhiteSpace(result.Error)) LogText.AppendText($"{Environment.NewLine}{result.Error.TrimEnd()}");
            }
        }
        catch (Exception exception)
        {
            LogText.AppendText($"{Environment.NewLine}External script failed [{eventName}]: {exception.Message}");
            if (!ignoreFailure) throw;
        }
    }

    private static async Task MonitorFxpAsync(ConnectionProfile destinationProfile, QueueEntryView entry, CancellationToken cancellationToken)
    {
        await using var monitor = await CreateWorkerAsync(destinationProfile, cancellationToken);
        long previousBytes = 0;
        var previousAt = DateTime.UtcNow;
        var hasSizeBaseline = false;
        bool? ioGuiExtAvailable = null;
        while (true)
        {
            await Task.Delay(250, cancellationToken);
            // ioFTPD commonly preallocates the complete destination file, so SIZE
            // cannot reveal live FXP progress. ioGuiExt exposes the same transfer
            // counter and speed that ioGUI uses; prefer it when available.
            try
            {
                var activity = await monitor.ExecuteCommandAsync("SITE ioGuiExt who", cancellationToken);
                ioGuiExtAvailable = activity.StatusCode is >= 200 and < 300;
                if (ioGuiExtAvailable == true)
                {
                    if (TryReadIoFtpdTransfer(activity.Message, entry, out var transferred, out var speed))
                    {
                        if (transferred >= 0) entry.BytesTransferred = transferred;
                        // ioFTPD briefly reports zero while changing internal state.
                        // Retain the latest valid sample rather than flashing 0 B/s.
                        if (speed > 0) entry.SpeedBytesPerSecond = speed;
                    }
                    continue;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Not every FTP server has ioGuiExt; fall through to SIZE polling.
            }

            var bytes = await monitor.GetSizeAsync(entry.Destination, cancellationToken);
            if (bytes is null) continue;
            var now = DateTime.UtcNow;
            if (!hasSizeBaseline)
            {
                previousBytes = bytes.Value;
                previousAt = now;
                hasSizeBaseline = true;
                continue;
            }
            var seconds = Math.Max((now - previousAt).TotalSeconds, 0.001);
            var measuredSpeed = Math.Max(0, (long)((bytes.Value - previousBytes) / seconds));
            if (measuredSpeed > 0 || ioGuiExtAvailable == false) entry.SpeedBytesPerSecond = measuredSpeed;
            entry.BytesTransferred = bytes.Value;
            previousBytes = bytes.Value;
            previousAt = now;
        }
    }

    private static bool TryReadIoFtpdTransfer(string response, QueueEntryView entry, out long transferred, out long speed)
    {
        transferred = -1;
        speed = 0;
        var fileName = entry.Name;
        foreach (var line in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var payload = line.Length > 4 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2])
                ? line[4..].Trim()
                : line.Trim();
            if (!payload.StartsWith("cid |", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = payload.Split('|').Select(part => part.Trim()).ToArray();
            if (parts.Length < 19 || parts[16] == "0") continue;
            var identity = $"{parts[10]} {parts[12]} {parts[13]}";
            if (!identity.Contains(fileName, StringComparison.OrdinalIgnoreCase)) continue;

            if (long.TryParse(parts[17], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                transferred = bytes;
            speed = ParseIoFtpdSpeed(parts[18]);
            return true;
        }
        return false;
    }

    private static long ParseIoFtpdSpeed(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var normalized = value.Trim().Replace(',', '.');
        var number = new string(normalized.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return 0;
        var unit = normalized[number.Length..].Trim().ToLowerInvariant();
        var multiplier = unit.StartsWith("g") ? 1024d * 1024 * 1024
            : unit.StartsWith("m") ? 1024d * 1024
            : unit.StartsWith("b") ? 1d
            : 1024d; // ioFTPD TRANSFERSPEED without a suffix is KiB/s.
        return Math.Max(0, (long)(amount * multiplier));
    }

    private static async Task<FtpRemoteSession> CreateWorkerAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        var session = new FtpRemoteSession();
        try
        {
            await session.ConnectAsync(profile, cancellationToken);
            new ProfileStore().PromoteAddress(profile.Id, session.ConnectedHost, session.ConnectedPort);
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    private void PauseTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueEntryView entry) _engine.Pause(entry.Id);
    }

    private async void ResumeTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueEntryView { State: "Paused" or "Failed" } entry)
        {
            if (_engine.Snapshot().Any(status => status.Item.Id == entry.Id)) _engine.Resume(entry.Id);
            else Schedule(entry);
            await Task.CompletedTask;
        }
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _queue.Where(item => item.State is "Completed" or "Failed").ToList()) _queue.Remove(entry);
        SaveQueue(); UpdateQueueStatus();
    }

    private void RemoveQueueJob_Click(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is QueueEntryView entry) RemoveTransferJob(entry.Id);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Remove all queued jobs? Active transfers will be stopped.", "Transfer Queue",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) ClearTransferJobs();
    }

    private void RemoveTransferJob(Guid id)
    {
        _engine.Remove(id);
        var entry = _queue.FirstOrDefault(item => item.Id == id);
        if (entry is not null) _queue.Remove(entry);
        SaveQueue(); UpdateQueueStatus();
    }

    private void ClearTransferJobs()
    {
        _engine.Clear();
        _queue.Clear();
        SaveQueue(); UpdateQueueStatus();
    }

    private void LoadQueue()
    {
        try
        {
            var source = File.Exists(_queuePath) ? _queuePath : File.Exists(_oldQueuePath) ? _oldQueuePath : null;
            if (source is null) return;
            var saved = JsonSerializer.Deserialize<List<QueueSnapshot>>(File.ReadAllText(source)) ?? [];
            foreach (var item in saved)
                _queue.Add(new QueueEntryView(item.Name, item.Source, item.Destination, item.Direction, item.Id == Guid.Empty ? Guid.NewGuid() : item.Id)
                { State = item.State is "Completed" ? "Completed" : "Paused", BytesTransferred = item.BytesTransferred, TotalBytes = item.TotalBytes, SourceProfileId = item.SourceProfileId });
            UpdateQueueStatus();
            if (source == _oldQueuePath) SaveQueue();
        }
        catch (Exception exception) { LogText.AppendText($"{Environment.NewLine}Queue load error: {exception.Message}"); }
    }

    private void SaveQueue()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_queuePath)!);
            var snapshots = _queue.Select(item => new QueueSnapshot(item.Name, item.Source, item.Destination, item.Direction, item.State, item.BytesTransferred, item.TotalBytes, item.Id, item.SourceProfileId));
            var temporary = _queuePath + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _queuePath, true);
        }
        catch (IOException) { }
    }

    private void UpdateQueueStatus() => QueueStatus.Text = $"{_queue.Count(entry => entry.State is "Queued" or "Transferring")} queued";

    private void Engine_StateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var status in _engine.Snapshot())
            {
                var entry = _queue.FirstOrDefault(item => item.Id == status.Item.Id); if (entry is null) continue;
                entry.State = status.State switch
                {
                    TransferWorkState.Queued => "Queued", TransferWorkState.Running => "Transferring",
                    TransferWorkState.Paused => "Paused", TransferWorkState.Completed => "Completed", _ => "Failed"
                };
            }
            SaveQueue(); UpdateQueueStatus();
        });
    }

    private async Task ExecuteEngineItemAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = _queue.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Transfer job no longer exists.");
        await ExecuteScheduledAsync(entry, cancellationToken);
    }

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        OperationCanceledException => "The operation timed out. If login succeeded, check the server's passive port range and firewall.",
        NotSupportedException => exception.Message,
        IoFtp.Core.Transport.FtpCommandException ftpException => $"FTP server replied {ftpException.StatusCode}: {ftpException.Message}",
        System.Net.WebException webException when webException.Response is System.Net.FtpWebResponse response => $"FTP server replied {(int)response.StatusCode}: {response.StatusDescription?.Trim() ?? "Unknown error"}",
        _ => exception.Message
    };

    private static string DescribeListingMode(DirectoryListingMode mode, IReadOnlySet<string> capabilities) => mode switch
    {
        DirectoryListingMode.StatThenList when capabilities.Contains("STAT") => "STAT -l (LIST fallback)",
        DirectoryListingMode.StatThenList => "LIST (STAT not advertised)",
        DirectoryListingMode.StatOnly => "STAT -l",
        _ => "LIST"
    };

    protected override async void OnClosed(EventArgs e)
    {
        SaveWindowLayout();
        _legendTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        if (_apiServer is not null) await _apiServer.DisposeAsync();
        await _engine.DisposeAsync();
        if (_remoteSession is not null) await _remoteSession.DisposeAsync();
        if (_leftRemoteSession is not null) await _leftRemoteSession.DisposeAsync();
        base.OnClosed(e);
    }

    private void RestoreWindowLayout()
    {
        var layout = _layoutStore.Load();
        if (layout is null) return;
        Width = Math.Max(MinWidth, layout.Width); Height = Math.Max(MinHeight, layout.Height);
        if (layout.Left >= SystemParameters.VirtualScreenLeft && layout.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            layout.Top >= SystemParameters.VirtualScreenTop && layout.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        { Left = layout.Left; Top = layout.Top; }
        if (layout.LeftPaneWidth > 100) LeftPaneColumn.Width = new GridLength(layout.LeftPaneWidth);
        _visibleQueueHeight = new GridLength(Math.Max(80, layout.QueueHeight));
        _visibleLogHeight = new GridLength(Math.Max(70, layout.LogHeight));
        if (layout.QueueVisible && QueueRow.Height.Value == 0) ToggleQueue_Click(this, new RoutedEventArgs());
        if (!layout.LogVisible && LogRow.Height.Value > 0) ToggleLog_Click(this, new RoutedEventArgs());
        if (layout.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _layoutStore.Save(new WindowLayout(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized, LeftPaneColumn.ActualWidth,
            QueueRow.Height.Value > 0, QueueRow.Height.Value > 0 ? QueueRow.ActualHeight : _visibleQueueHeight.Value,
            LogRow.Height.Value > 0, LogRow.Height.Value > 0 ? LogRow.ActualHeight : _visibleLogHeight.Value));
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogRow.Height.Value > 0)
        {
            _visibleLogHeight = LogRow.Height;
            LogRow.MinHeight = 0;
            LogRow.Height = new GridLength(0);
            LogSplitterRow.Height = new GridLength(0);
            LogPanel.Visibility = Visibility.Collapsed;
            LogSplitter.Visibility = Visibility.Collapsed;
            ToggleLogButton.Content = "Show Log";
        }
        else
        {
            LogPanel.Visibility = Visibility.Visible;
            LogSplitter.Visibility = Visibility.Visible;
            LogSplitterRow.Height = new GridLength(6);
            LogRow.MinHeight = 70;
            LogRow.Height = _visibleLogHeight.Value > 0 ? _visibleLogHeight : new GridLength(150);
            ToggleLogButton.Content = "Hide Log";
        }
    }

    private void ToggleQueue_Click(object sender, RoutedEventArgs e)
    {
        if (QueueRow.Height.Value > 0)
        {
            _visibleQueueHeight = QueueRow.Height;
            QueueRow.MinHeight = 0;
            QueueRow.Height = new GridLength(0);
            QueueSplitterRow.Height = new GridLength(0);
            QueuePanel.Visibility = Visibility.Collapsed;
            QueueSplitter.Visibility = Visibility.Collapsed;
            ToggleQueueButton.Content = "Show Queue";
        }
        else
        {
            QueuePanel.Visibility = Visibility.Visible;
            QueueSplitter.Visibility = Visibility.Visible;
            QueueSplitterRow.Height = new GridLength(6);
            QueueRow.MinHeight = 80;
            QueueRow.Height = _visibleQueueHeight.Value > 0 ? _visibleQueueHeight : new GridLength(190);
            ToggleQueueButton.Content = "Hide Queue";
        }
    }

    private void TransferJobs_Click(object sender, RoutedEventArgs e)
    {
        var window = new TransferJobsWindow(GetTransferJobs, RemoveTransferJob, ClearTransferJobs) { Owner = this };
        window.Show();
    }

    private void Metrics_Click(object sender, RoutedEventArgs e) => new MetricsWindow(GetMetricsSnapshot) { Owner = this }.Show();
    private void Scripts_Click(object sender, RoutedEventArgs e) => new ExternalScriptsWindow { Owner = this }.Show();
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private MetricsSnapshot GetMetricsSnapshot()
    {
        var profiles = new ProfileStore().Load();
        var connected = (_leftRemoteSession?.IsConnected == true ? 1 : 0) + (_remoteSession?.IsConnected == true ? 1 : 0);
        var active = _queue.Count(item => item.State is "Queued" or "Transferring");
        var running = _queue.Where(item => item.State == "Transferring").ToList();
        var speed = running.Sum(item => item.SpeedBytesPerSecond); var bytes = _queue.Sum(item => item.BytesTransferred);
        var fxp = _queue.Where(item => item.Direction.ToString().StartsWith("Relay", StringComparison.Ordinal)).ToList();
        var rows = new List<MetricRow>
        {
            new("Configured sites", profiles.Count.ToString(), $"Login slots: {profiles.Sum(site => site.EffectiveOptions.MaxSlots)}"),
            new("Connected panes", connected.ToString(), $"Left: {(_leftRemoteSession?.IsConnected == true ? "online" : "offline")}   Right: {(_remoteSession?.IsConnected == true ? "online" : "offline")}"),
            new("Transfer jobs", _queue.Count.ToString(), $"Queued/active: {active}"),
            new("Completed", _queue.Count(item => item.State == "Completed").ToString(), $"Failed: {_queue.Count(item => item.State == "Failed")}"),
            new("FXP jobs", fxp.Count.ToString(), $"Completed: {fxp.Count(item => item.State == "Completed")}   Failed: {fxp.Count(item => item.State == "Failed")}"),
            new("Current throughput", $"{FormatSize(speed)}/s", $"Running transfers: {running.Count}"),
            new("Transferred this queue", FormatSize(bytes), $"Remaining: {FormatSize(_queue.Sum(item => Math.Max(0, item.TotalBytes - item.BytesTransferred)))}"),
            new("API", _settings.EnableHttpsApi ? "Enabled" : "Disabled", _settings.EnableHttpsApi ? $"HTTPS port {_settings.HttpsApiPort}" : "—")
        };
        return new(connected, profiles.Count, active, $"{FormatSize(speed)}/s", FormatSize(bytes), rows);
    }

    private void UpdateLegendBar()
    {
        var mode = _settings.LegendBarMode;
        LegendBar.Visibility = mode.Equals("Hidden", StringComparison.OrdinalIgnoreCase) ? Visibility.Collapsed : Visibility.Visible;
        if (LegendBar.Visibility != Visibility.Visible) return;
        var snapshot = GetMetricsSnapshot();
        var compact = $"Sites {snapshot.ConnectedSites}/{snapshot.ConfiguredSites}   Jobs {snapshot.ActiveJobs}/{_queue.Count}   Speed {snapshot.TotalSpeed}   Transferred {snapshot.Transferred}";
        LegendText.Text = mode switch
        {
            "Static" => "▲ upload   ▼ download   ● idle   ■ queued   ✓ completed   ✕ failed",
            "Activity" => ConnectionStatus.Text,
            "Scrolling" => ScrollLegend(compact),
            _ => compact
        };
    }

    private string ScrollLegend(string text)
    {
        var padded = new string(' ', 60) + text + new string(' ', 60); if (padded.Length == 0) return "";
        _legendOffset %= padded.Length; var result = padded[_legendOffset..] + padded[.._legendOffset]; _legendOffset++; return result;
    }

    private void Sections_Click(object sender, RoutedEventArgs e)
    {
        new SectionsWindow { Owner = this }.Show();
    }

    private void SpreadJobs_Click(object sender, RoutedEventArgs e)
    {
        new SpreadJobsWindow { Owner = this }.Show();
    }

    private async void GlobalSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GlobalSettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Settings is not null)
        {
            _settings = dialog.Settings; _settingsStore.Save(_settings);
            ConfigureTrayIcon();
            UpdateLegendBar();
            _engine.ConfigureLocalSlots(_settings.MaxLocalDownloadSlots, _settings.MaxLocalUploadSlots);
            LogText.AppendText($"{Environment.NewLine}Global settings updated.");
            await RestartApiServerAsync();
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var result = await _updateCheckService.CheckAsync();
        var status = result.Error is not null && string.IsNullOrEmpty(result.LatestVersion)
            ? $"Update status: Could not check ({result.Error})."
            : result.UpdateAvailable
                ? $"Update available: FluxFTP {result.LatestVersion} — {result.ReleaseUrl}"
                : $"Update status: Latest version ({result.CurrentVersion}).";
        LogText.AppendText($"{Environment.NewLine}{status}");
        LogText.ScrollToEnd();
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon.Text = "FluxFTP";
        _trayIcon.Icon ??= System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _trayIcon.Visible = _settings.MinimizeToTray;
        if (_trayIcon.ContextMenuStrip is null)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open FluxFTP", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized || !_settings.MinimizeToTray) return;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task RestartApiServerAsync()
    {
        if (_apiServer is not null) { await _apiServer.DisposeAsync(); _apiServer = null; }
        if (!_settings.EnableHttpsApi) return;
        try
        {
            _apiServer = new ApiServer();
            await _apiServer.StartAsync(_settings, () => Dispatcher.Invoke(GetTransferJobs), StartApiTransferAsync, StartApiDownloadAsync,
                id => Dispatcher.Invoke(() => RemoveTransferJob(id)), id => Dispatcher.Invoke(() => ResetTransferJob(id)));
            LogText.AppendText($"{Environment.NewLine}HTTPS/JSON API listening on https://{(_settings.ApiLocalhostOnly ? "localhost" : "0.0.0.0")}:{_settings.HttpsApiPort}");
        }
        catch (Exception exception)
        {
            if (_apiServer is not null) await _apiServer.DisposeAsync(); _apiServer = null;
            LogText.AppendText($"{Environment.NewLine}API failed to start: {exception.Message}");
        }
        LogText.ScrollToEnd();
    }

    private async Task<object> StartApiTransferAsync(ApiTransferRequest request) => await await Dispatcher.InvokeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(request.SrcSite) || string.IsNullOrWhiteSpace(request.DstSite) || string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("src_site, dst_site and name are required for FXP jobs.");
        var leftToRight = _leftProfile?.Name.Equals(request.SrcSite, StringComparison.OrdinalIgnoreCase) == true && _rightProfile?.Name.Equals(request.DstSite, StringComparison.OrdinalIgnoreCase) == true;
        var rightToLeft = _rightProfile?.Name.Equals(request.SrcSite, StringComparison.OrdinalIgnoreCase) == true && _leftProfile?.Name.Equals(request.DstSite, StringComparison.OrdinalIgnoreCase) == true;
        if (!leftToRight && !rightToLeft) throw new InvalidOperationException("Both API FXP sites must currently be connected in the two Remote panels.");
        var sourceSession = leftToRight ? _leftRemoteSession! : _remoteSession!; var destinationSession = leftToRight ? _remoteSession! : _leftRemoteSession!;
        var sourceBase = NormalizeRemotePath(request.SrcSection is not null ? ResolveApiSection(request.SrcSite, request.SrcSection) : request.SrcPath ?? "/");
        var destinationBase = NormalizeRemotePath(request.DstSection is not null ? ResolveApiSection(request.DstSite, request.DstSection) : request.DstPath ?? "/");
        var source = NormalizeRemotePath($"{sourceBase}/{request.Name}"); var destination = NormalizeRemotePath($"{destinationBase}/{request.Name}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var item = (await sourceSession.ListAsync(sourceBase, timeout.Token)).FirstOrDefault(entry => entry.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"{request.Name} was not found on {request.SrcSite}.");
        var direction = leftToRight ? TransferDirection.RelayLeftToRight : TransferDirection.RelayRightToLeft;
        if (item.IsDirectory) await QueueRemoteDirectoryAsync(sourceSession, destinationSession, source, destination, direction);
        else Schedule(AddQueue(item.Name, item.FullPath, destination, direction, item.Size ?? 0));
        return new { name = request.Name, status = "QUEUED" };
    });

    private async Task<object> StartApiDownloadAsync(ApiDownloadRequest request) => await await Dispatcher.InvokeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(request.Site)) throw new ArgumentException("site is required.");
        var profile = new ProfileStore().Load().FirstOrDefault(item => item.Name.Equals(request.Site, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Site {request.Site} was not found.");
        var localRoot = string.IsNullOrWhiteSpace(request.LocalPath) ? _settings.LocalDownloadPath : request.LocalPath;
        if (string.IsNullOrWhiteSpace(localRoot)) throw new ArgumentException("local_path is required when no global local download path is configured.");
        localRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(localRoot)); Directory.CreateDirectory(localRoot);
        var remote = NormalizeRemotePath(request.RemoteSection is not null ? ResolveApiSection(request.Site, request.RemoteSection) : request.RemotePath ?? "/");
        var options = profile.EffectiveOptions;
        _engine.RegisterOrUpdateSite(new SitePolicy(profile.Id, profile.Name, options.MaxSlots, options.MaxDownloadSlots, options.MaxUploadSlots, options.Priority));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); await using var session = new FtpRemoteSession(); await session.ConnectAsync(ApplyGlobalProxy(profile), timeout.Token);
        var queued = 0;
        async Task QueueDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (var child in await session.ListAsync(sourceDirectory, timeout.Token))
            {
                if (child.Name is "." or ".." || ShouldSkip(child.Name)) continue;
                var destination = Path.Combine(destinationDirectory, child.Name);
                if (child.IsDirectory) { if (request.Recursive) await QueueDirectory(child.FullPath, destination); }
                else { Schedule(AddQueue(child.Name, child.FullPath, destination, TransferDirection.ApiDownload, child.Size ?? 0, profile.Id)); queued++; }
            }
        }
        var parent = RemoteParent(remote); var name = Path.GetFileName(remote.TrimEnd('/'));
        var selected = remote == "/" ? null : (await session.ListAsync(parent, timeout.Token)).FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (remote == "/") await QueueDirectory(remote, localRoot);
        else if (selected is null) throw new FileNotFoundException($"{remote} was not found on {request.Site}.");
        else if (selected.IsDirectory) await QueueDirectory(selected.FullPath, Path.Combine(localRoot, selected.Name));
        else { Schedule(AddQueue(selected.Name, selected.FullPath, Path.Combine(localRoot, selected.Name), TransferDirection.ApiDownload, selected.Size ?? 0, profile.Id)); queued++; }
        LogText.AppendText($"{Environment.NewLine}API queued {queued} download(s) from {request.Site}: {remote}"); LogText.ScrollToEnd();
        return new { site = request.Site, remote_path = remote, local_path = localRoot, queued, status = "QUEUED" };
    });

    private static string ResolveApiSection(string site, string sectionName)
    {
        var section = new SectionStore().Load().FirstOrDefault(item => item.Name.Equals(sectionName, StringComparison.OrdinalIgnoreCase));
        return section?.SitePaths.FirstOrDefault(pair => pair.Key.Equals(site, StringComparison.OrdinalIgnoreCase)).Value
            ?? throw new KeyNotFoundException($"Section {sectionName} is not configured for {site}.");
    }

    private void ResetTransferJob(Guid id)
    {
        var entry = _queue.FirstOrDefault(item => item.Id == id); if (entry is null) return;
        entry.BytesTransferred = 0; entry.State = "Paused";
        if (_engine.Snapshot().Any(status => status.Item.Id == id)) _engine.Resume(id); else Schedule(entry);
    }

    private IReadOnlyList<TransferJobInfo> GetTransferJobs() => _queue.Select(entry =>
    {
        var remoteToRemote = entry.Direction is TransferDirection.RelayLeftToRight or TransferDirection.RelayRightToLeft;
        var direction = entry.Direction switch
        {
            TransferDirection.Download or TransferDirection.DownloadFromLeft or TransferDirection.ApiDownload => "R→L",
            TransferDirection.Upload or TransferDirection.UploadToLeft => "L→R",
            TransferDirection.RelayLeftToRight => "R1→R2",
            _ => "R2→R1"
        };
        var done = entry.State == "Completed" ? "100%" : entry.TotalBytes > 0 ? $"{Math.Min(100, entry.BytesTransferred * 100 / entry.TotalBytes)}%" : entry.State == "Transferring" ? "RUN" : entry.State.ToUpperInvariant();
        return new TransferJobInfo(entry.Id, "—", entry.State == "Transferring" ? "now" : "—", direction,
            remoteToRemote ? "FXP" : "FTP", entry.Name, $"{entry.Source} → {entry.Destination}",
            entry.TotalBytes > 0 ? FormatSize(entry.TotalBytes) : entry.ProgressText, "1",
            entry.TotalBytes > 0 ? FormatSize(Math.Max(0, entry.TotalBytes - entry.BytesTransferred)) : "—",
            entry.SpeedBytesPerSecond > 0 ? $"{FormatSize(entry.SpeedBytesPerSecond)}/s" : "—", done, entry.State);
    }).ToList();

    private sealed record LocalEntryView(string Name, string Size, string Modified, string Attributes, string FullPath, bool IsDirectory);
    private sealed record RemoteEntryView(string Name, string DisplaySize, string DisplayModified, string Attributes, string FullPath, bool IsDirectory);

    private enum TransferDirection { Download, Upload, UploadToLeft, DownloadFromLeft, RelayLeftToRight, RelayRightToLeft, ApiDownload }
    private sealed record QuickSiteChoice(string Label, ConnectionProfile? Profile)
    {
        public override string ToString() => Label;
    }
    private sealed record QueueSnapshot(string Name, string Source, string Destination, TransferDirection Direction, string State, long BytesTransferred, long TotalBytes = 0, Guid Id = default, Guid? SourceProfileId = null);

    private sealed class QueueEntryView(string name, string source, string destination, TransferDirection direction, Guid? id = null, long totalBytes = 0) : INotifyPropertyChanged
    {
        private string _state = "Queued"; private long _bytesTransferred;
        public string Name { get; } = name; public string Source { get; } = source; public string Destination { get; } = destination;
        public TransferDirection Direction { get; } = direction;
        public Guid Id { get; } = id ?? Guid.NewGuid();
        public long TotalBytes { get; set; } = totalBytes;
        private long _speedBytesPerSecond;
        public long SpeedBytesPerSecond { get => _speedBytesPerSecond; set { _speedBytesPerSecond = value; Changed(); } }
        public Guid? SourceProfileId { get; set; }
        public DateTime LastPersistedAt { get; set; }
        public string State { get => _state; set { _state = value; Changed(); } }
        public long BytesTransferred { get => _bytesTransferred; set { _bytesTransferred = value; Changed(); Changed(nameof(ProgressText)); } }
        public string ProgressText => FormatSize(BytesTransferred);
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private sealed class DesktopTransferExecutor(MainWindow owner) : ITransferExecutor
    {
        public async Task ExecuteAsync(TransferWorkItem item, CancellationToken cancellationToken) =>
            await await owner.Dispatcher.InvokeAsync(() => owner.ExecuteEngineItemAsync(item.Id, cancellationToken));
    }
}
