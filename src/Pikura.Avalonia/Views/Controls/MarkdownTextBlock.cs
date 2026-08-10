using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pikura.Avalonia.Views.Controls;

/// <summary>
/// A TextBlock that renders simple Markdown (headers, bold, italic, lists, code, links).
/// </summary>
public sealed class MarkdownTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((c, _) => c.ParseAndRender());
    }

    private void ParseAndRender()
    {
        Inlines?.Clear();
        if (string.IsNullOrWhiteSpace(Markdown))
            return;

        var lines = Markdown.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var first = true;

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (!first)
                Inlines?.Add(new LineBreak());
            first = false;

            // Headers
            if (line.StartsWith("### "))
            {
                var run = new Run(line.Substring(4)) { FontWeight = FontWeight.Bold, FontSize = 13 };
                Inlines?.Add(run);
                continue;
            }
            if (line.StartsWith("## "))
            {
                var run = new Run(line.Substring(3)) { FontWeight = FontWeight.Bold, FontSize = 15 };
                Inlines?.Add(run);
                continue;
            }
            if (line.StartsWith("# "))
            {
                var run = new Run(line.Substring(2)) { FontWeight = FontWeight.Bold, FontSize = 18 };
                Inlines?.Add(run);
                continue;
            }

            // Horizontal rule
            if (line.Trim() == "---")
            {
                Inlines?.Add(new Run("—".PadRight(40, '—')) { Foreground = Brushes.Gray });
                continue;
            }

            // Blockquote
            if (line.TrimStart().StartsWith("> "))
            {
                var run = new Run(line.TrimStart().Substring(2)) { FontStyle = FontStyle.Italic, Foreground = Brushes.Gray };
                Inlines?.Add(run);
                continue;
            }

            // List items
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var indent = line.Length - trimmed.Length;
                var prefix = new string(' ', indent) + "• ";
                Inlines?.Add(new Run(prefix));
                ParseInline(trimmed.Substring(2));
                continue;
            }
            if (Regex.IsMatch(trimmed, @"^\d+\. "))
            {
                var match = Regex.Match(trimmed, @"^(\d+)\. ");
                if (match.Success)
                {
                    var indent = line.Length - trimmed.Length;
                    var prefix = new string(' ', indent) + match.Groups[1].Value + ". ";
                    Inlines?.Add(new Run(prefix));
                    ParseInline(trimmed.Substring(match.Length));
                    continue;
                }
            }

            // Normal paragraph
            ParseInline(line);
        }
    }

    private void ParseInline(string text)
    {
        // Process inline formatting: **bold**, *italic*, `code`, [link](url)
        var patterns = new (Regex regex, Func<string, Inline> create)[]
        {
            // Bold
            (new Regex(@"\*\*(.+?)\*\*"), s => new Run(s) { FontWeight = FontWeight.Bold }),
            // Italic
            (new Regex(@"\*(.+?)\*"), s => new Run(s) { FontStyle = FontStyle.Italic }),
            // Code
            (new Regex(@"`(.+?)`"), s => new Run(s) { FontFamily = new FontFamily("Consolas,Monospace"), Background = new SolidColorBrush(Color.Parse("#2d2d2d")) }),
            // Links
            (new Regex(@"\[([^\]]+)\]\(([^)]+)\)"), s => new Run(s) { Foreground = new SolidColorBrush(Color.Parse("#4FC3F7")), TextDecorations = new TextDecorationCollection { new TextDecoration { Location = TextDecorationLocation.Underline } } }),
        };

        var remaining = text;
        while (!string.IsNullOrEmpty(remaining))
        {
            var earliestMatch = patterns
                .Select(p => new { pattern = p, match = p.regex.Match(remaining) })
                .Where(x => x.match.Success)
                .OrderBy(x => x.match.Index)
                .FirstOrDefault();

            if (earliestMatch == null)
            {
                // No more formatting
                Inlines?.Add(new Run(remaining));
                break;
            }

            // Add text before the match
            if (earliestMatch.match.Index > 0)
            {
                Inlines?.Add(new Run(remaining.Substring(0, earliestMatch.match.Index)));
            }

            // Add formatted content
            var content = earliestMatch.match.Groups[1].Value;
            Inlines?.Add(earliestMatch.pattern.create(content));

            // Continue after the match
            remaining = remaining.Substring(earliestMatch.match.Index + earliestMatch.match.Length);
        }
    }
}
