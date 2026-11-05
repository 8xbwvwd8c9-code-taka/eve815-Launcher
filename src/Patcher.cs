using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EveLauncher
{
    /// <summary>
    /// 吞檔器：把 FtpUpdater 下載到 .update 暂存区的更新档，原子套用到 client_dir。
    ///
    /// 路徑檔案（.update/update.paths）每行格式為 "partAbsolutePath|destRelativeName"，
    /// 由 FtpUpdater.DownloadUpdates 在下载完成后写出。吞檔器逐行读取、原子替换，
    /// 任一档失败即逆序回滚。啟動器自身（LinLauncher.exe）正在运行无法原地替换，
    /// 先存成 .new，等下次啟動再套用。
    /// </summary>
    internal static class Patcher
    {
        public static bool ApplyStagedUpdates(Config cfg, string pathsFile)
        {
            if (string.IsNullOrEmpty(pathsFile) || !File.Exists(pathsFile))
            {
                Logger.Warn("吞檔器：找不到路徑檔案，無更新可套用");
                return false;
            }

            string[] lines = File.ReadAllLines(pathsFile, new UTF8Encoding(false));
            bool selfUpdated = false;
            string tmpDir = Path.GetDirectoryName(pathsFile);

            var applied = new List<string[]>();   // [target, bak]
            try
            {
                Logger.Info("吞檔器：開始套用 " + lines.Length + " 個更新檔");
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;

                    int sep = line.IndexOf('|');
                    if (sep < 0) continue;

                    string part = line.Substring(0, sep);
                    string destName = line.Substring(sep + 1);
                    if (!File.Exists(part))
                    {
                        Logger.Warn("吞檔器：暫存档缺失，跳過 " + destName);
                        continue;
                    }

                    string target = Path.Combine(cfg.ClientDir, destName);
                    bool isSelf = string.Equals(destName, FtpUpdater.SelfExeName, StringComparison.OrdinalIgnoreCase);

                    if (isSelf)
                    {
                        // 執行檔正在運行無法直接替換：先存成 .new，下次啟動套用
                        string newPath = Path.Combine(cfg.ClientDir, FtpUpdater.SelfExeName + ".new");
                        TryDelete(newPath);
                        File.Move(part, newPath);
                        selfUpdated = true;
                        Logger.Info("吞檔器：啟動器更新已存為 " + FtpUpdater.SelfExeName + ".new（下次啟動套用）");
                        TryDelete(part);
                        continue;
                    }

                    string bak = target + ".evelauncher.bak";
                    if (File.Exists(target))
                    {
                        TryDelete(bak);
                        File.Replace(part, target, bak);
                    }
                    else
                    {
                        File.Move(part, target);
                    }
                    applied.Add(new[] { target, bak });
                    Logger.Info("吞檔器：已套用 " + destName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("吞檔器套用失敗，開始回滾：" + ex.Message);
                for (int i = applied.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (File.Exists(applied[i][1]))
                            File.Replace(applied[i][1], applied[i][0], null);
                    }
                    catch (Exception rex)
                    {
                        Logger.Error("  回滾 " + applied[i][0] + " 失敗：" + rex.Message);
                    }
                }
                throw;
            }

            foreach (var a in applied) TryDelete(a[1]);
            TryDeleteDir(tmpDir);
            return selfUpdated;
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
