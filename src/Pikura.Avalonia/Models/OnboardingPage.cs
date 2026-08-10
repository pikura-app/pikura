namespace Pikura.Avalonia.Models;

/// <summary>
/// A single page shown in the feature highlights / onboarding dialog.
/// </summary>
public sealed class OnboardingPage
{
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconKey { get; set; } = "NewIcon";
    public string? Screenshot { get; set; }
}
