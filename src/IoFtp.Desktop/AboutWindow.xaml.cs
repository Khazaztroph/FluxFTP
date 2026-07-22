using System.Windows;
using IoFtp.Desktop.Services;

namespace IoFtp.Desktop;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {UpdateCheckService.CurrentVersion}";
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
