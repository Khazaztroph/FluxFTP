using System.Collections.ObjectModel;
using System.Windows;
using IoFtp.Core.Models;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class SiteManagerWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly ObservableCollection<ConnectionProfile> _profiles;
    public ConnectionProfile? SelectedProfile { get; private set; }

    public SiteManagerWindow()
    {
        InitializeComponent();
        _profiles = new ObservableCollection<ConnectionProfile>(_store.Load());
        SitesList.ItemsSource = _profiles;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Profile is not null) { _profiles.Add(dialog.Profile); Save(); SitesList.SelectedItem = dialog.Profile; }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SitesList.SelectedItem is not ConnectionProfile profile) return;
        var dialog = new ConnectionDialog(profile) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Profile is not null)
        {
            var index = _profiles.IndexOf(profile); _profiles[index] = dialog.Profile; Save(); SitesList.SelectedIndex = index;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SitesList.SelectedItem is not ConnectionProfile profile) return;
        if (MessageBox.Show($"Delete '{profile.Name}'?", "Site Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        { _profiles.Remove(profile); Save(); }
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        if (SitesList.SelectedItem is not ConnectionProfile profile) return;
        var dialog = new SiteOptionsWindow(profile) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Options is not null)
        { var index = _profiles.IndexOf(profile); _profiles[index] = profile with { Options = dialog.Options }; Save(); SitesList.SelectedIndex = index; }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (SitesList.SelectedItem is not ConnectionProfile profile) return;
        var dialog = new ConnectionDialog(profile, quickConnect: true) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Profile is not null)
        { SelectedProfile = dialog.Profile; DialogResult = true; }
    }

    private void Save() => _store.Save(_profiles);
}
