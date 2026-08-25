using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PokeTokenBar.Views;

public partial class PokedexView : UserControl
{
    public PokedexView() => InitializeComponent();
}

public class ZeroToVisConverter : IValueConverter
{
    public static readonly ZeroToVisConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is int n && n == 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}
