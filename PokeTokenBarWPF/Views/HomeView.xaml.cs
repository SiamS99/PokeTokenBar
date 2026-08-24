using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PokeTokenBar.Core;

namespace PokeTokenBar.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}

// ── Null → Visibility converter ──────────────────────────────────────────────

public class NullToVisConverter : IValueConverter
{
    public static readonly NullToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── Rarity → badge color ──────────────────────────────────────────────────────

public class RarityToBrushConverter : IValueConverter
{
    public static readonly RarityToBrushConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            Rarity.Uncommon  => Application.Current.Resources["RarityUncommonBrush"],
            Rarity.Rare      => Application.Current.Resources["RarityRareBrush"],
            Rarity.Legendary => Application.Current.Resources["RarityLegendaryBrush"],
            _                => Application.Current.Resources["RarityCommonBrush"],
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── Fraction (0..1) → proportional Star GridLength ─────────────────────────────
// Used to build progress bars from two Grid columns instead of a restyled
// ProgressBar control template, whose PART_Track/PART_Indicator sizing contract
// is easy to get subtly wrong (fill stuck full or empty). Star-column proportional
// sizing is core, well-established Grid behavior.

public class FractionToStarWidthConverter : IValueConverter
{
    public static readonly FractionToStarWidthConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is double d ? Math.Clamp(d, 0.0, 1.0) : 0.0;
        var invert = parameter as string == "invert";
        var w = invert ? 1.0 - v : v;
        return new GridLength(Math.Max(0.0001, w), GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
