using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EveLauncher
{
    /// <summary>
    /// FTP 差异更新器：拉 update.txt → md5 比对 → 断点续传 → 校验 → 原子替换。
    /// 设计原则：任一文件校验失败绝不落地；整体失败逆序回滚；失败必须回显，不静默。
    /// </summary>
    internal static class FtpUpdater
    {
        private const string TempDirName = ".update";

        /// <summary>目前執行檔的檔名（用於自更新判斷；實際部署為 LinLauncher.exe）。</summary>
        internal static string SelfExeName
        {
            get
            {
                try { return Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName); }
                catch { return "LinLauncher.exe"; }
            }
        }

        internal sealed class Entry
        {
            public string File;
            public string Md5;
        }

        /// <summary>返回 true 表示有新版本且已应用（可能需要重启自身）。</summary>
        public static bool Sync(Config cfg)
        {
            List<Entry> pending = CheckForUpdates(cfg);
            if (pending.Count == 0) return false;
            string pathFile = DownloadUpdates(cfg, pending, null);
            return Patcher.ApplyStagedUpdates(cfg, pathFile);
        }

        /// <summary>
        /// 只比對遠端清單與本地 md5，返回需要更新的檔案清單（不含已一致的）。
        /// update 關閉或沒配 FTP 時回傳空清單。不會下載任何檔案。
        /// </summary>
        public static List<Entry> CheckForUpdates(Config cfg)
        {
            var todo = new List<Entry>();
            if (!cfg.UpdateEnable)
            {
                Logger.Info("update.enable=0，跳过更新檢查");
                return todo;
            }
            if (string.IsNullOrEmpty(cfg.FtpHost))
            {
                Logger.Warn("update.enable=1 但 ftp_host 为空，跳过更新檢查");
                return todo;
            }

            string pass = ResolvePassword(cfg);
            string baseUrl = "ftp://" + cfg.FtpHost + (cfg.FtpPort != 21 ? ":" + cfg.FtpPort : "") +
                             NormalizeDir(cfg.RemoteDir);

            string manifestPath = Path.Combine(Path.GetTempPath(), "EveLauncher.manifest.tmp");
            try
            {
                FtpDownload(baseUrl + cfg.Manifest, cfg, pass, manifestPath, 0);
                var list = ParseManifest(File.ReadAllText(manifestPath, Encoding.Default));
                Logger.Info("远端清单 " + cfg.Manifest + " 共 " + list.Count + " 项");

                foreach (var e in list)
                {
                    if (string.Equals(e.File, SelfExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (cfg.SelfUpdate) todo.Add(e);
                        continue;
                    }
                    string local = Path.Combine(cfg.ClientDir, e.File);
                    string localMd5 = File.Exists(local) ? Deployer.Md5(local) : "";
                    if (!string.Equals(localMd5, e.Md5, StringComparison.OrdinalIgnoreCase))
                        todo.Add(e);
                }
            }
            finally
            {
                TryDelete(manifestPath);
            }

            Logger.Info("需更新 " + todo.Count + " 个文件：" +
                        string.Join(", ", todo.ConvertAll(x => x.File).ToArray()));
            return todo;
        }

        /// <summary>
        /// 下載待更新檔到 .update 暂存区並逐個校驗 md5，最後寫出「路徑檔案」
        /// （.update/update.paths，每行 "partAbsolutePath|destRelativeName"）。
        /// 回傳路徑檔案路徑，供吞檔器（Patcher）套用。任一檔失敗即丟棄暂存档並拋出。
        /// </summary>
        public static string DownloadUpdates(Config cfg, List<Entry> entries, Action<string, int> report)
        {
            if (entries == null || entries.Count == 0) return null;

            string pass = ResolvePassword(cfg);
            string baseUrl = "ftp://" + cfg.FtpHost + (cfg.FtpPort != 21 ? ":" + cfg.FtpPort : "") +
                             NormalizeDir(cfg.RemoteDir);
            string tmpDir = Path.Combine(cfg.ClientDir, TempDirName);
            Directory.CreateDirectory(tmpDir);

            var staged = new List<string>();
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (report != null)
                        report("下載中 " + e.File + " (" + (i + 1) + "/" + entries.Count + ")",
                               (i * 100) / entries.Count);

                    string part = Path.Combine(tmpDir, e.File + ".part");
                    TryDelete(part);
                    FtpDownloadWithRetry(baseUrl + e.File, cfg, pass, part, e);

                    string got = Deployer.Md5(part);
                    if (!string.Equals(got, e.Md5, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            e.File + " 校验失败：期望 " + e.Md5 + "，实际 " + got + "（已丢弃，不落地）");

                    staged.Add(part);
                    Logger.Info("  已下載 " + e.File);
                }

                // 寫出路徑檔案：把更新放在路徑檔案中，吞檔器據此套用
                string pathsFile = Path.Combine(tmpDir, "update.paths");
                var sb = new StringBuilder();
                for (int i = 0; i < entries.Count; i++)
                    sb.AppendLine(staged[i] + "|" + entries[i].File);
                File.WriteAllText(pathsFile, sb.ToString(), new UTF8Encoding(false));

                if (report != null) report("下載完成，等待套用", 100);
                return pathsFile;
            }
            catch (Exception)
            {
                foreach (var p in staged) TryDelete(p);
                throw;
            }
        }

        // ---- 清单解析 ------------------------------------------------------

        private static List<Entry> ParseManifest(string text)
        {
            var ini = IniFile.Parse(text);
            int count = ini.GetInt("main", "count", -1);
            var list = new List<Entry>();

            if (count < 0)
            {
                for (int i = 0; ; i++)
                {
                    string f = ini.Get("update", "file_" + i, null);
                    if (string.IsNullOrEmpty(f)) break;
                    list.Add(new Entry { File = f, Md5 = ini.Get("update", "md5_" + i, "") });
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    string f = ini.Get("update", "file_" + i, null);
                    if (string.IsNullOrEmpty(f)) continue;
                    list.Add(new Entry { File = f, Md5 = ini.Get("update", "md5_" + i, "") });
                }
            }

            foreach (var e in list)
                if (string.IsNullOrEmpty(e.Md5) || e.Md5.Length != 32)
                    throw new InvalidDataException("清单项 md5 缺失或格式不对：" + e.File);

            return list;
        }

        // ---- FTP -----------------------------------------------------------

        private static void FtpDownloadWithRetry(string url, Config cfg, string pass, string dest, Entry e)
        {
            Exception last = null;
            for (int attempt = 0; attempt <= cfg.Retry; attempt++)
            {
                try
                {
                    long offset = File.Exists(dest) ? new FileInfo(dest).Length : 0;
                    FtpDownload(url, cfg, pass, dest, offset);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Logger.Warn(string.Format("下载 {0} 第 {1} 次失败：{2}", e.File, attempt + 1, ex.Message));
                    if (attempt < cfg.Retry)
                        Thread.Sleep(1000 * (1 << Math.Min(attempt, 4)));
                }
            }
            throw new IOException("下载失败（已重试 " + cfg.Retry + " 次）：" + e.File, last);
        }

        private static void FtpDownload(string url, Config cfg, string pass, string dest, long offset)
        {
            var req = (FtpWebRequest)WebRequest.Create(url);
            req.Method = WebRequestMethods.Ftp.DownloadFile;
            req.Credentials = new NetworkCredential(cfg.FtpUser, pass);
            req.UseBinary = true;
            req.UsePassive = true;
            req.Timeout = cfg.Timeout * 1000;
            req.ReadWriteTimeout = cfg.Timeout * 1000;
            req.KeepAlive = false;
            if (offset > 0) req.ContentOffset = offset;

            using (var resp = (FtpWebResponse)req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var fs = new FileStream(dest, offset > 0 ? FileMode.Append : FileMode.Create,
                                           FileAccess.Write, FileShare.None))
            {
                if (rs == null) throw new IOException("FTP 响应流为空：" + url);
                var buf = new byte[64 * 1024];
                int n;
                while ((n = rs.Read(buf, 0, buf.Length)) > 0)
                    fs.Write(buf, 0, n);
            }
        }

        // ---- 凭据保护 ----

        private static string ResolvePassword(Config cfg)
        {
            if (!string.IsNullOrEmpty(cfg.FtpPassDpapi))
            {
                try
                {
                    byte[] raw = ProtectedData.Unprotect(
                        Convert.FromBase64String(cfg.FtpPassDpapi), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(raw);
                }
                catch (Exception ex)
                {
                    Logger.Warn("FTP 密码解不开（换机器或换用户？），将要求重新输入：" + ex.Message);
                }
            }

            string typed = PasswordPrompt.Ask("FTP 密码 — " + cfg.FtpHost + " / " + cfg.FtpUser);
            if (typed == null) throw new OperationCanceledException("用户取消输入 FTP 密码");

            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(typed), null,
                                                   DataProtectionScope.CurrentUser);
                AppendPassword(cfg, Convert.ToBase64String(enc));
            }
            catch (Exception ex)
            {
                Logger.Warn("FTP 密码无法写回配置（本次仅内存使用）：" + ex.Message);
            }
            return typed;
        }

        private static void AppendPassword(Config cfg, string b64)
        {
            string[] lines = File.ReadAllLines(cfg.ConfigPath, new UTF8Encoding(false));
            bool replaced = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("ftp_pass_dpapi", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "ftp_pass_dpapi=" + b64;
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
            {
                var list = new List<string>(lines);
                int at = list.FindIndex(x => x.Trim().StartsWith("[update]", StringComparison.OrdinalIgnoreCase));
                if (at < 0) list.Add("[update]");
                else at = at + 1;
                if (at < 0 || at > list.Count) at = list.Count;
                list.Insert(at, "ftp_pass_dpapi=" + b64);
                lines = list.ToArray();
            }
            File.WriteAllLines(cfg.ConfigPath, lines, new UTF8Encoding(false));
            Logger.Info("FTP 密码已用 DPAPI 加密写回配置");
        }

        // ---- 工具 ----------------------------------------------------------

        private static string NormalizeDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "/";
            if (!dir.StartsWith("/")) dir = "/" + dir;
            if (!dir.EndsWith("/")) dir += "/";
            return dir;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
