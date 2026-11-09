using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Pikura.Core.Settings;

namespace Pikura.Avalonia.Converters;

public class BlocklistScopeToDisplayConverter : IValueConverter
{
    /// <summary>resx key for a scope's display name — shared with
    /// <see cref="ViewModels.BlocklistScopeOption"/> so both show the same text.</summary>
    public static string KeyFor(BlocklistScope scope) => scope switch
    {
        BlocklistScope.AllTabs => "Blocklist_Scope_AllTabs",
        BlocklistScope.Gallery => "Nav_Gallery",
        BlocklistScope.Rankings => "Nav_Rankings",
        BlocklistScope.Discover => "Nav_Discover",
        BlocklistScope.Pixivision => "Blocklist_Scope_Pixivision",
        BlocklistScope.Search => "Nav_Search",
        BlocklistScope.Viewed => "Nav_Viewed",
        BlocklistScope.Bookmarks => "Nav_Bookmarks",
        _ => "Blocklist_Scope_AllTabs",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is BlocklistScope scope)
            return Localization.LocalizationService.Instance[KeyFor(scope)];
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
