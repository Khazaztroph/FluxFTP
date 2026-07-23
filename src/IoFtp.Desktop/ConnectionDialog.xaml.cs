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
        DescriptionBox.IsEnabled = !quickConnect;
        ProtocolBox.ItemsSource = Enum.GetValues<TransferProtocol>().Select(protocol => new ProtocolChoice(protocol)).ToArray();
        ListingModeBox.ItemsSource = new[]
        {
            new ListingModeOption(DirectoryListingMode.Auto, "Auto (MLSD, LIST, STAT -l)"),
            new ListingModeOption(DirectoryListingMode.ListOnly, "LIST only"),
            new ListingModeOption(DirectoryListingMode.StatThenList, "STAT -l, then LIST"),
            new ListingModeOption(DirectoryListingMode.StatOnly, "STAT -l only"),
        };
        _id = profile?.Id ?? Guid.NewGuid();
        _options = profile?.Options;
        if (profile is not null)
        {
            NameBox.Text = profile.Name;
            DescriptionBox.Text = profile.Description;
            ProtocolBox.SelectedItem = ((ProtocolChoice[])ProtocolBox.ItemsSource).First(choice => choice.Protocol == profile.Protocol);
            HostBox.Text = string.Join(' ', profile.EffectiveAddresses.Select(address => address.ToString()));
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
            ProtocolBox.SelectedItem = ((ProtocolChoice[])ProtocolBox.ItemsSource).First(choice => choice.Protocol == defaults.DefaultProtocol);
            ListingModeBox.SelectedIndex = 0;
            RemotePathBox.Text = "/";
            _options = new SiteOptions(defaults.DefaultSlots, defaults.DefaultUploadSlots, defaults.DefaultDownloadSlots,
                MaxIdleSeconds: defaults.DefaultIdleSeconds);
        }
        Loaded += (_, _) => HostBox.Focus();
    }

    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolBox.SelectedItem is not ProtocolChoice choice)
            return;
        var protocol = choice.Protocol;
        if (string.IsNullOrWhiteSpace(PortBox.Text) || PortBox.Text is "21" or "22" or "990")
            PortBox.Text = protocol switch { TransferProtocol.Sftp => "22", TransferProtocol.FtpsImplicit => "990", _ => "21" };
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var addressText = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(addressText))
        {
            ErrorText.Text = "Name and host are required.";
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ErrorText.Text = "Port must be a number between 1 and 65535.";
            return;
        }
        var addresses = new List<SiteEndpoint>();
        foreach (var token in addressText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SiteEndpoint.TryParse(token, port, out var endpoint))
            { ErrorText.Text = $"Invalid site address: {token}"; return; }
            if (!addresses.Contains(endpoint)) addresses.Add(endpoint);
        }
        if (addresses.Count == 0) { ErrorText.Text = "At least one site address is required."; return; }
        var listingMode = ((ListingModeOption)ListingModeBox.SelectedItem).Mode;
        var remotePath = RemotePathBox.Text.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(remotePath)) remotePath = "/";
        if (!remotePath.StartsWith('/')) remotePath = "/" + remotePath;
        var options = (_options ?? new SiteOptions()) with { BasePath = remotePath };
        var primary = addresses[0];
        Profile = new ConnectionProfile(_id, name, primary.Host, primary.Port, UsernameBox.Text.Trim(), ((ProtocolChoice)ProtocolBox.SelectedItem).Protocol,
            PasswordBox.Password, AllowInvalidCertificateBox.IsChecked == true, listingMode, options,
            AlternateAddresses: string.Join(' ', addresses.Skip(1).Select(address => address.ToString())), Description: DescriptionBox.Text.Trim());
        DialogResult = true;
    }

    private sealed record ListingModeOption(DirectoryListingMode Mode, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record ProtocolChoice(TransferProtocol Protocol)
    {
        public override string ToString() => TransferProtocolNames.Display(Protocol);
    }
}
