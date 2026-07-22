using System.Windows;
using IoFtp.Core.Models;
using IoFtp.Desktop.Models;
using IoFtp.Core.Transport;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class GlobalSettingsWindow : Window
{
    public GlobalSettings? Settings { get; private set; }
    public GlobalSettingsWindow(GlobalSettings s)
    {
        InitializeComponent(); ProtocolBox.ItemsSource = Enum.GetValues<TransferProtocol>().Select(protocol => new ProtocolChoice(protocol)).ToArray(); ProxyTypeBox.ItemsSource = Enum.GetValues<ProxyType>(); LegendModeBox.ItemsSource = new[] { "Scrolling", "Static", "Activity", "Compact", "Hidden" };
        BindBox.Text=s.BindAddress; PortFromBox.Text=$"{s.ActivePortFrom}"; PortToBox.Text=$"{s.ActivePortTo}"; ApiEnabledBox.IsChecked=s.EnableHttpsApi; ApiPortBox.Text=$"{s.HttpsApiPort}"; ApiLocalBox.IsChecked=s.ApiLocalhostOnly;
        ExpirationBox.Text=$"{s.PreparedJobExpirationSeconds}"; StarterBox.Text=$"{s.StarterTimeoutSeconds}"; RuntimeBox.Text=$"{s.MaxTransferRuntimeMinutes}"; JobHistoryBox.Text=$"{s.TransferJobHistory}"; TransferHistoryBox.Text=$"{s.TransferHistory}"; LogHistoryBox.Text=$"{s.LogBufferHistory}";
        UsernameBox.Text=s.DefaultUsername; ProtocolBox.SelectedItem=((ProtocolChoice[])ProtocolBox.ItemsSource).First(choice => choice.Protocol == s.DefaultProtocol); SlotsBox.Text=$"{s.DefaultSlots}"; UploadsBox.Text=$"{s.DefaultUploadSlots}"; DownloadsBox.Text=$"{s.DefaultDownloadSlots}"; DefaultIdleBox.Text=$"{s.DefaultIdleSeconds}";
        LocalPathBox.Text=s.LocalDownloadPath; LocalDownloadsBox.Text=$"{s.MaxLocalDownloadSlots}"; LocalUploadsBox.Text=$"{s.MaxLocalUploadSlots}";
        PriorityPatternsBox.Text=s.PriorityPatterns;
        SkipPatternsBox.Text=s.SkipPatterns;
        ApiPasswordBox.Password=s.ApiPassword;
        MinimizeToTrayBox.IsChecked=s.MinimizeToTray;
        LegendModeBox.SelectedItem=s.LegendBarMode; if (LegendModeBox.SelectedIndex < 0) LegendModeBox.SelectedItem="Compact";
        ProxyTypeBox.SelectedItem=s.ProxyType; ProxyHostBox.Text=s.ProxyHost; ProxyPortBox.Text=$"{s.ProxyPort}"; ProxyUsernameBox.Text=s.ProxyUsername; ProxyPasswordBox.Password=s.ProxyPassword; ProxyDnsBox.IsChecked=s.ProxyDns; ProxyDataBox.IsChecked=s.ProxyDataConnections;
        CheckUpdatesBox.IsChecked=s.CheckForUpdatesAtStartup;
        UpdateStatusText.Text=$"Installed version: {UpdateCheckService.CurrentVersion}";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var boxes = new[] { PortFromBox, PortToBox, ApiPortBox, ExpirationBox, StarterBox, RuntimeBox, JobHistoryBox, TransferHistoryBox, LogHistoryBox, SlotsBox, UploadsBox, DownloadsBox, DefaultIdleBox, LocalDownloadsBox, LocalUploadsBox, ProxyPortBox };
        if (boxes.Any(box => !int.TryParse(box.Text, out _))) { ErrorText.Text="All numeric settings must be whole numbers."; return; }
        int N(System.Windows.Controls.TextBox b)=>int.Parse(b.Text);
        if (N(PortFromBox) is <1 or >65535 || N(PortToBox)<N(PortFromBox) || N(SlotsBox)<1 || N(UploadsBox)<0 || N(DownloadsBox)<0 || N(UploadsBox)>N(SlotsBox) || N(DownloadsBox)>N(SlotsBox)) { ErrorText.Text="Port range or slot limits are invalid."; return; }
        if (ApiEnabledBox.IsChecked == true && string.IsNullOrWhiteSpace(ApiPasswordBox.Password)) { ErrorText.Text="API password is required when the API is enabled."; return; }
        if ((ProxyType)(ProxyTypeBox.SelectedItem ?? ProxyType.None) != ProxyType.None && (string.IsNullOrWhiteSpace(ProxyHostBox.Text) || N(ProxyPortBox) is < 1 or > 65535)) { ErrorText.Text="Proxy host or port is invalid."; return; }
        Settings = new(BindBox.Text.Trim(),N(PortFromBox),N(PortToBox),ApiEnabledBox.IsChecked==true,N(ApiPortBox),ApiLocalBox.IsChecked==true,N(ExpirationBox),N(StarterBox),N(RuntimeBox),N(JobHistoryBox),N(TransferHistoryBox),N(LogHistoryBox),UsernameBox.Text.Trim(),N(SlotsBox),N(UploadsBox),N(DownloadsBox),((ProtocolChoice)ProtocolBox.SelectedItem).Protocol,N(DefaultIdleBox),LocalPathBox.Text.Trim(),N(LocalDownloadsBox),N(LocalUploadsBox),PriorityPatternsBox.Text.Trim(),SkipPatternsBox.Text.Trim(),ApiPasswordBox.Password,MinimizeToTrayBox.IsChecked==true,LegendModeBox.SelectedItem?.ToString() ?? "Compact",(ProxyType)(ProxyTypeBox.SelectedItem ?? ProxyType.None),ProxyHostBox.Text.Trim(),N(ProxyPortBox),ProxyUsernameBox.Text.Trim(),ProxyPasswordBox.Password,ProxyDnsBox.IsChecked==true,ProxyDataBox.IsChecked==true,CheckUpdatesBox.IsChecked==true); DialogResult=true;
    }
    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ProxyPortBox.Text, out var port)) { ErrorText.Text="Proxy port is invalid."; return; }
        var proxy = new ProxyConfiguration((ProxyType)(ProxyTypeBox.SelectedItem ?? ProxyType.None), ProxyHostBox.Text.Trim(), port, ProxyUsernameBox.Text.Trim(), ProxyPasswordBox.Password, ProxyDnsBox.IsChecked==true, ProxyDataBox.IsChecked==true);
        if (proxy.Type == ProxyType.None) { ErrorText.Text="Select a proxy type first."; return; }
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var client = await ProxyConnector.ConnectAsync("example.com", 443, proxy, timeout.Token); ErrorText.Text="Proxy test succeeded."; }
        catch (Exception exception) { ErrorText.Text=$"Proxy test failed: {exception.Message}"; }
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text="Checking GitHub Releases…";
        var result = await new UpdateCheckService().CheckAsync(true);
        UpdateStatusText.Text = result.Error is not null && string.IsNullOrEmpty(result.LatestVersion)
            ? $"Update check failed: {result.Error}"
            : result.UpdateAvailable
                ? $"Update available: FluxFTP {result.LatestVersion}\n{result.ReleaseUrl}"
                : $"Latest version installed ({result.CurrentVersion}).";
    }
    private sealed record ProtocolChoice(TransferProtocol Protocol)
    {
        public override string ToString() => TransferProtocolNames.Display(Protocol);
    }
}
