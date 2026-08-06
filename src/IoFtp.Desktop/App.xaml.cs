using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Threading;

namespace IoFtp.Desktop;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\FluxFTP.Desktop.SingleInstance";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var firstInstance);
        if (!firstInstance)
        {
            RestoreExistingInstance();
            Shutdown();
            return;
        }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyDarkChrome));
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void RestoreExistingInstance()
    {
        var handle = FindWindow(null, "FluxFTP");
        if (handle == IntPtr.Zero) return;
        ShowWindow(handle, 9); // SW_RESTORE also reveals a WPF window hidden in the tray.
        SetForegroundWindow(handle);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
