using System;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pikura.Avalonia.Localization;

namespace Pikura.Avalonia.ViewModels;

public abstract class ViewModelBase : ObservableObject, ILanguageAware
{
    protected readonly ILogger Logger;

    protected ViewModelBase()
    {
        Logger = NullLogger.Instance;
        LocalizationService.Instance.Register(this);
    }

    protected ViewModelBase(ILogger logger)
    {
        Logger = logger;
        LocalizationService.Instance.Register(this);
    }

    /// <summary>Re-raises every computed get-only string property (so {Binding}-ed labels update),
    /// then re-renders a status message set through <see cref="SetStatus"/> if nothing overwrote
    /// it. Override to do more on language change; call base first.</summary>
    public virtual void OnLanguageChanged()
    {
        LanguageRefresh.RaiseComputedStrings(this, OnPropertyChanged);
        RefreshLocalizedStatus();
    }

    // ---- Re-renderable status message --------------------------------------

    private string? _statusKey;
    private object?[]? _statusArgs;
    private string? _statusRendered;
    private PropertyInfo? _statusProp;
    private bool _statusPropResolved;

    /// <summary>Renders a localized status message and assigns it to the VM's
    /// <c>StatusMessage</c> property (if it has one). The key+args are remembered so the message
    /// can be re-rendered in the new language — provided nothing else overwrote the text.</summary>
    protected void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        var text = args.Length == 0
            ? LocalizationService.Instance[key]
            : LocalizationService.F(key, args);
        _statusRendered = text;
        SetStatusText(text);
    }

    /// <summary>Re-renders the last <see cref="SetStatus"/> message in the current language, but
    /// only while the property still shows exactly the text we last rendered (a raw assignment or
    /// different message since then wins).</summary>
    protected void RefreshLocalizedStatus()
    {
        if (_statusKey is null) return;
        var prop = ResolveStatusProp();
        if (prop is null) return;
        if (!Equals(prop.GetValue(this) as string, _statusRendered)) return;
        var text = _statusArgs is { Length: > 0 } args
            ? LocalizationService.F(_statusKey, args)
            : LocalizationService.Instance[_statusKey];
        _statusRendered = text;
        prop.SetValue(this, text);
    }

    private void SetStatusText(string text) => ResolveStatusProp()?.SetValue(this, text);

    private PropertyInfo? ResolveStatusProp()
    {
        if (_statusPropResolved) return _statusProp;
        _statusPropResolved = true;
        var p = GetType().GetProperty("StatusMessage", BindingFlags.Public | BindingFlags.Instance);
        _statusProp = p is { PropertyType: { } t, CanWrite: true } && t == typeof(string) ? p : null;
        return _statusProp;
    }
}
