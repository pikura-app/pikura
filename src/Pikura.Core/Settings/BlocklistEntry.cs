using CommunityToolkit.Mvvm.ComponentModel;

namespace Pikura.Core.Settings;

public enum BlocklistType
{
    Tag,
    Artist,
    Title,
}

public enum BlocklistScope
{
    AllTabs,
    Gallery,
    Rankings,
    Discover,
    Pixivision,
    Search,
    Viewed,
    Bookmarks,
}

public partial class BlocklistEntry : ObservableObject
{
    [ObservableProperty] private BlocklistType _type;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private BlocklistScope _scope = BlocklistScope.AllTabs;
    [ObservableProperty] private bool _blockDownload;
}
