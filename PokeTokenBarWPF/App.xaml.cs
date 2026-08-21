using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using PokeTokenBar.Core;
using PokeTokenBar.ViewModels;
using PokeTokenBar.Views;

namespace PokeTokenBar;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private MainWindow?  _popup;
    private UsageStore?  _store;
    private AppSettings  _settings = new();
    private CompanionStore _companion = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Load settings and companion state.
        _settings  = AppSettings.Load();
        _companion = new CompanionStore();
        _companion.Load();

        // Keep the Run-key registration in sync with the setting (also
        // self-heals the registered path after the exe moves/updates).
        StartupManager.SetEnabled(_settings.LaunchAtStartup);

        // Build store and view model.
        _store = new UsageStore(_settings, _companion);
        var vm = new MainViewModel(_store);

        // Build popup window (hidden initially).
        _popup = new MainWindow(vm);

        // Build system tray icon.
        _trayIcon = new TaskbarIcon
        {
            Icon = BuildTrayIcon(),
            ToolTipText = "PokeTokenBar",
            ContextMenu = BuildContextMenu()
        };
        _trayIcon.TrayLeftMouseUp    += (_, _) => TogglePopup();
        _trayIcon.TrayBalloonTipClicked += (_, _) => TogglePopup();

        // Update tray tooltip when label changes.
        _store.PropertyChanged += (_, pe) =>
        {
            if (pe.PropertyName == nameof(UsageStore.TrayLabel))
                _trayIcon.ToolTipText = string.IsNullOrEmpty(_store.TrayLabel)
                    ? "PokeTokenBar"
                    : $"PokeTokenBar  {_store.TrayLabel}";
        };

        // Companion events → balloon notification.
        _store.PropertyChanged += (_, pe) =>
        {
            if (pe.PropertyName == nameof(UsageStore.CompanionChanged) && _store.CompanionChanged
                && _settings.EnableCompanionNotifications)
            {
                _trayIcon.ShowBalloonTip(
                    "PokeTokenBar",
                    $"Your companion {_companion.CompanionDisplayName} changed!",
                    BalloonIcon.Info);
            }
        };

        // Start polling.
        _store.Start();
    }

    private void TogglePopup()
    {
        if (_popup is null) return;

        if (_popup.IsVisible)
        {
            _popup.Hide();
        }
        else
        {
            _popup.ShowNearTray();
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => { _popup?.Hide(); _popup?.ShowNearTray(); };
        menu.Items.Add(openItem);

        var refreshItem = new MenuItem { Header = "Refresh Now" };
        refreshItem.Click += async (_, _) => { if (_store is not null) await _store.RefreshAsync(); };
        menu.Items.Add(refreshItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ExitApp()
    {
        _companion.Save();
        _settings.Save();
        _trayIcon?.Dispose();
        _store?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _store?.Dispose();
        base.OnExit(e);
    }

    // ── Tray icon generation ──────────────────────────────────────────────────

    private static Icon BuildTrayIcon()
    {
        // Try loading from Resources\tray.ico first.
        var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "tray.ico");
        if (File.Exists(icoPath))
            return new Icon(icoPath);

        // Fall back to a programmatically drawn Poké Ball icon.
        return CreatePokeballIcon();
    }

    private static Icon CreatePokeballIcon()
    {
        const int size = 16;
        using var bmp = new Bitmap(size, size);
        using var g   = Graphics.FromImage(bmp);

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Top half — red.
        g.FillEllipse(new SolidBrush(System.Drawing.Color.FromArgb(220, 50, 50)), 0, 0, size - 1, size - 1);
        // Bottom half — white.
        g.FillEllipse(System.Drawing.Brushes.White, 0, 0, size - 1, size - 1);
        using var whiteBrush = new SolidBrush(System.Drawing.Color.FromArgb(240, 240, 240));
        g.FillRectangle(whiteBrush, 0, size / 2, size, size / 2);
        // Centre band.
        g.FillRectangle(System.Drawing.Brushes.Black, 0, size / 2 - 1, size, 3);
        // Centre circle.
        g.FillEllipse(System.Drawing.Brushes.White, size / 2 - 3, size / 2 - 3, 6, 6);
        g.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.Black, 1f), size / 2 - 3, size / 2 - 3, 5, 5);
        // Outer border.
        g.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.Black, 1.5f), 0, 0, size - 2, size - 2);

        return Icon.FromHandle(bmp.GetHicon());
    }
}
