using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Pikura.Avalonia.Services;

/// <summary>
/// Reads pixiv Encyclopedia (dic.pixiv.net). Its Cloudflare bot management rejects .NET's
/// HttpClient outright (403 even with browser headers), so every request goes through a
/// hidden <see cref="NativeWebView"/> sitting on a dic.pixiv.net page: the JSON API under
/// <c>/_api</c> and the server-rendered article lists are fetched with same-origin fetch().
/// </summary>
public sealed class DicService
{
    public const string Origin = "https://dic.pixiv.net";

    private NativeWebView? _web;
    private Task<bool>? _ready;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly ILogger<DicService> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskCompletionSource<(int, string)>> _pending = new();
    private readonly Dictionary<string, Task<(int, string)>> _inflight = new();
    private readonly Dictionary<string, (DateTime At, string Body)> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private int _nextId;
    private bool _polling;
    private const int CacheCapacity = 200;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public DicService(ILogger<DicService> logger) => _logger = logger;

    public void Attach(NativeWebView web)
    {
        if (ReferenceEquals(_web, web)) return;
        _web = web;
        _ready = null;
    }

    /// <summary>Starts opening dic.pixiv.net in the background so the first request doesn't wait for it.</summary>
    public void WarmUp() => _ = EnsureReadyAsync();

    /// <summary>Forgets cached responses (Refresh).</summary>
    public void ClearCache()
    {
        lock (_lock) { _cache.Clear(); _cacheOrder.Clear(); }
    }

    private Task<bool> EnsureReadyAsync()
    {
        lock (_lock) return _ready ??= OpenOriginAsync();
    }

    private async Task<bool> OpenOriginAsync()
    {
        var web = _web;
        if (web is null) return false;
        await Dispatcher.UIThread.InvokeAsync(() => web.Navigate(new Uri(Origin + "/en/")));
        // Requests only need the origin's document and cookies, not the page's images and scripts, so
        // poll for the document instead of waiting for the page's load event.
        // Cloudflare may show its "Just a moment" interstitial first and then reload.
        string? state = null;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < until)
        {
            state = await InvokeAsync(web, "location.host + '|' + document.readyState + '|' + document.title");
            if (state is not null && state.StartsWith("dic.pixiv.net|", StringComparison.Ordinal)
                && !state.StartsWith("dic.pixiv.net|loading", StringComparison.Ordinal)
                && !state.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
                return true;
            await Task.Delay(200);
        }
        _logger.LogWarning("Dic: dic.pixiv.net never became ready (last state: {State})", state ?? "null");
        return false;
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

    /// <summary>
    /// Same-origin GET through the web view. Requests run asynchronously in the page (up to 4 at once),
    /// identical requests in flight are shared, and successful responses are cached for 10 minutes.
    /// </summary>
    private Task<(int Status, string Body)> GetAsync(string path, string? transform)
    {
        var key = (transform is null ? "A " : "L ") + path;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                if (DateTime.UtcNow - hit.At < CacheTtl)
                {
                    _cacheOrder.Remove(key);
                    _cacheOrder.AddFirst(key);
                    return Task.FromResult((200, hit.Body));
                }
                _cache.Remove(key);
                _cacheOrder.Remove(key);
            }
            if (_inflight.TryGetValue(key, out var running)) return running;
            var task = FetchAsync(key, path, transform);
            _inflight[key] = task;
            return task;
        }
    }

    /// <summary>Loads a response into the cache in the background (e.g. the next page of a list).</summary>
    public void PrefetchList(string path) => _ = GetAsync(path, ListTransform);

    /// <summary>Loads an <c>/_api</c> response into the cache in the background.</summary>
    public void PrefetchApi(string path) => _ = GetAsync("/_api" + path, null);

    private async Task<(int Status, string Body)> FetchAsync(string key, string path, string? transform)
    {
        await _slots.WaitAsync();
        try
        {
            if (!await EnsureReadyAsync())
            {
                lock (_lock) _ready = null;
                return (0, "");
            }
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _pending[id] = tcs;
            var script =
                "(function(){var r=window.__pk||(window.__pk={});" +
                "fetch(" + JsonSerializer.Serialize(path) + ",{credentials:'same-origin'}).then(function(x){return x.text().then(function(body){" +
                (transform is null ? "" : "if(x.status===200){" + transform + "}") +
                "r[" + id + "]={status:x.status,body:body};});})" +
                ".catch(function(e){r[" + id + "]={status:-1,body:String(e)};});return '1';})()";
            if (await InvokeAsync(_web!, script) is null)
            {
                lock (_lock) _pending.Remove(id);
                return (0, "");
            }
            EnsurePolling();
            var done = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout));
            if (done != tcs.Task)
            {
                lock (_lock) _pending.Remove(id);
                _logger.LogWarning("Dic: request {Path} timed out", path);
                return (0, "");
            }
            var (status, body) = tcs.Task.Result;
            if (status == 200 && body.Length > 0)
                lock (_lock)
                {
                    _cache[key] = (DateTime.UtcNow, body);
                    _cacheOrder.Remove(key);
                    _cacheOrder.AddFirst(key);
                    while (_cacheOrder.Count > CacheCapacity)
                    {
                        _cache.Remove(_cacheOrder.Last!.Value);
                        _cacheOrder.RemoveLast();
                    }
                }
            return (status, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dic: request {Path} failed", path);
            return (0, "");
        }
        finally
        {
            _slots.Release();
            lock (_lock) _inflight.Remove(key);
        }
    }

    private void EnsurePolling()
    {
        lock (_lock)
        {
            if (_polling) return;
            _polling = true;
        }
        _ = PollAsync();
    }

    /// <summary>Collects every finished request in one script call until none are pending.</summary>
    private async Task PollAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(30);
                lock (_lock)
                {
                    if (_pending.Count == 0) { _polling = false; return; }
                }
                string? json;
                try { json = await InvokeAsync(_web!, "(function(){var r=window.__pk||{};window.__pk={};return JSON.stringify(r);})()"); }
                catch { json = null; }
                if (json is null || json.Length <= 2) continue;
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, out var id)) continue;
                    TaskCompletionSource<(int, string)>? tcs;
                    lock (_lock)
                    {
                        if (!_pending.Remove(id, out tcs)) continue;
                    }
                    var status = p.Value.GetProperty("status").GetInt32();
                    var body = p.Value.GetProperty("body").GetString() ?? "";
                    tcs.TrySetResult((status, body));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dic: polling failed");
            lock (_lock) _polling = false;
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
