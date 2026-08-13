using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Pikura.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pikura.Avalonia.Views.Dialogs;

public partial class ChangelogDialog : Window
{
    public ChangelogDialog() { InitializeComponent(); }

    public ChangelogDialog(string version, string releaseNotes, string releasePageUrl)
    {
        InitializeComponent();

        VersionLabel.Text = $"Pikura v{version}";

        var section = ExtractVersionSection(releaseNotes, version);
        NotesPanel.Children.Clear();
        AppendMarkdown(string.IsNullOrWhiteSpace(section)
            ? "No release notes available for this version."
            : section);

        WireFooter(releasePageUrl);
    }

    /// <summary>
    /// Shows the full published release history (newest first) instead of a single version's
    /// notes — used by the About page's "View Changelog" button, per the user request that it
    /// should showcase the entire history rather than just the current version.
    /// </summary>
    public ChangelogDialog(IReadOnlyList<UpdateInfo> releases, string releasePageUrl)
    {
        InitializeComponent();

        Title = "Release History";
        VersionLabel.Text = "Release History";
        SubtitleLabel.Text = releases.Count == 0
            ? "No published releases found."
            : $"{releases.Count} release{(releases.Count == 1 ? "" : "s")} — newest first";
        ReleasePageBtn.Content = "All releases on GitHub ↗";

        NotesPanel.Children.Clear();
        for (var i = 0; i < releases.Count; i++)
        {
            var r = releases[i];
            if (i > 0)
            {
                NotesPanel.Children.Add(new Rectangle
                {
                    Height = 1,
                    Margin = new Thickness(0, 16, 0, 12),
                    Fill = new SolidColorBrush(Color.Parse("#33808080")),
                });
            }

            var heading = string.IsNullOrWhiteSpace(r.Title) || r.Title == r.Version
                ? $"v{r.Version}"
                : $"v{r.Version} — {r.Title}";
            NotesPanel.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            });

            AppendMarkdown(string.IsNullOrWhiteSpace(r.ReleaseNotes)
                ? "No release notes provided."
                : r.ReleaseNotes);
        }

        WireFooter(releasePageUrl);
    }

    private void WireFooter(string releasePageUrl)
    {
        ReleasePageBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(releasePageUrl))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(releasePageUrl) { UseShellExecute = true });
        };

        CloseBtn.Click += (_, _) => Close();
    }

    /// <summary>
    /// Extracts only the section for the given version from the full release notes,
    /// stopping before the next "## " heading (the previous release).
    /// </summary>
    private static string ExtractVersionSection(string fullNotes, string version)
    {
        if (string.IsNullOrWhiteSpace(fullNotes)) return string.Empty;
        var lines = fullNotes.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart('#').Trim().Contains(version, StringComparison.OrdinalIgnoreCase)
                && lines[i].StartsWith("##"))
            { start = i + 1; break; }
        }
        if (start < 0) return fullNotes; // fallback: show everything
        var sb = new System.Text.StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            // Stop at the next top-level version heading (but not sub-headings like ###)
            if (i > start && lines[i].StartsWith("## ")) break;
            // Skip horizontal rules
            if (lines[i].TrimStart('-').Trim() == string.Empty && lines[i].Contains('-') && lines[i].Length > 2) continue;
            sb.AppendLine(lines[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Appends a subset of markdown into the NotesPanel as formatted controls, without
    /// clearing what's already there — callers clear once up front so multiple releases can be
    /// appended in sequence.</summary>
    private void AppendMarkdown(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (string.IsNullOrEmpty(line))
            {
                NotesPanel.Children.Add(new TextBlock { Height = 6 });
                continue;
            }

            // ### sub-heading
            if (line.StartsWith("### "))
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = line[4..].Trim(),
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 10, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            // ## heading (shouldn't appear after extraction, but just in case)
            if (line.StartsWith("## "))
            {
                NotesPanel.Children.Add(new TextBlock
                {
                    Text = line[3..].Trim(),
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 8, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            // Bullet line: starts with "- "
            if (line.StartsWith("- "))
            {
                var content = line[2..].Trim();
                var tb = BuildInlineTextBlock(content, indent: true);
                NotesPanel.Children.Add(tb);
                continue;
            }

            // Plain paragraph
            NotesPanel.Children.Add(BuildInlineTextBlock(line, indent: false));
        }
    }

    /// <summary>Builds a TextBlock with inline bold (**text**) support.</summary>
    private static TextBlock BuildInlineTextBlock(string text, bool indent)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(indent ? 10 : 0, 1, 0, 1),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
        };

        // Split on **bold** markers
        var parts = Regex.Split(text, @"\*\*(.+?)\*\*");
        var isBold = false;
        var prefixAdded = false;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) { isBold = !isBold; continue; }

            if (indent && !prefixAdded)
            {
                tb.Inlines!.Add(new Run("• ") { FontWeight = FontWeight.Bold });
                prefixAdded = true;
            }

            tb.Inlines!.Add(new Run(part)
            {
                FontWeight = isBold ? FontWeight.SemiBold : FontWeight.Normal,
            });
            isBold = !isBold;
        }

        // If no inlines were added (e.g. no bold), set Text directly
        if (tb.Inlines?.Count == 0)
        {
            tb.Text = indent ? $"• {text}" : text;
        }

        return tb;
    }
}
