using System.Collections.ObjectModel;
using System.Windows;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class SectionsWindow : Window
{
    private readonly SectionStore _store = new();
    private readonly ObservableCollection<SectionRow> _rows = [];

    public SectionsWindow()
    {
        InitializeComponent();
        ValidationModeColumn.ItemsSource = Enum.GetValues<SectionValidationMode>();
        foreach (var section in _store.Load())
            foreach (var site in section.SitePaths.DefaultIfEmpty(new KeyValuePair<string, string>("ioFTPD", "/")))
                _rows.Add(new(section.Name, site.Key, site.Value, section.Hotkey, section.AllowPatterns, section.DenyPatterns, section.ValidationMode));
        SectionsGrid.ItemsSource = _rows;
    }

    private void Add_Click(object sender, RoutedEventArgs e) { var row = new SectionRow("New section", "ioFTPD", "/", 0, "", "", SectionValidationMode.Disabled); _rows.Add(row); SectionsGrid.SelectedItem = row; SectionsGrid.ScrollIntoView(row); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (SectionsGrid.SelectedItem is SectionRow row) _rows.Remove(row); }
    private void TestPrecheck_Click(object sender, RoutedEventArgs e)
    {
        SectionsGrid.CommitEdit(); SectionsGrid.CommitEdit();
        if (SectionsGrid.SelectedItem is not SectionRow row) { MessageBox.Show("Select a section first.", "Section precheck"); return; }
        _store.Save(_rows.Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Site))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SectionDefinition(group.Key,
                group.GroupBy(item => item.Site.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(site => site.Key, site => site.Last().Path.Trim(), StringComparer.OrdinalIgnoreCase),
                group.First().Hotkey, group.First().AllowPatterns.Trim(), group.First().DenyPatterns.Trim(), group.First().ValidationMode)));
        var prompt = new CommandParameterWindow("Release name to validate") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var result = SectionReleaseValidator.Validate(row.Name, prompt.Value.Trim());
        MessageBox.Show(result.Message, result.Accepted ? "Precheck passed" : $"Precheck {result.Mode}",
            MessageBoxButton.OK, result.Accepted ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SectionsGrid.CommitEdit(); SectionsGrid.CommitEdit();
        var sections = _rows.Where(row => !string.IsNullOrWhiteSpace(row.Name) && !string.IsNullOrWhiteSpace(row.Site))
            .GroupBy(row => row.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SectionDefinition(group.Key,
                group.GroupBy(row => row.Site.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(site => site.Key, site => site.Last().Path.Trim(), StringComparer.OrdinalIgnoreCase),
                group.First().Hotkey, group.First().AllowPatterns.Trim(), group.First().DenyPatterns.Trim(), group.First().ValidationMode)).ToList();
        _store.Save(sections);
        MessageBox.Show("Sections saved.", "Sections", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private sealed class SectionRow(string name, string site, string path, int hotkey, string allowPatterns, string denyPatterns, SectionValidationMode validationMode)
    {
        public string Name { get; set; } = name; public string Site { get; set; } = site;
        public string Path { get; set; } = path; public int Hotkey { get; set; } = hotkey;
        public string AllowPatterns { get; set; } = allowPatterns; public string DenyPatterns { get; set; } = denyPatterns;
        public SectionValidationMode ValidationMode { get; set; } = validationMode;
    }
}
