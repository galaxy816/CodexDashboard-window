using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using IOPath = System.IO.Path;

namespace CodexQuotaPet
{
    internal sealed class LimitInfo
    {
        public string Name;
        public int RemainingPercent;
        public long? ResetsAt;
        public long? WindowDurationMinutes;
    }

    internal sealed class QuotaSnapshot
    {
        public readonly List<LimitInfo> Limits = new List<LimitInfo>();
        public int? ResetCredits;
        public string Model;
        public string ReasoningEffort;
        public long? ContextUsedTokens;
        public long? ContextWindowTokens;
        public DateTime FetchedAt;
    }

    internal sealed class CodexRateLimitClient
    {
        private readonly object processLock = new object();
        private int protocolProcessId;

        public int ProtocolProcessId
        {
            get { lock (processLock) { return protocolProcessId; } }
        }

        public QuotaSnapshot Read()
        {
            string codexPath = FindCodexExecutable();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = codexPath;
            startInfo.Arguments = "app-server --stdio";
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("无法启动 Codex 本地服务。");

                lock (processLock) { protocolProcessId = process.Id; }

                string initialize = "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"codex-quota-pet\",\"version\":\"1.0.0\"},\"capabilities\":{\"experimentalApi\":true}}}";
                process.StandardInput.WriteLine(initialize);
                process.StandardInput.Flush();
                WaitForResponse(process, 1, 15000);

                process.StandardInput.WriteLine("{\"method\":\"initialized\",\"params\":{}}");
                process.StandardInput.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{}}");
                process.StandardInput.Flush();

                IDictionary<string, object> response = WaitForResponse(process, 2, 20000);
                object errorObject;
                if (response.TryGetValue("error", out errorObject) && errorObject != null)
                {
                    IDictionary<string, object> error = AsDictionary(errorObject);
                    string message = GetString(error, "message");
                    throw new InvalidOperationException(string.IsNullOrEmpty(message) ? "Codex 返回了额度读取错误。" : message);
                }

                IDictionary<string, object> result = AsDictionary(GetValue(response, "result"));
                QuotaSnapshot snapshot = ParseSnapshot(result);

