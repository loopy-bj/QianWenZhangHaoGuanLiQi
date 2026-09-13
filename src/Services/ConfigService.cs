using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace QianwenSwitcher.Services
{
    /// <summary>config.json 的加载/保存，内置默认登录态快照清单</summary>
    public class ConfigService
    {
        private readonly PathService _paths;

        public ConfigService(PathService paths)
        {
            _paths = paths;
        }

        public static List<string> DefaultSnapshotItems()
        {
            // 相对 User Data 目录的文件或文件夹（合计约 8~10MB，不含任何缓存）
            return new List<string>
            {
                "Local State",
                @"Default\Network",
                @"Default\Local Storage",
                @"Default\Session Storage",
                @"Default\IndexedDB",
                @"Default\Sync Data",
                @"Default\quark_leveldb_storage",
                @"Default\SharedStorage",
                @"Default\WebStorage",
                @"Default\Preferences",
                @"Default\Login Data",
                @"Default\Login Data For Account",
                "qianwen_user_info"
            };
        }

        public AppConfig LoadOrCreate()
        {
            try
            {
                if (File.Exists(_paths.ConfigPath))
                {
                    string json = File.ReadAllText(_paths.ConfigPath, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ser.MaxJsonLength = 64 * 1024 * 1024;
                    AppConfig cfg = ser.Deserialize<AppConfig>(json);
                    if (cfg != null)
                    {
                        EnsureDefaults(cfg);
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("读取 config.json 失败，将重建默认配置", ex);
                try
                {
                    string bak = _paths.ConfigPath + ".bad." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Copy(_paths.ConfigPath, bak, true);
                }
                catch { }
            }

            AppConfig fresh = new AppConfig();
            fresh.QianwenExePath = PathService.GuessQianwenExe();
            fresh.UserDataPath = PathService.DefaultUserDataPath();
            fresh.SnapshotItems = DefaultSnapshotItems();
            Save(fresh);
            return fresh;
        }

        public void Save(AppConfig cfg)
        {
            EnsureDefaults(cfg);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            string json = ser.Serialize(cfg);
            string tmp = _paths.ConfigPath + ".tmp";
            File.WriteAllText(tmp, PrettyJson(json), Encoding.UTF8);
            if (File.Exists(_paths.ConfigPath))
            {
                File.Replace(tmp, _paths.ConfigPath, null);
            }
            else
            {
                File.Move(tmp, _paths.ConfigPath);
            }
        }

        private static void EnsureDefaults(AppConfig cfg)
        {
            if (string.IsNullOrEmpty(cfg.QianwenExePath))
                cfg.QianwenExePath = PathService.GuessQianwenExe();
            if (string.IsNullOrEmpty(cfg.UserDataPath))
                cfg.UserDataPath = PathService.DefaultUserDataPath();
            if (cfg.SnapshotItems == null || cfg.SnapshotItems.Count == 0)
            {
                cfg.SnapshotItems = DefaultSnapshotItems();
            }
            else
            {
                // 版本升级：把新增的默认受管项合并进老用户的配置（如 Default\Sync Data，
                // 千问把登录身份写入 Chromium 同步库，不管理它会残留上一个账号的身份）
                foreach (string d in DefaultSnapshotItems())
                {
                    string nd = NormalizeItem(d);
                    bool exists = false;
                    foreach (string raw in cfg.SnapshotItems)
                    {
                        if (string.Equals(NormalizeItem(raw), nd, StringComparison.OrdinalIgnoreCase))
                        { exists = true; break; }
                    }
                    if (!exists) cfg.SnapshotItems.Add(d);
                }
            }
            if (cfg.Accounts == null)
                cfg.Accounts = new List<AccountProfile>();
        }

        private static string NormalizeItem(string raw)
        {
            return (raw ?? string.Empty).Replace('/', '\\').Trim('\\');
        }

        /// <summary>JavaScriptSerializer 输出无缩进，这里做一个轻量格式化方便用户手工查看</summary>
        private static string PrettyJson(string json)
        {
            StringBuilder sb = new StringBuilder(json.Length + 128);
            int indent = 0;
            bool inStr = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        sb.Append(json[i + 1]);
                        i++;
                    }
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inStr = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        sb.Append(c);
                        indent++;
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case '}':
                    case ']':
                        indent--;
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
