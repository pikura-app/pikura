using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Pikura.Avalonia.Controls;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views;

public partial class FeatureHighlightsWindow : Window
{
    public FeatureHighlightsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            RootBorder.Opacity = 1;
            RootBorder.RenderTransform = TransformOperations.Parse("scale(1)");
        };
    }

    public FeatureHighlightsWindow(FeatureHighlightsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.CloseRequested += () => Close(true);
        Closed += (_, _) => AnimatedImage.ClearCache();
    }
}
