using System.IO;
using System.Diagnostics;
using System.Collections.Concurrent;
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
    private bool _reloadingBookmarks;
    private bool _reloadingDrives;
    private readonly SemaphoreSlim _leftNavigationGate = new(1, 1);
    private readonly SemaphoreSlim _rightNavigationGate = new(1, 1);
    private readonly System.Windows.Forms.NotifyIcon _trayIcon = new();
    private readonly System.Windows.Threading.DispatcherTimer _legendTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ExternalScriptRunner _scriptRunner = new();
    private readonly UpdateCheckService _updateCheckService = new();
    private readonly FxpRouteStore _fxpRouteStore = new();
    private readonly HashSet<(Guid Source, Guid Destination)> _reverseFxpPairs;
    private readonly ConcurrentDictionary<ConnectionProfile, ConcurrentBag<PooledWorker>> _workerPool = new();
    private static readonly TimeSpan WorkerHealthCheckInterval = TimeSpan.FromSeconds(30);
    private bool _exitRequested;
    private bool _workerPoolShuttingDown;
    private int _legendOffset;
    public MainWindow()
    {
        InitializeComponent();
        _reverseFxpPairs = _fxpRouteStore.LoadReverseRoutes();
        _settings = _settingsStore.Load();
        LogText.Text = $"FluxFTP {UpdateCheckService.CurrentVersion} started.{Environment.NewLine}No network connections have been opened.";
        _engine = new GlobalTransferEngine(new DesktopTransferExecutor(this));
        _engine.ConfigureLocalSlots(_settings.MaxLocalDownloadSlots, _settings.MaxLocalUploadSlots);
        _engine.StateChanged += Engine_StateChanged;
        QueueList.ItemsSource = _queue;
        ReloadQuickSites(LeftQuickSites);
        ReloadQuickSites(RightQuickSites);
        ReloadLocalDrives();
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
            var fullDirectory = NormalizeLocalDirectory(directory);
            LocalList.ItemsSource = Directory.EnumerateFileSystemEntries(fullDirectory)
                .Select(path =>
                {
                    var isDirectory = Directory.Exists(path);
                    var modified = isDirectory
                        ? Directory.GetLastWriteTime(path)
                        : File.GetLastWriteTime(path);
                    var size = isDirectory ? "Folder" : FormatSize(new FileInfo(path).Length);
                    return new LocalEntryView(Path.GetFileName(path), size, modified.ToString("yyyy-MM-dd HH:mm"), File.GetAttributes(path).ToString(), "", false, path, isDirectory);
                })
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _localDirectory = fullDirectory;
            LocalPath.Text = fullDirectory;
            SelectCurrentDrive(LeftDrives, fullDirectory);
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
        LogText.AppendText($"{Environment.NewLine}Connecting with {TransferProtocolNames.Display(profile.Protocol)} to {profile.Host}:{profile.Port}…");
        try
        {
            await DisposePooledWorkersAsync(profile.Id);
            var session = left ? _leftRemoteSession : _remoteSession;
            if (session is not null) await session.DisposeAsync();
            session = new FtpRemoteSession();
            AttachProtocolLog(session, profile.Name);
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
            using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                try { await session.ConnectAsync(profile, connectTimeout.Token); }
                catch (SshHostKeyException hostKey)
                {
                    var trust = MessageBox.Show(
                        $"The SFTP server presented this SSH host key:\n\n{hostKey.Fingerprint}\n\nTrust and save this key for {profile.Name}?",
                        "Trust SFTP host key", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (trust != MessageBoxResult.Yes) throw;
                    profile = profile with { SshHostKeyFingerprint = hostKey.Fingerprint };
                    SaveTrustedHostKey(profile);
                    await session.ConnectAsync(profile, connectTimeout.Token);
                    if (left) _leftProfile = profile; else _rightProfile = profile;
                }
            }
            new ProfileStore().PromoteAddress(profile.Id, session.ConnectedHost, session.ConnectedPort);
            ConnectionStatus.Text = $"Connected: {session.ConnectedHost}:{session.ConnectedPort}; loading files…";
            LogText.AppendText(profile.Protocol == TransferProtocol.Sftp
                ? $"{Environment.NewLine}SFTP login succeeded. Loading directory…"
                : $"{Environment.NewLine}TLS login succeeded. Loading directory with {DescribeListingMode(profile.ListingMode, session.Capabilities)}…");
            LogText.ScrollToEnd();
            IReadOnlyList<IoFtp.Core.Abstractions.RemoteEntry> entries;
            using (var listTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                entries = await session.ListAsync(options.BasePath, listTimeout.Token);
            if (left) ShowLeftRemoteEntries(options.BasePath, entries); else ShowRemoteEntries(options.BasePath, entries);
            ReloadBookmarks(left);
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

    private static void SaveTrustedHostKey(ConnectionProfile trustedProfile)
    {
        var store = new ProfileStore();
        var profiles = store.Load().ToList();
        var index = profiles.FindIndex(profile => profile.Id == trustedProfile.Id);
        if (index < 0) return;
        profiles[index] = trustedProfile;
        store.Save(profiles);
    }

    private void AttachProtocolLog(FtpRemoteSession session, string siteName)
    {
        session.ProtocolMessage += message => Dispatcher.BeginInvoke(() =>
        {
            LogText.AppendText($"{Environment.NewLine}{DateTime.Now:HH:mm:ss} [{siteName}] {message}");
            LogText.ScrollToEnd();
        });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogText.Text)) Clipboard.SetText(LogText.Text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) =>
        LogText.Text = $"FluxFTP {UpdateCheckService.CurrentVersion} log cleared at {DateTime.Now:yyyy-MM-dd HH:mm:ss}.";

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
        if (_leftProfile is not null) await DisposePooledWorkersAsync(_leftProfile.Id);
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
        if (_rightProfile is not null) await DisposePooledWorkersAsync(_rightProfile.Id);
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
        LeftDrives.Visibility = LeftMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (LeftMode.SelectedIndex == 0) { LeftSiteTitle.Text = "LOCAL"; ReloadLocalDrives(); LoadLocalDirectory(_localDirectory); }
        else
        {
            LeftSiteTitle.Text = _leftProfile is null ? "REMOTE SITE" : $"REMOTE — {_leftProfile.Name.ToUpperInvariant()}";
            LocalPath.Text = _leftRemoteDirectory;
            if (_leftRemoteSession?.IsConnected == true) _ = NavigateLeftRemoteAsync(_leftRemoteDirectory);
            else LocalList.ItemsSource = null;
        }
        ReloadBookmarks(true);
    }

    private void RightMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RightDrives.Visibility = RightMode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RightMode.SelectedIndex == 0) { RemoteSiteTitle.Text = "LOCAL"; ReloadLocalDrives(); LoadRightLocalDirectory(_rightLocalDirectory); }
        else
        {
            RemoteSiteTitle.Text = _rightProfile is null ? "REMOTE SITE" : $"REMOTE — {_rightProfile.Name.ToUpperInvariant()}";
            RemotePath.Text = _remoteDirectory;
            if (_remoteSession?.IsConnected == true) _ = NavigateRemoteAsync(_remoteDirectory);
            else RemoteList.ItemsSource = null;
        }
        ReloadBookmarks(false);
    }

    private void ReloadBookmarks(bool left)
    {
        if (!IsLoaded) return;
        _reloadingBookmarks = true;
        var remoteMode = left ? LeftMode.SelectedIndex == 1 : RightMode.SelectedIndex == 1;
        var siteName = left ? _leftProfile?.Name : _rightProfile?.Name;
        var choices = new List<SiteBookmark> { new("Bookmarks…", "") };
        choices.AddRange(new BookmarkStore().Load().Where(bookmark => remoteMode
            ? !string.IsNullOrWhiteSpace(siteName) && bookmark.SiteName.Equals(siteName, StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrWhiteSpace(bookmark.SiteName)));
        var combo = left ? LeftBookmarks : RightBookmarks;
        combo.ItemsSource = choices;
        combo.SelectedIndex = 0;
        _reloadingBookmarks = false;
    }

    private async void LeftBookmarks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reloadingBookmarks || LeftBookmarks.SelectedItem is not SiteBookmark { Path.Length: > 0 } bookmark) return;
        if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(LocalBookmarkPath(bookmark.Path));
        else await NavigateLeftRemoteAsync(bookmark.Path);
    }

    private async void RightBookmarks_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reloadingBookmarks || RightBookmarks.SelectedItem is not SiteBookmark { Path.Length: > 0 } bookmark) return;
        if (RightMode.SelectedIndex == 0) LoadRightLocalDirectory(LocalBookmarkPath(bookmark.Path));
        else await NavigateRemoteAsync(bookmark.Path);
    }

    private static string LocalBookmarkPath(string path) =>
        Regex.IsMatch(path, @"^/[A-Za-z]:/") ? path[1..].Replace('/', Path.DirectorySeparatorChar) : path.Replace('/', Path.DirectorySeparatorChar);

    private void ShowLeftRemoteEntries(string path, IReadOnlyList<RemoteEntry> entries)
    {
        _leftRemoteDirectory = NormalizeRemotePath(path); LocalPath.Text = _leftRemoteDirectory;
        LocalList.ItemsSource = entries.Select(entry => new LocalEntryView(entry.Name,
            entry.IsDirectory ? "Folder" : entry.Size is { } size ? FormatSize(size) : "—",
            entry.ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—", entry.Attributes,
            NukeDetector.DetectName(entry.Name).Display, NukeDetector.DetectName(entry.Name).IsNuked, entry.FullPath, entry.IsDirectory))
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
        var width = Math.Max(120, e.NewSize.Width - 90 - 150 - 110 - 150 - 24);
        if (ReferenceEquals(sender, LocalList)) LeftNameColumn.Width = width;
        else if (ReferenceEquals(sender, RemoteList)) RightNameColumn.Width = width;
    }

    private void QueueList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = Math.Max(620, e.NewSize.Width - 28);
        QueueStateColumn.Width = 82;
        QueueProgressColumn.Width = Math.Clamp(available * 0.20, 170, 220);
        var paths = Math.Max(420, available - QueueStateColumn.Width - QueueProgressColumn.Width);
        QueueNameColumn.Width = Math.Max(140, paths * 0.30);
        QueueSourceColumn.Width = Math.Max(140, paths * 0.35);
        QueueDestinationColumn.Width = Math.Max(140, paths * 0.35);
    }

    private void LegendBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1050;
        var veryCompact = e.NewSize.Width < 850;
        var scrolling = _settings.LegendBarMode.Equals("Scrolling", StringComparison.OrdinalIgnoreCase);

        ElapsedColumn.Width = compact ? new GridLength(0) : new GridLength(125);
        ElapsedText.Visibility = scrolling || compact ? Visibility.Collapsed : Visibility.Visible;
        ElapsedSeparator.Visibility = scrolling || compact ? Visibility.Collapsed : Visibility.Visible;
        QueueTimeColumn.Width = veryCompact ? new GridLength(0) : new GridLength(compact ? 92 : 115);
        QueueTimeText.Visibility = scrolling || veryCompact ? Visibility.Collapsed : Visibility.Visible;
        QueueTimeSeparator.Visibility = scrolling || veryCompact ? Visibility.Collapsed : Visibility.Visible;
        TransferBytesColumn.Width = new GridLength(compact ? 160 : 205);
        StatusProgressColumn.Width = new GridLength(compact ? 120 : 150);
        RemainingColumn.Width = new GridLength(compact ? 120 : 145);
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
            var full = NormalizeLocalDirectory(directory);
            RemoteList.ItemsSource = Directory.EnumerateFileSystemEntries(full).Select(path =>
            {
                var folder = Directory.Exists(path); var modified = folder ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                return new RemoteEntryView(Path.GetFileName(path), folder ? "Folder" : FormatSize(new FileInfo(path).Length), modified.ToString("yyyy-MM-dd HH:mm"), File.GetAttributes(path).ToString(), "", false, path, folder);
            }).OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _rightLocalDirectory = full; RemotePath.Text = full; SelectCurrentDrive(RightDrives, full);
        }
        catch (Exception exception) { LogText.AppendText($"{Environment.NewLine}Local browse error: {exception.Message}"); }
    }

    private static string NormalizeLocalDirectory(string directory)
    {
        var value = Environment.ExpandEnvironmentVariables(directory.Trim());
        if (Regex.IsMatch(value, @"^[A-Za-z]:$")) value += Path.DirectorySeparatorChar;
        return Path.GetFullPath(value);
    }

    private void ReloadLocalDrives()
    {
        _reloadingDrives = true;
        try
        {
            var drives = DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName).ToList();
            LeftDrives.ItemsSource = drives;
            RightDrives.ItemsSource = drives;
            SelectCurrentDrive(LeftDrives, _localDirectory);
            SelectCurrentDrive(RightDrives, _rightLocalDirectory);
        }
        finally { _reloadingDrives = false; }
    }

    private static void SelectCurrentDrive(ComboBox combo, string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(root)) combo.SelectedItem = root;
    }

    private void LeftDrives_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingDrives && LeftMode.SelectedIndex == 0 && LeftDrives.SelectedItem is string drive)
            LoadLocalDirectory(drive);
    }

    private void RightDrives_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_reloadingDrives && RightMode.SelectedIndex == 0 && RightDrives.SelectedItem is string drive)
            LoadRightLocalDirectory(drive);
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
            NukeDetector.DetectName(entry.Name).Display,
            NukeDetector.DetectName(entry.Name).IsNuked,
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
        var entries = RemoteList.SelectedItems.Cast<RemoteEntryView>().ToList();
        if (entries.Count == 0) return;
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                if (RightMode.SelectedIndex == 1 && LeftMode.SelectedIndex == 1 && _remoteSession is not null && _leftRemoteSession is not null)
                    await QueueRemoteDirectoryAsync(_remoteSession, _leftRemoteSession, entry.FullPath,
                        NormalizeRemotePath($"{_leftRemoteDirectory}/{entry.Name}"), TransferDirection.RelayRightToLeft);
                else if (RightMode.SelectedIndex == 1 && LeftMode.SelectedIndex == 0 && _remoteSession is not null)
                    await QueueRemoteToLocalDirectoryAsync(_remoteSession, entry.FullPath,
                        Path.Combine(_localDirectory, entry.Name), TransferDirection.Download);
                else if (RightMode.SelectedIndex == 0 && LeftMode.SelectedIndex == 1 && _leftRemoteSession is not null)
                    await QueueLocalDirectoryAsync(entry.FullPath, _leftRemoteSession,
                        NormalizeRemotePath($"{_leftRemoteDirectory}/{entry.Name}"), TransferDirection.UploadToLeft);
                continue;
            }
            QueueEntryView queueEntry;
            if (RightMode.SelectedIndex == 1 && LeftMode.SelectedIndex == 0)
            {
                var destination = Path.Combine(_localDirectory, entry.Name);
                if (File.Exists(destination) && MessageBox.Show($"Replace '{entry.Name}'?", "Transfer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) continue;
                queueEntry = AddQueue(entry.Name, entry.FullPath, destination, TransferDirection.Download);
            }
            else if (RightMode.SelectedIndex == 0 && LeftMode.SelectedIndex == 1)
                queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_leftRemoteDirectory}/{entry.Name}"), TransferDirection.UploadToLeft);
            else if (RightMode.SelectedIndex == 1 && LeftMode.SelectedIndex == 1)
                queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_leftRemoteDirectory}/{entry.Name}"), TransferDirection.RelayRightToLeft);
            else continue;
            Schedule(queueEntry);
        }
        if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(_localDirectory); else await NavigateLeftRemoteAsync(_leftRemoteDirectory);
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var entries = LocalList.SelectedItems.Cast<LocalEntryView>().ToList();
        if (entries.Count == 0) return;
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                if (LeftMode.SelectedIndex == 1 && RightMode.SelectedIndex == 1 && _leftRemoteSession is not null && _remoteSession is not null)
                    await QueueRemoteDirectoryAsync(_leftRemoteSession, _remoteSession, entry.FullPath,
                        NormalizeRemotePath($"{_remoteDirectory}/{entry.Name}"), TransferDirection.RelayLeftToRight);
                else if (LeftMode.SelectedIndex == 1 && RightMode.SelectedIndex == 0 && _leftRemoteSession is not null)
                    await QueueRemoteToLocalDirectoryAsync(_leftRemoteSession, entry.FullPath,
                        Path.Combine(_rightLocalDirectory, entry.Name), TransferDirection.DownloadFromLeft);
                else if (LeftMode.SelectedIndex == 0 && RightMode.SelectedIndex == 1 && _remoteSession is not null)
                    await QueueLocalDirectoryAsync(entry.FullPath, _remoteSession,
                        NormalizeRemotePath($"{_remoteDirectory}/{entry.Name}"), TransferDirection.Upload);
                continue;
            }
            QueueEntryView queueEntry;
            var size = File.Exists(entry.FullPath) ? new FileInfo(entry.FullPath).Length : 0;
            if (LeftMode.SelectedIndex == 0 && RightMode.SelectedIndex == 1)
                queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_remoteDirectory}/{entry.Name}"), TransferDirection.Upload, size);
            else if (LeftMode.SelectedIndex == 1 && RightMode.SelectedIndex == 0)
            {
                var destination = Path.Combine(_rightLocalDirectory, entry.Name);
                if (File.Exists(destination) && MessageBox.Show($"Replace '{entry.Name}'?", "Transfer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) continue;
                queueEntry = AddQueue(entry.Name, entry.FullPath, destination, TransferDirection.DownloadFromLeft);
            }
            else if (LeftMode.SelectedIndex == 1 && RightMode.SelectedIndex == 1)
                queueEntry = AddQueue(entry.Name, entry.FullPath, NormalizeRemotePath($"{_remoteDirectory}/{entry.Name}"), TransferDirection.RelayLeftToRight);
            else continue;
            Schedule(queueEntry);
        }
        if (RightMode.SelectedIndex == 0) LoadRightLocalDirectory(_rightLocalDirectory); else await NavigateRemoteAsync(_remoteDirectory);
    }

    private void TransferLeftNow_Click(object sender, RoutedEventArgs e) => Upload_Click(sender, e);
    private void TransferRightNow_Click(object sender, RoutedEventArgs e) => Download_Click(sender, e);
    private void CopyNameLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) Clipboard.SetText(item.Name); }
    private void CopyPathLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) Clipboard.SetText(item.FullPath); }
    private void CopyNameRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) Clipboard.SetText(item.Name); }
    private void CopyPathRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) Clipboard.SetText(item.FullPath); }
    private void CopyUrlLeft_Click(object sender, RoutedEventArgs e) { if (LocalList.SelectedItem is LocalEntryView item) CopyEntryUrl(true, item.FullPath); }
    private void CopyUrlRight_Click(object sender, RoutedEventArgs e) { if (RemoteList.SelectedItem is RemoteEntryView item) CopyEntryUrl(false, item.FullPath); }
    private async void RefreshLeft_Click(object sender, RoutedEventArgs e) { if (LeftMode.SelectedIndex == 0) LoadLocalDirectory(_localDirectory); else await NavigateLeftRemoteAsync(_leftRemoteDirectory); }
    private async void RefreshRight_Click(object sender, RoutedEventArgs e) { if (RightMode.SelectedIndex == 0) LoadRightLocalDirectory(_rightLocalDirectory); else await NavigateRemoteAsync(_remoteDirectory); }

    private async void CreateFolderLeft_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync(true);
    private async void CreateFolderRight_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync(false);
    private async void CreateFolderAndEnterLeft_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync(true, true);
    private async void CreateFolderAndEnterRight_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync(false, true);
    private async Task CreateFolderAsync(bool left, bool enter = false)
    {
        var dialog = new CommandParameterWindow("New folder name:") { Owner = this }; if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        try
        {
            string createdPath;
            if (left && LeftMode.SelectedIndex == 0) { createdPath = Path.Combine(_localDirectory, dialog.Value); Directory.CreateDirectory(createdPath); }
            else if (!left && RightMode.SelectedIndex == 0) { createdPath = Path.Combine(_rightLocalDirectory, dialog.Value); Directory.CreateDirectory(createdPath); }
            else
            {
                var session = left ? _leftRemoteSession : _remoteSession; var directory = left ? _leftRemoteDirectory : _remoteDirectory;
                if (session?.IsConnected != true) return; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                createdPath = NormalizeRemotePath($"{directory}/{dialog.Value}");
                await session.ExecuteCommandAsync($"MKD {createdPath}", timeout.Token);
            }
            if (enter)
            {
                if (left && LeftMode.SelectedIndex == 0) LoadLocalDirectory(createdPath);
                else if (!left && RightMode.SelectedIndex == 0) LoadRightLocalDirectory(createdPath);
                else if (left) await NavigateLeftRemoteAsync(createdPath);
                else await NavigateRemoteAsync(createdPath);
            }
            else if (left) RefreshLeft_Click(this, new RoutedEventArgs()); else RefreshRight_Click(this, new RoutedEventArgs());
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
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var pending = new Stack<(string Source, string Destination)>();
            var files = new List<(RemoteEntry Entry, string Destination)>();
            pending.Push((sourceRoot, destinationRoot));
            var fileCount = 0;
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                IReadOnlyList<RemoteEntry> children;
                try { children = await source.ListAsync(folder.Source, timeout.Token); }
                catch (Exception exception) when (!folder.Source.Equals(sourceRoot, StringComparison.Ordinal))
                {
                    LogText.AppendText($"{Environment.NewLine}Skipped inaccessible remote folder {folder.Source}: {FriendlyMessage(exception)}");
                    continue;
                }
                var nuke = NukeDetector.DetectDirectory(RemoteLeaf(folder.Source), children);
                if (nuke.IsNuked && MessageBox.Show(
                        $"'{RemoteLeaf(folder.Source)}' is marked {nuke.Display}.\n\nQueue it anyway?",
                        "Nuked release", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    LogText.AppendText($"{Environment.NewLine}Nuke detection blocked transfer: {folder.Source} ({nuke.Display}).");
                    LogText.ScrollToEnd();
                    return;
                }
                await EnsureRemoteDirectoryAsync(destination, folder.Destination, timeout.Token);
                foreach (var child in children)
                {
                    var target = NormalizeRemotePath($"{folder.Destination}/{child.Name}");
                    if (child.IsDirectory)
                    {
                        if (!ShouldSkip(child.Name, true)) pending.Push((child.FullPath, target));
                    }
                    else if (!ShouldSkip(child.Name)) { files.Add((child, target)); fileCount++; }
                }
            }
            await WarmWorkersForDirectionAsync(direction, files.Count, timeout.Token);
            foreach (var file in files.OrderBy(file => PriorityRank(file.Entry.Name)).ThenBy(file => file.Entry.Name, StringComparer.OrdinalIgnoreCase))
                Schedule(AddQueue(file.Entry.Name, file.Entry.FullPath, file.Destination, direction, file.Entry.Size ?? 0));
            LogText.AppendText($"{Environment.NewLine}Queued remote folder {sourceRoot}: {fileCount} files.");
        }
        catch (Exception exception)
        {
            LogText.AppendText($"{Environment.NewLine}Could not queue remote folder {sourceRoot}: {FriendlyMessage(exception)}");
            ConnectionStatus.Text = "Remote folder queue failed";
        }
        LogText.ScrollToEnd();
    }

    private async void CreateFileLeft_Click(object sender, RoutedEventArgs e) => await CreateEmptyFileAsync(true);
    private async void CreateFileRight_Click(object sender, RoutedEventArgs e) => await CreateEmptyFileAsync(false);
    private async Task CreateEmptyFileAsync(bool left)
    {
        var dialog = new CommandParameterWindow("New file name:") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        try
        {
            var local = (left && LeftMode.SelectedIndex == 0) || (!left && RightMode.SelectedIndex == 0);
            if (local)
            {
                var directory = left ? _localDirectory : _rightLocalDirectory;
                await File.WriteAllBytesAsync(Path.Combine(directory, dialog.Value.Trim()), []);
            }
            else
            {
                var session = left ? _leftRemoteSession : _remoteSession;
                var directory = left ? _leftRemoteDirectory : _remoteDirectory;
                if (session?.IsConnected != true) return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await session.UploadAsync(NormalizeRemotePath($"{directory}/{dialog.Value.Trim()}"), Stream.Null, 0, null, timeout.Token);
            }
            if (left) RefreshLeft_Click(this, new RoutedEventArgs()); else RefreshRight_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) { MessageBox.Show(FriendlyMessage(exception), "Create file", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ViewLeft_Click(object sender, RoutedEventArgs e)
    {
        if (LocalList.SelectedItem is LocalEntryView item) await OpenOrViewAsync(true, item.FullPath, item.Name, item.IsDirectory);
    }

    private async void ViewRight_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is RemoteEntryView item) await OpenOrViewAsync(false, item.FullPath, item.Name, item.IsDirectory);
    }

    private async Task OpenOrViewAsync(bool left, string path, string name, bool directory)
    {
        try
        {
            var local = (left && LeftMode.SelectedIndex == 0) || (!left && RightMode.SelectedIndex == 0);
            if (directory)
            {
                if (local) { if (left) LoadLocalDirectory(path); else LoadRightLocalDirectory(path); }
                else if (left) await NavigateLeftRemoteAsync(path); else await NavigateRemoteAsync(path);
                return;
            }

            var openPath = path;
            if (!local)
            {
                var session = left ? _leftRemoteSession : _remoteSession;
                if (session?.IsConnected != true) return;
                var previewDirectory = Path.Combine(Path.GetTempPath(), "FluxFTP", "Preview");
                Directory.CreateDirectory(previewDirectory);
                openPath = Path.Combine(previewDirectory, $"{Guid.NewGuid():N}-{SanitizeFileName(name)}");
                await using var output = new FileStream(openPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await session.DownloadAsync(path, output, 0, null, timeout.Token);
                LogText.AppendText($"{Environment.NewLine}Preview downloaded: {path}");
            }
            Process.Start(new ProcessStartInfo(openPath) { UseShellExecute = true });
        }
        catch (Exception exception) { MessageBox.Show(FriendlyMessage(exception), "Open / View", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void CopyEntryUrl(bool left, string path)
    {
        var local = (left && LeftMode.SelectedIndex == 0) || (!left && RightMode.SelectedIndex == 0);
        if (local) { Clipboard.SetText(new Uri(Path.GetFullPath(path)).AbsoluteUri); return; }
        var profile = left ? _leftProfile : _rightProfile;
        if (profile is null) return;
        var scheme = profile.Protocol switch
        {
            TransferProtocol.Sftp => "sftp",
            TransferProtocol.FtpsImplicit => "ftps",
            TransferProtocol.FtpsExplicit => "ftpes",
            _ => "ftp"
        };
        var defaultPort = profile.Protocol switch { TransferProtocol.Sftp => 22, TransferProtocol.FtpsImplicit => 990, _ => 21 };
        var user = string.IsNullOrWhiteSpace(profile.Username) ? "" : $"{Uri.EscapeDataString(profile.Username)}@";
        var port = profile.Port == defaultPort ? "" : $":{profile.Port}";
        var encodedPath = "/" + string.Join('/', path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        Clipboard.SetText($"{scheme}://{user}{profile.Host}{port}{encodedPath}");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private async Task QueueRemoteToLocalDirectoryAsync(FtpRemoteSession source, string sourceRoot,
        string destinationRoot, TransferDirection direction)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var pending = new Stack<(string Source, string Destination)>();
            var files = new List<(RemoteEntry Entry, string Destination)>();
            pending.Push((sourceRoot, Path.GetFullPath(destinationRoot)));
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                var children = await source.ListAsync(folder.Source, timeout.Token);
                var nuke = NukeDetector.DetectDirectory(RemoteLeaf(folder.Source), children);
                if (nuke.IsNuked && MessageBox.Show(
                        $"'{RemoteLeaf(folder.Source)}' is marked {nuke.Display}.\n\nDownload it anyway?",
                        "Nuked release", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    LogText.AppendText($"{Environment.NewLine}Nuke detection blocked download: {folder.Source} ({nuke.Display}).");
                    LogText.ScrollToEnd();
                    return;
                }
                Directory.CreateDirectory(folder.Destination);
                foreach (var child in children)
                {
                    if (child.Name is "." or "..") continue;
                    var target = Path.Combine(folder.Destination, child.Name);
                    if (child.IsDirectory)
                    {
                        if (!ShouldSkip(child.Name, true)) pending.Push((child.FullPath, target));
                    }
                    else if (!ShouldSkip(child.Name)) files.Add((child, target));
                }
            }
            await WarmWorkersForDirectionAsync(direction, files.Count, timeout.Token);
            foreach (var file in files.OrderBy(file => PriorityRank(file.Entry.Name)).ThenBy(file => file.Entry.Name, StringComparer.OrdinalIgnoreCase))
                Schedule(AddQueue(file.Entry.Name, file.Entry.FullPath, file.Destination, direction, file.Entry.Size ?? 0));
            LogText.AppendText($"{Environment.NewLine}Queued remote folder for local download {sourceRoot}: {files.Count} files.");
            LogText.ScrollToEnd();
            if (direction == TransferDirection.Download) LoadLocalDirectory(_localDirectory); else LoadRightLocalDirectory(_rightLocalDirectory);
        }
        catch (Exception exception)
        {
            LogText.AppendText($"{Environment.NewLine}Could not queue remote folder {sourceRoot}: {FriendlyMessage(exception)}");
            LogText.ScrollToEnd();
        }
    }

    private async Task QueueLocalDirectoryAsync(string sourceRoot, FtpRemoteSession destination,
        string destinationRoot, TransferDirection direction)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var pending = new Stack<(string Source, string Destination)>();
            var files = new List<(FileInfo File, string Destination)>();
            pending.Push((Path.GetFullPath(sourceRoot), destinationRoot));
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                await EnsureRemoteDirectoryAsync(destination, folder.Destination, timeout.Token);
                foreach (var child in Directory.EnumerateFileSystemEntries(folder.Source))
                {
                    var name = Path.GetFileName(child);
                    var target = NormalizeRemotePath($"{folder.Destination}/{name}");
                    if (Directory.Exists(child))
                    {
                        if (!ShouldSkip(name, true)) pending.Push((child, target));
                    }
                    else if (!ShouldSkip(name)) files.Add((new FileInfo(child), target));
                }
            }
            await WarmWorkersForDirectionAsync(direction, files.Count, timeout.Token);
            foreach (var file in files.OrderBy(file => PriorityRank(file.File.Name)).ThenBy(file => file.File.Name, StringComparer.OrdinalIgnoreCase))
                Schedule(AddQueue(file.File.Name, file.File.FullName, file.Destination, direction, file.File.Length));
            LogText.AppendText($"{Environment.NewLine}Queued local folder {sourceRoot}: {files.Count} files.");
        }
        catch (Exception exception)
        {
            LogText.AppendText($"{Environment.NewLine}Could not queue local folder {sourceRoot}: {FriendlyMessage(exception)}");
            ConnectionStatus.Text = "Local folder queue failed";
        }
        LogText.ScrollToEnd();
    }

    private int PriorityRank(string name)
    {
        var patterns = _settings.PriorityPatterns.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < patterns.Length; index++)
            if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(patterns[index], name, true)) return index;
        return patterns.Length;
    }

    private bool ShouldSkip(string name, bool isDirectory = false) =>
        SkipRuleMatcher.ShouldSkip(_settings, name, isDirectory, "Transfer");

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
        {
            if (!item.IsSelected) list.SelectedItems.Clear();
            item.IsSelected = true;
        }
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

    private QueueEntryView AddQueue(string name, string source, string destination, TransferDirection direction, long totalBytes = 0, Guid? sourceProfileId = null, Guid? destinationProfileId = null)
    {
        var entry = new QueueEntryView(name, source, destination, direction, totalBytes: totalBytes) { SourceProfileId = sourceProfileId, DestinationProfileId = destinationProfileId, QueuedAt = DateTimeOffset.Now }; _queue.Add(entry); SaveQueue(); UpdateQueueStatus(); return entry;
    }

    private void Schedule(QueueEntryView entry)
    {
        var (sourceSite, destinationSite) = SitesFor(entry.Direction);
        sourceSite ??= entry.SourceProfileId;
        destinationSite ??= entry.DestinationProfileId;
        entry.State = "Queued";
        entry.QueuedAt ??= DateTimeOffset.Now;
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
        TransferDirection.ApiDownload or TransferDirection.ApiFxp => (null, null),
        _ => (null, null)
    };

    private async Task ExecuteScheduledAsync(QueueEntryView entry, CancellationToken cancellationToken)
    {
        var needsLeft = entry.Direction is TransferDirection.UploadToLeft or TransferDirection.DownloadFromLeft or TransferDirection.RelayLeftToRight or TransferDirection.RelayRightToLeft;
        var needsRight = entry.Direction is TransferDirection.Download or TransferDirection.Upload or TransferDirection.RelayLeftToRight or TransferDirection.RelayRightToLeft;
        var profiles = new ProfileStore().Load();
        // Site Options can be changed while a pane remains connected. Worker
        // slots must use the persisted profile so Clear FXP takes effect on the
        // next file without requiring a disconnect or application restart.
        var leftProfile = _leftProfile is null ? null :
            profiles.FirstOrDefault(profile => profile.Id == _leftProfile.Id) ?? _leftProfile;
        var rightProfile = _rightProfile is null ? null :
            profiles.FirstOrDefault(profile => profile.Id == _rightProfile.Id) ?? _rightProfile;
        if (leftProfile is not null) leftProfile = ApplyGlobalProxy(leftProfile);
        if (rightProfile is not null) rightProfile = ApplyGlobalProxy(rightProfile);
        var apiProfile = entry.Direction == TransferDirection.ApiDownload ? profiles.FirstOrDefault(profile => profile.Id == entry.SourceProfileId) : null;
        var apiFxpSource = entry.Direction == TransferDirection.ApiFxp ? profiles.FirstOrDefault(profile => profile.Id == entry.SourceProfileId) : null;
        var apiFxpDestination = entry.Direction == TransferDirection.ApiFxp ? profiles.FirstOrDefault(profile => profile.Id == entry.DestinationProfileId) : null;
        if (apiProfile is not null) apiProfile = ApplyGlobalProxy(apiProfile);
        if (apiFxpSource is not null) apiFxpSource = ApplyGlobalProxy(apiFxpSource);
        if (apiFxpDestination is not null) apiFxpDestination = ApplyGlobalProxy(apiFxpDestination);
        if ((needsLeft && leftProfile is null) || (needsRight && rightProfile is null) || (entry.Direction == TransferDirection.ApiDownload && apiProfile is null) ||
            (entry.Direction == TransferDirection.ApiFxp && (apiFxpSource is null || apiFxpDestination is null)))
            throw new InvalidOperationException("A required site is not connected.");
        FtpRemoteSession? leftWorker = null; FtpRemoteSession? rightWorker = null;
        FtpRemoteSession? apiWorker = null;
        FtpRemoteSession? apiFxpSourceWorker = null; FtpRemoteSession? apiFxpDestinationWorker = null;
        var reuseWorkers = false;
        try
        {
            entry.State = "Transferring";
            entry.StartedAt ??= DateTimeOffset.Now;
            SaveQueue();
            await RunScriptsAsync("BeforeTransfer", TransferScriptVariables(entry, "Starting"), false);
            if (needsLeft) leftWorker = await RentWorkerAsync(leftProfile!, cancellationToken);
            if (needsRight) rightWorker = await RentWorkerAsync(rightProfile!, cancellationToken);
            if (apiProfile is not null) apiWorker = await RentWorkerAsync(apiProfile, cancellationToken);
            if (apiFxpSource is not null) apiFxpSourceWorker = await RentWorkerAsync(apiFxpSource, cancellationToken);
            if (apiFxpDestination is not null) apiFxpDestinationWorker = await RentWorkerAsync(apiFxpDestination, cancellationToken);
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
                await using (var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 64 * 1024, true))
                {
                    output.Seek(0, SeekOrigin.End);
                    entry.BytesTransferred = output.Length;
                    await session.DownloadAsync(entry.Source, output, output.Length, progress, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
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
                var sourceSession = entry.Direction == TransferDirection.ApiFxp ? apiFxpSourceWorker! : entry.Direction == TransferDirection.RelayLeftToRight ? leftWorker! : rightWorker!;
                var destinationSession = entry.Direction == TransferDirection.ApiFxp ? apiFxpDestinationWorker! : entry.Direction == TransferDirection.RelayLeftToRight ? rightWorker! : leftWorker!;
                var sourceProfile = entry.Direction == TransferDirection.ApiFxp ? apiFxpSource! :
                    entry.Direction == TransferDirection.RelayLeftToRight ? leftProfile! : rightProfile!;
                var destinationProfile = entry.Direction == TransferDirection.ApiFxp ? apiFxpDestination! :
                    entry.Direction == TransferDirection.RelayLeftToRight ? rightProfile! : leftProfile!;
                await EnsureRemoteDirectoryAsync(destinationSession, RemoteParent(entry.Destination), cancellationToken);
                var clearFxp = !sourceSession.UsesTlsControl || !destinationSession.UsesTlsControl ||
                    sourceSession.FxpProtection == FxpProtectionMode.Clear ||
                    destinationSession.FxpProtection == FxpProtectionMode.Clear;
                var directFxpAvailable = sourceProfile.Protocol != TransferProtocol.Sftp &&
                    destinationProfile.Protocol != TransferProtocol.Sftp &&
                    (clearFxp || destinationSession.Capabilities.Contains("CPSV") ||
                    (sourceSession.Capabilities.Contains("SSCN") && destinationSession.Capabilities.Contains("SSCN")));
                var pair = (Source: sourceProfile.Id, Destination: destinationProfile.Id);
                var configuredReverse = PreferredReverseFxp(sourceProfile, destinationProfile);
                var learnedReverse = configuredReverse is null && _reverseFxpPairs.Contains(pair);
                var useReverse = clearFxp && (configuredReverse ?? learnedReverse);
                if (directFxpAvailable)
                {
                    try
                    {
                        LogText.AppendText($"{Environment.NewLine}Attempting direct {(clearFxp ? "clear" : "secure")} FXP: {entry.Name}");
                        var fxpStartedAt = DateTime.UtcNow;
                        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var monitor = MonitorFxpAsync(sourceProfile, destinationProfile, entry, monitorCancellation.Token);
                        try
                        {
                            if (useReverse)
                            {
                                var routeSource = configuredReverse == true ? "configured" : "learned";
                                LogText.AppendText($"{Environment.NewLine}Using {routeSource} PASV/PORT route for {sourceProfile.Name} → {destinationProfile.Name}: {entry.Name}");
                                await sourceSession.FxpToAsync(destinationSession, entry.Source, entry.Destination,
                                    cancellationToken, reverseDataConnection: true);
                            }
                            else
                            {
                                try { await sourceSession.FxpToAsync(destinationSession, entry.Source, entry.Destination, cancellationToken); }
                                catch (FtpCommandException exception) when (clearFxp && exception.StatusCode == 425)
                                {
                                    LogText.AppendText($"{Environment.NewLine}Standard clear FXP timed out; retrying with source PASV and destination PORT: {entry.Name}");
                                    await sourceSession.RetryFxpWithReversedTopologyAsync(
                                        destinationSession, entry.Source, entry.Destination, cancellationToken);
                                    if (_reverseFxpPairs.Add(pair)) _fxpRouteStore.SaveReverseRoutes(_reverseFxpPairs);
                                    LogText.AppendText($"{Environment.NewLine}Remembered reverse FXP route permanently for {sourceProfile.Name} → {destinationProfile.Name}.");
                                }
                            }
                        }
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
                        AppendFxpTimings(sourceSession);
                        LogText.AppendText($"{Environment.NewLine}Direct FXP completed via {sourceSession.LastFxpNegotiation}: {entry.Name}");
                        entry.State = "Completed";
                        await RunScriptsAsync("AfterTransfer", TransferScriptVariables(entry, "Completed"), true);
                        reuseWorkers = true;
                        return;
                    }
                    catch (FtpCommandException exception) when (exception.StatusCode == 553 && DestinationUsesXdupe(entry))
                    {
                        throw;
                    }
                    catch (Exception fxpException)
                    {
                        if (learnedReverse && (fxpException is OperationCanceledException or FtpCommandException { StatusCode: 425 }) &&
                            _reverseFxpPairs.Remove(pair))
                        {
                            _fxpRouteStore.SaveReverseRoutes(_reverseFxpPairs);
                            LogText.AppendText($"{Environment.NewLine}Removed stale learned reverse route for {sourceProfile.Name} → {destinationProfile.Name}.");
                        }
                        AppendFxpTimings(sourceSession);
                        AppendFxpFailureDiagnostic(entry, sourceProfile, destinationProfile, sourceSession,
                            destinationSession, clearFxp, PreferredReverseFxp(sourceProfile, destinationProfile));
                        LogText.AppendText($"{Environment.NewLine}Direct FXP rejected ({FriendlyMessage(fxpException)}). Reconnecting for client relay…");
                        if (entry.Direction == TransferDirection.ApiFxp)
                        {
                            if (apiFxpSourceWorker is not null) await apiFxpSourceWorker.DisposeAsync();
                            if (apiFxpDestinationWorker is not null) await apiFxpDestinationWorker.DisposeAsync();
                            apiFxpSourceWorker = await CreateWorkerAsync(apiFxpSource!, cancellationToken);
                            apiFxpDestinationWorker = await CreateWorkerAsync(apiFxpDestination!, cancellationToken);
                            sourceSession = apiFxpSourceWorker; destinationSession = apiFxpDestinationWorker;
                        }
                        else
                        {
                            if (leftWorker is not null) await leftWorker.DisposeAsync();
                            if (rightWorker is not null) await rightWorker.DisposeAsync();
                            leftWorker = await CreateWorkerAsync(leftProfile!, cancellationToken);
                            rightWorker = await CreateWorkerAsync(rightProfile!, cancellationToken);
                            sourceSession = entry.Direction == TransferDirection.RelayLeftToRight ? leftWorker : rightWorker;
                            destinationSession = entry.Direction == TransferDirection.RelayLeftToRight ? rightWorker : leftWorker;
                        }
                    }
                }
                else LogText.AppendText($"{Environment.NewLine}Secure direct FXP is unavailable in Auto mode; using client relay.");

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
            reuseWorkers = true;
        }
        catch (FtpCommandException exception) when (exception.StatusCode == 553 && DestinationUsesXdupe(entry))
        {
            ApplyXdupeReply(entry, exception.Message);
            entry.BytesTransferred = entry.TotalBytes;
            entry.State = "Completed";
            LogText.AppendText($"{Environment.NewLine}XDUPE skipped existing remote file: {entry.Name}");
            await RunScriptsAsync("AfterTransfer", TransferScriptVariables(entry, "XDUPE skipped"), true);
            reuseWorkers = true;
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
            await ReleaseWorkerAsync(leftProfile, leftWorker, reuseWorkers);
            await ReleaseWorkerAsync(rightProfile, rightWorker, reuseWorkers);
            await ReleaseWorkerAsync(apiProfile, apiWorker, reuseWorkers);
            await ReleaseWorkerAsync(apiFxpSource, apiFxpSourceWorker, reuseWorkers);
            await ReleaseWorkerAsync(apiFxpDestination, apiFxpDestinationWorker, reuseWorkers);
            SaveQueue(); UpdateQueueStatus(); LogText.ScrollToEnd();
        }
    }

    private void AppendFxpTimings(FtpRemoteSession session)
    {
        if (session.LastFxpStageTimings.Count == 0) return;
        var values = session.LastFxpStageTimings.Select(stage =>
            $"{stage.Name} {(stage.Elapsed.TotalSeconds >= 1 ? $"{stage.Elapsed.TotalSeconds:0.00}s" : $"{stage.Elapsed.TotalMilliseconds:0}ms")}");
        LogText.AppendText($"{Environment.NewLine}FXP timings: {string.Join(" | ", values)}");
    }

    private void AppendFxpFailureDiagnostic(QueueEntryView entry, ConnectionProfile sourceProfile,
        ConnectionProfile destinationProfile, FtpRemoteSession sourceSession, FtpRemoteSession destinationSession,
        bool clearFxp, bool? configuredReverse)
    {
        var route = configuredReverse == true ? "source PASV / destination PORT"
            : configuredReverse == false ? "destination PASV / source PORT"
            : sourceSession.LastFxpNegotiation == "None" ? "Auto (negotiation did not complete)"
            : sourceSession.LastFxpNegotiation;
        LogText.AppendText(
            $"{Environment.NewLine}FXP diagnostic:" +
            $"{Environment.NewLine}  Source: {sourceProfile.Name} ({sourceSession.ConnectedHost}:{sourceSession.ConnectedPort})" +
            $"{Environment.NewLine}  > CWD {RemoteParent(entry.Source)}" +
            $"{Environment.NewLine}  > RETR {RemoteLeaf(entry.Source)}" +
            $"{Environment.NewLine}  Destination: {destinationProfile.Name} ({destinationSession.ConnectedHost}:{destinationSession.ConnectedPort})" +
            $"{Environment.NewLine}  > CWD {RemoteParent(entry.Destination)}" +
            $"{Environment.NewLine}  > STOR {RemoteLeaf(entry.Destination)}" +
            $"{Environment.NewLine}  Destination parent: {RemoteParent(entry.Destination)}" +
            $"{Environment.NewLine}  Data protection: {(clearFxp ? "Clear" : "TLS")}; route: {route}" +
            $"{Environment.NewLine}  PRET: source {(sourceProfile.EffectiveOptions.NeedsPret ? "on" : "off")}, destination {(destinationProfile.EffectiveOptions.NeedsPret ? "on" : "off")}");
    }

    private static bool? PreferredReverseFxp(ConnectionProfile source, ConnectionProfile destination)
    {
        var reverse = source.EffectiveOptions.FxpDataRole == FxpDataRole.Passive ||
            destination.EffectiveOptions.FxpDataRole == FxpDataRole.Active;
        var standard = source.EffectiveOptions.FxpDataRole == FxpDataRole.Active ||
            destination.EffectiveOptions.FxpDataRole == FxpDataRole.Passive;
        return reverse == standard ? null : reverse;
    }

    private bool DestinationUsesXdupe(QueueEntryView entry)
    {
        var profile = entry.Direction switch
        {
            TransferDirection.Upload or TransferDirection.RelayLeftToRight => _rightProfile,
            TransferDirection.UploadToLeft or TransferDirection.RelayRightToLeft => _leftProfile,
            TransferDirection.ApiFxp => new ProfileStore().Load().FirstOrDefault(profile => profile.Id == entry.DestinationProfileId),
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
        var sites = SitesFor(entry.Direction); var profiles = new ProfileStore().Load(); var sourceId = sites.Source ?? entry.SourceProfileId; var destinationId = sites.Destination ?? entry.DestinationProfileId;
        return new()
        {
            ["name"] = entry.Name, ["source"] = entry.Source, ["destination"] = entry.Destination, ["path"] = entry.Destination,
            ["status"] = status, ["direction"] = entry.Direction.ToString(),
            ["source_site"] = profiles.FirstOrDefault(profile => profile.Id == sourceId)?.Name ?? "Local",
            ["destination_site"] = profiles.FirstOrDefault(profile => profile.Id == destinationId)?.Name ?? "Local"
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

    private static async Task MonitorFxpAsync(ConnectionProfile sourceProfile, ConnectionProfile destinationProfile,
        QueueEntryView entry, CancellationToken cancellationToken)
    {
        await using var monitor = await CreateWorkerAsync(destinationProfile, cancellationToken);
        FtpRemoteSession? sourceMonitor = null;
        long previousBytes = 0;
        var previousAt = DateTime.UtcNow;
        var hasSizeBaseline = false;
        bool? ioGuiExtAvailable = null;
        var destinationMisses = 0;
        var lastActivityAt = DateTime.UtcNow;

        void ApplyActivitySample(long transferred, long speed)
        {
            var now = DateTime.UtcNow;
            var elapsed = Math.Clamp((now - lastActivityAt).TotalSeconds, 0, 2);
            lastActivityAt = now;
            if (transferred > entry.BytesTransferred)
                entry.BytesTransferred = entry.TotalBytes > 0 ? Math.Min(transferred, entry.TotalBytes) : transferred;
            else if (speed > 0 && elapsed > 0)
            {
                var estimated = entry.BytesTransferred + (long)(speed * elapsed);
                entry.BytesTransferred = entry.TotalBytes > 0 ? Math.Min(estimated, entry.TotalBytes) : estimated;
            }
            if (speed > 0) entry.SpeedBytesPerSecond = speed;
        }

        try
        {
            while (true)
            {
                await Task.Delay(400, cancellationToken);
                // ioFTPD commonly preallocates the complete destination file, so SIZE
                // cannot reveal live FXP progress. ioGuiExt exposes the same transfer
                // counter and speed that ioGUI uses; prefer it when available.
                var activityMatched = false;
                try
                {
                    var activity = await monitor.ExecuteCommandAsync("SITE ioGuiExt who", cancellationToken);
                    ioGuiExtAvailable = activity.StatusCode is >= 200 and < 300;
                    if (ioGuiExtAvailable == true &&
                        TryReadIoFtpdTransfer(activity.Message, entry, expectUpload: true, out var transferred, out var speed))
                    {
                        // ioFTPD can leave TRANSFERSIZE at zero while still reporting
                        // a valid speed. Integrate that speed until a better counter
                        // arrives so the aggregate progress bar keeps moving.
                        ApplyActivitySample(transferred, speed);
                        activityMatched = true;
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    ioGuiExtAvailable = false;
                }

                if (activityMatched)
                {
                    destinationMisses = 0;
                    continue;
                }

                destinationMisses++;
                if (sourceMonitor is null && destinationMisses >= 3)
                {
                    try { sourceMonitor = await CreateWorkerAsync(sourceProfile, cancellationToken); }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                }
                if (sourceMonitor is not null)
                {
                    try
                    {
                        var sourceActivity = await sourceMonitor.ExecuteCommandAsync("SITE ioGuiExt who", cancellationToken);
                        if (sourceActivity.StatusCode is >= 200 and < 300 &&
                            TryReadIoFtpdTransfer(sourceActivity.Message, entry, expectUpload: false, out var transferred, out var speed))
                        {
                            ApplyActivitySample(transferred, speed);
                            continue;
                        }
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                }

                // If ioGuiExt answered but did not contain a matching row, SIZE is
                // still worth trying. Previously this fallback was skipped entirely.
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
        finally
        {
            if (sourceMonitor is not null) await sourceMonitor.DisposeAsync();
        }
    }

    private static bool TryReadIoFtpdTransfer(string response, QueueEntryView entry, bool expectUpload,
        out long transferred, out long speed)
    {
        transferred = -1;
        speed = 0;
        var fileName = entry.Name;
        (long Transferred, long Speed)? fallback = null;
        var activeCandidates = 0;
        foreach (var line in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var payload = line.Length > 4 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2])
                ? line[4..].Trim()
                : line.Trim();
            if (!payload.StartsWith("cid |", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = payload.Split('|').Select(part => part.Trim()).ToArray();
            if (parts.Length < 19) continue;
            var action = $"{parts[10]} {parts[16]}";
            var expectedAction = expectUpload
                ? action.Contains("STOR", StringComparison.OrdinalIgnoreCase) || action.Contains("UPLOAD", StringComparison.OrdinalIgnoreCase)
                : action.Contains("RETR", StringComparison.OrdinalIgnoreCase) || action.Contains("DOWNLOAD", StringComparison.OrdinalIgnoreCase);
            if (!expectedAction) continue;

            var bytes = long.TryParse(parts[17], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBytes)
                ? parsedBytes
                : -1;
            var parsedSpeed = ParseIoFtpdSpeed(parts[18]);
            var identity = $"{parts[10]} {parts[12]} {parts[13]}";
            if (identity.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                transferred = bytes;
                speed = parsedSpeed;
                return true;
            }

            activeCandidates++;
            fallback = (bytes, parsedSpeed);
        }
        if (activeCandidates != 1 || fallback is null) return false;
        transferred = fallback.Value.Transferred;
        speed = fallback.Value.Speed;
        return true;
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

    private async Task<FtpRemoteSession> RentWorkerAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        await DisposeStalePooledWorkersAsync(profile);
        var pool = _workerPool.GetOrAdd(profile, _ => new ConcurrentBag<PooledWorker>());
        while (pool.TryTake(out var pooled))
        {
            var session = pooled.Session;
            if (!session.IsConnected) { await session.DisposeAsync(); continue; }
            if (DateTimeOffset.UtcNow - pooled.ReturnedAt < WorkerHealthCheckInterval) return session;
            try
            {
                var response = await session.ExecuteCommandAsync("NOOP", cancellationToken);
                if (response.StatusCode is >= 200 and < 300) return session;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
            await session.DisposeAsync();
        }
        return await CreateWorkerAsync(profile, cancellationToken);
    }

    private async Task WarmWorkersForDirectionAsync(TransferDirection direction, int fileCount, CancellationToken cancellationToken)
    {
        if (fileCount <= 0 || _workerPoolShuttingDown) return;
        var profiles = new ProfileStore().Load();
        var requested = new Dictionary<ConnectionProfile, int>();

        void Add(Guid? profileId, bool download)
        {
            if (profileId is null) return;
            var profile = profiles.FirstOrDefault(item => item.Id == profileId);
            if (profile is null) return;
            profile = ApplyGlobalProxy(profile);
            var options = profile.EffectiveOptions;
            var directional = download ? options.MaxDownloadSlots : options.MaxUploadSlots;
            var desired = Math.Min(fileCount, Math.Min(options.MaxSlots, directional));
            if (desired > 0) requested[profile] = Math.Max(requested.GetValueOrDefault(profile), desired);
        }

        var sites = SitesFor(direction);
        Add(sites.Source, true);
        Add(sites.Destination, false);
        if (requested.Count == 0) return;

        var started = DateTime.UtcNow;
        await Task.WhenAll(requested.Select(async pair =>
        {
            await DisposeStalePooledWorkersAsync(pair.Key);
            var pool = _workerPool.GetOrAdd(pair.Key, _ => new ConcurrentBag<PooledWorker>());
            var missing = Math.Max(0, pair.Value - pool.Count);
            if (missing == 0) return;
            var sessions = await Task.WhenAll(Enumerable.Range(0, missing).Select(async _ =>
            {
                try { return await CreateWorkerAsync(pair.Key, cancellationToken); }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    LogText.Dispatcher.Invoke(() => LogText.AppendText(
                        $"{Environment.NewLine}Could not prewarm a slot for {pair.Key.Name}: {FriendlyMessage(exception)}"));
                    return null;
                }
            }));
            foreach (var session in sessions)
                if (session is not null) pool.Add(new PooledWorker(session, DateTimeOffset.UtcNow));
        }));
        var elapsed = DateTime.UtcNow - started;
        LogText.AppendText($"{Environment.NewLine}Transfer slots ready in {elapsed.TotalSeconds:0.00}s.");
    }

    private async Task ReleaseWorkerAsync(ConnectionProfile? profile, FtpRemoteSession? session, bool reusable)
    {
        if (session is null) return;
        if (!reusable || !session.IsConnected || _workerPoolShuttingDown || profile is null)
        {
            await session.DisposeAsync();
            return;
        }
        _workerPool.GetOrAdd(profile, _ => new ConcurrentBag<PooledWorker>())
            .Add(new PooledWorker(session, DateTimeOffset.UtcNow));
    }

    private async Task DisposePooledWorkersAsync(Guid? profileId = null)
    {
        var pools = _workerPool.Where(pair => profileId is null || pair.Key.Id == profileId).ToList();
        foreach (var pair in pools)
        {
            if (!_workerPool.TryRemove(pair.Key, out var pool)) continue;
            while (pool.TryTake(out var pooled)) await pooled.Session.DisposeAsync();
        }
    }

    private async Task DisposeStalePooledWorkersAsync(ConnectionProfile currentProfile)
    {
        var staleProfiles = _workerPool.Keys
            .Where(profile => profile.Id == currentProfile.Id && profile != currentProfile).ToList();
        foreach (var profile in staleProfiles)
        {
            if (!_workerPool.TryRemove(profile, out var pool)) continue;
            while (pool.TryTake(out var pooled)) await pooled.Session.DisposeAsync();
        }
    }

    private sealed record PooledWorker(FtpRemoteSession Session, DateTimeOffset ReturnedAt);

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
                { State = item.State is "Completed" ? "Completed" : "Paused", BytesTransferred = item.BytesTransferred, TotalBytes = item.TotalBytes, SourceProfileId = item.SourceProfileId, DestinationProfileId = item.DestinationProfileId, QueuedAt = item.QueuedAt, StartedAt = item.StartedAt });
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
            var snapshots = _queue.Select(item => new QueueSnapshot(item.Name, item.Source, item.Destination, item.Direction, item.State, item.BytesTransferred, item.TotalBytes, item.Id, item.SourceProfileId, item.DestinationProfileId, item.QueuedAt, item.StartedAt));
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
        DirectoryListingMode.Auto when capabilities.Contains("MLSD") => "MLSD (LIST, STAT -l fallback)",
        DirectoryListingMode.Auto => "LIST (STAT -l fallback; MLSD not advertised)",
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
        _workerPoolShuttingDown = true;
        await _engine.DisposeAsync();
        await DisposePooledWorkersAsync();
        if (_remoteSession is not null) await _remoteSession.DisposeAsync();
        if (_leftRemoteSession is not null) await _leftRemoteSession.DisposeAsync();
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (!_exitRequested && !ConfirmExit())
        {
            e.Cancel = true;
            return;
        }
        _exitRequested = true;
        base.OnClosing(e);
    }

    private void RestoreWindowLayout()
    {
        var layout = _layoutStore.Load();
        if (layout is null) return;
        Width = Math.Max(MinWidth, FiniteLayoutValue(layout.Width, Width));
        Height = Math.Max(MinHeight, FiniteLayoutValue(layout.Height, Height));
        if (double.IsFinite(layout.Left) && double.IsFinite(layout.Top) &&
            layout.Left >= SystemParameters.VirtualScreenLeft && layout.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            layout.Top >= SystemParameters.VirtualScreenTop && layout.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        { Left = layout.Left; Top = layout.Top; }
        if (double.IsFinite(layout.LeftPaneWidth) && layout.LeftPaneWidth > 100) LeftPaneColumn.Width = new GridLength(layout.LeftPaneWidth);
        _visibleQueueHeight = new GridLength(Math.Max(80, FiniteLayoutValue(layout.QueueHeight, 190)));
        _visibleLogHeight = new GridLength(Math.Max(70, FiniteLayoutValue(layout.LogHeight, 150)));
        if (layout.QueueVisible && QueueRow.Height.Value == 0) ToggleQueue_Click(this, new RoutedEventArgs());
        if (!layout.LogVisible && LogRow.Height.Value > 0) ToggleLog_Click(this, new RoutedEventArgs());
        if (layout.Maximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowLayout()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _layoutStore.Save(new WindowLayout(
            FiniteLayoutValue(bounds.Left, SystemParameters.WorkArea.Left),
            FiniteLayoutValue(bounds.Top, SystemParameters.WorkArea.Top),
            FiniteLayoutValue(bounds.Width, Math.Max(MinWidth, ActualWidth)),
            FiniteLayoutValue(bounds.Height, Math.Max(MinHeight, ActualHeight)),
            WindowState == WindowState.Maximized, FiniteLayoutValue(LeftPaneColumn.ActualWidth, 500),
            QueueRow.Height.Value > 0, QueueRow.Height.Value > 0 ? QueueRow.ActualHeight : _visibleQueueHeight.Value,
            LogRow.Height.Value > 0, LogRow.Height.Value > 0 ? LogRow.ActualHeight : _visibleLogHeight.Value));
    }

    private static double FiniteLayoutValue(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;

    private void QueueSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (QueuePanel.Visibility != Visibility.Visible) return;
        var height = Math.Clamp(QueueRow.ActualHeight - e.VerticalChange, 80, Math.Max(80, ActualHeight * 0.7));
        QueueRow.Height = new GridLength(height);
        _visibleQueueHeight = QueueRow.Height;
        e.Handled = true;
    }

    private void LogSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (LogPanel.Visibility != Visibility.Visible) return;
        var height = Math.Clamp(LogRow.ActualHeight - e.VerticalChange, 70, Math.Max(70, ActualHeight * 0.75));
        LogRow.Height = new GridLength(height);
        _visibleLogHeight = LogRow.Height;
        e.Handled = true;
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
            LogSplitterRow.Height = new GridLength(8);
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
            QueueSplitterRow.Height = new GridLength(8);
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
        var scrolling = mode.Equals("Scrolling", StringComparison.OrdinalIgnoreCase);
        ApplyLegendLayout(scrolling);
        var snapshot = GetMetricsSnapshot();
        var compact = $"Sites {snapshot.ConnectedSites}/{snapshot.ConfiguredSites}   Jobs {snapshot.ActiveJobs}/{_queue.Count}   Speed {snapshot.TotalSpeed}   Transferred {snapshot.Transferred}";
        LegendText.Text = mode switch
        {
            "Static" => "▲ upload   ▼ download   ● idle   ■ queued   ✓ completed   ✕ failed",
            "Activity" => ConnectionStatus.Text,
            "Scrolling" => ScrollLegend(compact),
            _ => compact
        };
        if (!scrolling) UpdateStatusProgress();
    }

    private void ApplyLegendLayout(bool scrolling)
    {
        Grid.SetColumnSpan(LegendText, scrolling ? 11 : 1);
        Panel.SetZIndex(LegendText, scrolling ? 1 : 0);

        var statusVisibility = scrolling ? Visibility.Collapsed : Visibility.Visible;
        TransferBytesSeparator.Visibility = statusVisibility;
        TransferBytesText.Visibility = statusVisibility;
        ProgressSeparator.Visibility = statusVisibility;
        StatusProgressPanel.Visibility = statusVisibility;
        RemainingSeparator.Visibility = statusVisibility;
        RemainingText.Visibility = statusVisibility;

        var compact = LegendBar.ActualWidth < 1050;
        var veryCompact = LegendBar.ActualWidth < 850;
        ElapsedSeparator.Visibility = scrolling || compact ? Visibility.Collapsed : Visibility.Visible;
        ElapsedText.Visibility = scrolling || compact ? Visibility.Collapsed : Visibility.Visible;
        QueueTimeSeparator.Visibility = scrolling || veryCompact ? Visibility.Collapsed : Visibility.Visible;
        QueueTimeText.Visibility = scrolling || veryCompact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStatusProgress()
    {
        var active = _queue.Where(item => item.State is "Queued" or "Transferring").ToList();
        if (active.Count == 0)
        {
            StatusProgressBar.IsIndeterminate = false;
            StatusProgressBar.Value = 0;
            StatusProgressText.Text = "Idle";
            TransferBytesText.Text = "—";
            ElapsedText.Text = "Elapsed: —";
            RemainingText.Text = "Remaining: —";
            QueueTimeText.Text = "Queue: 00:00";
            return;
        }

        var current = active.FirstOrDefault(item => item.State == "Transferring") ?? active[0];
        var direction = current.Direction switch
        {
            TransferDirection.Download or TransferDirection.DownloadFromLeft or TransferDirection.ApiDownload => "Receiving",
            TransferDirection.Upload or TransferDirection.UploadToLeft => "Sending",
            _ => "Relaying"
        };
        LegendText.Text = $"{direction}: {current.Name}";

        var percent = current.TotalBytes > 0
            ? Math.Clamp(current.BytesTransferred * 100d / current.TotalBytes, 0, 100)
            : 0;
        StatusProgressBar.IsIndeterminate = current.State == "Transferring" && current.TotalBytes <= 0;
        StatusProgressBar.Value = percent;
        StatusProgressText.Text = current.TotalBytes > 0 ? $"{percent:0}%" : current.State;
        TransferBytesText.Text = current.SpeedBytesPerSecond > 0
            ? $"{FormatSize(current.BytesTransferred)} ({FormatSize(current.SpeedBytesPerSecond)}/s)"
            : current.TotalBytes > 0
                ? $"{FormatSize(current.BytesTransferred)} / {FormatSize(current.TotalBytes)}"
                : FormatSize(current.BytesTransferred);

        var elapsed = current.StartedAt is { } started
            ? DateTimeOffset.Now - started.ToLocalTime()
            : TimeSpan.Zero;
        ElapsedText.Text = $"Elapsed: {FormatTransferTime(elapsed)}";
        var remainingBytes = Math.Max(0, current.TotalBytes - current.BytesTransferred);
        RemainingText.Text = current.SpeedBytesPerSecond > 0 && current.TotalBytes > 0
            ? $"Remaining: {FormatTransferTime(TimeSpan.FromSeconds(remainingBytes / (double)current.SpeedBytesPerSecond))}"
            : "Remaining: —";
        var oldestQueued = active.Where(item => item.QueuedAt is not null).MinBy(item => item.QueuedAt)?.QueuedAt;
        QueueTimeText.Text = $"Queue: {FormatTransferTime(oldestQueued is { } queued ? DateTimeOffset.Now - queued.ToLocalTime() : TimeSpan.Zero)}";
    }

    private static string FormatTransferTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero || !double.IsFinite(value.TotalSeconds)) return "—";
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
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
            menu.Items.Add("Exit FluxFTP", null, (_, _) => Dispatcher.Invoke(RequestExit));
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized || !_settings.MinimizeToTray) return;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void RequestExit()
    {
        if (!ConfirmExit()) return;
        _exitRequested = true;
        Close();
    }

    private bool ConfirmExit()
    {
        if (!_settings.EnableHttpsApi) return true;
        return MessageBox.Show(
            "The HTTPS/JSON API is active. Exiting FluxFTP will stop API automation and disconnect clients.\n\nExit FluxFTP?",
            "Exit FluxFTP", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
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
                id => Dispatcher.Invoke(() => RemoveTransferJob(id)), id => Dispatcher.Invoke(() => ResetTransferJob(id)),
                message => Dispatcher.BeginInvoke(() =>
                {
                    LogText.AppendText($"{Environment.NewLine}{message}");
                    LogText.ScrollToEnd();
                }));
            LogText.AppendText($"{Environment.NewLine}HTTPS/JSON API and cbftp UDP listening on {(_settings.ApiLocalhostOnly ? "localhost" : "0.0.0.0")}:{_settings.HttpsApiPort}");
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
        var validationSection = request.SrcSection ?? request.DstSection;
        if (!string.IsNullOrWhiteSpace(validationSection))
        {
            var validation = SectionReleaseValidator.Validate(validationSection, request.Name);
            if (!validation.Accepted)
            {
                LogText.AppendText($"{Environment.NewLine}Section precheck {validation.Mode}: {validation.Message}");
                LogText.ScrollToEnd();
                if (validation.Mode == SectionValidationMode.Block)
                    throw new InvalidOperationException($"Transfer blocked: {validation.Message}");
            }
        }
        var profiles = new ProfileStore().Load();
        var sourceProfile = profiles.FirstOrDefault(profile => profile.Name.Equals(request.SrcSite, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Site {request.SrcSite} was not found.");
        var destinationProfile = profiles.FirstOrDefault(profile => profile.Name.Equals(request.DstSite, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Site {request.DstSite} was not found.");
        foreach (var profile in new[] { sourceProfile, destinationProfile })
        {
            var options = profile.EffectiveOptions;
            _engine.RegisterOrUpdateSite(new SitePolicy(profile.Id, profile.Name, options.MaxSlots, options.MaxDownloadSlots, options.MaxUploadSlots, options.Priority));
        }
        var sourceBase = NormalizeRemotePath(request.SrcSection is not null ? ResolveApiSection(request.SrcSite, request.SrcSection) : request.SrcPath ?? "/");
        var destinationBase = NormalizeRemotePath(request.DstSection is not null ? ResolveApiSection(request.DstSite, request.DstSection) : request.DstPath ?? "/");
        var source = NormalizeRemotePath($"{sourceBase}/{request.Name}"); var destination = NormalizeRemotePath($"{destinationBase}/{request.Name}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); await using var sourceSession = new FtpRemoteSession();
        await sourceSession.ConnectAsync(ApplyGlobalProxy(sourceProfile), timeout.Token);
        var item = (await sourceSession.ListAsync(sourceBase, timeout.Token)).FirstOrDefault(entry => entry.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"{request.Name} was not found on {request.SrcSite}.");
        var queued = 0;
        var jobIds = new List<Guid>();
        var apiFiles = new List<(RemoteEntry Entry, string Destination)>();
        async Task QueueDirectory(string sourceDirectory, string destinationDirectory)
        {
            var children = await sourceSession.ListAsync(sourceDirectory, timeout.Token);
            var nuke = NukeDetector.DetectDirectory(RemoteLeaf(sourceDirectory), children);
            if (nuke.IsNuked)
                throw new InvalidOperationException($"Nuke detection blocked automated transfer: {sourceDirectory} ({nuke.Display}).");
            foreach (var child in children)
            {
                if (child.Name is "." or ".." || ShouldSkip(child.Name, child.IsDirectory)) continue;
                var childDestination = NormalizeRemotePath($"{destinationDirectory}/{child.Name}");
                if (child.IsDirectory) await QueueDirectory(child.FullPath, childDestination);
                else apiFiles.Add((child, childDestination));
            }
        }
        if (item.IsDirectory) await QueueDirectory(source, destination);
        else
        {
            var nuke = NukeDetector.DetectName(item.Name);
            if (nuke.IsNuked) throw new InvalidOperationException($"Nuke detection blocked automated transfer: {item.FullPath} ({nuke.Display}).");
            apiFiles.Add((item, destination));
        }
        foreach (var file in apiFiles)
        {
            var queuedEntry = AddQueue(file.Entry.Name, file.Entry.FullPath, file.Destination, TransferDirection.ApiFxp,
                file.Entry.Size ?? 0, sourceProfile.Id, destinationProfile.Id);
            jobIds.Add(queuedEntry.Id);
            Schedule(queuedEntry);
            queued++;
        }
        LogText.AppendText($"{Environment.NewLine}API queued FXP {request.Name}: {request.SrcSite} → {request.DstSite} ({queued} files)"); LogText.ScrollToEnd();
        return new ApiTransferStartResult(request.Name, "QUEUED", queued, request.SrcSite, request.DstSite, jobIds);
    });

    private async Task<object> StartApiDownloadAsync(ApiDownloadRequest request) => await await Dispatcher.InvokeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(request.Site)) throw new ArgumentException("site is required.");
        var profile = new ProfileStore().Load().FirstOrDefault(item =>
            item.Name.Equals(request.Site, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(item.Description) && item.Description.Equals(request.Site, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Site {request.Site} was not found.");
        var localRoot = string.IsNullOrWhiteSpace(request.LocalPath) ? _settings.LocalDownloadPath : request.LocalPath;
        if (string.IsNullOrWhiteSpace(localRoot)) throw new ArgumentException("local_path is required when no global local download path is configured.");
        localRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(localRoot)); Directory.CreateDirectory(localRoot);
        var remote = NormalizeRemotePath(request.RemoteSection is not null ? ResolveApiSection(profile.Name, request.RemoteSection) : request.RemotePath ?? "/");
        var options = profile.EffectiveOptions;
        _engine.RegisterOrUpdateSite(new SitePolicy(profile.Id, profile.Name, options.MaxSlots, options.MaxDownloadSlots, options.MaxUploadSlots, options.Priority));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); await using var session = new FtpRemoteSession(); await session.ConnectAsync(ApplyGlobalProxy(profile), timeout.Token);
        var queued = 0;
        var downloadFiles = new List<(RemoteEntry Entry, string Destination)>();
        async Task QueueDirectory(string sourceDirectory, string destinationDirectory)
        {
            var children = await session.ListAsync(sourceDirectory, timeout.Token);
            var nuke = NukeDetector.DetectDirectory(RemoteLeaf(sourceDirectory), children);
            if (nuke.IsNuked)
                throw new InvalidOperationException($"Nuke detection blocked automated download: {sourceDirectory} ({nuke.Display}).");
            foreach (var child in children)
            {
                if (child.Name is "." or ".." || ShouldSkip(child.Name, child.IsDirectory)) continue;
                var destination = Path.Combine(destinationDirectory, child.Name);
                if (child.IsDirectory) { if (request.Recursive) await QueueDirectory(child.FullPath, destination); }
                else downloadFiles.Add((child, destination));
            }
        }
        var parent = RemoteParent(remote); var name = Path.GetFileName(remote.TrimEnd('/'));
        var selected = remote == "/" ? null : (await session.ListAsync(parent, timeout.Token)).FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (remote == "/") await QueueDirectory(remote, localRoot);
        else if (selected is null) throw new FileNotFoundException($"{remote} was not found on {request.Site}.");
        else if (selected.IsDirectory) await QueueDirectory(selected.FullPath, Path.Combine(localRoot, selected.Name));
        else
        {
            var nuke = NukeDetector.DetectName(selected.Name);
            if (nuke.IsNuked) throw new InvalidOperationException($"Nuke detection blocked automated download: {selected.FullPath} ({nuke.Display}).");
            downloadFiles.Add((selected, Path.Combine(localRoot, selected.Name)));
        }
        foreach (var file in downloadFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
            Schedule(AddQueue(file.Entry.Name, file.Entry.FullPath, file.Destination, TransferDirection.ApiDownload, file.Entry.Size ?? 0, profile.Id));
            queued++;
        }
        LogText.AppendText($"{Environment.NewLine}API queued {queued} download(s) from {profile.Name}: {remote}"); LogText.ScrollToEnd();
        return new { site = profile.Name, description = profile.Description, remote_path = remote, local_path = localRoot, queued, status = "QUEUED" };
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
        var remoteToRemote = entry.Direction is TransferDirection.RelayLeftToRight or TransferDirection.RelayRightToLeft or TransferDirection.ApiFxp;
        var direction = entry.Direction switch
        {
            TransferDirection.Download or TransferDirection.DownloadFromLeft or TransferDirection.ApiDownload => "R→L",
            TransferDirection.Upload or TransferDirection.UploadToLeft => "L→R",
            TransferDirection.RelayLeftToRight => "R1→R2",
            TransferDirection.ApiFxp => "API FXP",
            _ => "R2→R1"
        };
        var progressPercent = entry.State == "Completed" ? 100d : entry.TotalBytes > 0 ? Math.Clamp(entry.BytesTransferred * 100d / entry.TotalBytes, 0, 100) : 0d;
        var done = entry.State == "Completed" ? "100%" : entry.TotalBytes > 0 ? $"{progressPercent:0}%" : entry.State == "Transferring" ? "RUN" : entry.State.ToUpperInvariant();
        return new TransferJobInfo(entry.Id, entry.QueuedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—", entry.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—", direction,
            remoteToRemote ? "FXP" : "FTP", entry.Name, $"{entry.Source} → {entry.Destination}",
            entry.TotalBytes > 0 ? FormatSize(entry.TotalBytes) : entry.ProgressText, "1",
            entry.TotalBytes > 0 ? FormatSize(Math.Max(0, entry.TotalBytes - entry.BytesTransferred)) : "—",
            entry.SpeedBytesPerSecond > 0 ? $"{FormatSize(entry.SpeedBytesPerSecond)}/s" : "—", done, entry.State, progressPercent);
    }).ToList();

    private static string RemoteLeaf(string path) => path.TrimEnd('/').Split('/').LastOrDefault() ?? path;

    private sealed record LocalEntryView(string Name, string Size, string Modified, string Attributes, string Status, bool IsNuked, string FullPath, bool IsDirectory);
    private sealed record RemoteEntryView(string Name, string DisplaySize, string DisplayModified, string Attributes, string Status, bool IsNuked, string FullPath, bool IsDirectory);

    private enum TransferDirection { Download, Upload, UploadToLeft, DownloadFromLeft, RelayLeftToRight, RelayRightToLeft, ApiDownload, ApiFxp }
    private sealed record QuickSiteChoice(string Label, ConnectionProfile? Profile)
    {
        public override string ToString() => Label;
    }
    private sealed record QueueSnapshot(string Name, string Source, string Destination, TransferDirection Direction, string State, long BytesTransferred, long TotalBytes = 0, Guid Id = default, Guid? SourceProfileId = null, Guid? DestinationProfileId = null, DateTimeOffset? QueuedAt = null, DateTimeOffset? StartedAt = null);

    private sealed class QueueEntryView(string name, string source, string destination, TransferDirection direction, Guid? id = null, long totalBytes = 0) : INotifyPropertyChanged
    {
        private string _state = "Queued"; private long _bytesTransferred;
        public string Name { get; } = name; public string Source { get; } = source; public string Destination { get; } = destination;
        public TransferDirection Direction { get; } = direction;
        public Guid Id { get; } = id ?? Guid.NewGuid();
        private long _totalBytes = totalBytes;
        public long TotalBytes { get => _totalBytes; set { _totalBytes = value; Changed(); Changed(nameof(ProgressPercent)); Changed(nameof(ProgressDisplay)); } }
        private long _speedBytesPerSecond;
        public long SpeedBytesPerSecond { get => _speedBytesPerSecond; set { _speedBytesPerSecond = value; Changed(); } }
        public Guid? SourceProfileId { get; set; }
        public Guid? DestinationProfileId { get; set; }
        public DateTimeOffset? QueuedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTime LastPersistedAt { get; set; }
        public string State { get => _state; set { _state = value; Changed(); Changed(nameof(ProgressPercent)); Changed(nameof(ProgressDisplay)); } }
        public long BytesTransferred { get => _bytesTransferred; set { _bytesTransferred = value; Changed(); Changed(nameof(ProgressText)); Changed(nameof(ProgressPercent)); Changed(nameof(ProgressDisplay)); } }
        public string ProgressText => FormatSize(BytesTransferred);
        public double ProgressPercent => State == "Completed" ? 100 : TotalBytes > 0 ? Math.Clamp(BytesTransferred * 100d / TotalBytes, 0, 100) : 0;
        public string ProgressDisplay => TotalBytes > 0 ? $"{FormatSize(BytesTransferred)} / {FormatSize(TotalBytes)}  ({ProgressPercent:0}%)" : ProgressText;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private sealed class DesktopTransferExecutor(MainWindow owner) : ITransferExecutor
    {
        public async Task ExecuteAsync(TransferWorkItem item, CancellationToken cancellationToken) =>
            await await owner.Dispatcher.InvokeAsync(() => owner.ExecuteEngineItemAsync(item.Id, cancellationToken));
    }
}
