using System.Collections.ObjectModel;
using System.Windows;
using IoFtp.Desktop.Services;
using Microsoft.Win32;

namespace IoFtp.Desktop;

public partial class ExternalScriptsWindow : Window
{
    private readonly ExternalScriptStore _store = new(); private readonly ObservableCollection<ScriptRow> _rows = [];
    public ExternalScriptsWindow()
    {
        InitializeComponent(); EventColumn.ItemsSource = ScriptEvents.All;
        foreach (var item in _store.Load()) _rows.Add(new(item)); ScriptsGrid.ItemsSource = _rows;
    }
    private void Add_Click(object sender, RoutedEventArgs e) { var row = new ScriptRow(new(Guid.NewGuid(), "New script", "Manual", "")); _rows.Add(row); ScriptsGrid.SelectedItem = row; }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (ScriptsGrid.SelectedItem is ScriptRow row) _rows.Remove(row); }
    private void Browse_Click(object sender, RoutedEventArgs e) { if (ScriptsGrid.SelectedItem is not ScriptRow row) return; var picker = new OpenFileDialog { Filter = "Scripts and programs|*.ps1;*.cmd;*.bat;*.exe|All files|*.*" }; if (picker.ShowDialog(this) == true) { row.FileName = picker.FileName; ScriptsGrid.Items.Refresh(); } }
    private void Save_Click(object sender, RoutedEventArgs e) { ScriptsGrid.CommitEdit(); ScriptsGrid.CommitEdit(); _store.Save(_rows.Select(row => row.ToDefinition())); OutputBox.AppendText("Scripts saved.\n"); }
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (ScriptsGrid.SelectedItem is not ScriptRow row) return; ScriptsGrid.CommitEdit(); ScriptsGrid.CommitEdit();
        var result = await new ExternalScriptRunner().RunAsync(row.ToDefinition(), new Dictionary<string, string> { ["site"]="Manual", ["name"]="Manual", ["status"]="Manual" });
        OutputBox.AppendText($"[{result.Name}] exit {result.ExitCode}\n{result.Output}{result.Error}\n"); OutputBox.ScrollToEnd();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private sealed class ScriptRow(ExternalScriptDefinition item)
    {
        public Guid Id { get; set; }=item.Id; public string Name { get; set; }=item.Name; public string Event { get; set; }=item.Event; public string FileName { get; set; }=item.FileName;
        public string Arguments { get; set; }=item.Arguments; public string WorkingDirectory { get; set; }=item.WorkingDirectory; public int TimeoutSeconds { get; set; }=item.TimeoutSeconds;
        public bool Enabled { get; set; }=item.Enabled; public bool BlockOnFailure { get; set; }=item.BlockOnFailure;
        public ExternalScriptDefinition ToDefinition() => new(Id,Name,Event,FileName,Arguments,WorkingDirectory,Math.Clamp(TimeoutSeconds,1,3600),Enabled,BlockOnFailure);
    }
}
