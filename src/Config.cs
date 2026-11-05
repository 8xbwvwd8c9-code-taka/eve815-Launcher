using System;
using System.IO;
using System.Text;

namespace EveLauncher
{
    /// <summary>
    /// EveLauncher.ini 的强类型视图。
    /// 所有相对路径一律以 exe 所在目录为基准展开（遵守 ADR：不写死绝对路径）。
    /// </summary>
    internal sealed class Config
    {
        public string BaseDir;
        public string ConfigPath;
        public string[] Args = new string[0];

        // [launcher]
        public string Profile = "eve815C";
        public string ClientDir;
        public string MasterDir;
        public string EncoderIni;
        public string LauncherExe = "LinLauncher.exe";
        public string ListUrl = "http://127.0.0.1:7000/list.txt";
        public int Port = 7000;
        public bool DeployPak = true;

        // [record]
        public uint Global1 = 0x7B726E57u;
        public uint Global2 = 0xB2E1E36Fu;

        // [update]
        public bool UpdateEnable;
        public string FtpHost = "";
        public int FtpPort = 21;
        public string FtpUser = "";
        public string FtpPassDpapi = "";
        public string RemoteDir = "/";
        public string Manifest = "update.txt";
        public int Timeout = 15;
        public int Retry = 3;
        public bool SelfUpdate = true;

        // [ui]
        public string LogFile = "EveLauncher.log";

        /// <summary>
        /// 选服窗显示策略：
        ///   always —— 总是弹出（默认，满足「要能选服务器」）
        ///   multi  —— 只有多于一台启用时才弹出
        ///   auto   —— 从不弹出，直接取第一台
        /// </summary>
        public string SelectMode = "always";

        // [override] —— 调试用，覆盖 Encoder.ini 的单个字段（留空=不覆盖）
        // 目的：不用去改 BIG5 编码的 Encoder.ini 就能做 A/B 对照
        public string OvHelper = "";
        public string OvUseBd = "";
        public string OvInject = "";
        /// <summary>注入时机（H1 验证用）：留空/suspend=挂起期注入（现行）；resume=放行并等客户端自身初始化跑完后再注入。</summary>
        public string OvInjectPhase = "";

        // [diag] 启动时抓一次客户端解包镜像（exe 加壳，磁盘指令不可用）
        public bool DumpOnLaunch;
        public int DumpDelay = 8000;

        // [patch] 客户端内存补丁
        public bool PatchEnable = true;
        public int PatchDelay = 8000;
        /// <summary>补丁地址（VA）。不同客户端 build 的取值器地址不同，必须按端配置。</summary>
        public long PatchVa = 0x6C6100;

        // [verify] 启动链文件的已知可用版本白名单（"文件名|md5前8位,文件名|md5前8位"）
        // 换客户端 build 时必须同步改这里，否则 Deployer 会拒绝启动。
        public string KnownGood = "LinLogin.bin|0ff95e8f,LauncherDll.dll|92b90f02";

        public string LogPath { get { return MakeAbsolute(LogFile); } }

