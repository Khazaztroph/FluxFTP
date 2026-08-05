using System.Windows;
using IoFtp.Core.Models;

namespace IoFtp.Desktop;

public partial class SiteOptionsWindow : Window
{
    public SiteOptions? Options { get; private set; }
    public ProxyConfiguration? SiteProxy { get; private set; }

    public SiteOptionsWindow(ConnectionProfile profile)
    {
        InitializeComponent();
        SiteSummary.Text = $"{profile.Name}   {TransferProtocolNames.Display(profile.Protocol)}   {profile.Host}:{profile.Port}";
        var options = profile.EffectiveOptions;
        MaxSlotsBox.Text = options.MaxSlots.ToString(); UploadSlotsBox.Text = options.MaxUploadSlots.ToString(); DownloadSlotsBox.Text = options.MaxDownloadSlots.ToString();
        PriorityBox.Text = options.Priority.ToString(); AllowUploadBox.IsChecked = options.AllowUpload; AllowDownloadBox.IsChecked = options.AllowDownload;
        StayLoggedInBox.IsChecked = options.StayLoggedIn; BasePathBox.Text = options.BasePath; TlsTransfersBox.IsChecked = options.PreferTlsTransfers;
        BinaryModeBox.IsChecked = options.ForceBinaryMode; IdleBox.Text = options.MaxIdleSeconds.ToString();
        SecureListingsBox.IsChecked = options.SecureFileListings;
        NeedsPretBox.IsChecked = options.NeedsPret;
        CeprBox.IsChecked = options.CeprSupported;
        XdupeBox.IsChecked = options.UseXdupe;
        FxpProtectionBox.ItemsSource = new[]
        {
            new FxpProtectionChoice(FxpProtectionMode.AutoSecure, "Auto — secure FXP (TLS)"),
            new FxpProtectionChoice(FxpProtectionMode.Clear, "Clear / plain FTP — no FXP data TLS")
        };
        FxpProtectionBox.DisplayMemberPath = nameof(FxpProtectionChoice.Label);
        FxpProtectionBox.SelectedItem = ((FxpProtectionChoice[])FxpProtectionBox.ItemsSource)
            .First(item => item.Mode == options.FxpProtection);
        FxpDataRoleBox.ItemsSource = new[]
        {
            new FxpDataRoleChoice(FxpDataRole.Auto, "Auto — detect and remember"),
            new FxpDataRoleChoice(FxpDataRole.Passive, "PASV — passive server"),
            new FxpDataRoleChoice(FxpDataRole.Active, "PORT — active server (CGNAT)")
        };
        FxpDataRoleBox.DisplayMemberPath = nameof(FxpDataRoleChoice.Label);
        FxpDataRoleBox.SelectedItem = ((FxpDataRoleChoice[])FxpDataRoleBox.ItemsSource)
            .First(item => item.Role == options.FxpDataRole);
        BrokenPasvBox.IsChecked = options.FxpDataRole == FxpDataRole.Active;
        BlockFromBox.Text = options.BlockTransfersFrom; BlockToBox.Text = options.BlockTransfersTo;
        AffilsBox.Text = options.Affils;
        ProxyModeBox.ItemsSource = new[]
        {
            new ProxyModeChoice(null, "Use global proxy"),
            new ProxyModeChoice(ProxyType.None, "No proxy — direct connection"),
            new ProxyModeChoice(ProxyType.Socks5, "SOCKS5"),
            new ProxyModeChoice(ProxyType.Socks4, "SOCKS4"),
            new ProxyModeChoice(ProxyType.HttpConnect, "HTTP CONNECT")
        };
        ProxyModeBox.DisplayMemberPath = nameof(ProxyModeChoice.Label);
        ProxyModeBox.SelectedItem = ((ProxyModeChoice[])ProxyModeBox.ItemsSource).First(item => item.Type == profile.Proxy?.Type);
        ProxyHostBox.Text = profile.Proxy?.Host ?? "";
        ProxyPortBox.Text = (profile.Proxy?.Port is > 0 ? profile.Proxy.Port : 1080).ToString();
        ProxyUsernameBox.Text = profile.Proxy?.Username ?? "";
        ProxyPasswordBox.Password = profile.Proxy?.Password ?? "";
        ProxyDnsBox.IsChecked = profile.Proxy?.ProxyDns ?? true;
        ProxyDataBox.IsChecked = profile.Proxy?.UseForData ?? true;
        UpdateProxyFields();
    }

