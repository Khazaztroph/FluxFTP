using System.Windows;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class BookmarkEditWindow : Window
{
    internal SiteBookmark? Bookmark { get; private set; }

    internal BookmarkEditWindow(IEnumerable<string> siteNames, SiteBookmark? bookmark = null)
    {
        InitializeComponent();
        SiteBox.ItemsSource = new[] { "(Local)" }.Concat(siteNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)).ToList();
        NameBox.Text = bookmark?.Name ?? "";
        PathBox.Text = bookmark?.Path ?? "";
        SiteBox.Text = string.IsNullOrWhiteSpace(bookmark?.SiteName) ? "(Local)" : bookmark.SiteName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var path = PathBox.Text.Trim();
        if (name.Length == 0 || path.Length == 0)
        { ErrorText.Text = "Name and path are required."; return; }
        var site = SiteBox.Text.Trim();
        if (site.Equals("(Local)", StringComparison.OrdinalIgnoreCase)) site = "";
        Bookmark = new SiteBookmark(name, path.Replace('\\', '/'), site);
        DialogResult = true;
    }
}
