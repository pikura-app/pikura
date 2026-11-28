using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Reads pixiv Encyclopedia (dic.pixiv.net). Its Cloudflare bot management rejects .NET's
/// HttpClient outright (403 even with browser headers), so every request goes through a
/// hidden <see cref="NativeWebView"/> sitting on a dic.pixiv.net page: the JSON API under
/// <c>/_api</c> and the server-rendered article lists are fetched with same-origin XHR.
/// </summary>
public sealed class DicService
{
    public const string Origin = "https://dic.pixiv.net";

    private NativeWebView? _web;
    private Task<bool>? _ready;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Attach(NativeWebView web)
    {
        if (ReferenceEquals(_web, web)) return;
        _web = web;
        _ready = null;
    }

    private Task<bool> EnsureReadyAsync() => _ready ??= OpenOriginAsync();

    private async Task<bool> OpenOriginAsync()
    {
        var web = _web;
        if (web is null) return false;
        var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, WebViewNavigationCompletedEventArgs e) => navigated.TrySetResult(e.IsSuccess);
        web.NavigationCompleted += OnCompleted;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => web.Navigate(new Uri(Origin + "/en/")));
            await Task.WhenAny(navigated.Task, Task.Delay(30000));
            // Cloudflare may show its "Just a moment" interstitial first and then reload.
            for (var i = 0; i < 40; i++)
            {
                var state = await InvokeAsync(web, "JSON.stringify(location.host + '|' + document.title)");
                if (state is not null && state.StartsWith("dic.pixiv.net|", StringComparison.Ordinal)
                    && !state.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                    return true;
                await Task.Delay(750);
            }
            return false;
        }
        finally
        {
            web.NavigationCompleted -= OnCompleted;
        }
    }

    private static async Task<string?> InvokeAsync(NativeWebView web, string script)
    {
        var raw = await Dispatcher.UIThread.InvokeAsync(() => web.InvokeScript(script));
        if (string.IsNullOrWhiteSpace(raw) || raw == "null") return null;
        // InvokeScript JSON-encodes the script's value; our scripts return JSON.stringify(...) strings.
        if (raw.StartsWith('"'))
        {
            try { raw = JsonSerializer.Deserialize<string>(raw); } catch { }
        }
        return raw;
    }

    /// <summary>Runs a script that returns <c>JSON.stringify({status, body})</c> after a sync XHR.</summary>
    private async Task<(int Status, string Body)> GetAsync(string path, string? transform)
    {
        await _gate.WaitAsync();
        try
        {
            if (!await EnsureReadyAsync())
            {
                _ready = null;
                return (0, "");
            }
            var script =
                "(function(){try{var x=new XMLHttpRequest();x.open('GET'," + JsonSerializer.Serialize(path) + ",false);x.send();" +
                "var body=x.responseText;" +
                (transform is null ? "" : "if(x.status===200){" + transform + "}") +
                "return JSON.stringify({status:x.status,body:body});" +
                "}catch(e){return JSON.stringify({status:-1,body:String(e)});}})()";
            var json = await InvokeAsync(_web!, script);
            if (json is null) return (0, "");
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetInt32();
            var body = doc.RootElement.GetProperty("body").GetString() ?? "";
            return (status, body);
        }
        catch
        {
            return (0, "");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>GET <c>/_api{path}</c>; returns the parsed body, or null with the HTTP status.</summary>
    public async Task<(JsonDocument? Doc, int Status)> GetApiAsync(string path)
    {
        var (status, body) = await GetAsync("/_api" + path, null);
        if (status != 200 || string.IsNullOrEmpty(body)) return (null, status);
        try { return (JsonDocument.Parse(body), status); }
        catch { return (null, status); }
    }

    private const string ListTransform = """
        var d=new DOMParser().parseFromString(body,'text/html');
        var t=function(n){return n?n.textContent.replace(/\s+/g,' ').trim():'';};
        var items=[].map.call(d.querySelectorAll('#main section article'),function(a){
          var h=a.querySelector('.info h2 a'),img=a.querySelector('.thumb img'),s=a.querySelector('p.summary');
          var sum='';if(s){var c=s.cloneNode(true);var l=c.querySelectorAll('a');if(l.length)l[l.length-1].remove();sum=t(c);}
          return {title:t(h),href:h?h.getAttribute('href'):'',image:img?img.getAttribute('src'):'',summary:sum,
                  data:[].map.call(a.querySelectorAll('ul.data li'),t),
                  related:[].map.call(a.querySelectorAll('.relation li a'),t)};});
        body=JSON.stringify({items:items,heading:t(d.querySelector('#search-title h1')),info:t(d.querySelector('#search-title .info'))});
        """;

    /// <summary>Fetches a server-rendered article list (popular, updates, new, category, search).</summary>
    public async Task<(JsonDocument? Doc, int Status)> GetListAsync(string path)
    {
        var (status, body) = await GetAsync(path, ListTransform);
        if (status != 200 || string.IsNullOrEmpty(body)) return (null, status);
        try { return (JsonDocument.Parse(body), status); }
        catch { return (null, status); }
    }
}
