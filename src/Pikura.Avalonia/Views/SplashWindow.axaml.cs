using Avalonia.Controls;
using Avalonia.Threading;

namespace Pikura.Avalonia.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void CloseSplash()
    {
        Dispatcher.UIThread.Post(Close);
    }
}
