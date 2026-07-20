using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IoFtp.Desktop;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyDarkChrome));
        base.OnStartup(e);
    }

    private static void ApplyDarkChrome(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = 1;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
