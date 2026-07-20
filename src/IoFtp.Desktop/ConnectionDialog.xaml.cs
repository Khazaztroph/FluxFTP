using System.Windows;
using System.Windows.Controls;
using IoFtp.Core.Models;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class ConnectionDialog : Window
{
    private readonly Guid _id;
    private readonly SiteOptions? _options;
    public ConnectionProfile? Profile { get; private set; }

    public ConnectionDialog(ConnectionProfile? profile = null, bool quickConnect = false)
    {
        InitializeComponent();
        Title = quickConnect ? "Quick Connect" : profile is null ? "New Site" : "Edit Site";
        AcceptButton.Content = quickConnect ? "Connect" : "Save";
        NameBox.IsEnabled = !quickConnect;
        ProtocolBox.ItemsSource = Enum.GetValues<TransferProtocol>();
        ListingModeBox.ItemsSource = new[]
        {
            new ListingModeOption(DirectoryListingMode.StatThenList, "STAT -l, then LIST"),
            new ListingModeOption(DirectoryListingMode.StatOnly, "STAT -l only"),
            new ListingModeOption(DirectoryListingMode.ListOnly, "LIST only")
        };
        _id = profile?.Id ?? Guid.NewGuid();
        _options = profile?.Options;
        if (profile is not null)
        {
            NameBox.Text = profile.Name;
            ProtocolBox.SelectedItem = profile.Protocol;
            HostBox.Text = profile.Host;
            PortBox.Text = profile.Port.ToString();
            UsernameBox.Text = profile.Username;
            PasswordBox.Password = profile.Password;
            RemotePathBox.Text = profile.EffectiveOptions.BasePath;
            AllowInvalidCertificateBox.IsChecked = profile.AllowInvalidCertificate;
            ListingModeBox.SelectedItem = ((ListingModeOption[])ListingModeBox.ItemsSource).First(option => option.Mode == profile.ListingMode);
        }
        else
        {
            var defaults = new GlobalSettingsStore().Load();
            NameBox.Text = quickConnect ? "Quick connection" : "New site";
            UsernameBox.Text = defaults.DefaultUsername;
            ProtocolBox.SelectedItem = defaults.DefaultProtocol;
            ListingModeBox.SelectedIndex = 0;
            RemotePathBox.Text = "/";
            _options = new SiteOptions(defaults.DefaultSlots, defaults.DefaultUploadSlots, defaults.DefaultDownloadSlots,
                MaxIdleSeconds: defaults.DefaultIdleSeconds);
        }
        Loaded += (_, _) => HostBox.Focus();
    }

    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolBox.SelectedItem is not TransferProtocol protocol)
            return;
        if (string.IsNullOrWhiteSpace(PortBox.Text) || PortBox.Text is "21" or "22" or "990")
            PortBox.Text = protocol switch { TransferProtocol.Sftp => "22", TransferProtocol.FtpsImplicit => "990", _ => "21" };
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(host))
        {
            ErrorText.Text = "Name and host are required.";
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ErrorText.Text = "Port must be a number between 1 and 65535.";
            return;
        }
        var listingMode = ((ListingModeOption)ListingModeBox.SelectedItem).Mode;
        var remotePath = RemotePathBox.Text.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(remotePath)) remotePath = "/";
        if (!remotePath.StartsWith('/')) remotePath = "/" + remotePath;
        var options = (_options ?? new SiteOptions()) with { BasePath = remotePath };
        Profile = new ConnectionProfile(_id, name, host, port, UsernameBox.Text.Trim(), (TransferProtocol)ProtocolBox.SelectedItem, PasswordBox.Password, AllowInvalidCertificateBox.IsChecked == true, listingMode, options);
        DialogResult = true;
    }

    private sealed record ListingModeOption(DirectoryListingMode Mode, string Label)
    {
        public override string ToString() => Label;
    }
}
