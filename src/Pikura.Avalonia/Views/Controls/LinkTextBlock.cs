using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pikura.Avalonia.Views.Controls;

/// <summary>
/// A TextBlock that auto-detects URLs and pixiv.net links in its text
/// and renders them as clickable underlined inline buttons.
/// </summary>
public sealed class LinkTextBlock : SelectableTextBlock
{
    private readonly DispatcherTimer _rebuildTimer;

    public static readonly StyledProperty<string?> LinkTextProperty =
        AvaloniaProperty.Register<LinkTextBlock, string?>(nameof(LinkText));

    public string? LinkText
    {
        get => GetValue(LinkTextProperty);
        set => SetValue(LinkTextProperty, value);
    }

    private static readonly Regex UrlRegex = new(
        @"(https?://[^\s""'<>]+|pixiv\.net/[^\s""'<>]+|[A-Za-z]:\\[^\n""'<>]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"\*(.+?)\*", RegexOptions.Compiled);

    public LinkTextBlock()
    {
        Focusable = true;
        IsTabStop = false;
        _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _rebuildTimer.Tick += (_, _) => { _rebuildTimer.Stop(); Rebuild(); };
        DetachedFromVisualTree += (_, _) => _rebuildTimer.Stop();
    }

    static LinkTextBlock()
    {
        LinkTextProperty.Changed.AddClassHandler<LinkTextBlock>((b, _) => b.ScheduleRebuild());
    }

    private void ScheduleRebuild()
    {
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    private void Rebuild()
    {
        var raw = LinkText;
        Inlines?.Clear();
        Text = null;

        if (string.IsNullOrEmpty(raw))
            return;

        var linkInlines = new List<Inline>();
        int pos = 0;

        foreach (Match m in UrlRegex.Matches(raw))
        {
            if (m.Index > pos)
                linkInlines.Add(new Run(raw[pos..m.Index]));

            var url = m.Value;
            var href = (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        || (url.Length >= 3 && url[1] == ':' && url[2] == '\\'))
                ? url
                : "https://" + url;

            var underline = new TextDecorationCollection
            {
                new TextDecoration { Location = TextDecorationLocation.Underline }
            };
            var btn = new TextBlock
            {
                Text = url,
                Cursor = new Cursor(StandardCursorType.Hand),
                TextDecorations = underline,
                Foreground = new SolidColorBrush(Color.Parse("#56A0DB")),
            };

            var captured = href;
            btn.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
                {
                    try
                    {
                        // Windows file/folder path — open in Explorer
                        if (captured.Length >= 3 && captured[1] == ':' && captured[2] == '\\')
                        {
                            if (System.IO.File.Exists(captured))
                                Process.Start("explorer.exe", $"/select,\"{captured}\"");
                            else
                                Process.Start(new ProcessStartInfo(captured) { UseShellExecute = true });
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo(captured) { UseShellExecute = true });
                        }
                    }
                    catch { }
                    e.Handled = true;
                }
            };

            linkInlines.Add(new InlineUIContainer { Child = btn, BaselineAlignment = BaselineAlignment.TextBottom });
            pos = m.Index + m.Length;
        }

        if (pos < raw.Length)
            linkInlines.Add(new Run(raw[pos..]));

        var inlines = new InlineCollection();
        foreach (var inline in linkInlines)
        {
            if (inline is Run run)
                ApplyMarkdownFormatting(run, inlines);
            else
                inlines.Add(inline);
        }

        Inlines = inlines;
    }

    private static void ApplyMarkdownFormatting(Run run, InlineCollection target)
    {
        var text = run.Text ?? string.Empty;
        int i = 0;
        while (i < text.Length)
        {
            var bold = BoldRegex.Match(text, i);
            var italic = ItalicRegex.Match(text, i);

            // Bold takes precedence when it starts at the same position as an italic match.
            if (bold.Success && (!italic.Success || bold.Index <= italic.Index))
            {
                if (bold.Index > i)
                    target.Add(new Run(text[i..bold.Index]));

                target.Add(new Run(bold.Groups[1].Value) { FontWeight = FontWeight.Bold });
                i = bold.Index + bold.Length;
            }
            else if (italic.Success)
            {
                if (italic.Index > i)
                    target.Add(new Run(text[i..italic.Index]));

                target.Add(new Run(italic.Groups[1].Value) { FontStyle = FontStyle.Italic });
                i = italic.Index + italic.Length;
            }
            else
            {
                target.Add(new Run(text[i..]));
                break;
            }
        }
    }
}
