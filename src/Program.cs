using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoStart
{
    static class Program
    {
        private static Mutex? _mutex;

        [STAThread]
        static void Main(string[] args)
        {
            Application.ThreadException += (s, e) => LogUnhandled(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex) LogUnhandled(ex);
            };

            if (args.Length > 0 && args[0] == "--install-update")
            {
                Environment.ExitCode = UpdateInstaller.Run(args);
                return;
            }

            bool isUpdate = args.Length > 0 && args[0] == "--updated";
            bool createdNew = false;
            int mutexRetries = isUpdate ? 10 : 1;
            for (int i = 0; i < mutexRetries; i++)
            {
                _mutex = new Mutex(true, "AutoStart_SingleInstance", out createdNew);
                if (createdNew) break;
                _mutex.Dispose();
                _mutex = null;
                Thread.Sleep(1000);
            }
            if (!createdNew)
            {
                if (!isUpdate)
                    MessageBox.Show("AutoStart is already running.\nCheck the system tray.", "AutoStart", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CyclerContext());

            _mutex!.ReleaseMutex();
            _mutex.Dispose();
        }

        private static void LogUnhandled(Exception ex)
        {
            try
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoStart");
                Directory.CreateDirectory(appData);
                string path = Path.Combine(appData, "cyclerlog.txt");
                File.AppendAllText(path, "[" + DateTime.Now + "] UNHANDLED: " + ex + Environment.NewLine);
            }
            catch { }
        }

    }


    class CyclerConfig
    {
        public string ExePath { get; set; } = "";
        public int RunMinutes { get; set; } = 10;
        public int OffMinutes { get; set; } = 1;
        public int HeartbeatSeconds { get; set; } = 30;
        public int StopWaitSeconds { get; set; } = 5;
        public bool AutoUpdate { get; set; } = true;
        public bool MinimizeTarget { get; set; } = true;
    }

    class UpdateInfo
    {
        public string TagName { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public bool IsZip { get; set; }
    }

    class CyclerContext : ApplicationContext
    {
        private const string GITHUB_REPO = "mstfonal/AutoStart";
        private static readonly string CURRENT_VERSION = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        private static readonly long MAX_LOG_BYTES = 1 * 1024 * 1024;

        private NotifyIcon? _tray;
        private ToolStripMenuItem? _statusMenuItem;
        private Thread? _workerThread;
        private Thread? _updateThread;
        private volatile bool _running = true;
        private volatile CyclerConfig _config = new CyclerConfig();
        private TargetWindowMonitor? _windowMonitor;
        private int _updateInProgress;
        private volatile string _activeTargetPath = "";
        private string _configPath = "";
        private string _logPath = "";
        private string _status = "Starting...";
        private int _cycleCount = 0;
        private readonly object _statusLock = new object();
        private readonly object _logLock = new object();
        private SynchronizationContext? _syncCtx;

        public CyclerContext()
        {
            // ApplicationContext is constructed before Application.Run installs a context.
            _syncCtx = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoStart");
            Directory.CreateDirectory(appData);
            _logPath = Path.Combine(appData, "cyclerlog.txt");
            _configPath = Path.Combine(appData, "config.json");

            string oldConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(oldConfigPath) && !File.Exists(_configPath))
            {
                try { File.Copy(oldConfigPath, _configPath); File.Delete(oldConfigPath); } catch { }
            }

            InitTray();
            RegisterAutostart();
            _config = LoadOrCreateConfig();
            NormalizeConfig(_config);
            _windowMonitor = new TargetWindowMonitor(() => _running && _config.MinimizeTarget,
                pid => TargetProcesses.Matches(pid, string.IsNullOrEmpty(_activeTargetPath) ? _config.ExePath : _activeTargetPath), Log);

            _workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "CyclerWorker" };
            _workerThread.Start();

            // Always start the checker so changing the checkbox works without a restart.
            {
                _updateThread = new Thread(UpdateLoop) { IsBackground = true, Name = "UpdateChecker" };
                _updateThread.Start();
            }
        }

        private static Icon CreateAppIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bgBrush = new SolidBrush(Color.FromArgb(30, 140, 60));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);
            using var borderPen = new Pen(Color.FromArgb(20, 100, 40), 1.5f);
            g.DrawEllipse(borderPen, 1, 1, 30, 30);
            using var font = new Font("Arial", 8, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("AS", font, brush, new RectangleF(0, 0, 32, 32), sf);
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void InitTray()
        {
            var menu = new ContextMenuStrip();

            _statusMenuItem = new ToolStripMenuItem("Status: Starting...") { Enabled = false };
            menu.Items.Add(_statusMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings", null, OnShowSettings);
            menu.Items.Add("Check for Updates", null, OnCheckUpdate);
            menu.Items.Add("Open Log", null, OnOpenLog);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, OnExit);

            _tray = new NotifyIcon
            {
                Icon = CreateAppIcon(),
                Text = "AutoStart v" + CURRENT_VERSION,
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += OnShowSettings;
        }

        private void SetStatus(string s)
        {
            lock (_statusLock) { _status = s; }
            string tip = "AutoStart v" + CURRENT_VERSION + " - " + s;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            _syncCtx?.Post(_ =>
            {
                if (_tray != null) _tray.Text = tip;
                if (_statusMenuItem != null) _statusMenuItem.Text = "▶ " + s;
            }, null);
        }

        private void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            _syncCtx?.Post(_ => { _tray?.ShowBalloonTip(5000, title, message, icon); }, null);
        }

        private void OnShowSettings(object? sender, EventArgs e)
        {
            if (_syncCtx != null)
                _syncCtx.Post(_ => ShowSettingsForm(), null);
            else
                ShowSettingsForm();
        }

        private void ShowSettingsForm()
        {
            var form = new Form
            {
                Text = "AutoStart v" + CURRENT_VERSION + " - Settings",
                Width = 480,
                Height = 340,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 0, 0, 15),
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true
            };

            int y = 20;

            var lblExe = new Label { Text = "Selected EXE:", Left = 20, Top = y, Width = 160, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
            string exeShort = string.IsNullOrEmpty(_config.ExePath) ? "(None)" : Path.GetFileName(_config.ExePath);
            var lblExePath = new Label { Text = exeShort, Left = 190, Top = y, Width = 260, AutoSize = false, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DarkBlue };
            y += 30;

            var btnReset = new Button { Text = "Reset EXE (Pick New)", Left = 190, Top = y, Width = 260, Height = 28 };
            btnReset.Click += (s, ev) =>
            {
                string picked = PickExe(required: false);
                if (!string.IsNullOrEmpty(picked))
                {
                    if (TargetProcesses.SamePath(picked, Application.ExecutablePath))
                    {
                        MessageBox.Show("Select the order program, not AutoStart itself.", "AutoStart");
                        return;
                    }
                    var changed = CloneConfig(_config);
                    changed.ExePath = picked;
                    _config = changed;
                    SaveConfig(_config);
                    lblExePath.Text = Path.GetFileName(picked);
                    Log("EXE changed: " + picked);
                    MessageBox.Show("EXE updated:\n" + picked + "\n\nTakes effect on next cycle.", "AutoStart", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            y += 38;

            var lblRun = new Label { Text = "Run duration (min):", Left = 20, Top = y + 3, Width = 160 };
            var txtRun = new TextBox { Text = _config.RunMinutes.ToString(), Left = 190, Top = y, Width = 80 };
            y += 35;

            var lblOff = new Label { Text = "Off duration (min):", Left = 20, Top = y + 3, Width = 160 };
            var txtOff = new TextBox { Text = _config.OffMinutes.ToString(), Left = 190, Top = y, Width = 80 };
            y += 35;

            var chkUpdate = new CheckBox { Text = "Auto-update", Left = 190, Top = y, Width = 260, Checked = _config.AutoUpdate };
            y += 28;

            var chkMin = new CheckBox { Text = "Keep target in background", Left = 190, Top = y, Width = 260, Checked = _config.MinimizeTarget };
            y += 40;

            var btnSave = new Button { Text = "Save", Left = 190, Top = y, Width = 110, Height = 28 };
            btnSave.Click += (s, ev) =>
            {
                if (!int.TryParse(txtRun.Text, out int r) || r < 1 || r > 10080 ||
                    !int.TryParse(txtOff.Text, out int o) || o < 0 || o > 10080)
                {
                    MessageBox.Show("Run must be 1–10080 minutes; off must be 0–10080 minutes.", "AutoStart");
                    return;
                }
                var changed = CloneConfig(_config);
                changed.RunMinutes = r;
                changed.OffMinutes = o;
                changed.AutoUpdate = chkUpdate.Checked;
                changed.MinimizeTarget = chkMin.Checked;
                _config = changed;
                SaveConfig(changed);
                _windowMonitor?.Sweep();
                Log("Settings saved.");
                MessageBox.Show("Settings saved.", "AutoStart", MessageBoxButtons.OK, MessageBoxIcon.Information);
                form.Close();
            };

            var btnCancel = new Button { Text = "Cancel", Left = 310, Top = y, Width = 100, Height = 28 };
            btnCancel.Click += (s, ev) => form.Close();

            form.Controls.AddRange(new Control[] { lblExe, lblExePath, btnReset, lblRun, txtRun, lblOff, txtOff, chkUpdate, chkMin, btnSave, btnCancel });
            form.ShowDialog();
        }

        private void OnCheckUpdate(object? sender, EventArgs e)
        {
            new Thread(() =>
            {
                try
                {
                    var info = CheckForUpdate();
                    if (info != null) ApplyUpdate(info);
                    else ShowBalloon("AutoStart", "No verified newer update. Version: " + CURRENT_VERSION);
                }
                catch { ShowBalloon("AutoStart", "Update check failed. See the log for details.", ToolTipIcon.Error); }
            })
            { IsBackground = true }.Start();
        }

        private void OnOpenLog(object? sender, EventArgs e)
        {
            if (File.Exists(_logPath))
                Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
            else
                MessageBox.Show("Log file not created yet.", "AutoStart");
        }

        private void OnExit(object? sender, EventArgs e)
        {
            _running = false;
            if (_tray != null) _tray.Visible = false;
            _windowMonitor?.Dispose();
            Log("=== Exit requested by user ===");
            Application.Exit();
        }

        private void UpdateLoop()
        {
            Thread.Sleep(60000);
            while (_running)
            {
                try
                {
                    if (!_config.AutoUpdate) { SleepCancellable(1000); continue; }
                    var info = CheckForUpdate();
                    if (info != null)
                    {
                        Log("Update found: " + info.TagName);
                        ShowBalloon("AutoStart - Update", "New version: " + info.TagName + " Applying in 60s...");
                        for (int i = 0; i < 120 && _running; i++) Thread.Sleep(500);
                        if (_running && _config.AutoUpdate) ApplyUpdate(info);
                    }
                    else
                    {
                        Log("Update check: already on latest (" + CURRENT_VERSION + ")");
                    }
                }
                catch (Exception ex) { Log("Update check error: " + ex.Message); }
                // 6 saatte bir kontrol
                bool enabledAtStart = _config.AutoUpdate;
                for (int i = 0; i < 43200 && _running && _config.AutoUpdate == enabledAtStart; i++) Thread.Sleep(500);
            }
        }

        private UpdateInfo? CheckForUpdate()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add("User-Agent", "AutoStart/" + CURRENT_VERSION);
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

                string url = "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";
                Log("Checking update: " + url);

                string json = client.GetStringAsync(url).GetAwaiter().GetResult();
                var update = UpdatePackage.Select(json, CURRENT_VERSION);
                Log(update == null ? "No newer compatible release with a verified asset." : "Update available: " + update.TagName);
                return update;
            }
            catch (Exception ex)
            {
                Log("Update check failed: " + ex.Message);
                throw;
            }
        }

        private void ApplyUpdate(UpdateInfo info)
        {
            if (Interlocked.CompareExchange(ref _updateInProgress, 1, 0) != 0) return;
            string currentExe = Application.ExecutablePath;
            string exeDir = Path.GetDirectoryName(currentExe)!;
            string tempPath = Path.Combine(exeDir, "AutoStart_new.exe");
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                client.DefaultRequestHeaders.Add("User-Agent", "AutoStart/" + CURRENT_VERSION);
                byte[] bytes = client.GetByteArrayAsync(info.DownloadUrl).GetAwaiter().GetResult();
                byte[] exe = UpdatePackage.VerifyAndExtract(bytes, info);
                File.WriteAllBytes(tempPath, exe);
                var fileVersion = FileVersionInfo.GetVersionInfo(tempPath).FileVersion;
                if (!Version.TryParse(fileVersion, out var downloaded) ||
                    downloaded.ToString(3) != new Version(info.TagName.TrimStart('v')).ToString(3))
                    throw new InvalidDataException("Downloaded executable version does not match the release tag.");

                UpdateInstaller.Launch(currentExe);
                Log("Verified update staged: " + info.TagName + "; installer started.");
                _running = false;
                _syncCtx!.Post(_ =>
                {
                    _windowMonitor?.Dispose();
                    if (_tray != null) _tray.Visible = false;
                    Application.Exit();
                }, null);
            }
            catch (Exception ex)
            {
                Log("UPDATE ERROR: " + ex.Message);
                ShowBalloon("AutoStart - Update Failed", ex.Message, ToolTipIcon.Error);
            }
            finally { if (_running) Interlocked.Exchange(ref _updateInProgress, 0); }
        }

        internal static CyclerConfig CloneConfig(CyclerConfig cfg) => new CyclerConfig
        {
            ExePath = cfg.ExePath, RunMinutes = cfg.RunMinutes, OffMinutes = cfg.OffMinutes,
            HeartbeatSeconds = cfg.HeartbeatSeconds, StopWaitSeconds = cfg.StopWaitSeconds,
            AutoUpdate = cfg.AutoUpdate, MinimizeTarget = cfg.MinimizeTarget
        };

        internal static void NormalizeConfig(CyclerConfig cfg)
        {
            cfg.RunMinutes = Math.Clamp(cfg.RunMinutes, 1, 10080);
            cfg.OffMinutes = Math.Clamp(cfg.OffMinutes, 0, 10080);
            cfg.HeartbeatSeconds = Math.Clamp(cfg.HeartbeatSeconds, 1, 3600);
            cfg.StopWaitSeconds = Math.Clamp(cfg.StopWaitSeconds, 1, 300);
            if (TargetProcesses.SamePath(cfg.ExePath, Application.ExecutablePath)) cfg.ExePath = "";
        }

        private CyclerConfig LoadOrCreateConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<CyclerConfig>(File.ReadAllText(_configPath));
                    if (cfg != null)
                    {
                        Log("Config loaded: " + _configPath);
                        if (string.IsNullOrEmpty(cfg.ExePath) || !File.Exists(cfg.ExePath))
                        {
                            Log("Saved EXE not found, re-picking: " + cfg.ExePath);
                            cfg.ExePath = PickExe(required: true);
                            SaveConfig(cfg);
                        }
                        return cfg;
                    }
                }
                catch (Exception ex) { Log("Config read error: " + ex.Message); }
            }
            var newCfg = new CyclerConfig { ExePath = PickExe(required: true) };
            SaveConfig(newCfg);
            return newCfg;
        }

        private void SaveConfig(CyclerConfig cfg)
        {
            try
            {
                string staged = _configPath + ".tmp";
                File.WriteAllText(staged, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(staged, _configPath, overwrite: true);
            }
            catch (Exception ex) { Log("Config save error: " + ex.Message); }
        }

        private string PickExe(bool required = true)
        {
            string result = "";
            var t = new Thread(() =>
            {
                using var dlg = new OpenFileDialog
                {
                    Filter = "Applications (*.exe)|*.exe",
                    Title = "Select EXE to cycle"
                };
                if (dlg.ShowDialog() == DialogResult.OK) result = dlg.FileName;
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (string.IsNullOrEmpty(result))
            {
                if (required)
                {
                    ShowBalloon("AutoStart", "No EXE selected. Right-click tray icon > Settings to pick one.", ToolTipIcon.Warning);
                    Log("No EXE selected by user.");
                }
                return "";
            }
            Log("EXE selected: " + result);
            return result;
        }

        private void RegisterAutostart()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("AutoStart", "\"" + Application.ExecutablePath + "\"");
                Log("Autostart registered.");
            }
            catch (Exception ex) { Log("Autostart error: " + ex.Message); }
        }

        private void WorkerLoop()
        {
            Log("=== AutoStart v" + CURRENT_VERSION + " started ===");
            Log("Config: Run=" + _config.RunMinutes + "min, Off=" + _config.OffMinutes + "min, EXE=" + _config.ExePath);

            while (_running)
            {
                if (string.IsNullOrEmpty(_config.ExePath) || !File.Exists(_config.ExePath))
                {
                    SetStatus("No EXE selected - open Settings");
                    SleepCancellable(5000);
                    continue;
                }

                CyclerConfig cycle = CloneConfig(_config);
                _activeTargetPath = cycle.ExePath;
                _cycleCount++;
                Log("=== CYCLE " + _cycleCount + " START ===");
                SetStatus("Cycle " + _cycleCount + " - launching EXE");

                if (GetProcessCount(cycle.ExePath) == 0)
                {
                    if (!StartExe(cycle, "Cycle start"))
                    {
                        Log("Launch failed. Waiting " + cycle.OffMinutes + " min.");
                        SetStatus("Launch failed - waiting");
                        SleepCancellable(Math.Max(1000, cycle.OffMinutes * 60 * 1000));
                        continue;
                    }
                }
                else Log("EXE already running.");

                SetStatus("Cycle " + _cycleCount + " - running (" + cycle.RunMinutes + " min)");
                var runEnd = DateTime.UtcNow.AddSeconds(cycle.RunMinutes * 60);
                while (DateTime.UtcNow < runEnd && _running)
                {
                    int remaining = (int)Math.Max(0, (runEnd - DateTime.UtcNow).TotalMilliseconds);
                    SleepCancellable(Math.Min(cycle.HeartbeatSeconds * 1000, remaining));
                    if (!_running) break;
                    if (DateTime.UtcNow < runEnd && GetProcessCount(cycle.ExePath) == 0)
                    {
                        Log("Heartbeat: EXE gone, restarting");
                        StartExe(cycle, "Heartbeat restart");
                    }
                }

                if (!_running) break;

                Log("Run time elapsed. Stopping EXE.");
                SetStatus("Cycle " + _cycleCount + " - stopping");
                StopExe(cycle);

                Log("Off phase: " + cycle.OffMinutes + " min");
                SetStatus("Cycle " + _cycleCount + " - off (" + cycle.OffMinutes + " min)");
                SleepCancellable(cycle.OffMinutes * 60 * 1000);
            }
            Log("=== Worker stopped ===");
        }

        private static int GetProcessCount(string target)
        {
            var processes = TargetProcesses.Find(target);
            int count = processes.Count;
            foreach (var process in processes) process.Dispose();
            return count;
        }

        private bool StartExe(CyclerConfig cycle, string reason)
        {
            Log("START: " + reason);
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = cycle.ExePath,
                    WorkingDirectory = Path.GetDirectoryName(cycle.ExePath),
                    UseShellExecute = false,
                    WindowStyle = _config.MinimizeTarget ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
                });
                Log("START OK PID=" + process?.Id);
                return process != null;
            }
            catch (Exception ex) { Log("START ERROR: " + ex.Message); return false; }
        }

        private void StopExe(CyclerConfig cycle)
        {
            var processes = TargetProcesses.Find(cycle.ExePath);
            try
            {
                foreach (var process in processes)
                    try { process.CloseMainWindow(); } catch { }
                var deadline = DateTime.UtcNow.AddSeconds(cycle.StopWaitSeconds);
                while (DateTime.UtcNow < deadline && _running)
                {
                    if (GetProcessCount(cycle.ExePath) == 0) return;
                    Thread.Sleep(250);
                }
                if (!_running) return;
                foreach (var process in processes)
                    try
                    {
                        if (!process.HasExited && TargetProcesses.Matches(process.Id, cycle.ExePath))
                        { process.Kill(); Log("STOP: killed verified target PID=" + process.Id); }
                    }
                    catch (Exception ex) { Log("STOP error: " + ex.Message); }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private void SleepCancellable(int ms)
        {
            int elapsed = 0;
            while (elapsed < ms && _running)
            {
                Thread.Sleep(Math.Min(500, ms - elapsed));
                elapsed += 500;
            }
        }

        private void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            try
            {
                lock (_logLock)
                {
                    if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MAX_LOG_BYTES)
                    {
                        var lines = File.ReadAllLines(_logPath);
                        int keep = Math.Min(200, lines.Length);
                        File.WriteAllLines(_logPath, lines[^keep..], System.Text.Encoding.UTF8);
                        File.AppendAllText(_logPath, "[" + DateTime.Now + "] Log trimmed to last " + keep + " lines." + Environment.NewLine, System.Text.Encoding.UTF8);
                    }
                    File.AppendAllText(_logPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
