using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QianwenSwitcher.Services
{
    /// <summary>保存 / 更新 / 切换的编排服务，串联进程、身份、快照各服务</summary>
    public class SwitchService
    {
        private readonly PathService _paths;
        private readonly ConfigService _configService;
        private readonly ProfileService _profileService;
        private readonly IdentityService _identityService;
        private int _busy;

        public bool IsBusy { get { return Thread.VolatileRead(ref _busy) != 0; } }

        public SwitchService(PathService paths, ConfigService configService,
            ProfileService profileService, IdentityService identityService)
        {
            _paths = paths;
            _configService = configService;
            _profileService = profileService;
            _identityService = identityService;
        }

        private bool EnterBusy()
        {
            return Interlocked.Exchange(ref _busy, 1) == 0;
        }

        private void LeaveBusy()
        {
            Interlocked.Exchange(ref _busy, 0);
        }

        private ProcessService NewProcessService(AppConfig cfg)
        {
            return new ProcessService(cfg.QianwenExePath);
        }

        /// <summary>关闭千问并识别当前身份（保存/更新快照前调用）</summary>
        public Task<Identity> CloseAndDetectAsync(AppConfig cfg, IProgress<OperationProgress> progress)
        {
            return Task.Run(delegate
            {
                if (!EnterBusy()) throw new InvalidOperationException("已有操作正在进行，请稍候");
                try
                {
                    StopQianwen(cfg, progress);
                    if (progress != null) progress.Report(new OperationProgress(85, "正在识别当前登录账号…"));
                    return _identityService.Read(cfg.UserDataPath);
                }
                finally { LeaveBusy(); }
            });
        }

        /// <summary>把当前实时登录态打成账号 zip（调用前需已关闭千问）</summary>
        public Task<long> SaveSnapshotAsync(AppConfig cfg, AccountProfile acc,
            IProgress<OperationProgress> progress)
        {
            return Task.Run(delegate
            {
                if (!EnterBusy()) throw new InvalidOperationException("已有操作正在进行，请稍候");
                try
                {
                    // 保存前必须确认真实登录 blob 存在。绝不在此时写占位文件——
                    // 占位会让身份识别把登出态误判为已登录，进而把好快照覆盖成坏快照。
                    if (!string.IsNullOrEmpty(acc.Uid) &&
                        !HasRealUserInfoBlob(cfg.UserDataPath, acc.Uid))
                    {
                        throw new InvalidOperationException(
                            "未检测到账号「" + acc.Name + "」的真实登录信息，请确认千问已登录该账号后再保存快照");
                    }
                    string zipPath = Path.Combine(_paths.ProfilesDir, acc.ZipFile);
                    long size = _profileService.CreateSnapshot(cfg, zipPath, progress);
                    Logger.Info("账号快照已保存: " + acc.Name + " (" + IdentityService.Mask(acc.Uid) + ")");
                    return size;
                }
                finally { LeaveBusy(); }
            });
        }

        /// <summary>
        /// 判断 qianwen_user_info/<uid> 是否为千问写入的真实加密 blob
        /// （≥100 字节且非占位）。占位文件与缺失都代表没有真实登录。
        /// </summary>
        public static bool HasRealUserInfoBlob(string userDataPath, string uid)
        {
            try
            {
                if (string.IsNullOrEmpty(uid)) return false;
                string f = Path.Combine(userDataPath, "qianwen_user_info", uid);
                if (!File.Exists(f)) return false;
                FileInfo fi = new FileInfo(f);
                if (fi.Length < 100) return false;
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    int n = (int)Math.Min(200, fs.Length);
                    byte[] buf = new byte[n];
                    int read = 0;
                    while (read < n)
                    {
                        int r = fs.Read(buf, read, n - read);
                        if (r <= 0) break;
                        read += r;
                    }
                    if (read < n) return false;
                    string head = System.Text.Encoding.ASCII.GetString(buf);
                    if (head.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>一键切换：安全备份→更新当前账号快照→还原目标快照→失败自动回滚→重启千问</summary>
        public Task<SwitchResult> SwitchAsync(AppConfig cfg, AccountProfile target,
            IProgress<OperationProgress> progress)
        {
            return Task.Run(delegate
            {
                SwitchResult result = new SwitchResult();
                if (!EnterBusy())
                {
                    result.Message = "已有操作正在进行，请稍候";
                    return result;
                }
                try
                {
                    if (!File.Exists(Path.Combine(_paths.ProfilesDir, target.ZipFile)))
                    {
                        result.Message = "目标账号快照文件不存在：" + target.ZipFile;
                        return result;
                    }

                    bool wasRunning = NewProcessService(cfg).IsQianwenRunning();
                    StopQianwen(cfg, progress);

                    Identity current = _identityService.Read(cfg.UserDataPath);
                    result.CurrentIdentity = current;

                    if (current.LoggedIn &&
                        string.Equals(current.Uid, target.Uid, StringComparison.Ordinal))
                    {
                        result.Success = true;
                        result.SameAccount = true;
                        result.Message = "当前已经是账号「" + target.Name + "」，无需切换";
                        Logger.Info("切换跳过：当前已是目标账号 " + IdentityService.Mask(target.Uid));
                        if (wasRunning && cfg.RelaunchAfterOperation)
                        {
                            // 等文件句柄释放再启动，否则千问会因文件被占而频闪崩溃-重启循环
                            NewProcessService(cfg).WaitFilesReleased(cfg.UserDataPath, null);
                            StartQianwen(cfg);
                        }
                        return result;
                    }

                    // 在整个"备份→快照→还原→回滚"窗口内启动后台守护，
                    // 任何千问生态进程复活都会在 250ms 内被杀掉，杜绝句柄抢占。
                    ProcessService guardPs = NewProcessService(cfg);
                    bool doRelaunch = false;
                    bool hardFail = false;
                    using (RestoreGuard guard = guardPs.StartRestoreGuard())
                    {
                        // 1) 安全备份当前实时登录态
                        if (progress != null) progress.Report(new OperationProgress(20, "正在创建切换前安全备份…"));
                        string safetyZip = _profileService.NewSafetyZipPath();
                        _profileService.CreateSnapshot(cfg, safetyZip, progress);

                        // 2) 当前账号若已在列表中，顺手更新它的快照，避免新登录态丢失
                        if (current.LoggedIn)
                        {
                            AccountProfile currentAcc = cfg.Accounts.FirstOrDefault(a =>
                                string.Equals(a.Uid, current.Uid, StringComparison.Ordinal) &&
                                !string.Equals(a.Id, target.Id, StringComparison.Ordinal));
                            if (currentAcc != null)
                            {
                                if (progress != null) progress.Report(new OperationProgress(40, "正在更新当前账号快照…"));
                                // current.LoggedIn 已保证存在真实 blob，不再写占位文件
                                string curZip = Path.Combine(_paths.ProfilesDir, currentAcc.ZipFile);
                                _profileService.CreateSnapshot(cfg, curZip, progress);
                                currentAcc.Nickname = current.Nickname;
                                currentAcc.UpdatedAt = NowText();
                                _configService.Save(cfg);
                            }
                        }

                        // 3) 还原目标快照；失败立即用安全备份回滚
                        if (progress != null) progress.Report(new OperationProgress(60, "正在还原目标账号登录态…"));
                        try
                        {
                            _profileService.RestoreSnapshot(
                                Path.Combine(_paths.ProfilesDir, target.ZipFile),
                                cfg.UserDataPath, progress, target.Uid,
                                delegate (string lockedPath)
                                {
                                    // Restart Manager 精确查杀占用该路径的进程，再补一次全量强杀
                                    guardPs.KillLockers(lockedPath);
                                    guardPs.ForceKillAll();
                                });
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("还原目标快照失败，开始自动回滚", ex);
                            result.Message = "切换失败，已自动回滚到原账号：" + ex.Message;
                            try
                            {
                                _profileService.RestoreSnapshot(safetyZip, cfg.UserDataPath, null,
                                    current != null && current.LoggedIn ? current.Uid : null,
                                    delegate (string lockedPath)
                                    {
                                        guardPs.KillLockers(lockedPath);
                                        guardPs.ForceKillAll();
                                    });
                                result.RolledBack = true;
                                Logger.Info("自动回滚成功");
                            }
                            catch (Exception ex2)
                            {
                                Logger.Error("自动回滚也失败了！安全备份保留在: " + safetyZip, ex2);
                                result.Message = "切换与回滚均失败，请用 _safety 目录中最新备份手动恢复。" + Environment.NewLine + ex.Message;
                                hardFail = true;
                            }
                            doRelaunch = wasRunning && cfg.RelaunchAfterOperation;
                            if (doRelaunch)
                            {
                                NewProcessService(cfg).WaitFilesReleased(cfg.UserDataPath, null);
                            }
                            goto RelaunchPhase;
                        }

                        // 4) 裁剪安全备份，只保留最近 3 份
                        _profileService.PruneSafety(3);

                        // 5) 等待还原后的文件句柄完全释放（防索引/杀毒短暂锁定导致千问启动频闪）
                        if (progress != null) progress.Report(new OperationProgress(90, "正在等待文件就绪…"));
                        bool released = NewProcessService(cfg).WaitFilesReleased(cfg.UserDataPath, null);
                        if (!released) Logger.Info("文件释放等待超时，仍尝试启动千问");

                        doRelaunch = cfg.RelaunchAfterOperation;
                    }

                RelaunchPhase:
                    // 守护已停止（不会误杀新实例），再启动千问
                    if (doRelaunch)
                    {
                        if (progress != null) progress.Report(new OperationProgress(96, "正在重新启动千问…"));
                        bool started = StartQianwen(cfg);
                        if (started)
                        {
                            // 等待千问校验会话：成功后会把真实 blob 写入/刷新到
                            // qianwen_user_info/<uid>；占位文件（22 字节）原样存在则说明未登录
                            for (int i = 0; i < 25; i++)
                            {
                                Thread.Sleep(1000);
                                if (HasRealUserInfoBlob(cfg.UserDataPath, target.Uid)) break;
                                if (progress != null && i % 3 == 2)
                                    progress.Report(new OperationProgress(97,
                                        "正在等待千问确认登录（" + (i + 1) + "/25 秒）…"));
                            }
                        }
                        result.LoginVerified =
                            HasRealUserInfoBlob(cfg.UserDataPath, target.Uid);
                        Logger.Info("登录确认: " + (result.LoginVerified ? "成功" : "未检测到真实 blob")
                            + " -> " + IdentityService.Mask(target.Uid));
                    }

                    if (result.RolledBack || hardFail)
                    {
                        return result;
                    }

                    result.Success = true;
                    if (doRelaunch)
                    {
                        result.Message = result.LoginVerified
                            ? "已切换到账号「" + target.Name + "」，并确认登录成功"
                            : "已还原账号「" + target.Name + "」的登录态并启动千问，但未在 25 秒内检测到登录确认。"
                              + "请查看千问界面：若显示“登录”按钮，请在千问中重新登录该账号后点“更新该账号快照”。";
                    }
                    else
                    {
                        result.Message = "已切换到账号「" + target.Name + "」（未自动重启千问）";
                    }
                    Logger.Info("切换成功 -> " + target.Name + " (" + IdentityService.Mask(target.Uid)
                        + "), loginVerified=" + result.LoginVerified);
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Error("切换流程异常", ex);
                    result.Message = "切换异常：" + ex.Message;
                    return result;
                }
                finally { LeaveBusy(); }
            });
        }

        /// <summary>停止千问全部进程并等待关键文件句柄释放</summary>
        public void StopQianwen(AppConfig cfg, IProgress<OperationProgress> progress)
        {
            ProcessService ps = NewProcessService(cfg);
            ps.StopAll(delegate(string m)
            {
                if (progress != null) progress.Report(new OperationProgress(5, m));
            });
            bool released = ps.WaitFilesReleased(cfg.UserDataPath, delegate(string m)
            {
                if (progress != null) progress.Report(new OperationProgress(10, m));
            });
            if (!released)
            {
                Logger.Warn("等待文件句柄释放超时，继续尝试操作");
            }
        }

        public bool StartQianwen(AppConfig cfg)
        {
            return NewProcessService(cfg).Start(cfg.QianwenExePath, Path.GetDirectoryName(cfg.QianwenExePath));
        }

        public bool IsQianwenRunning(AppConfig cfg)
        {
            return NewProcessService(cfg).IsQianwenRunning();
        }

        public Identity DetectOnly(AppConfig cfg)
        {
            return _identityService.Read(cfg.UserDataPath);
        }

        public static string NowText()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
