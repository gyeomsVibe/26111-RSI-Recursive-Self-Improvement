using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class SessionBoost : IDisposable {
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint Flags);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSetTimerResolution(uint DesiredResolution, bool SetResolution, out uint CurrentResolution);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQueryTimerResolution(out uint MinimumResolution, out uint MaximumResolution, out uint CurrentResolution);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint HIGH_PRIORITY_CLASS = 0x00000080;
    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    private bool timerActive;
    private bool ntTimerActive;
    private IntPtr targetHandle = IntPtr.Zero;
    private Process owned;
    private DateTime started;
    private DateTime _lastBoostScanUtc = DateTime.MinValue;
    private ProcessPriorityClass original = ProcessPriorityClass.Normal;
    public bool Enabled;
    public int Pid { get; private set; }
    public bool PriorityRaised { get; private set; }
    public string Summary { get; private set; }

    public SessionBoost() { Summary = "대기"; }

    private static void DisablePowerThrottling(IntPtr hProcess) {
        try {
            var state = new PROCESS_POWER_THROTTLING_STATE {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0
            };
            SetProcessInformation(hProcess, ProcessPowerThrottling, ref state, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
        } catch { }
    }

    public string Refresh(string expectedPath) {
        if (!Enabled) { Restore(); return "세션 부스터 꺼짐"; }
        if (targetHandle != IntPtr.Zero) {
            uint exitCode;
            if (GetExitCodeProcess(targetHandle, out exitCode) && exitCode == 259u) {
                return "세션 부스터 가동 중: " + Summary;
            }
            Restore();
        } else if (owned != null) {
            try {
                owned.Refresh();
                if (!owned.HasExited) return "세션 부스터 가동 중: " + Summary;
            } catch { }
            Restore();
        }
        DateTime nowUtc = DateTime.UtcNow;
        if (_lastBoostScanUtc != DateTime.MinValue && (nowUtc - _lastBoostScanUtc).TotalMilliseconds < 2500.0) {
            return "세션 부스터 대기·다음 게임 실행 시 자동 적용";
        }
        _lastBoostScanUtc = nowUtc;
        var games = Process.GetProcessesByName("CrimsonDesert");
        try {
            if (games.Length == 0) return "세션 부스터 대기·다음 게임 실행 시 자동 적용";
            if (games.Length != 1) throw new InvalidOperationException("게임이 여러 개여서 부스터를 적용하지 않았습니다.");
            var p = games[0];
            try {
                if (p.MainModule != null && !string.Equals(p.MainModule.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("게임 경로가 달라 부스터를 적용하지 않았습니다.");
            } catch (System.ComponentModel.Win32Exception) { }

            try { original = p.PriorityClass; } catch { original = ProcessPriorityClass.Normal; }
            try { started = p.StartTime.ToUniversalTime(); } catch { started = DateTime.UtcNow; }
            try { p.PriorityBoostEnabled = true; } catch { }
            targetHandle = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, false, p.Id);
            try { timerActive = (timeBeginPeriod(1) == 0); } catch { timerActive = false; }
            uint setR = 10000u;
            try {
                uint minR, maxR, curR;
                if (NtQueryTimerResolution(out minR, out maxR, out curR) == 0 && NtSetTimerResolution(Math.Max(5000u, maxR), true, out setR) == 0) {
                    ntTimerActive = true;
                }
            } catch { }
            try { DisablePowerThrottling(targetHandle != IntPtr.Zero ? targetHandle : p.Handle); } catch { }
            if (targetHandle != IntPtr.Zero) {
                try { if (SetPriorityClass(targetHandle, ABOVE_NORMAL_PRIORITY_CLASS)) PriorityRaised = true; } catch { }
            }
            try { owned = Process.GetProcessById(p.Id); } catch { owned = null; }
            Pid = p.Id;
            Summary = string.Format(
                "우선순위 ABOVE_NORMAL · 타이머 {0} · CPU 스로틀링 해제",
                ntTimerActive ? string.Format("{0:0.0}ms", setR / 10000.0) : (timerActive ? "1.0ms" : "기본")
            );
            return "세션 부스터 적용 완료 (" + Summary + ")";
        } finally {
            foreach (var p in games) p.Dispose();
        }
    }

    public static SessionBoost Apply(int pid, bool highPriority, bool timer1Ms, Process ownReference) {
        var b = new SessionBoost { Pid = pid, owned = ownReference };
        var notes = new List<string>();
        if (timer1Ms) {
            try { b.timerActive = (timeBeginPeriod(1) == 0); } catch { b.timerActive = false; }
            try {
                uint minR, maxR, curR;
                if (NtQueryTimerResolution(out minR, out maxR, out curR) == 0) {
                    uint setR;
                    if (NtSetTimerResolution(Math.Max(5000u, maxR), true, out setR) == 0) {
                        b.ntTimerActive = true;
                        notes.Add(string.Format("타이머 {0:0.0}ms", setR / 10000.0));
                    }
                }
            } catch { }
            if (!b.ntTimerActive && b.timerActive) notes.Add("타이머 1ms");
        }
        if (pid > 0) {
            IntPtr h = IntPtr.Zero;
            try {
                h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_INFORMATION, false, pid);
                if (h != IntPtr.Zero) {
                    b.targetHandle = h;
                    if (highPriority) {
                        try {
                            if (SetPriorityClass(h, HIGH_PRIORITY_CLASS)) { b.PriorityRaised = true; notes.Add("우선순위 HIGH"); }
                        } catch { }
                    }
                    try { SetProcessWorkingSetSizeEx(h, (IntPtr)(1024L * 1024 * 1024), (IntPtr)(4096L * 1024 * 1024), 0); } catch { }
                }
            } catch { }
        }
        b.Summary = notes.Count > 0 ? string.Join(" · ", notes.ToArray()) : "기본";
        return b;
    }

    public void Restore() {
        if (targetHandle != IntPtr.Zero) {
            if (PriorityRaised) { try { SetPriorityClass(targetHandle, NORMAL_PRIORITY_CLASS); } catch { } }
            try { CloseHandle(targetHandle); } catch { }
            targetHandle = IntPtr.Zero;
        }
        PriorityRaised = false;
        Pid = 0;
        _lastBoostScanUtc = DateTime.MinValue;
        if (ntTimerActive) { uint d; try { NtSetTimerResolution(0, false, out d); } catch { } ntTimerActive = false; }
        if (timerActive) { try { timeEndPeriod(1); } catch { } timerActive = false; }
        if (owned != null) { try { owned.Dispose(); } catch { } owned = null; }
    }

    public void Dispose() { Restore(); }
}

public sealed class EditorCheatState {
    // Tab 1: Combat & Survival (7 rows)
    public int DamageTakenMode = 0;            // 0..4 (OFF, -30%, -50%, -75%, GodMode)
    public float DamageDealtMultiplier = 1.0f; // 1.0..5.0, >=900 => OHK
    public int PlayerStaminaMode = 0;          // 0..4 (OFF, -30%, -50%, 2xRecov, Infinite)
    public int CooldownMode = 0;               // 0..4 (OFF, -30%, -50%, -75%, 0s NoCD)
    public float ExpMultiplier = 1.0f;         // 1.0..10.0
    public float SpeedMultiplier = 1.0f;       // 1.0..3.0
    public float JumpMultiplier = 1.0f;        // 1.0..5.0

    // Tab 2: Romance & Taming (6 rows)
    public float RomanceMultiplier = 1.0f;     // 1.0..10.0, >=100 => Instant MAX
    public int AnimalTamingMode = 0;           // 0..4 (OFF, -50%, x3, x5, Instant)
    public float MountSpeedMultiplier = 1.0f;  // 1.0..3.0
    public int MountStaminaMode = 0;           // 0..4 (OFF, -30%, -50%, -75%, Infinite)
    public int UnderwaterOxygenMode = 0;       // 0..4 (OFF, x2, x3, x5, Infinite)
    public float UnderwaterSpeedMultiplier = 1.0f; // 1.0..3.0

    // Tab 3: Craft & Gear (6 rows)
    public int CraftMaterialCost = 0;          // 0..4 (OFF, -25%, -50%, -75%, Free 0)
    public int CraftGreatSuccess = 0;          // 0..4 (OFF, x2, x3, x5, 100%)
    public int DurabilityMode = 0;             // 0..4 (OFF, -30%, -50%, -75%, Infinite)
    public int GearHiderMode = 0;              // 0..3 (OFF, HelmHide, Helm+Cloak, +Sheathed)
    public int WeaponImbueMode = 0;            // 0..5 (None, Fire, Frost, Lightning, Lifesteal+Crit, Master)

    // Tab 4: Thief & Special (6 rows)
    public int ThiefDetectionMode = 0;         // 0..3 (OFF, -50%, -80%, 0% FullStealth)
    public int PickpocketSuccessMode = 0;      // 0..3 (OFF, x2, x3, 100%)
    public int BountyImmunityMode = 0;         // 0..3 (OFF, -50%, 0G, PermanentInnocent)
    public int ThiefGloveMode = 0;             // 0..3 (Unowned, Equipped, AllSaves, Fence100%)
    public int BagExpandSlots = 0;             // 0, 20, 40, 60, 900
    public int SpecialConvenienceMode = 0;     // 0..3 (OFF, Sleep0s, NoFallDamage, FreeFlight)

    // Shared / Legacy Bounds
    public int MaxHealthVal = 0;
    public int MaxStaminaVal = 0;
    public int MaxSpiritVal = 0;
    public int DefenseVal = 0;
    public bool FreezeDaytime = false;
}

internal static class LiveMemoryCheatEngine {
    private static long _lastLogReadOffset = 0;
    private static long _lastLogLen = -1;
    private static int _cumulativeStartCount = 0;
    private static int _cumulativeEndCount = 0;
    private static DateTime _cumulativeLastLoadTime = DateTime.MinValue;
    private static string _lastCachedLogPath = null;
    private static long _cachedSilver = -1L;
    private static DateTime _lastSilverQueryUtc = DateTime.MinValue;
    private static DateTime _lastSilverCacheFileCheckUtc = DateTime.MinValue;
    private static DateTime _lastSilverCacheFileMtimeUtc = DateTime.MinValue;
    private static int _arrowSanitizedPid = 0;
    private static readonly Dictionary<ushort, int> _itemKeyByTableIdx = new Dictionary<ushort, int>();

    private static Process cachedProc = null;
    private static DateTime _lastProcSearchUtc = DateTime.MinValue;
    private static DateTime _externalFlingYieldUntilUtc = DateTime.MinValue;
    private static DateTime _lastHealthWriteUtc = DateTime.MinValue;
    private static string _lastHealthSignature = "";
    static DateTime _lastWerCheckUtc = DateTime.MinValue;
    static bool _cachedWerActive = false;
    static readonly Dictionary<long, byte[]> _caveParamCache = new Dictionary<long, byte[]>();
    static DateTime _lastFullSilverScanUtc = DateTime.MinValue;
    static DateTime _lastWorldCheckUtc = DateTime.MinValue;
    static bool _cachedWorldSettled = false;
    static string _cachedWorldStatus = "";
    static long _cachedWorkingSet64 = 0;
    static DateTime _cachedStartTimeUtc = DateTime.MinValue;
    static readonly object _syncLock = new object();
    static long _fastJumpPollTicks = 0L;
    static DateTime _lastHealthReadbackUtc = DateTime.MinValue;
    static int _lastHealthActiveCount = -1;
    static long _lastHealthSilver = -1L;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RESERVE = 0x2000;
    const uint MEM_RELEASE = 0x8000;
    const uint PAGE_EXECUTE_READWRITE = 0x40;

    // =========================================================================
    // Verified RVAs & Vanilla Signatures for CrimsonDesert.exe v1.0.0.2625
    // =========================================================================
    const long RVA_PLAYER        = 0x024C5D31; // 8B: 48 8B 58 40 48 8B 43 08
    const long RVA_STATS         = 0x01559873; // 8B: 48 C1 E0 04 48 03 46 58
    const long RVA_COMMIT        = 0x0C3A7164; // 9B: 48 89 5F 08 48 8B 5C 24 48
    const long RVA_DEFENSE       = 0x015593A9; // 15B: 8B 0C 88 49 8B 46 18 48 C1 E1 05 48 8B 0C 01
    const long RVA_DMG           = 0x0C38660E; // 13B: 44 8B 24 88 48 8D 4C 24 50 4A 8B 1C E3
    const long RVA_DMG_APPLY     = 0x01559BD0; // 15B: 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18
    const long RVA_EXP           = 0x09293A74; // 8B: C5 FA 59 CB C5 F2 58 D2
    const long RVA_KISS          = 0x03BB05B7; // 5B: 41 0F 58 45 00
    const long RVA_DAYTIME       = 0x008BB2A3; // 5B: C5 FA 11 41 2C
    const long RVA_SAVE_ITEM     = 0x01DF9580; // 5B: 48 83 7E 10 00 (rsi = ItemRecord)
    const long RVA_SELECTED_ITEM = 0x0209190C; // 10B: 48 8B 4B 10 41 B8 FF FF 00 00 (rbx = ItemRecord)
    const long RVA_DURABILITY    = 0x020961CF; // 11B: 0F B7 7D 40 0F B7 C7 66 41 03 C5
    const long RVA_CRIME_EVAL    = 0x02234DC0; // 9B: 48 89 5C 24 08 48 8B 41 40
    const long RVA_HORSE_TAMING  = 0x00948047; // 12B: C5 FA 5E C1 C5 FA 11 83 D4 0C 00 00
    const long RVA_BREATH_VITALS = 0x03712DF0; // 10B: 48 8B 51 60 4C 8B D1 48 85 D2
    const long RVA_SLOT_EXPAND   = 0x01E010F0; // 10B: 48 89 5C 24 18 56 48 83 EC 20
    const long RVA_ROMANCE_TRUST = 0x01BDB420; // 7B: 4C 8B DC 53 55 56 57
    const long RVA_COUNT_ITEMS   = 0x01DFB470; // 10B: 48 89 5C 24 08 48 89 54 24 10
    const long RVA_WANTED_EVAL   = 0x015A0C00; // 7B: 66 89 54 24 10 53 57
    const long RVA_FRIENDLY_DAILY = 0x01BDC930; // 5B: E9 3B 72 DC 0B (-> 0x0D9A3B70 CanDoDailyEvent)
    const long RVA_AFFINITY_ADD   = 0x01BDCC50; // 5B: E9 2B AB DC 0B (-> 0x0D9A7780 AddAffinityExp)
    const long RVA_FRIENDLY_ADD   = 0x0239B016; // 8B: 48 8B 43 20 48 89 45 CF (UpdateFriendly delta read)
    const long RVA_ITEM_TABLE_MGR     = 0x06331248; // QWORD GlobalItemTableManager* ([rip+0x060199DC] at 0x00317865)
    const long RVA_ITEM_TABLE_MGR_REF = 0x00317865; // 7B: 48 8B 1D DC 99 01 06

