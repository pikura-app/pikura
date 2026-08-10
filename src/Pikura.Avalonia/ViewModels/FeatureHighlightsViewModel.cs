using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using Pikura.Avalonia.Models;

namespace Pikura.Avalonia.ViewModels;

public sealed partial class FeatureHighlightsViewModel : ViewModelBase
{
    private int _currentPageIndex;

    public FeatureHighlightsViewModel(IReadOnlyList<OnboardingPage> pages)
    {
        Pages = pages;
        NextCommand = new RelayCommand(Next, () => CanGoNext);
        PreviousCommand = new RelayCommand(Previous, () => CanGoPrevious);
        SkipCommand = new RelayCommand(Finish);
        FinishCommand = new RelayCommand(Finish);
    }

    public IReadOnlyList<OnboardingPage> Pages { get; }

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (value < 0 || Pages.Count == 0 || value >= Pages.Count) return;
            if (SetProperty(ref _currentPageIndex, value))
            {
                OnPropertyChanged(nameof(CurrentPage));
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(IsLastPage));
                OnPropertyChanged(nameof(StepText));
                NextCommand.NotifyCanExecuteChanged();
                PreviousCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public OnboardingPage? CurrentPage => Pages.Count > 0 ? Pages[CurrentPageIndex] : null;

    public bool CanGoPrevious => _currentPageIndex > 0;

    public bool CanGoNext => Pages.Count > 0 && _currentPageIndex < Pages.Count - 1;

    public bool IsLastPage => Pages.Count > 0 && _currentPageIndex == Pages.Count - 1;

    public string StepText => $"Step {_currentPageIndex + 1} of {Pages.Count}";

    public IRelayCommand NextCommand { get; }
    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand SkipCommand { get; }
    public IRelayCommand FinishCommand { get; }

    public event Action? CloseRequested;

    private void Next()
    {
        if (CanGoNext)
            CurrentPageIndex++;
        else
            Finish();
    }

    private void Previous()
    {
        if (CanGoPrevious)
            CurrentPageIndex--;
    }

    private void Finish()
    {
        CloseRequested?.Invoke();
    }
}
