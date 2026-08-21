using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;
using PokeTokenBar.ViewModels;

namespace PokeTokenBar.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _ = vm.ReloadSpriteAsync();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    /// <summary>
    /// Position the window near the system tray (bottom-right).
    /// </summary>
    public void ShowNearTray()
    {
        // Force a real layout pass so ActualHeight reflects the true content
        // size even on the very first call — Measure alone leaves it at 0,
        // since only Arrange populates it, which pushed the window mostly
        // off-screen on first open.
        Measure(new Size(Width, double.PositiveInfinity));
        Arrange(new Rect(DesiredSize));

        // Anchor to the monitor under the cursor (where the tray icon was
        // actually clicked), not SystemParameters.WorkArea — that's always
        // the *primary* display, which leaves the popup rendered off on a
        // monitor the user isn't looking at on multi-monitor setups.
        var workArea = NativeMethods.GetWorkAreaUnderCursor();
        var dpi = VisualTreeHelper.GetDpi(this);

        Left = workArea.Right  / dpi.DpiScaleX - Width - 16;
        Top  = workArea.Bottom / dpi.DpiScaleY - ActualHeight - 16;

        Show();
        Activate();
    }
}

// ── Multi-monitor work-area lookup ─────────────────────────────────────────────

file static class NativeMethods
{
    public static Rect GetWorkAreaUnderCursor()
    {
        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var wa = info.rcWork;
                return new Rect(wa.Left, wa.Top, wa.Right - wa.Left, wa.Bottom - wa.Top);
            }
        }

        // Fallback: primary monitor work area (old behavior).
        var sp = SystemParameters.WorkArea;
        return new Rect(sp.Left, sp.Top, sp.Width, sp.Height);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}

// ── Tab visibility converter ─────────────────────────────────────────────────

public class TabVisConverter : IValueConverter
{
    public static readonly TabVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PopoverTab tab && parameter is string target)
            return tab.ToString() == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
