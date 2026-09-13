using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace QianwenSwitcher.Services
{
    /// <summary>
    /// 识别当前千问登录身份。
    /// 唯一可靠依据：qianwen_user_info 下以 uId 命名、且内容为真实加密 blob 的文件
    /// （千问登录校验通过后才会写入/刷新，通常数百字节以上）。
    /// 注意：本程序还原时写的 22 字节 "时间戳|placeholder" 占位文件、以及 leveldb
    /// 历史 SST 中残留的旧账号 uId 字符串都不能作为已登录依据，否则会在登出/半初始化
    /// 状态下把好快照覆盖成坏快照（脏快照循环）。
    /// </summary>
    public class IdentityService
    {
        public Identity Read(string userDataPath)
        {
            Identity id = new Identity();
            try
            {
                string preferredUid = ReadRealUidFromUserInfo(userDataPath);

                if (!string.IsNullOrEmpty(preferredUid))
                {
                    id.Uid = preferredUid;
                    id.LoggedIn = true;

                    Dictionary<string, string> scanned = ScanAllLevelDb(userDataPath);
                    if (scanned != null && scanned.ContainsKey(preferredUid))
                    {
                        id.Nickname = scanned[preferredUid];
                    }
                    if (string.IsNullOrEmpty(id.Nickname))
                    {
                        // 昵称尚未写入磁盘时，用 uid 后 4 位做标识
                        id.Nickname = "账号" + id.Uid.Substring(id.Uid.Length - 4);
                    }
                }

                Logger.Info("识别当前登录身份: " + (id.LoggedIn
                    ? ("uId=" + Mask(id.Uid) + ", nickname=" + id.Nickname)
                    : "未登录（无真实 user_info blob）"));
            }
            catch (Exception ex)
            {
                Logger.Error("识别登录身份失败", ex);
            }
            return id;
        }

        /// <summary>
        /// 返回当前真正登录账号的 uId：仅当 qianwen_user_info 目录下存在以纯数字 uId
        /// 命名、且内容为真实 blob（非占位、长度 ≥100）的文件时。取最近写入的一个。
        /// </summary>
        private static string ReadRealUidFromUserInfo(string userDataPath)
        {
            try
            {
                string dir = Path.Combine(userDataPath, "qianwen_user_info");
                if (!Directory.Exists(dir)) return string.Empty;
                List<FileInfo> files = Directory.GetFiles(dir)
                    .Select(p => new FileInfo(p))
                    .Where(f => Regex.IsMatch(f.Name, "^[0-9]{6,}$"))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
                foreach (FileInfo f in files)
                {
                    try
                    {
                        if (f.Length < 100) continue;
                        // 大文件只读前 200 字节即可识别占位；真实 blob 为 时间戳|base64
                        int n = (int)Math.Min(200, f.Length);
                        byte[] head = new byte[n];
                        using (FileStream fs = new FileStream(f.FullName, FileMode.Open,
                                   FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            int read = 0;
                            while (read < n)
                            {
                                int r = fs.Read(head, read, n - read);
                                if (r <= 0) break;
                                read += r;
                            }
                            if (read < n) continue;
                        }
                        string text = Encoding.ASCII.GetString(head);
                        if (text.IndexOf("placeholder", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        return f.Name;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("读取 qianwen_user_info 失败: " + ex.Message);
            }
            return string.Empty;
        }

        private static Dictionary<string, string> ScanAllLevelDb(string userDataPath)
        {
            var result = new Dictionary<string, string>();
            try
            {
                // 收集所有可能包含 leveldb 的目录
                var dirs = new List<string>();
                string localLevelDb = Path.Combine(userDataPath, @"Default\Local Storage\leveldb");
                if (Directory.Exists(localLevelDb)) dirs.Add(localLevelDb);

                // IndexedDB 下的 leveldb
                string indexedDir = Path.Combine(userDataPath, @"Default\IndexedDB");
                if (Directory.Exists(indexedDir))
                {
                    try
                    {
                        foreach (string d in Directory.GetDirectories(indexedDir))
                        {
                            string sub = Path.Combine(d, "indexeddb.leveldb");
                            if (Directory.Exists(sub)) dirs.Add(sub);
                        }
                    }
                    catch { }
                }

                // quark_leveldb_storage
                string quark = Path.Combine(userDataPath, @"Default\quark_leveldb_storage");
                if (Directory.Exists(quark)) dirs.Add(quark);

                Encoding latin = Encoding.GetEncoding(28591);
                // 扩大窗口到 8000，覆盖 account_info_atom 内 uId 到 nickname 的距离
                Regex rx = new Regex("\"uId\":\"(?<uid>[0-9]{6,25})\"[\\s\\S]{0,8000}?\"nickname\":\"(?<nick>(?:[^\"\\\\]|\\\\.)*)\"",
                    RegexOptions.Compiled);

                foreach (string dir in dirs)
                {
                    List<FileInfo> files;
                    try
                    {
                        files = Directory.GetFiles(dir)
                            .Select(p => new FileInfo(p))
                            .Where(f => f.Extension == ".ldb" || f.Extension == ".log")
                            .OrderBy(f => f.LastWriteTime)
                            .ToList();
                    }
                    catch { continue; }

                    foreach (FileInfo f in files)
                    {
                        byte[] bytes;
                        try { bytes = File.ReadAllBytes(f.FullName); }
                        catch { continue; }
                        string text = latin.GetString(bytes);
                        MatchCollection mc = rx.Matches(text);
                        foreach (Match m in mc)
                        {
                            string uid = m.Groups["uid"].Value;
                            string nick = UnescapeJson(m.Groups["nick"].Value);
                            if (!string.IsNullOrEmpty(nick))
                            {
                                result[uid] = nick; // 新文件覆盖旧文件
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("扫描 leveldb 身份失败: " + ex.Message);
            }
            return result;
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            string hex = s.Substring(i + 1, 4);
                            int code;
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            else sb.Append(n);
                        }
                        else sb.Append(n);
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        public static string Mask(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return string.Empty;
            if (uid.Length <= 8) return uid;
            return uid.Substring(0, 4) + "****" + uid.Substring(uid.Length - 4);
        }
    }
}
