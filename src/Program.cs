using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ExeCycler
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Rollback modu: guncelleme basarisiz oldugunda eski EXE ile baslatilir
            if (args.Length > 0 && args[0] == "--rollback")
            {
                string backupPath = args.Length > 1 ? args[1] : "";
                PerformRollback(backupPath);
                return;
            }

            // Baslangic beklemesi: kendini guncelleyen EXE yeni baslatiliyorsa
            if (args.Length > 0 && args[0] == "--updated")
            {
                Thread.Sleep(1500); // eski process'in kapanmasini bekle
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CyclerContext());
        }

        private static void PerformRollback(string backupPath)
        {
            try
            {
                string currentExe = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(backupPath) && File.Exists(backupPath))
                {
                    Thread.Sleep(1500);
                    File.Copy(backupPath, currentExe, overwrite: true);
                    Process.Start(new ProcessStartInfo(currentExe) { UseShellExecute = true });
                }
            }
            catch { }
        }
    }

    class CyclerConfig
    {
        public string ExePath { get; set; } = "";
        public int RunSeconds { get; set; } = 600;
        public int OffSeconds { get; set; } = 10;
        public int HeartbeatSeconds { get; set; } = 30;
        public int StopWaitSeconds { get; set; } = 5;
        public bool AutoUpdate { get; set; } = true;
    }

    class UpdateInfo
    {
        public string TagName { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
    }

    class CyclerContext : ApplicationContext
    {
        private const string GITHUB_REPO = "mstfonal/ExeCycler";
        private const string CURRENT_VERSION = "1.0.0";

        private NotifyIcon? _tray;
        private Thread? _workerThread;
        private Thread? _updateThread;
        private volatile bool _running = true;
        private CyclerConfig _config = new CyclerConfig();
        private string _configPath = "";
        private string _logPath = "";
        private string _status = "Baslatiliyor...";
        private int _cycleCount = 0;
        private readonly object _statusLock = new object();
        private readonly object _logLock = new object();
        private SynchronizationContext? _syncCtx;

        public CyclerContext()
        {
            _syncCtx = SynchronizationContext.Current;

            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ExeCycler");
            Directory.CreateDirectory(appData);
            _logPath = Path.Combine(appData, "cyclerlog.txt");
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

            InitTray();
            RegisterAutostart();
            _config = LoadOrCreateConfig();

            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "CyclerWorker"
            };
            _workerThread.Start();

            if (_config.AutoUpdate)
            {
                _updateThread = new Thread(UpdateLoop)
                {
                    IsBackground = true,
                    Name = "UpdateChecker"
                };
                _updateThread.Start();
            }
        }

        // ----------------------------------------------------------------
        // Tray
        // ----------------------------------------------------------------

        private void InitTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Durum", null, OnShowStatus);
            menu.Items.Add("Guncelleme Kontrol Et", null, OnCheckUpdate);
            menu.Items.Add("Log dosyasini ac", null, OnOpenLog);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Cikis", null, OnExit);

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "EXE Cycler v" + CURRENT_VERSION,
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += OnShowStatus;
        }

        private void SetStatus(string s)
        {
            lock (_statusLock) { _status = s; }
            string tip = "EXE Cycler v" + CURRENT_VERSION + " - " + s;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            _syncCtx?.Post(_ =>
            {
                if (_tray != null) _tray.Text = tip;
            }, null);
        }

        private void ShowTrayNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            _syncCtx?.Post(_ =>
            {
                _tray?.ShowBalloonTip(5000, title, message, icon);
            }, null);
        }

        private void OnShowStatus(object? sender, EventArgs e)
        {
            string s;
            lock (_statusLock) { s = _status; }
            MessageBox.Show(
                "Versiyon: " + CURRENT_VERSION +
                "\nDurum: " + s +
                "\nDongu sayisi: " + _cycleCount +
                "\n\nEXE: " + (_config?.ExePath ?? "-") +
                "\nRun: " + _config?.RunSeconds + "s | Off: " + _config?.OffSeconds + "s" +
                "\nOto-guncelleme: " + (_config?.AutoUpdate == true ? "Acik" : "Kapali"),
                "EXE Cycler", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnCheckUpdate(object? sender, EventArgs e)
        {
            Thread t = new Thread(() =>
            {
                var info = CheckForUpdate();
                if (info != null)
                    ApplyUpdate(info);
                else
                    ShowTrayNotification("EXE Cycler", "Guncelleme yok. Surum: " + CURRENT_VERSION);
            })
            { IsBackground = true };
            t.Start();
        }

        private void OnOpenLog(object? sender, EventArgs e)
        {
            if (File.Exists(_logPath))
                Process.Start(new ProcessStartInfo(_logPath) { UseShellExecute = true });
            else
                MessageBox.Show("Log dosyasi henuz olusturulmadi.", "EXE Cycler");
        }

        private void OnExit(object? sender, EventArgs e)
        {
            _running = false;
            if (_tray != null) _tray.Visible = false;
            Log("=== Kullanici istegi ile cikiliyor ===");
            Application.Exit();
        }

        // ----------------------------------------------------------------
        // Otomatik Guncelleme Sistemi
        // ----------------------------------------------------------------

        private void UpdateLoop()
        {
            // Baslarken 60 saniye bekle (sistem yerlessin)
            Thread.Sleep(60000);

            while (_running)
            {
                try
                {
                    var info = CheckForUpdate();
                    if (info != null)
                    {
                        Log("Guncelleme bulundu: " + info.TagName);
                        ShowTrayNotification("EXE Cycler - Guncelleme",
                            "Yeni surum: " + info.TagName + " (mevcut: " + CURRENT_VERSION + ")\n60 saniye icerisinde uygulanacak...",
                            ToolTipIcon.Info);

                        // 60 saniye bekle (iptal sansı)
                        for (int i = 0; i < 120 && _running; i++)
                            Thread.Sleep(500);

                        if (_running)
                            ApplyUpdate(info);
                    }
                }
                catch (Exception ex)
                {
                    Log("Guncelleme kontrol hatasi: " + ex.Message);
                }

                // Her 6 saatte bir kontrol et
                for (int i = 0; i < 43200 && _running; i++)
                    Thread.Sleep(500);
            }
        }

        private UpdateInfo? CheckForUpdate()
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.Add("User-Agent", "ExeCycler/" + CURRENT_VERSION);

                string url = "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";
                string json = client.GetStringAsync(url).GetAwaiter().GetResult();

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.GetProperty("tag_name").GetString() ?? "";
                string releaseVersion = tagName.TrimStart('v');

                if (!IsNewerVersion(releaseVersion, CURRENT_VERSION))
                    return null;

                // EXE download URL'ini bul
                string downloadUrl = "";
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl))
                    return null;

                return new UpdateInfo { TagName = tagName, DownloadUrl = downloadUrl };
            }
            catch (Exception ex)
            {
                Log("Guncelleme kontrolu basarisiz: " + ex.Message);
                return null;
            }
        }

        private void ApplyUpdate(UpdateInfo info)
        {
            string currentExe = Application.ExecutablePath;
            string exeDir = Path.GetDirectoryName(currentExe) ?? AppDomain.CurrentDomain.BaseDirectory;
            string backupPath = Path.Combine(exeDir, "ExeCycler_backup.exe");
            string tempPath = Path.Combine(exeDir, "ExeCycler_new.exe");

            Log("Guncelleme basliyor: " + info.TagName);
            ShowTrayNotification("EXE Cycler", "Guncelleme indiriliyor: " + info.TagName + "...");

            try
            {
                // 1. Yeni EXE'yi indir
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(120);
                client.DefaultRequestHeaders.Add("User-Agent", "ExeCycler/" + CURRENT_VERSION);

                byte[] newExeBytes = client.GetByteArrayAsync(info.DownloadUrl).GetAwaiter().GetResult();

                if (newExeBytes.Length < 1024)
                    throw new Exception("Indirilen dosya cok kucuk, gecersiz EXE.");

                File.WriteAllBytes(tempPath, newExeBytes);
                Log("Yeni EXE indirildi: " + tempPath + " (" + newExeBytes.Length + " bytes)");

                // 2. Mevcut EXE'yi yedekle
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Copy(currentExe, backupPath);
                Log("Mevcut EXE yedeklendi: " + backupPath);

                // 3. Yeni EXE'yi yerine koy (kendimizi degistiremeyiz, bat script ile yapacagiz)
                string batPath = Path.Combine(exeDir, "update_helper.bat");
                string batContent =
                    "@echo off\r\n" +
                    "timeout /t 2 /nobreak >nul\r\n" +
                    "copy /y \"" + tempPath + "\" \"" + currentExe + "\"\r\n" +
                    "if errorlevel 1 goto rollback\r\n" +
                    "del \"" + tempPath + "\"\r\n" +
                    "start \"" + currentExe + "\" --updated\r\n" +
                    "del \"%~f0\"\r\n" +
                    "exit\r\n" +
                    ":rollback\r\n" +
                    "echo Guncelleme basarisiz, rollback yapiliyor...\r\n" +
                    "copy /y \"" + backupPath + "\" \"" + currentExe + "\"\r\n" +
                    "start \"" + currentExe + "\"\r\n" +
                    "del \"%~f0\"\r\n" +
                    "exit\r\n";

                File.WriteAllText(batPath, batContent);

                Log("Guncelleme uygulanacak, uygulama yeniden baslatiliyor...");
                ShowTrayNotification("EXE Cycler", "Guncelleme uygulanacak, yeniden baslatiliyor...");

                // 4. BAT'i calistir ve cik
                _running = false;
                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + batPath + "\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });

                Thread.Sleep(500);
                _syncCtx?.Post(_ =>
                {
                    if (_tray != null) _tray.Visible = false;
                    Application.Exit();
                }, null);
            }
            catch (Exception ex)
            {
                Log("GUNCELLEME HATASI: " + ex.Message + " - Rollback yapiliyor...");

                // Temizlik
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                // Eger backup varsa geri yukle
                try
                {
                    if (File.Exists(backupPath) && File.Exists(currentExe))
                    {
                        // Cok nadir: indirme basarisiz oldugu icin mevcut EXE hala saglam
                        Log("Mevcut EXE saglam, guncelleme iptal edildi.");
                    }
                }
                catch { }

                ShowTrayNotification("EXE Cycler - Guncelleme Hatasi",
                    "Guncelleme basarisiz: " + ex.Message + "\nMevcut surum kullanilmaya devam ediyor.",
                    ToolTipIcon.Error);
            }
        }

        private static bool IsNewerVersion(string remote, string local)
        {
            try
            {
                var r = new Version(remote);
                var l = new Version(local);
                return r > l;
            }
            catch { return false; }
        }

        // ----------------------------------------------------------------
        // Config
        // ----------------------------------------------------------------

        private CyclerConfig LoadOrCreateConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    var cfg = JsonSerializer.Deserialize<CyclerConfig>(json);
                    if (cfg != null)
                    {
                        Log("Config yuklendi: " + _configPath);
                        if (string.IsNullOrEmpty(cfg.ExePath) || !File.Exists(cfg.ExePath))
                        {
                            cfg.ExePath = PickExe();
                            SaveConfig(cfg);
                        }
                        return cfg;
                    }
                }
                catch (Exception ex)
                {
                    Log("Config okuma hatasi: " + ex.Message);
                }
            }

            var newCfg = new CyclerConfig();
            newCfg.ExePath = PickExe();
            SaveConfig(newCfg);
            return newCfg;
        }

        private void SaveConfig(CyclerConfig cfg)
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, opts));
            }
            catch (Exception ex)
            {
                Log("Config kaydetme hatasi: " + ex.Message);
            }
        }

        private string PickExe()
        {
            string result = "";
            var t = new Thread(() =>
            {
                using var dlg = new OpenFileDialog
                {
                    Filter = "Uygulamalar (*.exe)|*.exe",
                    Title = "Dongu EXE secin"
                };
                if (dlg.ShowDialog() == DialogResult.OK)
                    result = dlg.FileName;
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (string.IsNullOrEmpty(result))
            {
                MessageBox.Show("EXE secilmedi. Program kapaniyor.", "EXE Cycler",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Application.Exit();
                Environment.Exit(0);
            }

            Log("EXE secildi: " + result);
            return result;
        }

        // ----------------------------------------------------------------
        // Autostart
        // ----------------------------------------------------------------

        private void RegisterAutostart()
        {
            try
            {
                string exePath = Application.ExecutablePath;
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("ExeCycler", "\"" + exePath + "\"");
                Log("Autostart kaydedildi.");
            }
            catch (Exception ex)
            {
                Log("Autostart kayit hatasi: " + ex.Message);
            }
        }

        // ----------------------------------------------------------------
        // Worker Loop
        // ----------------------------------------------------------------

        private void WorkerLoop()
        {
            Log("=== EXE Cycler v" + CURRENT_VERSION + " baslatildi ===");
            Log("Config: Run=" + _config.RunSeconds + "s, Off=" + _config.OffSeconds + "s, " +
                "Heartbeat=" + _config.HeartbeatSeconds + "s, EXE=" + _config.ExePath);

            while (_running)
            {
                _cycleCount++;
                Log("=== DONGU " + _cycleCount + " BASLADI ===");
                SetStatus("Dongu " + _cycleCount + " - EXE baslatiliyor");

                if (GetProcessCount() == 0)
                {
                    bool started = StartExe("Dongu baslangici");
                    if (!started)
                    {
                        Log("Baslatma basarisiz. " + _config.OffSeconds + "s bekleniyor.");
                        SetStatus("Baslatma hatasi - bekleniyor");
                        SleepCancellable(_config.OffSeconds * 1000);
                        continue;
                    }
                }
                else
                {
                    Log("EXE zaten calisiyor.");
                }

                SetStatus("Dongu " + _cycleCount + " - RUN (" + _config.RunSeconds + "s)");
                var runEnd = DateTime.UtcNow.AddSeconds(_config.RunSeconds);

                while (DateTime.UtcNow < runEnd && _running)
                {
                    SleepCancellable(_config.HeartbeatSeconds * 1000);
                    if (!_running) break;
                    int cnt = GetProcessCount();
                    if (cnt == 0)
                    {
                        Log("HEARTBEAT: EXE calısmiyor -> yeniden baslatiliyor");
                        StartExe("RUN fazinda EXE kapandi");
                    }
                }

                if (!_running) break;

                Log("RUN suresi doldu. EXE durduruluyor.");
                SetStatus("Dongu " + _cycleCount + " - durduruluyor");
                StopExe();

                Log("OFF fazi: " + _config.OffSeconds + "s bekleniyor");
                SetStatus("Dongu " + _cycleCount + " - OFF (" + _config.OffSeconds + "s)");
                SleepCancellable(_config.OffSeconds * 1000);
            }

            Log("=== Worker durdu ===");
        }

        // ----------------------------------------------------------------
        // Process Helpers
        // ----------------------------------------------------------------

        private int GetProcessCount()
        {
            string exeName = Path.GetFileNameWithoutExtension(_config.ExePath);
            string targetLow = _config.ExePath.ToLowerInvariant();
            int count = 0;
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                try
                {
                    if (p.MainModule?.FileName?.ToLowerInvariant() == targetLow)
                        count++;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return count;
        }

        private bool StartExe(string reason)
        {
            Log("START: " + reason);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _config.ExePath,
                    WorkingDirectory = Path.GetDirectoryName(_config.ExePath),
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = true
                };
                var p = Process.Start(psi);
                Log("START: basarili PID=" + p?.Id);
                return true;
            }
            catch (Exception ex)
            {
                Log("START HATASI: " + ex.Message);
                return false;
            }
        }

        private void StopExe()
        {
            string exeName = Path.GetFileNameWithoutExtension(_config.ExePath);
            string targetLow = _config.ExePath.ToLowerInvariant();
            var processes = new System.Collections.Generic.List<Process>();

            foreach (var p in Process.GetProcessesByName(exeName))
            {
                try
                {
                    if (p.MainModule?.FileName?.ToLowerInvariant() == targetLow)
                        processes.Add(p);
                    else
                        p.Dispose();
                }
                catch { p.Dispose(); }
            }

            if (processes.Count == 0) { Log("STOP: zaten calısmiyor."); return; }

            foreach (var p in processes)
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                catch { }
            }

            var deadline = DateTime.UtcNow.AddSeconds(_config.StopWaitSeconds);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                if (GetProcessCount() == 0)
                {
                    Log("STOP: graceful basarili.");
                    foreach (var p in processes) p.Dispose();
                    return;
                }
            }

            foreach (var p in processes)
            {
                try { p.Kill(); Log("STOP: kill PID=" + p.Id); }
                catch (Exception ex) { Log("STOP: kill hatasi PID=" + p.Id + " - " + ex.Message); }
                finally { p.Dispose(); }
            }

            Thread.Sleep(500);
            int final = GetProcessCount();
            Log(final == 0 ? "STOP: force kill basarili." : "STOP: UYARI - " + final + " process hala calisiyor!");
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private void SleepCancellable(int ms)
        {
            const int chunk = 500;
            int elapsed = 0;
            while (elapsed < ms && _running)
            {
                Thread.Sleep(Math.Min(chunk, ms - elapsed));
                elapsed += chunk;
            }
        }

        private void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
            try
            {
                lock (_logLock)
                    File.AppendAllText(_logPath, line + Environment.NewLine,
                        System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
