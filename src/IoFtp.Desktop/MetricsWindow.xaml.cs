using System.Windows;
using System.Windows.Threading;

namespace IoFtp.Desktop;

public partial class MetricsWindow : Window
{
    private readonly Func<MetricsSnapshot> _snapshot;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal MetricsWindow(Func<MetricsSnapshot> snapshot)
    {
        InitializeComponent(); _snapshot = snapshot; _timer.Tick += (_, _) => RefreshMetrics();
        Loaded += (_, _) => { RefreshMetrics(); _timer.Start(); }; Closed += (_, _) => _timer.Stop();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshMetrics();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void RefreshMetrics()
    {
        var value = _snapshot(); SitesText.Text = $"{value.ConnectedSites}/{value.ConfiguredSites}"; ActiveText.Text = $"{value.ActiveJobs}";
        SpeedText.Text = value.TotalSpeed; BytesText.Text = value.Transferred; UpdatedText.Text = DateTime.Now.ToString("HH:mm:ss");
        MetricsList.ItemsSource = value.Rows;
    }
}

internal sealed record MetricRow(string Name, string Value, string Detail);
internal sealed record MetricsSnapshot(int ConnectedSites, int ConfiguredSites, int ActiveJobs, string TotalSpeed, string Transferred, IReadOnlyList<MetricRow> Rows);
