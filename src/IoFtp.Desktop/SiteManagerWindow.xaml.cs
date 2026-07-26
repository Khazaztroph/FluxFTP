using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using IoFtp.Core.Models;
using IoFtp.Desktop.Services;
using Microsoft.Win32;

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
        if (dialog.ShowDialog() == true && dialog.Profile is not null)
        {
            if (DescriptionConflict(dialog.Profile)) return;
            _profiles.Add(dialog.Profile); Save(); SitesList.SelectedItem = dialog.Profile;
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SitesList.SelectedItem is not ConnectionProfile profile) return;
        var dialog = new ConnectionDialog(profile) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Profile is not null)
        {
            if (DescriptionConflict(dialog.Profile, profile.Id)) return;
            var index = _profiles.IndexOf(profile); _profiles[index] = dialog.Profile; Save(); SitesList.SelectedIndex = index;
        }
    }

    private bool DescriptionConflict(ConnectionProfile profile, Guid? exceptId = null)
    {
        var others = _profiles.Where(item => item.Id != exceptId).ToList();
        var conflict = !string.IsNullOrWhiteSpace(profile.Description) && others.Any(item =>
            item.Description.Equals(profile.Description, StringComparison.OrdinalIgnoreCase) ||
            item.Name.Equals(profile.Description, StringComparison.OrdinalIgnoreCase)) ||
            others.Any(item => !string.IsNullOrWhiteSpace(item.Description) && item.Description.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (!conflict) return false;
        MessageBox.Show("Site names and descriptions must be unique and cannot overlap.",
            "Site Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
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

    private void ImportFtpRush_Click(object sender, RoutedEventArgs e)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var defaultFile = Path.Combine(documents, "FTPRush", "site.json");
        var legacyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FTPRush", "RushSite.xml");
        if (!File.Exists(defaultFile) && File.Exists(legacyFile)) defaultFile = legacyFile;
        var picker = new OpenFileDialog
        {
            Title = "Import FTPRush sites",
            Filter = "FTPRush sites (site.json;RushSite.xml)|site.json;RushSite.xml|JSON files (*.json)|*.json|Legacy FTPRush XML (*.xml)|*.xml",
            InitialDirectory = File.Exists(defaultFile) ? Path.GetDirectoryName(defaultFile) : documents,
            FileName = File.Exists(defaultFile) ? Path.GetFileName(defaultFile) : "site.json"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var imported = FtpRushSiteImporter.ImportPackage(picker.FileName);
            var dialog = new FtpRushImportWindow("FTPRush", imported.Sites, _profiles, imported.Bookmarks) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var importedSites = 0;
            foreach (var profile in dialog.SelectedProfiles)
            {
                var index = _profiles.Select((existing, position) => (existing, position))
                    .Where(item => IsSameSite(item.existing, profile))
                    .Select(item => item.position).DefaultIfEmpty(-1).First();
                if (index >= 0)
                {
                    if (!dialog.ReplaceExisting) continue;
                    _profiles[index] = profile with { Id = _profiles[index].Id };
                }
                else _profiles.Add(profile);
                importedSites++;
            }
            var bookmarkStore = new BookmarkStore();
            var bookmarks = bookmarkStore.Load();
            var importedBookmarks = 0;
            foreach (var bookmark in dialog.SelectedBookmarks)
            {
                var index = bookmarks.FindIndex(existing =>
                    existing.SiteName.Equals(bookmark.SiteName, StringComparison.OrdinalIgnoreCase) &&
                    existing.Name.Equals(bookmark.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    if (!dialog.ReplaceExisting) continue;
                    bookmarks[index] = bookmark;
                }
                else bookmarks.Add(bookmark);
                importedBookmarks++;
            }
            Save();
            bookmarkStore.Save(bookmarks);
            MessageBox.Show($"Imported {importedSites} FTPRush site(s) and {importedBookmarks} bookmark(s). Passwords are now protected with Windows DPAPI.",
                "FTPRush Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"FTPRush import failed: {exception.Message}", "FTPRush Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportFlashFxp_Click(object sender, RoutedEventArgs e)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var defaultFile = Path.Combine(desktop, "FlashFXP Sites.ftp");
        var picker = new OpenFileDialog
        {
            Title = "Import FlashFXP sites",
            Filter = "FlashFXP site exports (*.ftp)|*.ftp|XML files (*.xml)|*.xml",
            InitialDirectory = desktop,
            FileName = File.Exists(defaultFile) ? Path.GetFileName(defaultFile) : ""
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var imported = FlashFxpSiteImporter.Import(picker.FileName);
            var dialog = new FtpRushImportWindow("FlashFXP", imported, _profiles) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            foreach (var profile in dialog.SelectedProfiles) _profiles.Add(profile);
            Save();
            MessageBox.Show($"Imported {dialog.SelectedProfiles.Count} FlashFXP site(s). Passwords are now protected with Windows DPAPI.",
                "FlashFXP Import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show($"FlashFXP import failed: {exception.Message}", "FlashFXP Import", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save() => _store.Save(_profiles);

    private static bool IsSameSite(ConnectionProfile left, ConnectionProfile right) =>
        left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) ||
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port &&
        left.Username.Equals(right.Username, StringComparison.OrdinalIgnoreCase);
}
