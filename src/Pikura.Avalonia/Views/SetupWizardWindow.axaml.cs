using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Pikura.Avalonia.ViewModels;

namespace Pikura.Avalonia.Views;

/// <summary>First-run Quick Setup wizard. Await <see cref="Completion"/>:
/// true = finished, false = closed without finishing (first-run path should quit).
/// In first-run mode (DialogMode=false) the window does NOT close itself on Finish —
/// the caller shows the main window first, then closes the wizard, so there is never
/// a zero-visible-window moment (ShutdownMode is OnLastWindowClose by default).</summary>
public partial class SetupWizardWindow : Window
{
    private bool _finished;

    /// <summary>Settings → "Run setup again" shows it via ShowDialog; in that mode Finish
    /// closes the dialog itself and X just closes it (no quit).</summary>
    public bool DialogMode { get; init; }

    public TaskCompletionSource<bool> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Parameterless ctor so the XAML resource stays reachable by the runtime loader.</summary>
    public SetupWizardWindow()
    {
        InitializeComponent();
    }

    public SetupWizardWindow(SetupWizardViewModel vm) : this()
    {
        DataContext = vm;
        vm.Finished += (_, _) =>
        {
            _finished = true;
            Completion.TrySetResult(true);
            if (DialogMode) Close();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Completion.TrySetResult(_finished);
    }
}
