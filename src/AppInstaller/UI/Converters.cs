using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AppInstaller.Core;

namespace AppInstaller.UI
{
    public class InverseBoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            return value is bool && (bool)value ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }

    /// <summary>Wiersz w widoku instalacji: kafelek (ikona, nazwa) + stan instalacji.</summary>
    public class InstallRow
    {
        public AppItem Tile { get; set; }
        public InstallItem Item { get; set; }
    }

    /// <summary>Pozycja na liście kategorii.</summary>
    public class CategoryItem
    {
        public string Id { get; set; }
        public string Glyph { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
    }
}
