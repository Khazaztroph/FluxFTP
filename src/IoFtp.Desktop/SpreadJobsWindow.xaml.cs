using System.Collections.ObjectModel;
using System.Windows;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class SpreadJobsWindow : Window
{
    private readonly SpreadJobStore _store = new();
    private readonly SpreadPresetStore _presetStore = new();
    private readonly ObservableCollection<SpreadJobRow> _rows = [];
    private readonly ObservableCollection<SpreadPresetDefinition> _presets = [];
    public SpreadJobsWindow()
    {
        InitializeComponent();
        SectionColumn.ItemsSource = new SectionStore().Load().Select(section => section.Name).ToList();
        SourceColumn.ItemsSource = new ProfileStore().Load().Select(site => site.Name).ToList();
        foreach (var job in _store.Load()) _rows.Add(new(job.Name, job.Section, job.SourceSite, job.TargetSites, job.Enabled));
        foreach (var preset in _presetStore.Load()) _presets.Add(preset);
        JobsGrid.ItemsSource = _rows;
        PresetBox.ItemsSource = _presets;
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var section = SectionColumn.ItemsSource.Cast<string>().FirstOrDefault() ?? "";
        var site = SourceColumn.ItemsSource.Cast<string>().FirstOrDefault() ?? "";
        var row = new SpreadJobRow("New spread job", section, site, "", true); _rows.Add(row); JobsGrid.SelectedItem = row; JobsGrid.ScrollIntoView(row);
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (JobsGrid.SelectedItem is SpreadJobRow row) _rows.Remove(row); }
    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not SpreadPresetDefinition preset) return;
        var row = JobsGrid.SelectedItem as SpreadJobRow;
        if (row is null)
        {
            row = new SpreadJobRow(preset.Name, preset.Section, preset.SourceSite, preset.TargetSites, true);
            _rows.Add(row);
        }
        else
        {
            row.Section = preset.Section; row.SourceSite = preset.SourceSite; row.TargetSites = preset.TargetSites;
            JobsGrid.Items.Refresh();
        }
        JobsGrid.SelectedItem = row; JobsGrid.ScrollIntoView(row);
    }
    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        JobsGrid.CommitEdit(); JobsGrid.CommitEdit();
        if (JobsGrid.SelectedItem is not SpreadJobRow row) { MessageBox.Show("Select a spread job first.", "Spread Presets"); return; }
        var name = PresetBox.Text.Trim();
        if (name.Length == 0) { MessageBox.Show("Enter a preset name.", "Spread Presets"); return; }
        var preset = new SpreadPresetDefinition(name, row.Section, row.SourceSite, row.TargetSites);
        var index = _presets.ToList().FindIndex(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _presets[index] = preset; else _presets.Add(preset);
        _presetStore.Save(_presets); PresetBox.SelectedItem = preset;
    }
    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetBox.SelectedItem is not SpreadPresetDefinition preset) return;
        _presets.Remove(preset); _presetStore.Save(_presets); PresetBox.SelectedIndex = -1; PresetBox.Text = "";
    }
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
