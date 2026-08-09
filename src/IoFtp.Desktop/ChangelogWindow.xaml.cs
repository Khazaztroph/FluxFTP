using System.Reflection;
using System.IO;
using System.Windows;

namespace IoFtp.Desktop;

public partial class ChangelogWindow : Window
{
    public ChangelogWindow()
    {
        InitializeComponent();
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));
        if (resource is null) { ChangelogText.Text="The changelog is not available in this build."; return; }
        using var stream = assembly.GetManifestResourceStream(resource);
        using var reader = new StreamReader(stream!);
        ChangelogText.Text = reader.ReadToEnd();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
