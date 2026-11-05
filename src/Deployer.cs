using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EveLauncher
{
    /// <summary>
    /// 保证启动链文件就位。策略：client_dir 与 master_dir 的 md5 不一致就从 master 覆盖。
    /// 不碰 Lin.bin（两端已同 md5，且体积大、更新归 FTP 管）。
    /// </summary>
    internal static class Deployer
    {
        /// <summary>launcher 自身档名自检 + LauncherDll 看门狗都要求它叫这个名字。</summary>
        private static readonly string[] Chain =
        {
            // 不包含 LinLauncher.exe —— 那正是本程序自己（看门狗要求它叫这个名字）
            "LinLogin.bin",
            "LauncherDll.dll",
            "LinHelperZ.ini"
        };

        /// <summary>
        /// 已知可用版本白名单，来自 [verify] known_good（"文件名|md5前8位,文件名|md5前8位"）。
        /// 换客户端 build 时必须同步改配置，否则会拒绝启动，避免「用戶端版本配合錯誤」。
        /// </summary>
        private static string[] ParseKnownGood(Config cfg)
        {
            string raw = cfg.KnownGood ?? "";
            var list = new System.Collections.Generic.List<string>();
            foreach (var part in raw.Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                if (p.IndexOf('|') <= 0)
                {
                    Logger.Warn("[verify] known_good 条目格式错误（应为 文件名|md5前8位）：" + p);
                    continue;
                }
                list.Add(p);
            }
            return list.ToArray();
        }

        public static void Ensure(Config cfg)
        {
            foreach (var f in Chain) EnsureOne(cfg, f);
            if (cfg.DeployPak) EnsureOne(cfg, "change.pak");
            else Logger.Info("deploy_pak=0，跳过 change.pak（吃档文件由客户端自带）");

            VerifyKnownGood(cfg);
        }

        private static void EnsureOne(Config cfg, string name)
        {
            string dst = Path.Combine(cfg.ClientDir, name);
            string src = Path.Combine(cfg.MasterDir, name);

            if (!File.Exists(src))
            {
                if (File.Exists(dst)) { Logger.Warn("master 缺少 " + name + "，沿用客户端现有文件"); return; }
                throw new FileNotFoundException("master_dir 与 client_dir 都缺少 " + name + "：" + src);
            }

            string srcMd5 = Md5(src);
            if (File.Exists(dst))
            {
                string dstMd5 = Md5(dst);
                if (string.Equals(dstMd5, srcMd5, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info(string.Format("  {0,-18} 一致 ({1})", name, srcMd5.Substring(0, 8)));
                    return;
                }
                Logger.Warn(string.Format("  {0,-18} 不一致 客户端={1} master={2} → 覆盖",
                                          name, dstMd5.Substring(0, 8), srcMd5.Substring(0, 8)));
            }
            else
            {
                Logger.Info(string.Format("  {0,-18} 缺失 → 从 master 复制 ({1})", name, srcMd5.Substring(0, 8)));
            }

            AtomicCopy(src, dst);
        }

        private static void VerifyKnownGood(Config cfg)
        {
            string[] knownGood = ParseKnownGood(cfg);
            if (knownGood.Length == 0)
            {
                Logger.Warn("[verify] known_good 为空，跳过版本校验");
                return;
            }

            foreach (var spec in knownGood)
            {
                string[] parts = spec.Split('|');
                string name = parts[0].Trim();
                string want = parts[1].Trim();
                string path = Path.Combine(cfg.ClientDir, name);

                if (!File.Exists(path))
                    throw new FileNotFoundException("启动链缺文件：" + path);

                string got = Md5(path);
                if (!got.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        name + " 版本不符（期望 " + want + "…，实际 " + got.Substring(0, 8) + "…）。\n" +
                        "继续启动会出现「用戶端版本配合錯誤」。\n" +
                        "换客户端 build 时请同步修改 [verify] known_good。");

                Logger.Info(string.Format("  {0,-18} 版本校验通过 ({1})", name, want));
            }
        }

        private static void AtomicCopy(string src, string dst)
        {
            string tmp = dst + ".evelauncher.tmp";
            File.Copy(src, tmp, true);

            if (File.Exists(dst))
            {
                string bak = dst + ".evelauncher.bak";
                try { if (File.Exists(bak)) File.Delete(bak); } catch (Exception) { }
                File.Replace(tmp, dst, bak);
                try { File.Delete(bak); } catch (Exception) { }
            }
            else
            {
                File.Move(tmp, dst);
            }
        }

        public static string Md5(string path)
        {
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] hash = md5.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