    static readonly byte[] VANILLA_PLAYER        = new byte[] { 0x48, 0x8B, 0x58, 0x40, 0x48, 0x8B, 0x43, 0x08 };
    static readonly byte[] VANILLA_STATS         = new byte[] { 0x48, 0xC1, 0xE0, 0x04, 0x48, 0x03, 0x46, 0x58 };
    static readonly byte[] VANILLA_COMMIT        = new byte[] { 0x48, 0x89, 0x5F, 0x08, 0x48, 0x8B, 0x5C, 0x24, 0x48 };
    static readonly byte[] VANILLA_DEFENSE       = new byte[] { 0x8B, 0x0C, 0x88, 0x49, 0x8B, 0x46, 0x18, 0x48, 0xC1, 0xE1, 0x05, 0x48, 0x8B, 0x0C, 0x01 };
    static readonly byte[] VANILLA_DMG           = new byte[] { 0x44, 0x8B, 0x24, 0x88, 0x48, 0x8D, 0x4C, 0x24, 0x50, 0x4A, 0x8B, 0x1C, 0xE3 };
    static readonly byte[] VANILLA_DMG_APPLY     = new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18 };
    static readonly byte[] VANILLA_EXP           = new byte[] { 0xC5, 0xFA, 0x59, 0xCB, 0xC5, 0xF2, 0x58, 0xD2 };
    static readonly byte[] VANILLA_KISS          = new byte[] { 0x41, 0x0F, 0x58, 0x45, 0x00 };
    static readonly byte[] VANILLA_DAYTIME       = new byte[] { 0xC5, 0xFA, 0x11, 0x41, 0x2C };
    static readonly byte[] VANILLA_SAVE_ITEM     = new byte[] { 0x48, 0x83, 0x7E, 0x10, 0x00 };
    static readonly byte[] VANILLA_SELECTED_ITEM = new byte[] { 0x48, 0x8B, 0x4B, 0x10, 0x41, 0xB8, 0xFF, 0xFF, 0x00, 0x00 };
    static readonly byte[] VANILLA_DURABILITY    = new byte[] { 0x0F, 0xB7, 0x7D, 0x40, 0x0F, 0xB7, 0xC7, 0x66, 0x41, 0x03, 0xC5 };
    static readonly byte[] VANILLA_CRIME_EVAL    = new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x8B, 0x41, 0x40 };
    static readonly byte[] VANILLA_HORSE_TAMING  = new byte[] { 0xC5, 0xFA, 0x5E, 0xC1, 0xC5, 0xFA, 0x11, 0x83, 0xD4, 0x0C, 0x00, 0x00 };
    static readonly byte[] VANILLA_BREATH_VITALS = new byte[] { 0x48, 0x8B, 0x51, 0x60, 0x4C, 0x8B, 0xD1, 0x48, 0x85, 0xD2 };
    static readonly byte[] VANILLA_SLOT_EXPAND   = new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x56, 0x48, 0x83, 0xEC, 0x20 };
    static readonly byte[] VANILLA_ROMANCE_TRUST = new byte[] { 0x4C, 0x8B, 0xDC, 0x53, 0x55, 0x56, 0x57 };
    static readonly byte[] VANILLA_COUNT_ITEMS   = new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x54, 0x24, 0x10 };
    static readonly byte[] VANILLA_WANTED_EVAL   = new byte[] { 0x66, 0x89, 0x54, 0x24, 0x10, 0x53, 0x57 };
    static readonly byte[] VANILLA_FRIENDLY_DAILY = new byte[] { 0xE9, 0x3B, 0x72, 0xDC, 0x0B };
    static readonly byte[] VANILLA_AFFINITY_ADD   = new byte[] { 0xE9, 0x2B, 0xAB, 0xDC, 0x0B };
    static readonly byte[] VANILLA_FRIENDLY_ADD   = new byte[] { 0x48, 0x8B, 0x43, 0x20, 0x48, 0x89, 0x45, 0xCF };
    static readonly byte[] VANILLA_ITEM_TABLE_MGR_REF = new byte[] { 0x48, 0x8B, 0x1D, 0xDC, 0x99, 0x01, 0x06 };

    // Cave Code Offsets (cf + 0x0000 .. cf + 0x2FFF)
    const long OFF_CODE_PLAYER        = 0x0000;
    const long OFF_CODE_STATS         = 0x0100;
    const long OFF_CODE_COMMIT        = 0x0600;
    const long OFF_CODE_DEFENSE       = 0x0900;
    const long OFF_CODE_DMG           = 0x0B00;
    const long OFF_CODE_SAVE_ITEM     = 0x0D00;
    const long OFF_CODE_SELECTED_ITEM = 0x0F00;
    const long OFF_CODE_KISS          = 0x1100;
    const long OFF_CODE_DAYTIME       = 0x1380;
    const long OFF_CODE_EXP           = 0x1500;
    const long OFF_CODE_DURABILITY    = 0x1600;
    const long OFF_CODE_CRIME_EVAL    = 0x1800;
    const long OFF_CODE_HORSE_TAMING  = 0x1A00;
    const long OFF_CODE_BREATH_VITALS = 0x1B80;
    const long OFF_CODE_SLOT_EXPAND   = 0x1E00;
    const long OFF_CODE_ROMANCE_TRUST = 0x2000;
    const long OFF_CODE_COUNT_ITEMS   = 0x2100;
    const long OFF_CODE_WANTED_EVAL   = 0x2200;
    const long OFF_CODE_FRIENDLY_DAILY = 0x2300;
    const long OFF_CODE_AFFINITY_ADD   = 0x2400;
    const long OFF_CODE_FRIENDLY_ADD   = 0x2500;
    const long OFF_CODE_SAVE_ITEM_BODY     = 0x2680; // 0x2680..0x2A7F (1024B body)
    const long OFF_CODE_SELECTED_ITEM_BODY = 0x2A80; // 0x2A80..0x2FFF (1408B body)

    // Cave Data Offsets (dataBase = cf + 0x3000)
    const long DATA_BASE_OFF          = 0x3000;
    const long OFF_DATA_PLAYER        = 0x3000; // i64
    const long OFF_DATA_GODMODE       = 0x3008; // i32
    const long OFF_DATA_STAMINA_MODE  = 0x300C; // i32 (0..4)
    const long OFF_DATA_COOLDOWN_MODE = 0x3010; // i32 (0..4)
    const long OFF_DATA_MOUNT_STA     = 0x3014; // i32 (0..4)
    const long OFF_DATA_DEFENSE       = 0x3018; // i32
    const long OFF_DATA_DMG_MODE      = 0x301C; // i32
    const long OFF_DATA_MAX_HP        = 0x3020; // i32
    const long OFF_DATA_MAX_STA       = 0x3024; // i32
    const long OFF_DATA_MAX_SPIRIT    = 0x3028; // i32
    const long OFF_DATA_SILVER_PTR    = 0x3030; // i64
    const long OFF_DATA_CUR_SILVER    = 0x3038; // i64
    const long OFF_DATA_PEND_GOLD     = 0x3040; // i64
    const long OFF_DATA_SEL_ID        = 0x3048; // i32
    const long OFF_DATA_SEL_PTR       = 0x3050; // i64
    const long OFF_DATA_SEL_COUNT     = 0x3058; // i64
    const long OFF_DATA_EDIT_AMT      = 0x3060; // i64
    const long OFF_DATA_EDIT_ID       = 0x3068; // i32
    const long OFF_DATA_KISS_MULT     = 0x3070; // f32
    const long OFF_DATA_KISS_STATE    = 0x3074; // i32
    const long OFF_DATA_KISS_JUMPSC   = 0x3078; // f32 (mult - 1.0f)
    const long OFF_DATA_KISS_GLIDE    = 0x307C; // i32
    const long OFF_DATA_KISS_CLAMPY   = 0x3080; // f32
    const long OFF_DATA_KISS_MAXY     = 0x3084; // f32
    const long OFF_DATA_SWIM_MULT     = 0x3088; // f32
    const long OFF_DATA_UNDERWATER    = 0x308C; // i32
    const long OFF_DATA_FREEZE_TIME   = 0x3090; // i32
    const long OFF_DATA_ADVANCE_TICKS = 0x3098; // i64
    const long OFF_DATA_CUR_HOUR      = 0x30A0; // i32
    const long OFF_DATA_CUR_MIN       = 0x30A4; // i32
    const long OFF_DATA_EXP_MULT      = 0x30B0; // f32
    const long OFF_DATA_P_STA_PTR     = 0x30B8; // i64 (captured stat entry for player id 17)
    const long OFF_DATA_P_SPI_PTR     = 0x30C0; // i64 (captured stat entry for player id 18)
    const long OFF_DATA_COMMIT_CNT    = 0x30C8; // i32
    const long OFF_DATA_THIEF_DET     = 0x30D0; // i32
    const long OFF_DATA_PICKPOCKET    = 0x30D4; // i32
    const long OFF_DATA_BOUNTY_MODE   = 0x30D8; // i32
    const long OFF_DATA_CRIME_CTR     = 0x30DC; // i32
    const long OFF_DATA_DUR_MODE      = 0x30E0; // i32
    const long OFF_DATA_DUR_ACC       = 0x30E4; // i32
    const long OFF_DATA_TAMING_MULT   = 0x30E8; // f32
    const long OFF_DATA_TAMING_INST   = 0x30EC; // i32
    const long OFF_DATA_BREATH_MODE   = 0x30F0; // i32
    const long OFF_DATA_BREATH_MULT   = 0x30F4; // f32
    const long OFF_DATA_BAG_EXPAND    = 0x30F8; // i32
    const long OFF_DATA_INV_MGR_PTR   = 0x3100; // i64
    const long OFF_DATA_CRAFT_COST    = 0x3108; // i32
    const long OFF_DATA_CRAFT_SUCC    = 0x310C; // i32
    const long OFF_DATA_IMBUE_MODE    = 0x3110; // i32
    const long OFF_DATA_LIFESTEAL_PCT = 0x3114; // i32
    const long OFF_DATA_SPEC_CONV     = 0x3118; // i32
    const long OFF_DATA_SLEEP_TRIG    = 0x311C; // i32
    const long OFF_DATA_ROMANCE_MULT  = 0x3120; // f32
    const long OFF_DATA_GEAR_HIDER    = 0x3124; // i32
    const long OFF_DATA_THIEF_GLOVE   = 0x3128; // i32
    const long OFF_DATA_MOUNT_SPEED   = 0x312C; // f32
    const long OFF_DATA_MOUNT_SEEN    = 0x3130; // i32
    const long OFF_DATA_CONST_ONE     = 0x3134; // f32 (1.0f)
    const long OFF_DATA_CONST_ZERO    = 0x3138; // f32 (0.0f)
    const long OFF_DATA_FLY_LIFTY     = 0x313C; // f32
    const long OFF_DATA_PREV_BREATH   = 0x3140; // f32
    const long OFF_DATA_NPC_MGR_PTR   = 0x3148; // i64
    const long OFF_DATA_DUR_HITS      = 0x3150; // i32
    const long OFF_DATA_CRIME_HITS    = 0x3154; // i32
    const long OFF_DATA_TAMING_HITS   = 0x3158; // i32
    const long OFF_DATA_BREATH_HITS   = 0x315C; // i32
    const long OFF_DATA_SLOT_HITS     = 0x3160; // i32
    const long OFF_DATA_SAVE_HITS     = 0x3164; // i32
    const long OFF_DATA_SEL_HITS      = 0x3168; // i32
    const long OFF_DATA_ROM_HITS      = 0x316C; // i32
    const long OFF_DATA_CNT_HITS      = 0x3170; // i32
    const long OFF_DATA_WANTED_HITS   = 0x3174; // i32
    const long OFF_DATA_MOUNT_DECAY   = 0x3178; // i32
    const long OFF_DATA_PREV_MOUNT_S  = 0x317C; // i32
    const long OFF_DATA_CONST_NEG100  = 0x3180; // f32 (-100.0f)
    const long OFF_DATA_PREV_STA      = 0x3188; // i64
    const long OFF_DATA_PREV_SPI      = 0x3190; // i64
    const long OFF_DATA_PREV_MSTA     = 0x3198; // i64
    const long OFF_DATA_PREV_HP       = 0x31A0; // i64
    const long OFF_DATA_BAG_CONT_PTR  = 0x31A8; // i64
    const long OFF_DATA_DMG_TAKEN_MODE = 0x31B0; // i32 (0..4)
    const long OFF_DATA_P_HP_PTR      = 0x31B8; // i64

    static long _lastValidPlayerPtr = 0;
    static long _lastValidStaPtr = 0;
    static long _lastValidSpiPtr = 0;
    static long _lastValidHpPtr = 0;
    static long _lastValidInvMgrPtr = 0;
    static long _lastValidBagContPtr = 0;
    static long _lastValidSilverPtr = 0;
    static System.Threading.Timer _fastJumpPollTimer = null;

    // External FLiNG Memory Control Addresses (0x211D0000 range)
    public const long FLING_ADDR_GODMODE   = 0x211D0200L;
    public const long FLING_ADDR_HPMULT    = 0x211D0204L;
    public const long FLING_ADDR_STAMINA   = 0x211D0208L;
    public const long FLING_ADDR_STAMULT   = 0x211D020CL;
    public const long FLING_ADDR_SPIRIT    = 0x211D0210L;
    public const long FLING_ADDR_SPIMULT   = 0x211D0214L;
    public const long FLING_ADDR_MOUNT_STA = 0x211D0218L;
    public const long FLING_ADDR_DEFENSE   = 0x211F0200L;
    public const long FLING_ADDR_DMG       = 0x21200200L;

    static int attachedPid = 0;
    static IntPtr hProc = IntPtr.Zero;
    static long baseAddr = 0;
    static IntPtr caveFling = IntPtr.Zero;
    static bool trampolinesReady = false;

    // On-Demand Hook Tracking Flags
    static bool hookPlayerInstalled       = false;
    static bool hookStatsInstalled        = false;
    static bool hookCommitInstalled       = false;
    static bool hookDefenseInstalled      = false;
    static bool hookDmgInstalled          = false;
    static bool hookSaveItemInstalled     = false;
    static bool hookSelectedItemInstalled = false;
    static bool hookKissInstalled         = false;
    static bool hookDaytimeInstalled      = false;
    static bool hookExpInstalled          = false;
    static bool hookDurabilityInstalled   = false;
    static bool hookCrimeEvalInstalled    = false;
    static bool hookHorseTamingInstalled  = false;
    static bool hookBreathVitalsInstalled = false;
    static bool hookSlotExpandInstalled   = false;
    static bool hookRomanceTrustInstalled = false;
    static bool hookFriendlyDailyInstalled = false;
    static bool hookAffinityAddInstalled   = false;
    static bool hookFriendlyAddInstalled   = false;
    static bool hookCountItemsInstalled   = false;
    static bool hookWantedEvalInstalled   = false;

    static bool expOrphanChecked = false;
    static bool commitOrphanChecked = false;
    static long lastCommitLogTicks = 0;
    static int lastCommitCount = -1;

    // World Load Tracking State (Pearl Abyss Launcher_*.log telemetry)
    static string cachedLogPath = null;
    static int prevEndLoadCount = -1;
    static DateTime lastObservedSaveLoadUtc = DateTime.MinValue;

    // Public Runtime States
    public static float ExpMult = 1.0f;
    public static bool FreezeDaytimeEnabled = false;
    public static bool GlideNoFallEnabled = false;
    static DateTime lastJumpTriggerUtc = DateTime.MinValue;
    static DateTime sleepHealUntilUtc = DateTime.MinValue;
    static bool wasSpaceDown = false;
    static bool _extFling = false;
    static DateTime _extFlingAt = DateTime.MinValue;
    static DateTime _lastFlingCheckUtc = DateTime.MinValue;
    static bool _cachedFlingRunning = false;

    static EditorCheatState _lastSyncedState = new EditorCheatState();

    public static bool IsAttached {
        get { return hProc != IntPtr.Zero && (caveFling != IntPtr.Zero || IsExternalFlingTrainerRunning()); }
    }

    public static void NotifyExternalFlingLaunched() {
        _externalFlingYieldUntilUtc = DateTime.UtcNow.AddSeconds(12);
        _lastFlingCheckUtc = DateTime.MinValue;
        _extFlingAt = DateTime.MinValue;
        _cachedFlingRunning = true;
        _extFling = true;
        try { RevertFlingOverlappingHooks(); } catch { }
        EngLog("External FLiNG trainer detected: 7 FLiNG AOB sites yielded to vanilla, non-overlapping internal hooks preserved.");
    }

    public static bool EnsureProcessAttached() {
        if (hProc != IntPtr.Zero && attachedPid != 0) {
            try {
                uint exitCode;
                if (GetExitCodeProcess(hProc, out exitCode) && exitCode == 259) return true;
            } catch { }
        }
        var procs = Process.GetProcessesByName("CrimsonDesert");
        if (procs.Length > 0) {
            try {
                attachedPid = procs[0].Id;
                try { baseAddr = procs[0].MainModule.BaseAddress.ToInt64(); } catch { }
                hProc = OpenProcess(PROCESS_ALL_ACCESS, false, attachedPid);
                return (hProc != IntPtr.Zero);
            } catch {
                return false;
            } finally {
                for (int i = 0; i < procs.Length; i++) procs[i].Dispose();
            }
        }
        return false;
    }

    static IntPtr AllocNear(IntPtr hProcess, long targetAddr, uint size) {
        long minAddr = Math.Max(0x10000000L, targetAddr - 0x60000000L);
        long maxAddr = targetAddr + 0x60000000L;
        for (long addr = (targetAddr & ~0xFFFFL) - 0x100000L; addr >= minAddr; addr -= 0x100000L) {
            IntPtr p = VirtualAllocEx(hProcess, (IntPtr)addr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (p != IntPtr.Zero) return p;
        }
        for (long addr = (targetAddr & ~0xFFFFL) + 0x100000L; addr <= maxAddr; addr += 0x100000L) {
            IntPtr p = VirtualAllocEx(hProcess, (IntPtr)addr, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (p != IntPtr.Zero) return p;
        }
        return VirtualAllocEx(hProcess, IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    }

    static bool ExtFlingRunning() {
        if (DateTime.UtcNow < _externalFlingYieldUntilUtc) return true;
        if ((DateTime.UtcNow - _extFlingAt).TotalMilliseconds > 500) {
            try { _extFling = IsExternalFlingTrainerRunning(); } catch { _extFling = false; }
            _extFlingAt = DateTime.UtcNow;
        }
        return _extFling;
    }

    static byte[] ReadFlingCtl(long addr, int len) { return ExtFlingRunning() ? ReadBytes(addr, len) : null; }
    static bool WriteFlingCtl(long addr, byte[] data) { return ExtFlingRunning() && WriteBytes(addr, data); }

    static void CacheCaveBytes(long addr, byte[] data) {
        if (data == null || data.Length == 0 || data.Length > 8) return;
        byte[] copy = new byte[data.Length];
        Buffer.BlockCopy(data, 0, copy, 0, data.Length);
        _caveParamCache[addr] = copy;
    }

    static bool WriteBytes(long addr, byte[] data) {
        if (hProc == IntPtr.Zero || data == null || data.Length == 0) return false;
        long cf = caveFling != IntPtr.Zero ? caveFling.ToInt64() : 0L;
        bool inCave = (cf != 0L && addr >= cf && (addr + data.Length) <= (cf + 0x4000L));
        byte[] existing = ReadBytes(addr, data.Length);
        if (existing != null && existing.Length == data.Length) {
            bool same = true;
            for (int i = 0; i < data.Length; i++) {
                if (existing[i] != data[i]) { same = false; break; }
            }
            if (same) {
                if (inCave) CacheCaveBytes(addr, data);
                return true;
            }
        }
        if (inCave) {
            // caveFling is already RWX: skip VirtualProtectEx to avoid cross-core IPI TLB shootdowns.
            IntPtr writtenCave;
            bool okCave = WriteProcessMemory(hProc, (IntPtr)addr, data, data.Length, out writtenCave);
            if (okCave) CacheCaveBytes(addr, data);
            return okCave;
        }
        uint oldProt;
        if (!VirtualProtectEx(hProc, (IntPtr)addr, (uint)data.Length, PAGE_EXECUTE_READWRITE, out oldProt)) return false;
        IntPtr written;
        bool ok = WriteProcessMemory(hProc, (IntPtr)addr, data, data.Length, out written);
        uint dummy;
        VirtualProtectEx(hProc, (IntPtr)addr, (uint)data.Length, oldProt, out dummy);
        return ok;
    }

    static bool WriteCaveParam(long addr, byte[] data) {
        if (hProc == IntPtr.Zero || data == null || data.Length == 0) return false;
        byte[] prev;
        if (_caveParamCache.TryGetValue(addr, out prev) && prev != null && prev.Length == data.Length) {
            bool same = true;
            for (int i = 0; i < data.Length; i++) {
                if (prev[i] != data[i]) { same = false; break; }
            }
            if (same) return true;
        }
        IntPtr written;
        if (!WriteProcessMemory(hProc, (IntPtr)addr, data, data.Length, out written)) return false;
        byte[] copy = new byte[data.Length];
        Array.Copy(data, copy, data.Length);
        _caveParamCache[addr] = copy;
        return true;
    }

    static byte[] ReadBytes(long addr, int len) {
        if (hProc == IntPtr.Zero || len <= 0) return null;
        byte[] buf = new byte[len];
        IntPtr read;
        if (!ReadProcessMemory(hProc, (IntPtr)addr, buf, len, out read)) return null;
        return buf;
    }

    static bool IsReadableGamePtr(long ptr) {
        return ptr >= 0x10000000L && (ptr >> 47) == 0;
    }

    static bool IsValidGameHeapPtr(long ptr) {
        if (!IsReadableGamePtr(ptr)) return false;
        if (baseAddr != 0 && ptr >= baseAddr && ptr < baseAddr + 0x17000000L) return false;
        return true;
    }

    static long ReadI64(long addr) {
        byte[] b = ReadBytes(addr, 8);
        return b != null ? BitConverter.ToInt64(b, 0) : 0L;
    }

    static int ReadI32(long addr) {
        byte[] b = ReadBytes(addr, 4);
        return b != null ? BitConverter.ToInt32(b, 0) : 0;
    }

    static uint ReadU32(long addr) {
        byte[] b = ReadBytes(addr, 4);
        return b != null ? BitConverter.ToUInt32(b, 0) : 0u;
    }

    static ushort ReadU16(long addr) {
        byte[] b = ReadBytes(addr, 2);
        return b != null ? BitConverter.ToUInt16(b, 0) : (ushort)0;
    }

    static string GetSilverCacheFilePath() {
        try {
            string bd = (AppDomain.CurrentDomain.BaseDirectory ?? "").TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(bd)) return null;
            if (Directory.Exists(Path.Combine(bd, "tuning"))) {
                return Path.Combine(bd, @"tuning\editor\silver_cache.txt");
            }
            return Path.Combine(bd, @"editor\silver_cache.txt");
        } catch {
            return null;
        }
    }

    static void PersistSilverCacheFile(long val) {
        if (val < 0) return;
        try {
            string p = GetSilverCacheFilePath();
            if (string.IsNullOrEmpty(p)) return;
            string dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(p, val.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false));
            _lastSilverCacheFileMtimeUtc = File.GetLastWriteTimeUtc(p);
            _lastSilverCacheFileCheckUtc = DateTime.UtcNow;
        } catch { }
    }

    static bool TryResolveRealItemKeyByTableIdx(ushort tblIdx, out int realItemKey, bool bypassCache) {
        realItemKey = 0;
        if (tblIdx == 0xFFFF || hProc == IntPtr.Zero || baseAddr == 0) return false;
        if (!bypassCache && _itemKeyByTableIdx.TryGetValue(tblIdx, out realItemKey)) {
            return realItemKey > 0;
        }
        long mgr = ReadI64(baseAddr + RVA_ITEM_TABLE_MGR);
        if (!IsReadableGamePtr(mgr)) return false;
        uint tblCnt = ReadU32(mgr + 0x08);
        if (tblIdx >= tblCnt) return false;
        long arr = ReadI64(mgr + 0x58);
        if (!IsReadableGamePtr(arr)) return false;
        long stPtr = ReadI64(arr + (long)tblIdx * 8L);
        if (!IsReadableGamePtr(stPtr)) return false;
        int k = ReadI32(stPtr);
        if (k <= 0) return false;
        realItemKey = k;
        _itemKeyByTableIdx[tblIdx] = k;
        return true;
    }

    static bool TryVerifyRecordItemKey(long recordPtr, int expectedItemKey) {
        if (expectedItemKey <= 0 || !IsValidGameHeapPtr(recordPtr) || hProc == IntPtr.Zero) return false;
        ushort tblIdx = ReadU16(recordPtr + 0x08);
        int resolvedKey;
        return TryResolveRealItemKeyByTableIdx(tblIdx, out resolvedKey, true) && resolvedKey == expectedItemKey;
    }

    static bool TryFindPlayerBagSilverRecord(out long silverRecordPtr, out long currentSilver, bool sanitizeArrows, long explicitTargetGold = -1L) {
        silverRecordPtr = 0;
        currentSilver = -1L;
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero || baseAddr == 0) return false;
        long cf = caveFling.ToInt64();
        long invMgr = ReadI64(cf + OFF_DATA_INV_MGR_PTR);
        if (IsValidGameHeapPtr(invMgr)) {
            _lastValidInvMgrPtr = invMgr;
        } else if (IsValidGameHeapPtr(_lastValidInvMgrPtr)) {
            invMgr = _lastValidInvMgrPtr;
            WriteBytes(cf + OFF_DATA_INV_MGR_PTR, BitConverter.GetBytes(invMgr));
        }

        var candidateContainers = new List<long>();
        if (IsValidGameHeapPtr(invMgr)) {
            long[] arrOffs = new long[] { 0x18, 0xD0 };
            long[] cntOffs = new long[] { 0x20, 0xB8 };
            for (int k = 0; k < arrOffs.Length; k++) {
                long contArr = ReadI64(invMgr + arrOffs[k]);
                int contCnt = ReadI32(invMgr + cntOffs[k]);
                if (IsValidGameHeapPtr(contArr) && contCnt >= 1 && contCnt <= 32) {
                    byte[] ptrBuf = ReadBytes(contArr, contCnt * 8);
                    if (ptrBuf != null && ptrBuf.Length >= contCnt * 8) {
                        for (int c = 0; c < contCnt; c++) {
                            long cp = BitConverter.ToInt64(ptrBuf, c * 8);
                            if (IsValidGameHeapPtr(cp) && !candidateContainers.Contains(cp)) candidateContainers.Add(cp);
                        }
                    }
                }
            }
        }
        long bagCont = ReadI64(cf + OFF_DATA_BAG_CONT_PTR);
        if (IsValidGameHeapPtr(bagCont)) {
            _lastValidBagContPtr = bagCont;
            if (!candidateContainers.Contains(bagCont)) candidateContainers.Insert(0, bagCont);
        } else if (IsValidGameHeapPtr(_lastValidBagContPtr)) {
            WriteBytes(cf + OFF_DATA_BAG_CONT_PTR, BitConverter.GetBytes(_lastValidBagContPtr));
            if (!candidateContainers.Contains(_lastValidBagContPtr)) candidateContainers.Add(_lastValidBagContPtr);
        }
        var matchedSilverPtrs = new List<long>();
        for (int c = 0; c < candidateContainers.Count; c++) {
            long contPtr = candidateContainers[c];
            if (!IsValidGameHeapPtr(contPtr)) continue;
            byte[] hdr = ReadBytes(contPtr, 0x14);
            if (hdr == null || hdr.Length < 0x12) continue;
            long itemsBase = BitConverter.ToInt64(hdr, 0);
            ushort slotCap = BitConverter.ToUInt16(hdr, 0x0C);
            ushort invKey = BitConverter.ToUInt16(hdr, 0x10);
            if (!IsValidGameHeapPtr(itemsBase) || slotCap < 1 || slotCap > 2048) continue;
            if (invKey == 2 || !IsValidGameHeapPtr(_lastValidBagContPtr)) {
                _lastValidBagContPtr = contPtr;
                WriteBytes(cf + OFF_DATA_BAG_CONT_PTR, BitConverter.GetBytes(contPtr));
            }

            byte[] itemsBuf = ReadBytes(itemsBase, slotCap * 0xC8);
            if (itemsBuf == null && slotCap > 128) itemsBuf = ReadBytes(itemsBase, 128 * 0xC8);
            if (itemsBuf == null && slotCap > 32)  itemsBuf = ReadBytes(itemsBase, 32 * 0xC8);
            if (itemsBuf == null && slotCap > 8)   itemsBuf = ReadBytes(itemsBase, 8 * 0xC8);
            if (itemsBuf == null || itemsBuf.Length < 0xC8) continue;
            int actualSlots = Math.Min((int)slotCap, itemsBuf.Length / 0xC8);
            for (int s = 0; s < actualSlots; s++) {
                int off = s * 0xC8;
                ushort tblIdx = BitConverter.ToUInt16(itemsBuf, off + 0x08);
                if (tblIdx == 0xFFFF) continue;
                long stack = BitConverter.ToInt64(itemsBuf, off + 0x10);
                if (stack <= 0) continue;

                int realItemKey;
                if (!TryResolveRealItemKeyByTableIdx(tblIdx, out realItemKey, false)) continue;

                if (sanitizeArrows && realItemKey == 50001 && stack > 9999L) {
                    long recPtr = itemsBase + (long)s * 0xC8L;
                    if (TryVerifyRecordItemKey(recPtr, 50001)) {
                        long cleanArrows = (invKey == 2) ? 501L : 1L;
                        WriteBytes(recPtr + 0x10, BitConverter.GetBytes(cleanArrows));
                        EngLog(string.Format("SanitizeArrows LIVE invKey={0} slot={1} @0x{2:X}: {3} -> {4}", invKey, s, recPtr, stack, cleanArrows));
                    }
                } else if (realItemKey == 1 && (silverRecordPtr == 0 || invKey == 2)) {
                    long recPtr = itemsBase + (long)s * 0xC8L;
                    if (TryVerifyRecordItemKey(recPtr, 1)) {
                        if (!matchedSilverPtrs.Contains(recPtr)) matchedSilverPtrs.Add(recPtr);
                        silverRecordPtr = recPtr;
                        currentSilver = ReadI64(recPtr + 0x10);
                        _lastValidSilverPtr = recPtr;
                        _lastValidBagContPtr = contPtr;
                        WriteBytes(cf + OFF_DATA_BAG_CONT_PTR, BitConverter.GetBytes(contPtr));
                    }
                }
            }
            // BlackSpace Engine has TWO containers per item (0x18 display + 0xD0 authoritative): NEVER break early!
        }

        long cachedSilPtr = ReadI64(cf + OFF_DATA_SILVER_PTR);
        if (IsValidGameHeapPtr(cachedSilPtr) && TryVerifyRecordItemKey(cachedSilPtr, 1)) {
            if (!matchedSilverPtrs.Contains(cachedSilPtr)) matchedSilverPtrs.Add(cachedSilPtr);
            if (silverRecordPtr == 0) {
                silverRecordPtr = cachedSilPtr;
                currentSilver = ReadI64(cachedSilPtr + 0x10);
            }
        }
        if (IsValidGameHeapPtr(_lastValidSilverPtr) && TryVerifyRecordItemKey(_lastValidSilverPtr, 1)) {
            if (!matchedSilverPtrs.Contains(_lastValidSilverPtr)) matchedSilverPtrs.Add(_lastValidSilverPtr);
            if (silverRecordPtr == 0) {
                silverRecordPtr = _lastValidSilverPtr;
                currentSilver = ReadI64(_lastValidSilverPtr + 0x10);
            }
        }

        if (silverRecordPtr != 0 && currentSilver >= 0) {
            long pendGold = ReadI64(cf + OFF_DATA_PEND_GOLD);
            long targetGold = (explicitTargetGold > 0) ? explicitTargetGold : pendGold;
            if (targetGold > 0) {
                byte[] goldBytes = BitConverter.GetBytes(targetGold);
                for (int i = 0; i < matchedSilverPtrs.Count; i++) {
                    WriteBytes(matchedSilverPtrs[i] + 0x10, goldBytes);
                }
                if (matchedSilverPtrs.Count >= 2) {
                    WriteBytes(cf + OFF_DATA_PEND_GOLD, BitConverter.GetBytes(0L));
                } else {
                    WriteBytes(cf + OFF_DATA_PEND_GOLD, goldBytes);
                }
                currentSilver = targetGold;
                EngLog(string.Format("TryFindPlayerBagSilverRecord synchronized targetGold={0} across {1} container(s) (primary @0x{2:X})", targetGold, matchedSilverPtrs.Count, silverRecordPtr));
            }
            WriteBytes(cf + OFF_DATA_SILVER_PTR, BitConverter.GetBytes(silverRecordPtr));
            WriteBytes(cf + OFF_DATA_CUR_SILVER, BitConverter.GetBytes(currentSilver));
            _cachedSilver = currentSilver;
            _lastSilverQueryUtc = DateTime.UtcNow;
            PersistSilverCacheFile(currentSilver);
            return true;
        }
        return false;
    }

    static bool WriteRelJmp5(long fromAddr, long toAddr, int totalPatchLen) {
        long rel = toAddr - (fromAddr + 5);
        if (rel < int.MinValue || rel > int.MaxValue) return false;
        byte[] patch = new byte[totalPatchLen];
        patch[0] = 0xE9;
        byte[] relBytes = BitConverter.GetBytes((int)rel);
        Array.Copy(relBytes, 0, patch, 1, 4);
        for (int i = 5; i < totalPatchLen; i++) patch[i] = 0x90;
        return WriteBytes(fromAddr, patch);
    }

    static bool MatchesBytes(byte[] actual, byte[] expected) {
        if (actual == null || expected == null || actual.Length != expected.Length) return false;
        for (int i = 0; i < expected.Length; i++) {
            if (actual[i] != expected[i]) return false;
        }
        return true;
    }

    static bool IsOwnHookAt(long siteAddr, byte[] vanilla, long expectedTrampolineAddr) {
        byte[] cur = ReadBytes(siteAddr, vanilla.Length);
        if (cur == null || cur.Length != vanilla.Length || cur[0] != 0xE9) return false;
        for (int i = 5; i < cur.Length; i++) {
            if (cur[i] != 0x90) return false;
        }
        int rel = BitConverter.ToInt32(cur, 1);
        long dst = siteAddr + 5 + rel;
        if (expectedTrampolineAddr != 0 && dst == expectedTrampolineAddr) return true;
        if (caveFling != IntPtr.Zero) {
            long cf = caveFling.ToInt64();
            if (dst >= cf && dst < cf + 0x4000) return true;
        }
        return false;
    }

    static void SyncSingleHook(string name, bool wantInstall, ref bool flag, long siteAddr, long trampAddr, byte[] vanilla) {
        if (hProc == IntPtr.Zero || baseAddr == 0 || caveFling == IntPtr.Zero) return;
        if (wantInstall) {
            if (!flag) {
                byte[] cur = ReadBytes(siteAddr, vanilla.Length);
                if (MatchesBytes(cur, vanilla) || IsOwnHookAt(siteAddr, vanilla, 0)) {
                    if (WriteRelJmp5(siteAddr, trampAddr, vanilla.Length)) {
                        flag = true;
                        EngLog("Hook ON: " + name);
                    }
                } else {
                    EngLog("Hook REFUSED (" + name + "): non-vanilla site bytes");
                }
            }
        } else {
            if (flag) {
                if (IsOwnHookAt(siteAddr, vanilla, 0)) {
                    WriteBytes(siteAddr, vanilla);
                    EngLog("Hook OFF: " + name);
                } else {
                    EngLog("Hook YIELD (" + name + "): site owned by external trainer");
                }
                flag = false;
            }
        }
    }

    static void SafeRevertHookIfOwn(long siteAddr, byte[] vanilla, ref bool flag) {
        if (!flag && !IsOwnHookAt(siteAddr, vanilla, 0)) return;
        if (IsOwnHookAt(siteAddr, vanilla, 0)) {
            WriteBytes(siteAddr, vanilla);
        }
        flag = false;
    }

    // Reverts the 7 exact code sites scanned by FLiNG Plus 12 Trainer AOBs so FLiNG never throws 'Address not found'
    public static void RevertFlingOverlappingHooks() {
        if (hProc == IntPtr.Zero || baseAddr == 0) return;
        SafeRevertHookIfOwn(baseAddr + RVA_PLAYER,        VANILLA_PLAYER,        ref hookPlayerInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_STATS,         VANILLA_STATS,         ref hookStatsInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_DEFENSE,       VANILLA_DEFENSE,       ref hookDefenseInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_DMG,           VANILLA_DMG,           ref hookDmgInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_SAVE_ITEM,     VANILLA_SAVE_ITEM,     ref hookSaveItemInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_SELECTED_ITEM, VANILLA_SELECTED_ITEM, ref hookSelectedItemInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_DAYTIME,       VANILLA_DAYTIME,       ref hookDaytimeInstalled);
    }

    public static void RevertAllHooks() {
        if (hProc == IntPtr.Zero || baseAddr == 0) return;
        RevertFlingOverlappingHooks();
        SafeRevertHookIfOwn(baseAddr + RVA_COMMIT,        VANILLA_COMMIT,        ref hookCommitInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_KISS,          VANILLA_KISS,          ref hookKissInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_EXP,           VANILLA_EXP,           ref hookExpInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_DURABILITY,    VANILLA_DURABILITY,    ref hookDurabilityInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_CRIME_EVAL,    VANILLA_CRIME_EVAL,    ref hookCrimeEvalInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_HORSE_TAMING,  VANILLA_HORSE_TAMING,  ref hookHorseTamingInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_BREATH_VITALS, VANILLA_BREATH_VITALS, ref hookBreathVitalsInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_SLOT_EXPAND,   VANILLA_SLOT_EXPAND,   ref hookSlotExpandInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_ROMANCE_TRUST, VANILLA_ROMANCE_TRUST, ref hookRomanceTrustInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_FRIENDLY_DAILY,VANILLA_FRIENDLY_DAILY,ref hookFriendlyDailyInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_AFFINITY_ADD,  VANILLA_AFFINITY_ADD,  ref hookAffinityAddInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_FRIENDLY_ADD,  VANILLA_FRIENDLY_ADD,  ref hookFriendlyAddInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_COUNT_ITEMS,   VANILLA_COUNT_ITEMS,   ref hookCountItemsInstalled);
        SafeRevertHookIfOwn(baseAddr + RVA_WANTED_EVAL,   VANILLA_WANTED_EVAL,   ref hookWantedEvalInstalled);
    }

    public static bool IsExternalFlingTrainerRunning() {
        if (DateTime.UtcNow < _externalFlingYieldUntilUtc) return true;
        if ((DateTime.UtcNow - _lastFlingCheckUtc).TotalMilliseconds < 500) {
            return _cachedFlingRunning;
        }
        _lastFlingCheckUtc = DateTime.UtcNow;
        int selfPid = 0;
        try { selfPid = Process.GetCurrentProcess().Id; } catch { }
        try {
            bool found = false;
            string[] names = new string[] {
                "FLiNG_Trainer_Plus12",
                "Crimson Desert Enhanced v1.0-v2.0x Plus 12 Trainer Updated 2026.10.30"
            };
            for (int n = 0; n < names.Length && !found; n++) {
                Process[] ps = Process.GetProcessesByName(names[n]);
                for (int i = 0; i < ps.Length; i++) {
                    try { if (ps[i].Id != selfPid) found = true; } catch { }
                    try { ps[i].Dispose(); } catch { }
                }
            }
            _cachedFlingRunning = found;
            return found;
        } catch {
            return false;
        }
    }

    static bool IsCrashHandlerActive() {
        if ((DateTime.UtcNow - _lastWerCheckUtc).TotalMilliseconds < 3000) {
            return _cachedWerActive;
        }
        _lastWerCheckUtc = DateTime.UtcNow;
        try {
            Process[] wer = Process.GetProcessesByName("WerFault");
            bool active = (wer.Length > 0);
            for (int i = 0; i < wer.Length; i++) { try { wer[i].Dispose(); } catch { } }
            _cachedWerActive = active;
            return active;
        } catch {
            return false;
        }
    }

    // =========================================================================
    // Offline PE RVA Signature Verifier (Self-Test Gate 2)
    // =========================================================================
    public static bool VerifyAllHookSignatures(string exePath, out string report) {
        var checks = new KeyValuePair<string, KeyValuePair<long, byte[]>>[] {
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_PLAYER",        new KeyValuePair<long, byte[]>(RVA_PLAYER,        VANILLA_PLAYER)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_STATS",         new KeyValuePair<long, byte[]>(RVA_STATS,         VANILLA_STATS)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_COMMIT",        new KeyValuePair<long, byte[]>(RVA_COMMIT,        VANILLA_COMMIT)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_DEFENSE",       new KeyValuePair<long, byte[]>(RVA_DEFENSE,       VANILLA_DEFENSE)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_DMG",           new KeyValuePair<long, byte[]>(RVA_DMG,           VANILLA_DMG)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_DMG_APPLY",     new KeyValuePair<long, byte[]>(RVA_DMG_APPLY,     VANILLA_DMG_APPLY)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_EXP",           new KeyValuePair<long, byte[]>(RVA_EXP,           VANILLA_EXP)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_KISS",          new KeyValuePair<long, byte[]>(RVA_KISS,          VANILLA_KISS)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_DAYTIME",       new KeyValuePair<long, byte[]>(RVA_DAYTIME,       VANILLA_DAYTIME)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_SAVE_ITEM",     new KeyValuePair<long, byte[]>(RVA_SAVE_ITEM,     VANILLA_SAVE_ITEM)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_SELECTED_ITEM", new KeyValuePair<long, byte[]>(RVA_SELECTED_ITEM, VANILLA_SELECTED_ITEM)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_DURABILITY",    new KeyValuePair<long, byte[]>(RVA_DURABILITY,    VANILLA_DURABILITY)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_CRIME_EVAL",    new KeyValuePair<long, byte[]>(RVA_CRIME_EVAL,    VANILLA_CRIME_EVAL)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_HORSE_TAMING",  new KeyValuePair<long, byte[]>(RVA_HORSE_TAMING,  VANILLA_HORSE_TAMING)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_BREATH_VITALS", new KeyValuePair<long, byte[]>(RVA_BREATH_VITALS, VANILLA_BREATH_VITALS)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_SLOT_EXPAND",   new KeyValuePair<long, byte[]>(RVA_SLOT_EXPAND,   VANILLA_SLOT_EXPAND)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_ROMANCE_TRUST", new KeyValuePair<long, byte[]>(RVA_ROMANCE_TRUST, VANILLA_ROMANCE_TRUST)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_COUNT_ITEMS",   new KeyValuePair<long, byte[]>(RVA_COUNT_ITEMS,   VANILLA_COUNT_ITEMS)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_WANTED_EVAL",   new KeyValuePair<long, byte[]>(RVA_WANTED_EVAL,   VANILLA_WANTED_EVAL)),
            new KeyValuePair<string, KeyValuePair<long, byte[]>>("ITEM_TABLE_MGR_REF", new KeyValuePair<long, byte[]>(RVA_ITEM_TABLE_MGR_REF, VANILLA_ITEM_TABLE_MGR_REF))
        };

        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) {
            report = "CrimsonDesert.exe not found: " + (exePath ?? "<null>");
            return false;
        }
        try {
            int passCount = 0;
            var failList = new List<string>();
            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs)) {
                byte[] dos = br.ReadBytes(64);
                int peOff = BitConverter.ToInt32(dos, 0x3C);
                fs.Seek(peOff, SeekOrigin.Begin);
                byte[] pe = br.ReadBytes(24);
                ushort numSec = BitConverter.ToUInt16(pe, 6);
                ushort optSz = BitConverter.ToUInt16(pe, 20);
                fs.Seek(peOff + 24 + optSz, SeekOrigin.Begin);

                var secs = new List<long[]>(); // [va, span, roff]
                for (int i = 0; i < numSec; i++) {
                    byte[] s = br.ReadBytes(40);
                    uint vsize = BitConverter.ToUInt32(s, 8);
                    uint va = BitConverter.ToUInt32(s, 12);
                    uint rsize = BitConverter.ToUInt32(s, 16);
                    uint roff = BitConverter.ToUInt32(s, 20);
                    secs.Add(new long[] { va, Math.Max(vsize, rsize), roff });
                }

                for (int i = 0; i < checks.Length; i++) {
                    string name = checks[i].Key;
                    long rva = checks[i].Value.Key;
                    byte[] expected = checks[i].Value.Value;
                    long fileOff = -1;
                    for (int s = 0; s < secs.Count; s++) {
                        if (rva >= secs[s][0] && rva < secs[s][0] + secs[s][1]) {
                            fileOff = secs[s][2] + (rva - secs[s][0]);
                            break;
                        }
                    }
                    if (fileOff < 0) {
                        failList.Add(name + "(no section)");
                        continue;
                    }
                    fs.Seek(fileOff, SeekOrigin.Begin);
                    byte[] actual = br.ReadBytes(expected.Length);
                    if (MatchesBytes(actual, expected)) passCount++;
                    else failList.Add(name + string.Format("@0x{0:X}", rva));
                }

                var extraChecks = new KeyValuePair<string, KeyValuePair<long, byte[]>>[] {
                    new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_FRIENDLY_DAILY", new KeyValuePair<long, byte[]>(RVA_FRIENDLY_DAILY, VANILLA_FRIENDLY_DAILY)),
                    new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_AFFINITY_ADD",   new KeyValuePair<long, byte[]>(RVA_AFFINITY_ADD,   VANILLA_AFFINITY_ADD)),
                    new KeyValuePair<string, KeyValuePair<long, byte[]>>("RVA_FRIENDLY_ADD",   new KeyValuePair<long, byte[]>(RVA_FRIENDLY_ADD,   VANILLA_FRIENDLY_ADD))
                };
                for (int i = 0; i < extraChecks.Length; i++) {
                    string name = extraChecks[i].Key;
                    long rva = extraChecks[i].Value.Key;
                    byte[] expected = extraChecks[i].Value.Value;
                    long fileOff = -1;
                    for (int s = 0; s < secs.Count; s++) {
                        if (rva >= secs[s][0] && rva < secs[s][0] + secs[s][1]) {
                            fileOff = secs[s][2] + (rva - secs[s][0]);
                            break;
                        }
                    }
                    if (fileOff < 0) {
                        failList.Add(name + "(no section)");
                        continue;
                    }
                    fs.Seek(fileOff, SeekOrigin.Begin);
                    byte[] actual = br.ReadBytes(expected.Length);
                    if (!MatchesBytes(actual, expected)) failList.Add(name + string.Format("@0x{0:X}", rva));
                }
            }
            if (failList.Count == 0) {
                report = string.Format("RVA_VERIFY_OK={0}/{1}", passCount, checks.Length);
                return true;
            }
            report = string.Format("RVA_VERIFY_FAIL={0}/{1}: {2}", passCount, checks.Length, string.Join(", ", failList.ToArray()));
            return false;
        } catch (Exception ex) {
            report = "RVA_VERIFY_EXC=" + ex.Message;
            return false;
        }
    }

    static int _prevLoggedWSetMB = 0;
    public static bool IsWorldSettled(int pid, double uptimeSec, out string loadStatus) {
        return IsWorldLoadedAndSettled(pid, 0, uptimeSec, out loadStatus);
    }

    static void PurgeStaleCavePointers() {
        _itemKeyByTableIdx.Clear();
        _arrowSanitizedPid = 0;
        _lastValidPlayerPtr = 0;
        _lastValidStaPtr = 0;
        _lastValidSpiPtr = 0;
        _lastValidHpPtr = 0;
        _lastValidInvMgrPtr = 0;
        _lastValidBagContPtr = 0;
        _lastValidSilverPtr = 0;
        _cachedSilver = -1L;
        _lastSilverQueryUtc = DateTime.MinValue;
        _lastFullSilverScanUtc = DateTime.MinValue;
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero) return;
        long cf = caveFling.ToInt64();
        byte[] z8 = new byte[8];
        WriteBytes(cf + OFF_DATA_PLAYER, z8);
        WriteBytes(cf + OFF_DATA_P_STA_PTR, z8);
        WriteBytes(cf + OFF_DATA_P_SPI_PTR, z8);
        WriteBytes(cf + OFF_DATA_P_HP_PTR, z8);
        WriteBytes(cf + OFF_DATA_INV_MGR_PTR, z8);
        WriteBytes(cf + OFF_DATA_BAG_CONT_PTR, z8);
        WriteBytes(cf + OFF_DATA_SILVER_PTR, z8);
        WriteBytes(cf + OFF_DATA_SEL_PTR, z8);
        WriteBytes(cf + OFF_DATA_PREV_STA, z8);
        WriteBytes(cf + OFF_DATA_PREV_SPI, z8);
        WriteBytes(cf + OFF_DATA_PREV_MSTA, z8);
        WriteBytes(cf + OFF_DATA_PREV_HP, z8);
        EngLog("PurgeStaleCavePointers: cleared all cached actor/inventory/silver pointers for save load.");
    }

    public static bool IsWorldLoadedAndSettled(int pid, long workingSetBytes, double uptimeSec, out string loadStatus) {
        loadStatus = "로딩 대기 중";
        if (uptimeSec < 12.0) {
            loadStatus = string.Format("게임 부팅 및 엔진 초기화 대기 ({0:0}s / 12s)", uptimeSec);
            return false;
        }
        try {
            if (cachedLogPath == null || (DateTime.UtcNow - lastObservedSaveLoadUtc).TotalSeconds > 30) {
                string paBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pearl Abyss");
                string[] logDirs = new string[] {
                    Path.Combine(paBase, "log"),
                    Path.Combine(paBase, "CD", "log")
                };
                DateTime procStartApprox = DateTime.UtcNow.AddSeconds(-uptimeSec - 30.0);
                DateTime maxWt = DateTime.MinValue;
                string bestPidFile = null;
                string bestRecentFile = null;
                string pidToken = "_" + pid + ".log";
                for (int d = 0; d < logDirs.Length; d++) {
                    if (!Directory.Exists(logDirs[d])) continue;
                    string[] files = Directory.GetFiles(logDirs[d], "Launcher_*.log");
                    for (int i = 0; i < files.Length; i++) {
                        DateTime wt = File.GetLastWriteTimeUtc(files[i]);
                        if (files[i].IndexOf(pidToken, StringComparison.OrdinalIgnoreCase) >= 0) {
                            bestPidFile = files[i];
                            break;
                        }
                        if (wt >= procStartApprox && wt > maxWt) {
                            maxWt = wt;
                            bestRecentFile = files[i];
                        }
                    }
                    if (bestPidFile != null) break;
                }
                if (bestPidFile != null) cachedLogPath = bestPidFile;
                else if (bestRecentFile != null) cachedLogPath = bestRecentFile;
            }

            if (cachedLogPath != null && File.Exists(cachedLogPath)) {
                FileInfo fi = new FileInfo(cachedLogPath);
                long curLen = fi.Length;
                if (cachedLogPath != _lastCachedLogPath || curLen < _lastLogLen) {
                    _lastCachedLogPath = cachedLogPath;
                    _lastLogReadOffset = 0;
                    _lastLogLen = -1;
                    _cumulativeStartCount = 0;
                    _cumulativeEndCount = 0;
                }
                if (curLen != _lastLogLen) {
                    _lastLogLen = curLen;
                    string deltaText = "";
                    using (FileStream fs = new FileStream(cachedLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                        fs.Seek(_lastLogReadOffset, SeekOrigin.Begin);
                        using (StreamReader sr = new StreamReader(fs, Encoding.UTF8)) {
                            deltaText = sr.ReadToEnd();
                            _lastLogReadOffset = fs.Position;
                        }
                    }
                    if (!string.IsNullOrEmpty(deltaText)) {
                        int dStart = 0, dEnd = 0, idx = 0;
                        while ((idx = deltaText.IndexOf("Start load", idx, StringComparison.OrdinalIgnoreCase)) != -1) { dStart++; idx += 10; }
                        idx = 0;
                        while ((idx = deltaText.IndexOf("End load", idx, StringComparison.OrdinalIgnoreCase)) != -1) { dEnd++; idx += 8; }
                        _cumulativeStartCount += dStart;
                        _cumulativeEndCount += dEnd;
                        if (dStart > 0) PurgeStaleCavePointers();
                        if (dEnd > 0) _cumulativeLastLoadTime = DateTime.UtcNow;
                    }
                }

                if (_cumulativeStartCount > _cumulativeEndCount) {
                    prevEndLoadCount = _cumulativeEndCount;
                    loadStatus = "월드 세이브 데이터 로딩 중 (로딩 보호 작동 중)";
                    return false;
                }
                if (_cumulativeEndCount > 0) {
                    if (prevEndLoadCount != _cumulativeEndCount) {
                        if (prevEndLoadCount > 0) PurgeStaleCavePointers();
                        prevEndLoadCount = _cumulativeEndCount;
                        lastObservedSaveLoadUtc = DateTime.UtcNow;
                    }
                    double settleSec = (DateTime.UtcNow - lastObservedSaveLoadUtc).TotalSeconds;
                    if (settleSec < 4.0) {
                        loadStatus = string.Format("맵 3D 엔티티 안정화 대기 ({0:0.0}s / 4.0s)", settleSec);
                        return false;
                    }
                    loadStatus = "인게임 월드 안정화 완료";
                    return true;
                }
                if (cachedLogPath.IndexOf("_" + pid + ".log", StringComparison.OrdinalIgnoreCase) >= 0) {
                    loadStatus = "타이틀 화면 대기 중 (세이브 월드 진입 후 자동 연결)";
                    return false;
                }
            }
        } catch { }

        double wsMb = workingSetBytes / (1024.0 * 1024.0);
        if (workingSetBytes > 0 && wsMb < 1800.0 && uptimeSec < 45.0) {
            int mbInt = (int)wsMb;
            if (Math.Abs(mbInt - _prevLoggedWSetMB) > 50) _prevLoggedWSetMB = mbInt;
            loadStatus = string.Format("게임 월드 메모리 로딩 대기 ({0:N0} MB)", wsMb);
            return false;
        }
        if (uptimeSec < 32.0) {
            loadStatus = string.Format("타이틀 화면 및 리소스 초기화 대기 ({0:0}s / 32s)", uptimeSec);
            return false;
        }
        loadStatus = "인게임 월드 안정화 완료 (Fallback)";
        return true;
    }

    static string EngLogPath {
        get {
            try { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine.log"); }
            catch { return null; }
        }
    }

    static void EngLog(string msg) {
        try {
            string p = EngLogPath;
            if (p != null) {
                try {
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 2097152L) {
                        string prev = Path.Combine(Path.GetDirectoryName(p), "engine.prev.log");
                        if (File.Exists(prev)) File.Delete(prev);
                        File.Move(p, prev);
                    }
                } catch { }
                string line = DateTime.Now.ToString("HH:mm:ss.fff ") + msg + "\r\n";
                File.AppendAllText(p, line);
                try {
                    string dir = Path.GetDirectoryName(p);
                    if (dir != null && string.Equals(Path.GetFileName(dir), "tuning", StringComparison.OrdinalIgnoreCase)) {
                        string rootLog = Path.Combine(Directory.GetParent(dir).FullName, "engine.log");
                        File.AppendAllText(rootLog, line);
                    }
                } catch { }
            }
        } catch { }
    }

    // =========================================================================
    // Public Sync Entrypoints (New EditorCheatState + Legacy Signature)
    // =========================================================================
    public static string SyncWithGame(EditorCheatState state) {
        if (state == null) state = new EditorCheatState();
        _lastSyncedState = state;
        return SyncWithGameCore(state);
    }

    public static string SyncWithGame(
        int damageTakenMode,
        int playerStaminaMode,
        float damageDealtMultiplier,
        int cooldownMode,
        float speedMultiplier,
        int jumpMultiplier,
        float mountSpeedMultiplier,
        int mountStaminaMode,
        float underwaterSpeedMultiplier,
        int maxHealthVal = 0,
        int maxStaminaVal = 0,
        int maxSpiritVal = 0,
        int defenseVal = 1000
    ) {
        var st = new EditorCheatState {
            DamageTakenMode = damageTakenMode,
            PlayerStaminaMode = playerStaminaMode,
            DamageDealtMultiplier = damageDealtMultiplier,
            CooldownMode = cooldownMode,
            SpeedMultiplier = speedMultiplier,
            JumpMultiplier = (float)jumpMultiplier,
            MountSpeedMultiplier = mountSpeedMultiplier,
            MountStaminaMode = mountStaminaMode,
            UnderwaterSpeedMultiplier = underwaterSpeedMultiplier,
            MaxHealthVal = maxHealthVal,
            MaxStaminaVal = maxStaminaVal,
            MaxSpiritVal = maxSpiritVal,
            DefenseVal = defenseVal,
            ExpMultiplier = ExpMult,
            FreezeDaytime = FreezeDaytimeEnabled,
            SpecialConvenienceMode = GlideNoFallEnabled ? 2 : 0
        };
        return SyncWithGameCore(st);
    }

    static void EnsureFastJumpPollerRunning() {
        if (_fastJumpPollTimer != null) return;
        _fastJumpPollTimer = new System.Threading.Timer(delegate {
            try {
                System.Threading.Interlocked.Increment(ref _fastJumpPollTicks);
                if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero || attachedPid == 0) return;
                EditorCheatState st = _lastSyncedState;
                if (st == null || st.JumpMultiplier <= 1.05f || st.SpecialConvenienceMode == 3) return;
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                uint fgPid;
                GetWindowThreadProcessId(fg, out fgPid);
                if (fgPid != (uint)attachedPid) return;
                bool sp = ((GetAsyncKeyState(0x20) & 0x8000) != 0);
                long cf = caveFling.ToInt64();
                if (sp && !wasSpaceDown) {
                    lastJumpTriggerUtc = DateTime.UtcNow;
                    WriteCaveParam(cf + OFF_DATA_KISS_MAXY, BitConverter.GetBytes(-100.0f));
                    WriteCaveParam(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(2));
                } else if (!sp && (DateTime.UtcNow - lastJumpTriggerUtc).TotalMilliseconds >= 80.0) {
                    WriteCaveParam(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(0));
                }
                wasSpaceDown = sp;
            } catch { }
        }, null, 20, 20);
    }

    static string SyncWithGameCore(EditorCheatState st) {
        lock (_syncLock) {
        try {
            EnsureFastJumpPollerRunning();
            bool extFlingActive = ExtFlingRunning();

            if (cachedProc != null) {
                bool alive = false;
                if (hProc != IntPtr.Zero) {
                    try {
                        uint code;
                        alive = (GetExitCodeProcess(hProc, out code) && code == 259);
                    } catch { alive = false; }
                } else {
                    try { alive = !cachedProc.HasExited; } catch { alive = false; }
                }
                if (!alive) { Detach(); return "대기 중 (게임 프로세스 종료됨)"; }
            } else {
                if ((DateTime.UtcNow - _lastProcSearchUtc).TotalMilliseconds < 1800) {
                    return "대기 중 (게임 프로세스 대기)";
                }
                _lastProcSearchUtc = DateTime.UtcNow;
                var procs = Process.GetProcessesByName("CrimsonDesert");
                if (procs.Length == 0) { Detach(); return "대기 중 (게임 미실행)"; }
                cachedProc = procs[0];
                _cachedWorkingSet64 = 0;
                _cachedStartTimeUtc = DateTime.MinValue;
                _lastWorldCheckUtc = DateTime.MinValue;
                _cachedWorldSettled = false;
                for (int i = 1; i < procs.Length; i++) procs[i].Dispose();
            }

            if (IsCrashHandlerActive()) {
                if (hProc != IntPtr.Zero && baseAddr != 0) RevertAllHooks();
                return "게임 종료/크래시 핸들러 감지 (후크 안전 일시 정지)";
            }

            Process p = cachedProc;
            int gamePid = p.Id;
            if (_cachedStartTimeUtc == DateTime.MinValue || !_cachedWorldSettled || (DateTime.UtcNow - _lastWorldCheckUtc).TotalMilliseconds >= 2500.0) {
                try {
                    p.Refresh();
                    if (p.HasExited) { Detach(); return "대기 중 (게임 프로세스 종료됨)"; }
                    _cachedWorkingSet64 = p.WorkingSet64;
                    if (_cachedStartTimeUtc == DateTime.MinValue) _cachedStartTimeUtc = p.StartTime.ToUniversalTime();
                } catch { }
                double uptimeSec = _cachedStartTimeUtc != DateTime.MinValue ? (DateTime.UtcNow - _cachedStartTimeUtc).TotalSeconds : 0.0;
                _cachedWorldSettled = IsWorldLoadedAndSettled(gamePid, _cachedWorkingSet64, uptimeSec, out _cachedWorldStatus);
                _lastWorldCheckUtc = DateTime.UtcNow;
            }
            string earlyLoadMsg = _cachedWorldStatus;
            if (!_cachedWorldSettled) {
                if (hProc != IntPtr.Zero && baseAddr != 0) RevertAllHooks();
                return earlyLoadMsg;
            }

            if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero || !trampolinesReady || gamePid != attachedPid) {
                attachedPid = gamePid;
                baseAddr = p.MainModule.BaseAddress.ToInt64();
                if (hProc == IntPtr.Zero) hProc = OpenProcess(PROCESS_ALL_ACCESS, false, gamePid);
                if (hProc == IntPtr.Zero) return "권한 부족 (관리자 권한 실행 필요)";
                if (!InitTrampolines()) return "메모리 트램펄린 초기화 실패";
            }

            long cf = caveFling.ToInt64();

            // 1. Write All Live Parameters Into Data Block (cf + 0x3000..0x31FF)
            int bGodMode = (st.DamageTakenMode >= 4) ? 1 : 0;
            int actualDefVal = 0;
            if (st.DamageTakenMode == 1) actualDefVal = 1428;
            else if (st.DamageTakenMode == 2) actualDefVal = 2500;
            else if (st.DamageTakenMode == 3) actualDefVal = 5000;
            else if (st.DamageTakenMode >= 4) actualDefVal = 999999;
            else if (st.DefenseVal > 0) actualDefVal = st.DefenseVal;
            if (st.WeaponImbueMode == 2 || st.WeaponImbueMode == 5) {
                actualDefVal = Math.Max(actualDefVal + 350, 350);
            }

            int dmgModeVal = 0;
            if (st.DamageDealtMultiplier >= 900.0f) dmgModeVal = 1000;
            else if (st.DamageDealtMultiplier > 1.05f) dmgModeVal = (int)Math.Round(st.DamageDealtMultiplier * 2.0f);

            int pStaMode = Math.Max(0, Math.Min(4, st.PlayerStaminaMode));
            int cdMode = Math.Max(0, Math.Min(4, st.CooldownMode));
            int mStaMode = Math.Max(0, Math.Min(4, st.MountStaminaMode));
            int dmgTakenMode = Math.Max(0, Math.Min(4, st.DamageTakenMode));

            WriteCaveParam(cf + OFF_DATA_GODMODE,        BitConverter.GetBytes(bGodMode));
            WriteCaveParam(cf + OFF_DATA_DMG_TAKEN_MODE, BitConverter.GetBytes(dmgTakenMode));
            WriteCaveParam(cf + OFF_DATA_STAMINA_MODE,   BitConverter.GetBytes(pStaMode));
            WriteCaveParam(cf + OFF_DATA_COOLDOWN_MODE,  BitConverter.GetBytes(cdMode));
            WriteCaveParam(cf + OFF_DATA_MOUNT_STA,      BitConverter.GetBytes(mStaMode));
            WriteCaveParam(cf + OFF_DATA_DEFENSE,        BitConverter.GetBytes(actualDefVal));
            WriteCaveParam(cf + OFF_DATA_DMG_MODE,       BitConverter.GetBytes(dmgModeVal));
            WriteCaveParam(cf + OFF_DATA_MAX_HP,         BitConverter.GetBytes(st.MaxHealthVal));
            WriteCaveParam(cf + OFF_DATA_MAX_STA,        BitConverter.GetBytes(st.MaxStaminaVal));
            WriteCaveParam(cf + OFF_DATA_MAX_SPIRIT,     BitConverter.GetBytes(st.MaxSpiritVal));

            if (extFlingActive) {
                RevertFlingOverlappingHooks();
                WriteFlingCtl(FLING_ADDR_GODMODE,   BitConverter.GetBytes(bGodMode));
                WriteFlingCtl(FLING_ADDR_STAMINA,   BitConverter.GetBytes(pStaMode > 0 ? 1 : 0));
                WriteFlingCtl(FLING_ADDR_SPIRIT,    BitConverter.GetBytes(cdMode > 0 ? 1 : 0));
                WriteFlingCtl(FLING_ADDR_MOUNT_STA, BitConverter.GetBytes(mStaMode > 0 ? 1 : 0));
                WriteFlingCtl(FLING_ADDR_DEFENSE,   BitConverter.GetBytes(actualDefVal > 0 ? Math.Max(1, actualDefVal / 100) : 0));
                WriteFlingCtl(FLING_ADDR_DMG,       BitConverter.GetBytes(dmgModeVal));
            }

            // Craft & Gear & Imbue
            WriteCaveParam(cf + OFF_DATA_DUR_MODE,   BitConverter.GetBytes(Math.Max(0, Math.Min(4, st.DurabilityMode))));
            WriteCaveParam(cf + OFF_DATA_CRAFT_COST, BitConverter.GetBytes(Math.Max(0, Math.Min(4, st.CraftMaterialCost))));
            WriteCaveParam(cf + OFF_DATA_CRAFT_SUCC, BitConverter.GetBytes(Math.Max(0, Math.Min(4, st.CraftGreatSuccess))));
            WriteCaveParam(cf + OFF_DATA_IMBUE_MODE, BitConverter.GetBytes(Math.Max(0, Math.Min(5, st.WeaponImbueMode))));
            WriteCaveParam(cf + OFF_DATA_GEAR_HIDER, BitConverter.GetBytes(Math.Max(0, Math.Min(3, st.GearHiderMode))));

            // Crime, Stealth, Pickpocket, Bounty, Bag, Special
            WriteCaveParam(cf + OFF_DATA_THIEF_DET,   BitConverter.GetBytes(Math.Max(0, Math.Min(3, st.ThiefDetectionMode))));
            WriteCaveParam(cf + OFF_DATA_PICKPOCKET,  BitConverter.GetBytes(Math.Max(0, Math.Min(3, st.PickpocketSuccessMode))));
            WriteCaveParam(cf + OFF_DATA_BOUNTY_MODE, BitConverter.GetBytes(Math.Max(0, Math.Min(3, st.BountyImmunityMode))));
            WriteCaveParam(cf + OFF_DATA_THIEF_GLOVE, BitConverter.GetBytes(Math.Max(0, Math.Min(3, st.ThiefGloveMode))));
            WriteCaveParam(cf + OFF_DATA_BAG_EXPAND,  BitConverter.GetBytes(Math.Max(0, st.BagExpandSlots)));
            WriteCaveParam(cf + OFF_DATA_SPEC_CONV,   BitConverter.GetBytes(Math.Max(0, Math.Min(3, st.SpecialConvenienceMode))));

            // Taming
            float tameMult = 1.0f;
            int tameInst = 0;
            if (st.AnimalTamingMode == 1) tameMult = 1.5f;
            else if (st.AnimalTamingMode == 2) tameMult = 3.0f;
            else if (st.AnimalTamingMode == 3) tameMult = 5.0f;
            else if (st.AnimalTamingMode >= 4) { tameMult = 1.0f; tameInst = 1; }
            WriteCaveParam(cf + OFF_DATA_TAMING_MULT, BitConverter.GetBytes(tameMult));
            WriteCaveParam(cf + OFF_DATA_TAMING_INST, BitConverter.GetBytes(tameInst));

            // Breath
            float breathMult = 1.0f;
            if (st.UnderwaterOxygenMode == 1) breathMult = 2.0f;
            else if (st.UnderwaterOxygenMode == 2) breathMult = 3.0f;
            else if (st.UnderwaterOxygenMode == 3) breathMult = 5.0f;
            else if (st.UnderwaterOxygenMode >= 4) breathMult = 100.0f;
            WriteCaveParam(cf + OFF_DATA_BREATH_MODE, BitConverter.GetBytes(Math.Max(0, Math.Min(4, st.UnderwaterOxygenMode))));
            WriteCaveParam(cf + OFF_DATA_BREATH_MULT, BitConverter.GetBytes(breathMult));

            // Romance & EXP (strictly separate!)
            float romMult = (st.RomanceMultiplier >= 90.0f) ? 100.0f : Math.Max(1.0f, st.RomanceMultiplier);
            WriteCaveParam(cf + OFF_DATA_ROMANCE_MULT, BitConverter.GetBytes(romMult));

            float expM = Math.Max(1.0f, Math.Min(100.0f, st.ExpMultiplier));
            ExpMult = expM;
            WriteCaveParam(cf + OFF_DATA_EXP_MULT, BitConverter.GetBytes(expM));

            // Speed, Underwater Swim Speed, Mount Speed, Jump & Flight State Machine
            // Never override effSpeed with UnderwaterSpeedMultiplier or MountSpeedMultiplier (fixes abnormal default speed!)
            float effSpeed = Math.Max(1.0f, Math.Min(3.0f, st.SpeedMultiplier));
            WriteCaveParam(cf + OFF_DATA_KISS_MULT, BitConverter.GetBytes(effSpeed));
            WriteCaveParam(cf + OFF_DATA_SWIM_MULT, BitConverter.GetBytes(Math.Max(1.0f, st.UnderwaterSpeedMultiplier)));
            WriteCaveParam(cf + OFF_DATA_MOUNT_SPEED, BitConverter.GetBytes(Math.Max(1.0f, st.MountSpeedMultiplier)));

            float jumpScale = Math.Max(0.0f, st.JumpMultiplier - 1.0f);
            WriteCaveParam(cf + OFF_DATA_KISS_JUMPSC, BitConverter.GetBytes(jumpScale));

            int sleepHeal = (DateTime.UtcNow < sleepHealUntilUtc) ? 1 : 0;
            WriteCaveParam(cf + OFF_DATA_SLEEP_TRIG, BitConverter.GetBytes(sleepHeal));

            bool freeFlightActive = (st.SpecialConvenienceMode == 3);
            int baseKissState = freeFlightActive ? 4 : 0;
            float clampYVal = freeFlightActive ? -0.05f : -1.5f;
            WriteCaveParam(cf + OFF_DATA_KISS_CLAMPY, BitConverter.GetBytes(clampYVal));

            // Poll in-game keyboard only while CrimsonDesert is foreground
            bool isGameFg = false;
            try {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero) {
                    uint fgPid;
                    GetWindowThreadProcessId(fg, out fgPid);
                    isGameFg = (fgPid == gamePid);
                }
            } catch { }

            bool spaceNow = isGameFg && ((GetAsyncKeyState(0x20) & 0x8000) != 0);
            bool ctrlOrC = isGameFg && (((GetAsyncKeyState(0xA2) & 0x8000) != 0) || ((GetAsyncKeyState(0x43) & 0x8000) != 0));
            if (spaceNow && !wasSpaceDown && st.JumpMultiplier > 1.05f && !freeFlightActive) {
                lastJumpTriggerUtc = DateTime.UtcNow;
                WriteCaveParam(cf + OFF_DATA_KISS_MAXY, BitConverter.GetBytes(-100.0f));
                WriteCaveParam(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(2)); // One-shot jump impulse (auto-reset in KISS asm!)
            }
            wasSpaceDown = spaceNow;

            if (freeFlightActive) {
                float flyY = spaceNow ? 8.0f : (ctrlOrC ? -6.0f : 0.0f);
                WriteCaveParam(cf + OFF_DATA_FLY_LIFTY, BitConverter.GetBytes(flyY));
                WriteCaveParam(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(4));
            } else {
                WriteCaveParam(cf + OFF_DATA_FLY_LIFTY, BitConverter.GetBytes(0.0f));
                if ((DateTime.UtcNow - lastJumpTriggerUtc).TotalMilliseconds >= 80.0) {
                    WriteCaveParam(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(baseKissState));
                }
            }

            // 2. Compute Need-State for Every Single Verified Hook Site
            bool haveCorePtrs = IsValidGameHeapPtr(_lastValidPlayerPtr)
                             && IsValidGameHeapPtr(_lastValidStaPtr)
                             && IsValidGameHeapPtr(_lastValidSpiPtr);
            bool needStatsFeature = (bGodMode != 0 || dmgTakenMode != 0 || pStaMode != 0 || cdMode != 0 || mStaMode != 0
                                  || st.MaxHealthVal > 0 || st.MaxStaminaVal > 0 || st.MaxSpiritVal > 0
                                  || st.WeaponImbueMode >= 4 || sleepHeal != 0);
            bool needDefense = !extFlingActive && (actualDefVal > 0);
            bool needDmg = !extFlingActive && (dmgModeVal > 0 || st.WeaponImbueMode > 0);
            bool needPlayer = !extFlingActive && (!haveCorePtrs || needStatsFeature || needDefense || needDmg);
            bool needStats = !extFlingActive && (!haveCorePtrs || needStatsFeature);
            bool needCommit = (bGodMode != 0 || pStaMode != 0 || cdMode != 0)
                           && (IsValidGameHeapPtr(_lastValidStaPtr) || IsValidGameHeapPtr(_lastValidSpiPtr) || IsValidGameHeapPtr(_lastValidHpPtr));

            byte[] pendingGoldB = ReadBytes(cf + OFF_DATA_PEND_GOLD, 8);
            long pendingGoldVal = pendingGoldB != null ? BitConverter.ToInt64(pendingGoldB, 0) : 0;
            byte[] editHoverB = ReadBytes(cf + OFF_DATA_EDIT_AMT, 8);
            long editHoverVal = editHoverB != null ? BitConverter.ToInt64(editHoverB, 0) : 0;
            long invMgrVal = ReadI64(cf + OFF_DATA_INV_MGR_PTR);
            long bagContVal = ReadI64(cf + OFF_DATA_BAG_CONT_PTR);
            bool silverPtrValid = IsValidGameHeapPtr(ReadI64(cf + OFF_DATA_SILVER_PTR));
            bool needSaveItem = !extFlingActive && (pendingGoldVal > 0 || editHoverVal > 0 || st.CraftMaterialCost > 0 || !silverPtrValid);
            bool needSelectedItem = !extFlingActive && (pendingGoldVal > 0 || editHoverVal > 0 || !silverPtrValid);

            bool needKiss = (effSpeed > 1.01f || st.JumpMultiplier > 1.05f || freeFlightActive);
            bool needDaytime = !extFlingActive && (st.FreezeDaytime || FreezeDaytimeEnabled);
            bool needExp = (expM > 1.001f);
            bool needDurability = (st.DurabilityMode > 0);
            bool needCrime = (st.ThiefDetectionMode > 0 || st.PickpocketSuccessMode > 0 || st.BountyImmunityMode > 0 || st.ThiefGloveMode == 3);
            bool needTaming = (st.AnimalTamingMode > 0);
            bool needBreath = (st.UnderwaterOxygenMode > 0);
            bool needSlotExpand = (st.BagExpandSlots > 0 || invMgrVal == 0 || pendingGoldVal > 0 || !silverPtrValid);
            bool needRomance = (st.RomanceMultiplier > 1.05f);
            bool needCountItems = (st.CraftMaterialCost > 0 || invMgrVal == 0 || pendingGoldVal > 0 || !silverPtrValid);
            bool needWanted = (st.BountyImmunityMode > 0 || st.ThiefGloveMode == 3);

            if (invMgrVal != 0 || bagContVal != 0 || _lastValidInvMgrPtr != 0 || _lastValidBagContPtr != 0) {
                bool doSanitize = (_arrowSanitizedPid != attachedPid);
                bool needFullBagScan = doSanitize || pendingGoldVal > 0 || !silverPtrValid || (DateTime.UtcNow - _lastFullSilverScanUtc).TotalSeconds >= 12.0;
                if (needFullBagScan) {
                    long foundSPtr, foundSil;
                    TryFindPlayerBagSilverRecord(out foundSPtr, out foundSil, doSanitize);
                    _lastFullSilverScanUtc = DateTime.UtcNow;
                    if (doSanitize) _arrowSanitizedPid = attachedPid;
                }
            }

            // Clean up any leftover orphan EXP / COMMIT hooks from previous launcher crash
            if (!expOrphanChecked && !needExp) {
                expOrphanChecked = true;
                if (IsOwnHookAt(baseAddr + RVA_EXP, VANILLA_EXP, 0)) {
                    WriteBytes(baseAddr + RVA_EXP, VANILLA_EXP);
                    EngLog("EXP orphan hook restored to vanilla");
                }
            }
            if (!commitOrphanChecked && !needCommit) {
                commitOrphanChecked = true;
                if (IsOwnHookAt(baseAddr + RVA_COMMIT, VANILLA_COMMIT, 0)) {
                    WriteBytes(baseAddr + RVA_COMMIT, VANILLA_COMMIT);
                    EngLog("COMMIT orphan hook restored to vanilla");
                }
            }

            // 3. Synchronize All 21 Hook Sites Strictly On-Demand (7 FLiNG AOB sites yielded when extFlingActive)
            SyncSingleHook("PLAYER",        needPlayer,       ref hookPlayerInstalled,       baseAddr + RVA_PLAYER,        cf + OFF_CODE_PLAYER,        VANILLA_PLAYER);
            SyncSingleHook("STATS",         needStats,        ref hookStatsInstalled,        baseAddr + RVA_STATS,         cf + OFF_CODE_STATS,         VANILLA_STATS);
            SyncSingleHook("COMMIT",        needCommit,       ref hookCommitInstalled,       baseAddr + RVA_COMMIT,        cf + OFF_CODE_COMMIT,        VANILLA_COMMIT);
            SyncSingleHook("DEFENSE",       needDefense,      ref hookDefenseInstalled,      baseAddr + RVA_DEFENSE,       cf + OFF_CODE_DEFENSE,       VANILLA_DEFENSE);
            SyncSingleHook("DMG",           needDmg,          ref hookDmgInstalled,          baseAddr + RVA_DMG,           cf + OFF_CODE_DMG,           VANILLA_DMG);
            SyncSingleHook("SAVE_ITEM",     needSaveItem,     ref hookSaveItemInstalled,     baseAddr + RVA_SAVE_ITEM,     cf + OFF_CODE_SAVE_ITEM,     VANILLA_SAVE_ITEM);
            SyncSingleHook("SELECTED_ITEM", needSelectedItem, ref hookSelectedItemInstalled, baseAddr + RVA_SELECTED_ITEM, cf + OFF_CODE_SELECTED_ITEM, VANILLA_SELECTED_ITEM);
            SyncSingleHook("KISS",          needKiss,         ref hookKissInstalled,         baseAddr + RVA_KISS,          cf + OFF_CODE_KISS,          VANILLA_KISS);
            SyncSingleHook("DAYTIME",       needDaytime,      ref hookDaytimeInstalled,      baseAddr + RVA_DAYTIME,       cf + OFF_CODE_DAYTIME,       VANILLA_DAYTIME);
            SyncSingleHook("EXP",           needExp,          ref hookExpInstalled,          baseAddr + RVA_EXP,           cf + OFF_CODE_EXP,           VANILLA_EXP);
            SyncSingleHook("DURABILITY",    needDurability,   ref hookDurabilityInstalled,   baseAddr + RVA_DURABILITY,    cf + OFF_CODE_DURABILITY,    VANILLA_DURABILITY);
            SyncSingleHook("CRIME_EVAL",    needCrime,        ref hookCrimeEvalInstalled,    baseAddr + RVA_CRIME_EVAL,    cf + OFF_CODE_CRIME_EVAL,    VANILLA_CRIME_EVAL);
            SyncSingleHook("HORSE_TAMING",  needTaming,       ref hookHorseTamingInstalled,  baseAddr + RVA_HORSE_TAMING,  cf + OFF_CODE_HORSE_TAMING,  VANILLA_HORSE_TAMING);
            SyncSingleHook("BREATH_VITALS", needBreath,       ref hookBreathVitalsInstalled, baseAddr + RVA_BREATH_VITALS, cf + OFF_CODE_BREATH_VITALS, VANILLA_BREATH_VITALS);
            SyncSingleHook("SLOT_EXPAND",   needSlotExpand,   ref hookSlotExpandInstalled,   baseAddr + RVA_SLOT_EXPAND,   cf + OFF_CODE_SLOT_EXPAND,   VANILLA_SLOT_EXPAND);
            SyncSingleHook("ROMANCE_TRUST", needRomance,      ref hookRomanceTrustInstalled, baseAddr + RVA_ROMANCE_TRUST, cf + OFF_CODE_ROMANCE_TRUST, VANILLA_ROMANCE_TRUST);
            SyncSingleHook("FRIENDLY_DAILY",needRomance,      ref hookFriendlyDailyInstalled,baseAddr + RVA_FRIENDLY_DAILY,cf + OFF_CODE_FRIENDLY_DAILY,VANILLA_FRIENDLY_DAILY);
            SyncSingleHook("AFFINITY_ADD",  needRomance,      ref hookAffinityAddInstalled,  baseAddr + RVA_AFFINITY_ADD,  cf + OFF_CODE_AFFINITY_ADD,  VANILLA_AFFINITY_ADD);
            SyncSingleHook("FRIENDLY_ADD",  needRomance,      ref hookFriendlyAddInstalled,  baseAddr + RVA_FRIENDLY_ADD,  cf + OFF_CODE_FRIENDLY_ADD,  VANILLA_FRIENDLY_ADD);
            SyncSingleHook("COUNT_ITEMS",   needCountItems,   ref hookCountItemsInstalled,   baseAddr + RVA_COUNT_ITEMS,   cf + OFF_CODE_COUNT_ITEMS,   VANILLA_COUNT_ITEMS);
            SyncSingleHook("WANTED_EVAL",   needWanted,       ref hookWantedEvalInstalled,   baseAddr + RVA_WANTED_EVAL,   cf + OFF_CODE_WANTED_EVAL,   VANILLA_WANTED_EVAL);

            if (needCommit) {
                byte[] nb = ReadBytes(cf + OFF_DATA_COMMIT_CNT, 4);
                if (nb != null) {
                    int n = BitConverter.ToInt32(nb, 0);
                    long nowT = DateTime.UtcNow.Ticks;
                    if (n != lastCommitCount && (nowT - lastCommitLogTicks) > 30000000L) {
                        EngLog("COMMIT overrides: " + n);
                        lastCommitCount = n;
                        lastCommitLogTicks = nowT;
                    }
                }
            }

            int activeCount = GetActiveFeatureCount(st);
            int selId = 0; long selCnt = 0;
            long liveSilver = GetLiveSilverAmount();
            DateTime nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastHealthReadbackUtc).TotalMilliseconds >= 150.0 || activeCount != _lastHealthActiveCount || liveSilver != _lastHealthSilver) {
                _lastHealthReadbackUtc = nowUtc;
                _lastHealthActiveCount = activeCount;
                _lastHealthSilver = liveSilver;
                GetSelectedItemInfo(out selId, out selCnt);
                WriteLiveEditorHealth(cf, activeCount, earlyLoadMsg, liveSilver);
            }
            string silverStr = (liveSilver > 100) ? string.Format("은화 {0:N2}G", liveSilver / 100.0) : (selId > 0 ? string.Format("아이템[ID:{0}] {1:N0}", selId, selCnt) : "인게임 연결됨");

            if (activeCount == 0) {
                return string.Format("인게임 순정 대기 (PID {0}, {1})", attachedPid, silverStr);
            }
            return string.Format("실시간 후크 가동 (PID {0}, {1}종 활성, {2})", attachedPid, activeCount, silverStr);
        } catch (Exception ex) {
            EngLog("EXC in SyncWithGameCore: " + ex);
            return "메모리 동기화 대기";
        }
        }
    }

    static void WriteLiveEditorHealth(long cf, int activeCount, string loadStatus, long liveSilver) {
        try {
            byte[] d = ReadBytes(cf + DATA_BASE_OFF, 0x200);
            if (d == null || d.Length < 0x1C0) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            long playerPtr = BitConverter.ToInt64(d, (int)(OFF_DATA_PLAYER - DATA_BASE_OFF));
            long staPtr = BitConverter.ToInt64(d, (int)(OFF_DATA_P_STA_PTR - DATA_BASE_OFF));
            long spiPtr = BitConverter.ToInt64(d, (int)(OFF_DATA_P_SPI_PTR - DATA_BASE_OFF));
            long hpPtr  = BitConverter.ToInt64(d, (int)(OFF_DATA_P_HP_PTR - DATA_BASE_OFF));
            long invMgrPtr = BitConverter.ToInt64(d, (int)(OFF_DATA_INV_MGR_PTR - DATA_BASE_OFF));
            long bagContPtr = BitConverter.ToInt64(d, (int)(OFF_DATA_BAG_CONT_PTR - DATA_BASE_OFF));

            if (IsValidGameHeapPtr(playerPtr)) {
                _lastValidPlayerPtr = playerPtr;
            } else if (IsValidGameHeapPtr(_lastValidPlayerPtr)) {
                playerPtr = _lastValidPlayerPtr;
                WriteBytes(cf + OFF_DATA_PLAYER, BitConverter.GetBytes(playerPtr));
            }
            if (!IsValidGameHeapPtr(staPtr) || !IsValidGameHeapPtr(spiPtr) || !IsValidGameHeapPtr(hpPtr)) {
                if (IsValidGameHeapPtr(playerPtr)) {
                    long statComp = ReadI64(playerPtr + 0x18);
                    long statArr = IsValidGameHeapPtr(statComp) ? ReadI64(statComp + 0x58) : 0;
                    if (IsValidGameHeapPtr(statArr)) {
                        for (int k = 0; k < 24; k++) {
                            long rec = statArr + k * 0x30L;
                            ushort stId = ReadU16(rec);
                            long mx = ReadI64(rec + 0x18);
                            if (mx <= 0 || mx > 1000000000L) continue;
                            if (stId == 0 && !IsValidGameHeapPtr(hpPtr)) hpPtr = rec;
                            else if (stId == 17 && !IsValidGameHeapPtr(staPtr)) staPtr = rec;
                            else if (stId == 18 && !IsValidGameHeapPtr(spiPtr)) spiPtr = rec;
                        }
                        if (!IsValidGameHeapPtr(hpPtr) && ReadI64(statArr + 0x18L) > 0) hpPtr = statArr;
                        if (!IsValidGameHeapPtr(staPtr) && ReadI64(statArr + 0x30L + 0x18L) > 0) staPtr = statArr + 0x30L;
                        if (!IsValidGameHeapPtr(spiPtr)) {
                            long candSpi = IsValidGameHeapPtr(staPtr) ? (staPtr + 0x30L) : (statArr + 0x60L);
                            if (ReadI64(candSpi + 0x18L) > 0) spiPtr = candSpi;
                        }
                    }
                }
            }
            if (IsValidGameHeapPtr(hpPtr)) {
                _lastValidHpPtr = hpPtr;
                WriteBytes(cf + OFF_DATA_P_HP_PTR, BitConverter.GetBytes(hpPtr));
            } else if (IsValidGameHeapPtr(_lastValidHpPtr)) {
                hpPtr = _lastValidHpPtr;
                WriteBytes(cf + OFF_DATA_P_HP_PTR, BitConverter.GetBytes(hpPtr));
            }
            if (IsValidGameHeapPtr(staPtr)) {
                _lastValidStaPtr = staPtr;
                WriteBytes(cf + OFF_DATA_P_STA_PTR, BitConverter.GetBytes(staPtr));
            } else if (IsValidGameHeapPtr(_lastValidStaPtr)) {
                staPtr = _lastValidStaPtr;
                WriteBytes(cf + OFF_DATA_P_STA_PTR, BitConverter.GetBytes(staPtr));
            }
            if (IsValidGameHeapPtr(spiPtr)) {
                _lastValidSpiPtr = spiPtr;
                WriteBytes(cf + OFF_DATA_P_SPI_PTR, BitConverter.GetBytes(spiPtr));
            } else if (IsValidGameHeapPtr(_lastValidSpiPtr)) {
                spiPtr = _lastValidSpiPtr;
                WriteBytes(cf + OFF_DATA_P_SPI_PTR, BitConverter.GetBytes(spiPtr));
            }
            if (IsValidGameHeapPtr(invMgrPtr)) _lastValidInvMgrPtr = invMgrPtr;
            else if (IsValidGameHeapPtr(_lastValidInvMgrPtr)) invMgrPtr = _lastValidInvMgrPtr;
            if (IsValidGameHeapPtr(bagContPtr)) _lastValidBagContPtr = bagContPtr;
            else if (IsValidGameHeapPtr(_lastValidBagContPtr)) bagContPtr = _lastValidBagContPtr;
            int hMountSeen = BitConverter.ToInt32(d, (int)(OFF_DATA_MOUNT_SEEN - DATA_BASE_OFF));
            int hCommit = BitConverter.ToInt32(d, (int)(OFF_DATA_COMMIT_CNT - DATA_BASE_OFF));
            int hDur    = BitConverter.ToInt32(d, (int)(OFF_DATA_DUR_HITS - DATA_BASE_OFF));
            int hCrime  = BitConverter.ToInt32(d, (int)(OFF_DATA_CRIME_HITS - DATA_BASE_OFF));
            int hTame   = BitConverter.ToInt32(d, (int)(OFF_DATA_TAMING_HITS - DATA_BASE_OFF));
            int hBreath = BitConverter.ToInt32(d, (int)(OFF_DATA_BREATH_HITS - DATA_BASE_OFF));
            int hSlot   = BitConverter.ToInt32(d, (int)(OFF_DATA_SLOT_HITS - DATA_BASE_OFF));
            int hSave   = BitConverter.ToInt32(d, (int)(OFF_DATA_SAVE_HITS - DATA_BASE_OFF));
            int hSel    = BitConverter.ToInt32(d, (int)(OFF_DATA_SEL_HITS - DATA_BASE_OFF));
            int hRom    = BitConverter.ToInt32(d, (int)(OFF_DATA_ROM_HITS - DATA_BASE_OFF));
            int hCnt    = BitConverter.ToInt32(d, (int)(OFF_DATA_CNT_HITS - DATA_BASE_OFF));
            int hWanted = BitConverter.ToInt32(d, (int)(OFF_DATA_WANTED_HITS - DATA_BASE_OFF));

            // Read back live cave control values for smart verification & RSI telemetry
            int cvGod        = BitConverter.ToInt32(d, (int)(OFF_DATA_GODMODE - DATA_BASE_OFF));
            int cvSta        = BitConverter.ToInt32(d, (int)(OFF_DATA_STAMINA_MODE - DATA_BASE_OFF));
            int cvCd         = BitConverter.ToInt32(d, (int)(OFF_DATA_COOLDOWN_MODE - DATA_BASE_OFF));
            int cvMsta       = BitConverter.ToInt32(d, (int)(OFF_DATA_MOUNT_STA - DATA_BASE_OFF));
            int cvDef        = BitConverter.ToInt32(d, (int)(OFF_DATA_DEFENSE - DATA_BASE_OFF));
            int cvDmg        = BitConverter.ToInt32(d, (int)(OFF_DATA_DMG_MODE - DATA_BASE_OFF));
            long cvPendGold  = BitConverter.ToInt64(d, (int)(OFF_DATA_PEND_GOLD - DATA_BASE_OFF));
            float cvSpeed    = BitConverter.ToSingle(d, (int)(OFF_DATA_KISS_MULT - DATA_BASE_OFF));
            int cvKissState  = BitConverter.ToInt32(d, (int)(OFF_DATA_KISS_STATE - DATA_BASE_OFF));
            float cvJumpSc   = BitConverter.ToSingle(d, (int)(OFF_DATA_KISS_JUMPSC - DATA_BASE_OFF));
            float cvSwim     = BitConverter.ToSingle(d, (int)(OFF_DATA_SWIM_MULT - DATA_BASE_OFF));
            int cvFreeze     = BitConverter.ToInt32(d, (int)(OFF_DATA_FREEZE_TIME - DATA_BASE_OFF));
            float cvExp      = BitConverter.ToSingle(d, (int)(OFF_DATA_EXP_MULT - DATA_BASE_OFF));
            int cvThiefDet   = BitConverter.ToInt32(d, (int)(OFF_DATA_THIEF_DET - DATA_BASE_OFF));
            int cvPick       = BitConverter.ToInt32(d, (int)(OFF_DATA_PICKPOCKET - DATA_BASE_OFF));
            int cvBounty     = BitConverter.ToInt32(d, (int)(OFF_DATA_BOUNTY_MODE - DATA_BASE_OFF));
            int cvDur        = BitConverter.ToInt32(d, (int)(OFF_DATA_DUR_MODE - DATA_BASE_OFF));
            float cvTameMult = BitConverter.ToSingle(d, (int)(OFF_DATA_TAMING_MULT - DATA_BASE_OFF));
            int cvTameInst   = BitConverter.ToInt32(d, (int)(OFF_DATA_TAMING_INST - DATA_BASE_OFF));
            int cvBreathMode = BitConverter.ToInt32(d, (int)(OFF_DATA_BREATH_MODE - DATA_BASE_OFF));
            int cvBag        = BitConverter.ToInt32(d, (int)(OFF_DATA_BAG_EXPAND - DATA_BASE_OFF));
            int cvCraftCost  = BitConverter.ToInt32(d, (int)(OFF_DATA_CRAFT_COST - DATA_BASE_OFF));
            int cvCraftSucc  = BitConverter.ToInt32(d, (int)(OFF_DATA_CRAFT_SUCC - DATA_BASE_OFF));
            int cvImbue      = BitConverter.ToInt32(d, (int)(OFF_DATA_IMBUE_MODE - DATA_BASE_OFF));
            int cvSpec       = BitConverter.ToInt32(d, (int)(OFF_DATA_SPEC_CONV - DATA_BASE_OFF));
            float cvRom      = BitConverter.ToSingle(d, (int)(OFF_DATA_ROMANCE_MULT - DATA_BASE_OFF));
            int cvGear       = BitConverter.ToInt32(d, (int)(OFF_DATA_GEAR_HIDER - DATA_BASE_OFF));
            int cvGlove      = BitConverter.ToInt32(d, (int)(OFF_DATA_THIEF_GLOVE - DATA_BASE_OFF));
            float cvMspd     = BitConverter.ToSingle(d, (int)(OFF_DATA_MOUNT_SPEED - DATA_BASE_OFF));

            var hooks = new List<string>();
            if (hookPlayerInstalled)       hooks.Add("\"PLAYER\"");
            if (hookStatsInstalled)        hooks.Add("\"STATS\"");
            if (hookCommitInstalled)       hooks.Add("\"COMMIT\"");
            if (hookDefenseInstalled)      hooks.Add("\"DEFENSE\"");
            if (hookDmgInstalled)          hooks.Add("\"DMG\"");
            if (hookSaveItemInstalled)     hooks.Add("\"SAVE_ITEM\"");
            if (hookSelectedItemInstalled) hooks.Add("\"SELECTED_ITEM\"");
            if (hookKissInstalled)         hooks.Add("\"KISS\"");
            if (hookDaytimeInstalled)      hooks.Add("\"DAYTIME\"");
            if (hookExpInstalled)          hooks.Add("\"EXP\"");
            if (hookDurabilityInstalled)   hooks.Add("\"DURABILITY\"");
            if (hookCrimeEvalInstalled)    hooks.Add("\"CRIME_EVAL\"");
            if (hookHorseTamingInstalled)  hooks.Add("\"HORSE_TAMING\"");
            if (hookBreathVitalsInstalled) hooks.Add("\"BREATH_VITALS\"");
            if (hookSlotExpandInstalled)   hooks.Add("\"SLOT_EXPAND\"");
            if (hookRomanceTrustInstalled) hooks.Add("\"ROMANCE_TRUST\"");
            if (hookFriendlyDailyInstalled) hooks.Add("\"FRIENDLY_DAILY\"");
            if (hookAffinityAddInstalled)  hooks.Add("\"AFFINITY_ADD\"");
            if (hookFriendlyAddInstalled)  hooks.Add("\"FRIENDLY_ADD\"");
            if (hookCountItemsInstalled)   hooks.Add("\"COUNT_ITEMS\"");
            if (hookWantedEvalInstalled)   hooks.Add("\"WANTED_EVAL\"");

            string hooksJoined = string.Join(", ", hooks.ToArray());
            string caveValuesJson = string.Format(
                inv,
                "{{\"god\": {0}, \"sta\": {1}, \"cd\": {2}, \"msta\": {3}, \"def\": {4}, \"dmg\": {5}, " +
                "\"pendGold\": {6}, \"speed\": {7:0.###}, \"kissState\": {8}, \"jumpSc\": {9:0.###}, " +
                "\"swim\": {10:0.###}, \"freeze\": {11}, \"exp\": {12:0.###}, \"thiefDet\": {13}, \"pick\": {14}, " +
                "\"bounty\": {15}, \"dur\": {16}, \"tameMult\": {17:0.###}, \"tameInst\": {18}, \"breathMode\": {19}, " +
                "\"bag\": {20}, \"craftCost\": {21}, \"craftSucc\": {22}, \"imbue\": {23}, \"spec\": {24}, " +
                "\"rom\": {25:0.###}, \"gear\": {26}, \"glove\": {27}, \"mspd\": {28:0.###}}}",
                cvGod, cvSta, cvCd, cvMsta, cvDef, cvDmg, cvPendGold, cvSpeed, cvKissState, cvJumpSc,
                cvSwim, cvFreeze, cvExp, cvThiefDet, cvPick, cvBounty, cvDur, cvTameMult, cvTameInst, cvBreathMode,
                cvBag, cvCraftCost, cvCraftSucc, cvImbue, cvSpec, cvRom, cvGear, cvGlove, cvMspd
            );

            string stateSig = string.Format(
                inv,
                "{0}|{1}|{2}|{3}|{4}",
                activeCount,
                (playerPtr >= 0x10000000L && (playerPtr >> 47) == 0) ? 1 : 0,
                liveSilver,
                hooksJoined.Length,
                caveValuesJson
            );

            if (stateSig == _lastHealthSignature && (DateTime.UtcNow - _lastHealthWriteUtc).TotalSeconds < 5.0) {
                return;
            }
            _lastHealthSignature = stateSig;
            _lastHealthWriteUtc = DateTime.UtcNow;

            string safeLoad = (loadStatus ?? "").Replace("\\", "\\\\").Replace("\"", "'");
            string json = string.Format(
                inv,
                "{{\r\n" +
                "  \"timestamp\": \"{0}\",\r\n" +
                "  \"attached_pid\": {1},\r\n" +
                "  \"cave_addr\": \"0x{2:X}\",\r\n" +
                "  \"world_loaded\": true,\r\n" +
                "  \"load_status\": \"{3}\",\r\n" +
                "  \"active_feature_count\": {4},\r\n" +
                "  \"player_ptr_valid\": {5},\r\n" +
                "  \"sta_ptr_valid\": {6},\r\n" +
                "  \"spi_ptr_valid\": {7},\r\n" +
                "  \"player_ptr\": \"0x{22:X}\",\r\n" +
                "  \"sta_ptr\": \"0x{23:X}\",\r\n" +
                "  \"spi_ptr\": \"0x{24:X}\",\r\n" +
                "  \"inv_mgr_ptr\": \"0x{25:X}\",\r\n" +
                "  \"bag_cont_ptr\": \"0x{26:X}\",\r\n" +
                "  \"mount_seen\": {27},\r\n" +
                "  \"fast_jump_poller_active\": {28},\r\n" +
                "  \"fast_jump_poll_interval_ms\": 20,\r\n" +
                "  \"fast_jump_poll_ticks\": {29},\r\n" +
                "  \"live_silver\": {8},\r\n" +
                "  \"installed_hooks\": [{9}],\r\n" +
                "  \"hit_counters\": {{\"commit\": {10}, \"dur\": {11}, \"crime\": {12}, \"tame\": {13}, \"breath\": {14}, \"slot\": {15}, \"save\": {16}, \"sel\": {17}, \"rom\": {18}, \"cnt\": {19}, \"wanted\": {20}}},\r\n" +
                "  \"cave_values\": {21}\r\n" +
                "}}\r\n",
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", inv),
                attachedPid,
                cf,
                safeLoad,
                activeCount,
                (playerPtr >= 0x10000000L && (playerPtr >> 47) == 0) ? "true" : "false",
                (staPtr >= 0x10000000L && (staPtr >> 47) == 0) ? "true" : "false",
                (spiPtr >= 0x10000000L && (spiPtr >> 47) == 0) ? "true" : "false",
                liveSilver,
                hooksJoined,
                hCommit, hDur, hCrime, hTame, hBreath, hSlot, hSave, hSel, hRom, hCnt, hWanted,
                caveValuesJson,
                playerPtr, staPtr, spiPtr, invMgrPtr, bagContPtr, hMountSeen,
                (_fastJumpPollTimer != null) ? "true" : "false",
                System.Threading.Interlocked.Read(ref _fastJumpPollTicks)
            );

            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string rsiDir = Directory.Exists(Path.Combine(baseDir, "tuning"))
                ? Path.Combine(baseDir, @"tuning\research\rsi_telemetry")
                : Path.Combine(baseDir, @"research\rsi_telemetry");
            Directory.CreateDirectory(rsiDir);
            string dst = Path.Combine(rsiDir, "live_editor_health.json");
            string tmp = dst + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Copy(tmp, dst, true);
            File.Delete(tmp);
        } catch { }
    }

    static int GetActiveFeatureCount(EditorCheatState st) {
        int c = 0;
        if (st.DamageTakenMode > 0) c++;
        if (st.DamageDealtMultiplier > 1.05f) c++;
        if (st.PlayerStaminaMode > 0) c++;
        if (st.CooldownMode > 0) c++;
        if (st.ExpMultiplier > 1.05f) c++;
        if (st.SpeedMultiplier > 1.01f) c++;
        if (st.JumpMultiplier > 1.05f) c++;
        if (st.RomanceMultiplier > 1.05f) c++;
        if (st.AnimalTamingMode > 0) c++;
        if (st.MountSpeedMultiplier > 1.01f) c++;
        if (st.MountStaminaMode > 0) c++;
        if (st.UnderwaterOxygenMode > 0) c++;
        if (st.UnderwaterSpeedMultiplier > 1.01f) c++;
        if (st.CraftMaterialCost > 0) c++;
        if (st.CraftGreatSuccess > 0) c++;
        if (st.DurabilityMode > 0) c++;
        if (st.GearHiderMode > 0) c++;
        if (st.WeaponImbueMode > 0) c++;
        if (st.ThiefDetectionMode > 0) c++;
        if (st.PickpocketSuccessMode > 0) c++;
        if (st.BountyImmunityMode > 0) c++;
        if (st.ThiefGloveMode > 0) c++;
        if (st.BagExpandSlots > 0) c++;
        if (st.SpecialConvenienceMode > 0) c++;
        if (st.FreezeDaytime || FreezeDaytimeEnabled) c++;
        return c;
    }

    // =========================================================================
    // Helper Emitters for Rel32 Safe X64 Trampolines
    // =========================================================================
    struct Rel32Ref {
        public int Pos;
        public string Label;
    }

    static void EmitJcc32(List<byte> code, List<Rel32Ref> refs, byte op2, string label) {
        code.Add(0x0F);
        code.Add(op2);
        refs.Add(new Rel32Ref { Pos = code.Count, Label = label });
        code.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });
    }

    static void EmitJmp32(List<byte> code, List<Rel32Ref> refs, string label) {
        code.Add(0xE9);
        refs.Add(new Rel32Ref { Pos = code.Count, Label = label });
        code.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });
    }

    static void ResolveLabels(List<byte> code, Dictionary<string, int> labels, List<Rel32Ref> refs) {
        for (int i = 0; i < refs.Count; i++) {
            Rel32Ref r = refs[i];
            int target = labels[r.Label];
            int rel = target - (r.Pos + 4);
            byte[] b = BitConverter.GetBytes(rel);
            for (int k = 0; k < 4; k++) code[r.Pos + k] = b[k];
        }
    }

    static void EmitMovabsRax(List<byte> code, long addr) {
        code.Add(0x48); code.Add(0xB8); code.AddRange(BitConverter.GetBytes(addr));
    }
    static void EmitMovabsRdx(List<byte> code, long addr) {
        code.Add(0x48); code.Add(0xBA); code.AddRange(BitConverter.GetBytes(addr));
    }
    static void EmitMovabsRcx(List<byte> code, long addr) {
        code.Add(0x48); code.Add(0xB9); code.AddRange(BitConverter.GetBytes(addr));
    }
    static void EmitRipIncDword(List<byte> code, long baseCaveOff, long dataAbsAddr) {
        code.Add(0xFF); code.Add(0x05);
        int disp = (int)(dataAbsAddr - (baseCaveOff + code.Count + 4));
        code.AddRange(BitConverter.GetBytes(disp));
    }
    static void EmitAbsJmp(List<byte> code, long dst) {
        code.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
        code.AddRange(BitConverter.GetBytes(dst));
    }

    // Validates candidate pointer in RAX is canonical readable user-mode (>= 0x10000000 and bit 47+ == 0).
    static void EmitCheckReadablePtrRax(List<byte> code, List<Rel32Ref> refs, Dictionary<string, int> lbl, string failLabel) {
        code.AddRange(new byte[] { 0x48, 0x3D, 0x00, 0x00, 0x00, 0x10 }); // cmp rax, 0x10000000
        EmitJcc32(code, refs, 0x82, failLabel);                           // jb failLabel
        code.AddRange(new byte[] { 0x48, 0x89, 0xC1, 0x48, 0xC1, 0xE9, 0x2F }); // mov rcx, rax; shr rcx, 47
        EmitJcc32(code, refs, 0x85, failLabel);                           // jnz failLabel
    }

    // Validates candidate pointer in RAX (non-null, canonical user-mode, and NOT inside PE image [peLow..peHigh]).
    // Clobbers scratch RCX. Caller must save RAX & RCX and restore them at failLabel.
    static void EmitCheckHeapPtrRax(List<byte> code, List<Rel32Ref> refs, Dictionary<string, int> lbl, long peLow, long peHigh, string failLabel) {
        EmitCheckReadablePtrRax(code, refs, lbl, failLabel);
        string okLbl = "heap_ok_" + code.Count;
        EmitMovabsRcx(code, peLow);
        code.AddRange(new byte[] { 0x48, 0x39, 0xC8 });                   // cmp rax, rcx
        EmitJcc32(code, refs, 0x82, okLbl);                               // jb okLbl (below PE)
        EmitMovabsRcx(code, peHigh);
        code.AddRange(new byte[] { 0x48, 0x39, 0xC8 });                   // cmp rax, rcx
        EmitJcc32(code, refs, 0x86, failLabel);                           // jbe failLabel (inside PE static image!)
        lbl[okLbl] = code.Count;
    }

    // Expects eax = uint16 runtime table index from [ItemRecord + 0x08].
    // Resolves true 32-bit _itemKey via GlobalItemTableManager into eax, or jumps to failLabel with balanced stack.
    static void EmitResolveRealItemKeyEax(
        List<byte> code, List<Rel32Ref> refs, Dictionary<string, int> lbl,
        long itemTableMgrGlobalAddr, string failLabel, string suffix
    ) {
        string popFailLbl = "res_pop_fail_" + suffix;
        string okLbl = "res_ok_" + suffix;
        code.AddRange(new byte[] { 0x3D, 0xFF, 0xFF, 0x00, 0x00 });       // cmp eax, 0xFFFF
        EmitJcc32(code, refs, 0x83, failLabel);                           // jae failLabel
        code.Add(0x50);                                                   // push rax (save tableIdx)
        EmitMovabsRax(code, itemTableMgrGlobalAddr);                      // rax = &GlobalItemTableManager
        code.AddRange(new byte[] { 0x48, 0x8B, 0x00 });                   // rax = *rax
        EmitCheckReadablePtrRax(code, refs, lbl, popFailLbl);
        code.AddRange(new byte[] { 0x48, 0x8B, 0x14, 0x24 });             // rdx = [rsp] (tableIdx)
        code.AddRange(new byte[] { 0x3B, 0x50, 0x08 });                   // cmp edx, [rax + 0x08] (tableCount)
        EmitJcc32(code, refs, 0x83, popFailLbl);
        code.AddRange(new byte[] { 0x48, 0x8B, 0x40, 0x58 });             // rax = [rax + 0x58] (ItemStaticData**)
        EmitCheckReadablePtrRax(code, refs, lbl, popFailLbl);
        code.AddRange(new byte[] { 0x48, 0x8B, 0x14, 0x24 });             // rdx = [rsp]
        code.AddRange(new byte[] { 0x48, 0x8B, 0x04, 0xD0 });             // rax = [rax + rdx*8]
        EmitCheckReadablePtrRax(code, refs, lbl, popFailLbl);
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x08 });             // add rsp, 8
        code.AddRange(new byte[] { 0x8B, 0x00 });                         // mov eax, [rax] (true _itemKey)
        EmitJmp32(code, refs, okLbl);
        lbl[popFailLbl] = code.Count;
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x08 });             // add rsp, 8
        EmitJmp32(code, refs, failLabel);
        lbl[okLbl] = code.Count;
    }

    static void FreeCaveOnInitFail() {
        if (hProc != IntPtr.Zero && caveFling != IntPtr.Zero) {
            try { VirtualFreeEx(hProc, caveFling, 0, MEM_RELEASE); } catch { }
        }
        caveFling = IntPtr.Zero;
        _caveParamCache.Clear();
        trampolinesReady = false;
    }

    static void WriteCaveSlot(string name, long cf, long slotOff, long limitOff, byte[] bytes) {
        int maxLen = (int)(limitOff - slotOff);
        if (bytes.Length > maxLen) {
            throw new InvalidOperationException(string.Format("{0} cave overflow: {1} > {2}", name, bytes.Length, maxLen));
        }
        if (!WriteBytes(cf + slotOff, bytes)) {
            throw new InvalidOperationException(name + " cave write failed");
        }
    }

    // =========================================================================
    // Initialize All 21 Verified Trampolines in One 16KB Cave (No Vanilla Overwrite)
    // =========================================================================
    static bool InitTrampolines() {
        long addrPlayer = baseAddr + RVA_PLAYER;
        long peLow = baseAddr;
        long peHigh = baseAddr + 0x17000000L; // Covers SizeOfImage (0x16499000) so static PE objects are never written to.
        long[] allAddrs = new long[] {
            baseAddr + RVA_PLAYER, baseAddr + RVA_STATS, baseAddr + RVA_COMMIT,
            baseAddr + RVA_DEFENSE, baseAddr + RVA_DMG, baseAddr + RVA_SAVE_ITEM,
            baseAddr + RVA_SELECTED_ITEM, baseAddr + RVA_KISS, baseAddr + RVA_DAYTIME,
            baseAddr + RVA_EXP, baseAddr + RVA_DURABILITY, baseAddr + RVA_CRIME_EVAL,
            baseAddr + RVA_HORSE_TAMING, baseAddr + RVA_BREATH_VITALS, baseAddr + RVA_SLOT_EXPAND,
            baseAddr + RVA_ROMANCE_TRUST, baseAddr + RVA_FRIENDLY_DAILY, baseAddr + RVA_AFFINITY_ADD,
            baseAddr + RVA_FRIENDLY_ADD, baseAddr + RVA_COUNT_ITEMS, baseAddr + RVA_WANTED_EVAL
        };

        // Center allocation near baseAddr + 0x05800000L so every RVA in 0x008BB2A3..0x0C3A7164 is within +-2GB
        caveFling = AllocNear(hProc, baseAddr + 0x05800000L, 0x4000);
        if (caveFling == IntPtr.Zero) caveFling = AllocNear(hProc, addrPlayer, 0x4000);
        if (caveFling == IntPtr.Zero) return false;
        long cf = caveFling.ToInt64();
        _caveParamCache.Clear();

        for (int i = 0; i < allAddrs.Length; i++) {
            if (Math.Abs(cf - (allAddrs[i] + 5)) > 0x7FFF0000L) {
                EngLog(string.Format("Cave reach overflow at addr 0x{0:X}", allAddrs[i]));
                FreeCaveOnInitFail();
                return false;
            }
        }

        try {
        // Initialize 4KB data block cleanly
        WriteBytes(cf + DATA_BASE_OFF, new byte[4096]);
        WriteBytes(cf + OFF_DATA_KISS_MULT,    BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_SWIM_MULT,    BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_MOUNT_SPEED,  BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_KISS_CLAMPY,  BitConverter.GetBytes(-1.5f));
        WriteBytes(cf + OFF_DATA_FREEZE_TIME,  BitConverter.GetBytes(-1));
        WriteBytes(cf + OFF_DATA_EXP_MULT,     BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_TAMING_MULT,  BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_BREATH_MULT,  BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_ROMANCE_MULT, BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_CONST_ONE,    BitConverter.GetBytes(1.0f));
        WriteBytes(cf + OFF_DATA_CONST_ZERO,   BitConverter.GetBytes(0.0f));
        WriteBytes(cf + OFF_DATA_CONST_NEG100, BitConverter.GetBytes(-100.0f));

        long dPlayer     = cf + OFF_DATA_PLAYER;
        long dGodMode    = cf + OFF_DATA_GODMODE;
        long dStaMode    = cf + OFF_DATA_STAMINA_MODE;
        long dCdMode     = cf + OFF_DATA_COOLDOWN_MODE;
        long dMStaMode   = cf + OFF_DATA_MOUNT_STA;
        long dDef        = cf + OFF_DATA_DEFENSE;
        long dDmg        = cf + OFF_DATA_DMG_MODE;
        long dMaxHp      = cf + OFF_DATA_MAX_HP;
        long dMaxSta     = cf + OFF_DATA_MAX_STA;
        long dMaxSpi     = cf + OFF_DATA_MAX_SPIRIT;
        long dSilverPtr  = cf + OFF_DATA_SILVER_PTR;
        long dCurSilver  = cf + OFF_DATA_CUR_SILVER;
        long dPendGold   = cf + OFF_DATA_PEND_GOLD;
        long dSelId      = cf + OFF_DATA_SEL_ID;
        long dSelPtr     = cf + OFF_DATA_SEL_PTR;
        long dSelCnt     = cf + OFF_DATA_SEL_COUNT;
        long dEditAmt    = cf + OFF_DATA_EDIT_AMT;
        long dEditId     = cf + OFF_DATA_EDIT_ID;
        long dKissBase   = cf + OFF_DATA_KISS_MULT;
        long dDtBase     = cf + OFF_DATA_FREEZE_TIME;
        long dExpMult    = cf + OFF_DATA_EXP_MULT;
        long dPStaPtr    = cf + OFF_DATA_P_STA_PTR;
        long dPSpiPtr    = cf + OFF_DATA_P_SPI_PTR;
        long dCommitCnt  = cf + OFF_DATA_COMMIT_CNT;
        long dThiefDet   = cf + OFF_DATA_THIEF_DET;
        long dPickpocket = cf + OFF_DATA_PICKPOCKET;
        long dBounty     = cf + OFF_DATA_BOUNTY_MODE;
        long dCrimeCtr   = cf + OFF_DATA_CRIME_CTR;
        long dDurMode    = cf + OFF_DATA_DUR_MODE;
        long dDurAcc     = cf + OFF_DATA_DUR_ACC;
        long dTameMult   = cf + OFF_DATA_TAMING_MULT;
        long dTameInst   = cf + OFF_DATA_TAMING_INST;
        long dBreathMode = cf + OFF_DATA_BREATH_MODE;
        long dBreathMult = cf + OFF_DATA_BREATH_MULT;
        long dBagExpand  = cf + OFF_DATA_BAG_EXPAND;
        long dInvMgrPtr  = cf + OFF_DATA_INV_MGR_PTR;
        long dImbueMode  = cf + OFF_DATA_IMBUE_MODE;
        long dLifesteal  = cf + OFF_DATA_LIFESTEAL_PCT;
        long dSleepTrig  = cf + OFF_DATA_SLEEP_TRIG;
        long dGloveMode  = cf + OFF_DATA_THIEF_GLOVE;
        long dMountSeen  = cf + OFF_DATA_MOUNT_SEEN;
        long dConstOne   = cf + OFF_DATA_CONST_ONE;
        long dFlyLift    = cf + OFF_DATA_FLY_LIFTY;
        long dUnderwater = cf + OFF_DATA_UNDERWATER;
        long dSwimMult     = cf + OFF_DATA_SWIM_MULT;
        long dPrevBreath   = cf + OFF_DATA_PREV_BREATH;
        long dNpcMgrPtr    = cf + OFF_DATA_NPC_MGR_PTR;
        long dPrevSta      = cf + OFF_DATA_PREV_STA;
        long dPrevSpi      = cf + OFF_DATA_PREV_SPI;
        long dPrevMSta     = cf + OFF_DATA_PREV_MSTA;
        long dPrevHp       = cf + OFF_DATA_PREV_HP;
        long dBagContPtr   = cf + OFF_DATA_BAG_CONT_PTR;
        long dDmgTakenMode = cf + OFF_DATA_DMG_TAKEN_MODE;
        long dPHpPtr       = cf + OFF_DATA_P_HP_PTR;

        // ---------------------------------------------------------------------
        // 1. RVA_PLAYER (cf + 0x0000, 8B) -> Captures ServerStatusActorComponent
        //    CRITICAL: Removed speculative 5-level [rcx+0xA0]->...->[rbx+0xB8] dereference chain
        //    that caused Access Violation crashes during combat when non-player actors hit RVA_PLAYER!
        // ---------------------------------------------------------------------
        {
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x48, 0x3D, 0x00, 0x00, 0x01, 0x00 }); // cmp rax, 0x10000
            EmitJcc32(code, refs, 0x82, "skip_rax");
            code.AddRange(new byte[] { 0x51, 0x48, 0x89, 0xC1, 0x48, 0xC1, 0xE9, 0x2F, 0x59 }); // push rcx; mov rcx, rax; shr rcx, 47; pop rcx
            EmitJcc32(code, refs, 0x85, "skip_rax");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x58, 0x20 });             // mov rbx, [rax+0x20]
            code.AddRange(new byte[] { 0x48, 0x81, 0xFB, 0x00, 0x00, 0x00, 0x10 }); // cmp rbx, 0x10000000
            EmitJcc32(code, refs, 0x82, "skip_dplayer");
            code.AddRange(new byte[] { 0x51, 0x48, 0x89, 0xD9, 0x48, 0xC1, 0xE9, 0x2F, 0x59 }); // push rcx; mov rcx, rbx; shr rcx, 47; pop rcx
            EmitJcc32(code, refs, 0x85, "skip_dplayer");
            code.AddRange(new byte[] { 0x48, 0x89, 0x1D });
            code.AddRange(BitConverter.GetBytes((int)(dPlayer - (cf + OFF_CODE_PLAYER + code.Count + 4))));
            lbl["skip_dplayer"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x8B, 0x58, 0x40 });             // mov rbx, [rax+0x40]
            code.AddRange(new byte[] { 0x48, 0x81, 0xFB, 0x00, 0x00, 0x01, 0x00 }); // cmp rbx, 0x10000
            EmitJcc32(code, refs, 0x82, "skip_rbx");
            code.AddRange(new byte[] { 0x51, 0x48, 0x89, 0xD9, 0x48, 0xC1, 0xE9, 0x2F, 0x59 }); // push rcx; mov rcx, rbx; shr rcx, 47; pop rcx
            EmitJcc32(code, refs, 0x85, "skip_rbx");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x43, 0x08 });             // mov rax, [rbx+0x08]
            EmitJmp32(code, refs, "done");
            lbl["skip_rbx"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x31, 0xC0 });                   // xor rax, rax
            EmitJmp32(code, refs, "done");
            lbl["skip_rax"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x31, 0xDB, 0x48, 0x31, 0xC0 }); // xor rbx, rbx; xor rax, rax
            lbl["done"] = code.Count;
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_PLAYER + 8);
            WriteCaveSlot("PLAYER", cf, OFF_CODE_PLAYER, OFF_CODE_STATS, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 2. RVA_STATS (cf + 0x0100, 8B) -> Player & Mount Health/Stamina/Spirit + Lifesteal + Ptr Capture
        //    CRITICAL: In FUNC_STATS (0x01559873), ECX holds the integer Stat ID (0=HP, 17=Stamina, 18=Spirit)
        //    loaded at 0x0155984C (`mov ecx, [rax+14h]`), while `[rax]` is the 64-bit descriptor pointer!
        //    After `push rbx, rcx, rdx`, `[rsp+0x08]` holds the saved ECX Stat ID!
        // ---------------------------------------------------------------------
        {
            long stBase = cf + OFF_CODE_STATS;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(VANILLA_STATS);                                     // shl rax, 4; add rax, [rsi+0x58]
            code.AddRange(new byte[] { 0x48, 0x3D, 0x00, 0x00, 0x01, 0x00 }); // cmp rax, 0x10000
            EmitJcc32(code, refs, 0x82, "ret");
            code.AddRange(new byte[] { 0x48, 0x81, 0xFE, 0x00, 0x00, 0x01, 0x00 }); // cmp rsi, 0x10000
            EmitJcc32(code, refs, 0x82, "ret");
            code.AddRange(new byte[] { 0x53, 0x51, 0x52 });                   // push rbx, rcx, rdx ([rsp+8] = Stat ID in ecx!)

            code.AddRange(new byte[] { 0x48, 0x89, 0xC1, 0x48, 0xC1, 0xE9, 0x2F }); // shr rax copy >> 47
            EmitJcc32(code, refs, 0x85, "done");

            EmitMovabsRdx(code, dPlayer);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x1A });                   // mov rbx, [dPlayer]
            code.AddRange(new byte[] { 0x48, 0x81, 0xFB, 0x00, 0x00, 0x01, 0x00 });
            EmitJcc32(code, refs, 0x82, "done");
            code.AddRange(new byte[] { 0x48, 0x89, 0xD9, 0x48, 0xC1, 0xE9, 0x2F });
            EmitJcc32(code, refs, 0x85, "done");

            code.AddRange(new byte[] { 0x48, 0x3B, 0x73, 0x18 });             // cmp rsi, [rbx+0x18] (StatComponent* == dPlayer->StatComponent*)
            EmitJcc32(code, refs, 0x84, "is_player");
            code.AddRange(new byte[] { 0x48, 0x39, 0x1E });                   // cmp [rsi], rbx
            EmitJcc32(code, refs, 0x84, "is_player");
            code.AddRange(new byte[] { 0x48, 0x39, 0xDE });                   // cmp rsi, rbx
            EmitJcc32(code, refs, 0x85, "not_player");
            lbl["is_player"] = code.Count;

            // Load true 16-bit Stat ID from [rsp+0x38] (saved at 0x0155983C) + 0x18 (3 pushes) = [rsp+0x50]
            code.AddRange(new byte[] { 0x0F, 0xB7, 0x4C, 0x24, 0x50 });       // movzx ecx, word ptr [rsp+0x50]

            // --- Player Health (id 0) ---
            code.AddRange(new byte[] { 0x83, 0xF9, 0x00 });                   // cmp ecx, 0
            EmitJcc32(code, refs, 0x85, "p_chk_sta");
            EmitMovabsRdx(code, dPHpPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });                   // [dPHpPtr] = rax
            EmitMovabsRdx(code, dMaxHp);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });             // mov edx, [dMaxHp]; test edx, edx
            EmitJcc32(code, refs, 0x8E, "p_hp_god");
            code.AddRange(new byte[] { 0x48, 0x63, 0xD2, 0x48, 0x69, 0xD2, 0xE8, 0x03, 0x00, 0x00, 0x48, 0x89, 0x50, 0x18 });
            lbl["p_hp_god"] = code.Count;
            EmitMovabsRdx(code, dLifesteal);
            code.AddRange(new byte[] { 0x8B, 0x0A, 0x85, 0xC9 });             // ecx = [dLifesteal]; test ecx, ecx
            EmitJcc32(code, refs, 0x8E, "p_hp_god2");
            code.AddRange(new byte[] { 0xC7, 0x02, 0x00, 0x00, 0x00, 0x00 }); // clear [dLifesteal]
            code.AddRange(new byte[] { 0x48, 0x8B, 0x58, 0x18, 0x48, 0x85, 0xDB }); // rbx = MaxHP
            EmitJcc32(code, refs, 0x8E, "p_hp_god2");
            code.AddRange(new byte[] { 0x50 });                               // push rax (stat* ptr)
            code.AddRange(new byte[] { 0x48, 0x63, 0xC9, 0x48, 0x0F, 0xAF, 0xCB }); // rcx = MaxHP * pct
            code.AddRange(new byte[] { 0x48, 0x89, 0xC8, 0x48, 0x99, 0xBB, 0x64, 0x00, 0x00, 0x00, 0x48, 0xF7, 0xFB }); // rax = heal
            code.AddRange(new byte[] { 0x48, 0x8B, 0x1C, 0x24 });             // rbx = stat*
            code.AddRange(new byte[] { 0x48, 0x01, 0x43, 0x08 });             // add [rbx+8], rax
            code.AddRange(new byte[] { 0x48, 0x8B, 0x4B, 0x18, 0x48, 0x39, 0x4B, 0x08 }); // cur vs max
            EmitJcc32(code, refs, 0x8E, "p_ls_ok");
            code.AddRange(new byte[] { 0x48, 0x89, 0x4B, 0x08 });             // clamp cur = max
            lbl["p_ls_ok"] = code.Count;
            code.AddRange(new byte[] { 0x58 });                               // pop rax
            EmitRipIncDword(code, stBase, dCommitCnt);
            lbl["p_hp_god2"] = code.Count;
            EmitMovabsRdx(code, dGodMode);
            code.AddRange(new byte[] { 0x8B, 0x0A });                         // ecx = [dGodMode]
            EmitMovabsRdx(code, dSleepTrig);
            code.AddRange(new byte[] { 0x0B, 0x0A });                         // or ecx, [dSleepTrig]
            EmitMovabsRdx(code, dPrevHp);                                     // rdx = &dPrevHp
            EmitJcc32(code, refs, 0x85, "fill_max_and_save");                 // GodMode or SleepHeal -> lock max!
            EmitMovabsRdx(code, dDmgTakenMode);
            code.AddRange(new byte[] { 0x8B, 0x0A });                         // ecx = [dDmgTakenMode] (0..3)
            EmitMovabsRdx(code, dPrevHp);                                     // rdx = &dPrevHp
            code.AddRange(new byte[] { 0x85, 0xC9 });
            EmitJcc32(code, refs, 0x8E, "save_prev");
            EmitJmp32(code, refs, "mode_123_to_pct");

            // --- Player Stamina (id 17) ---
            lbl["p_chk_sta"] = code.Count;
            code.AddRange(new byte[] { 0x83, 0xF9, 0x11 });                   // cmp ecx, 17
            EmitJcc32(code, refs, 0x85, "p_chk_spi");
            EmitMovabsRdx(code, dPStaPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });                   // [dPStaPtr] = rax
            EmitMovabsRdx(code, dMaxSta);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });
            EmitJcc32(code, refs, 0x8E, "p_sta_inf");
            code.AddRange(new byte[] { 0x48, 0x63, 0xD2, 0x48, 0x69, 0xD2, 0xE8, 0x03, 0x00, 0x00, 0x48, 0x89, 0x50, 0x18 });
            lbl["p_sta_inf"] = code.Count;
            EmitMovabsRdx(code, dStaMode);
            code.AddRange(new byte[] { 0x8B, 0x0A });                         // ecx = [dStaMode]
            EmitMovabsRdx(code, dSleepTrig);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x00 });
            EmitMovabsRdx(code, dPrevSta);                                    // rdx = &dPrevSta
            EmitJcc32(code, refs, 0x85, "fill_max_and_save");
            code.AddRange(new byte[] { 0x85, 0xC9 });
            EmitJcc32(code, refs, 0x8E, "save_prev");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x04 });                   // mode 4 = Infinite Stamina
            EmitJcc32(code, refs, 0x84, "fill_max_and_save");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x03 });                   // mode 3 = 2x Regen + -30% drain
            EmitJcc32(code, refs, 0x85, "mode_123_to_pct");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x1A, 0x48, 0x85, 0xDB }); // rbx = [dPrevSta]
            EmitJcc32(code, refs, 0x8E, "save_prev");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x48, 0x08, 0x48, 0x29, 0xD9 }); // rcx = cur - prev
            EmitJcc32(code, refs, 0x8E, "sta_m3_drain");
            code.AddRange(new byte[] { 0x48, 0x01, 0x48, 0x08 });             // regen > 0: add [rax+8], (cur - prev)
            EmitRipIncDword(code, stBase, dCommitCnt);
            EmitJmp32(code, refs, "clamp_inc_save");
            lbl["sta_m3_drain"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x1E, 0x00, 0x00, 0x00 });       // mov ecx, 30 (refund 30% on drain)
            EmitJmp32(code, refs, "apply_refund");

            // --- Player Spirit / Skill Cooldown (id 18) ---
            lbl["p_chk_spi"] = code.Count;
            code.AddRange(new byte[] { 0x83, 0xF9, 0x12 });                   // cmp ecx, 18
            EmitJcc32(code, refs, 0x85, "done");
            EmitMovabsRdx(code, dPSpiPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });                   // [dPSpiPtr] = rax
            EmitMovabsRdx(code, dMaxSpi);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });
            EmitJcc32(code, refs, 0x8E, "p_spi_inf");
            code.AddRange(new byte[] { 0x48, 0x63, 0xD2, 0x48, 0x69, 0xD2, 0xE8, 0x03, 0x00, 0x00, 0x48, 0x89, 0x50, 0x18 });
            lbl["p_spi_inf"] = code.Count;
            EmitMovabsRdx(code, dCdMode);
            code.AddRange(new byte[] { 0x8B, 0x0A });                         // ecx = [dCdMode]
            EmitMovabsRdx(code, dSleepTrig);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x00 });
            EmitMovabsRdx(code, dPrevSpi);                                    // rdx = &dPrevSpi
            EmitJcc32(code, refs, 0x85, "fill_max_and_save");
            code.AddRange(new byte[] { 0x85, 0xC9 });
            EmitJcc32(code, refs, 0x8E, "save_prev");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x04 });                   // mode 4 = Infinite Spirit / 0s Cooldown
            EmitJcc32(code, refs, 0x84, "fill_max_and_save");
            EmitJmp32(code, refs, "mode_123_to_pct");

            // --- Non-Player (Mount Stamina id 17) ---
            lbl["not_player"] = code.Count;
            code.AddRange(new byte[] { 0x0F, 0xB7, 0x4C, 0x24, 0x50 });       // movzx ecx, word ptr [rsp+0x50] (Stat ID)
            code.AddRange(new byte[] { 0x83, 0xF9, 0x11 });                   // cmp ecx, 17
            EmitJcc32(code, refs, 0x85, "done");
            EmitMovabsRdx(code, dMountSeen);
            code.AddRange(new byte[] { 0xFF, 0x02 });                         // inc dword [dMountSeen]
            EmitMovabsRdx(code, dMStaMode);
            code.AddRange(new byte[] { 0x8B, 0x0A });                         // ecx = [dMStaMode]
            EmitMovabsRdx(code, dPrevMSta);                                   // rdx = &dPrevMSta
            code.AddRange(new byte[] { 0x85, 0xC9 });
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x04 });
            EmitJcc32(code, refs, 0x84, "fill_max_and_save");
            EmitJmp32(code, refs, "mode_123_to_pct");

            // --- Shared Proportional Refund & Max Lock Helpers ---
            lbl["fill_max_and_save"] = code.Count;
            EmitRipIncDword(code, stBase, dCommitCnt);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x58, 0x18, 0x48, 0x85, 0xDB }); // rbx = [rax+0x18]
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x48, 0x89, 0x58, 0x08 });             // [rax+0x08] = rbx
            EmitJmp32(code, refs, "save_prev");

            lbl["mode_123_to_pct"] = code.Count;
            code.AddRange(new byte[] { 0x83, 0xF9, 0x01 });
            EmitJcc32(code, refs, 0x84, "pct30");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x02 });
            EmitJcc32(code, refs, 0x84, "pct50");
            code.AddRange(new byte[] { 0xB9, 0x4B, 0x00, 0x00, 0x00 });       // 75% refund (-75% drain)
            EmitJmp32(code, refs, "apply_refund");
            lbl["pct30"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x1E, 0x00, 0x00, 0x00 });       // 30% refund (-30% drain)
            EmitJmp32(code, refs, "apply_refund");
            lbl["pct50"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x32, 0x00, 0x00, 0x00 });       // 50% refund (-50% drain)

            lbl["apply_refund"] = code.Count;
            EmitRipIncDword(code, stBase, dCommitCnt);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x1A, 0x48, 0x85, 0xDB }); // rbx = [rdx] (prev)
            EmitJcc32(code, refs, 0x8E, "save_prev");
            code.AddRange(new byte[] { 0x48, 0x2B, 0x58, 0x08 });             // rbx = prev - cur (drain)
            EmitJcc32(code, refs, 0x8E, "save_prev");
            code.AddRange(new byte[] { 0x52, 0x50 });                         // push rdx; push rax
            code.AddRange(new byte[] { 0x48, 0x89, 0xD8, 0x48, 0x63, 0xC9, 0x48, 0x0F, 0xAF, 0xC1 }); // rax = drain * pct
            code.AddRange(new byte[] { 0x48, 0x99, 0xB9, 0x64, 0x00, 0x00, 0x00, 0x48, 0xF7, 0xF9 }); // rax = (drain * pct) / 100
            code.AddRange(new byte[] { 0x48, 0x89, 0xC3, 0x58, 0x5A });       // rbx = refund; pop rax; pop rdx
            code.AddRange(new byte[] { 0x48, 0x01, 0x58, 0x08 });             // add [rax+0x08], rbx

            lbl["clamp_inc_save"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x8B, 0x58, 0x18, 0x48, 0x39, 0x58, 0x08 }); // cmp [rax+8], [rax+18]
            EmitJcc32(code, refs, 0x8E, "save_prev");
            code.AddRange(new byte[] { 0x48, 0x89, 0x58, 0x08 });             // clamp [rax+8] = [rax+18]

            lbl["save_prev"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x8B, 0x58, 0x08, 0x48, 0x89, 0x1A }); // [rdx] = [rax+0x08]

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5A, 0x59, 0x5B });                   // pop rdx, rcx, rbx
            lbl["ret"] = code.Count;
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_STATS + 8);
            WriteCaveSlot("STATS", cf, OFF_CODE_STATS, OFF_CODE_COMMIT, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 3. RVA_COMMIT (cf + 0x0600, 9B) -> Proportional Stamina/Cooldown/God
        //    CRITICAL: Only match verified player pointers (dPStaPtr, dPSpiPtr, dPHpPtr)!
        //    Previously, `cmp dword [rdi], 0 / 17 / 18` matched arbitrary structs in combat,
        //    corrupting non-stat structures and enemy HP/Stamina!
        // ---------------------------------------------------------------------
        {
            long cBase = cf + OFF_CODE_COMMIT;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x51, 0x52 });                   // push rax, rcx, rdx
            code.AddRange(new byte[] { 0x48, 0x81, 0xFF, 0x00, 0x00, 0x00, 0x10 }); // cmp rdi, 0x10000000
            EmitJcc32(code, refs, 0x82, "done");
            code.AddRange(new byte[] { 0x48, 0x89, 0xF9, 0x48, 0xC1, 0xE9, 0x2F }); // mov rcx, rdi; shr rcx, 47
            EmitJcc32(code, refs, 0x85, "done");

            // --- Player Stamina (rdi == [dPStaPtr]) ---
            EmitMovabsRdx(code, dPStaPtr);
            code.AddRange(new byte[] { 0x48, 0x3B, 0x3A });                   // cmp rdi, [dPStaPtr]
            EmitJcc32(code, refs, 0x85, "chk_spirit");
            EmitMovabsRdx(code, dStaMode);
            code.AddRange(new byte[] { 0x8B, 0x0A, 0xBA, 0x01, 0x00, 0x00, 0x00 }); // ecx = dStaMode; edx = 1 (player)
            code.AddRange(new byte[] { 0x85, 0xC9 });                         // test ecx, ecx
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x04 });                   // mode 4 = inf
            EmitJcc32(code, refs, 0x84, "fill_max");

            // delta = rbx - [rdi+8]
            code.AddRange(new byte[] { 0x48, 0x89, 0xD8, 0x48, 0x2B, 0x47, 0x08 });
            code.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
            EmitJcc32(code, refs, 0x88, "sta_drain");
            // Positive regen: mode 3 + player => 2x recovery!
            code.AddRange(new byte[] { 0x83, 0xF9, 0x03 });
            EmitJcc32(code, refs, 0x85, "done");
            code.AddRange(new byte[] { 0x48, 0xD1, 0xE0, 0x48, 0x8B, 0x5F, 0x08, 0x48, 0x01, 0xC3 }); // rbx = [rdi+8] + 2*delta
            code.AddRange(new byte[] { 0x48, 0x3B, 0x5F, 0x18 });
            EmitJcc32(code, refs, 0x8E, "inc_ret");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x5F, 0x18 });
            EmitJmp32(code, refs, "inc_ret");

            lbl["sta_drain"] = code.Count;
            code.AddRange(new byte[] { 0x83, 0xF9, 0x01 });
            EmitJcc32(code, refs, 0x84, "pct70");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x02 });
            EmitJcc32(code, refs, 0x84, "pct50");
            code.AddRange(new byte[] { 0xB9, 0x50, 0x00, 0x00, 0x00 });       // mode 3 player = 80%
            EmitJmp32(code, refs, "apply_scale");

            // --- Player Spirit / Skill Cooldown (rdi == [dPSpiPtr]) ---
            lbl["chk_spirit"] = code.Count;
            EmitMovabsRdx(code, dPSpiPtr);
            code.AddRange(new byte[] { 0x48, 0x3B, 0x3A });                   // cmp rdi, [dPSpiPtr]
            EmitJcc32(code, refs, 0x85, "chk_hp");
            EmitMovabsRdx(code, dCdMode);
            code.AddRange(new byte[] { 0x8B, 0x0A, 0x85, 0xC9 });
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x04 });
            EmitJcc32(code, refs, 0x84, "fill_max");
            code.AddRange(new byte[] { 0x48, 0x89, 0xD8, 0x48, 0x2B, 0x47, 0x08 });
            code.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
            EmitJcc32(code, refs, 0x89, "done");                              // non-negative unchanged
            code.AddRange(new byte[] { 0x83, 0xF9, 0x01 });
            EmitJcc32(code, refs, 0x84, "pct70");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x02 });
            EmitJcc32(code, refs, 0x84, "pct50");
            code.AddRange(new byte[] { 0xB9, 0x19, 0x00, 0x00, 0x00 });       // 25% (-75%)
            EmitJmp32(code, refs, "apply_scale");

            lbl["pct70"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x46, 0x00, 0x00, 0x00 });
            EmitJmp32(code, refs, "apply_scale");
            lbl["pct50"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x32, 0x00, 0x00, 0x00 });

            lbl["apply_scale"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x0F, 0xAF, 0xC1 });             // imul rax, rcx
            code.AddRange(new byte[] { 0x48, 0x99 });                         // cqo
            code.AddRange(new byte[] { 0xB9, 0x64, 0x00, 0x00, 0x00 });       // mov ecx, 100
            code.AddRange(new byte[] { 0x48, 0xF7, 0xF9 });                   // idiv rcx
            code.AddRange(new byte[] { 0x48, 0x8B, 0x5F, 0x08 });             // mov rbx, [rdi+0x08]
            code.AddRange(new byte[] { 0x48, 0x01, 0xC3 });                   // add rbx, rax
            EmitJmp32(code, refs, "inc_ret");

            // --- Player HP GodMode lock in commit (rdi == [dPHpPtr]) ---
            lbl["chk_hp"] = code.Count;
            EmitMovabsRdx(code, dPHpPtr);
            code.AddRange(new byte[] { 0x48, 0x3B, 0x3A });                   // cmp rdi, [dPHpPtr]
            EmitJcc32(code, refs, 0x85, "done");
            EmitMovabsRdx(code, dGodMode);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x00 });
            EmitJcc32(code, refs, 0x84, "done");

            lbl["fill_max"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x3B, 0x5F, 0x18 });
            EmitJcc32(code, refs, 0x8D, "done");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x5F, 0x18 });

            lbl["inc_ret"] = code.Count;
            EmitRipIncDword(code, cBase, dCommitCnt);

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5A, 0x59, 0x58 });                   // pop rdx, rcx, rax
            code.AddRange(VANILLA_COMMIT);                                    // mov [rdi+8], rbx; mov rbx, [rsp+0x48]
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_COMMIT + 9);
            WriteCaveSlot("COMMIT", cf, OFF_CODE_COMMIT, OFF_CODE_DEFENSE, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 4. RVA_DEFENSE (cf + 0x0900, 15B)
        // ---------------------------------------------------------------------
        {
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x53, 0x52 });                         // push rbx, rdx
            code.AddRange(new byte[] { 0x89, 0xCB });                         // mov ebx, ecx
            code.AddRange(VANILLA_DEFENSE);
            EmitMovabsRdx(code, dDef);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x00 });
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x83, 0xFB, 0x04 });
            EmitJcc32(code, refs, 0x85, "done");
            EmitMovabsRdx(code, dPlayer);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x12, 0x48, 0x81, 0xFA, 0x00, 0x00, 0x00, 0x10 });
            EmitJcc32(code, refs, 0x82, "done");
            code.AddRange(new byte[] { 0x49, 0x81, 0xFE, 0x00, 0x00, 0x00, 0x10 });
            EmitJcc32(code, refs, 0x82, "done");
            code.AddRange(new byte[] { 0x49, 0x39, 0xD6 });                   // cmp r14, rdx
            EmitJcc32(code, refs, 0x84, "def_match");
            code.AddRange(new byte[] { 0x49, 0x39, 0x16 });                   // cmp [r14], rdx
            EmitJcc32(code, refs, 0x84, "def_match");
            code.AddRange(new byte[] { 0x4C, 0x3B, 0x72, 0x18 });             // cmp r14, [rdx+0x18]
            EmitJcc32(code, refs, 0x85, "done");
            lbl["def_match"] = code.Count;
            EmitMovabsRdx(code, dDef);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x48, 0x69, 0xD2, 0xE8, 0x03, 0x00, 0x00, 0x48, 0x01, 0xD1 }); // imul rdx, [dDef], 1000; add rcx, rdx
            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5A, 0x5B });
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_DEFENSE + 15);
            WriteCaveSlot("DEFENSE", cf, OFF_CODE_DEFENSE, OFF_CODE_DMG, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 5. RVA_DMG (cf + 0x0B00, 13B) -> Proportional Damage + OHK + Elemental Imbue
        // ---------------------------------------------------------------------
        {
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x52 });                               // push rdx
            code.AddRange(new byte[] { 0x89, 0xCA });                         // mov edx, ecx
            code.AddRange(new byte[] { 0x44, 0x8B, 0x24, 0x88 });             // mov r12d, [rax+rcx*4]
            code.AddRange(new byte[] { 0x4A, 0x8B, 0x1C, 0xE3 });             // mov rbx, [rbx+r12*8]
            code.AddRange(new byte[] { 0x83, 0xFA, 0x02 });                   // offense stat id 2..6
            EmitJcc32(code, refs, 0x82, "done");
            code.AddRange(new byte[] { 0x83, 0xFA, 0x06 });
            EmitJcc32(code, refs, 0x87, "done");
            code.AddRange(new byte[] { 0x48, 0x85, 0xDB });                   // test rbx, rbx (> 0)
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x48, 0x81, 0xFB, 0x00, 0xE1, 0xF5, 0x05 }); // cmp rbx, 100000000
            EmitJcc32(code, refs, 0x8F, "done");

            EmitMovabsRdx(code, dPlayer);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x12, 0x48, 0x81, 0xFA, 0x00, 0x00, 0x00, 0x10 });
            EmitJcc32(code, refs, 0x82, "done");
            code.AddRange(new byte[] { 0x49, 0x81, 0xFF, 0x00, 0x00, 0x00, 0x10 });
            EmitJcc32(code, refs, 0x82, "done");
            code.AddRange(new byte[] { 0x49, 0x39, 0xD7 });                   // cmp r15, rdx
            EmitJcc32(code, refs, 0x84, "dmg_match");
            code.AddRange(new byte[] { 0x49, 0x39, 0x17 });                   // cmp [r15], rdx
            EmitJcc32(code, refs, 0x84, "dmg_match");
            code.AddRange(new byte[] { 0x4C, 0x3B, 0x7A, 0x18 });             // cmp r15, [rdx+0x18]
            EmitJcc32(code, refs, 0x85, "done");
            lbl["dmg_match"] = code.Count;

            EmitMovabsRdx(code, dDmg);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });             // edx = dmgModeVal
            EmitJcc32(code, refs, 0x8E, "chk_imbue");
            code.AddRange(new byte[] { 0x81, 0xFA, 0x84, 0x03, 0x00, 0x00 }); // cmp edx, 900
            EmitJcc32(code, refs, 0x7D, "ohk");
            code.AddRange(new byte[] { 0x48, 0x0F, 0xAF, 0xDA, 0x48, 0xD1, 0xEB }); // rbx = (rbx * rdx) >> 1
            EmitJmp32(code, refs, "chk_imbue");
            lbl["ohk"] = code.Count;
            code.AddRange(new byte[] { 0xBB, 0x00, 0xCA, 0x9A, 0x3B });       // mov ebx, 1000000000

            lbl["chk_imbue"] = code.Count;
            EmitMovabsRdx(code, dImbueMode);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });             // edx = imbueMode
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x50, 0x51 });                         // push rax, rcx
            code.AddRange(new byte[] { 0x83, 0xFA, 0x01 });                   // Fire +35%
            EmitJcc32(code, refs, 0x84, "imb35");
            code.AddRange(new byte[] { 0x83, 0xFA, 0x02 });                   // Frost +25%
            EmitJcc32(code, refs, 0x84, "imb25");
            code.AddRange(new byte[] { 0x83, 0xFA, 0x03 });                   // Lightning +45%
            EmitJcc32(code, refs, 0x84, "imb45");
            code.AddRange(new byte[] { 0x83, 0xFA, 0x04 });                   // Lifesteal + Crit +35%
            EmitJcc32(code, refs, 0x84, "imb_ls8");
            EmitMovabsRax(code, dLifesteal);                                  // Master +65% + Lifesteal 10%
            code.AddRange(new byte[] { 0xC7, 0x00, 0x0A, 0x00, 0x00, 0x00, 0xB9, 0xA5, 0x00, 0x00, 0x00 }); // pct=165
            EmitJmp32(code, refs, "imb_scale");
            lbl["imb_ls8"] = code.Count;
            EmitMovabsRax(code, dLifesteal);
            code.AddRange(new byte[] { 0xC7, 0x00, 0x08, 0x00, 0x00, 0x00 });
            lbl["imb35"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x87, 0x00, 0x00, 0x00 });       // 135
            EmitJmp32(code, refs, "imb_scale");
            lbl["imb25"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x7D, 0x00, 0x00, 0x00 });       // 125
            EmitJmp32(code, refs, "imb_scale");
            lbl["imb45"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x91, 0x00, 0x00, 0x00 });       // 145
            lbl["imb_scale"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x81, 0xFB, 0x00, 0xCA, 0x9A, 0x3B }); // skip if already >= 1e9
            EmitJcc32(code, refs, 0x8D, "imb_pop");
            code.AddRange(new byte[] { 0x48, 0x89, 0xD8, 0x48, 0x0F, 0xAF, 0xC1, 0x31, 0xD2, 0xB9, 0x64, 0x00, 0x00, 0x00, 0x48, 0xF7, 0xF1, 0x48, 0x89, 0xC3 });
            lbl["imb_pop"] = code.Count;
            code.AddRange(new byte[] { 0x59, 0x58 });                         // pop rcx, rax

            lbl["done"] = code.Count;
            code.Add(0x5A);                                                   // pop rdx
            code.AddRange(new byte[] { 0x48, 0x8D, 0x4C, 0x24, 0x50 });       // lea rcx, [rsp+0x50]
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_DMG + 13);
            WriteCaveSlot("DMG", cf, OFF_CODE_DMG, OFF_CODE_SAVE_ITEM, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 6. RVA_SAVE_ITEM (cf + 0x0D00, 5B: 48 83 7E 10 00) -> rsi ItemRecord, r13 InventoryContainer
        //    Resolves true _itemKey via GlobalItemTableManager and restricts Silver to invKey == 2!
        //    CRITICAL: executes 48 83 7E 10 00 LAST to preserve RFLAGS for 0x01DF9585 jle!
        // ---------------------------------------------------------------------
        {
            long svBase = cf + OFF_CODE_SAVE_ITEM_BODY;
            long dItemTableMgr = baseAddr + RVA_ITEM_TABLE_MGR;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x51, 0x52 });                   // push rax, rcx, rdx
            EmitRipIncDword(code, svBase, cf + OFF_DATA_SAVE_HITS);
            code.AddRange(new byte[] { 0x48, 0x89, 0xF0 });                   // mov rax, rsi
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "done");
            code.AddRange(new byte[] { 0x48, 0x83, 0x7E, 0x10, 0x00 });       // cmp qword [rsi+0x10], 0
            EmitJcc32(code, refs, 0x8E, "done");

            code.AddRange(new byte[] { 0x0F, 0xB7, 0x46, 0x08 });             // eax = tableIdx [rsi+8]
            EmitResolveRealItemKeyEax(code, refs, lbl, dItemTableMgr, "done", "sv");
            code.AddRange(new byte[] { 0x83, 0xF8, 0x01 });                   // cmp eax, 1 (true Silver!)
            EmitJcc32(code, refs, 0x85, "chk_edit");

            code.AddRange(new byte[] { 0x4C, 0x89, 0xE8 });                   // mov rax, r13 (InventoryContainer*)
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "done");
            code.AddRange(new byte[] { 0x66, 0x41, 0x83, 0x7D, 0x10, 0x02 }); // cmp word [r13+0x10], 2 (Player Bag)
            EmitJcc32(code, refs, 0x85, "done");

            EmitMovabsRdx(code, dBagContPtr);
            code.AddRange(new byte[] { 0x4C, 0x89, 0x2A });                   // [dBagContPtr] = r13
            EmitMovabsRdx(code, dSilverPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x32 });                   // [dSilverPtr] = rsi
            code.AddRange(new byte[] { 0x48, 0x8B, 0x46, 0x10 });             // rax = [rsi+0x10]
            EmitMovabsRdx(code, dCurSilver);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });                   // [dCurSilver] = rax
            EmitMovabsRdx(code, dPendGold);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x02, 0x48, 0x85, 0xC0 }); // rax = [dPendGold]
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x48, 0x89, 0x46, 0x10 });             // [rsi+0x10] = rax
            EmitMovabsRdx(code, dCurSilver);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });
            // Keep dPendGold active across container iterations so both 0x18 (Display) and 0xD0 (Authoritative) get synced!
            EmitJmp32(code, refs, "done");

            lbl["chk_edit"] = code.Count;
            EmitMovabsRdx(code, dEditId);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });             // edx = [dEditId]; test edx, edx
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x39, 0xD0 });                         // cmp eax, edx (true _itemKey == [dEditId])
            EmitJcc32(code, refs, 0x85, "done");
            EmitMovabsRdx(code, dEditAmt);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x02, 0x48, 0x85, 0xC0 }); // rax = [dEditAmt]
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x48, 0x89, 0x46, 0x10 });             // [rsi+0x10] = rax
            code.AddRange(new byte[] { 0x48, 0xC7, 0x02, 0x00, 0x00, 0x00, 0x00 }); // clear dEditAmt
            EmitMovabsRdx(code, dEditId);
            code.AddRange(new byte[] { 0xC7, 0x02, 0x00, 0x00, 0x00, 0x00 }); // clear dEditId

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5A, 0x59, 0x58 });                   // pop rdx, rcx, rax
            code.AddRange(VANILLA_SAVE_ITEM);                                 // cmp qword [rsi+0x10], 0 (sets RFLAGS!)
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_SAVE_ITEM + 5);
            WriteCaveSlot("SAVE_ITEM_BODY", cf, OFF_CODE_SAVE_ITEM_BODY, OFF_CODE_SELECTED_ITEM_BODY, code.ToArray());
            var svStub = new List<byte> { 0xE9 };
            svStub.AddRange(BitConverter.GetBytes((int)(OFF_CODE_SAVE_ITEM_BODY - (OFF_CODE_SAVE_ITEM + 5))));
            WriteCaveSlot("SAVE_ITEM_STUB", cf, OFF_CODE_SAVE_ITEM, OFF_CODE_SELECTED_ITEM, svStub.ToArray());
        }

        // ---------------------------------------------------------------------
        // 7. RVA_SELECTED_ITEM (cf + 0x0F00 -> E9 -> cf + 0x2A80, 10B: rbx ItemRecord, r13 InventoryContainer)
        // ---------------------------------------------------------------------
        {
            long slBase = cf + OFF_CODE_SELECTED_ITEM_BODY;
            long dItemTableMgr = baseAddr + RVA_ITEM_TABLE_MGR;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x51, 0x52 });                   // push rax, rcx, rdx
            EmitRipIncDword(code, slBase, cf + OFF_DATA_SEL_HITS);
            code.AddRange(new byte[] { 0x4C, 0x89, 0xE8 });                   // mov rax, r13 (InventoryContainer*)
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "sl_after_bag");
            EmitMovabsRdx(code, dBagContPtr);
            code.AddRange(new byte[] { 0x66, 0x41, 0x83, 0x7D, 0x10, 0x02 }); // cmp word [r13+0x10], 2 (Player Bag)
            EmitJcc32(code, refs, 0x84, "sl_store_bag");
            code.AddRange(new byte[] { 0x48, 0x83, 0x3A, 0x00 });             // cmp qword [dBagContPtr], 0
            EmitJcc32(code, refs, 0x85, "sl_after_bag");
            lbl["sl_store_bag"] = code.Count;
            code.AddRange(new byte[] { 0x4C, 0x89, 0x2A });                   // [dBagContPtr] = r13
            lbl["sl_after_bag"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x89, 0xD8 });                   // mov rax, rbx
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "done");
            code.AddRange(new byte[] { 0x48, 0x83, 0x7B, 0x10, 0x00 });
            EmitJcc32(code, refs, 0x8E, "done");

            code.AddRange(new byte[] { 0x0F, 0xB7, 0x43, 0x08 });             // tableIdx [rbx+8]
            EmitResolveRealItemKeyEax(code, refs, lbl, dItemTableMgr, "done", "sl");
            EmitMovabsRdx(code, dSelId);
            code.AddRange(new byte[] { 0x89, 0x02 });                         // [dSelId] = realItemKey
            EmitMovabsRdx(code, dSelPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x1A });                   // [dSelPtr] = rbx
            code.Add(0x50);                                                   // push rax (preserve realItemKey)
            code.AddRange(new byte[] { 0x48, 0x8B, 0x4B, 0x10 });
            EmitMovabsRdx(code, dSelCnt);
            code.AddRange(new byte[] { 0x48, 0x89, 0x0A });                   // [dSelCnt] = [rbx+0x10]
            code.Add(0x58);                                                   // pop rax (realItemKey)

            code.AddRange(new byte[] { 0x83, 0xF8, 0x01 });                   // if hovered real Silver (itemKey==1)
            EmitJcc32(code, refs, 0x85, "chk_hover_edit");
            code.AddRange(new byte[] { 0x4C, 0x89, 0xE8 });                   // mov rax, r13 (InventoryContainer*)
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "restore_eax1_edit");
            code.AddRange(new byte[] { 0x66, 0x41, 0x83, 0x7D, 0x10, 0x02 }); // cmp word [r13+0x10], 2 (Player Bag)
            EmitJcc32(code, refs, 0x85, "restore_eax1_edit");
            EmitMovabsRdx(code, dSilverPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x1A });                   // [dSilverPtr] = rbx
            EmitMovabsRdx(code, dPendGold);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x02, 0x48, 0x85, 0xC0 }); // rax = [dPendGold]
            EmitJcc32(code, refs, 0x8E, "sl_read_sil");
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x10 });             // [rbx+0x10] = [dPendGold]
            EmitMovabsRdx(code, dSelCnt);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });
            lbl["sl_read_sil"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x8B, 0x43, 0x10 });
            EmitMovabsRdx(code, dCurSilver);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });
            lbl["restore_eax1_edit"] = code.Count;
            code.AddRange(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00 });       // mov eax, 1

            lbl["chk_hover_edit"] = code.Count;
            EmitMovabsRdx(code, dEditId);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });             // edx = [dEditId]; test edx, edx
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x39, 0xD0 });                         // cmp eax, edx (realItemKey == [dEditId])
            EmitJcc32(code, refs, 0x85, "done");
            EmitMovabsRdx(code, dEditAmt);
            code.AddRange(new byte[] { 0x48, 0x8B, 0x02, 0x48, 0x85, 0xC0 }); // rax = [dEditAmt]
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x48, 0x89, 0x43, 0x10 });             // [rbx+0x10] = rax
            EmitMovabsRdx(code, dSelCnt);
            code.AddRange(new byte[] { 0x48, 0x89, 0x02 });
            EmitMovabsRdx(code, dEditAmt);
            code.AddRange(new byte[] { 0x48, 0xC7, 0x02, 0x00, 0x00, 0x00, 0x00 });
            EmitMovabsRdx(code, dEditId);
            code.AddRange(new byte[] { 0xC7, 0x02, 0x00, 0x00, 0x00, 0x00 });

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5A, 0x59, 0x58 });                   // pop rdx, rcx, rax
            code.AddRange(VANILLA_SELECTED_ITEM);
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_SELECTED_ITEM + 10);
            WriteCaveSlot("SELECTED_ITEM_BODY", cf, OFF_CODE_SELECTED_ITEM_BODY, DATA_BASE_OFF, code.ToArray());
            var slStub = new List<byte> { 0xE9 };
            slStub.AddRange(BitConverter.GetBytes((int)(OFF_CODE_SELECTED_ITEM_BODY - (OFF_CODE_SELECTED_ITEM + 5))));
            WriteCaveSlot("SELECTED_ITEM_STUB", cf, OFF_CODE_SELECTED_ITEM, OFF_CODE_KISS, slStub.ToArray());
        }

        // ---------------------------------------------------------------------
        // 8. RVA_KISS (cf + 0x1100, 5B) -> Move Speed, Jump, No-Fall & Free Flight
        // ---------------------------------------------------------------------
        {
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x52 });                         // push rax, rdx
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x38 });             // sub rsp, 0x38
            code.AddRange(new byte[] { 0x0F, 0x11, 0x04, 0x24 });             // movups [rsp], xmm0
            code.AddRange(new byte[] { 0x0F, 0x11, 0x4C, 0x24, 0x10 });       // movups [rsp+0x10], xmm1
            code.AddRange(new byte[] { 0x0F, 0x11, 0x54, 0x24, 0x20 });       // movups [rsp+0x20], xmm2
            EmitMovabsRax(code, dKissBase);

            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x08 });             // movss xmm1, [rax+0x00] (speedMult)
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x14, 0x24 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0xD1 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x14, 0x24 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x54, 0x24, 0x08 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0xD1 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x54, 0x24, 0x08 });

            code.AddRange(new byte[] { 0x8B, 0x50, 0x04 });                   // edx = [dKissBase + 4] (state)
            code.AddRange(new byte[] { 0x83, 0xFA, 0x01 });                   // state 1 = Glide clamp
            EmitJcc32(code, refs, 0x84, "clamp_y");
            code.AddRange(new byte[] { 0x83, 0xFA, 0x03 });                   // state 3 = No-Fall clamp
            EmitJcc32(code, refs, 0x84, "clamp_y");
            code.AddRange(new byte[] { 0x83, 0xFA, 0x02 });                   // state 2 = Jump Scale
            EmitJcc32(code, refs, 0x84, "jump_scale");
            code.AddRange(new byte[] { 0x83, 0xFA, 0x04 });                   // state 4 = Free Flight
            EmitJcc32(code, refs, 0x85, "done");

            // Free Flight (state 4)
            EmitMovabsRdx(code, dFlyLift);
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x0A });             // xmm1 = flyLiftY
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x50, 0x10 });       // xmm2 = hover clamp (-0.05f)
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x5F, 0x54, 0x24, 0x04 }); // max(Y, hover)
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x58, 0xD1 });             // + flyLiftY
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x54, 0x24, 0x04 });
            EmitJmp32(code, refs, "done");

            lbl["jump_scale"] = code.Count;
            // CRITICAL: Reset state = 0 immediately on first frame (cheat-source-0.CT line 744)
            // so jump impulse never scales multi-frame combat/ragdoll Y velocities!
            code.AddRange(new byte[] { 0xC7, 0x40, 0x04, 0x00, 0x00, 0x00, 0x00 }); // mov dword ptr [rax+0x04], 0
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x50, 0x14 });       // track max Y
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x5F, 0x54, 0x24, 0x04 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x50, 0x14 });
            code.AddRange(new byte[] { 0x0F, 0x57, 0xD2 });                   // xorps xmm2, xmm2
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x5F, 0x54, 0x24, 0x04 }); // max(Y, 0)
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0x50, 0x08 });       // * (mult - 1)
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x4C, 0x24, 0x04 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x58, 0xCA });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x4C, 0x24, 0x04 });
            EmitJmp32(code, refs, "done");

            lbl["clamp_y"] = code.Count;
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x48, 0x10 });       // movss xmm1, [rax+0x10]
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x5F, 0x4C, 0x24, 0x04 });
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x4C, 0x24, 0x04 });

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x0F, 0x10, 0x04, 0x24 });
            code.AddRange(new byte[] { 0x0F, 0x10, 0x4C, 0x24, 0x10 });
            code.AddRange(new byte[] { 0x0F, 0x10, 0x54, 0x24, 0x20 });
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x38, 0x5A, 0x58 });
            code.AddRange(VANILLA_KISS);
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_KISS + 5);
            WriteCaveSlot("KISS", cf, OFF_CODE_KISS, OFF_CODE_DAYTIME, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 9. RVA_DAYTIME (cf + 0x1380, 5B) -> Freeze Time + Advance Hours / Instant Sleep
        // ---------------------------------------------------------------------
        {
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x52 });
            code.AddRange(new byte[] { 0x48, 0x81, 0xF9, 0x00, 0x00, 0x00, 0x10 });
            EmitJcc32(code, refs, 0x82, "done");
            EmitMovabsRax(code, dDtBase);
            code.AddRange(new byte[] { 0x8B, 0x51, 0x34, 0x89, 0x50, 0x10 }); // curHour
            code.AddRange(new byte[] { 0x8B, 0x51, 0x38, 0x89, 0x50, 0x14 }); // curMin
            code.AddRange(new byte[] { 0x48, 0x8B, 0x50, 0x08, 0x48, 0x85, 0xD2 });
            EmitJcc32(code, refs, 0x8E, "chk_freeze");
            code.AddRange(new byte[] { 0x48, 0x39, 0x51, 0x48 });
            EmitJcc32(code, refs, 0x86, "clr_adv");
            code.AddRange(new byte[] { 0x48, 0x29, 0x51, 0x48 });
            lbl["clr_adv"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0xC7, 0x40, 0x08, 0x00, 0x00, 0x00, 0x00 });
            lbl["chk_freeze"] = code.Count;
            code.AddRange(new byte[] { 0x8B, 0x10, 0x85, 0xD2 });
            EmitJcc32(code, refs, 0x88, "done");
            EmitJcc32(code, refs, 0x85, "freeze_on");
            code.AddRange(new byte[] { 0xC7, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xC6, 0x41, 0x2B, 0x00 });
            EmitJmp32(code, refs, "done");
            lbl["freeze_on"] = code.Count;
            code.AddRange(new byte[] { 0xC6, 0x41, 0x2B, 0x01 });
            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5A, 0x58 });
            code.AddRange(VANILLA_DAYTIME);
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_DAYTIME + 5);
            WriteCaveSlot("DAYTIME", cf, OFF_CODE_DAYTIME, OFF_CODE_EXP, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 10. RVA_EXP (cf + 0x1500, 8B) -> EXP Multiplier
        // ---------------------------------------------------------------------
        {
            long eBase = cf + OFF_CODE_EXP;
            var code = new List<byte>();
            code.AddRange(new byte[] { 0xC5, 0xFA, 0x59, 0xCB });             // vmulss xmm1, xmm0, xmm3
            code.AddRange(new byte[] { 0xC5, 0xF2, 0x59, 0x0D });             // vmulss xmm1, xmm1, [dExpMult]
            code.AddRange(BitConverter.GetBytes((int)(dExpMult - (eBase + code.Count + 4))));
            code.AddRange(new byte[] { 0xC5, 0xF2, 0x58, 0xD2 });             // vaddss xmm2, xmm1, xmm2
            EmitAbsJmp(code, baseAddr + RVA_EXP + 8);
            WriteCaveSlot("EXP", cf, OFF_CODE_EXP, OFF_CODE_DURABILITY, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 11. RVA_DURABILITY (cf + 0x1600, 11B) -> Proportional -30%/-50%/-75%/Infinite
        //     CRITICAL: runs VANILLA_DURABILITY LAST so SF matches (ax + r13w) for jns!
        // ---------------------------------------------------------------------
        {
            long durBase = cf + OFF_CODE_DURABILITY;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x51, 0x52, 0x56 });             // push rax, rcx, rdx, rsi
            EmitRipIncDword(code, durBase, cf + OFF_DATA_DUR_HITS);
            code.AddRange(new byte[] { 0x41, 0x0F, 0xBF, 0xC5 });             // movsx eax, r13w
            code.AddRange(new byte[] { 0x85, 0xC0 });                         // only wear (eax < 0)
            EmitJcc32(code, refs, 0x8D, "done");
            EmitMovabsRdx(code, dDurMode);
            code.AddRange(new byte[] { 0x8B, 0x0A, 0x85, 0xC9 });
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x83, 0xF9, 0x04 });
            EmitJcc32(code, refs, 0x8C, "scale_wear");
            code.AddRange(new byte[] { 0x45, 0x31, 0xED });                   // xor r13d, r13d (Infinite)
            EmitJmp32(code, refs, "done");

            lbl["scale_wear"] = code.Count;
            code.AddRange(new byte[] { 0xF7, 0xD8 });                         // neg eax (positive loss)
            code.AddRange(new byte[] { 0xBE, 0x46, 0x00, 0x00, 0x00 });       // esi = 70 (-30%)
            code.AddRange(new byte[] { 0x83, 0xF9, 0x01 });
            EmitJcc32(code, refs, 0x84, "mul_acc");
            code.AddRange(new byte[] { 0xBE, 0x32, 0x00, 0x00, 0x00 });       // esi = 50 (-50%)
            code.AddRange(new byte[] { 0x83, 0xF9, 0x02 });
            EmitJcc32(code, refs, 0x84, "mul_acc");
            code.AddRange(new byte[] { 0xBE, 0x19, 0x00, 0x00, 0x00 });       // esi = 25 (-75%)
            lbl["mul_acc"] = code.Count;
            code.AddRange(new byte[] { 0x0F, 0xAF, 0xC6 });                   // imul eax, esi
            EmitMovabsRdx(code, dDurAcc);
            code.AddRange(new byte[] { 0x03, 0x02 });                         // add eax, [dDurAcc]
            code.AddRange(new byte[] { 0x89, 0xD6 });                         // keep dDurAcc ptr in rsi? No rdx fits! Wait use rcx=100, keep ptr in rsi!
            code.AddRange(new byte[] { 0x48, 0x89, 0xD6 });                   // mov rsi, rdx (save dDurAcc ptr)
            code.AddRange(new byte[] { 0x31, 0xD2, 0xB9, 0x64, 0x00, 0x00, 0x00, 0xF7, 0xF1 }); // eax = q, edx = rem
            code.AddRange(new byte[] { 0x89, 0x16 });                         // [dDurAcc] = edx
            code.AddRange(new byte[] { 0xF7, 0xD8, 0x44, 0x89, 0xC5 });       // WRONG modrm! Use mov r13d, eax = 41 89 C5!
            // Re-encode mov r13d, eax cleanly right after removing bad bytes above
            code.RemoveRange(code.Count - 5, 5);
            code.AddRange(new byte[] { 0xF7, 0xD8, 0x41, 0x89, 0xC5 });       // neg eax; mov r13d, eax

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5E, 0x5A, 0x59, 0x58 });             // pop rsi, rdx, rcx, rax
            code.AddRange(VANILLA_DURABILITY);
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_DURABILITY + 11);
            WriteCaveSlot("DURABILITY", cf, OFF_CODE_DURABILITY, OFF_CODE_CRIME_EVAL, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 12. RVA_CRIME_EVAL (cf + 0x1800, 9B) -> Returns al = 7 (Innocent Sentinel)
        //     Callers check `cmp al, 7; setne bl` => al==7 suppresses detection/crime/bounty!
        // ---------------------------------------------------------------------
        {
            long crBase = cf + OFF_CODE_CRIME_EVAL;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x51, 0x52 });                         // push rcx, rdx (preserve rcx/rdx args if falling through)
            EmitRipIncDword(code, crBase, cf + OFF_DATA_CRIME_HITS);

            // Instant 100% bypass if thiefDet==3 || bounty==3 || pickpocket==3 || glove==3
            EmitMovabsRdx(code, dThiefDet);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x03 });
            EmitJcc32(code, refs, 0x84, "innocent");
            EmitMovabsRdx(code, dBounty);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x03 });
            EmitJcc32(code, refs, 0x84, "innocent");
            EmitMovabsRdx(code, dPickpocket);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x03 });
            EmitJcc32(code, refs, 0x84, "innocent");
            EmitMovabsRdx(code, dGloveMode);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x03 });
            EmitJcc32(code, refs, 0x84, "innocent");

            // Check partial ratios: highest suppression denominator wins
            code.AddRange(new byte[] { 0x50 });                               // push rax
            EmitMovabsRdx(code, dBounty);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x02 });                   // bounty 0G => 90%
            EmitJcc32(code, refs, 0x84, "mod10");
            EmitMovabsRdx(code, dThiefDet);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x02 });                   // stealth -80%
            EmitJcc32(code, refs, 0x84, "mod5");
            EmitMovabsRdx(code, dPickpocket);
            code.AddRange(new byte[] { 0x83, 0x3A, 0x02 });                   // pickpocket x3 => ~67%
            EmitJcc32(code, refs, 0x84, "mod3");
            // Otherwise if any == 1 => 50%
            EmitMovabsRdx(code, dThiefDet);
            code.AddRange(new byte[] { 0x8B, 0x0A });
            EmitMovabsRdx(code, dBounty);
            code.AddRange(new byte[] { 0x0B, 0x0A });
            EmitMovabsRdx(code, dPickpocket);
            code.AddRange(new byte[] { 0x0B, 0x0A, 0x85, 0xC9 });
            EmitJcc32(code, refs, 0x84, "fallthrough_pop3");
            code.AddRange(new byte[] { 0xB9, 0x02, 0x00, 0x00, 0x00 });
            EmitJmp32(code, refs, "do_mod");
            lbl["mod10"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x0A, 0x00, 0x00, 0x00 });
            EmitJmp32(code, refs, "do_mod");
            lbl["mod5"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x05, 0x00, 0x00, 0x00 });
            EmitJmp32(code, refs, "do_mod");
            lbl["mod3"] = code.Count;
            code.AddRange(new byte[] { 0xB9, 0x03, 0x00, 0x00, 0x00 });

            lbl["do_mod"] = code.Count;
            EmitRipIncDword(code, crBase, dCrimeCtr);
            EmitMovabsRdx(code, dCrimeCtr);
            code.AddRange(new byte[] { 0x8B, 0x02, 0x31, 0xD2, 0xF7, 0xF1 }); // eax=q, edx=ctr%denom
            code.AddRange(new byte[] { 0x85, 0xD2 });                         // pass only when rem == 0
            EmitJcc32(code, refs, 0x84, "fallthrough_pop3");
            code.Add(0x58);                                                   // pop rax
            lbl["innocent"] = code.Count;
            code.AddRange(new byte[] { 0x5A, 0x59 });                         // pop rdx, rcx
            code.AddRange(new byte[] { 0xB8, 0x07, 0x00, 0x00, 0x00, 0xC3 }); // mov eax, 7; ret

            lbl["fallthrough_pop3"] = code.Count;
            code.Add(0x58);                                                   // pop rax
            code.AddRange(new byte[] { 0x5A, 0x59 });                         // pop rdx, rcx
            code.AddRange(VANILLA_CRIME_EVAL);
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_CRIME_EVAL + 9);
            WriteCaveSlot("CRIME_EVAL", cf, OFF_CODE_CRIME_EVAL, OFF_CODE_HORSE_TAMING, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 13. RVA_HORSE_TAMING (cf + 0x1A00, 12B) -> x1.5 / x3 / x5 / Instant
        // ---------------------------------------------------------------------
        {
            long htBase = cf + OFF_CODE_HORSE_TAMING;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0xC5, 0xFA, 0x5E, 0xC1 });             // vdivss xmm0, xmm0, xmm1
            EmitRipIncDword(code, htBase, cf + OFF_DATA_TAMING_HITS);
            code.AddRange(new byte[] { 0x83, 0x3D });
            code.AddRange(BitConverter.GetBytes((int)(dTameInst - (htBase + code.Count + 5))));
            code.Add(0x00);
            EmitJcc32(code, refs, 0x84, "scale_tame");
            code.AddRange(new byte[] { 0xC5, 0xFA, 0x10, 0x05 });             // vmovss xmm0, [dConstOne]
            code.AddRange(BitConverter.GetBytes((int)(dConstOne - (htBase + code.Count + 4))));
            EmitJmp32(code, refs, "store_tame");
            lbl["scale_tame"] = code.Count;
            code.AddRange(new byte[] { 0xC5, 0xFA, 0x59, 0x05 });             // vmulss xmm0, xmm0, [dTameMult]
            code.AddRange(BitConverter.GetBytes((int)(dTameMult - (htBase + code.Count + 4))));
            lbl["store_tame"] = code.Count;
            code.AddRange(new byte[] { 0xC5, 0xFA, 0x11, 0x83, 0xD4, 0x0C, 0x00, 0x00 }); // vmovss [rbx+0xCD4], xmm0
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_HORSE_TAMING + 12);
            WriteCaveSlot("HORSE_TAMING", cf, OFF_CODE_HORSE_TAMING, OFF_CODE_BREATH_VITALS, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 14. RVA_BREATH_VITALS (cf + 0x1B80, 10B) -> Breath Slow / Infinite + Underwater Flag
        //     CRITICAL: runs `test rdx, rdx` (48 85 D2) LAST for `cmove` right after!
        //     Safety: checks actor dynamic breath flag ([r10+0x31] != 0), canonical heap
        //     pointers outside PE static image [peLow..peHigh], and valid breath floats
        //     BEFORE writing [r9+0x16C] so static default PE tables never fault (0xC0000005).
        // ---------------------------------------------------------------------
        {
            long brBase = cf + OFF_CODE_BREATH_VITALS;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x48, 0x8B, 0x51, 0x60 });             // mov rdx, [rcx+0x60]
            code.AddRange(new byte[] { 0x4C, 0x8B, 0xD1 });                   // mov r10, rcx
            EmitRipIncDword(code, brBase, cf + OFF_DATA_BREATH_HITS);

            code.AddRange(new byte[] { 0x41, 0x80, 0x7A, 0x31, 0x00 });       // cmp byte ptr [r10+0x31], 0
            EmitJcc32(code, refs, 0x84, "done_no_pop");                       // inactive/static sentinel actor
            code.AddRange(new byte[] { 0x48, 0x81, 0xFA, 0x00, 0x00, 0x00, 0x10 }); // cmp rdx, 0x10000000
            EmitJcc32(code, refs, 0x82, "done_no_pop");

            code.AddRange(new byte[] { 0x50, 0x51, 0x41, 0x51 });             // push rax, rcx, r9
            code.AddRange(new byte[] { 0x48, 0x89, 0xD0 });                   // mov rax, rdx
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "done");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x42, 0x18 });             // rax = [rdx+0x18]
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "done");
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x08 });                   // r9 = [rax]
            code.AddRange(new byte[] { 0x4C, 0x89, 0xC8 });                   // mov rax, r9
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "done");

            // Sanity-check maxBreath [r9+0x168] in [1.0f .. 100000.0f] and curBreath [r9+0x16C] >= 0
            code.AddRange(new byte[] { 0x41, 0x8B, 0x81, 0x68, 0x01, 0x00, 0x00 }); // mov eax, [r9+0x168]
            code.AddRange(new byte[] { 0x3D, 0x00, 0x00, 0x80, 0x3F });             // cmp eax, 1.0f
            EmitJcc32(code, refs, 0x82, "done");                                    // jb done
            code.AddRange(new byte[] { 0x3D, 0x00, 0x50, 0xC3, 0x47 });             // cmp eax, 100000.0f
            EmitJcc32(code, refs, 0x87, "done");                                    // ja done
            code.AddRange(new byte[] { 0x41, 0x8B, 0x89, 0x6C, 0x01, 0x00, 0x00 }); // mov ecx, [r9+0x16C]
            code.AddRange(new byte[] { 0x85, 0xC9 });                               // test ecx, ecx
            EmitJcc32(code, refs, 0x88, "done");                                    // js done (negative/NaN)
            code.AddRange(new byte[] { 0x39, 0xC1 });                               // cmp ecx, eax (cur vs max)
            EmitJcc32(code, refs, 0x83, "land_fast");                               // jae land_fast (on land; never write!)

            // Submerged! Save xmm0/xmm1 safely on stack
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });             // sub rsp, 0x28
            code.AddRange(new byte[] { 0x0F, 0x11, 0x04, 0x24 });
            code.AddRange(new byte[] { 0x0F, 0x11, 0x4C, 0x24, 0x10 });
            code.AddRange(new byte[] { 0xF3, 0x41, 0x0F, 0x10, 0x81, 0x6C, 0x01, 0x00, 0x00 }); // xmm0 = cur
            code.AddRange(new byte[] { 0xF3, 0x41, 0x0F, 0x10, 0x89, 0x68, 0x01, 0x00, 0x00 }); // xmm1 = max

            EmitMovabsRax(code, dUnderwater);
            code.AddRange(new byte[] { 0xC7, 0x00, 0x01, 0x00, 0x00, 0x00 });
            EmitMovabsRax(code, dBreathMode);
            code.AddRange(new byte[] { 0x8B, 0x00, 0x85, 0xC0 });
            EmitJcc32(code, refs, 0x8E, "pop_xmm");
            code.AddRange(new byte[] { 0x83, 0xF8, 0x04 });                   // Infinite oxygen => set cur = max
            EmitJcc32(code, refs, 0x8C, "partial_breath");
            code.AddRange(new byte[] { 0xF3, 0x41, 0x0F, 0x11, 0x89, 0x6C, 0x01, 0x00, 0x00 }); // movss [r9+0x16C], xmm1
            EmitJmp32(code, refs, "pop_xmm");

            lbl["partial_breath"] = code.Count;
            EmitMovabsRax(code, dPrevBreath);
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x08 });             // xmm1 = prevBreath
            code.AddRange(new byte[] { 0x0F, 0x2E, 0xC8 });                   // cmp prev vs cur
            EmitJcc32(code, refs, 0x86, "save_prev");
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x5C, 0xC8 });             // xmm1 = prev - cur (drop > 0)
            EmitMovabsRax(code, dBreathMult);
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x5E, 0x08 });             // xmm1 = (prev - cur) / mult
            EmitMovabsRax(code, dPrevBreath);
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x00 });             // xmm0 = prev
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x5C, 0xC1 });             // xmm0 = prev - smallerDrop
            code.AddRange(new byte[] { 0xF3, 0x41, 0x0F, 0x11, 0x81, 0x6C, 0x01, 0x00, 0x00 }); // write cur!
            lbl["save_prev"] = code.Count;
            EmitMovabsRax(code, dPrevBreath);
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x11, 0x00 });             // store latest cur -> dPrevBreath

            lbl["pop_xmm"] = code.Count;
            code.AddRange(new byte[] { 0x0F, 0x10, 0x04, 0x24 });
            code.AddRange(new byte[] { 0x0F, 0x10, 0x4C, 0x24, 0x10 });
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
            EmitJmp32(code, refs, "done");

            lbl["land_fast"] = code.Count;
            EmitMovabsRax(code, dUnderwater);
            code.AddRange(new byte[] { 0xC7, 0x00, 0x00, 0x00, 0x00, 0x00 });
            EmitMovabsRax(code, dPrevBreath);
            code.AddRange(new byte[] { 0x89, 0x08 });                         // store ecx (cur float bits)

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x41, 0x59, 0x59, 0x58 });             // pop r9, rcx, rax
            lbl["done_no_pop"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x85, 0xD2 });                   // test rdx, rdx (RFLAGS!)
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_BREATH_VITALS + 10);
            WriteCaveSlot("BREATH_VITALS", cf, OFF_CODE_BREATH_VITALS, OFF_CODE_SLOT_EXPAND, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 15. RVA_SLOT_EXPAND (cf + 0x1E00, 10B) -> Bag/Warehouse Safe Expansion (Bag <= 700!)
        // ---------------------------------------------------------------------
        {
            long seBase = cf + OFF_CODE_SLOT_EXPAND;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x51, 0x53, 0x52, 0x56 });       // push rax, rcx, rbx, rdx, rsi
            EmitRipIncDword(code, seBase, cf + OFF_DATA_SLOT_HITS);
            code.AddRange(new byte[] { 0x48, 0x89, 0xC8 });                   // mov rax, rcx
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "done");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x18 });       // restore rcx from [rsp+0x18]

            EmitMovabsRax(code, dInvMgrPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x08 });                   // store rcx -> dInvMgrPtr

            EmitMovabsRdx(code, dBagExpand);
            code.AddRange(new byte[] { 0x8B, 0x12, 0x85, 0xD2 });             // edx = extraSlots
            EmitJcc32(code, refs, 0x8E, "done");

            code.AddRange(new byte[] { 0x48, 0x8B, 0x41, 0x18 });             // rax = [rcx+0x18] (array)
            code.AddRange(new byte[] { 0x8B, 0x71, 0x20 });                   // esi = [rcx+0x20] (count)
            code.AddRange(new byte[] { 0x85, 0xF6 });
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x83, 0xFE, 0x20 });                   // safety cap count <= 32
            EmitJcc32(code, refs, 0x87, "done");
            code.Add(0x50);                                                   // push rax (array cursor)
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "pop_rax_done");
            code.Add(0x58);                                                   // pop rax

            lbl["loop"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x8B, 0x18 });                   // rbx = [rax]
            code.Add(0x50);                                                   // push rax
            code.AddRange(new byte[] { 0x48, 0x89, 0xD8 });                   // mov rax, rbx
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "pop_rax_next");
            code.Add(0x58);                                                   // pop rax
            code.AddRange(new byte[] { 0x66, 0x83, 0x7B, 0x10, 0x02 });       // invKey == 2 (player bag)?
            EmitJcc32(code, refs, 0x85, "chk_storage");
            // Player Bag: min 448 + edx, HARD CAP 700
            code.AddRange(new byte[] { 0x52, 0x81, 0xC2, 0xC0, 0x01, 0x00, 0x00 }); // push rdx; add edx, 448
            code.AddRange(new byte[] { 0x81, 0xFA, 0xBC, 0x02, 0x00, 0x00 }); // cmp edx, 700
            EmitJcc32(code, refs, 0x8E, "bag_apply");
            code.AddRange(new byte[] { 0xBA, 0xBC, 0x02, 0x00, 0x00 });       // clamp edx = 700
            lbl["bag_apply"] = code.Count;
            code.AddRange(new byte[] { 0x66, 0x39, 0x53, 0x14 });             // cmp [rbx+0x14], dx
            EmitJcc32(code, refs, 0x8D, "pop_rdx_next");
            code.AddRange(new byte[] { 0x66, 0x89, 0x53, 0x14 });
            lbl["pop_rdx_next"] = code.Count;
            code.Add(0x5A);                                                   // pop rdx
            EmitJmp32(code, refs, "next");

            lbl["chk_storage"] = code.Count;
            code.AddRange(new byte[] { 0x81, 0xFA, 0x84, 0x03, 0x00, 0x00 }); // edx == 900?
            EmitJcc32(code, refs, 0x85, "next");
            code.AddRange(new byte[] { 0x66, 0x39, 0x53, 0x14 });
            EmitJcc32(code, refs, 0x8D, "next");
            code.AddRange(new byte[] { 0x66, 0x89, 0x53, 0x14 });
            EmitJmp32(code, refs, "next");

            lbl["pop_rax_next"] = code.Count;
            code.Add(0x58);                                                   // pop rax

            lbl["next"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x83, 0xC0, 0x08, 0xFF, 0xCE }); // add rax, 8; dec esi
            EmitJcc32(code, refs, 0x85, "loop");
            EmitJmp32(code, refs, "done");

            lbl["pop_rax_done"] = code.Count;
            code.Add(0x58);                                                   // pop rax

            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x5E, 0x5A, 0x5B, 0x59, 0x58 });       // pop rsi, rdx, rbx, rcx, rax
            code.AddRange(VANILLA_SLOT_EXPAND);
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_SLOT_EXPAND + 10);
            WriteCaveSlot("SLOT_EXPAND", cf, OFF_CODE_SLOT_EXPAND, OFF_CODE_ROMANCE_TRUST, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 16. RVA_ROMANCE_TRUST (cf + 0x2000, 7B) -> Safe Capture & Telemetry Hook
        // ---------------------------------------------------------------------
        {
            long rtBase = cf + OFF_CODE_ROMANCE_TRUST;
            var code = new List<byte>();
            code.Add(0x50);                                                   // push rax
            EmitMovabsRax(code, dNpcMgrPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x08 });                   // [dNpcMgrPtr] = rcx
            EmitRipIncDword(code, rtBase, cf + OFF_DATA_ROM_HITS);
            code.Add(0x58);                                                   // pop rax
            code.AddRange(VANILLA_ROMANCE_TRUST);
            EmitAbsJmp(code, baseAddr + RVA_ROMANCE_TRUST + 7);
            WriteCaveSlot("ROMANCE_TRUST", cf, OFF_CODE_ROMANCE_TRUST, OFF_CODE_COUNT_ITEMS, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 16b. RVA_FRIENDLY_DAILY (cf + 0x2300, 5B) -> Daily Talk/Gift Cap Bypass
        // ---------------------------------------------------------------------
        {
            long fdBase = cf + OFF_CODE_FRIENDLY_DAILY;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x50, 0x51 });                         // push rax; push rcx
            EmitRipIncDword(code, fdBase, cf + OFF_DATA_ROM_HITS);
            EmitMovabsRax(code, cf + OFF_DATA_ROMANCE_MULT);
            code.AddRange(new byte[] { 0x81, 0x38, 0x66, 0x66, 0x86, 0x3F }); // cmp dword ptr [rax], 1.05f
            EmitJcc32(code, refs, 0x8E, "orig");
            code.AddRange(new byte[] { 0x48, 0x8B, 0x04, 0x24 });             // rax = rcx (FriendlyElement + 0x18)
            EmitCheckHeapPtrRax(code, refs, lbl, peLow, peHigh, "allow");
            code.AddRange(new byte[] { 0xC7, 0x40, 0x18, 0x00, 0x00, 0x00, 0x00 }); // gimmick _dailyCount = 0
            code.AddRange(new byte[] { 0xC7, 0x40, 0x28, 0x00, 0x00, 0x00, 0x00 }); // actionFrame _dailyCount = 0
            code.AddRange(new byte[] { 0xC7, 0x40, 0x38, 0x00, 0x00, 0x00, 0x00 }); // talk _dailyCount = 0
            lbl["allow"] = code.Count;
            code.AddRange(new byte[] { 0x59, 0x58 });                         // pop rcx; pop rax
            code.AddRange(new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }); // mov eax, 1; ret
            lbl["orig"] = code.Count;
            code.AddRange(new byte[] { 0x59, 0x58 });
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + 0x0D9A3B70L);
            WriteCaveSlot("FRIENDLY_DAILY", cf, OFF_CODE_FRIENDLY_DAILY, OFF_CODE_AFFINITY_ADD, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 16c. RVA_AFFINITY_ADD (cf + 0x2400, 5B) -> Affinity EXP Multiplier (r9)
        // ---------------------------------------------------------------------
        {
            long aaBase = cf + OFF_CODE_AFFINITY_ADD;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.Add(0x50);                                                   // push rax
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x20 });             // sub rsp, 0x20
            code.AddRange(new byte[] { 0x0F, 0x11, 0x04, 0x24 });             // movups [rsp], xmm0
            code.AddRange(new byte[] { 0x0F, 0x11, 0x4C, 0x24, 0x10 });       // movups [rsp+0x10], xmm1
            EmitRipIncDword(code, aaBase, cf + OFF_DATA_ROM_HITS);
            code.AddRange(new byte[] { 0x4D, 0x85, 0xC9 });                   // test r9, r9
            EmitJcc32(code, refs, 0x8E, "done");                              // jle done
            EmitMovabsRax(code, cf + OFF_DATA_ROMANCE_MULT);
            code.AddRange(new byte[] { 0x81, 0x38, 0x66, 0x66, 0x86, 0x3F }); // cmp [rax], 1.05f
            EmitJcc32(code, refs, 0x8E, "done");
            code.AddRange(new byte[] { 0x81, 0x38, 0x00, 0x00, 0xB4, 0x42 }); // cmp [rax], 90.0f
            EmitJcc32(code, refs, 0x8C, "scale");
            code.AddRange(new byte[] { 0x49, 0xC7, 0xC1, 0x10, 0x27, 0x00, 0x00 }); // mov r9, 10000
            EmitJmp32(code, refs, "done");
            lbl["scale"] = code.Count;
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x08 });             // movss xmm1, [rax]
            code.AddRange(new byte[] { 0xF3, 0x49, 0x0F, 0x2A, 0xC1 });       // cvtsi2ss xmm0, r9
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0xC1 });             // mulss xmm0, xmm1
            code.AddRange(new byte[] { 0xF3, 0x48, 0x0F, 0x2D, 0xC0 });       // cvtss2si rax, xmm0
            code.AddRange(new byte[] { 0x48, 0x3D, 0xA0, 0x86, 0x01, 0x00 }); // cmp rax, 100000
            EmitJcc32(code, refs, 0x8E, "set_r9");
            code.AddRange(new byte[] { 0x48, 0xC7, 0xC0, 0xA0, 0x86, 0x01, 0x00 }); // mov rax, 100000
            lbl["set_r9"] = code.Count;
            code.AddRange(new byte[] { 0x49, 0x89, 0xC1 });                   // mov r9, rax
            lbl["done"] = code.Count;
            code.AddRange(new byte[] { 0x0F, 0x10, 0x04, 0x24 });             // movups xmm0, [rsp]
            code.AddRange(new byte[] { 0x0F, 0x10, 0x4C, 0x24, 0x10 });       // movups xmm1, [rsp+0x10]
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x20 });             // add rsp, 0x20
            code.Add(0x58);                                                   // pop rax
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + 0x0D9A7780L);
            WriteCaveSlot("AFFINITY_ADD", cf, OFF_CODE_AFFINITY_ADD, OFF_CODE_FRIENDLY_ADD, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 16d. RVA_FRIENDLY_ADD (cf + 0x2500, 8B) -> Friendly EXP Multiplier (rbx+0x20 -> rax)
        // ---------------------------------------------------------------------
        {
            long faBase = cf + OFF_CODE_FRIENDLY_ADD;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.AddRange(new byte[] { 0x48, 0x8B, 0x43, 0x20 });             // mov rax, [rbx+0x20]
            EmitRipIncDword(code, faBase, cf + OFF_DATA_ROM_HITS);
            code.AddRange(new byte[] { 0x48, 0x85, 0xC0 });                   // test rax, rax
            EmitJcc32(code, refs, 0x8E, "store");                             // jle store
            code.Add(0x52);                                                   // push rdx
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x20 });             // sub rsp, 0x20
            code.AddRange(new byte[] { 0x0F, 0x11, 0x04, 0x24 });             // movups [rsp], xmm0
            code.AddRange(new byte[] { 0x0F, 0x11, 0x4C, 0x24, 0x10 });       // movups [rsp+0x10], xmm1
            EmitMovabsRdx(code, cf + OFF_DATA_ROMANCE_MULT);
            code.AddRange(new byte[] { 0x81, 0x3A, 0x66, 0x66, 0x86, 0x3F }); // cmp [rdx], 1.05f
            EmitJcc32(code, refs, 0x8E, "restore");
            code.AddRange(new byte[] { 0x81, 0x3A, 0x00, 0x00, 0xB4, 0x42 }); // cmp [rdx], 90.0f
            EmitJcc32(code, refs, 0x8C, "scale");
            code.AddRange(new byte[] { 0x48, 0xC7, 0xC0, 0x10, 0x27, 0x00, 0x00 }); // mov rax, 10000
            EmitJmp32(code, refs, "restore");
            lbl["scale"] = code.Count;
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x10, 0x0A });             // movss xmm1, [rdx]
            code.AddRange(new byte[] { 0xF3, 0x48, 0x0F, 0x2A, 0xC0 });       // cvtsi2ss xmm0, rax
            code.AddRange(new byte[] { 0xF3, 0x0F, 0x59, 0xC1 });             // mulss xmm0, xmm1
            code.AddRange(new byte[] { 0xF3, 0x48, 0x0F, 0x2D, 0xC0 });       // cvtss2si rax, xmm0
            code.AddRange(new byte[] { 0x48, 0x3D, 0xA0, 0x86, 0x01, 0x00 }); // cmp rax, 100000
            EmitJcc32(code, refs, 0x8E, "restore");
            code.AddRange(new byte[] { 0x48, 0xC7, 0xC0, 0xA0, 0x86, 0x01, 0x00 }); // mov rax, 100000
            lbl["restore"] = code.Count;
            code.AddRange(new byte[] { 0x0F, 0x10, 0x04, 0x24 });             // movups xmm0, [rsp]
            code.AddRange(new byte[] { 0x0F, 0x10, 0x4C, 0x24, 0x10 });       // movups xmm1, [rsp+0x10]
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x20 });             // add rsp, 0x20
            code.Add(0x5A);                                                   // pop rdx
            lbl["store"] = code.Count;
            code.AddRange(new byte[] { 0x48, 0x89, 0x45, 0xCF });             // mov [rbp-0x31], rax
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_FRIENDLY_ADD + 8);
            WriteCaveSlot("FRIENDLY_ADD", cf, OFF_CODE_FRIENDLY_ADD, OFF_CODE_SAVE_ITEM_BODY, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 17. RVA_COUNT_ITEMS (cf + 0x2100, 10B) -> Captures InventoryManager* (rcx) + Telemetry
        // ---------------------------------------------------------------------
        {
            long ciBase = cf + OFF_CODE_COUNT_ITEMS;
            var code = new List<byte>();
            var refs = new List<Rel32Ref>();
            var lbl = new Dictionary<string, int>();
            code.Add(0x50);                                                   // push rax
            EmitRipIncDword(code, ciBase, cf + OFF_DATA_CNT_HITS);
            code.AddRange(new byte[] { 0x48, 0x81, 0xF9, 0x00, 0x00, 0x00, 0x10 }); // cmp rcx, 0x10000000
            EmitJcc32(code, refs, 0x82, "ci_done");
            EmitMovabsRax(code, dInvMgrPtr);
            code.AddRange(new byte[] { 0x48, 0x89, 0x08 });                   // [dInvMgrPtr] = rcx
            lbl["ci_done"] = code.Count;
            code.Add(0x58);                                                   // pop rax
            code.AddRange(VANILLA_COUNT_ITEMS);
            ResolveLabels(code, lbl, refs);
            EmitAbsJmp(code, baseAddr + RVA_COUNT_ITEMS + 10);
            WriteCaveSlot("COUNT_ITEMS", cf, OFF_CODE_COUNT_ITEMS, OFF_CODE_WANTED_EVAL, code.ToArray());
        }

        // ---------------------------------------------------------------------
        // 18. RVA_WANTED_EVAL (cf + 0x2200, 7B) -> Safe Telemetry Hook
        // ---------------------------------------------------------------------
        {
            long wBase = cf + OFF_CODE_WANTED_EVAL;
            var code = new List<byte>();
            EmitRipIncDword(code, wBase, cf + OFF_DATA_WANTED_HITS);
            code.AddRange(VANILLA_WANTED_EVAL);
            EmitAbsJmp(code, baseAddr + RVA_WANTED_EVAL + 7);
            WriteCaveSlot("WANTED_EVAL", cf, OFF_CODE_WANTED_EVAL, OFF_CODE_FRIENDLY_DAILY, code.ToArray());
        }

        // Note: Never blindly write VANILLA_* into game memory during InitTrampolines()!
        hookPlayerInstalled = hookStatsInstalled = hookCommitInstalled = hookDefenseInstalled = false;
        hookDmgInstalled = hookSaveItemInstalled = hookSelectedItemInstalled = hookKissInstalled = false;
        hookDaytimeInstalled = hookExpInstalled = hookDurabilityInstalled = hookCrimeEvalInstalled = false;
        hookHorseTamingInstalled = hookBreathVitalsInstalled = hookSlotExpandInstalled = false;
        hookRomanceTrustInstalled = hookFriendlyDailyInstalled = hookAffinityAddInstalled = hookFriendlyAddInstalled = hookCountItemsInstalled = hookWantedEvalInstalled = false;

        trampolinesReady = true;
        EngLog(string.Format("InitTrampolines READY @ 0x{0:X} (0x4000 bytes, 21 hooks)", cf));
        return true;
        } catch (Exception ex) {
            EngLog("InitTrampolines FAILED: " + ex.Message);
            FreeCaveOnInitFail();
            return false;
        }
    }

    // =========================================================================
    // Public Control & Telemetry Helpers
    // =========================================================================
    public static bool TriggerManualJumpImpulse(float impulse = 320.0f) {
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero || baseAddr == 0) return false;
        string dummy;
        var procs = Process.GetProcessesByName("CrimsonDesert");
        try {
            if (procs.Length == 0) return false;
            Process p = procs[0];
            double up = (DateTime.UtcNow - p.StartTime.ToUniversalTime()).TotalSeconds;
            if (!IsWorldLoadedAndSettled(p.Id, p.WorkingSet64, up, out dummy)) return false;
        } finally {
            foreach (var pr in procs) pr.Dispose();
        }
        long cf = caveFling.ToInt64();
        float scale = impulse > 10.0f ? Math.Max(0.5f, impulse / 320.0f) : Math.Max(0.0f, impulse);
        lastJumpTriggerUtc = DateTime.UtcNow;
        WriteBytes(cf + OFF_DATA_KISS_JUMPSC, BitConverter.GetBytes(scale));
        WriteBytes(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(2));
        SyncSingleHook("KISS", true, ref hookKissInstalled, baseAddr + RVA_KISS, cf + OFF_CODE_KISS, VANILLA_KISS);
        return true;
    }

    public static void DisableFreeFlightImmediate() {
        GlideNoFallEnabled = false;
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero) return;
        try {
            long cf = caveFling.ToInt64();
            WriteBytes(cf + OFF_DATA_SPEC_CONV, BitConverter.GetBytes(0));
            WriteBytes(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(0));
            WriteBytes(cf + OFF_DATA_FLY_LIFTY, BitConverter.GetBytes(0.0f));
            WriteBytes(cf + OFF_DATA_KISS_CLAMPY, BitConverter.GetBytes(-1.5f));
        } catch { }
    }

    public static bool TriggerImmediateRest() {
        sleepHealUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
        return AdvanceDaytimeHours(8);
    }

    public static bool SetFreezeDaytime(bool freeze) {
        FreezeDaytimeEnabled = freeze;
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero) return false;
        WriteBytes(caveFling.ToInt64() + OFF_DATA_FREEZE_TIME, BitConverter.GetBytes(freeze ? 1 : 0));
        return true;
    }

    public static bool AdvanceDaytimeHours(int hours) {
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero) return false;
        long ticksToAdvance = Math.Max(1, hours) * 180000L;
        WriteBytes(caveFling.ToInt64() + OFF_DATA_ADVANCE_TICKS, BitConverter.GetBytes(ticksToAdvance));
        return true;
    }

    public static bool GetLiveWorldTime(out int hour, out int minute) {
        hour = -1; minute = -1;
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero) return false;
        long cf = caveFling.ToInt64();
        byte[] hBytes = ReadBytes(cf + OFF_DATA_CUR_HOUR, 4);
        byte[] mBytes = ReadBytes(cf + OFF_DATA_CUR_MIN, 4);
        if (hBytes != null && mBytes != null) {
            int h = BitConverter.ToInt32(hBytes, 0);
            int m = BitConverter.ToInt32(mBytes, 0);
            if (h >= 0 && h < 24 && m >= 0 && m < 60 && (h > 0 || m > 0)) {
                hour = h; minute = m; return true;
            }
        }
        return false;
    }

    public static long ReadExactSilverNow() {
        EnsureProcessAttached();
        if (hProc != IntPtr.Zero && caveFling != IntPtr.Zero) {
            long cf = caveFling.ToInt64();
            long sPtr, curSil;
            if (TryFindPlayerBagSilverRecord(out sPtr, out curSil, false) && curSil >= 0) {
                return curSil;
            }
            long cachedSPtr = ReadI64(cf + OFF_DATA_SILVER_PTR);
            if (!IsValidGameHeapPtr(cachedSPtr) && IsValidGameHeapPtr(_lastValidSilverPtr)) cachedSPtr = _lastValidSilverPtr;
            if (IsValidGameHeapPtr(cachedSPtr) && TryVerifyRecordItemKey(cachedSPtr, 1)) {
                long v = ReadI64(cachedSPtr + 0x10);
                if (v >= 0) {
                    _cachedSilver = v;
                    _lastSilverQueryUtc = DateTime.UtcNow;
                    PersistSilverCacheFile(v);
                    return v;
                }
            }
        }
        return GetSaveFileSilverAmount();
    }

    public static bool InjectGoldToMemory(long targetGold) {
        if (targetGold <= 0) return false;
        _cachedSilver = targetGold;
        _lastSilverQueryUtc = DateTime.UtcNow;
        PersistSilverCacheFile(targetGold);

        EnsureProcessAttached();
        if (hProc != IntPtr.Zero && caveFling != IntPtr.Zero) {
            long cf = caveFling.ToInt64();
            byte[] goldBytes = BitConverter.GetBytes(targetGold);
            long sPtr, curSil;
            // Pass targetGold directly so TryFindPlayerBagSilverRecord writes to ALL matched containers (0x18 Display + 0xD0 Authoritative)!
            bool resolvedDirect = TryFindPlayerBagSilverRecord(out sPtr, out curSil, false, targetGold);
            if (!resolvedDirect) {
                long cachedSPtr = ReadI64(cf + OFF_DATA_SILVER_PTR);
                if (!IsValidGameHeapPtr(cachedSPtr) && IsValidGameHeapPtr(_lastValidSilverPtr)) cachedSPtr = _lastValidSilverPtr;
                if (IsValidGameHeapPtr(cachedSPtr) && TryVerifyRecordItemKey(cachedSPtr, 1)) {
                    sPtr = cachedSPtr;
                    curSil = ReadI64(cachedSPtr + 0x10);
                    WriteBytes(sPtr + 0x10, goldBytes);
                    resolvedDirect = true;
                }
            }
            if (resolvedDirect && sPtr != 0) {
                WriteBytes(cf + OFF_DATA_CUR_SILVER, goldBytes);
                WriteBytes(cf + OFF_DATA_PEND_GOLD, goldBytes); // Keep in pendGold so next save/UI refresh also syncs any newly opened container!
                EngLog(string.Format("InjectGoldToMemory LIVE OK @0x{0:X}: {1} -> {2}", sPtr, curSil, targetGold));
            } else {
                WriteBytes(cf + OFF_DATA_PEND_GOLD, goldBytes);
                SyncSingleHook("SLOT_EXPAND", true, ref hookSlotExpandInstalled, baseAddr + RVA_SLOT_EXPAND, cf + OFF_CODE_SLOT_EXPAND, VANILLA_SLOT_EXPAND);
                SyncSingleHook("COUNT_ITEMS", true, ref hookCountItemsInstalled, baseAddr + RVA_COUNT_ITEMS, cf + OFF_CODE_COUNT_ITEMS, VANILLA_COUNT_ITEMS);
                if (!ExtFlingRunning()) {
                    SyncSingleHook("SAVE_ITEM", true, ref hookSaveItemInstalled, baseAddr + RVA_SAVE_ITEM, cf + OFF_CODE_SAVE_ITEM, VANILLA_SAVE_ITEM);
                    SyncSingleHook("SELECTED_ITEM", true, ref hookSelectedItemInstalled, baseAddr + RVA_SELECTED_ITEM, cf + OFF_CODE_SELECTED_ITEM, VANILLA_SELECTED_ITEM);
                }
                EngLog(string.Format("InjectGoldToMemory DEFERRED pendGold={0} (awaiting invMgr/saveItem)", targetGold));
            }
            return true;
        }

        if (hProc == IntPtr.Zero) {
            try {
                string rootDir = AppDomain.CurrentDomain.BaseDirectory;
                string pyPath = Path.Combine(rootDir, @"tuning\Apply-RealCheats.py");
                if (!File.Exists(pyPath)) pyPath = Path.Combine(rootDir, "Apply-RealCheats.py");
                if (File.Exists(pyPath)) {
                    var psi = new ProcessStartInfo {
                        FileName = "python",
                        Arguments = string.Format("\"{0}\" --gold-set {1}", pyPath, targetGold),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process proc = Process.Start(psi)) {
                        if (proc != null) proc.WaitForExit(4000);
                    }
                }
            } catch { }
        }

        return true;
    }

    public static long GetLiveSilverAmount() {
        if (hProc != IntPtr.Zero && caveFling != IntPtr.Zero) {
            if ((DateTime.UtcNow - _lastSilverQueryUtc).TotalMilliseconds >= 1500.0 || _cachedSilver < 0) {
                long cf = caveFling.ToInt64();
                long sPtr = ReadI64(cf + OFF_DATA_SILVER_PTR);
                if (IsValidGameHeapPtr(sPtr) && TryVerifyRecordItemKey(sPtr, 1)) {
                    long v = ReadI64(sPtr + 0x10);
                    if (v >= 0) {
                        _cachedSilver = v;
                        _lastSilverQueryUtc = DateTime.UtcNow;
                        PersistSilverCacheFile(v);
                        return v;
                    }
                }
                long cVal = ReadI64(cf + OFF_DATA_CUR_SILVER);
                if (cVal > 0) {
                    _cachedSilver = cVal;
                    _lastSilverQueryUtc = DateTime.UtcNow;
                    return cVal;
                }
            }
            if (_cachedSilver >= 0) return _cachedSilver;
        }
        return GetSaveFileSilverAmount();
    }

    public static void GetSelectedItemInfo(out int itemId, out long count) {
        itemId = 0; count = 0;
        if (hProc == IntPtr.Zero || caveFling == IntPtr.Zero) return;
        long cf = caveFling.ToInt64();
        byte[] idBytes = ReadBytes(cf + OFF_DATA_SEL_ID, 4);
        if (idBytes != null) itemId = BitConverter.ToInt32(idBytes, 0);
        byte[] cntBytes = ReadBytes(cf + OFF_DATA_SEL_COUNT, 8);
        if (cntBytes != null) count = BitConverter.ToInt64(cntBytes, 0);
    }

    public static bool EditSelectedItem(int targetId, long newCount) {
        if (targetId <= 0 || hProc == IntPtr.Zero || caveFling == IntPtr.Zero) return false;
        long cf = caveFling.ToInt64();
        long iPtr = ReadI64(cf + OFF_DATA_SEL_PTR);
        if (IsValidGameHeapPtr(iPtr) && TryVerifyRecordItemKey(iPtr, targetId)) {
            WriteBytes(iPtr + 0x10, BitConverter.GetBytes(newCount));
            WriteBytes(cf + OFF_DATA_SEL_COUNT, BitConverter.GetBytes(newCount));
            return true;
        }
        WriteBytes(cf + OFF_DATA_EDIT_AMT, BitConverter.GetBytes(newCount));
        WriteBytes(cf + OFF_DATA_EDIT_ID, BitConverter.GetBytes(targetId));
        return true;
    }

    public static void SetGodMode(bool enable) {
        EnsureProcessAttached();
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_GODMODE, BitConverter.GetBytes(enable ? 1 : 0));
        WriteFlingCtl(FLING_ADDR_GODMODE, BitConverter.GetBytes(enable ? 1 : 0));
        if (enable) SetDefense(999999);
    }

    public static void SetDefenseMode(int mode) {
        int def = 0;
        if (mode == 1) def = 1428;
        else if (mode == 2) def = 2500;
        else if (mode == 3) def = 5000;
        else if (mode >= 4) def = 999999;
        SetDefense(def);
        SetGodMode(mode >= 4);
    }

    public static void SetDefense(int val) {
        EnsureProcessAttached();
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_DEFENSE, BitConverter.GetBytes(val));
        int flingDef = val > 0 ? Math.Max(1, val / 100) : 0;
        WriteFlingCtl(FLING_ADDR_DEFENSE, BitConverter.GetBytes(flingDef));
    }

    public static void SetInfiniteStamina(bool enable) {
        SetStaminaMode(enable ? 4 : 0);
    }

    public static void SetStaminaMode(int mode) {
        EnsureProcessAttached();
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_STAMINA_MODE, BitConverter.GetBytes(Math.Max(0, Math.Min(4, mode))));
        WriteFlingCtl(FLING_ADDR_STAMINA, BitConverter.GetBytes(mode > 0 ? 1 : 0));
        SetMaxStamina(mode >= 2 ? 2000 : (mode == 1 ? 1500 : 0));
    }

    public static void SetInfiniteSpirit(bool enable) {
        SetCooldownMode(enable ? 4 : 0);
    }

    public static void SetCooldownMode(int mode) {
        EnsureProcessAttached();
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_COOLDOWN_MODE, BitConverter.GetBytes(Math.Max(0, Math.Min(4, mode))));
        WriteFlingCtl(FLING_ADDR_SPIRIT, BitConverter.GetBytes(mode > 0 ? 1 : 0));
        SetMaxSpirit(mode >= 2 ? 2000 : (mode == 1 ? 1500 : 0));
    }

    public static void SetInfiniteMountStamina(bool enable) {
        SetMountStaminaMode(enable ? 4 : 0);
    }

    public static void SetMountStaminaMode(int mode) {
        EnsureProcessAttached();
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_MOUNT_STA, BitConverter.GetBytes(Math.Max(0, Math.Min(4, mode))));
        WriteFlingCtl(FLING_ADDR_MOUNT_STA, BitConverter.GetBytes(mode > 0 ? 1 : 0));
    }

    public static void SetOneHitKill(bool enable) {
        EnsureProcessAttached();
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_DMG_MODE, BitConverter.GetBytes(enable ? 1000 : 0));
        WriteFlingCtl(FLING_ADDR_DMG, BitConverter.GetBytes(enable ? 1000 : 0));
    }

    public static void SetDamageMultiplier(float mult) {
        EnsureProcessAttached();
        int m = (mult >= 900.0f) ? 1000 : (mult > 1.05f ? (int)Math.Round(mult * 2.0f) : 0);
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_DMG_MODE, BitConverter.GetBytes(m));
        WriteFlingCtl(FLING_ADDR_DMG, BitConverter.GetBytes(m));
    }

    public static void SetMaxHp(int hp) {
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_MAX_HP, BitConverter.GetBytes(hp));
    }

    public static void SetMaxStamina(int sta) {
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_MAX_STA, BitConverter.GetBytes(sta));
    }

    public static void SetMaxSpirit(int spirit) {
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_MAX_SPIRIT, BitConverter.GetBytes(spirit));
    }

    public static void SetSpeedMultiplier(float speed) {
        if (caveFling != IntPtr.Zero) WriteBytes(caveFling.ToInt64() + OFF_DATA_KISS_MULT, BitConverter.GetBytes(speed));
    }

    public static void SetJumpMultiplier(float mult) {
        if (caveFling != IntPtr.Zero) {
            float scale = Math.Max(0.0f, mult - 1.0f);
            WriteBytes(caveFling.ToInt64() + OFF_DATA_KISS_JUMPSC, BitConverter.GetBytes(scale));
        }
    }

    public static void SetJumpMultiplier(int mult) {
        SetJumpMultiplier((float)mult);
    }

    public static void QueryLiveCheatFlags(out bool god, out bool sta, out bool spi, out bool def, out bool ohk, out bool mountSta) {
        god = false; sta = false; spi = false; def = false; ohk = false; mountSta = false;
        if (!EnsureProcessAttached()) return;
        byte[] bSta = ReadFlingCtl(FLING_ADDR_STAMINA, 4);
        int sVal = (bSta != null) ? BitConverter.ToInt32(bSta, 0) : 0;
        if (sVal == 0 && caveFling != IntPtr.Zero) {
            byte[] b = ReadBytes(caveFling.ToInt64() + OFF_DATA_STAMINA_MODE, 4);
            if (b != null) sVal = BitConverter.ToInt32(b, 0);
        }
        sta = (sVal > 0);

        byte[] bHp = ReadFlingCtl(FLING_ADDR_GODMODE, 4);
        int hpVal = (bHp != null) ? BitConverter.ToInt32(bHp, 0) : 0;
        if (hpVal == 0 && caveFling != IntPtr.Zero) {
            byte[] b = ReadBytes(caveFling.ToInt64() + OFF_DATA_GODMODE, 4);
            if (b != null) hpVal = BitConverter.ToInt32(b, 0);
        }
        god = (hpVal > 0);

        byte[] bSpi = ReadFlingCtl(FLING_ADDR_SPIRIT, 4);
        int spiVal = (bSpi != null) ? BitConverter.ToInt32(bSpi, 0) : 0;
        if (spiVal == 0 && caveFling != IntPtr.Zero) {
            byte[] b = ReadBytes(caveFling.ToInt64() + OFF_DATA_COOLDOWN_MODE, 4);
            if (b != null) spiVal = BitConverter.ToInt32(b, 0);
        }
        spi = (spiVal > 0);

        byte[] bMount = ReadFlingCtl(FLING_ADDR_MOUNT_STA, 4);
        int mVal = (bMount != null) ? BitConverter.ToInt32(bMount, 0) : 0;
        if (mVal == 0 && caveFling != IntPtr.Zero) {
            byte[] b = ReadBytes(caveFling.ToInt64() + OFF_DATA_MOUNT_STA, 4);
            if (b != null) mVal = BitConverter.ToInt32(b, 0);
        }
        mountSta = (mVal > 0);

        byte[] bDef = ReadFlingCtl(FLING_ADDR_DEFENSE, 4);
        int defVal = (bDef != null) ? BitConverter.ToInt32(bDef, 0) : 0;
        if (defVal == 0 && caveFling != IntPtr.Zero) {
            byte[] b = ReadBytes(caveFling.ToInt64() + OFF_DATA_DEFENSE, 4);
            if (b != null) defVal = BitConverter.ToInt32(b, 0);
        }
        def = (defVal > 0);

        byte[] bDmg = ReadFlingCtl(FLING_ADDR_DMG, 4);
        int dmgVal = (bDmg != null) ? BitConverter.ToInt32(bDmg, 0) : 0;
        if (dmgVal == 0 && caveFling != IntPtr.Zero) {
            byte[] b = ReadBytes(caveFling.ToInt64() + OFF_DATA_DMG_MODE, 4);
            if (b != null) dmgVal = BitConverter.ToInt32(b, 0);
        }
        ohk = (dmgVal >= 900);
    }

    public static void QueryLiveCheatFlags(out bool god, out bool sta, out bool spi, out bool def, out bool ohk) {
        bool dummy;
        QueryLiveCheatFlags(out god, out sta, out spi, out def, out ohk, out dummy);
    }

    public static string GetLiveCheatStatusString() {
        bool god, sta, spi, def, ohk, msta;
        QueryLiveCheatFlags(out god, out sta, out spi, out def, out ohk, out msta);
        if (hProc == IntPtr.Zero) return "게임 미연결";
        return string.Format("기력:[{0}] | 체력:[{1}] | 쿨/정기:[{2}] | 탈것:[{3}] | 방어:[{4}] | 공격:[{5}]",
            sta ? "ON" : "OFF",
            god ? "무적" : "OFF",
            spi ? "ON" : "OFF",
            msta ? "ON" : "OFF",
            def ? "ON" : "OFF",
            ohk ? "일격" : (_lastSyncedState.DamageDealtMultiplier > 1.05f ? "x" + _lastSyncedState.DamageDealtMultiplier : "OFF")
        );
    }

    public static long GetSaveFileSilverAmount() {
        // Pure O(1) cache file read with mtime guard: never spawn python during UI/gameplay or --self-test.
        try {
            DateTime now = DateTime.UtcNow;
            if (_cachedSilver < 0 || (now - _lastSilverCacheFileCheckUtc).TotalSeconds >= 2.0) {
                _lastSilverCacheFileCheckUtc = now;
                string cachePath = GetSilverCacheFilePath();
                if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath)) {
                    DateTime wt = File.GetLastWriteTimeUtc(cachePath);
                    if (_cachedSilver < 0 || wt != _lastSilverCacheFileMtimeUtc) {
                        _lastSilverCacheFileMtimeUtc = wt;
                        string raw = File.ReadAllText(cachePath, Encoding.UTF8).Trim();
                        long parsed;
                        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed) && parsed >= 0) {
                            _cachedSilver = parsed;
                        }
                    }
                }
            }
        } catch { }
        return _cachedSilver;
    }

    public static void Detach() {
        _itemKeyByTableIdx.Clear();
        _caveParamCache.Clear();
        _lastValidPlayerPtr = 0;
        _lastValidStaPtr = 0;
        _lastValidSpiPtr = 0;
        _lastValidHpPtr = 0;
        _lastValidInvMgrPtr = 0;
        _lastValidBagContPtr = 0;
        _lastValidSilverPtr = 0;
        _arrowSanitizedPid = 0;
        _cachedSilver = -1L;
        _lastSilverQueryUtc = DateTime.MinValue;
        _lastFullSilverScanUtc = DateTime.MinValue;
        _lastSilverCacheFileCheckUtc = DateTime.MinValue;
        _lastWorldCheckUtc = DateTime.MinValue;
        _cachedWorldSettled = false;
        _cachedWorldStatus = "";
        _cachedWorkingSet64 = 0;
        _cachedStartTimeUtc = DateTime.MinValue;
        if (cachedProc != null) { try { cachedProc.Dispose(); } catch { } cachedProc = null; }
        if (hProc == IntPtr.Zero) return;
        try {
            if (caveFling != IntPtr.Zero) {
                long cf = caveFling.ToInt64();
                WriteBytes(cf + OFF_DATA_KISS_MULT, BitConverter.GetBytes(1.0f));
                WriteBytes(cf + OFF_DATA_KISS_JUMPSC, BitConverter.GetBytes(0.0f));
                WriteBytes(cf + OFF_DATA_KISS_STATE, BitConverter.GetBytes(0));
                WriteBytes(cf + OFF_DATA_MOUNT_SPEED, BitConverter.GetBytes(1.0f));
                WriteBytes(cf + OFF_DATA_SWIM_MULT, BitConverter.GetBytes(1.0f));
                WriteBytes(cf + OFF_DATA_SPEC_CONV, BitConverter.GetBytes(0));
            }
            RevertAllHooks();
            System.Threading.Thread.Sleep(60);
            if (caveFling != IntPtr.Zero) {
                VirtualFreeEx(hProc, caveFling, 0, MEM_RELEASE);
                caveFling = IntPtr.Zero;
            }
        } catch { }
        CloseHandle(hProc);
        hProc = IntPtr.Zero;
        attachedPid = 0;
        baseAddr = 0;
        trampolinesReady = false;
        cachedLogPath = null;
        prevEndLoadCount = -1;
        lastObservedSaveLoadUtc = DateTime.MinValue;
        hookPlayerInstalled = hookStatsInstalled = hookCommitInstalled = hookDefenseInstalled = false;
        hookDmgInstalled = hookSaveItemInstalled = hookSelectedItemInstalled = hookKissInstalled = false;
        hookDaytimeInstalled = hookExpInstalled = hookDurabilityInstalled = hookCrimeEvalInstalled = false;
        hookHorseTamingInstalled = hookBreathVitalsInstalled = hookSlotExpandInstalled = false;
        hookRomanceTrustInstalled = hookFriendlyDailyInstalled = hookAffinityAddInstalled = hookFriendlyAddInstalled = hookCountItemsInstalled = hookWantedEvalInstalled = false;
    }

    public static void ApplyVisualContrastCurve(bool enable) {
        // Intentional no-op on Pascal GPUs to prevent DX12 swapchain gamma LUT stalls.
    }
}
