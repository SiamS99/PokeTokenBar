using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;
using PokeTokenBar.ViewModels;

namespace PokeTokenBar.Views;

public partial class MainWindow : Window
{
    // Cached from the monitor under the cursor at open time — reused on every
    // resize so the popup keeps its bottom-right corner anchored as tabs with
    // different content heights (e.g. Settings vs. Home) grow or shrink it,
    // instead of only ever positioning once and letting it extend off-screen.
    private Rect _anchorWorkArea;
    private DpiScale _anchorDpi;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _ = vm.ReloadSpriteAsync();

        SizeChanged += (_, e) =>
        {
            if (e.HeightChanged) RepositionToAnchor();
        };
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
        // Show first, invisibly, so SizeToContent runs its *real* layout pass —
        // a Window only resolves its final size once its HWND exists (unlike a
        // plain FrameworkElement), so calling Measure/Arrange before Show() does
        // not reliably predict ActualHeight (verified: it under-reported, pushing
        // most of the window below the screen on first open).
        Opacity = 0;
        Show();
        UpdateLayout();

        // Anchor to the monitor under the cursor (where the tray icon was
        // actually clicked), not SystemParameters.WorkArea — that's always
        // the *primary* display, which leaves the popup rendered off on a
        // monitor the user isn't looking at on multi-monitor setups.
        _anchorWorkArea = NativeMethods.GetWorkAreaUnderCursor();
        _anchorDpi = VisualTreeHelper.GetDpi(this);
        RepositionToAnchor();

        Opacity = 1;
        Activate();
    }

    /// <summary>
    /// Re-applies Left/Top from the cached anchor so the window's bottom-right
    /// corner stays put while its height changes (SizeToContent="Height" means
    /// switching tabs — Settings is taller than Home — resizes the window, and
    /// without this it only ever grows downward off-screen since Top was fixed
    /// at open time).
    /// </summary>
    private void RepositionToAnchor()
    {
        if (_anchorWorkArea.Width <= 0) return; // not shown yet — nothing cached

        Left = _anchorWorkArea.Right  / _anchorDpi.DpiScaleX - Width - 16;
        Top  = _anchorWorkArea.Bottom / _anchorDpi.DpiScaleY - ActualHeight - 16;
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

// ── Tab pill selected-state converters ───────────────────────────────────────

public class TabPillBgConverter : IValueConverter
{
    public static readonly TabPillBgConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PopoverTab tab && parameter is string target && tab.ToString() == target
            ? Application.Current.Resources["CardAltBrush"]
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class TabPillFgConverter : IValueConverter
{
    public static readonly TabPillFgConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PopoverTab tab && parameter is string target && tab.ToString() == target
            ? Application.Current.Resources["TextBrush"]
            : Application.Current.Resources["SubtextBrush"];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
