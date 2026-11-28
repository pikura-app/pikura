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
        var result = lines.ToArray();
        var hoshi = provider == "hoshi";
        var budget = hoshi ? 1500 : 4000;
        var chunk = new List<int>();
        var size = 0;
        async Task FlushAsync()
        {
            if (chunk.Count == 0) return;
            var input = chunk.Select(i => lines[i]).ToList();
            var output = hoshi ? await HoshiAsync(input, targetLang, ct) : await GoogleAsync(input, targetLang, ct);
            for (var k = 0; k < chunk.Count; k++) result[chunk[k]] = k < output.Count ? output[k] : "";
            if (output.Count > chunk.Count) result[chunk[^1]] += " " + string.Join(" ", output.Skip(chunk.Count));
            chunk.Clear();
            size = 0;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (size + lines[i].Length > budget) await FlushAsync();
            chunk.Add(i);
            size += lines[i].Length + 1;
        }
        await FlushAsync();
        return result;
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
