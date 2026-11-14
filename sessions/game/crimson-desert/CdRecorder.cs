// CdRecorder: strictly read-only live gameplay evidence recorder for CrimsonDesert.exe (v1.0.0.2625).
// Uses PROCESS_VM_READ | PROCESS_QUERY_INFORMATION (0x0410) only; never writes to game memory.
// Logs: (a) INI changes (TOGGLE), (b) all 18 SessionBoost hook sites (SITES),
// (c) dynamically discovered SessionBoost 0x4000 cave control block (CAVE) & hit counters (HITS),
// (d) private RWX regions (RWX), and (e) save file modification events (SAVE).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class CdRecorder {
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_VM_READ = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    struct MBI {
        public IntPtr Base;
        public IntPtr AllocBase;
        public uint AllocProt;
        public IntPtr Size;
        public uint State;
        public uint Prot;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MBI lpBuffer, uint dwLength);

    struct HookSiteDef {
        public string Name;
        public long Rva;
        public long CodeOff;
        public string VanillaHex;
    }

    static readonly HookSiteDef[] SITES = new HookSiteDef[] {
        new HookSiteDef { Name = "PLAYER",        Rva = 0x024C5D31, CodeOff = 0x0000, VanillaHex = "488B5840488B4308" },
        new HookSiteDef { Name = "STATS",         Rva = 0x01559873, CodeOff = 0x0100, VanillaHex = "48C1E00448034658" },
        new HookSiteDef { Name = "COMMIT",        Rva = 0x0C3A7164, CodeOff = 0x0600, VanillaHex = "48895F08488B5C2448" },
        new HookSiteDef { Name = "DEFENSE",       Rva = 0x015593A9, CodeOff = 0x0900, VanillaHex = "8B0C88498B461848C1E105488B0C01" },
        new HookSiteDef { Name = "DMG",           Rva = 0x0C38660E, CodeOff = 0x0B00, VanillaHex = "448B2488488D4C24504A8B1CE3" },
        new HookSiteDef { Name = "SAVE_ITEM",     Rva = 0x01DF9580, CodeOff = 0x0D00, VanillaHex = "48837E1000" },
        new HookSiteDef { Name = "SELECTED_ITEM", Rva = 0x0209190C, CodeOff = 0x0F00, VanillaHex = "488B4B1041B8FFFF0000" },
        new HookSiteDef { Name = "KISS",          Rva = 0x03BB05B7, CodeOff = 0x1100, VanillaHex = "410F584500" },
        new HookSiteDef { Name = "DAYTIME",       Rva = 0x008BB2A3, CodeOff = 0x1380, VanillaHex = "C5FA11412C" },
        new HookSiteDef { Name = "EXP",           Rva = 0x09293A74, CodeOff = 0x1500, VanillaHex = "C5FA59CBC5F258D2" },
        new HookSiteDef { Name = "DURABILITY",    Rva = 0x020961CF, CodeOff = 0x1600, VanillaHex = "0FB77D400FB7C7664103C5" },
        new HookSiteDef { Name = "CRIME_EVAL",    Rva = 0x02234DC0, CodeOff = 0x1800, VanillaHex = "48895C2408488B4140" },
        new HookSiteDef { Name = "HORSE_TAMING",  Rva = 0x00948047, CodeOff = 0x1A00, VanillaHex = "C5FA5EC1C5FA1183D40C0000" },
        new HookSiteDef { Name = "BREATH_VITALS", Rva = 0x03712DF0, CodeOff = 0x1B80, VanillaHex = "488B51604C8BD14885D2" },
        new HookSiteDef { Name = "SLOT_EXPAND",   Rva = 0x01E010F0, CodeOff = 0x1E00, VanillaHex = "48895C2418564883EC20" },
        new HookSiteDef { Name = "ROMANCE_TRUST", Rva = 0x01BDB420, CodeOff = 0x2000, VanillaHex = "4C8BDC53555657" },
        new HookSiteDef { Name = "COUNT_ITEMS",   Rva = 0x01DFB470, CodeOff = 0x2100, VanillaHex = "48895C24084889542410" },
        new HookSiteDef { Name = "WANTED_EVAL",   Rva = 0x015A0C00, CodeOff = 0x2200, VanillaHex = "66895424105357" }
    };

    static string Hx(byte[] b) {
        return BitConverter.ToString(b).Replace("-", "");
    }

    static Dictionary<string, string> ReadIni(string path) {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            if (!File.Exists(path)) return d;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) {
                string l;
                while ((l = sr.ReadLine()) != null) {
                    string t = l.Trim();
                    if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("#") || t.StartsWith("[")) continue;
                    int eq = t.IndexOf('=');
                    if (eq > 0) d[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
                }
            }
        } catch { }
        return d;
    }

    static long ReadCaveFromHealthJson(string root, int targetPid) {
        try {
            string p = Path.Combine(root, @"tuning\research\rsi_telemetry\live_editor_health.json");
            if (!File.Exists(p)) return 0;
            string json = File.ReadAllText(p, Encoding.UTF8);
            Match mPid = Regex.Match(json, "\"attached_pid\"\\s*:\\s*(\\d+)");
            Match mCave = Regex.Match(json, "\"cave_addr\"\\s*:\\s*\"0x([0-9A-Fa-f]+)\"");
            if (mPid.Success && mCave.Success) {
                int pidVal;
                if (int.TryParse(mPid.Groups[1].Value, out pidVal) && pidVal == targetPid) {
                    long cVal;
                    if (long.TryParse(mCave.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cVal) && cVal > 0x10000L) {
                        return cVal;
                    }
                }
            }
        } catch { }
        return 0;
    }

    static long FindCaveByMagicScan(IntPtr h, long bas) {
        try {
            // SessionBoost AllocNear allocates within +-0x60000000 of baseAddr + 0x05800000L.
            long addr = bas > 0x60000000L ? Math.Max(0x10000L, bas - 0x65000000L) : 0x10000L;
            long endAddr = bas > 0 ? bas + 0x70000000L : 0x200000000L;
            uint sz = (uint)Marshal.SizeOf(typeof(MBI));
            MBI m;
            while (addr < endAddr && VirtualQueryEx(h, (IntPtr)addr, out m, sz) != 0) {
                long regionBase = m.Base.ToInt64();
                long regionSize = m.Size.ToInt64();
                if (m.State == 0x1000 && m.Prot == 0x40 && m.Type == 0x20000 && regionSize == 0x4000) {
                    byte[] c1 = new byte[8];
                    byte[] c2 = new byte[4];
                    IntPtr r1, r2;
                    if (ReadProcessMemory(h, (IntPtr)(regionBase + 0x3134), c1, 8, out r1) &&
                        ReadProcessMemory(h, (IntPtr)(regionBase + 0x3180), c2, 4, out r2)) {
                        float one = BitConverter.ToSingle(c1, 0);
                        float zero = BitConverter.ToSingle(c1, 4);
                        float neg100 = BitConverter.ToSingle(c2, 0);
                        if (Math.Abs(one - 1.0f) < 0.0001f && Math.Abs(zero) < 0.0001f && Math.Abs(neg100 + 100.0f) < 0.001f) {
                            return regionBase;
                        }
                    }
                }
                long next = regionBase + regionSize;
                if (next <= addr) break;
                addr = next;
            }
        } catch { }
        return 0;
    }

    static int I32(byte[] d, int off3000) {
        return BitConverter.ToInt32(d, off3000 - 0x3000);
    }

    static long I64(byte[] d, int off3000) {
        return BitConverter.ToInt64(d, off3000 - 0x3000);
    }

    static float F32(byte[] d, int off3000) {
        return BitConverter.ToSingle(d, off3000 - 0x3000);
    }

    static string FmtF(float v) {
        return float.IsNaN(v) || float.IsInfinity(v) ? "nan" : v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    static void Main(string[] args) {
        bool exitWithGame = Array.IndexOf(args, "--exit-with-game") >= 0;
        string tag = "";
        string explicitOut = "";
        int durationSec = 0;

        for (int i = 0; i < args.Length - 1; i++) {
            if (args[i] == "--session") tag = args[i + 1];
            else if (args[i] == "--out") explicitOut = args[i + 1];
            else if (args[i] == "--duration") {
                int parsed;
                if (int.TryParse(args[i + 1], out parsed)) durationSec = parsed;
            }
        }

        bool created;
        Mutex mtx = new Mutex(true, "Global\\CdRecorderSingleton", out created);
        if (!created) return;

        try {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string root = Path.GetFullPath(Path.Combine(dir, "..", ".."));
            string ini = Path.Combine(root, "tuning", "CrimsonDesert_CheatConfig.ini");
            string save = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Pearl Abyss", "CD", "save", "22202");
            string evDir = Path.Combine(dir, "evidence");
            Directory.CreateDirectory(evDir);
            string logPath = !string.IsNullOrEmpty(explicitOut)
                ? explicitOut
                : Path.Combine(evDir, "session_" + (!string.IsNullOrEmpty(tag) ? tag : DateTime.Now.ToString("yyyyMMdd_HHmmss")) + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));

            using (var w = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true }) {
                Action<string, string> E = delegate(string k, string m) {
                    w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + k + "\t" + m);
                };

                E("START", "recorder_v2 pid=" + Process.GetCurrentProcess().Id + " ini=" + ini);
                var prevIni = ReadIni(ini);
                string prevSites = "", prevCave = "", prevHits = "", prevState = "", prevSave = "";
                IntPtr h = IntPtr.Zero;
                int hpid = 0;
                long bas = 0;
                long caveAddr = 0;
                bool magicScannedForPid = false;
                int tick = 0;
                bool seenGame = false;
                int goneTicks = 0;
                DateTime startUtc = DateTime.UtcNow;

                while (true) {
                    if (durationSec > 0 && (DateTime.UtcNow - startUtc).TotalSeconds >= durationSec) {
                        E("END", "duration reached (" + durationSec + "s); recorder stops");
                        break;
                    }

                    Thread.Sleep(500);
                    tick++;

                    var curIni = ReadIni(ini);
                    foreach (var kv in curIni) {
                        string oldVal;
                        if (!prevIni.TryGetValue(kv.Key, out oldVal) || oldVal != kv.Value) {
                            if (!kv.Key.Equals("UpdatedAt", StringComparison.OrdinalIgnoreCase)) {
                                E("TOGGLE", kv.Key + ": " + (oldVal ?? "<new>") + " -> " + kv.Value);
                            }
                        }
                    }
                    prevIni = curIni;

                    if (tick % 2 != 0) continue; // every 1.0s sample game memory (zero full-RAM VirtualQueryEx stall!)

                    Process[] ps = Process.GetProcessesByName("CrimsonDesert");
                    try {
                        string st;
                        if (ps.Length == 0) {
                            st = "GAME_OFF";
                            if (h != IntPtr.Zero) { CloseHandle(h); h = IntPtr.Zero; hpid = 0; bas = 0; caveAddr = 0; magicScannedForPid = false; }
                            if (seenGame && exitWithGame && ++goneTicks >= 4) {
                                E("END", "game exited; recorder stops");
                                break;
                            }
                        } else {
                            seenGame = true;
                            goneTicks = 0;
                            Process p = ps[0];
                            int launcherCnt = 0, trainerCnt = 0;
                            var lps = Process.GetProcessesByName("CrimsonLauncher"); launcherCnt = lps.Length;
                            for (int i = 0; i < lps.Length; i++) lps[i].Dispose();
                            var tps = Process.GetProcessesByName("CrimsonTrainer"); trainerCnt = tps.Length;
                            for (int i = 0; i < tps.Length; i++) tps[i].Dispose();
                            var fps = Process.GetProcessesByName("FLiNG_Trainer_Plus12"); trainerCnt += fps.Length;
                            for (int i = 0; i < fps.Length; i++) fps[i].Dispose();

                            st = "GAME_ON pid=" + p.Id + " ws=" + (p.WorkingSet64 >> 20) + "MB launcher=" + (launcherCnt > 0) + " trainer=" + trainerCnt;

                            if (hpid != p.Id) {
                                if (h != IntPtr.Zero) CloseHandle(h);
                                h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, p.Id);
                                hpid = p.Id;
                                caveAddr = 0;
                                magicScannedForPid = false;
                                try { bas = p.MainModule.BaseAddress.ToInt64(); } catch { bas = 0; }
                                E("ATTACH", "pid=" + p.Id + " base=0x" + bas.ToString("X") + " handle=" + (h == IntPtr.Zero ? "FAIL_ERR_" + Marshal.GetLastWin32Error() : "OK_RO"));
                            }

                            if (h != IntPtr.Zero && bas != 0) {
                                var sb = new StringBuilder();
                                long detectedCfFromHook = 0;
                                for (int i = 0; i < SITES.Length; i++) {
                                    HookSiteDef site = SITES[i];
                                    byte[] buf = new byte[site.VanillaHex.Length / 2];
                                    IntPtr br;
                                    bool ok = ReadProcessMemory(h, (IntPtr)(bas + site.Rva), buf, buf.Length, out br);
                                    string hex = ok ? Hx(buf) : "READ_FAIL";
                                    string stateStr;
                                    if (!ok) {
                                        stateStr = "READ_FAIL";
                                    } else if (hex == site.VanillaHex) {
                                        stateStr = "VANILLA";
                                    } else if (buf[0] == 0xE9 && buf.Length >= 5) {
                                        stateStr = "HOOKED";
                                        int rel = BitConverter.ToInt32(buf, 1);
                                        long dst = (bas + site.Rva) + 5 + rel;
                                        long candCf = dst - site.CodeOff;
                                        if ((candCf & 0xFFF) == 0 && candCf > 0x10000 && detectedCfFromHook == 0) {
                                            detectedCfFromHook = candCf;
                                        }
                                    } else {
                                        stateStr = "OTHER:" + hex.Substring(0, Math.Min(10, hex.Length));
                                    }
                                    sb.Append(site.Name).Append("=").Append(stateStr).Append(" ");
                                }
                                string sStr = sb.ToString().TrimEnd();
                                if (sStr != prevSites) { E("SITES", sStr); prevSites = sStr; }

                                if (detectedCfFromHook != 0) {
                                    caveAddr = detectedCfFromHook;
                                } else if (caveAddr == 0) {
                                    caveAddr = ReadCaveFromHealthJson(root, p.Id);
                                    if (caveAddr == 0 && !magicScannedForPid) {
                                        magicScannedForPid = true;
                                        caveAddr = FindCaveByMagicScan(h, bas);
                                    }
                                }

                                if (caveAddr != 0) {
                                    byte[] d = new byte[0x188];
                                    IntPtr br2;
                                    if (ReadProcessMemory(h, (IntPtr)(caveAddr + 0x3000), d, d.Length, out br2) && br2.ToInt32() >= 0x184) {
                                        // Validate magic constants before trusting cave memory
                                        float cOne = F32(d, 0x3134);
                                        float cNeg = F32(d, 0x3180);
                                        if (Math.Abs(cOne - 1.0f) < 0.01f && Math.Abs(cNeg + 100.0f) < 0.1f) {
                                            string caveLine = string.Format(
                                                CultureInfo.InvariantCulture,
                                                "cf=0x{0:X} player=0x{1:X} silver={2} staPtr=0x{3:X} spiPtr=0x{4:X} god={5} sta={6} cd={7} msta={8} def={9} dmg={10} exp={11} kissMult={12} jumpSc={13} swim={14} freeze={15} thiefDet={16} pick={17} bounty={18} dur={19} tame={20} breath={21} bag={22} craftCost={23} craftSucc={24} imbue={25} spec={26} rom={27} gear={28} glove={29} mspd={30} selId={31}",
                                                caveAddr,
                                                I64(d, 0x3000),
                                                I64(d, 0x3038),
                                                I64(d, 0x30B8),
                                                I64(d, 0x30C0),
                                                I32(d, 0x3008),
                                                I32(d, 0x300C),
                                                I32(d, 0x3010),
                                                I32(d, 0x3014),
                                                I32(d, 0x3018),
                                                I32(d, 0x301C),
                                                FmtF(F32(d, 0x30B0)),
                                                FmtF(F32(d, 0x3070)),
                                                FmtF(F32(d, 0x3078)),
                                                FmtF(F32(d, 0x3088)),
                                                I32(d, 0x3090),
                                                I32(d, 0x30D0),
                                                I32(d, 0x30D4),
                                                I32(d, 0x30D8),
                                                I32(d, 0x30E0),
                                                FmtF(F32(d, 0x30E8)),
                                                I32(d, 0x30F0),
                                                I32(d, 0x30F8),
                                                I32(d, 0x3108),
                                                I32(d, 0x310C),
                                                I32(d, 0x3110),
                                                I32(d, 0x3118),
                                                FmtF(F32(d, 0x3120)),
                                                I32(d, 0x3124),
                                                I32(d, 0x3128),
                                                FmtF(F32(d, 0x312C)),
                                                I32(d, 0x3048)
                                            );
                                            if (caveLine != prevCave) { E("CAVE", caveLine); prevCave = caveLine; }

                                            string hitsLine = string.Format(
                                                CultureInfo.InvariantCulture,
                                                "commit={0} dur={1} crime={2} tame={3} breath={4} slot={5} save={6} sel={7} rom={8} cnt={9} wanted={10}",
                                                I32(d, 0x30C8),
                                                I32(d, 0x3150),
                                                I32(d, 0x3154),
                                                I32(d, 0x3158),
                                                I32(d, 0x315C),
                                                I32(d, 0x3160),
                                                I32(d, 0x3164),
                                                I32(d, 0x3168),
                                                I32(d, 0x316C),
                                                I32(d, 0x3170),
                                                I32(d, 0x3174)
                                            );
                                            if (hitsLine != prevHits) { E("HITS", hitsLine); prevHits = hitsLine; }
                                        }
                                    } else {
                                        caveAddr = 0; // re-discover on next tick
                                    }
                                }
                            }
                        }

                        if (st != prevState) { E("STATE", st); prevState = st; }
                        try {
                            if (Directory.Exists(save)) {
                                var sbs = new StringBuilder();
                                var files = new DirectoryInfo(save).GetFiles("*.save", SearchOption.AllDirectories);
                                for (int fi = 0; fi < files.Length; fi++) {
                                    var f = files[fi];
                                    sbs.Append(f.FullName.Substring(save.Length)).Append("|").Append(f.Length).Append("|").Append(f.LastWriteTime.ToString("HH:mm:ss")).Append(";");
                                }
                                string sv = sbs.ToString();
                                if (sv != prevSave) { if (prevSave != "") E("SAVE", "changed"); prevSave = sv; }
                            }
                        } catch { }
                    } finally {
                        for (int i = 0; i < ps.Length; i++) ps[i].Dispose();
                    }
                }
            }
        } finally {
            try { mtx.ReleaseMutex(); mtx.Dispose(); } catch { }
        }
    }
}
