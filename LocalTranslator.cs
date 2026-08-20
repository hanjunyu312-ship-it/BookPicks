using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BookPicks
{
    /// <summary>
    /// 本地离线翻译引擎:管理 transformers MarianMT 子进程(JSON 行协议)。
    /// 状态机 NotInstalled → Installing → Starting → Ready(→ Failed 反复崩溃后回退在线)。
    /// 懒启动:首次翻译请求触发;PrewarmAsync 请求热榜时自然带着引擎后台加载。
    /// </summary>
    public sealed class LocalTranslator : IDisposable
    {
        public enum Status { NotInstalled, Installing, Starting, Ready, Failed }

        private const int StartTimeoutSec = 180;     // 首次 torch import + 模型加载可达 30-90s
        private const int RequestTimeoutSec = 120;   // 只覆盖单次推理
        private const int MaxRestartsPer10Min = 3;

        private static readonly string AppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookPicks");
        private static string VenvPython => Path.Combine(AppData, "translator", "venv", "Scripts", "python.exe");
        private static string ServerPy => Path.Combine(AppContext.BaseDirectory, "tools", "translate_server.py");
        private static string InstallerPs1 => Path.Combine(AppContext.BaseDirectory, "tools", "install_translator.ps1");
        private static string ModelDir => Path.Combine(AppData, "models", "opus-mt-en-zh");
        private static string Marker => Path.Combine(AppData, "translator", "installed.json");
        private static string LogFile => Path.Combine(AppData, "translator", "server.log");

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<string[]>> _pending = new();
        private readonly List<DateTime> _restarts = new();
        private readonly StringBuilder _stderrTail = new();
        private readonly object _lock = new();
        private Process? _proc;
        private Task _startTask = Task.CompletedTask;
        private long _nextId;
        private System.Threading.Timer? _heartbeat;
        private TaskCompletionSource? _pongTcs;      // 心跳专用,与普通请求分开

        public Status State { get; private set; } = Status.NotInstalled;
        public string LastError { get; private set; } = "";

        /// <summary>本地引擎是否已安装(venv + 完成标记 + 模型权重齐全)。</summary>
        public bool Installed =>
            File.Exists(VenvPython) && File.Exists(Marker)
            && File.Exists(Path.Combine(ModelDir, "pytorch_model.bin"));

        public bool LocalUsable => State is Status.Ready or Status.Starting;

        // ---------------- 启动 ----------------

        /// <summary>并发首调共享同一个启动任务;未安装或已在启动中则直接返回。</summary>
        public async Task EnsureStartedAsync()
        {
            if (LocalUsable || !Installed) return;
            lock (_lock)
            {
                if (!_startTask.IsCompleted) return;
                _startTask = StartAsync();
            }
            await _startTask;
        }

        private async Task StartAsync()
        {
            State = Status.Starting;
            var p = new Process { EnableRaisingEvents = true };
            p.StartInfo = new ProcessStartInfo
            {
                FileName = VenvPython,
                Arguments = $"\"{ServerPy}\" --model \"{ModelDir}\" --beams 4 --max-tokens 512",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            p.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            p.StartInfo.Environment["PYTHONUNBUFFERED"] = "1";
            p.StartInfo.Environment["TRANSFORMERS_OFFLINE"] = "1"; // 模型必须本地存在,杜绝偷偷联网
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            p.OutputDataReceived += (_, e) => OnStdout(p, ready, e.Data);
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) CaptureStderr(e.Data); };
            p.Exited += (_, _) => { if (ReferenceEquals(_proc, p)) OnExited(); };

            lock (_lock) _proc = p;
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                lock (_lock) _proc = null;
                State = Status.Failed;
                LastError = "启动失败:" + ex.Message;
                throw new IOException(LastError);
            }

            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(StartTimeoutSec));
                State = Status.Ready;
                LastError = "";
                StartHeartbeat();
            }
            catch (Exception ex)
            {
                Kill(p);
                State = Status.Failed;
                LastError = "模型加载超时或失败,已回退在线翻译(" + ex.Message + ")";
                throw new IOException(LastError);
            }
        }

        private void OnStdout(Process p, TaskCompletionSource ready, string? line)
        {
            if (string.IsNullOrEmpty(line) || !ReferenceEquals(_proc, p)) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement r = doc.RootElement;
                if (r.TryGetProperty("event", out JsonElement ev))
                {
                    if (ev.GetString() == "ready") ready.TrySetResult();
                    else if (ev.GetString() == "fatal")
                        ready.TrySetException(new Exception(r.TryGetProperty("error", out JsonElement er)
                            ? er.GetString() : "引擎启动失败"));
                    return;
                }
                if (r.TryGetProperty("id", out JsonElement idEl))
                {
                    if (idEl.GetInt64() == -1 && r.TryGetProperty("pong", out _))
                    {
                        _pongTcs?.TrySetResult();
                        return;
                    }
                    if (_pending.TryRemove(idEl.GetInt64(), out var tcs))
                    {
                        if (r.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean()
                            && r.TryGetProperty("texts", out JsonElement arr))
                            tcs.TrySetResult(arr.EnumerateArray()
                                .Select(x => x.GetString() ?? "").ToArray());
                        else
                            tcs.TrySetException(new Exception(r.TryGetProperty("error", out JsonElement er)
                                ? er.GetString() : "引擎返回错误"));
                    }
                }
            }
            catch { /* 非 JSON 行忽略 */ }
        }

        // ---------------- 请求 ----------------

        /// <summary>批量翻译;并发串行化,超时/崩溃自动重启,失败抛异常由调用方回退在线。</summary>
        public async Task<string[]> TranslateBatchAsync(IReadOnlyList<string> texts)
        {
            if (!Installed) throw new InvalidOperationException("本地引擎未安装");
            await _gate.WaitAsync();
            try
            {
                await EnsureStartedAsync();
                if (State != Status.Ready || _proc == null)
                    throw new IOException("本地引擎不可用:" + LastError);

                long id = Interlocked.Increment(ref _nextId);
                var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[id] = tcs;
                try
                {
                    await _proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, texts }));
                    await _proc.StandardInput.FlushAsync();
                }
                catch (Exception ex)
                {
                    _pending.TryRemove(id, out _);
                    tcs.TrySetException(new IOException("引擎进程不可写", ex));
                    ScheduleRestart(); // fire-and-forget,防持锁死锁
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RequestTimeoutSec));
                using var reg = cts.Token.Register(() =>
                    tcs.TrySetException(new TimeoutException("本地翻译超时")));
                try { return await tcs.Task; }
                catch
                {
                    _pending.TryRemove(id, out _);
                    ScheduleRestart();
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        // ---------------- 崩溃 / 重启策略 ----------------

        private void OnExited()
        {
            foreach (var (_, tcs) in _pending.ToArray())
                tcs.TrySetException(new IOException("引擎进程退出"));
            _pending.Clear();
            ScheduleRestart();
        }

        /// <summary>10 分钟内最多重启 3 次,超限置 Failed,本会话回退在线翻译。</summary>
        private void ScheduleRestart()
        {
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                _restarts.RemoveAll(x => now - x > TimeSpan.FromMinutes(10));
                if (_restarts.Count >= MaxRestartsPer10Min)
                {
                    State = Status.Failed;
                    LastError = "引擎反复崩溃,已回退在线翻译";
                    return;
                }
                _restarts.Add(now);
            }
            Kill(_proc);
            State = Status.Starting;
            _startTask = Task.Run(async () =>
            {
                try { await StartAsync(); }
                catch { /* 失败已置 Failed,由上层回退在线 */ }
            });
        }

        // ---------------- 心跳 ----------------

        private void StartHeartbeat()
        {
            _heartbeat ??= new System.Threading.Timer(_ =>
            {
                if (State != Status.Ready || _proc == null || _pending.Count > 0 || !_gate.Wait(0))
                    return;
                try
                {
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pongTcs = tcs;
                    _proc.StandardInput.WriteLineAsync("{\"id\":-1,\"ping\":true}").Wait(2000);
                    if (!tcs.Task.Wait(TimeSpan.FromSeconds(10)))
                        ScheduleRestart();
                }
                catch { ScheduleRestart(); }
                finally { _pongTcs = null; _gate.Release(); }
            }, null, 60_000, 60_000);
        }

        // ---------------- 安装 ----------------

        /// <summary>弹出可见 PowerShell 窗口运行安装脚本(窗口即进度 UI)。已安装或安装中拒绝重复触发。</summary>
        public string LaunchInstaller()
        {
            if (Installed) return JsonSerializer.Serialize(new { ok = true, already = true });
            if (State == Status.Installing)
                return JsonSerializer.Serialize(new { ok = false, error = "安装正在进行中" });
            if (!File.Exists(InstallerPs1))
                return JsonSerializer.Serialize(new { ok = false, error = "缺少 install_translator.ps1" });
            try
            {
                State = Status.Installing;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{InstallerPs1}\"",
                    UseShellExecute = true,
                });
                return JsonSerializer.Serialize(new { ok = true });
            }
            catch (Exception ex)
            {
                State = Status.NotInstalled;
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        // ---------------- 状态 / 杂项 ----------------

        /// <summary>状态 JSON(供 /api/translate/status 与自检)。</summary>
        public string StatusJson()
        {
            // 安装窗口正在跑但未完成:Installing 态由 LaunchInstaller 设置,无需额外判断
            return JsonSerializer.Serialize(new
            {
                status = State.ToString().ToLowerInvariant(),
                installed = Installed,
                model = ModelDir,
                lastError = LastError,
                logTail = _stderrTail.ToString(),
            });
        }

        private void CaptureStderr(string line)
        {
            try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
            lock (_lock)
            {
                _stderrTail.Append(line).Append('\n');
                if (_stderrTail.Length > 4096) _stderrTail.Remove(0, _stderrTail.Length - 4096);
            }
        }

        private static void Kill(Process? p)
        {
            try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
            try { p?.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _heartbeat?.Dispose();
            Kill(_proc);
            _gate.Dispose();
        }
    }
}
