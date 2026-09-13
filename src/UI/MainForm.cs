using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using QianwenSwitcher.Services;

namespace QianwenSwitcher.UI
{
    public class MainForm : Form
    {
        private readonly PathService _paths;
        private readonly ConfigService _configService;
        private readonly SwitchService _switch;
        private readonly AutoStartService _autoStart;
        private AppConfig _cfg;

        private string _currentUid = string.Empty;
        private bool _realExit = false;
        private bool _closeHintShown = false;
        private Font _boldFont;

        private ListView _list;
        private Label _lblCurrent;
        private Label _lblEmpty;
        private Button _btnNew;
        private Button _btnSwitch;
        private Button _btnUpdate;
        private Button _btnRename;
        private Button _btnDelete;
        private Button _btnAbout;
        private TextBox _txtExe;
        private TextBox _txtData;
        private Button _btnBrowseExe;
        private Button _btnBrowseData;
        private CheckBox _chkRelaunch;
        private CheckBox _chkAutoStart;
        private CheckBox _chkFullMode;
        private Button _btnSaveSettings;
        private ProgressBar _progress;
        private Label _lblStatus;

        public TrayContext Tray { get; set; }

        public MainForm(PathService paths, ConfigService configService,
            SwitchService switchService, AutoStartService autoStart, AppConfig cfg)
        {
            _paths = paths;
            _configService = configService;
            _switch = switchService;
            _autoStart = autoStart;
            _cfg = cfg;

            SuspendLayout();
            Text = "千问账号快捷切换器";
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(780, 540);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = UiUtil.LoadAppIcon(_cfg.QianwenExePath);
            _boldFont = new Font(Font, FontStyle.Bold);

            // Dock 顺序：先加边缘面板，最后加 Fill，保证列表填充剩余区域
            BuildRightPanel();
            BuildBottomPanel();
            BuildAccountList();

            ResumeLayout(false);
            LoadSettingsToUi();
        }

        // ------------------------------------------------------------ 界面构建

        private void BuildAccountList()
        {
            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.GridLines = false;
            _list.HideSelection = false;
            _list.OwnerDraw = true;
            _list.BorderStyle = BorderStyle.None;
            _list.Font = new Font("Microsoft YaHei UI", 9.5F);
            _list.Columns.Add("备注名", 190);
            _list.Columns.Add("千问昵称", 150);
            _list.Columns.Add("账号 ID", 160);
            _list.Columns.Add("快照时间", 150);
            _list.Columns.Add("大小", 80);
            _list.DoubleClick += delegate { SwitchTo(SelectedAccount()); };
            _list.SelectedIndexChanged += delegate { UpdateButtons(); };
            _list.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; };
            _list.DrawSubItem += OnDrawSubItem;
            _list.DrawItem += delegate { };

            _lblEmpty = new Label();
            _lblEmpty.Text = "还没有账号快照" + Environment.NewLine + Environment.NewLine
                + "请先在千问中登录账号，然后点击右侧按钮保存";
            _lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            _lblEmpty.ForeColor = Color.Gray;
            _lblEmpty.Font = new Font("Microsoft YaHei UI", 10F);
            _lblEmpty.Dock = DockStyle.Fill;
            _lblEmpty.BackColor = Color.White;

            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Color.White;
            host.Padding = new Padding(14, 12, 6, 8);
            host.Controls.Add(_lblEmpty);
            host.Controls.Add(_list);

            Controls.Add(host);
        }

        private void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            AccountProfile acc = e.Item.Tag as AccountProfile;
            bool current = acc != null && string.Equals(acc.Uid, _currentUid, StringComparison.Ordinal);
            bool selected = e.Item.Selected;

            Color back;
            if (selected) back = UiUtil.Accent;
            else if (current) back = UiUtil.LightBlue;
            else back = (e.ItemIndex % 2 == 0) ? Color.White : UiUtil.AltRow;
            Color fore = selected ? Color.White : Color.FromArgb(40, 40, 40);

