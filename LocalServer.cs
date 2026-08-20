using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BookPicks
{
    /// <summary>
    /// 本地 HTTP 服务器(绑定 127.0.0.1 随机端口,无需管理员权限):
    ///  - 提供 www/ 静态资源(前端界面)
    ///  - 反向代理 Open Library API(/api/*),规避浏览器 CORS 限制
    ///  - 封面经 /api/cover 代理并缓存(封面 CDN 直连在国内常被阻断)
    ///  - 自动发现本地代理(Clash 等),启动时探测各通道连通性并自动切换
    ///  - 磁盘缓存到 %LOCALAPPDATA%\BookPicks\cache\,网络失败时用旧缓存兜底
    ///  - 收藏读写(/api/favorites)
    /// </summary>
    public sealed class LocalServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _wwwRoot;
        private readonly string _cacheDir;
        private readonly string _favoritesPath;
        private readonly HttpClient _httpDirect;
        private readonly HttpClient? _httpProxy;
        private readonly List<HttpClient> _clients = new();
        /// <summary>翻译并发闸:限制同时发出的翻译请求数,避免触发限流。</summary>
        private readonly SemaphoreSlim _translateGate = new(8);
        /// <summary>本地离线翻译引擎(优先使用,失败自动回退在线接口)。</summary>
        private readonly LocalTranslator _translator = new();

        public int Port { get; }
        public string BaseUrl => "http://127.0.0.1:" + Port + "/";
        /// <summary>探测到的代理信息(用于自检报告展示)。</summary>
        public string ProxyInfo { get; }

        public LocalServer()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookPicks");
            _cacheDir = Path.Combine(appData, "cache");
            _favoritesPath = Path.Combine(appData, "favorites.json");
            Directory.CreateDirectory(_cacheDir);

            _wwwRoot = Path.Combine(AppContext.BaseDirectory, "www");
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            // 直连通道若被墙会一直超时,给较短超时;代理通道给正常超时
            _httpDirect = MakeClient(null, TimeSpan.FromSeconds(12));
            string? proxy = FindLocalProxy();
            ProxyInfo = proxy ?? "无(直连)";
            _httpProxy = proxy != null ? MakeClient(new WebProxy(proxy), TimeSpan.FromSeconds(25)) : null;

            ProbeAndOrderClients();
        }

        public void Run()
        {
            _ = AcceptLoopAsync();
            _ = PrewarmAsync();
        }

        /// <summary>
        /// 启动预热:1.5 秒后后台请求今日热榜,把数据(含书名翻译)提前取回并缓存,
        /// 用户打开界面时热榜已是秒开(首启唯一一次翻译成本被预热吸收)。
        /// </summary>
        private async Task PrewarmAsync()
        {
            try
            {
                await Task.Delay(1500);
                using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                using var resp = await c.GetAsync(BaseUrl + "api/trending");
                resp.Dispose();
            }
            catch { /* 预热失败不影响正常使用 */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _httpDirect.Dispose();
            _httpProxy?.Dispose();
            _translator.Dispose();
        }

        // ---------------- 网络通道(直连 + 本地代理) ----------------

        private static HttpClient MakeClient(WebProxy? proxy, TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                Proxy = proxy,
                UseProxy = proxy != null,
            };
            var c = new HttpClient(handler) { Timeout = timeout };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("BookPicks/1.0 (desktop book browser)");
            return c;
        }

        /// <summary>查找可用本地代理:优先环境变量,其次系统代理设置(Clash 类工具常留下 127.0.0.1:端口)。</summary>
        private static string? FindLocalProxy()
        {
            foreach (string env in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
            {
                string? p = NormalizeProxy(Environment.GetEnvironmentVariable(env));
                if (p != null) return p;
            }
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                return NormalizeProxy(key?.GetValue("ProxyServer") as string);
            }
            catch { return null; }
        }

        private static string? NormalizeProxy(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.Contains('=') || s.Contains(';'))
            {
                // 形如 http=127.0.0.1:7890;https=127.0.0.1:7890 → 取 https 段
                string? https = null;
                foreach (string part in s.Split(';'))
                {
                    string t = part.Trim();
                    if (t.Length == 0) continue;
                    if (t.ToLowerInvariant().StartsWith("https"))
                    {
                        int eq = t.IndexOf('=');
                        https = "http://" + (eq >= 0 ? t.Substring(eq + 1).Trim() : t);
                    }
                }
                if (https != null) return https;
                // 无 https 段则取第一个含端口的键值对
                foreach (string part in s.Split(';'))
                {
                    string t = part.Trim();
                    int eq = t.IndexOf('=');
                    if (eq >= 0) return "http://" + t.Substring(eq + 1).Trim();
                }
                return null;
            }
            if (!s.Contains("://")) s = "http://" + s;
            return Uri.TryCreate(s, UriKind.Absolute, out _) ? s : null;
        }

        /// <summary>启动时快速探测(4 秒内)各通道连通性,决定请求顺序;请求时自动切换下一个通道。</summary>
        private void ProbeAndOrderClients()
        {
            bool directOk = Probe(_httpDirect);
            bool proxyOk = _httpProxy != null && Probe(_httpProxy);

            if (_httpProxy != null && proxyOk) _clients.Add(_httpProxy);
            if (directOk) _clients.Add(_httpDirect);
            if (_httpProxy != null && !proxyOk) _clients.Add(_httpProxy);
            if (!directOk) _clients.Add(_httpDirect);
        }

        private static bool Probe(HttpClient client)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var resp = client.GetAsync("https://openlibrary.org/trending/daily.json",
                    HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>
        /// 按探测顺序逐个通道尝试,全部失败则抛出最后一次异常。
        /// 服务器已明确响应(如 404/503,异常带状态码)时立即抛出,不再切换通道——切换通道只针对真正的网络失败。
        /// </summary>
        private async Task<string> FetchWithFallbackAsync(string url)
        {
            // 最多两轮:首轮全失败后重新探测通道顺序(代理可能刚启动),再试一轮
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Exception? last = null;
                foreach (HttpClient c in _clients)
                {
                    try { return await c.GetStringAsync(url); }
                    catch (HttpRequestException ex) when (ex.StatusCode != null)
                    {
                        throw; // 上游已应答,无需换通道
                    }
                    catch (Exception ex) { last = ex; }
                }
                if (attempt == 0) ProbeAndOrderClients();
                else throw last ?? new HttpRequestException("所有网络通道均不可用");
            }
            throw new HttpRequestException("所有网络通道均不可用");
        }

        private async Task<byte[]> FetchBytesWithFallbackAsync(string url)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Exception? last = null;
                foreach (HttpClient c in _clients)
                {
                    try { return await c.GetByteArrayAsync(url); }
                    catch (HttpRequestException ex) when (ex.StatusCode != null)
                    {
                        throw; // 上游已应答,无需换通道
                    }
                    catch (Exception ex) { last = ex; }
                }
                if (attempt == 0) ProbeAndOrderClients();
                else throw last ?? new HttpRequestException("所有网络通道均不可用");
            }
            throw new HttpRequestException("所有网络通道均不可用");
        }

        // ---------------- HTTP 连接处理 ----------------

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    stream.ReadTimeout = 15000;
                    var (head, leftover) = await ReadRequestAsync(stream);
                    if (head == null) return;

                    string[] lines = head.Split("\r\n");
                    string[] parts = lines[0].Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0];
                    string path = parts[1];
                    string query = "";
                    int qi = path.IndexOf('?');
                    if (qi >= 0) { query = path.Substring(qi + 1); path = path.Substring(0, qi); }

                    byte[]? body = null;
                    if (method == "POST")
                    {
                        int len = GetHeaderInt(lines, "Content-Length");
                        if (len > 0)
                        {
                            body = new byte[len];
                            // 请求头与请求体可能同包到达,先补上已读到的部分
                            int got = Math.Min(len, leftover.Length);
                            if (got > 0) Array.Copy(leftover, 0, body, 0, got);
                            int read = got;
                            while (read < len)
                            {
                                int r = await stream.ReadAsync(body, read, len - read);
                                if (r <= 0) break;
                                read += r;
                            }
                        }
                    }

                    (int status, string contentType, byte[] payload) = await RouteAsync(method, path, query, body);

                    var sb = new StringBuilder();
                    sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
                    sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
                    sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
                    sb.Append("Connection: close\r\n");
                    sb.Append("Cache-Control: no-store\r\n\r\n");
                    byte[] headBytes = Encoding.UTF8.GetBytes(sb.ToString());
                    await stream.WriteAsync(headBytes, 0, headBytes.Length);
                    await stream.WriteAsync(payload, 0, payload.Length);
                    await stream.FlushAsync();
                }
                catch { /* 单次请求失败不影响整体服务 */ }
            }
        }

        /// <summary>读取请求头,返回 (请求头文本, 同包多读到的请求体字节)。</summary>
        private static async Task<(string? head, byte[] leftover)> ReadRequestAsync(NetworkStream stream)
        {
            var acc = new List<byte>(8192);
            var tmp = new byte[8192];
            while (acc.Count < 65536)
            {
                int n = await stream.ReadAsync(tmp, 0, tmp.Length);
                if (n <= 0) return (null, Array.Empty<byte>());
                for (int i = 0; i < n; i++) acc.Add(tmp[i]);
                int idx = IndexOfHeaderEnd(acc);
                if (idx >= 0)
                {
                    string head = Encoding.ASCII.GetString(acc.ToArray(), 0, idx);
                    var rest = new byte[acc.Count - (idx + 4)];
                    acc.CopyTo(idx + 4, rest, 0, rest.Length);
                    return (head, rest);
                }
            }
            return (Encoding.ASCII.GetString(acc.ToArray()), Array.Empty<byte>());
        }

        private static int IndexOfHeaderEnd(List<byte> b)
        {
            for (int i = 0; i + 3 < b.Count; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        private static int GetHeaderInt(string[] lines, string name)
        {
            foreach (var line in lines.Skip(1))
            {
                int ci = line.IndexOf(':');
                if (ci > 0 && line.Substring(0, ci).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(line.Substring(ci + 1).Trim(), out int v) ? v : 0;
            }
            return 0;
        }

        private static string StatusText(int code) => code switch
        {
            200 => "OK",
            201 => "Created",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            _ => "Error"
        };

        // ---------------- 路由 ----------------

        private async Task<(int, string, byte[])> RouteAsync(string method, string path, string query, byte[]? body)
        {
            try
            {
                if (method == "GET" && path == "/api/trending")
                    return await ProxySlimAsync("trending_v2",
                        "https://openlibrary.org/trending/daily.json", TimeSpan.FromHours(12), ProjectTrendingAsync);

                if (method == "GET" && path.StartsWith("/api/trending/"))
                {
                    // 日 / 周 / 月榜:/api/trending/daily | weekly | monthly
                    string period = path.Substring("/api/trending/".Length);
                    if (period is "daily" or "weekly" or "monthly")
                        return await ProxySlimAsync("trending_" + period,
                            "https://openlibrary.org/trending/" + period + ".json", TimeSpan.FromHours(12), ProjectTrendingAsync);
                }

                if (method == "GET" && path == "/api/search")
                    return await ProxySlimAsync("search_v2_" + QueryHash(query),
                        "https://openlibrary.org/search.json?" + query, TimeSpan.FromHours(24), ProjectSearchAsync);

                if (method == "GET" && path.StartsWith("/api/subjects/"))
                {
                    string slug = path.Substring("/api/subjects/".Length);
                    return await ProxySlimAsync("subj_v2_" + slug + "_" + QueryHash(query),
                        "https://openlibrary.org/subjects/" + slug + ".json?" + query, TimeSpan.FromHours(24), ProjectSearchAsync);
                }

                if (method == "GET" && path.StartsWith("/api/cover/"))
                {
                    // 形如 /api/cover/12539702/L.jpg
                    string[] cp = path.Substring("/api/cover/".Length).Split('/');
                    if (cp.Length == 2 && int.TryParse(cp[0], out int coverId))
                    {
                        string size = cp[1].Replace(".jpg", "", StringComparison.OrdinalIgnoreCase)
                                       .ToUpperInvariant();
                        if (size is "S" or "M" or "L")
                            return await GetCoverAsync(coverId, size);
                    }
                }

                if (method == "GET" && path.StartsWith("/api/work/"))
                    return await GetWorkAsync(path.Substring("/api/work/".Length));

                if (method == "GET" && path.StartsWith("/api/ratings/"))
                    return await ProxySlimAsync("ratings_" + path.Substring("/api/ratings/".Length),
                        "https://openlibrary.org/works/" + path.Substring("/api/ratings/".Length) + "/ratings.json",
                        TimeSpan.FromHours(24), null);

                if (method == "GET" && path.StartsWith("/api/authors/"))
                    return await ProxySlimAsync("author_" + path.Substring("/api/authors/".Length),
                        "https://openlibrary.org/authors/" + path.Substring("/api/authors/".Length) + ".json",
                        TimeSpan.FromDays(7), null);

                if (method == "GET" && path == "/api/favorites")
                    return JsonResult(File.Exists(_favoritesPath) ? await File.ReadAllTextAsync(_favoritesPath) : "[]");

                if (method == "POST" && path == "/api/favorites" && body != null)
                {
                    string tmp = _favoritesPath + ".tmp";
                    await File.WriteAllBytesAsync(tmp, body);
                    File.Move(tmp, _favoritesPath, true);
                    return JsonResult("{\"ok\":true}");
                }

                // 翻译引擎状态 / 实测 / 触发安装(离线翻译功能)
                if (method == "GET" && path == "/api/translate/status")
                    return JsonResult(_translator.StatusJson());

                if (method == "GET" && path == "/api/translate/probe")
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var zh = await _translator.TranslateBatchAsync(new[] { "Hello world" });
                        sw.Stop();
                        return JsonResult(JsonSerializer.Serialize(new
                        {
                            ok = true, text = zh[0], elapsedMs = sw.ElapsedMilliseconds,
                        }));
                    }
                    catch (Exception ex)
                    {
                        return JsonResult(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
                    }
                }

                if (method == "POST" && path == "/api/translate/install")
                    return JsonResult(_translator.LaunchInstaller());

                if (method == "GET")
                    return ServeStatic(path);

                return (404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));
            }
            catch (Exception)
            {
                return (500, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"internal\"}"));
            }
        }

        // ---------------- Open Library 代理与缓存 ----------------

        /// <summary>
        /// 代理上游接口;ttl 内命中缓存直接返回,网络失败时用任意旧缓存兜底(注入 cached:true)。
        /// project 非空时把上游大 JSON 瘦身为前端需要的最小字段(异步,支持书名翻译)。
        /// </summary>
        private async Task<(int, string, byte[])> ProxySlimAsync(string cacheKey, string url, TimeSpan ttl, Func<JsonDocument, Task<string>>? project)
        {
            string cacheFile = Path.Combine(_cacheDir, cacheKey + ".json");
            string? fresh = ReadCache(cacheFile, ttl);
            if (fresh != null) return JsonResult(fresh);

            try
            {
                string json = await FetchWithFallbackAsync(url);
                if (project != null)
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    json = await project(doc);
                }
                try { File.WriteAllText(cacheFile, json); } catch { }
                return JsonResult(json);
            }
            catch (Exception)
            {
                string? stale = ReadCache(cacheFile, TimeSpan.MaxValue);
                if (stale != null)
                {
                    if (project == null)
                        stale = "{\"cached\":true,\"data\":" + stale + "}";
                    else
                        stale = InjectCached(JsonDocument.Parse(stale).RootElement);
                    return JsonResult(stale);
                }
                return (502, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"network\"}"));
            }
        }

        private static string? ReadCache(string file, TimeSpan ttl)
        {
            if (File.Exists(file) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)) < ttl)
                return File.ReadAllText(file);
            return null;
        }

        private static byte[]? ReadCacheBytes(string file, TimeSpan ttl)
        {
            if (File.Exists(file) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)) < ttl)
                return File.ReadAllBytes(file);
            return null;
        }

        /// <summary>给缓存的瘦身 JSON 注入 cached:true 标记(前端据此提示"离线缓存数据")。</summary>
        private static string InjectCached(JsonElement obj)
        {
            var d = new Dictionary<string, object?>();
            foreach (var p in obj.EnumerateObject())
                d[p.Name] = JsonSerializer.Deserialize<object?>(p.Value.GetRawText());
            d["cached"] = true;
            return JsonSerializer.Serialize(d);
        }

        /// <summary>书籍详情:取 work JSON;若 key 是 edition 则通过 /books 接口回退解析出 work。</summary>
        private async Task<(int, string, byte[])> GetWorkAsync(string key)
        {
            string cacheFile = Path.Combine(_cacheDir, "work_" + key + ".json");
            string? cached = ReadCache(cacheFile, TimeSpan.FromHours(24));
            if (cached != null)
            {
                // 旧版本写入的缓存简介还是英文:翻译一次并写回,之后再命中即为中文版
                string translated = await TranslateWorkFieldsAsync(cached);
                if (translated != cached)
                {
                    try { File.WriteAllText(cacheFile, translated); } catch { }
                    cached = translated;
                }
                return JsonResult(cached);
            }
            try
            {
                string json = await FetchWithFallbackAsync("https://openlibrary.org/works/" + key + ".json");
                json = await TranslateWorkFieldsAsync(json);
                File.WriteAllText(cacheFile, json);
                return JsonResult(json);
            }
            catch (HttpRequestException)
            {
                // 可能是 edition key(/books/..M):从 books 接口找到所属 work 再取
                try
                {
                    string bookJson = await FetchWithFallbackAsync("https://openlibrary.org/books/" + key + ".json");
                    using JsonDocument doc = JsonDocument.Parse(bookJson);
                    if (doc.RootElement.TryGetProperty("works", out JsonElement ws) && ws.GetArrayLength() > 0)
                    {
                        string workKey = ws[0].GetProperty("key").GetString()?.Replace("/works/", "") ?? "";
                        string json = await FetchWithFallbackAsync("https://openlibrary.org/works/" + workKey + ".json");
                        json = await TranslateWorkFieldsAsync(json);
                        File.WriteAllText(cacheFile, json);
                        return JsonResult(json);
                    }
                }
                catch { }
                string? old404 = ReadCache(cacheFile, TimeSpan.MaxValue);
                if (old404 != null) return JsonResult(old404);
                return (404, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"not_found\"}"));
            }
            catch (Exception)
            {
                string? old = ReadCache(cacheFile, TimeSpan.MaxValue);
                if (old != null)
                    return JsonResult("{\"cached\":true,\"data\":" + old + "}");
                return (502, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"error\":\"network\"}"));
            }
        }

        /// <summary>
        /// 封面经本地代理取回并缓存 30 天。封面 CDN 返回 302 重定向到 archive.org(国内直连被阻断,
        /// HttpClient 自动跟随重定向且全程走代理);代理节点偶发卡顿,故最多重试 3 次。
        /// </summary>
        private async Task<(int, string, byte[])> GetCoverAsync(int coverId, string size)
        {
            string cacheFile = Path.Combine(_cacheDir, "cover_" + coverId + size + ".jpg");
            byte[]? cached = ReadCacheBytes(cacheFile, TimeSpan.FromDays(30));
            if (cached != null) return (200, "image/jpeg", cached);

            string url = "https://covers.openlibrary.org/b/id/" + coverId + "-" + size + ".jpg";
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    byte[] bytes = await FetchBytesWithFallbackAsync(url);
                    try { File.WriteAllBytes(cacheFile, bytes); } catch { }
                    return (200, "image/jpeg", bytes);
                }
                catch (Exception)
                {
                    if (attempt == 3)
                    {
                        byte[]? old = ReadCacheBytes(cacheFile, TimeSpan.MaxValue);
                        if (old != null) return (200, "image/jpeg", old);
                        return (502, "image/jpeg", Array.Empty<byte>());
                    }
                    await Task.Delay(1200 * attempt);
                }
            }
            return (502, "image/jpeg", Array.Empty<byte>());
        }

        // ---------------- 中文翻译(书名 + 简介) ----------------

        private static readonly Regex CjkRegex = new(@"[一-鿿]");
        private static readonly JsonSerializerOptions JsonRelaxed = new()
        {
            // 宽松转义:中文直接输出,避免 \uXXXX 序列(缓存与响应均为可读中文)
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 把 work JSON 中的书名和简介翻译成中文(原文保留在 title_original / description_original)。
        /// 翻译失败或原文已含中文时保持原样;任何异常都不影响详情返回。
        /// </summary>
        private async Task<string> TranslateWorkFieldsAsync(string workJson)
        {
            try
            {
                JsonNode? node = JsonNode.Parse(workJson);
                if (node == null) return workJson;
                bool changed = false;

                // 书名
                if (node["title"] is JsonValue titleVal)
                {
                    string origTitle = titleVal.GetValue<string>() ?? "";
                    string zhTitle = await TranslateTitleAsync(origTitle);
                    if (zhTitle != origTitle)
                    {
                        node["title"] = zhTitle;
                        node["title_original"] = origTitle;
                        changed = true;
                    }
                }

                // 简介(可能是字符串或 {value:...})
                JsonNode? desc = node["description"];
                if (desc != null)
                {
                    string? original = desc is JsonValue
                        ? desc.GetValue<string>()
                        : desc["value"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(original))
                    {
                        string zh = await TranslateToChineseAsync(original);
                        if (zh != original)
                        {
                            node["description"] = zh;
                            node["description_original"] = original;
                            changed = true;
                        }
                    }
                }

                // 主题标签(subjects 数组)→ 中文,原文保留在 subjects_original
                if (node["subjects"] is JsonArray subArr && subArr.Count > 0)
                {
                    var originals = subArr
                        .Select(x => x?.GetValue<string>() ?? "")
                        .ToArray();
                    string[] zh = await TranslateSubjectsAsync(originals);
                    bool anyChanged = false;
                    for (int i = 0; i < zh.Length; i++)
                        if (zh[i] != originals[i]) { anyChanged = true; break; }
                    if (anyChanged)
                    {
                        node["subjects"] = new JsonArray(zh.Select(x => JsonValue.Create(x)).ToArray());
                        node["subjects_original"] =
                            new JsonArray(originals.Select(x => JsonValue.Create(x)).ToArray());
                        changed = true;
                    }
                }

                return changed ? node.ToJsonString(JsonRelaxed) : workJson;
            }
            catch { return workJson; }
        }

        /// <summary>把英文书名翻译成中文;结果按原文哈希缓存 30 天。失败时返回原标题。</summary>
        private async Task<string> TranslateTitleAsync(string title)
        {
            if (string.IsNullOrWhiteSpace(title) || CjkRegex.IsMatch(title)) return title; // 已含中文,不翻译
            string hash = QueryHash("t:" + title);
            string cacheFile = Path.Combine(_cacheDir, "trans_" + hash + ".txt");
            string? cached = ReadCache(cacheFile, TimeSpan.FromDays(30));
            if (cached != null) return cached;

            await _translateGate.WaitAsync();
            try
            {
                string zh = await TranslateChunkAsync(title);
                if (zh == title || string.IsNullOrWhiteSpace(zh)) return title;
                try { File.WriteAllText(cacheFile, zh); } catch { }
                return zh;
            }
            finally { _translateGate.Release(); }
        }

        /// <summary>把英文简介翻译成中文;结果按原文哈希缓存 30 天。失败时返回原文。</summary>
        private async Task<string> TranslateToChineseAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || CjkRegex.IsMatch(text)) return text; // 已含中文,不翻译
            string hash = QueryHash(text);
            string cacheFile = Path.Combine(_cacheDir, "trans_" + hash + ".txt");
            string? cached = ReadCache(cacheFile, TimeSpan.FromDays(30));
            if (cached != null) return cached;

            await _translateGate.WaitAsync();
            try
            {
                // 分块翻译:在线接口单请求 URL 有长度限制;本地模型受位置上限约束更短。
                // 任一块失败则整体回退原文,避免中英混杂。
                int chunkSize = _translator.LocalUsable ? 1600 : 3800;
                var parts = new List<string>();
                for (int i = 0; i < text.Length; i += chunkSize)
                    parts.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));

                var sb = new StringBuilder();
                foreach (string part in parts)
                {
                    string zh = await TranslateChunkAsync(part);
                    if (zh == part) return text; // 该块失败 → 整体回退
                    sb.Append(zh);
                }
                string result = sb.ToString();
                if (result.Length == 0) return text;
                try { File.WriteAllText(cacheFile, result); } catch { }
                return result;
            }
            finally { _translateGate.Release(); }
        }

        /// <summary>调用在线翻译接口翻译一个分块:Google 优先,失败自动换 MyMemory 免费接口兜底。
        /// 与 Open Library 共用同一套通道自动切换;两者都失败时返回原文(由调用方决定是否回退)。</summary>
        private async Task<string> TranslateChunkAsync(string chunk)
        {
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q="
                         + Uri.EscapeDataString(chunk);
            try
            {
                string resp = await FetchWithFallbackAsync(url);
                using var doc = JsonDocument.Parse(resp);
                var sb = new StringBuilder();
                if (doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].ValueKind == JsonValueKind.Array)
                    foreach (JsonElement seg in doc.RootElement[0].EnumerateArray())
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                            sb.Append(seg[0].GetString());
                if (sb.Length > 0) return sb.ToString();
            }
            catch { /* Google 失败 → 换备用接口 */ }

            // 备用:MyMemory 免费翻译接口(无需 API key),单条请求
            try
            {
                string alt = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(chunk)
                             + "&langpair=en|zh-CN";
                string resp = await FetchWithFallbackAsync(alt);
                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("responseData", out JsonElement rd)
                    && rd.TryGetProperty("translatedText", out JsonElement tt))
                {
                    string t = tt.GetString() ?? "";
                    if (t.Length > 0) return t;
                }
            }
            catch { }
            return chunk;
        }

        /// <summary>多行文本合并为一次翻译请求(换行分隔),按行拆回结果。
        /// 用于主题标签批量翻译:单次请求翻译 10 个标签,请求数少 10 倍,大幅降低被限流的概率。
        /// 整体失败返回全空串;行数与输入不符时,无法对齐的位置返回空串,由调用方逐条兜底。</summary>
        private async Task<string[]> TranslateLinesAsync(string[] lines)
        {
            string joined = string.Join("\n", lines);
            string zh = await TranslateChunkAsync(joined);
            if (zh == joined || string.IsNullOrEmpty(zh)) return new string[lines.Length];
            string[] parts = zh.Split('\n');
            var result = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
                result[i] = i < parts.Length ? parts[i].Trim() : "";
            return result;
        }

        /// <summary>翻译单个分块:本地离线引擎优先,失败自动回退在线 Google 接口。</summary>
        private async Task<string> TranslateChunkWithFallbackAsync(string chunk)
        {
            if (_translator.LocalUsable)
            {
                try { return (await _translator.TranslateBatchAsync(new[] { chunk }))[0]; }
                catch { /* 本地失败 → 在线回退 */ }
            }
            return await TranslateChunkAsync(chunk);
        }

        /// <summary>
        /// 主题标签批量翻译成中文:逐条查缓存(前缀 "s:" 防与书名哈希碰撞),
        /// 未命中时本地引擎整批一次翻译;否则按 10 个一批合并为一次在线请求(换行分隔),
        /// 行数不符/翻译失败的单条再逐条兜底。中文/空白原样保留。
        /// </summary>
        private async Task<string[]> TranslateSubjectsAsync(string[] subjects)
        {
            var result = new string[subjects.Length];
            var miss = new List<(int idx, string text)>();
            for (int i = 0; i < subjects.Length; i++)
            {
                string s = (subjects[i] ?? "").Trim();
                if (s.Length == 0 || CjkRegex.IsMatch(s)) { result[i] = s; continue; }
                string cacheFile = Path.Combine(_cacheDir, "trans_" + QueryHash("s:" + s) + ".txt");
                string? c = ReadCache(cacheFile, TimeSpan.FromDays(30));
                if (c != null) result[i] = c;
                else miss.Add((i, s));
            }
            if (miss.Count == 0) return result;

            await _translateGate.WaitAsync();
            try
            {
                if (_translator.LocalUsable)
                {
                    try
                    {
                        string[] zh = await _translator.TranslateBatchAsync(miss.Select(x => x.text).ToArray());
                        if (zh.Length == miss.Count)
                        {
                            for (int k = 0; k < miss.Count; k++)
                            {
                                string z = zh[k];
                                if (z.Length > 0 && z != miss[k].text)
                                {
                                    result[miss[k].idx] = z;
                                    try
                                    {
                                        File.WriteAllText(Path.Combine(_cacheDir,
                                            "trans_" + QueryHash("s:" + miss[k].text) + ".txt"), z);
                                    }
                                    catch { }
                                }
                            }
                            return result;
                        }
                    }
                    catch { /* 整批失败 → 在线批量兜底 */ }
                }

                // 在线批量:每 10 个标签合并为一次请求;整体失败或行数不对齐的条目逐条兜底
                for (int start = 0; start < miss.Count; start += 10)
                {
                    var batch = miss.Skip(start).Take(10).ToList();
                    string[] zh = await TranslateLinesAsync(batch.Select(x => x.text).ToArray());
                    for (int k = 0; k < batch.Count; k++)
                    {
                        string z = zh[k];
                        if (z.Length > 0 && z != batch[k].text)
                        {
                            result[batch[k].idx] = z;
                            try
                            {
                                File.WriteAllText(Path.Combine(_cacheDir,
                                    "trans_" + QueryHash("s:" + batch[k].text) + ".txt"), z);
                            }
                            catch { }
                        }
                        else
                        {
                            // 该条失败 → 单条重试一次
                            result[batch[k].idx] = await TranslateChunkWithFallbackAsync(batch[k].text);
                        }
                    }
                }
            }
            finally { _translateGate.Release(); }
            return result;
        }

        // ---------------- 瘦身投影 ----------------

        /// <summary>trending/daily.json → {works:[{key,title,title_original,author_name,cover_i,first_publish_year}]}(书名翻译为中文)</summary>
        private async Task<string> ProjectTrendingAsync(JsonDocument doc)
        {
            var works = new List<Dictionary<string, object?>>();
            if (doc.RootElement.TryGetProperty("works", out JsonElement arr))
                foreach (JsonElement w in arr.EnumerateArray())
                    works.Add(await ProjectWorkAsync(w));
            return JsonSerializer.Serialize(new { source = "openlibrary", cached = false, works }, JsonRelaxed);
        }

        /// <summary>search.json / subjects API → {total, works:[{...}]}(subjects 结果可能包一层 work;书名翻译为中文)。
        /// search.json 的书目在 docs 数组,subjects 接口在 works 数组,两者都要处理。</summary>
        private async Task<string> ProjectSearchAsync(JsonDocument doc)
        {
            var works = new List<Dictionary<string, object?>>();
            JsonElement root = doc.RootElement;
            long total = 0;
            if (root.TryGetProperty("numFound", out JsonElement nf)) total = nf.GetInt64();
            else if (root.TryGetProperty("work_count", out JsonElement wc)) total = wc.GetInt64();

            if (root.TryGetProperty("works", out JsonElement arr))
            {
                foreach (JsonElement w in arr.EnumerateArray())
                {
                    JsonElement work = w.TryGetProperty("work", out JsonElement ww) ? ww : w;
                    var d = await ProjectWorkAsync(work);
                    // subjects API 用 cover_id,归一化到 cover_i
                    if (d["cover_i"] == null && d["cover_id"] != null) d["cover_i"] = d["cover_id"];
                    works.Add(d);
                }
            }
            else if (root.TryGetProperty("docs", out JsonElement docs))
            {
                // search.json 的书目在 docs 数组(字段:title/author_name/cover_i/first_publish_year 等)
                foreach (JsonElement w in docs.EnumerateArray())
                    works.Add(await ProjectWorkAsync(w));
            }
            return JsonSerializer.Serialize(new { source = "openlibrary", cached = false, total, works }, JsonRelaxed);
        }

        /// <summary>提取单本书的最小字段,并把书名翻译为中文(原名列在 title_original)。</summary>
        private async Task<Dictionary<string, object?>> ProjectWorkAsync(JsonElement work)
        {
            var d = Pick(work, "key", "title", "author_name", "cover_i", "cover_id",
                "first_publish_year", "ratings_average", "ratings_count", "edition_count");
            // subjects API 的 authors 是 [{name}],归一化到 author_name
            if (d["author_name"] == null && work.TryGetProperty("authors", out JsonElement auths))
            {
                var names = new List<string>();
                foreach (JsonElement a in auths.EnumerateArray())
                    if (a.TryGetProperty("name", out JsonElement nm)) names.Add(nm.GetString() ?? "");
                if (names.Count > 0) d["author_name"] = names;
            }
            // 书名直接从 JsonElement 取字符串(Deserialize&lt;object&gt; 返回的是 JsonElement,不能 as string)
            string orig = work.TryGetProperty("title", out JsonElement te) ? te.GetString() ?? "" : "";
            string zh = await TranslateTitleAsync(orig);
            if (zh != orig) { d["title"] = zh; d["title_original"] = orig; }
            return d;
        }

        /// <summary>按字段名提取元素原始 JSON(保持类型:字符串/数字/数组)。</summary>
        private static Dictionary<string, object?> Pick(JsonElement e, params string[] names)
        {
            var d = new Dictionary<string, object?>();
            foreach (string n in names)
            {
                if (e.TryGetProperty(n, out JsonElement v))
                    d[n] = JsonSerializer.Deserialize<object?>(v.GetRawText());
                else
                    d[n] = null;
            }
            return d;
        }

        // ---------------- 静态资源 ----------------

        private (int, string, byte[]) ServeStatic(string path)
        {
            string rel = Uri.UnescapeDataString(path.TrimStart('/'));
            if (rel.Length == 0) rel = "index.html";
            string full = Path.GetFullPath(Path.Combine(_wwwRoot, rel));
            if (!full.StartsWith(_wwwRoot, StringComparison.OrdinalIgnoreCase))
                return (403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("forbidden"));
            if (File.Exists(full))
                return (200, ContentTypeFor(full), File.ReadAllBytes(full));
            return (404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));
        }

        private static string ContentTypeFor(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            return ext switch
            {
                ".html" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                ".woff2" => "font/woff2",
                _ => "application/octet-stream"
            };
        }

        // ---------------- 小工具 ----------------

        private static (int, string, byte[]) JsonResult(string json)
            => (200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

        private static string QueryHash(string query)
        {
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(query));
            return Convert.ToHexString(h)[..16];
        }
    }
}
