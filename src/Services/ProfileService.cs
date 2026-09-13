using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace QianwenSwitcher.Services
{
    /// <summary>
    /// 账号快照的创建（zip）与还原（暂存→校验→替换），以及安全备份的裁剪。
    /// 所有暂存文件都放在程序目录 profiles\_staging 下，不使用 C 盘临时目录。
    /// </summary>
    public class ProfileService
    {
        private readonly PathService _paths;

        // 完整模式下排除的缓存目录（相对 User Data 的任意一段路径名）
        private static readonly string[] ExcludedSegments =
        {
            "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "GraphiteDawnCache", "GrShaderCache", "ShaderCache", "Crashpad",
            "JumpListIconsMostVisited", "JumpListIconsRecentClosed", "VideoDecodeStats",
            "WebrtcVideoStats", "image_cache", "remote_resource_cache", "CacheStorage",
            "DawnGraphiteCache", "GraphiteDawnCache"
        };

        public ProfileService(PathService paths)
        {
            _paths = paths;
        }

        // ---------------------------------------------------------------- 枚举

        /// <summary>返回需要快照的全部文件（相对 User Data 的路径，以反斜杠分隔）</summary>
        public List<string> CollectRelativeFiles(AppConfig cfg)
        {
            List<string> rels = new List<string>();
            string root = cfg.UserDataPath.TrimEnd('\\');

            if (cfg.FullMode)
            {
                string defaultDir = Path.Combine(root, "Default");
                if (Directory.Exists(defaultDir))
                {
                    Walk(defaultDir, defaultDir, rels, true);
                }
                AddFile(root, "Local State", rels);
                string infoDir = Path.Combine(root, "qianwen_user_info");
                if (Directory.Exists(infoDir)) Walk(infoDir, root, rels, false);
                return rels;
            }

            foreach (string raw in cfg.SnapshotItems)
            {
                string item = (raw ?? string.Empty).Replace('/', '\\').Trim('\\');
                if (item.Length == 0) continue;
                string full = Path.Combine(root, item);
                if (File.Exists(full))
                {
                    rels.Add(item);
                }
                else if (Directory.Exists(full))
                {
                    Walk(full, root, rels, false);
                }
            }
            return rels.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddFile(string root, string rel, List<string> rels)
        {
            if (File.Exists(Path.Combine(root, rel))) rels.Add(rel);
        }

        private static void Walk(string dir, string root, List<string> rels, bool pruneCache)
        {
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { return; }

            foreach (string f in files)
            {
                string rel = f.Substring(root.Length + 1);
                rels.Add(rel);
            }
            foreach (string d in subDirs)
            {
                string name = Path.GetFileName(d);
                if (pruneCache && IsExcluded(Path.Combine(d).Substring(root.Length + 1), name)) continue;
                Walk(d, root, rels, pruneCache);
            }
        }

        private static bool IsExcluded(string rel, string segmentName)
        {
            if (rel.StartsWith(@"Default\Service Worker\CacheStorage", StringComparison.OrdinalIgnoreCase))
                return true;
            if (segmentName.StartsWith("Backup_", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (string ex in ExcludedSegments)
            {
                if (string.Equals(segmentName, ex, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 打包

        public long CreateSnapshot(AppConfig cfg, string zipPath, IProgress<OperationProgress> progress)
        {
            List<string> rels = CollectRelativeFiles(cfg);
            string root = cfg.UserDataPath.TrimEnd('\\');

            string tmpZip = zipPath + ".tmp";
            if (File.Exists(tmpZip)) TryDelete(tmpZip);
            if (File.Exists(zipPath)) TryDelete(zipPath);

            long totalBytes = 0;
            foreach (string rel in rels)
            {
                try { totalBytes += new FileInfo(Path.Combine(root, rel)).Length; } catch { }
            }

            using (FileStream fs = new FileStream(tmpZip, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                int done = 0;
                long doneBytes = 0;
                foreach (string rel in rels)
                {
                    string full = Path.Combine(root, rel);
                    string entryName = rel.Replace('\\', '/');
                    try
                    {
                        // 注意：Create 模式下条目一旦写入就不能再读 Length/CompressedLength（会抛异常），故不能取条目大小
                        zip.CreateEntryFromFile(full, entryName, CompressionLevel.Optimal);
                        try { doneBytes += new FileInfo(full).Length; } catch { }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("打包跳过文件 " + rel + ": " + ex.Message);
                    }
                    done++;
                    if (progress != null && (done % 20 == 0 || done == rels.Count))
                    {
                        int pct = totalBytes == 0 ? 0
                            : (int)Math.Min(99, (long)done * 70 / Math.Max(1, rels.Count));
                        progress.Report(new OperationProgress(pct, "正在保存登录态快照 " + done + "/" + rels.Count));
                    }
                }
            }

            File.Move(tmpZip, zipPath);
            long size = new FileInfo(zipPath).Length;
            Logger.Info("快照已创建: " + Path.GetFileName(zipPath) + "，" + rels.Count + " 个文件，"
                + (size / 1024) + " KB");
            if (progress != null)
                progress.Report(new OperationProgress(100, "快照完成"));
            return size;
        }

        // ---------------------------------------------------------------- 还原

        /// <summary>暂存→校验→清理旧根→覆盖。任何异常由调用方用安全备份回滚。</summary>
        /// <param name="targetUid">目标账号 UID，用于还原后兜底补齐 qianwen_user_info 文件</param>
        /// <param name="onLocked">删除某路径失败时回调，参数为被占用的路径（用于精确查杀占用进程）</param>
        public void RestoreSnapshot(string zipPath, string userDataPath,
            IProgress<OperationProgress> progress, string targetUid = null,
            Action<string> onLocked = null)
        {
            if (!File.Exists(zipPath)) throw new FileNotFoundException("快照文件不存在", zipPath);
            string root = userDataPath.TrimEnd('\\');
            string stage = _paths.NewStagingFolder();
            Logger.Info("开始还原快照: " + Path.GetFileName(zipPath) + "，暂存目录 " + stage);

            try
            {
                if (progress != null) progress.Report(new OperationProgress(10, "正在解包到暂存目录…"));
                ZipFile.ExtractToDirectory(zipPath, stage);

                List<string> stagedFiles = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                    .ToList();
                int zipEntryCount;
                long zipTotalSize = 0;
                using (ZipArchive zr = ZipFile.OpenRead(zipPath))
                {
                    zipEntryCount = zr.Entries.Count(e => e.Name.Length > 0 || !e.FullName.EndsWith("/"));
                    foreach (ZipArchiveEntry e in zr.Entries)
                    {
                        if (!e.FullName.EndsWith("/")) zipTotalSize += e.Length;
                    }
                }

                // 校验：文件数与总大小一致
                long stagedTotal = stagedFiles.Sum(f => new FileInfo(f).Length);
                if (stagedFiles.Count != zipEntryCount || stagedTotal != zipTotalSize)
                {
                    throw new InvalidDataException(string.Format(
                        "快照校验失败（条目 {0}/{1}，大小 {2}/{3}），已中止且未改动千问数据",
                        stagedFiles.Count, zipEntryCount, stagedTotal, zipTotalSize));
                }
                if (progress != null) progress.Report(new OperationProgress(55, "快照校验通过，正在替换登录态…"));

                List<string> rels = stagedFiles
                    .Select(f => f.Substring(stage.Length + 1).Replace('/', '\\'))
                    .ToList();
                List<string> roots = rels.Select(RootOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // 1) 删除旧的受管根（目录整删；文件连同 sqlite 旁车文件一起删）
                //    qianwen_user_info 始终加入删除列表，防止旧账号文件残留
                if (!roots.Contains("qianwen_user_info", StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add("qianwen_user_info");
                }
                // Default\Sync Data 始终清空：千问把登录身份写在 Chromium 同步库里，
                // 旧快照（升级前制作）不含该项，不清掉会残留上一个账号的活动身份。
                // 清空后若快照里没有该目录即为中性态，千问会凭 cookies 重建（已实测可行）。
                if (!roots.Contains(@"Default\Sync Data", StringComparer.OrdinalIgnoreCase))
                {
                    roots.Add(@"Default\Sync Data");
                }
                foreach (string r in roots)
                {
                    string live = Path.Combine(root, r);
                    if (Directory.Exists(live))
                    {
                        Retry(delegate
                        {
                            try
                            {
                                DeleteDirectoryRobust(live);
                            }
                            catch (Exception)
                            {
                                // 删除失败通常是进程内存映射了 leveldb 文件；
                                // 回调按 Restart Manager 精确查杀占用进程后再重试
                                if (onLocked != null) { try { onLocked(live); } catch { } }
                                throw;
                            }
                        }, 20);
                    }
                    else if (File.Exists(live))
                    {
                        Retry(delegate
                        {
                            try
                            {
                                File.Delete(live);
                                TryDelete(live + "-journal");
                                TryDelete(live + "-wal");
                                TryDelete(live + "-shm");
                            }
                            catch (Exception)
                            {
                                if (onLocked != null) { try { onLocked(live); } catch { } }
                                throw;
                            }
                        }, 20);
                    }
                }

                // 2) 拷贝暂存文件到真实目录
                int copied = 0;
                foreach (string rel in rels)
                {
                    string src = Path.Combine(stage, rel);
                    string dst = Path.Combine(root, rel);
                    string dstDir = Path.GetDirectoryName(dst);
                    if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
                    CopyWithRetry(src, dst);
                    copied++;
                    if (progress != null && copied % 20 == 0)
                        progress.Report(new OperationProgress(60 + copied * 35 / Math.Max(1, rels.Count),
                            "正在还原 " + copied + "/" + rels.Count));
                }
                Logger.Info("快照还原完成，共 " + copied + " 个文件");

                // 3) 兜底补齐 qianwen_user_info/<uid> 文件。
                //    千问靠此目录下以 UID 命名的文件识别当前登录用户；
                //    若快照中缺失（保存时千问尚未写入），创建占位文件让千问回退到 cookies 登录。
                if (!string.IsNullOrEmpty(targetUid))
                {
                    EnsureUserInfoFile(root, targetUid);
                }
            }
            finally
            {
                try { Directory.Delete(stage, true); } catch { }
            }
        }

        /// <summary>
        /// 确保 qianwen_user_info 目录下存在以 uid 命名的文件。
        /// 若快照已还原该文件且内容有效则保留；否则删除其他账号残留文件并创建
        /// 占位文件。占位内容无效时千问会回退到 cookies/local storage 验证会话。
        /// 注意：qianwen_user_info 的加密内容与账号绑定，不能跨账号复用。
        /// </summary>
        private static void EnsureUserInfoFile(string userDataPath, string uid)
        {
            try
            {
                string dir = Path.Combine(userDataPath, "qianwen_user_info");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string targetFile = Path.Combine(dir, uid);

                // 先清理其他账号的残留文件（关键：防止旧账号 UID 残留导致千问识别错误账号）
                CleanupOtherUserInfoFiles(dir, uid);

                // 若目标文件已存在且内容有效（非占位），直接用
                if (File.Exists(targetFile))
                {
                    string existing = File.ReadAllText(targetFile);
                    if (!IsPlaceholderUserInfo(existing)) return;
                }

                // 创建占位文件（内容无效，千问会回退到 cookies 登录）
                string ts = ((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
                string content = ts + "|placeholder";
                File.WriteAllText(targetFile, content);
                Logger.Info("qianwen_user_info 已创建占位文件: " + uid);
            }
            catch (Exception ex)
            {
                Logger.Warn("补齐 qianwen_user_info 失败: " + ex.Message);
            }
        }

        /// <summary>删除 qianwen_user_info 目录下除目标 UID 外的所有文件</summary>
        private static void CleanupOtherUserInfoFiles(string dir, string keepUid)
        {
            try
            {
                string[] files = Directory.GetFiles(dir);
                foreach (string f in files)
                {
                    if (!string.Equals(Path.GetFileName(f), keepUid, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>判断 qianwen_user_info 内容是否为无效占位</summary>
        private static bool IsPlaceholderUserInfo(string content)
        {
            if (string.IsNullOrEmpty(content)) return true;
            if (content.Contains("placeholder")) return true;
            // 有效内容通常 > 100 字节（时间戳 + base64 加密令牌）
            if (content.Length < 100) return true;
            return false;
        }

        /// <summary>把一条相对路径归一到受管根，用于还原前清理，防止上一个账号的残留导致串号</summary>
        private static string RootOf(string rel)
        {
            if (rel.StartsWith(@"Default\", StringComparison.OrdinalIgnoreCase))
            {
                string rest = rel.Substring(8);
                int i = rest.IndexOf('\\');
                return i < 0 ? rel : @"Default\" + rest.Substring(0, i);
            }
            int j = rel.IndexOf('\\');
            return j < 0 ? rel : rel.Substring(0, j);
        }

        // ---------------------------------------------------------------- 安全备份

        public string NewSafetyZipPath()
        {
            return Path.Combine(_paths.SafetyDir,
                "safetynet_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".zip");
        }

        public void PruneSafety(int keep)
        {
            try
            {
                List<FileInfo> olds = Directory.GetFiles(_paths.SafetyDir, "safetynet_*.zip")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
                for (int i = keep; i < olds.Count; i++)
                {
                    TryDelete(olds[i].FullName);
                    Logger.Info("清理旧安全备份: " + olds[i].Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("清理安全备份失败: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 工具

        private static void CopyWithRetry(string src, string dst)
        {
            Exception last = null;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Copy(src, dst, true);
                    File.SetLastWriteTime(dst, File.GetLastWriteTime(src));
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(150);
                }
            }
            throw last;
        }

        private static void Retry(Action a, int times)
        {
            Exception last = null;
            for (int i = 0; i < times; i++)
            {
                try { a(); return; }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(500);
                }
            }
            throw last;
        }

        /// <summary>
        /// 健壮删除目录：先清只读属性，再整删；整删失败时退化为逐个文件删除；
        /// 仍失败时用"重命名兜底"——把目录改名移走，让还原流程能创建全新目录。
        /// 应对 leveldb 数据文件句柄延迟释放、被搜索索引器/杀毒占用等情况。
        /// </summary>
        private static void DeleteDirectoryRobust(string dir)
        {
            // 1) 清除所有只读属性
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        FileAttributes attr = File.GetAttributes(f);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                    }
                    catch { }
                }
            }
            catch { }

            // 2) 尝试整目录删除
            try
            {
                Directory.Delete(dir, true);
                return;
            }
            catch { }

            // 3) 退化为逐个文件删除（某些文件句柄延迟释放时，逐个删可能成功）
            try
            {
                string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (string f in files)
                {
                    try { File.Delete(f); }
                    catch
                    {
                        try
                        {
                            FileAttributes attr = File.GetAttributes(f);
                            if ((attr & FileAttributes.ReadOnly) != 0)
                                File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
                            File.Delete(f);
                        }
                        catch { }
                    }
                }
                // 删除所有空子目录（从深到浅）
                string[] dirs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
                Array.Sort(dirs, (a, b) => b.Length.CompareTo(a.Length));
                foreach (string d in dirs)
                {
                    try { Directory.Delete(d, false); } catch { }
                }
                Directory.Delete(dir, false);
                return;
            }
            catch { }

            // 4) 终极兜底：重命名移走。重命名只需要父目录写权限，
            //    即使内部文件被锁也能成功，这样还原流程可创建全新目录。
            if (Directory.Exists(dir))
            {
                string trash = dir + ".trash_" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                try
                {
                    Directory.Move(dir, trash);
                    Logger.Info("目录无法删除，已重命名移走: " + Path.GetFileName(dir) + " -> " + Path.GetFileName(trash));
                    // 后台尝试清理被移走的目录（不阻塞还原流程）
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            try
                            {
                                if (Directory.Exists(trash)) Directory.Delete(trash, true);
                                break;
                            }
                            catch { System.Threading.Thread.Sleep(500); }
                        }
                    });
                    return;
                }
                catch (Exception ex)
                {
                    // 重命名也失败，抛出让上层重试
                    throw new IOException("删除目录失败且无法重命名: " + dir, ex);
                }
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }
    }
}
