using System.Collections.ObjectModel;
using System.Windows;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class SpreadJobsWindow : Window
{
    private readonly SpreadJobStore _store = new();
    private readonly ObservableCollection<SpreadJobRow> _rows = [];
    public SpreadJobsWindow()
    {
        InitializeComponent();
        SectionColumn.ItemsSource = new SectionStore().Load().Select(section => section.Name).ToList();
        SourceColumn.ItemsSource = new ProfileStore().Load().Select(site => site.Name).ToList();
        foreach (var job in _store.Load()) _rows.Add(new(job.Name, job.Section, job.SourceSite, job.TargetSites, job.Enabled));
        JobsGrid.ItemsSource = _rows;
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var section = SectionColumn.ItemsSource.Cast<string>().FirstOrDefault() ?? "";
        var site = SourceColumn.ItemsSource.Cast<string>().FirstOrDefault() ?? "";
        var row = new SpreadJobRow("New spread job", section, site, "", true); _rows.Add(row); JobsGrid.SelectedItem = row; JobsGrid.ScrollIntoView(row);
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (JobsGrid.SelectedItem is SpreadJobRow row) _rows.Remove(row); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        JobsGrid.CommitEdit(); JobsGrid.CommitEdit();
        _store.Save(_rows.Where(row => !string.IsNullOrWhiteSpace(row.Name)).Select(row => new SpreadJobDefinition(row.Name.Trim(), row.Section, row.SourceSite, row.TargetSites, row.Enabled)));
        MessageBox.Show("Spread jobs saved.", "Spread Jobs", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private sealed class SpreadJobRow(string name, string section, string sourceSite, string targetSites, bool enabled)
    {
        public string Name { get; set; } = name; public string Section { get; set; } = section;
        public string SourceSite { get; set; } = sourceSite; public string TargetSites { get; set; } = targetSites;
        public bool Enabled { get; set; } = enabled;
    }
}
