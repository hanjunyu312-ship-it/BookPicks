using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BookPicks
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
                return RunSelfTest().GetAwaiter().GetResult();

            // 单实例保护:避免双开导致缓存文件并发读写冲突
            using var mutex = new Mutex(true, @"Global\BookPicks_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("BookPicks 已经在运行了,请到任务栏切换窗口。",
                    "韩俊宇的书库", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            // 先启动本地服务器(提供前端界面 + Open Library 代理)
            var server = new LocalServer();
            server.Run();

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookPicks");
                Directory.CreateDirectory(dir);
                // 记录端口,便于调试排查
                File.WriteAllText(Path.Combine(dir, "port.txt"), server.Port.ToString());
            }
            catch { }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(server.BaseUrl));
            server.Dispose();
            return 0;
        }

        /// <summary>
        /// 自检模式:启动本地服务器并逐一验证关键接口(数据源可达性、缓存、收藏读写),
        /// 结果写入 %LOCALAPPDATA%\BookPicks\selftest.txt,退出码 0=全部通过。
        /// 用法:BookPicks.exe --selftest
        /// </summary>
        private static async Task<int> RunSelfTest()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookPicks");
            Directory.CreateDirectory(appData);
            string reportPath = Path.Combine(appData, "selftest.txt");

            var server = new LocalServer();
            server.Run();
            string b = server.BaseUrl;
            var report = new StringBuilder();
            report.AppendLine("BookPicks 自检报告 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("本地端口: " + server.Port + " | 代理: " + server.ProxyInfo);
            report.AppendLine();
            var fails = new List<string>();

            void Record(string name, bool ok, string detail = "")
            {
                report.AppendLine((ok ? "[通过] " : "[失败] ") + name + (detail.Length > 0 ? "  | " + detail : ""));
                if (!ok) fails.Add(name);
            }

            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) })
            {
                async Task<(bool ok, string body)> Get(string url)
                {
                    try
                    {
                        var r = await client.GetAsync(url);
                        return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
                    }
                    catch (Exception ex) { return (false, ex.Message); }
                }

                // 1. 静态首页
                var (ok1, body1) = await Get(b + "index.html");
                Record("静态首页 index.html", ok1 && body1.Contains("韩俊宇的书库"),
                    ok1 ? (body1.Length + " 字节") : body1.Substring(0, Math.Min(200, body1.Length)));

                // 2. 今日热榜(Open Library 全球趋势,每日更新)
                var (ok2, body2) = await Get(b + "api/trending");
                bool trendingOk = ok2 && body2.Contains("\"works\"");
                Record("今日热榜 /api/trending", trendingOk, trendingOk ? body2.Length + " 字节" : body2);

                // 3. 分类浏览(书库)
                var (ok3, body3) = await Get(b + "api/subjects/fiction?offset=0&limit=3");
                Record("书库分类 /api/subjects/fiction", ok3 && body3.Contains("\"works\""),
                    ok3 ? body3.Length + " 字节" : body3);

                // 4. 关键词搜索
                var (ok4, body4) = await Get(b + "api/search?q=harry+potter&page=1&limit=3");
                Record("搜索 /api/search?q=harry+potter", ok4 && body4.Contains("\"works\""),
                    ok4 ? body4.Length + " 字节" : body4);

                // 5. 书籍详情(含简介)
                var (ok5, body5) = await Get(b + "api/work/OL17930368W");
                Record("书籍详情 /api/work/OL17930368W", ok5 && body5.Contains("Atomic Habits"),
                    ok5 ? "返回 " + body5.Length + " 字节" : body5.Substring(0, Math.Min(200, body5.Length)));

                // 5b. 简介中文翻译(解析 JSON 检查 description 是否含中文字符)
                bool hasCjk = false;
                try
                {
                    using var doc5 = System.Text.Json.JsonDocument.Parse(body5);
                    if (doc5.RootElement.TryGetProperty("description", out var dd5) &&
                        dd5.ValueKind == System.Text.Json.JsonValueKind.String)
                        hasCjk = System.Text.RegularExpressions.Regex.IsMatch(
                            dd5.GetString() ?? "", "[一-鿿]");
                }
                catch { }
                Record("简介中文翻译 /api/work", ok5 && hasCjk,
                    hasCjk ? "简介已翻译为中文 ✅" : "简介仍为英文(翻译服务不可达?详情弹窗可查看原文)");

                // 6. 评分
                var (ok6, body6) = await Get(b + "api/ratings/OL17930368W");
                Record("评分 /api/ratings/OL17930368W", ok6 && body6.Contains("summary"), body6.Substring(0, Math.Min(120, body6.Length)));

                // 7. 封面经本地代理取回(取热榜第一本有封面的书)
                bool coverOk = false;
                string coverDetail = "跳过(榜内无封面)";
                if (trendingOk)
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(body2);
                        foreach (var w in doc.RootElement.GetProperty("works").EnumerateArray())
                        {
                            if (w.TryGetProperty("cover_i", out var ci) && ci.ValueKind == System.Text.Json.JsonValueKind.Number)
                            {
                                var cr = await client.GetAsync(b + "api/cover/" + ci.GetInt64() + "/M.jpg");
                                bool isJpeg = cr.Content.Headers.ContentType != null &&
                                              cr.Content.Headers.ContentType.MediaType == "image/jpeg";
                                coverOk = cr.IsSuccessStatusCode && isJpeg && cr.Content.Headers.ContentLength > 0;
                                coverDetail = cr.StatusCode + " 封面ID=" + ci.GetInt64();
                                break;
                            }
                        }
                    }
                    catch { }
                }
                Record("封面代理 /api/cover", coverOk, coverDetail);

                // 8. 收藏读写(测试后恢复原数据)
                string favPath = Path.Combine(appData, "favorites.json");
                string original = File.Exists(favPath) ? await File.ReadAllTextAsync(favPath) : null;
                try
                {
                    var post = await client.PostAsync(b + "api/favorites",
                        new StringContent("[{\"key\":\"__selftest__\",\"title\":\"自检\"}]", Encoding.UTF8, "application/json"));
                    var get = await client.GetAsync(b + "api/favorites");
                    string got = await get.Content.ReadAsStringAsync();
                    Record("收藏读写 /api/favorites",
                        post.IsSuccessStatusCode && get.IsSuccessStatusCode && got.Contains("__selftest__"), got);
                }
                catch (Exception ex) { Record("收藏读写 /api/favorites", false, ex.Message); }
                finally
                {
                    if (original != null) await File.WriteAllTextAsync(favPath, original);
                    else if (File.Exists(favPath)) File.Delete(favPath);
                }

                // 9. 异常请求处理:不存在的 work key 应返回明确的 404 而非崩溃
                var (ok9, body9) = await Get(b + "api/work/__nonexistent__");
                bool expect404 = !ok9 && body9.Contains("not_found");
                Record("异常请求处理(应返回 404)", expect404, body9.Substring(0, Math.Min(120, body9.Length)));

                // 10. 原文保留:翻译后的详情应同时带 description_original(前端可切换查看原文)
                var (ok10, body10) = await Get(b + "api/work/OL17930368W");
                bool keepOrig = false;
                try
                {
                    using var doc10 = System.Text.Json.JsonDocument.Parse(body10);
                    if (doc10.RootElement.TryGetProperty("description", out var dd10) &&
                        dd10.ValueKind == System.Text.Json.JsonValueKind.String &&
                        System.Text.RegularExpressions.Regex.IsMatch(dd10.GetString() ?? "", "[一-鿿]"))
                        keepOrig = doc10.RootElement.TryGetProperty("description_original", out _);
                }
                catch { }
                Record("翻译保留原文 description_original", ok10 && keepOrig,
                    keepOrig ? "中文简介 + 英文原文同时可用" : "缺少原文字段或简介未翻译");

                // 11. 本地翻译引擎状态(离线翻译;未安装不算失败,前端有下载入口)
                var (ok11, body11) = await Get(b + "api/translate/status");
                string st11 = "unknown";
                try
                {
                    using var doc11 = System.Text.Json.JsonDocument.Parse(body11);
                    if (doc11.RootElement.TryGetProperty("status", out var stEl))
                        st11 = stEl.GetString() ?? "unknown";
                }
                catch { }
                Record("本地翻译引擎状态 /api/translate/status", ok11 && st11 is "ready" or "starting" or "notinstalled" or "installing" or "failed", st11);

                // 12. 标签翻译:详情 subjects 应含中文且保留 subjects_original(复用第 10 项响应)
                bool subjOk = false;
                string subjDetail = "无 subjects 字段";
                try
                {
                    using var doc12 = System.Text.Json.JsonDocument.Parse(body10);
                    if (doc12.RootElement.TryGetProperty("subjects", out var ss12) &&
                        ss12.ValueKind == System.Text.Json.JsonValueKind.Array &&
                        ss12.GetArrayLength() > 0)
                    {
                        bool hasCjk12 = ss12.EnumerateArray().Any(x =>
                            System.Text.RegularExpressions.Regex.IsMatch(x.GetString() ?? "", "[一-鿿]"));
                        bool hasOrig12 = doc12.RootElement.TryGetProperty("subjects_original", out _);
                        subjOk = hasCjk12 && hasOrig12;
                        subjDetail = (hasCjk12 ? "标签已翻译 ✅" : "标签仍为英文")
                                   + (hasOrig12 ? " + 原文保留" : "(缺 subjects_original)");
                    }
                }
                catch { }
                Record("标签翻译 /api/work subjects", subjOk, subjDetail);

                // 13. 本地引擎实测(仅就绪时;未就绪记"跳过"不判失败)
                if (st11 == "ready")
                {
                    var (ok13, body13) = await Get(b + "api/translate/probe");
                    bool hasCjk13 = System.Text.RegularExpressions.Regex.IsMatch(body13, "[一-鿿]");
                    Record("本地引擎实测 /api/translate/probe", ok13 && hasCjk13, body13);
                }
                else
                {
                    Record("本地引擎实测 /api/translate/probe", true, "跳过(引擎未就绪,使用在线翻译)");
                }
            }

            report.AppendLine();
            report.AppendLine(fails.Count == 0
                ? "自检结果:全部通过 ✅"
                : $"自检结果:{fails.Count} 项失败 ❌(" + string.Join(", ", fails) + ")");

            server.Dispose();
            // UTF-8 带 BOM,保证记事本/PowerShell 直接打开不乱码
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true));
            return fails.Count == 0 ? 0 : 1;
        }
    }
}
