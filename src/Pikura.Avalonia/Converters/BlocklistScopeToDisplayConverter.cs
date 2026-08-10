using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Converters;

public class BlocklistScopeToDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is BlocklistScope scope)
        {
            return scope switch
            {
                BlocklistScope.AllTabs => "All Tabs",
                BlocklistScope.Gallery => "Gallery",
                BlocklistScope.Rankings => "Rankings",
                BlocklistScope.Discover => "Discover",
                BlocklistScope.Pixivision => "Pixivision",
                BlocklistScope.Search => "Search",
                BlocklistScope.Viewed => "Viewed",
                BlocklistScope.Bookmarks => "Bookmarks",
                _ => scope.ToString(),
            };
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
