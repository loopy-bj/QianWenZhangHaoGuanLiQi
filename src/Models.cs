using System;
using System.Collections.Generic;

namespace QianwenSwitcher
{
    /// <summary>配置文件 config.json 数据模型</summary>
    public class AppConfig
    {
        public string QianwenExePath { get; set; }
        public string UserDataPath { get; set; }
        public bool RelaunchAfterOperation { get; set; }
        public bool AutoStart { get; set; }
        /// <summary>完整模式：备份整个 Default（排除缓存），作为最小清单失效时的兜底</summary>
        public bool FullMode { get; set; }
        public List<string> SnapshotItems { get; set; }
        public List<AccountProfile> Accounts { get; set; }

        public AppConfig()
        {
            QianwenExePath = string.Empty;
            UserDataPath = string.Empty;
            RelaunchAfterOperation = true;
            AutoStart = true;
            FullMode = false;
            SnapshotItems = new List<string>();
            Accounts = new List<AccountProfile>();
        }
    }

    /// <summary>一个已保存的千问账号快照</summary>
    public class AccountProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Uid { get; set; }
        public string Nickname { get; set; }
        /// <summary>zip 文件名（位于 profiles 目录下）</summary>
        public string ZipFile { get; set; }
        public string CreatedAt { get; set; }
        public string UpdatedAt { get; set; }

        public AccountProfile()
        {
            Id = string.Empty;
            Name = string.Empty;
            Uid = string.Empty;
            Nickname = string.Empty;
            ZipFile = string.Empty;
            CreatedAt = string.Empty;
            UpdatedAt = string.Empty;
        }
    }

    /// <summary>当前千问登录身份识别结果</summary>
    public class Identity
    {
        public bool LoggedIn;
        public string Uid;
        public string Nickname;

        public Identity()
        {
            LoggedIn = false;
            Uid = string.Empty;
            Nickname = string.Empty;
        }
    }

    /// <summary>后台操作进度，用于 UI 线程更新</summary>
    public class OperationProgress
    {
        public int Percent;
        public string Message;

        public OperationProgress(int percent, string message)
        {
            Percent = percent;
            Message = message;
        }
    }

    /// <summary>切换操作结果</summary>
    public class SwitchResult
    {
        public bool Success;
        public bool SameAccount;
        public bool RolledBack;
        /// <summary>还原并重启后，是否确认千问以目标账号真正登录（检测到真实 user_info blob）</summary>
        public bool LoginVerified;
        public string Message;
        public Identity CurrentIdentity;

        public SwitchResult()
        {
            Success = false;
            SameAccount = false;
            RolledBack = false;
            LoginVerified = false;
            Message = string.Empty;
            CurrentIdentity = new Identity();
        }
    }
}
