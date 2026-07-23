using System.Windows;
using IoFtp.Core.Models;

namespace IoFtp.Desktop;

public partial class SiteOptionsWindow : Window
{
    public SiteOptions? Options { get; private set; }

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
        BlockFromBox.Text = options.BlockTransfersFrom; BlockToBox.Text = options.BlockTransfersTo;
        AffilsBox.Text = options.Affils;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPositive(MaxSlotsBox.Text, out var slots) || !TryNonNegative(UploadSlotsBox.Text, out var uploads) ||
            !TryNonNegative(DownloadSlotsBox.Text, out var downloads) || !int.TryParse(PriorityBox.Text, out var priority) ||
            !TryPositive(IdleBox.Text, out var idle))
        { ErrorText.Text = "Slot limits and idle time must be valid whole numbers."; return; }
        if (uploads > slots || downloads > slots)
        { ErrorText.Text = "Upload and download slot limits cannot exceed total slots."; return; }
        Options = new SiteOptions(slots, uploads, downloads, priority, AllowUploadBox.IsChecked == true, AllowDownloadBox.IsChecked == true,
            StayLoggedInBox.IsChecked == true, string.IsNullOrWhiteSpace(BasePathBox.Text) ? "/" : BasePathBox.Text.Trim(),
            TlsTransfersBox.IsChecked == true, BinaryModeBox.IsChecked == true, idle, BlockFromBox.Text.Trim(), BlockToBox.Text.Trim(), SecureListingsBox.IsChecked == true,
            NeedsPretBox.IsChecked == true, CeprBox.IsChecked == true, XdupeBox.IsChecked == true, AffilsBox.Text.Trim());
        DialogResult = true;
    }

    private static bool TryPositive(string text, out int value) => int.TryParse(text, out value) && value > 0;
    private static bool TryNonNegative(string text, out int value) => int.TryParse(text, out value) && value >= 0;
}