        public static Config Load(string[] args)
        {
            var cfg = new Config();
            cfg.Args = args ?? new string[0];
            cfg.BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

            string explicitPath = GetArg(args, "--config");
            string path = null;
            if (!string.IsNullOrEmpty(explicitPath)) path = explicitPath;
            else if (File.Exists(Path.Combine(cfg.BaseDir, "EveLauncher.ini")))
                path = Path.Combine(cfg.BaseDir, "EveLauncher.ini");
            else if (File.Exists(Path.Combine(Environment.CurrentDirectory, "EveLauncher.ini")))
                path = Path.Combine(Environment.CurrentDirectory, "EveLauncher.ini");

            if (path == null)
                throw new FileNotFoundException(
                    "找不到 EveLauncher.ini。请放在 EveLauncher.exe 同目录，或用 --config 指定。\n查找位置：\n  " +
                    Path.Combine(cfg.BaseDir, "EveLauncher.ini") + "\n  " +
                    Path.Combine(Environment.CurrentDirectory, "EveLauncher.ini"));

            cfg.ConfigPath = Path.GetFullPath(path);
            cfg.BaseDir = Path.GetDirectoryName(cfg.ConfigPath);

            var ini = IniFile.Load(cfg.ConfigPath, new UTF8Encoding(false));

            cfg.Profile = ini.Get("launcher", "profile", cfg.Profile);
            cfg.MasterDir = cfg.Expand(ini.Get("launcher", "master_dir", "."));
            cfg.ClientDir = cfg.Expand(ini.Get("launcher", "client_dir", "."));
            cfg.EncoderIni = cfg.Expand(ini.Get("launcher", "encoder_ini", "%MASTER%\\Encoder.ini"));
            cfg.LauncherExe = ini.Get("launcher", "launcher_exe", cfg.LauncherExe);
            cfg.ListUrl = ini.Get("launcher", "list_url", cfg.ListUrl);
            cfg.Port = ini.GetInt("launcher", "port", cfg.Port);
            cfg.DeployPak = ini.GetBool("launcher", "deploy_pak", cfg.DeployPak);

            cfg.Global1 = ini.GetUInt("record", "global1", cfg.Global1);
            cfg.Global2 = ini.GetUInt("record", "global2", cfg.Global2);

            cfg.UpdateEnable = ini.GetBool("update", "enable", false);
            cfg.FtpHost = ini.Get("update", "ftp_host", "");
            cfg.FtpPort = ini.GetInt("update", "ftp_port", 21);
            cfg.FtpUser = ini.Get("update", "ftp_user", "");
            cfg.FtpPassDpapi = ini.Get("update", "ftp_pass_dpapi", "");
            cfg.RemoteDir = ini.Get("update", "remote_dir", "/");
            cfg.Manifest = ini.Get("update", "manifest", "update.txt");
            cfg.Timeout = ini.GetInt("update", "timeout", 15);
            cfg.Retry = ini.GetInt("update", "retry", 3);
            cfg.SelfUpdate = ini.GetBool("update", "self_update", true);

            cfg.LogFile = ini.Get("ui", "log", cfg.LogFile);
            cfg.SelectMode = ini.Get("ui", "select", cfg.SelectMode).Trim().ToLowerInvariant();

            cfg.OvHelper = ini.Get("override", "helper", "").Trim();
            cfg.OvUseBd = ini.Get("override", "usebd", "").Trim();
            cfg.OvInject = ini.Get("override", "inject", "").Trim();
            cfg.OvInjectPhase = ini.Get("override", "inject_phase", "").Trim().ToLowerInvariant();

            cfg.DumpOnLaunch = ini.GetBool("diag", "dump_on_launch", false);
            cfg.DumpDelay = ini.GetInt("diag", "dump_delay", 8000);

            cfg.PatchEnable = ini.GetBool("patch", "enable", true);
            cfg.PatchDelay = ini.GetInt("patch", "delay", 8000);
            cfg.PatchVa = ini.GetLongHex("patch", "va", cfg.PatchVa);

            cfg.KnownGood = ini.Get("verify", "known_good", cfg.KnownGood);

            cfg.Validate();
            return cfg;
        }

        public string Expand(string v)
        {
            if (v == null) return null;
            v = v.Replace("%~dp0", BaseDir + "\\")
                 .Replace("%BASEDIR%", BaseDir)
                 .Replace("%MASTER%", MasterDir ?? BaseDir);
            if (v.Length == 0) return BaseDir;
            return Path.IsPathRooted(v) ? v : Path.GetFullPath(Path.Combine(BaseDir, v));
        }

        public string MakeAbsolute(string relativeOrAbsolute)
        {
            return Path.IsPathRooted(relativeOrAbsolute)
                ? relativeOrAbsolute
                : Path.GetFullPath(Path.Combine(BaseDir, relativeOrAbsolute));
        }

        /// <summary>[override] 段落到选定的服务器记录上（调试用）。</summary>
        public void ApplyOverride(ServerEntry s)
        {
            int v;
            if (int.TryParse(OvHelper, out v)) { s.Helper = v; Logger.Info("[override] helper=" + v); }
            if (int.TryParse(OvUseBd, out v)) { s.UseBd = v; Logger.Info("[override] usebd=" + v); }
        }

        private void Validate()
        {
            if (Port < 1 || Port > 65535)
                throw new ArgumentException("port 不合法：" + Port);

            // launcher 内建 URL 字段上限 31 字符（笔记实测）
            if (ListUrl != null && ListUrl.Length > 31)
                throw new ArgumentException("list_url 超过 31 字符上限（launcher 字段限制）：" + ListUrl);

            if (!Directory.Exists(ClientDir))
                throw new DirectoryNotFoundException("client_dir 不存在：" + ClientDir);
            if (!Directory.Exists(MasterDir))
                throw new DirectoryNotFoundException("master_dir 不存在：" + MasterDir);
            if (!File.Exists(EncoderIni))
                throw new FileNotFoundException("encoder_ini 不存在：" + EncoderIni);
        }

        internal static string GetArg(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        internal static bool HasFlag(string[] args, string name)
        {
            if (args == null) return false;
            foreach (var a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
