using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using QianwenSwitcher.Services;
using QianwenSwitcher.UI;

namespace QianwenSwitcher
{
    internal static class Program
    {
        private static Mutex _mutex;
        private static EventWaitHandle _showSignal;
        private static AppContext _context;

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            _mutex = new Mutex(true, "Local\\QianwenAccountSwitcher_Mutex_v1", out createdNew);
            if (!createdNew)
            {
                // 已有实例运行：通知它显示主窗口后退出
                try
                {
                    _showSignal = EventWaitHandle.OpenExisting("Local\\QianwenAccountSwitcher_Show_v1");
                    _showSignal.Set();
                }
                catch { }
                return;
            }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
                "Local\\QianwenAccountSwitcher_Show_v1");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                _context = new AppContext(_showSignal, args);
                Application.Run(_context);
            }
            catch (Exception ex)
            {
                Logger.Error("程序致命异常", ex);
                MessageBox.Show("程序发生异常：" + ex.Message, "千问账号快捷切换器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (_context != null) _context.Cleanup();
                try { _mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    /// <summary>托盘常驻的应用上下文（主窗口延迟显示）</summary>
    internal class AppContext : ApplicationContext
    {
        private readonly PathService _paths;
        private readonly ConfigService _configService;
        private readonly SwitchService _switch;
        private readonly AutoStartService _autoStart;
        private readonly AppConfig _cfg;
        private readonly MainForm _form;
        private readonly TrayContext _tray;
        private RegisteredWaitHandle _showWait;

        public AppContext(EventWaitHandle showSignal, string[] args)
        {
            _paths = new PathService();
            _paths.EnsureDirs();
            Logger.Init(_paths.LogsDir);
            Logger.Info("===== 千问账号快捷切换器启动 =====");

            _paths.CleanStaging();
            _configService = new ConfigService(_paths);
            bool firstRun = !File.Exists(_paths.ConfigPath);
            _cfg = _configService.LoadOrCreate();

            // 首次运行时把默认的“开机自启”落到注册表；之后以注册表实际状态为准
            _autoStart = new AutoStartService(_paths.ExePath);
            if (firstRun && _cfg.AutoStart)
            {
                _cfg.AutoStart = _autoStart.Set(true);
                _configService.Save(_cfg);
            }
            else if (_cfg.AutoStart != _autoStart.IsEnabled())
            {
                _cfg.AutoStart = _autoStart.IsEnabled();
                _configService.Save(_cfg);
            }

            ProfileService profileService = new ProfileService(_paths);
            IdentityService identityService = new IdentityService();
            _switch = new SwitchService(_paths, _configService, profileService, identityService);

            _form = new MainForm(_paths, _configService, _switch, _autoStart, _cfg);
            _tray = new TrayContext(_paths, _cfg, _configService, _switch, _autoStart, _form);
            _form.Tray = _tray;

            // 即使主窗口不显示也提前创建句柄，保证托盘回调可使用 BeginInvoke
            IntPtr force = _form.Handle;

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Logger.Error("UI 线程异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Logger.Error("未处理异常(IsTerminating=" + e.IsTerminating + ")",
                    e.ExceptionObject as Exception);
            };

            _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
                delegate
                {
                    try { _tray.ShowForm(); }
                    catch (Exception ex) { Logger.Error("唤起信号处理失败", ex); }
                }, null, -1, false);

            bool minimized = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
                        minimized = true;
                }
            }

            if (!minimized)
            {
                _form.Show();
            }
            else
            {
                Logger.Info("以托盘驻留模式启动");
            }
        }

        public void Cleanup()
        {
            try
            {
                Logger.Info("程序退出");
                if (_showWait != null) _showWait.Unregister(null);
                if (_tray != null) _tray.Dispose();
                _paths.CleanStaging();
            }
            catch { }
        }
    }
}
