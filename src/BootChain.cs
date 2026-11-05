using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace EveLauncher
{
    /// <summary>
    /// 自写启动链 —— 完全复刻原版 LinLauncher 的 FUN_004b5a90：
    ///   1. CreateProcessW(LinLogin.bin, NULL, CREATE_SUSPENDED|CREATE_NO_WINDOW, cwd=程序目录)
    ///   2. CreateFileMappingW("{C109539E-0803-40BC-996C-AC1F97442984}_<pid>", 0x2000)
    ///      写入共享内存（字段映射见 BuildShareBlock，逐条对应反编译）
    ///   3. 注入 LauncherDll.dll（OpenProcess + VirtualAllocEx + WriteProcessMemory + CreateRemoteThread）
    ///   4. ResumeThread，并常驻直到客户端退出
    /// 不再需要 list.txt / hfs.exe / 本机 HTTP。
    /// </summary>
    internal static class BootChain
    {
        // ---- Win32 ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes,
            uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, uint dwNumberOfBytesToMap);

        [DllImport("kernel32.dll")]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize,
            uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, uint nSize, IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
            uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
            UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        [DllImport("kernel32.dll")]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public uint cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public ushort wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint dwProcessId, dwThreadId;
        }

        private const uint CREATE_SUSPENDED = 0x4;
        private const uint CREATE_NO_WINDOW = 0x400;
        private const uint PAGE_READWRITE = 0x4;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint FILE_MAP_ALL_ACCESS = 0xF001F;
        private const uint PROCESS_ALL_ACCESS = 0x1FFFFF;
        private const uint INFINITE = 0xFFFFFFFF;

        private const string ShareNameFormat = "{C109539E-0803-40BC-996C-AC1F97442984}_";
        private const int ShareSize = 0x2000;

        // ---- 主流程 ----

        public static int Run(Config cfg)
        {
            return RunCore(cfg, null, null);
        }

        /// <summary>直接用指定的伺服器進入啟動鏈（主介面已選好，不再彈選服窗）。</summary>
        public static int Run(Config cfg, ServerEntry preselected)
        {
            return RunCore(cfg, preselected, null);
        }

        /// <summary>
        /// 诊断：完整走一遍启动链，放行后等 delayMs，从客户端进程读 [imageBase+rva, +size)
        /// 写入 outPath，然后结束客户端。用于抓加壳后的运行时指令（磁盘上是压缩的）。
        /// </summary>
        public static int RunDump(Config cfg, string outPath, long rva, int size, int delayMs)
        {
            return RunCore(cfg, null, (pid, hProc) =>
            {
                Logger.Info(string.Format("等待 {0}ms 后转储客户端内存 RVA=0x{1:X} size=0x{2:X}", delayMs, rva, size));
                Thread.Sleep(delayMs);
                DumpImage(hProc, outPath, rva, size);
            });
        }

        /// <summary>
        /// 从客户端进程读 [0x400000+rva, +size) 写入文件。按 64KB 分块，不可读的页留 0，
        /// 这样即使部分页未提交也能拿到整段解包后的镜像。
        /// </summary>
        internal static void DumpImage(IntPtr hProc, string outPath, long rva, int size)
        {
            var buf = new byte[size];
            const int Chunk = 0x10000;
            var tmp = new byte[Chunk];
            int okChunks = 0, badChunks = 0;
            IntPtr got;

            for (int off = 0; off < size; off += Chunk)
            {
                int len = Math.Min(Chunk, size - off);
                IntPtr addr = new IntPtr(0x400000L + rva + off);
                if (ReadProcessMemory(hProc, addr, tmp, len, out got))
                {
                    Array.Copy(tmp, 0, buf, off, len);
                    okChunks++;
                }
                else
                {
                    badChunks++;
                }
            }

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllBytes(outPath, buf);
            Logger.Info(string.Format("已转储 → {0}（可读块 {1}，不可读块 {2}）", outPath, okChunks, badChunks));
        }

        private static int RunCore(Config cfg, ServerEntry preselected, Action<int, IntPtr> afterResume)
        {
            if (IntPtr.Size != 4)
                throw new InvalidOperationException(
                    "本程序必须为 32 位（x86）。当前为 " + (IntPtr.Size * 8) + " 位，" +
                    "无法向 32 位的 LinLogin.bin 注入 LauncherDll.dll（WOW64 隔离）。");

            string clientDir = cfg.ClientDir;
            string clientExe = Path.Combine(clientDir, "LinLogin.bin");
            string dllPath = Path.Combine(clientDir, "LauncherDll.dll");

            if (!File.Exists(clientExe)) throw new FileNotFoundException("找不到客户端：" + clientExe);
            if (!File.Exists(dllPath)) throw new FileNotFoundException("找不到 LauncherDll.dll：" + dllPath);

            List<ServerEntry> servers = EncoderIni.ReadEnabled(cfg.EncoderIni);
            if (servers.Count == 0)
            {
                Logger.Warn("Encoder.ini 里没有任何 server_enableN=1 的服务器");
                MessageBox.Show("Encoder.ini 里没有启用的服务器。\n请把至少一个 server_enableN 改成 1。",
                                "沒有服務器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }

            // 选服策略：always 总是弹窗（默认）；multi 多台才弹；auto 从不弹
            bool ask = cfg.SelectMode == "always"
                       || (cfg.SelectMode == "multi" && servers.Count > 1);
            if (Config.HasFlag(cfg.Args, "--no-select")) ask = false;
            if (Config.HasFlag(cfg.Args, "--select")) ask = true;

            // 主介面（LauncherForm）已選好伺服器 → 直接用 preselected，不再彈選服窗
            ServerEntry pick = preselected ?? (ask ? PickServer(servers) : servers[0]);
            if (pick == null) { Logger.Info("用户取消选服"); return 0; }

            cfg.ApplyOverride(pick);

            Logger.Info("选定服务器：" + pick);
            byte[] record = ListBuilder.BuildRecord(pick, cfg.Global1, cfg.Global2);
            Logger.Info("记录：" + ListBuilder.Describe(record));

            byte[] share = BuildShareBlock(record);

            // 1. 挂起启动客户端（复刻 FUN_004b5a90：0x404 = CREATE_SUSPENDED|CREATE_NO_WINDOW）
            var si = new STARTUPINFO();
            si.cb = (uint)Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            if (!CreateProcessW(clientExe, null, IntPtr.Zero, IntPtr.Zero, false,
                                CREATE_SUSPENDED | CREATE_NO_WINDOW, IntPtr.Zero, clientDir, ref si, out pi))
                throw new InvalidOperationException(
                    "CreateProcess 失败（err=" + Marshal.GetLastWin32Error() + "）：" + clientExe);

            Logger.Info("客户端已挂起启动，PID=" + pi.dwProcessId);

            IntPtr hMap = IntPtr.Zero, pView = IntPtr.Zero;
            try
            {
                // 2. 建共享内存并写入
                string mapName = ShareNameFormat + pi.dwProcessId;
                hMap = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, ShareSize, mapName);
                if (hMap == IntPtr.Zero)
                    throw new InvalidOperationException("CreateFileMapping 失败（err=" + Marshal.GetLastWin32Error() + "）");

                pView = MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, 0);
                if (pView == IntPtr.Zero)
                    throw new InvalidOperationException("MapViewOfFile 失败（err=" + Marshal.GetLastWin32Error() + "）");

                Marshal.Copy(share, 0, pView, share.Length);
                Logger.Info("共享内存已写入 " + mapName + "（" + ShareSize + " 字节）");

                // 3. 注入 LauncherDll.dll（[override] inject=0 可跳过，用于排除輔助 DLL）
                //    H1 验证：inject_phase=resume 时挂起期不注入，改为放行、客户端自身初始化跑完后再注入
                bool injectDeferred = cfg.OvInject != "0" && cfg.OvInjectPhase == "resume";
                if (cfg.OvInject == "0")
                {
                    Logger.Warn("[override] inject=0 —— 跳过 LauncherDll.dll 注入");
                }
                else if (injectDeferred)
                {
                    Logger.Warn("[override] inject_phase=resume —— 挂起期不注入，放行后再注入");
                }
                else
                {
                    InjectDll((int)pi.dwProcessId, dllPath);
                }

                // 4. 放行
                ResumeThread(pi.hThread);
                Logger.Info("客户端已放行");

                // 复刻原版时序：放行后先 Sleep(1000) 让客户端打开映射，再 unmap/close
                Thread.Sleep(1000);
                UnmapViewOfFile(pView); pView = IntPtr.Zero;
                CloseHandle(hMap); hMap = IntPtr.Zero;

                // 3b. 延后注入（inject_phase=resume）：此时客户端已放行并自行初始化约 1s，再加载 LauncherDll.dll
                if (injectDeferred)
                {
                    Logger.Info("放行后延时注入 LauncherDll.dll…");
                    InjectDll((int)pi.dwProcessId, dllPath);
                }

                // 5. 校验注入结果（DLL 是否真的进了客户端进程）
                if (cfg.OvInject != "0")
                    VerifyInjection((int)pi.dwProcessId);

                // 5a. 客户端补丁（等壳解开后再写；失败只告警，不阻断启动）
                if (cfg.PatchEnable)
                {
                    try
                    {
                        Logger.Info("等待 " + cfg.PatchDelay + "ms 后应用客户端补丁…");
                        Thread.Sleep(cfg.PatchDelay);
                        ApplyPatches(pi.hProcess, cfg.PatchVa);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("客户端补丁失败（不影响启动）：" + ex.Message);
                    }
                }

                // 5b. 首次启动时抓一次解包镜像（诊断用；失败不影响启动）
                if (cfg.DumpOnLaunch)
                {
                    string img = Path.Combine(clientDir, "log", "client_img.bin");
                    if (File.Exists(img))
                    {
                        Logger.Info("解包镜像已存在，跳过转储：" + img);
                    }
                    else
                    {
                        try
                        {
                            Logger.Info("等待 " + cfg.DumpDelay + "ms 后转储解包镜像…");
                            Thread.Sleep(cfg.DumpDelay);
                            DumpImage(pi.hProcess, img, 0x1000, 0x1330000);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("镜像转储失败（不影响启动）：" + ex.Message);
                        }
                    }
                }

                // 5b. 诊断探针（--dumpcode）：读完就结束客户端，不等它自然退出
                if (afterResume != null)
                {
                    afterResume((int)pi.dwProcessId, pi.hProcess);
                    TerminateProcess(pi.hProcess, 0);
                    Logger.Info("诊断完成，客户端已结束");
                    return 0;
                }

                // 6. 常驻：LauncherDll 的看门狗要求 LinLauncher.exe 进程存在
                WaitForSingleObject(pi.hProcess, INFINITE);
                Logger.Info("客户端已退出");
                return 0;
            }
            finally
            {
                if (pView != IntPtr.Zero) UnmapViewOfFile(pView);
                if (hMap != IntPtr.Zero) CloseHandle(hMap);
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }

        /// <summary>
        /// 任务怪物卡片的 m_npcInfo 取值器由多个调用点共用。空指针检查放在调用点辅助函数，
        /// 保留原取值器堆栈序言与结尾，避免补丁写入时客户端正好执行该函数而破坏活动堆栈。
        /// PatchVa 是原取值器地址；呼叫点偏移及 helper code cave 对应 eve815C build 1705102501。
        /// </summary>
        private static readonly long[] NpcInfoGetterCallSiteOffsets =
        {
            -0x28FD4, -0x1CF07, -0xBAA1, -0x8201, 0x46098, 0x463BA
        };

        private static readonly byte[] NpcInfoNullGuardBytes =
        {
            0x85, 0xC9,             // test ecx, ecx
            0x74, 0x06,             // je   null result
            0xE8, 0xE6, 0xFF, 0xFF, 0xFF, // call original getter
            0xC3,                   // ret
            0x33, 0xC0,             // xor  eax, eax
            0xC3                    // ret
        };

        // FIX: [CAND-261C78990D036C14] zone3.xml 缺少巢狀字串物件時回傳空字串 - [已修復但未實機測試]
        private static readonly byte[] ZoneTextGetterHelperBytes =
        {
            0x85, 0xC9,             // test ecx, ecx
            0x75, 0x03,             // jnz to the original getter
            0x33, 0xC0,             // xor eax, eax
            0xC3,                   // ret
            0xE9, 0xD4, 0x30, 0x20, 0x00 // jmp 0x6C6A40
        };

        private static readonly byte[] ZoneTextCStringHelperBytes =
        {
            0x85, 0xC9,             // test ecx, ecx
            0x75, 0x06,             // jnz to the original c_str getter
            0xB8, 0xE3, 0xDA, 0x20, 0x01, // mov eax, empty string at 0x120DAE3
            0xC3,                   // ret
            0xE9, 0x91, 0x3D, 0xF6, 0xFF // jmp 0x4273E0
        };

        private static readonly byte[] ZoneTextCallSites =
        {
            0xE8, 0x39, 0xA5, 0xD5, 0xFF, // call 0x4C3960
            0x8B, 0xC8,                   // mov ecx, eax
            0xE8, 0x12, 0xA2, 0xD5, 0xFF  // call 0x4C3640
        };

        private static readonly byte[] ZoneTextCallSitesOriginalBytes =
        {
            0xE8, 0x19, 0xD6, 0xF5, 0xFF, // call 0x6C6A40
            0x8B, 0xC8,                   // mov ecx, eax
            0xE8, 0xB2, 0xDF, 0xCB, 0xFF  // call 0x4273E0
        };

        private const long NullVtableResultCallSiteVa = 0x10453D0;
        private const long NullVtableResultHelperVa = 0x10B7F40;

        private static readonly byte[] NullVtableResultOriginalBytes =
        {
            0x8B, 0x45, 0xEC, 0x8B, 0x10, 0x8B, 0x4D,
            0xEC, 0x8B, 0x42, 0x0C, 0xFF, 0xD0
        };

        private static readonly byte[] NullVtableResultGuardBytes =
        {
            0x85, 0xC0,             // test eax, eax
            0x74, 0x08,             // je   null result
            0x8B, 0x10,             // mov  edx, [eax]
            0x8B, 0xC8,             // mov  ecx, eax
            0xFF, 0x52, 0x0C,       // call dword ptr [edx+0x0C]
            0xC3,                   // ret
            0x33, 0xC0,             // xor  eax, eax
            0x99,                   // cdq
            0xC3                    // ret
        };

        internal static void ApplyPatches(IntPtr hProc, long patchVa)
        {
            long npcGuardVa = patchVa + 0x11;
            var npcCallSiteVas = new long[NpcInfoGetterCallSiteOffsets.Length];
            var npcCallSitePatches = new byte[NpcInfoGetterCallSiteOffsets.Length][];
            for (int i = 0; i < NpcInfoGetterCallSiteOffsets.Length; i++)
            {
                npcCallSiteVas[i] = patchVa + NpcInfoGetterCallSiteOffsets[i];
                npcCallSitePatches[i] = BuildRelativeCallPatch(npcCallSiteVas[i], npcGuardVa, 5);
            }

            byte[] nullVtableCallPatch = BuildRelativeCallPatch(
                NullVtableResultCallSiteVa, NullVtableResultHelperVa, NullVtableResultOriginalBytes.Length);

            VerifyPatchBytes(hProc, npcGuardVa, Int3Bytes(NpcInfoNullGuardBytes.Length), "NPC info guard code cave");
            for (int i = 0; i < npcCallSiteVas.Length; i++)
                VerifyPatchBytes(hProc, npcCallSiteVas[i], BuildRelativeCallPatch(npcCallSiteVas[i], patchVa, 5), "NPC info getter call site");
            VerifyPatchBytes(hProc, 0x4C3960, Int3Bytes(ZoneTextGetterHelperBytes.Length), "zone text getter code cave");
            VerifyPatchBytes(hProc, 0x4C3640, Int3Bytes(ZoneTextCStringHelperBytes.Length), "zone text string code cave");
            VerifyPatchBytes(hProc, 0x769422, ZoneTextCallSitesOriginalBytes, "zone3 call site");
            VerifyPatchBytes(hProc, NullVtableResultHelperVa, Int3Bytes(NullVtableResultGuardBytes.Length), "null vtable guard code cave");
            VerifyPatchBytes(hProc, NullVtableResultCallSiteVa, NullVtableResultOriginalBytes, "null vtable call site");

            ApplyPatch(hProc, npcGuardVa, NpcInfoNullGuardBytes);
            for (int i = 0; i < npcCallSiteVas.Length; i++)
                ApplyPatch(hProc, npcCallSiteVas[i], npcCallSitePatches[i]);
            ApplyPatch(hProc, 0x4C3960, ZoneTextGetterHelperBytes);
            ApplyPatch(hProc, 0x4C3640, ZoneTextCStringHelperBytes);
            ApplyPatch(hProc, 0x769422, ZoneTextCallSites);
            ApplyPatch(hProc, NullVtableResultHelperVa, NullVtableResultGuardBytes);
            ApplyPatch(hProc, NullVtableResultCallSiteVa, nullVtableCallPatch);
        }

        private static byte[] BuildRelativeCallPatch(long callVa, long targetVa, int length)
        {
            if (length < 5)
                throw new ArgumentOutOfRangeException("length");

            var patchBytes = new byte[length];
            patchBytes[0] = 0xE8;
            byte[] relative = BitConverter.GetBytes(checked((int)(targetVa - (callVa + 5))));
            Array.Copy(relative, 0, patchBytes, 1, relative.Length);
            for (int i = 5; i < patchBytes.Length; i++)
                patchBytes[i] = 0x90;
            return patchBytes;
        }

        private static byte[] Int3Bytes(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = 0xCC;
            return bytes;
        }

        private static void VerifyPatchBytes(IntPtr hProc, long patchVa, byte[] expectedBytes, string description)
        {
            var actualBytes = new byte[expectedBytes.Length];
            IntPtr bytesRead;
            if (!ReadProcessMemory(hProc, new IntPtr(patchVa), actualBytes, actualBytes.Length, out bytesRead)
                || bytesRead.ToInt64() != actualBytes.Length)
                throw new InvalidOperationException(description + " 讀取失敗（err=" + Marshal.GetLastWin32Error() + "）");

            for (int i = 0; i < actualBytes.Length; i++)
                if (actualBytes[i] != expectedBytes[i])
                    throw new InvalidOperationException(string.Format("{0} 簽章不符（VA=0x{1:X}，offset=0x{2:X}）", description, patchVa, i));
        }

        private static void ApplyPatch(IntPtr hProc, long patchVa, byte[] patchBytes)
        {
            uint oldProtect;
            if (!VirtualProtectEx(hProc, new IntPtr(patchVa), new UIntPtr((uint)patchBytes.Length),
                                  0x40 /* PAGE_EXECUTE_READWRITE */, out oldProtect))
                throw new InvalidOperationException("VirtualProtectEx 失败（err=" + Marshal.GetLastWin32Error() + "）");

            bool ok = WriteProcessMemory(hProc, new IntPtr(patchVa), patchBytes,
                                         (uint)patchBytes.Length, IntPtr.Zero);

            uint restore;
            VirtualProtectEx(hProc, new IntPtr(patchVa), new UIntPtr((uint)patchBytes.Length),
                             oldProtect, out restore);
            FlushInstructionCache(hProc, new IntPtr(patchVa), new UIntPtr((uint)patchBytes.Length));

            if (!ok)
                throw new InvalidOperationException("补丁写入失败（err=" + Marshal.GetLastWin32Error() + "）");

            // 回读校验
            var check = new byte[patchBytes.Length];
            IntPtr got;
            if (ReadProcessMemory(hProc, new IntPtr(patchVa), check, check.Length, out got))
            {
                bool same = true;
                for (int i = 0; i < check.Length; i++)
                    if (check[i] != patchBytes[i]) { same = false; break; }
                Logger.Info(string.Format("补丁 0x{0:X} 已写入，回读校验：{1}", patchVa, same ? "一致" : "不一致！"));
                if (!same)
                    Logger.Warn("补丁回读不一致，首字节 0x" + check[0].ToString("x2"));
            }
            else
            {
                Logger.Info(string.Format("补丁 0x{0:X} 已写入（回读失败，跳过校验）", patchVa));
            }
        }

        // ---- 共享内存构造（严格对应 FUN_004b5a90） ----
        //
        // 记录基址 = this+0x728+idx*0xD5，原版各字段来源：
        //   +0x768+idx*0xD5 = 记录+0x40  → share+0x00 (0x20)  host
        //   +0x788          = 记录+0x60  → share+0x20 (4)     port
        //   +0x79D          = 记录+0x75  → share+0x24 (1)
        //   +0x79E          = 记录+0x76  → share+0x25 (1)     helper
        //   +0x78D          = 记录+0x65  → share+0x26 (0x10)  备用 IP
        //   +0x79F          = 记录+0x77  → share+0x36 (1)     encrypt
        //   +0x7A0          = 记录+0x78  → share+0x38 (0x20)  bdfile
        //   +0x7E0          = 记录+0xB8  → share+0x79 (1)     randkey
        //   +0x7E9          = 记录+0xC1  → share+0x7C (4)     全局值2
        //   +0x7E5          = 记录+0xBD  → share+0x80 (4)     全局值1
        // 另：share+0x37 固定 1，share+0x78 固定 0。
        internal static byte[] BuildShareBlock(byte[] record)
        {
            var s = new byte[ShareSize];

            Array.Copy(record, 0x40, s, 0x00, 0x20);   // host
            Array.Copy(record, 0x60, s, 0x20, 0x04);   // port
            s[0x24] = record[0x75];
            s[0x25] = record[0x76];
            Array.Copy(record, 0x65, s, 0x26, 0x10);   // 备用 IP
            s[0x36] = record[0x77];                    // encrypt
            s[0x37] = 1;
            Array.Copy(record, 0x78, s, 0x38, 0x20);   // bdfile UTF-16
            s[0x78] = 0;
            s[0x79] = record[0xB8];                    // randkey（原版只写 1 字节）
            Array.Copy(record, 0xC1, s, 0x7C, 0x04);   // 全局值2 → +0x7C
            Array.Copy(record, 0xBD, s, 0x80, 0x04);   // 全局值1 → +0x80

            return s;
        }

        // ---- 注入（复刻 FUN_004b3270） ----

        private static void InjectDll(int pid, string dllPath)
        {
            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            if (hProc == IntPtr.Zero)
                throw new InvalidOperationException("OpenProcess 失败（err=" + Marshal.GetLastWin32Error() + "）");

            IntPtr remote = IntPtr.Zero;
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(dllPath + "\0");
                remote = VirtualAllocEx(hProc, IntPtr.Zero, (uint)bytes.Length, MEM_COMMIT, PAGE_READWRITE);
                if (remote == IntPtr.Zero)
                    throw new InvalidOperationException("VirtualAllocEx 失败（err=" + Marshal.GetLastWin32Error() + "）");

                if (!WriteProcessMemory(hProc, remote, bytes, (uint)bytes.Length, IntPtr.Zero))
                    throw new InvalidOperationException("WriteProcessMemory 失败（err=" + Marshal.GetLastWin32Error() + "）");

                IntPtr hK32 = GetModuleHandleW("kernel32.dll");
                IntPtr loadLibraryA = GetProcAddress(hK32, "LoadLibraryA");
                if (loadLibraryA == IntPtr.Zero)
                    throw new InvalidOperationException("找不到 LoadLibraryA");

                IntPtr hThread = CreateRemoteThread(hProc, IntPtr.Zero, 0, loadLibraryA, remote, 0, IntPtr.Zero);
                if (hThread == IntPtr.Zero)
                    throw new InvalidOperationException("CreateRemoteThread 失败（err=" + Marshal.GetLastWin32Error() + "）");

                WaitForSingleObject(hThread, 30000);
                CloseHandle(hThread);
                Logger.Info("LauncherDll.dll 注入完成");
            }
            finally
            {
                if (remote != IntPtr.Zero) VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
                CloseHandle(hProc);
            }
        }

        private static void VerifyInjection(int pid)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        foreach (ProcessModule m in p.Modules)
                        {
                            if (string.Equals(m.ModuleName, "LauncherDll.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.Info(string.Format(
                                    "已确认 LauncherDll.dll 加载于客户端 0x{0:X8}（{1} 字节）",
                                    m.BaseAddress.ToInt64(), m.ModuleMemorySize));
                                return;
                            }
                        }
                    }
                }
                catch (Exception) { }
                Thread.Sleep(250);
            }
            Logger.Warn("5 秒内未在客户端进程里看到 LauncherDll.dll —— 注入可能失败，登录会被服务端拒绝");
        }

        // ---- 选服 ----

        private static ServerEntry PickServer(List<ServerEntry> servers)
        {
            // 自绘界面（ServerSelectForm）：全屏背景图 + 左侧半透明服务器卡片 +
            // 金色選中項與右下角「開始遊戲」按鈕。
            return ServerSelectForm.Pick(servers, 0);
        }
    }
}