                process.StandardInput.WriteLine("{\"id\":3,\"method\":\"thread/list\",\"params\":{\"archived\":false,\"limit\":12,\"sortKey\":\"recency_at\",\"sortDirection\":\"desc\"}}");
                process.StandardInput.Flush();
                IDictionary<string, object> threadResponse = WaitForResponse(process, 3, 20000);
                object threadError;
                if (!threadResponse.TryGetValue("error", out threadError) || threadError == null)
                {
                    IDictionary<string, object> threadResult = AsDictionary(GetValue(threadResponse, "result"));
                    AddCurrentThreadDetails(snapshot, threadResult);
                }
                return snapshot;
            }
            finally
            {
                lock (processLock) { protocolProcessId = 0; }
                try { process.StandardInput.Close(); } catch { }
                try
                {
                    if (!process.HasExited)
                    {
                        if (!process.WaitForExit(800)) process.Kill();
                    }
                }
                catch { }
                process.Dispose();
            }
        }

        private static IDictionary<string, object> WaitForResponse(Process process, int expectedId, int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            JavaScriptSerializer serializer = new JavaScriptSerializer();

            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                int remaining = Math.Max(1, timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
                Task<string> readTask = Task.Factory.StartNew(delegate { return process.StandardOutput.ReadLine(); });
                if (!readTask.Wait(remaining))
                    throw new TimeoutException("等待 Codex 额度数据超时。");

                string line = readTask.Result;
                if (line == null)
                    throw new InvalidOperationException("Codex 本地服务意外结束。");

                object parsed;
                try { parsed = serializer.DeserializeObject(line); }
                catch { continue; }

                IDictionary<string, object> message = parsed as IDictionary<string, object>;
                if (message == null) continue;
                object id;
                if (message.TryGetValue("id", out id) && Convert.ToInt32(id, CultureInfo.InvariantCulture) == expectedId)
                    return message;
            }

            throw new TimeoutException("等待 Codex 额度数据超时。");
        }

        private static QuotaSnapshot ParseSnapshot(IDictionary<string, object> result)
        {
            QuotaSnapshot snapshot = new QuotaSnapshot();
            snapshot.FetchedAt = DateTime.Now;

            object resetObject;
            if (result.TryGetValue("rateLimitResetCredits", out resetObject) && resetObject != null)
            {
                IDictionary<string, object> resets = AsDictionary(resetObject);
                object count;
                if (resets.TryGetValue("availableCount", out count) && count != null)
                    snapshot.ResetCredits = Convert.ToInt32(count, CultureInfo.InvariantCulture);
            }

            List<IDictionary<string, object>> buckets = new List<IDictionary<string, object>>();
            object byIdObject;
            if (result.TryGetValue("rateLimitsByLimitId", out byIdObject) && byIdObject != null)
            {
                IDictionary<string, object> byId = AsDictionary(byIdObject);
                foreach (KeyValuePair<string, object> pair in byId)
                {
                    if (pair.Value != null) buckets.Add(AsDictionary(pair.Value));
                }
            }

            if (buckets.Count == 0)
            {
                object legacy;
                if (result.TryGetValue("rateLimits", out legacy) && legacy != null)
                    buckets.Add(AsDictionary(legacy));
            }

            foreach (IDictionary<string, object> bucket in buckets)
            {
                string bucketName = GetString(bucket, "limitName");
                AddWindow(snapshot, bucket, "primary", bucketName);
                AddWindow(snapshot, bucket, "secondary", bucketName);
            }

            snapshot.Limits.Sort(delegate(LimitInfo left, LimitInfo right)
            {
                long a = left.WindowDurationMinutes.HasValue ? left.WindowDurationMinutes.Value : long.MaxValue;
                long b = right.WindowDurationMinutes.HasValue ? right.WindowDurationMinutes.Value : long.MaxValue;
                return a.CompareTo(b);
            });
            return snapshot;
        }

        private static void AddWindow(QuotaSnapshot snapshot, IDictionary<string, object> bucket, string key, string bucketName)
        {
            object windowObject;
            if (!bucket.TryGetValue(key, out windowObject) || windowObject == null) return;
            IDictionary<string, object> window = AsDictionary(windowObject);
            object usedObject;
            if (!window.TryGetValue("usedPercent", out usedObject) || usedObject == null) return;

            int used = Math.Max(0, Math.Min(100, Convert.ToInt32(usedObject, CultureInfo.InvariantCulture)));
            long? duration = GetNullableLong(window, "windowDurationMins");
            long? resetsAt = GetNullableLong(window, "resetsAt");
            string name = FormatWindowName(duration);
            if (!string.IsNullOrWhiteSpace(bucketName)) name = bucketName + " · " + name;

            bool duplicate = snapshot.Limits.Any(delegate(LimitInfo item)
            {
                return item.WindowDurationMinutes == duration && item.ResetsAt == resetsAt && item.RemainingPercent == 100 - used;
            });
            if (!duplicate)
            {
                snapshot.Limits.Add(new LimitInfo
                {
                    Name = name,
                    RemainingPercent = 100 - used,
                    ResetsAt = resetsAt,
                    WindowDurationMinutes = duration
                });
            }
        }

        private static void AddCurrentThreadDetails(QuotaSnapshot snapshot, IDictionary<string, object> threadResult)
        {
            object dataObject;
            if (!threadResult.TryGetValue("data", out dataObject) || dataObject == null) return;
            IEnumerable data = dataObject as IEnumerable;
            if (data == null) return;

            IDictionary<string, object> first = null;
            IDictionary<string, object> active = null;
            foreach (object item in data)
            {
                IDictionary<string, object> thread = item as IDictionary<string, object>;
                if (thread == null) continue;
                if (first == null) first = thread;
                object statusObject;
                if (thread.TryGetValue("status", out statusObject) && statusObject != null)
                {
                    IDictionary<string, object> status = statusObject as IDictionary<string, object>;
                    if (status != null && string.Equals(GetString(status, "type"), "active", StringComparison.OrdinalIgnoreCase))
                    {
                        active = thread;
                        break;
                    }
                }
            }

            IDictionary<string, object> selected = active ?? first;
            if (selected == null) return;
            snapshot.Model = GetString(selected, "model");
            snapshot.ReasoningEffort = GetString(selected, "reasoningEffort");
            string rolloutPath = GetString(selected, "path");
            if (string.IsNullOrWhiteSpace(rolloutPath))
                rolloutPath = FindRolloutPath(GetString(selected, "id"));
            if (!string.IsNullOrWhiteSpace(rolloutPath)) AddContextUsageFromRollout(snapshot, rolloutPath);
        }

        private static string FindRolloutPath(string threadId)
        {
            if (string.IsNullOrWhiteSpace(threadId)) return null;
            try
            {
                string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(codexHome))
                    codexHome = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                string[] roots =
                {
                    IOPath.Combine(codexHome, "sessions"),
                    IOPath.Combine(codexHome, "archived_sessions")
                };
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    string match = Directory.GetFiles(root, "*" + threadId + "*.jsonl", SearchOption.AllDirectories)
                        .OrderByDescending(delegate(string file) { return File.GetLastWriteTimeUtc(file); })
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match)) return match;
                }
            }
            catch { }
            return null;
        }

        private static void AddContextUsageFromRollout(QuotaSnapshot snapshot, string rolloutPath)
        {
            try
            {
                if (rolloutPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
                    rolloutPath = rolloutPath.Substring(4);
                string fullPath = IOPath.GetFullPath(rolloutPath);
                string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (string.IsNullOrWhiteSpace(codexHome))
                    codexHome = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                string allowedRoot = IOPath.GetFullPath(codexHome).TrimEnd(IOPath.DirectorySeparatorChar) + IOPath.DirectorySeparatorChar;
                if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return;

                const int maximumTailBytes = 4 * 1024 * 1024;
                string tail;
                using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = Math.Min(stream.Length, maximumTailBytes);
                    stream.Seek(-length, SeekOrigin.End);
                    byte[] buffer = new byte[(int)length];
                    int offset = 0;
                    while (offset < buffer.Length)
                    {
                        int read = stream.Read(buffer, offset, buffer.Length - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    tail = Encoding.UTF8.GetString(buffer, 0, offset);
                }

                string[] lines = tail.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].TrimEnd('\r');
                    if (line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) < 0) continue;
                    IDictionary<string, object> root;
                    try { root = serializer.DeserializeObject(line) as IDictionary<string, object>; }
                    catch { continue; }
                    if (root == null) continue;
                    IDictionary<string, object> payload = GetOptionalDictionary(root, "payload");
                    IDictionary<string, object> info = GetOptionalDictionary(payload, "info");
                    IDictionary<string, object> last = GetOptionalDictionary(info, "last_token_usage");
                    if (info == null || last == null) continue;
                    snapshot.ContextWindowTokens = GetNullableLong(info, "model_context_window");
                    snapshot.ContextUsedTokens = GetNullableLong(last, "total_tokens");
                    return;
                }
            }
            catch { }
        }

        private static string FormatWindowName(long? minutes)
        {
            if (!minutes.HasValue) return "使用限额";
            if (minutes.Value == 300) return "5 小时使用限额";
            if (minutes.Value == 10080) return "每周使用限额";
            if (minutes.Value % 10080 == 0) return (minutes.Value / 10080).ToString(CultureInfo.InvariantCulture) + " 周使用限额";
            if (minutes.Value % 1440 == 0) return (minutes.Value / 1440).ToString(CultureInfo.InvariantCulture) + " 天使用限额";
            if (minutes.Value % 60 == 0) return (minutes.Value / 60).ToString(CultureInfo.InvariantCulture) + " 小时使用限额";
            return minutes.Value.ToString(CultureInfo.InvariantCulture) + " 分钟使用限额";
        }

        private static string FindCodexExecutable()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_QUOTA_PET_CODEX_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string binRoot = IOPath.Combine(localAppData, "OpenAI", "Codex", "bin");
            if (Directory.Exists(binRoot))
            {
                FileInfo newest = new DirectoryInfo(binRoot)
                    .GetFiles("codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(delegate(FileInfo file) { return file.LastWriteTimeUtc; })
                    .FirstOrDefault();
                if (newest != null) return newest.FullName;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string part in path.Split(IOPath.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                string candidate;
                try { candidate = IOPath.Combine(part.Trim(), "codex.exe"); }
                catch { continue; }
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException("未找到 codex.exe。请先安装或更新 Codex。也可通过 CODEX_QUOTA_PET_CODEX_PATH 指定路径。");
        }

        private static IDictionary<string, object> AsDictionary(object value)
        {
            IDictionary<string, object> dictionary = value as IDictionary<string, object>;
            if (dictionary == null) throw new InvalidDataException("Codex 返回了无法识别的数据格式。");
            return dictionary;
        }

        private static object GetValue(IDictionary<string, object> dictionary, string key)
        {
            object value;
            if (!dictionary.TryGetValue(key, out value)) throw new InvalidDataException("Codex 响应缺少字段：" + key);
            return value;
        }

        private static IDictionary<string, object> GetOptionalDictionary(IDictionary<string, object> dictionary, string key)
        {
            if (dictionary == null) return null;
            object value;
            if (!dictionary.TryGetValue(key, out value) || value == null) return null;
            return value as IDictionary<string, object>;
        }

        private static string GetString(IDictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static long? GetNullableLong(IDictionary<string, object> dictionary, string key)
        {
            object value;
            if (!dictionary.TryGetValue(key, out value) || value == null) return null;
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
    }

    internal sealed class PetController : IDisposable
    {
        private readonly Window window;
        private readonly StackPanel limitsPanel;
        private readonly TextBlock resetCreditsText;
        private readonly TextBlock statusText;
        private readonly TextBlock modelText;
        private readonly TextBlock effortText;
        private readonly TextBlock contextValueText;
        private readonly Grid contextProgressGrid;
        private readonly Border contextProgressFill;
        private readonly Ellipse statusDot;
        private readonly Button refreshButton;
        private readonly Button minimizeButton;
        private readonly Button pinButton;
        private readonly DispatcherTimer processTimer;
        private readonly DispatcherTimer refreshTimer;
        private readonly CodexRateLimitClient client = new CodexRateLimitClient();
        private readonly Forms.NotifyIcon trayIcon;
        private readonly Forms.ContextMenuStrip trayMenu;
        private bool isReading;
        private bool codexWasRunning;
        private bool exitRequested;
        private int refreshCountdownSeconds = 30;
        private string statusPrefix;
        private bool statusIsError;
        private bool manuallyOpened;
        private readonly EventWaitHandle showEvent;
        private RegisteredWaitHandle showRegistration;

        public PetController(EventWaitHandle showEvent)
        {
            this.showEvent = showEvent;
            Stream xamlStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexQuotaPet.PetWindow.xaml");
            if (xamlStream == null)
            {
                string xamlPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "PetWindow.xaml");
                if (!File.Exists(xamlPath)) throw new FileNotFoundException("缺少界面文件 PetWindow.xaml。", xamlPath);
                xamlStream = File.OpenRead(xamlPath);
            }
            using (xamlStream)
            {
                window = (Window)XamlReader.Load(xamlStream);
            }

            limitsPanel = (StackPanel)window.FindName("LimitsPanel");
            resetCreditsText = (TextBlock)window.FindName("ResetCreditsText");
            statusText = (TextBlock)window.FindName("StatusText");
            modelText = (TextBlock)window.FindName("ModelText");
            effortText = (TextBlock)window.FindName("EffortText");
            contextValueText = (TextBlock)window.FindName("ContextValueText");
            contextProgressGrid = (Grid)window.FindName("ContextProgressGrid");
            contextProgressFill = (Border)window.FindName("ContextProgressFill");
            statusDot = (Ellipse)window.FindName("StatusDot");
            refreshButton = (Button)window.FindName("RefreshButton");
            minimizeButton = (Button)window.FindName("MinimizeButton");
            pinButton = (Button)window.FindName("PinButton");

            window.SourceInitialized += delegate { RestorePosition(); };
            window.MouseLeftButtonDown += WindowMouseLeftButtonDown;
            window.MouseLeftButtonUp += delegate { SavePosition(); };
            window.Closing += WindowClosing;
            window.MouseRightButtonUp += delegate { trayMenu.Show(Forms.Cursor.Position); };
            refreshButton.Click += delegate { RefreshQuota(); };
            pinButton.Click += delegate { ToggleTopmost(); };
            minimizeButton.Click += delegate
            {
                window.WindowState = WindowState.Minimized;
                trayIcon.Visible = true;
            };

            trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("立即刷新", null, delegate { RefreshQuota(); });
            trayMenu.Items.Add("显示仪表盘", null, delegate { ShowPet(); });
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出后台监控", null, delegate { Exit(); });

            trayIcon = new Forms.NotifyIcon();
            trayIcon.Icon = Drawing.SystemIcons.Information;
            trayIcon.Text = "Codex 使用仪表盘";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += delegate { ShowPet(); };

            processTimer = new DispatcherTimer();
            processTimer.Interval = TimeSpan.FromSeconds(2);
            processTimer.Tick += delegate { CheckCodexProcesses(); };

            refreshTimer = new DispatcherTimer();
            refreshTimer.Interval = TimeSpan.FromSeconds(1);
            refreshTimer.Tick += delegate { RefreshTimerTick(); };

            UpdatePinButton();
        }

        private void ToggleTopmost()
        {
            window.Topmost = !window.Topmost;
            UpdatePinButton();
        }

        private void UpdatePinButton()
        {
            if (window.Topmost)
            {
                pinButton.Background = BrushFrom("#FF32BFA8");
                pinButton.Foreground = BrushFrom("#FF071C18");
                pinButton.ToolTip = "取消固定在最上层";
            }
            else
            {
                pinButton.Background = BrushFrom("#22FFFFFF");
                pinButton.Foreground = BrushFrom("#FFD9DBDF");
                pinButton.ToolTip = "固定在最上层";
            }
        }

        public void Start(bool manualStart)
        {
            manuallyOpened = manualStart;
            showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
            {
                window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    manuallyOpened = true;
                    ShowPet();
                    RefreshQuota();
                }));
            }, null, Timeout.Infinite, false);
            processTimer.Start();
            refreshTimer.Start();
            CheckCodexProcesses();
            if (manualStart && !window.IsVisible)
            {
                ShowPet();
                RefreshQuota();
            }
        }

        private void CheckCodexProcesses()
        {
            bool running = IsExternalCodexRunning();
            if (running && !codexWasRunning)
            {
                codexWasRunning = true;
                ShowPet();
                RefreshQuota();
            }
            else if (!running && codexWasRunning)
            {
                codexWasRunning = false;
                if (!manuallyOpened)
                {
                    window.Hide();
                    trayIcon.Visible = false;
                }
            }
        }

        private bool IsExternalCodexRunning()
        {
            int protocolPid = client.ProtocolProcessId;
            int currentPid = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentPid || process.Id == protocolPid) continue;
                    string name = process.ProcessName ?? string.Empty;
                    if (name.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) return true;
                    if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = string.Empty;
                        try { path = process.MainModule.FileName ?? string.Empty; } catch { }
                        string title = string.Empty;
                        try { title = process.MainWindowTitle ?? string.Empty; } catch { }
                        if (path.IndexOf("\\OpenAI\\Codex\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            path.IndexOf("\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }

        private void RefreshQuota()
        {
            if (isReading) return;
            isReading = true;
            refreshButton.IsEnabled = false;
            statusText.Text = "正在读取 Codex 订阅额度…";
            statusDot.Fill = BrushFrom("#FFF2C86B");

            Task.Factory.StartNew(delegate { return client.Read(); })
                .ContinueWith(delegate(Task<QuotaSnapshot> task)
                {
                    window.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        isReading = false;
                        refreshButton.IsEnabled = true;
                        if (task.IsCanceled)
                        {
                            RenderError("额度读取已取消");
                        }
                        else if (task.IsFaulted)
                        {
                            Exception error = task.Exception;
                            while (error is AggregateException && error.InnerException != null) error = error.InnerException;
                            RenderError(error.InnerException != null ? error.InnerException.Message : error.Message);
                        }
                        else
                        {
                            RenderSnapshot(task.Result);
                        }
                    }));
                });
        }

        private void RefreshTimerTick()
        {
            if (!codexWasRunning || isReading) return;
            if (refreshCountdownSeconds > 0) refreshCountdownSeconds--;
            UpdateCountdownStatus();
            if (refreshCountdownSeconds <= 0) RefreshQuota();
        }

        private void UpdateCountdownStatus()
        {
            if (string.IsNullOrWhiteSpace(statusPrefix)) return;
            string action = statusIsError ? "后重试" : "后自动刷新";
            statusText.Text = statusPrefix + "（" + refreshCountdownSeconds + "s " + action + "）";
        }

        private void RenderSnapshot(QuotaSnapshot snapshot)
        {
            limitsPanel.Children.Clear();
            if (snapshot.Limits.Count == 0)
            {
                RenderUnavailableRow("官方未返回可显示的额度周期");
            }
            else
            {
                for (int i = 0; i < snapshot.Limits.Count; i++)
                {
                    if (i > 0)
                    {
                        Border separator = new Border();
                        separator.BorderBrush = BrushFrom("#FF393C42");
                        separator.BorderThickness = new Thickness(0, 1, 0, 0);
                        separator.Margin = new Thickness(0, 5, 0, 6);
                        limitsPanel.Children.Add(separator);
                    }
                    limitsPanel.Children.Add(CreateLimitRow(snapshot.Limits[i]));
                }
            }

            resetCreditsText.Text = snapshot.ResetCredits.HasValue ? snapshot.ResetCredits.Value + " 次" : "官方未提供";
            RenderCurrentThread(snapshot);
            refreshCountdownSeconds = 30;
            statusPrefix = "已同步 · " + snapshot.FetchedAt.ToString("HH:mm:ss");
            statusIsError = false;
            UpdateCountdownStatus();

            int minimum = snapshot.Limits.Count > 0 ? snapshot.Limits.Min(delegate(LimitInfo item) { return item.RemainingPercent; }) : 100;
            ApplyMood(minimum);
        }

        private void RenderCurrentThread(QuotaSnapshot snapshot)
        {
            modelText.Text = "模型：" + (string.IsNullOrWhiteSpace(snapshot.Model) ? "官方未提供" : snapshot.Model);
            effortText.Text = "强度：" + FormatEffort(snapshot.ReasoningEffort);

            int percent = 0;
            if (snapshot.ContextUsedTokens.HasValue && snapshot.ContextWindowTokens.HasValue && snapshot.ContextWindowTokens.Value > 0)
            {
                percent = (int)Math.Round(100.0 * snapshot.ContextUsedTokens.Value / snapshot.ContextWindowTokens.Value);
                percent = Math.Max(0, Math.Min(100, percent));
                contextValueText.Text = percent + "% · " + FormatTokenCount(snapshot.ContextUsedTokens.Value) + " / " + FormatTokenCount(snapshot.ContextWindowTokens.Value);
            }
            else
            {
                contextValueText.Text = "官方未提供";
            }

            contextProgressGrid.ColumnDefinitions[0].Width = new GridLength(Math.Max(0.001, percent), GridUnitType.Star);
            contextProgressGrid.ColumnDefinitions[1].Width = new GridLength(Math.Max(0.001, 100 - percent), GridUnitType.Star);
            contextProgressFill.Background = AccentFor(100 - percent);
        }

        private static string FormatEffort(string effort)
        {
            if (string.IsNullOrWhiteSpace(effort)) return "官方未提供";
            switch (effort.ToLowerInvariant())
            {
                case "none": return "关闭";
                case "minimal": return "极低";
                case "low": return "低";
                case "medium": return "中";
                case "high": return "高";
                case "xhigh": return "极高";
                case "max": return "最大";
                case "ultra": return "超高";
                default: return effort;
            }
        }

        private static string FormatTokenCount(long tokens)
        {
            if (tokens >= 1000000) return (tokens / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (tokens >= 1000) return (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        private UIElement CreateLimitRow(LimitInfo info)
        {
            Grid row = new Grid();
            row.Margin = new Thickness(0, 3, 0, 2);
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock name = new TextBlock();
            name.Text = info.Name;
            name.FontSize = 12;
            name.FontWeight = FontWeights.SemiBold;
            row.Children.Add(name);

            TextBlock percent = new TextBlock();
            percent.Text = "剩余 " + info.RemainingPercent + "%";
            percent.Foreground = BrushFrom("#FFC7C9CD");
            percent.FontSize = 11;
            percent.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(percent, 1);
            row.Children.Add(percent);

            TextBlock reset = new TextBlock();
            reset.Text = "重置时间：" + FormatResetTime(info.ResetsAt);
            reset.Foreground = BrushFrom("#FFA6A8AE");
            reset.FontSize = 10;
            reset.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetRow(reset, 1);
            Grid.SetColumnSpan(reset, 2);
            row.Children.Add(reset);

            Border track = new Border();
            track.Height = 6;
            track.CornerRadius = new CornerRadius(3);
            track.Background = BrushFrom("#FF4A4D52");
            track.HorizontalAlignment = HorizontalAlignment.Stretch;
            track.Margin = new Thickness(0, 5, 0, 1);
            Grid.SetRow(track, 2);
            Grid.SetColumnSpan(track, 2);

            Grid progressGrid = new Grid();
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, info.RemainingPercent), GridUnitType.Star) });
            progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 100 - info.RemainingPercent), GridUnitType.Star) });
            Border fill = new Border();
            fill.CornerRadius = new CornerRadius(3);
            fill.Background = AccentFor(info.RemainingPercent);
            Grid.SetColumn(fill, 0);
            progressGrid.Children.Add(fill);
            track.Child = progressGrid;
            row.Children.Add(track);

            return row;
        }

        private void RenderUnavailableRow(string message)
        {
            TextBlock text = new TextBlock();
            text.Text = message;
            text.Foreground = BrushFrom("#FFA6A8AE");
            text.FontSize = 13;
            text.Margin = new Thickness(0, 18, 0, 18);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            limitsPanel.Children.Add(text);
        }

        private void RenderError(string message)
        {
            limitsPanel.Children.Clear();
            RenderUnavailableRow("额度不可用");
            resetCreditsText.Text = "官方未提供";
            modelText.Text = "模型：官方未提供";
            effortText.Text = "强度：官方未提供";
            contextValueText.Text = "官方未提供";
            contextProgressGrid.ColumnDefinitions[0].Width = new GridLength(0.001, GridUnitType.Star);
            contextProgressGrid.ColumnDefinitions[1].Width = new GridLength(100, GridUnitType.Star);
            refreshCountdownSeconds = 30;
            statusPrefix = ShortError(message);
            statusIsError = true;
            UpdateCountdownStatus();
            statusDot.Fill = BrushFrom("#FFEF6B73");
            ApplyMood(0);
        }

        private static string ShortError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "读取失败，稍后自动重试";
            string normalized = message.Replace("\r", " ").Replace("\n", " ").Trim();
            if (normalized.Length > 46) normalized = normalized.Substring(0, 46) + "…";
            return normalized;
        }

        private void ApplyMood(int remaining)
        {
            Brush accent = AccentFor(remaining);
            statusDot.Fill = accent;
        }

        private static Brush AccentFor(int remaining)
        {
            if (remaining < 20) return BrushFrom("#FFEF6B73");
            if (remaining < 50) return BrushFrom("#FFF2C86B");
            return BrushFrom("#FF79E2CF");
        }

        private static Brush BrushFrom(string value)
        {
            return (Brush)new BrushConverter().ConvertFromString(value);
        }

        private static string FormatResetTime(long? unixSeconds)
        {
            if (!unixSeconds.HasValue) return "官方未提供";
            DateTime local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).LocalDateTime;
            if (local.Date == DateTime.Today) return local.ToString("HH:mm");
            return local.ToString("yyyy年M月d日 HH:mm");
        }

        private void WindowMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton != MouseButton.Left) return;
            DependencyObject source = args.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source is Button) return;
                source = VisualTreeHelper.GetParent(source);
            }
            try { window.DragMove(); } catch { }
        }

        private void ShowPet()
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            trayIcon.Visible = true;
        }

        private void WindowClosing(object sender, System.ComponentModel.CancelEventArgs args)
        {
            if (exitRequested) return;
            args.Cancel = true;
            window.Hide();
        }

        private void RestorePosition()
        {
            string settingsPath = GetSettingsPath();
            double left;
            double top;
            if (File.Exists(settingsPath))
            {
                string[] parts = File.ReadAllText(settingsPath).Split('|');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out left) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out top))
                {
                    Rect work = SystemParameters.WorkArea;
                    window.Left = Math.Max(work.Left, Math.Min(left, work.Right - window.Width));
                    window.Top = Math.Max(work.Top, Math.Min(top, work.Bottom - window.Height));
                    return;
                }
            }

            Rect area = SystemParameters.WorkArea;
            window.Left = area.Right - window.Width - 18;
            window.Top = area.Bottom - window.Height - 12;
        }

        private void SavePosition()
        {
            try
            {
                string path = GetSettingsPath();
                Directory.CreateDirectory(IOPath.GetDirectoryName(path));
                File.WriteAllText(path,
                    window.Left.ToString(CultureInfo.InvariantCulture) + "|" +
                    window.Top.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static string GetSettingsPath()
        {
            return IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "window-position.txt");
        }

        private void Exit()
        {
            exitRequested = true;
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            SavePosition();
            processTimer.Stop();
            refreshTimer.Stop();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
            if (showRegistration != null) showRegistration.Unregister(null);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool backgroundStart = args.Any(delegate(string arg)
            {
                return string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase);
            });
            bool created;
            bool eventCreated;
            using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexQuotaPet.Show", out eventCreated))
            using (Mutex mutex = new Mutex(true, "Local\\CodexQuotaPet.Singleton", out created))
            {
                if (!created)
                {
                    if (!backgroundStart) showEvent.Set();
                    return 0;
                }
                try
                {
                    Application application = new Application();
                    application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    using (PetController controller = new PetController(showEvent))
                    {
                        controller.Start(!backgroundStart);
                        application.Run();
                    }
                    return 0;
                }
                catch (Exception error)
                {
                    try
                    {
                        string errorDirectory = AppDomain.CurrentDomain.BaseDirectory;
                        File.WriteAllText(IOPath.Combine(errorDirectory, "last-error.log"), DateTime.Now.ToString("O") + Environment.NewLine + error.ToString());
                    }
                    catch { }
                    Forms.MessageBox.Show(error.Message, "Codex 使用仪表盘", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
    }
}