    private void BrokenPasvBox_Click(object sender, RoutedEventArgs e)
    {
        var role = BrokenPasvBox.IsChecked == true ? FxpDataRole.Active : FxpDataRole.Auto;
        FxpDataRoleBox.SelectedItem = ((FxpDataRoleChoice[])FxpDataRoleBox.ItemsSource)
            .First(item => item.Role == role);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositive(MaxSlotsBox.Text, out var slots) || !TryNonNegative(UploadSlotsBox.Text, out var uploads) ||
            !TryNonNegative(DownloadSlotsBox.Text, out var downloads) || !int.TryParse(PriorityBox.Text, out var priority) ||
            !TryPositive(IdleBox.Text, out var idle))
        { ErrorText.Text = "Slot limits and idle time must be valid whole numbers."; return; }
        if (uploads > slots || downloads > slots)
        { ErrorText.Text = "Upload and download slot limits cannot exceed total slots."; return; }
        var proxyType = (ProxyModeBox.SelectedItem as ProxyModeChoice)?.Type;
        if (proxyType is not null and not ProxyType.None &&
            (string.IsNullOrWhiteSpace(ProxyHostBox.Text) || !int.TryParse(ProxyPortBox.Text, out var proxyPort) || proxyPort is < 1 or > 65535))
        { ErrorText.Text = "Site proxy host or port is invalid."; return; }
        Options = new SiteOptions(slots, uploads, downloads, priority, AllowUploadBox.IsChecked == true, AllowDownloadBox.IsChecked == true,
            StayLoggedInBox.IsChecked == true, string.IsNullOrWhiteSpace(BasePathBox.Text) ? "/" : BasePathBox.Text.Trim(),
            TlsTransfersBox.IsChecked == true, BinaryModeBox.IsChecked == true, idle, BlockFromBox.Text.Trim(), BlockToBox.Text.Trim(), SecureListingsBox.IsChecked == true,
            NeedsPretBox.IsChecked == true, CeprBox.IsChecked == true, XdupeBox.IsChecked == true, AffilsBox.Text.Trim(),
            (FxpProtectionBox.SelectedItem as FxpProtectionChoice)?.Mode ?? FxpProtectionMode.AutoSecure,
            BrokenPasvBox.IsChecked == true
                ? FxpDataRole.Active
                : (FxpDataRoleBox.SelectedItem as FxpDataRoleChoice)?.Role ?? FxpDataRole.Auto);
        SiteProxy = proxyType switch
        {
            null => null,
            ProxyType.None => new ProxyConfiguration(ProxyType.None),
            _ => new ProxyConfiguration(proxyType.Value, ProxyHostBox.Text.Trim(), int.Parse(ProxyPortBox.Text),
                ProxyUsernameBox.Text.Trim(), ProxyPasswordBox.Password, ProxyDnsBox.IsChecked == true, ProxyDataBox.IsChecked == true)
        };
        DialogResult = true;
    }

    private void ProxyMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateProxyFields();

    private void UpdateProxyFields()
    {
        if (ProxyHostBox is null) return;
        var custom = (ProxyModeBox.SelectedItem as ProxyModeChoice)?.Type is not null and not ProxyType.None;
        ProxyHostBox.IsEnabled = custom; ProxyPortBox.IsEnabled = custom; ProxyUsernameBox.IsEnabled = custom;
        ProxyPasswordBox.IsEnabled = custom; ProxyDnsBox.IsEnabled = custom; ProxyDataBox.IsEnabled = custom;
    }

    private static bool TryPositive(string text, out int value) => int.TryParse(text, out value) && value > 0;
    private static bool TryNonNegative(string text, out int value) => int.TryParse(text, out value) && value >= 0;
    private sealed record FxpProtectionChoice(FxpProtectionMode Mode, string Label);
    private sealed record FxpDataRoleChoice(FxpDataRole Role, string Label);
    private sealed record ProxyModeChoice(ProxyType? Type, string Label);
}
