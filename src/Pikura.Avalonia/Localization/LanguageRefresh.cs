using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Pikura.Avalonia.Localization;

/// <summary>
/// Re-raises change notifications for an object's computed (get-only) string properties after a
/// language change. Computed localized labels — <c>StatusText</c>, <c>TypeLabel</c> and friends —
/// are public getters with no setter, so they're identified by shape rather than by name. Source-
/// generated <c>[ObservableProperty]</c> properties all have public setters and are skipped, which
/// keeps this cheap even on item view models that exist in the thousands.
/// </summary>
public static class LanguageRefresh
{
    private static readonly ConcurrentDictionary<Type, string[]> ComputedStringProps = new();

    public static void RaiseComputedStrings(object target, Action<string> raise)
    {
        var names = ComputedStringProps.GetOrAdd(target.GetType(), Collect);
        foreach (var name in names) raise(name);
    }

    private static string[] Collect(Type type)
    {
        var names = new List<string>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType != typeof(string)) continue;
            if (!p.CanRead) continue;
            if (p.SetMethod is { IsPublic: true }) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            names.Add(p.Name);
        }
        return names.ToArray();
    }
}
