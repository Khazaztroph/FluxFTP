using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using IoFtp.Core.Models;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class FtpRushImportWindow : Window
{
    private readonly List<ImportRow> _rows;
    public IReadOnlyList<ConnectionProfile> SelectedProfiles { get; private set; } = [];

    internal FtpRushImportWindow(string sourceName, IReadOnlyList<FtpRushImportedSite> sites, IReadOnlyCollection<ConnectionProfile> existing)
    {
        InitializeComponent();
        Title = $"Import {sourceName} Sites";
        HeadingText.Text = $"IMPORT {sourceName.ToUpperInvariant()} SITES";
        _rows = sites.Select(site => new ImportRow(site,
            existing.Any(item => item.Name.Equals(site.Profile.Name, StringComparison.OrdinalIgnoreCase) ||
                (item.Host.Equals(site.Profile.Host, StringComparison.OrdinalIgnoreCase) && item.Port == site.Profile.Port && item.Username.Equals(site.Profile.Username, StringComparison.OrdinalIgnoreCase)))))
            .ToList();
        SitesGrid.ItemsSource = _rows;
        SummaryText.Text = $"{_rows.Count} compatible {sourceName} site(s) found. Existing duplicates are unselected.";
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SitesGrid.CommitEdit();
        SelectedProfiles = _rows.Where(row => row.Selected).Select(row => row.Site.Profile).ToList();
        if (SelectedProfiles.Count == 0) { SummaryText.Text = "Select at least one non-duplicate site to import."; return; }
        DialogResult = true;
    }

    private sealed class ImportRow(FtpRushImportedSite site, bool duplicate) : INotifyPropertyChanged
    {
        private bool _selected = !duplicate;
        public FtpRushImportedSite Site { get; } = site;
        public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public string Name => Site.Profile.Name;
        public string GroupPath => Site.GroupPath;
        public string Protocol => TransferProtocolNames.Display(Site.Profile.Protocol);
        public string Host => Site.Profile.Host;
        public int Port => Site.Profile.Port;
        public string RemotePath => Site.Profile.EffectiveOptions.BasePath;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
