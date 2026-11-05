using System;
using System.Diagnostics;
using System.IO;

namespace EveLauncher
{
    /// <summary>
    /// 启动原版 LinLauncher.exe，把注入/共享内存/吃档全部交给它。
    /// 我们只保证：cwd 正确、档名正确、list.txt 已经在 127.0.0.1:port 上待命。
    /// </summary>
    internal static class GameLauncher
    {
        public static int Run(Config cfg)
        {
            string exe = Path.Combine(cfg.ClientDir, cfg.LauncherExe);

            if (!File.Exists(exe))
                throw new FileNotFoundException("找不到启动目标：" + exe);

            // launcher 有档名自检（0x4b13d4），档名不符会弹「登入器檔名已變更!!」后退出
            if (!string.Equals(Path.GetFileName(exe), "LinLauncher.exe", StringComparison.OrdinalIgnoreCase))
                Logger.Warn("启动目标不是 LinLauncher.exe，可能触发 launcher 档名自检：" + exe);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = cfg.ClientDir,   // launcher 用相对路径找 LinLogin.bin / list.txt
                UseShellExecute = false
            };

            Logger.Info("启动 " + exe + "（cwd=" + cfg.ClientDir + "）");

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5 || ex.NativeErrorCode == 740)
            {
                // 740 = ERROR_ELEVATION_REQUIRED。本进程没提权时，交给 shell 触发 UAC。
                Logger.Warn("直接启动被拒（需要提升权限），改用 ShellExecute 触发 UAC");
                psi.UseShellExecute = true;
                p = Process.Start(psi);
            }

            using (p)
            {
                if (p == null) throw new InvalidOperationException("Process.Start 返回 null：" + exe);
                p.WaitForExit();
                Logger.Info("launcher 退出，ExitCode=" + p.ExitCode);
                return p.ExitCode;
            }
        }
    }
}
