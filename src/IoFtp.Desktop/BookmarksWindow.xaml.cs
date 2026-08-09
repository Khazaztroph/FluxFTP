using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class BookmarksWindow : Window
{
    private readonly ObservableCollection<BookmarkRow> _items;
    private readonly IReadOnlyList<string> _siteNames;

    internal BookmarksWindow(string initialSite = "", string initialPath = "")
    {
        InitializeComponent();
        _siteNames = new ProfileStore().Load().Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _items = new ObservableCollection<BookmarkRow>(new BookmarkStore().Load().Select(bookmark => new BookmarkRow(bookmark)));
        BookmarksGrid.ItemsSource = _items;
        if (!string.IsNullOrWhiteSpace(initialPath) && !_items.Any(item =>
                item.SiteName.Equals(initialSite, StringComparison.OrdinalIgnoreCase) && item.Path.Equals(initialPath, StringComparison.OrdinalIgnoreCase)))
            SummaryText.Text = $"Current path: {initialPath} — use Add to save it.";
        Tag = new SiteBookmark("", initialPath, initialSite);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var initial = Tag as SiteBookmark;
        var dialog = new BookmarkEditWindow(_siteNames, initial is { Path.Length: > 0 } ? initial : null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Bookmark is not null) _items.Add(new BookmarkRow(dialog.Bookmark));
        Tag = null;
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarksGrid.SelectedItem is not BookmarkRow selected) return;
        var dialog = new BookmarkEditWindow(_siteNames, selected.ToBookmark()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Bookmark is null) return;
        var index = _items.IndexOf(selected);
        _items[index] = new BookmarkRow(dialog.Bookmark);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in BookmarksGrid.SelectedItems.Cast<BookmarkRow>().ToList()) _items.Remove(item);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var legacy = Path.Combine(roaming, "FTPRush", "RushSite.xml");
        var modern = Path.Combine(documents, "FTPRush", "site.json");
        var preferred = File.Exists(legacy) ? legacy : modern;
        var picker = new OpenFileDialog
        {
            Title = "Import FTPRush bookmarks",
            Filter = "FTPRush sites and bookmarks (RushSite.xml;site.json)|RushSite.xml;site.json|XML files (*.xml)|*.xml|JSON files (*.json)|*.json",
            InitialDirectory = File.Exists(preferred) ? Path.GetDirectoryName(preferred) : documents,
            FileName = File.Exists(preferred) ? Path.GetFileName(preferred) : "RushSite.xml"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var imported = FtpRushSiteImporter.ImportPackage(picker.FileName).Bookmarks;
            var count = 0;
            foreach (var bookmark in imported)
            {
                var existing = _items.FirstOrDefault(item => item.SiteName.Equals(bookmark.SiteName, StringComparison.OrdinalIgnoreCase) &&
                    item.Name.Equals(bookmark.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) _items[_items.IndexOf(existing)] = new BookmarkRow(bookmark);
                else _items.Add(new BookmarkRow(bookmark));
                count++;
            }
            SummaryText.Text = $"Imported {count} bookmark(s) from {Path.GetFileName(picker.FileName)}.";
        }
        catch (Exception exception) { SummaryText.Text = $"Import failed: {exception.Message}"; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        new BookmarkStore().Save(_items.Select(item => item.ToBookmark()));
        DialogResult = true;
    }

    private void BookmarksGrid_DoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    private sealed record BookmarkRow(string Name, string Path, string SiteName)
    {
        public BookmarkRow(SiteBookmark bookmark) : this(bookmark.Name, bookmark.Path, bookmark.SiteName) { }
        public string SiteDisplay => string.IsNullOrWhiteSpace(SiteName) ? "Local" : SiteName;
        public SiteBookmark ToBookmark() => new(Name, Path, SiteName);
    }
}
