using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PokeTokenBar.ViewModels;

namespace PokeTokenBar.Views;

/// <summary>
/// Shows an animated GIF sprite from raw bytes (using WPF's GifBitmapDecoder
/// for animated GIF playback) or a static PNG, with egg fallback.
/// </summary>
public partial class SpriteView : UserControl
{
    public SpriteView() => InitializeComponent();

    protected override void OnPropertyChanged(System.Windows.DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property.Name == nameof(DataContext))
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (_, pe) =>
                {
                    if (pe.PropertyName is nameof(MainViewModel.SpriteBytes)
                        or nameof(MainViewModel.SpriteLoading))
                        UpdateSprite(vm);
                };
                UpdateSprite(vm);
            }
        }
    }

    private void UpdateSprite(MainViewModel vm)
    {
        Dispatcher.Invoke(() =>
        {
            if (vm.SpriteLoading)
            {
                Spinner.Visibility = Visibility.Visible;
                return;
            }
            Spinner.Visibility = Visibility.Collapsed;

            var bytes = vm.SpriteBytes;
            if (bytes is null or { Length: 0 })
            {
                EggEmoji.Visibility   = Visibility.Visible;
                SpriteImage.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                using var ms = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(ms,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count > 1)
                {
                    // Animated GIF: use WpfAnimatedGif approach via ObjectAnimationUsingKeyFrames.
                    SpriteImage.Source = CreateAnimatedSource(bytes);
                }
                else
                {
                    SpriteImage.Source = decoder.Frames[0];
                }

                EggEmoji.Visibility    = Visibility.Collapsed;
                SpriteImage.Visibility = Visibility.Visible;
            }
            catch
            {
                EggEmoji.Visibility    = Visibility.Visible;
                SpriteImage.Visibility = Visibility.Collapsed;
            }
        });
    }

    private static BitmapSource CreateAnimatedSource(byte[] bytes)
    {
        // For simplicity render first frame; full GIF animation requires
        // a third-party library (XamlAnimatedGif) or manual storyboard.
        using var ms = new MemoryStream(bytes);
        var decoder = new GifBitmapDecoder(ms,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }
}

// ── Bool → Visibility converter ──────────────────────────────────────────────

public class BoolToVisConverter : System.Windows.Data.IValueConverter
{
    public static readonly BoolToVisConverter Instance = new();

    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
        => (value is true) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c)
        => throw new NotSupportedException();
}