            using (SolidBrush b = new SolidBrush(back))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }
            Font f = (e.ColumnIndex == 0 && current) ? _boldFont : _list.Font;
            Rectangle r = e.Bounds;
            r.X += 8;
            r.Width -= 12;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, f, r, fore, flags);
        }

        private void BuildRightPanel()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Right;
            p.Width = 150;
            p.Padding = new Padding(8, 10, 12, 10);
            p.BackColor = Color.FromArgb(245, 246, 248);

            _lblCurrent = new Label();
            _lblCurrent.Left = 10; _lblCurrent.Top = 8; _lblCurrent.Width = 128; _lblCurrent.Height = 56;
            _lblCurrent.Text = "当前账号：检测中…";
            _lblCurrent.ForeColor = Color.FromArgb(70, 70, 70);

            _btnNew = MakeButton("保存当前账号", 72);
            _btnSwitch = MakeButton("一键切换到此账号", 116);
            _btnUpdate = MakeButton("更新该账号快照", 160);
            _btnRename = MakeButton("重命名", 204);
            _btnDelete = MakeButton("删除", 248);
            _btnAbout = MakeButton("关于与安全说明", 292);
            int w = 128;
            foreach (Button b in new[] { _btnNew, _btnSwitch, _btnUpdate, _btnRename, _btnDelete, _btnAbout })
            {
                b.Left = 10; b.Width = w;
            }

            _btnNew.Click += SaveCurrentClick;
            _btnSwitch.Click += delegate { SwitchTo(SelectedAccount()); };
            _btnUpdate.Click += UpdateClick;
            _btnRename.Click += RenameClick;
            _btnDelete.Click += DeleteClick;
            _btnAbout.Click += AboutClick;

            p.Controls.Add(_lblCurrent);
            p.Controls.Add(_btnNew);
            p.Controls.Add(_btnSwitch);
            p.Controls.Add(_btnUpdate);
            p.Controls.Add(_btnRename);
            p.Controls.Add(_btnDelete);
            p.Controls.Add(_btnAbout);
            Controls.Add(p);
        }

        private Button MakeButton(string text, int top)
        {
            Button b = new Button();
            b.Text = text;
            b.Top = top;
            b.Height = 36;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
            b.BackColor = Color.White;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            return b;
        }

        private void BuildBottomPanel()
        {
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 148;
            bottom.BackColor = Color.FromArgb(245, 246, 248);

            // 进度行
            Panel progRow = new Panel();
            progRow.Dock = DockStyle.Bottom;
            progRow.Height = 30;
            _progress = new ProgressBar();
            _progress.Left = 14; _progress.Top = 7; _progress.Width = 240; _progress.Height = 16;
            _lblStatus = new Label();
            _lblStatus.Left = 264; _lblStatus.Top = 7; _lblStatus.Width = 520; _lblStatus.Height = 18;
            _lblStatus.Text = "就绪";
            _lblStatus.ForeColor = Color.DimGray;
            progRow.Controls.Add(_progress);
            progRow.Controls.Add(_lblStatus);

            GroupBox grp = new GroupBox();
            grp.Text = "设置";
            grp.Dock = DockStyle.Fill;
            grp.Padding = new Padding(10, 6, 10, 6);

            Label l1 = new Label(); l1.Text = "千问程序："; l1.Left = 12; l1.Top = 24; l1.Width = 76;
            _txtExe = new TextBox(); _txtExe.Left = 88; _txtExe.Top = 21; _txtExe.Width = 420;
            _btnBrowseExe = new Button(); _btnBrowseExe.Text = "浏览…"; _btnBrowseExe.Left = 516;
            _btnBrowseExe.Top = 19; _btnBrowseExe.Width = 64; _btnBrowseExe.Height = 26;
            _btnBrowseExe.Click += delegate
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Filter = "千问程序|qianwen.exe|可执行文件|*.exe";
                    if (File.Exists(_txtExe.Text)) ofd.FileName = _txtExe.Text;
                    if (ofd.ShowDialog(this) == DialogResult.OK) _txtExe.Text = ofd.FileName;
                }
            };

            Label l2 = new Label(); l2.Text = "数据目录："; l2.Left = 12; l2.Top = 54; l2.Width = 76;
            _txtData = new TextBox(); _txtData.Left = 88; _txtData.Top = 51; _txtData.Width = 420;
            _btnBrowseData = new Button(); _btnBrowseData.Text = "浏览…"; _btnBrowseData.Left = 516;
            _btnBrowseData.Top = 49; _btnBrowseData.Width = 64; _btnBrowseData.Height = 26;
            _btnBrowseData.Click += delegate
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "选择千问 User Data 目录";
                    if (Directory.Exists(_txtData.Text)) fbd.SelectedPath = _txtData.Text;
                    if (fbd.ShowDialog(this) == DialogResult.OK) _txtData.Text = fbd.SelectedPath;
                }
            };

            _chkRelaunch = new CheckBox(); _chkRelaunch.Text = "操作后自动重启千问";
            _chkRelaunch.Left = 600; _chkRelaunch.Top = 22; _chkRelaunch.Width = 180;
            _chkAutoStart = new CheckBox(); _chkAutoStart.Text = "开机自启动（驻留托盘）";
            _chkAutoStart.Left = 600; _chkAutoStart.Top = 48; _chkAutoStart.Width = 190;
            _chkAutoStart.CheckedChanged += AutoStartCheckedChanged;
            _chkFullMode = new CheckBox(); _chkFullMode.Text = "完整备份模式（兼容兜底）";
            _chkFullMode.Left = 600; _chkFullMode.Top = 74; _chkFullMode.Width = 190;

            _btnSaveSettings = new Button(); _btnSaveSettings.Text = "保存设置";
            _btnSaveSettings.Left = 88; _btnSaveSettings.Top = 80; _btnSaveSettings.Width = 100;
            _btnSaveSettings.Height = 28;
            _btnSaveSettings.Click += SaveSettingsClick;

            grp.Controls.Add(l1); grp.Controls.Add(_txtExe); grp.Controls.Add(_btnBrowseExe);
            grp.Controls.Add(l2); grp.Controls.Add(_txtData); grp.Controls.Add(_btnBrowseData);
            grp.Controls.Add(_chkRelaunch); grp.Controls.Add(_chkAutoStart);
            grp.Controls.Add(_chkFullMode); grp.Controls.Add(_btnSaveSettings);

            bottom.Controls.Add(progRow);
            bottom.Controls.Add(grp);
            Controls.Add(bottom);
        }

        // ------------------------------------------------------------ 设置区

        private void LoadSettingsToUi()
        {
            _txtExe.Text = _cfg.QianwenExePath;
            _txtData.Text = _cfg.UserDataPath;
            _chkRelaunch.Checked = _cfg.RelaunchAfterOperation;
            _chkAutoStart.Checked = _autoStart.IsEnabled();
            _chkFullMode.Checked = _cfg.FullMode;
        }

        private void SaveSettingsClick(object sender, EventArgs e)
        {
            string exe = _txtExe.Text.Trim();
            string data = _txtData.Text.Trim();
            if (!File.Exists(exe))
            {
                if (MessageBox.Show(this, "千问程序路径不存在，仍要保存吗？", "路径确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            if (!Directory.Exists(data))
            {
                if (MessageBox.Show(this, "数据目录不存在，仍要保存吗？", "路径确认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            _cfg.QianwenExePath = exe;
            _cfg.UserDataPath = data;
            _cfg.RelaunchAfterOperation = _chkRelaunch.Checked;
            _cfg.FullMode = _chkFullMode.Checked;
            _cfg.AutoStart = _chkAutoStart.Checked;
            _configService.Save(_cfg);
            Icon = UiUtil.LoadAppIcon(exe);
            SetStatus("设置已保存");
            Logger.Info("设置已更新");
        }

        private bool _autoStartChanging;

        private void AutoStartCheckedChanged(object sender, EventArgs e)
        {
            if (_autoStartChanging) return;
            if (!_chkAutoStart.Focused) return;
            bool want = _chkAutoStart.Checked;
            _autoStartChanging = true;
            try
            {
                bool ok = _autoStart.Set(want);
                if (!ok)
                {
                    // 设置失败，恢复勾选状态
                    _chkAutoStart.Checked = !want;
                    SetStatus("开机自启设置失败（见日志）");
                    return;
                }
                _cfg.AutoStart = want;
                _configService.Save(_cfg);
                SetStatus(want ? "已开启开机自启" : "已取消开机自启");
            }
            finally
            {
                _autoStartChanging = false;
            }
        }

        // ------------------------------------------------------------ 账号操作

        private AccountProfile SelectedAccount()
        {
            if (_list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as AccountProfile;
        }

        private async void SaveCurrentClick(object sender, EventArgs e)
        {
            if (_switch.IsBusy) return;
            if (!ConfirmPaths()) return;

            bool wasRunning = _switch.IsQianwenRunning(_cfg);
            if (wasRunning)
            {
                if (MessageBox.Show(this, "保存账号快照需要先临时关闭千问（约几秒，保存后自动重启），是否继续？",
                        "保存当前账号", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }

            Identity id;
            try
            {
                SetBusy(true, "正在关闭千问…");
                id = await _switch.CloseAndDetectAsync(_cfg, NewProgress());
            }
            catch (Exception ex)
            {
                Logger.Error("关闭/识别失败", ex);
                FinishWithError("操作失败：" + ex.Message, wasRunning);
                return;
            }

            if (!id.LoggedIn)
            {
                SetBusy(false, "未检测到登录账号");
                MessageBox.Show(this, "没有检测到已登录的千问账号，请先登录后再保存。", "未登录",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                RelaunchIfNeeded(wasRunning);
                return;
            }

            AccountProfile existing = _cfg.Accounts.FirstOrDefault(a =>
                string.Equals(a.Uid, id.Uid, StringComparison.Ordinal));
            if (existing != null)
            {
                SetBusy(false, "就绪");
                if (MessageBox.Show(this, "账号「" + existing.Name + "」已存在，是否更新它的快照？",
                        "账号已存在", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await DoUpdate(existing, id, wasRunning);
                }
                else
                {
                    RelaunchIfNeeded(wasRunning);
                }
                return;
            }

            string name = InputDialog.Show(this, "保存当前账号",
                "给这个账号起个备注名：", id.Nickname);
            if (name == null)
            {
                SetBusy(false, "已取消");
                RelaunchIfNeeded(wasRunning);
                return;
            }

            AccountProfile acc = new AccountProfile();
            acc.Id = Guid.NewGuid().ToString("N");
            acc.Name = name;
            acc.Uid = id.Uid;
            acc.Nickname = id.Nickname;
            acc.ZipFile = acc.Id + ".zip";
            acc.CreatedAt = SwitchService.NowText();
            acc.UpdatedAt = acc.CreatedAt;

            try
            {
                SetBusy(true, "正在保存账号快照…");
                await _switch.SaveSnapshotAsync(_cfg, acc, NewProgress());
                _cfg.Accounts.Add(acc);
                _configService.Save(_cfg);
                SetStatus("账号「" + name + "」已保存");
                if (Tray != null) Tray.Notify("保存成功", "账号「" + name + "」快照已保存", false);
            }
            catch (Exception ex)
            {
                Logger.Error("保存快照失败", ex);
                FinishWithError("保存失败：" + ex.Message, wasRunning);
                return;
            }

            await RefreshAccountsAsync();
            SetBusy(false, "就绪");
            RelaunchIfNeeded(wasRunning);
        }

        public async void SwitchTo(AccountProfile target)
        {
            if (target == null)
            {
                MessageBox.Show(this, "请先在列表中选择一个账号。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_switch.IsBusy)
            {
                if (Tray != null) Tray.Notify("请稍候", "上一个操作还在进行中", false);
                return;
            }
            if (!ConfirmPaths()) return;

            try
            {
                SetBusy(true, "正在切换到「" + target.Name + "」…");
                SwitchResult r = await _switch.SwitchAsync(_cfg, target, NewProgress());
                await RefreshAccountsAsync();
                if (r.Success && !r.SameAccount)
                {
                    SetStatus(r.Message);
                    if (Tray != null)
                    {
                        Tray.Notify(r.LoginVerified ? "切换成功" : "未确认登录",
                            r.Message, !r.LoginVerified);
                    }
                    if (!r.LoginVerified)
                    {
                        MessageBox.Show(this, r.Message, "未确认登录",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else if (r.SameAccount)
                {
                    SetStatus(r.Message);
                    if (Tray != null) Tray.Notify("无需切换", r.Message, false);
                }
                else
                {
                    SetStatus(r.Message);
                    MessageBox.Show(this, r.Message + (r.RolledBack ? Environment.NewLine + "已恢复到切换前的账号。" : ""),
                        "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    if (Tray != null) Tray.Notify("切换失败", "已回滚或需手动恢复", true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("切换失败", ex);
                MessageBox.Show(this, "切换失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, "就绪");
            }
        }

        private async void UpdateClick(object sender, EventArgs e)
        {
            AccountProfile acc = SelectedAccount();
            if (acc == null) return;
            if (_switch.IsBusy) return;
            if (!ConfirmPaths()) return;

            bool wasRunning = _switch.IsQianwenRunning(_cfg);
            if (MessageBox.Show(this, "更新快照会用当前登录态覆盖「" + acc.Name + "」的旧快照，需要先关闭千问，是否继续？",
                    "更新快照", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Identity id;
            try
            {
                SetBusy(true, "正在关闭千问…");
                id = await _switch.CloseAndDetectAsync(_cfg, NewProgress());
            }
            catch (Exception ex)
            {
                FinishWithError("操作失败：" + ex.Message, wasRunning);
                return;
            }

            if (!id.LoggedIn)
            {
                SetBusy(false, "未检测到登录账号");
                MessageBox.Show(this, "当前千问未登录，无法更新。", "未登录",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                RelaunchIfNeeded(wasRunning);
                return;
            }
            if (!string.Equals(id.Uid, acc.Uid, StringComparison.Ordinal))
            {
                SetBusy(false, "账号不匹配");
                MessageBox.Show(this, "当前登录的是另一个账号（" + id.Nickname + "），与该快照不匹配，已取消。"
                    + Environment.NewLine + "如需保存新账号，请使用「保存当前账号」。",
                    "账号不匹配", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RelaunchIfNeeded(wasRunning);
                return;
            }

            await DoUpdate(acc, id, wasRunning);
        }

        private async System.Threading.Tasks.Task DoUpdate(AccountProfile acc, Identity id, bool wasRunning)
        {
            try
            {
                SetBusy(true, "正在更新「" + acc.Name + "」快照…");
                await _switch.SaveSnapshotAsync(_cfg, acc, NewProgress());
                acc.Nickname = id.Nickname;
                acc.UpdatedAt = SwitchService.NowText();
                _configService.Save(_cfg);
                SetStatus("账号「" + acc.Name + "」快照已更新");
                if (Tray != null) Tray.Notify("更新成功", "账号「" + acc.Name + "」快照已更新", false);
                await RefreshAccountsAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("更新快照失败", ex);
                FinishWithError("更新失败：" + ex.Message, wasRunning);
                return;
            }
            SetBusy(false, "就绪");
            RelaunchIfNeeded(wasRunning);
        }

        private void RenameClick(object sender, EventArgs e)
        {
            AccountProfile acc = SelectedAccount();
            if (acc == null) return;
            string name = InputDialog.Show(this, "重命名账号", "新的备注名：", acc.Name);
            if (name == null) return;
            acc.Name = name;
            _configService.Save(_cfg);
            RefreshAccounts();
        }

        private void DeleteClick(object sender, EventArgs e)
        {
            AccountProfile acc = SelectedAccount();
            if (acc == null) return;
            if (MessageBox.Show(this,
                    "确定删除账号「" + acc.Name + "」的本地快照吗？" + Environment.NewLine
                    + "仅删除本机快照文件，不影响千问当前登录，也不会退出任何账号。",
                    "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                string zip = Path.Combine(_paths.ProfilesDir, acc.ZipFile);
                if (File.Exists(zip)) File.Delete(zip);
                _cfg.Accounts.Remove(acc);
                _configService.Save(_cfg);
                SetStatus("已删除账号「" + acc.Name + "」");
                RefreshAccounts();
            }
            catch (Exception ex)
            {
                Logger.Error("删除快照失败", ex);
                MessageBox.Show(this, "删除失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AboutClick(object sender, EventArgs e)
        {
            MessageBox.Show(this,
                "千问账号快捷切换器 v1.0" + Environment.NewLine + Environment.NewLine
                + "原理：关闭千问后，只备份/还原约 8MB 的登录态文件（Cookie、Local Storage 等），不碰缓存。"
                + Environment.NewLine + Environment.NewLine
                + "安全机制：每次切换前自动生成安全备份（profiles\\_safety，保留最近 3 份），"
                + "还原失败会自动回滚。" + Environment.NewLine + Environment.NewLine
                + "注意：账号快照等同于登录凭据，请妥善保管 profiles 目录，不要发给他人。",
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ------------------------------------------------------------ 列表刷新

        public async System.Threading.Tasks.Task RefreshAccountsAsync()
        {
            Identity id = null;
            try
            {
                id = await System.Threading.Tasks.Task.Run(
                    (Func<Identity>)(() => _switch.DetectOnly(_cfg)));
            }
            catch (Exception ex)
            {
                Logger.Warn("刷新账号状态失败: " + ex.Message);
            }
            _currentUid = id != null ? (id.Uid ?? string.Empty) : string.Empty;

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (AccountProfile acc in _cfg.Accounts)
            {
                long size = 0;
                try { size = new FileInfo(Path.Combine(_paths.ProfilesDir, acc.ZipFile)).Length; } catch { }
                bool current = string.Equals(acc.Uid, _currentUid, StringComparison.Ordinal);

                ListViewItem item = new ListViewItem();
                item.Text = (current ? "● " : "    ") + acc.Name;
                item.SubItems.Add(acc.Nickname);
                item.SubItems.Add(IdentityService.Mask(acc.Uid));
                item.SubItems.Add(acc.UpdatedAt);
                item.SubItems.Add(ProfileService.FormatSize(size));
                item.Tag = acc;
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            _lblEmpty.Visible = _cfg.Accounts.Count == 0;
            _list.Visible = _cfg.Accounts.Count > 0;
            if (id != null && id.LoggedIn)
            {
                _lblCurrent.Text = "当前账号：\n" + id.Nickname + "\n" + IdentityService.Mask(id.Uid);
            }
            else
            {
                _lblCurrent.Text = "当前账号：\n未检测到登录";
            }
            UpdateButtons();
            if (Tray != null) Tray.RebuildAccounts(_currentUid);
        }

        private void UpdateButtons()
        {
            bool sel = SelectedAccount() != null;
            _btnSwitch.Enabled = sel;
            _btnUpdate.Enabled = sel;
            _btnRename.Enabled = sel;
            _btnDelete.Enabled = sel;
        }

        // ------------------------------------------------------------ 辅助

        private bool ConfirmPaths()
        {
            if (!File.Exists(_cfg.QianwenExePath) || !Directory.Exists(_cfg.UserDataPath))
            {
                MessageBox.Show(this, "千问程序路径或数据目录无效，请在下方设置区检查后保存。", "路径无效",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private IProgress<OperationProgress> NewProgress()
        {
            return new Progress<OperationProgress>(p =>
            {
                _progress.Value = Math.Max(0, Math.Min(100, p.Percent));
                if (!string.IsNullOrEmpty(p.Message)) _lblStatus.Text = p.Message;
            });
        }

        private void SetBusy(bool busy, string status)
        {
            foreach (Control c in new Control[] { _btnNew, _btnSwitch, _btnUpdate, _btnRename,
                _btnDelete, _btnSaveSettings, _btnBrowseExe, _btnBrowseData, _list })
            {
                c.Enabled = !busy;
            }
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
            if (!busy)
            {
                _progress.Value = 0;  // 操作结束后重置进度条
                UpdateButtons();
            }
        }

        private void FinishWithError(string message, bool wasRunning)
        {
            SetBusy(false, message);
            MessageBox.Show(this, message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            RelaunchIfNeeded(wasRunning);
        }

        private void RelaunchIfNeeded(bool wasRunning)
        {
            if (wasRunning && _cfg.RelaunchAfterOperation)
            {
                try { _switch.StartQianwen(_cfg); } catch { }
            }
        }

        private void SetStatus(string text)
        {
            _lblStatus.Text = text;
            Logger.Info(text);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            await RefreshAccountsAsync();
        }

        /// <summary>同步上下文里使用的“发射后不管”刷新入口（保证 UI 线程上继续执行）</summary>
        private async void RefreshAccounts()
        {
            await RefreshAccountsAsync();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!_realExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!_closeHintShown)
                {
                    _closeHintShown = true;
                    if (Tray != null) Tray.Notify("仍在后台运行", "我会待在托盘里，右键图标可快速换号", false);
                }
            }
        }

        public void RealExit()
        {
            _realExit = true;
            Close();
        }
    }
}
