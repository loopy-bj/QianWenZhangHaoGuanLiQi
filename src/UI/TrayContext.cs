using System;
using System.Drawing;
using System.Windows.Forms;
using QianwenSwitcher.Services;

namespace QianwenSwitcher.UI
{
    /// <summary>系统托盘：账号快速切换菜单、气泡通知、自启开关与退出</summary>
    public class TrayContext
    {
        private readonly PathService _paths;
        private readonly AppConfig _cfg;
        private readonly ConfigService _configService;
        private readonly SwitchService _switch;
        private readonly AutoStartService _autoStart;
        private readonly MainForm _form;

        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miSwitch;
        private ToolStripMenuItem _miAutoStart;
        private string _lastCurrentUid = string.Empty;
        private bool _autoStartChanging;

        public TrayContext(PathService paths, AppConfig cfg, ConfigService configService,
            SwitchService switchService, AutoStartService autoStart, MainForm form)
        {
            _paths = paths;
            _cfg = cfg;
            _configService = configService;
            _switch = switchService;
            _autoStart = autoStart;
            _form = form;
            Build();
        }

        private void Build()
        {
            _menu = new ContextMenuStrip();

            _miSwitch = new ToolStripMenuItem("切换账号");
            _miSwitch.DropDownOpening += delegate { RebuildAccounts(_lastCurrentUid); };

            ToolStripMenuItem miShow = new ToolStripMenuItem("显示主界面");
            miShow.Click += delegate { ShowForm(); };

            ToolStripMenuItem miRestart = new ToolStripMenuItem("重启千问");
            miRestart.Click += delegate
            {
                try
                {
                    _switch.StopQianwen(_cfg, null);
                    _switch.StartQianwen(_cfg);
                    Notify("重启千问", "千问已重新启动", false);
                }
                catch (Exception ex)
                {
                    Logger.Error("托盘重启千问失败", ex);
                    Notify("重启失败", ex.Message, true);
                }
            };

            _miAutoStart = new ToolStripMenuItem("开机自启动");
            _miAutoStart.CheckOnClick = true;
            _miAutoStart.Checked = _autoStart.IsEnabled();
            _miAutoStart.CheckedChanged += delegate
            {
                if (_autoStartChanging) return;
                bool want = _miAutoStart.Checked;
                _autoStartChanging = true;
                try
                {
                    bool ok = _autoStart.Set(want);
                    if (!ok)
                    {
                        _miAutoStart.Checked = !want;
                        return;
                    }
                    _cfg.AutoStart = want;
                    _configService.Save(_cfg);
                }
                finally
                {
                    _autoStartChanging = false;
                }
            };

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate
            {
                _tray.Visible = false;
                Application.Exit();
            };

            _menu.Items.Add(_miSwitch);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miShow);
            _menu.Items.Add(miRestart);
            _menu.Items.Add(_miAutoStart);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miExit);

            // 菜单每次打开时，从注册表重新读取自启真实状态，避免显示与系统不一致
            _menu.Opening += delegate
            {
                _autoStartChanging = true;
                try { _miAutoStart.Checked = _autoStart.IsEnabled(); }
                finally { _autoStartChanging = false; }
            };

            _tray = new NotifyIcon();
            _tray.Icon = UiUtil.LoadAppIcon(_cfg.QianwenExePath);
            _tray.Text = "千问账号快捷切换器";
            _tray.Visible = true;
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowForm();
            };
        }

        public void RebuildAccounts(string currentUid)
        {
            _lastCurrentUid = currentUid ?? string.Empty;
            ToolStripItemCollection items = _miSwitch.DropDownItems;
            items.Clear();
            if (_cfg.Accounts.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("（还没有保存的账号）");
                empty.Enabled = false;
                items.Add(empty);
                return;
            }
            foreach (AccountProfile acc in _cfg.Accounts)
            {
                ToolStripMenuItem mi = new ToolStripMenuItem();
                bool current = !string.IsNullOrEmpty(currentUid) &&
                    string.Equals(acc.Uid, currentUid, StringComparison.Ordinal);
                mi.Text = (current ? "● " : "    ") + acc.Name + "（" + acc.Nickname + "）";
                mi.Checked = current;
                mi.Tag = acc;
                mi.Click += delegate
                {
                    AccountProfile target = mi.Tag as AccountProfile;
                    if (target != null && !_switch.IsBusy)
                    {
                        _form.BeginInvoke(new Action(delegate { _form.SwitchTo(target); }));
                    }
                };
                items.Add(mi);
            }
        }

        public void ShowForm()
        {
            if (_form.InvokeRequired)
            {
                _form.BeginInvoke(new Action(ShowForm));
                return;
            }
            if (!_form.Visible) _form.Show();
            if (_form.WindowState == FormWindowState.Minimized)
                _form.WindowState = FormWindowState.Normal;
            _form.BringToFront();
            _form.Activate();
        }

        public void Notify(string title, string text, bool error)
        {
            if (_form.IsHandleCreated && _form.InvokeRequired)
            {
                _form.BeginInvoke(new Action(delegate { Notify(title, text, error); }));
                return;
            }
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text.Length > 120 ? text.Substring(0, 120) : text;
                _tray.BalloonTipIcon = error ? ToolTipIcon.Error : ToolTipIcon.Info;
                _tray.ShowBalloonTip(2500);
            }
            catch { }
        }

        public void Dispose()
        {
            try
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            catch { }
        }
    }
}
