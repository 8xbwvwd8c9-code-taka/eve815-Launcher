using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace EveLauncher
{
    internal static class Program
    {
        private const string AppName = "EveLauncher";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (Config.HasFlag(args, "--diag"))
                {
                    ConsoleBridge.Ensure();
                    string proc = Config.GetArg(args, "--diag") ?? "Lin.bin";
                    string rawAddrs = Config.GetArg(args, "--addr");
                    string[] addrs = string.IsNullOrEmpty(rawAddrs)
                        ? null
                        : rawAddrs.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    int len = 32;
                    int.TryParse(Config.GetArg(args, "--len"), out len);
                    Diag.Run(proc, addrs, len <= 0 ? 32 : len);
                    return 0;
                }

                Config cfg = Config.Load(args);
                Logger.Init(cfg.LogPath, Config.HasFlag(args, "--verbose"));
                Logger.Info("配置 " + cfg.ConfigPath + " profile=" + cfg.Profile);
                Logger.Info("client_dir=" + cfg.ClientDir);
                Logger.Info("master_dir=" + cfg.MasterDir);
                Logger.Info("encoder_ini=" + cfg.EncoderIni);

                if (Config.HasFlag(args, "--selftest"))
                    return SelfTest(cfg, args);

                if (Config.HasFlag(args, "--gen-list"))
                    return GenList(cfg, args);

                if (Config.HasFlag(args, "--dump-share"))
                    return DumpShare(cfg);

                if (Config.HasFlag(args, "--dumpcode"))
                    return DumpCode(cfg, args);

                if (Config.HasFlag(args, "--serve-only"))
                    return ServeOnly(cfg, args);

                // 只弹选服窗，不启动客户端（调界面用）
                if (Config.HasFlag(args, "--pick-ui"))
                {
                    ConsoleBridge.Ensure();
                    var list = EncoderIni.ReadEnabled(cfg.EncoderIni);
                    ServerEntry pick = ServerSelectForm.Pick(list, 0);
                    Console.WriteLine(pick == null ? "取消" : "选中：" + pick);
                    return 0;
                }

                // 把选服窗渲染成 PNG（写文档 / 校验布局用，不受屏幕 DPI 与截屏坐标影响）
                if (Config.GetArg(args, "--ui-shot") != null)
                    return UiShot(cfg, Config.GetArg(args, "--ui-shot"));

                // 把主介面 LauncherForm 渲染成 PNG（校验布局用，不触发部署/启动链）
                if (Config.GetArg(args, "--launcher-shot") != null)
                    return LauncherShot(cfg, Config.GetArg(args, "--launcher-shot"));

                return Launch(cfg);
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
                // 配置阶段就可能失败（此时 Logger 还没 Init），额外写一份兜底错误文件
                try
                {
                    string dir = AppDomain.CurrentDomain.BaseDirectory;
                    File.AppendAllText(Path.Combine(dir, "EveLauncher.error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                        ex + Environment.NewLine + new string('-', 60) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }

                MessageBox.Show(ex.Message, AppName + " 启动失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ---- 正常启动链 ----------------------------------------------------

        private static int Launch(Config cfg)
        {
            // 先套用上一次更新留下來的「自更新檔」（.new）。成功則本程序退出、
            // 由新版執行檔接管。這裡發生在 mutex 之前，避免新版被舊 mutex 卡住。
            int self = ApplySelfUpdate(cfg);
            if (self >= 0) return self;

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\" + AppName + "." + cfg.Profile, out createdNew))
            {
                if (!createdNew)
                {
                    Logger.Warn("已有同 profile 的实例在运行，退出");
                    MessageBox.Show("已经有一个 " + cfg.Profile + " 的登入器在运行。",
                                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 2;
                }

                Logger.Info("---- 部署 ----");
                Deployer.Ensure(cfg);
                CleanupTemp(cfg);

                // 更新檢查 / 下載 / 套用全部交由主介面右下角狀態機驅動
                // （一開始顯示「檢查更新檔」→ 無更新「開始遊戲」；有更新則
                //  「下載更新中」→「下載完畢開始更新」→ 啟動吞檔器 →「開始遊戲」）。
                Logger.Info("---- 顯示主介面 ----");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LauncherForm(cfg));
                return 0;
            }
        }

        /// <summary>
        /// 啟動時套用上一次更新留下的自更新檔（&lt;SelfExeName&gt;.new）。
        /// 啟動器執行檔正在運行無法原地替換，故採用兩段式：
        ///   1) 把現行執行檔更名為 .bak（Windows 允許更名執行中的 exe）
        ///   2) 把 .new 移到現行檔名
        ///   3) 重新啟動新執行檔，本程序隨即退出
        /// .new 可能落在 client_dir（Patcher 預設落點）或與執行檔同目錄，二者都查。
        /// 回傳 ≥0 表示已重新啟動並應結束本程序；回傳 -1 表示沒有待套用更新，繼續正常流程。
        /// </summary>
        private static int ApplySelfUpdate(Config cfg)
        {
            string running;
            try { running = Process.GetCurrentProcess().MainModule.FileName; }
            catch (Exception ex) { Logger.Warn("取執行檔路徑失敗：" + ex.Message); return -1; }

            string self = FtpUpdater.SelfExeName;
            var candidates = new List<string>
            {
                Path.Combine(Path.GetDirectoryName(running), self + ".new"),
                Path.Combine(cfg.ClientDir, self + ".new")
            };

            string newPath = null;
            foreach (var c in candidates)
                if (File.Exists(c)) { newPath = c; break; }

            if (newPath == null) return -1;

            try
            {
                string bak = running + ".bak";
                TryDelete(bak);
                File.Move(running, bak);          // 更名現行執行檔
                File.Move(newPath, running);      // 新版本移到現行檔名

                Logger.Info("自更新套用完成，重新啟動：" + running);
                var psi = new ProcessStartInfo
                {
                    FileName = running,
                    Arguments = string.Join(" ", cfg.Args ?? new string[0]),
                    UseShellExecute = false
                };

                bool started = false;
                try { Process.Start(psi); started = true; }
                catch (Exception ex) { Logger.Error("重新啟動失敗：" + ex.Message); }

                if (started) return 0;           // 新版已接管，本程序退出

                // 重新啟動失敗 → 還原，避免使用者什麼都跑不起來
                Logger.Warn("自更新還原：重新啟動失敗，保留舊版本繼續");
                TryDelete(newPath);
                File.Move(running, newPath);     // 新版本退回 .new
                File.Move(bak, running);         // 舊版本復位
                return -1;
            }
            catch (Exception ex)
            {
                Logger.Error("自更新套用失敗：" + ex.Message + "（以舊版本繼續）");
                return -1;
            }
        }

        // ---- 仅起服务 ----

        private static int ServeOnly(Config cfg, string[] args)
        {
            ConsoleBridge.Ensure();
            List<string> debug;
            byte[] listBytes = ListBuilder.BuildListBytes(cfg, out debug);
            foreach (var d in debug) Console.WriteLine("  " + d);

            int hold;
            bool timed = int.TryParse(Config.GetArg(args, "--hold"), out hold) && hold > 0;

            using (var server = ListServer.Start(cfg.Port, listBytes))
            {
                Console.WriteLine("list 服务已就绪：" + cfg.ListUrl + "（" + listBytes.Length + " 字节）");
                if (timed)
                {
                    Console.WriteLine("保持 " + hold + " 秒…");
                    Thread.Sleep(hold * 1000);
                }
                else
                {
                    Console.WriteLine("按 Enter 停止…");
                    try { Console.ReadLine(); } catch (Exception) { }
                }
                server.Stop();
                Console.WriteLine("已停止，端口 " + cfg.Port + " 释放");
            }
            return 0;
        }

        // ---- 只生成 list.txt -----------------------------------------------

        private static int GenList(Config cfg, string[] args)
        {
            ConsoleBridge.Ensure();
            string outPath = Config.GetArg(args, "--gen-list");
            if (string.IsNullOrEmpty(outPath)) outPath = Path.Combine(cfg.ClientDir, "list.txt");

            List<string> debug;
            byte[] tpl = null;
            string tplPath = Config.GetArg(args, "--template");
            if (!string.IsNullOrEmpty(tplPath))
            {
                tpl = File.ReadAllBytes(tplPath);
                Console.WriteLine("模板：" + tplPath + "（" + tpl.Length + " 字节）");
            }

            byte[] bytes = ListBuilder.BuildListBytes(cfg, tpl, out debug);
            File.WriteAllBytes(outPath, bytes);

            Console.WriteLine("已生成 " + outPath + "（" + bytes.Length + " 字节）");
            foreach (var d in debug) Console.WriteLine("  " + d);
            return 0;
        }

        // ---- 选服窗渲染成 PNG ----------------------------------------------

        private static int UiShot(Config cfg, string outPath)
        {
            ConsoleBridge.Ensure();
            var list = EncoderIni.ReadEnabled(cfg.EncoderIni);
            using (var f = new ServerSelectForm(list, 0))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-6000, -6000);   // 屏幕外，避免闪窗
                f.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                Application.DoEvents();

                using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine(string.Format("已渲染 {0}（{1}×{2}，{3} 台伺服器）",
                        outPath, bmp.Width, bmp.Height, list.Count));
                }
                f.Close();
            }
            return 0;
        }

        /// <summary>--launcher-shot &lt;out&gt;：离屏渲染主介面为 PNG（仅构造窗体，不走 Deployer/启动链）。</summary>
        private static int LauncherShot(Config cfg, string outPath)
        {
            ConsoleBridge.Ensure();
            using (var f = new LauncherForm(cfg))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-6000, -6000);   // 屏幕外，避免闪窗
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);                     // 让连通性探测先跑完（离线会翻红）
                Application.DoEvents();

                using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine(string.Format("已渲染主介面 {0}（{1}×{2}，{3} 台伺服器）",
                        outPath, bmp.Width, bmp.Height, f.ServerCountForShot));
                }
                f.Close();
            }
            return 0;
        }

        // ---- 共享内存块自检（T-boot） --------------------------------------

        private static int DumpShare(Config cfg)
        {
            ConsoleBridge.Ensure();
            var servers = EncoderIni.ReadEnabled(cfg.EncoderIni);
            foreach (var s in servers)
            {
                byte[] rec = ListBuilder.BuildRecord(s, cfg.Global1, cfg.Global2);
                byte[] share = BootChain.BuildShareBlock(rec);

                Console.WriteLine("服务器：" + s);
                Console.WriteLine("  记录 : " + ListBuilder.Describe(rec));
                Console.WriteLine("  共享内存关键字段：");
                Console.WriteLine("    +0x00 host    = '" +
                    Encoding.ASCII.GetString(share, 0x00, 0x20).Split('\0')[0] + "'");
                Console.WriteLine(string.Format("    +0x20 port    = {0}", BitConverter.ToInt32(share, 0x20)));
                Console.WriteLine(string.Format("    +0x24        = 0x{0:x2}", share[0x24]));
                Console.WriteLine(string.Format("    +0x25 helper = 0x{0:x2}", share[0x25]));
                Console.WriteLine("    +0x26 backup  = " + BitConverter.ToString(share, 0x26, 0x10));
                Console.WriteLine(string.Format("    +0x36 encrypt= 0x{0:x2}", share[0x36]));
                Console.WriteLine(string.Format("    +0x37        = 0x{0:x2}", share[0x37]));
                Console.WriteLine("    +0x38 bdfile  = '" +
                    Encoding.Unicode.GetString(share, 0x38, 0x20).Split('\0')[0] + "'");
                Console.WriteLine(string.Format("    +0x78        = 0x{0:x2}", share[0x78]));
                Console.WriteLine(string.Format("    +0x79 randkey= 0x{0:x2}", share[0x79]));
                Console.WriteLine(string.Format("    +0x7C global2= 0x{0:X8}", BitConverter.ToUInt32(share, 0x7C)));
                Console.WriteLine(string.Format("    +0x80 global1= 0x{0:X8}", BitConverter.ToUInt32(share, 0x80)));
            }
            Console.WriteLine("位数：" + (IntPtr.Size * 8) + " 位（必须 32）");
            return 0;
        }

        /// <summary>
        /// --dumpcode &lt;out&gt; [--from 0xRVA] [--size 0xN] [--delay ms]
        /// 客户端是加壳的，磁盘指令是压缩的；只有运行时内存里才是真指令。
        /// 这个模式完整走一遍启动链，放行后等一会儿把客户端解包后的镜像抓出来。
        /// </summary>
        private static int DumpCode(Config cfg, string[] args)
        {
            ConsoleBridge.Ensure();
            string outPath = Config.GetArg(args, "--dumpcode");
            if (string.IsNullOrEmpty(outPath))
                throw new ArgumentException("--dumpcode 需要一个输出文件路径");

            long rva = ParseHex(Config.GetArg(args, "--from"), 0x1000L);
            int size = (int)ParseHex(Config.GetArg(args, "--size"), 0x600000L);
            int delay = (int)ParseHex(Config.GetArg(args, "--delay"), 6000L);

            Console.WriteLine(string.Format("转储 RVA=0x{0:X} 长度=0x{1:X} 延迟={2}ms → {3}", rva, size, delay, outPath));
            cfg.SelectMode = "auto";   // 诊断模式不弹选服窗
            return BootChain.RunDump(cfg, outPath, rva, size, delay);
        }

        private static long ParseHex(string s, long fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim();
            long v;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out v) ? v : fallback;
            return long.TryParse(s, out v) ? v : fallback;
        }

        // ---- T1 自测 -------------------------------------------------------

        private static int SelfTest(Config cfg, string[] args)
        {
            ConsoleBridge.Ensure();
            int fail = 0;

            // 1) 构造记录 vs 黄金样本
            string golden = Config.GetArg(args, "--golden");
            if (!string.IsNullOrEmpty(golden))
            {
                var probe = new ServerEntry
                {
                    Slot = 1,
                    Name = Config.GetArg(args, "--name") ?? "黑皮天堂",
                    Ip = Config.GetArg(args, "--ip") ?? "127.0.0.1",
                    Port = int.Parse(Config.GetArg(args, "--port") ?? "2001"),
                    UseBd = 1,
                    BdFile = "change.pak",
                    Helper = 1,
                    RandKey = 0
                };
                byte[] built = ListBuilder.BuildRecord(probe, cfg.Global1, cfg.Global2);
                byte[] gold = File.ReadAllBytes(golden);

                bool ok = built.Length == gold.Length;
                var diffs = new List<int>();
                if (ok)
                    for (int i = 0; i < gold.Length; i++)
                        if (built[i] != gold[i]) diffs.Add(i);

                Console.WriteLine("[T1a] 记录逐字节比对");
                if (ok && diffs.Count == 0)
                {
                    Console.WriteLine("      PASS  (" + built.Length + " 字节，零差异)");
                }
                else
                {
                    fail++;
                    Console.WriteLine("      FAIL  差异 " + diffs.Count + " 处：" + Join(diffs));
                    for (int i = 0; i < Math.Min(diffs.Count, 12); i++)
                    {
                        int o = diffs[i];
                        Console.WriteLine(string.Format("        0x{0:X3} 生成={1:x2} 黄金={2:x2}", o, built[o], gold[o]));
                    }
                }
            }

            // 2) 加密往返 + 与参考 list.txt 比对
            string refList = Config.GetArg(args, "--ref");
            if (!string.IsNullOrEmpty(refList))
            {
                string text = File.ReadAllText(refList, Encoding.Default);
                var ini = IniFile.Parse(text);
                string b64 = ini.Get("list", "ServerData0", null);

                Console.WriteLine("[T1b] 加密往返 vs " + Path.GetFileName(refList));
                if (string.IsNullOrEmpty(b64))
                {
                    fail++;
                    Console.WriteLine("      FAIL  参考文件里没有 ServerData0");
                }
                else
                {
                    byte[] rec = ListBuilder.Decrypt(b64);
                    string again = ListBuilder.Encrypt(rec);
                    bool same = string.Equals(again, b64, StringComparison.Ordinal);
                    Console.WriteLine("      " + (same ? "PASS" : "FAIL") + "  长度 " + b64.Length + " / " + again.Length);
                    if (!same) fail++;
                    Console.WriteLine("      记录内容：" + ListBuilder.Describe(rec));
                }
            }

            // 3) 端到端：Encoder.ini → 记录
            Console.WriteLine("[T1c] Encoder.ini → 记录");
            try
            {
                var servers = EncoderIni.ReadEnabled(cfg.EncoderIni);
                foreach (var s in servers)
                {
                    byte[] rec = ListBuilder.BuildRecord(s, cfg.Global1, cfg.Global2);
                    string b64 = ListBuilder.Encrypt(rec);
                    Console.WriteLine("      " + s);
                    Console.WriteLine("      → " + ListBuilder.Describe(rec));
                    Console.WriteLine("      → base64 " + b64.Length + " 字符");
                    if (b64.Length != 284) { fail++; Console.WriteLine("      FAIL base64 长度应为 284"); }
                    if (ListBuilder.Encrypt(ListBuilder.Decrypt(b64)) != b64)
                    { fail++; Console.WriteLine("      FAIL 往返不一致"); }
                }
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine("      FAIL  " + ex.Message);
            }

            Console.WriteLine(fail == 0 ? "== 全部通过 ==" : "== 失败 " + fail + " 项 ==");
            return fail == 0 ? 0 : 3;
        }

        private static string Join(List<int> xs)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(xs.Count, 40); i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("0x").Append(xs[i].ToString("X3"));
            }
            if (xs.Count > 40) sb.Append("…");
            return sb.ToString();
        }

        // ---- 工具 ----------------------------------------------------------

        private static void AtomicWrite(string path, byte[] bytes)
        {
            string tmp = path + ".evelauncher.tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        /// <summary>
        /// 诊断：原版 launcher 下载成功时会把清单写到 %TEMP%\list_000.txt（该文件不会被删）。
        /// 用它判断「下载到底有没有发生」，比看界面可靠。
        /// </summary>
        private static void ReportLauncherTemp()
        {
            try
            {
                string temp = Path.GetTempPath();
                var files = Directory.GetFiles(temp, "list_*.txt");
                if (files.Length == 0)
                {
                    Logger.Warn("  %TEMP% 下没有 list_*.txt —— 下载分支从未成功执行");
                    return;
                }
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    Logger.Info(string.Format("  {0}  {1} 字节  修改于 {2:HH:mm:ss}",
                                              fi.Name, fi.Length, fi.LastWriteTime));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("  检查 %TEMP% 失败：" + ex.Message);
            }
        }

        private static void CleanupTemp(Config cfg)
        {
            try
            {
                string dir = Path.Combine(cfg.ClientDir, ".update");
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch (Exception) { }
        }
    }

    /// <summary>WinExe 没有控制台，命令行模式下借父进程控制台输出。</summary>
    internal static class ConsoleBridge
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        private static bool _done;

        public static void Ensure()
        {
            if (_done) return;
            _done = true;
            if (!AttachConsole(-1)) AllocConsole();
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            stdout.AutoFlush = true;
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
            stderr.AutoFlush = true;
            Console.SetError(stderr);
        }
    }
}
