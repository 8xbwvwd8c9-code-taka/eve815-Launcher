using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EveLauncher
{
    /// <summary>
    /// 诊断模式（支撑 T8）：附着到已运行的客户端进程，按模块基址 + 绝对地址读内存，
    /// 用来确认 LauncherDll 的补丁点（E9 = jmp）与共享内存记录是否落地。
    /// 地址通过命令行传入，不写死，方便换 build 时不改代码。
    /// </summary>
    internal static class Diag
    {
        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        public static void Run(string processName, string[] addrs, int length)
        {
            Process[] procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0)
            {
                Console.WriteLine("找不到进程：" + processName);
                return;
            }

            var p = procs[0];
            Console.WriteLine("进程 " + p.ProcessName + " pid=" + p.Id);

            IntPtr baseAddr = IntPtr.Zero;
            foreach (ProcessModule m in p.Modules)
            {
                Console.WriteLine(string.Format("  模块 {0,-20} base=0x{1:X8} size=0x{2:X8}",
                                                m.ModuleName, m.BaseAddress.ToInt64(), m.ModuleMemorySize));
                if (m.ModuleName.Equals(processName + ".bin", StringComparison.OrdinalIgnoreCase) ||
                    m.ModuleName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    baseAddr = m.BaseAddress;
            }

            if (baseAddr == IntPtr.Zero && p.MainModule != null)
                baseAddr = p.MainModule.BaseAddress;

            IntPtr h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess 失败，错误码 " + Marshal.GetLastWin32Error() + "（需要同权限运行）");
                return;
            }

            try
            {
                if (addrs != null)
                {
                    foreach (var a in addrs)
                    {
                        long va = ParseAddr(a);
                        Dump(h, new IntPtr(va), length, "绝对地址 0x" + va.ToString("X8"));
                    }
                }

                // 约定：进程基址起 0x84 字节的共享记录镜像（T8 用）
                Dump(h, baseAddr, Math.Max(length, 0x84), "模块基址 0x" + baseAddr.ToInt64().ToString("X8"));
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static long ParseAddr(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber);
            long v;
            return long.TryParse(s, out v) ? v : 0;
        }

        private static void Dump(IntPtr h, IntPtr addr, int length, string title)
        {
            var buf = new byte[length];
            IntPtr read;
            if (!ReadProcessMemory(h, addr, buf, length, out read))
            {
                Console.WriteLine(string.Format("  {0}: 读取失败 (err={1})", title, Marshal.GetLastWin32Error()));
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0 && i % 16 == 0) sb.Append(Environment.NewLine).Append("      ");
                sb.Append(buf[i].ToString("x2")).Append(' ');
            }
            Console.WriteLine("  " + title + ":");
            Console.WriteLine("      " + sb);

            if (buf[0] == 0xE9)
                Console.WriteLine("      → 首字节 E9，jmp 已落地");
            else
                Console.WriteLine("      → 首字节 0x" + buf[0].ToString("x2") + "，非 jmp");
        }
    }
}
