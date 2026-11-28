using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pikura.Avalonia.Localization;

namespace Pikura.Avalonia.Services;

/// <summary>Translates novel paragraphs with Google Translate or Hoshi (local Ollama),
/// keeping one output line per input line.</summary>
public static class NovelTranslation
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> lines, string provider, string targetLang, CancellationToken ct)
    {
        var hoshi = provider == "hoshi";
        // Long paragraphs are split into sentence pieces: Google's free endpoint stops partway
        // through very long input, and a small model loses track of long lines.
        var pieceMax = hoshi ? 600 : 1200;
        var budget = hoshi ? 1500 : 3000;
        var pieces = new List<(int Line, string Text)>();
        for (var i = 0; i < lines.Count; i++)
            if (!string.IsNullOrWhiteSpace(lines[i]))
                foreach (var piece in SplitLong(lines[i], pieceMax)) pieces.Add((i, piece));

        var translated = new string[pieces.Count];
        var batches = new List<List<int>>();
        var current = new List<int>();
        var size = 0;
        for (var k = 0; k < pieces.Count; k++)
        {
            if (current.Count > 0 && size + pieces[k].Text.Length > budget) { batches.Add(current); current = []; size = 0; }
            current.Add(k);
            size += pieces[k].Text.Length + 1;
        }
        if (current.Count > 0) batches.Add(current);

        async Task RunBatchAsync(List<int> batch)
        {
            var input = batch.Select(k => pieces[k].Text).ToList();
            var output = hoshi ? await HoshiAsync(input, targetLang, ct) : await GoogleAsync(input, targetLang, ct);
            if (output.Count == input.Count)
            {
                for (var j = 0; j < batch.Count; j++) translated[batch[j]] = output[j];
                return;
            }
            // Line count didn't survive: translate each piece on its own so nothing is left untranslated.
            for (var j = 0; j < batch.Count; j++)
            {
                var single = hoshi ? await HoshiAsync([input[j]], targetLang, ct) : await GoogleAsync([input[j]], targetLang, ct);
                translated[batch[j]] = string.Join(" ", single);
            }
        }

        if (hoshi)
            foreach (var batch in batches) await RunBatchAsync(batch);
        else
        {
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(batches.Select(async batch =>
            {
                await gate.WaitAsync(ct);
                try { await RunBatchAsync(batch); }
                finally { gate.Release(); }
            }));
        }

        var sep = targetLang is "ja" or "zh" ? "" : " ";
        var result = lines.ToArray();
        foreach (var group in pieces.Select((p, k) => (p.Line, Text: translated[k] ?? p.Text)).GroupBy(x => x.Line))
            result[group.Key] = string.Join(sep, group.Select(x => x.Text.Trim()));
        return result;
    }

    /// <summary>Splits a paragraph into pieces of at most <paramref name="max"/> characters at sentence ends.</summary>
    internal static IEnumerable<string> SplitLong(string text, int max)
    {
        if (text.Length <= max) { yield return text; yield break; }
        var start = 0;
        while (start < text.Length)
        {
            if (text.Length - start <= max) { yield return text[start..]; yield break; }
            var cut = -1;
            for (var i = start + max - 1; i > start + max / 3; i--)
                if ("。！？!?.」』…\u3000 ".Contains(text[i])) { cut = i + 1; break; }
            if (cut < 0) cut = start + max;
            yield return text[start..cut];
            start = cut;
        }
    }

    private static async Task<List<string>> GoogleAsync(List<string> lines, string target, CancellationToken ct)
    {
        var tl = target == "zh" ? "zh-CN" : target;
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", string.Join("\n", lines))]);
        using var resp = await Http.PostAsync($"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={tl}&dt=t", content, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var sb = new StringBuilder();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
            if (seg.ValueKind == JsonValueKind.Array && seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
        return Split(sb.ToString(), lines.Count);
    }

    private static async Task<List<string>> HoshiAsync(List<string> lines, string target, CancellationToken ct)
    {
        var ollama = AppServices.Get<OllamaService>();
        if (!ollama.IsReady) throw new InvalidOperationException(LocalizationService.Instance["Novel_HoshiOff"]);
        var language = new CultureInfo(target).EnglishName;
        var system = $"You are a literary translator. Translate the user's novel text into {language}. " +
                     "Keep the meaning, tone and names. The input has one paragraph per line; output exactly one translated line per input line, " +
                     "in the same order, with no numbering, notes or extra text.";
        var output = await ollama.CompleteAsync(system, string.Join("\n", lines), ct);
        return Split(output, lines.Count);
    }

    private static List<string> Split(string text, int expected)
    {
        var parts = text.Replace("\r", "").Split('\n').Select(s => s.Trim()).ToList();
        if (parts.Count != expected) parts = parts.Where(p => p.Length > 0).ToList();
        return parts;
    }
}
