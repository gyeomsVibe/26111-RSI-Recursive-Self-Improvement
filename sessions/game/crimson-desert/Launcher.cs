using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class ConfigStore {
    internal static XmlDocument Load(string path) {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2097152 };
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(path, settings)) doc.Load(reader);
        if (doc.DocumentElement == null || doc.DocumentElement.Name != "EngineOptionSave")
            throw new InvalidDataException("게임 그래픽 설정 형식이 아닙니다.");
        return doc;
    }
    public static void Validate(string path) { Load(path); }
    public static void RestoreGraphics(string source, string target, string backups) {
        var saved = Load(source); var current = Load(target);
        if (saved.DocumentElement.GetAttribute("ReflectObjectXMLDataVersion") != current.DocumentElement.GetAttribute("ReflectObjectXMLDataVersion"))
            throw new InvalidDataException("프로필과 현재 게임 설정 형식 버전이 다릅니다.");
        var from = saved.SelectSingleNode("/EngineOptionSave/EngineOption") as XmlElement;
        var to = current.SelectSingleNode("/EngineOptionSave/EngineOption") as XmlElement;
        if (from == null || to == null || !from.HasAttribute("Template") || !saved.DocumentElement.HasAttribute("_selectPreset"))
            throw new InvalidDataException("그래픽 프리셋 정보를 찾지 못했습니다.");
        current.DocumentElement.SetAttribute("_selectPreset", saved.DocumentElement.GetAttribute("_selectPreset"));
        to.SetAttribute("Template", from.GetAttribute("Template"));
        foreach (var name in new[] { "_videoOption", "_resolutionOption", "_graphicsOption" }) {
            string check = "_check_" + name;
            if (from.HasAttribute(check)) to.SetAttribute(check, from.GetAttribute(check));
            var old = to.SelectSingleNode("*[@Name='" + name + "']");
            var replacement = from.SelectSingleNode("*[@Name='" + name + "']");
            if (old != null) to.RemoveChild(old);
            if (replacement != null) to.AppendChild(current.ImportNode(replacement, true));
        }
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".graphics.tmp";
        try { current.Save(staging); Restore(staging, target, backups); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    public static void ApplyCustomGraphics(string target, string backups, string fov, string zoom, string shake, string blur, string scale, string tex, string model, string water) {
        var doc = Load(target);
        var opt = doc.SelectSingleNode("/EngineOptionSave/EngineOption") as XmlElement;
        if (opt != null) opt.SetAttribute("Template", "/OS_M_Windows::Default.engineoption");

        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionVideo", "OptionFloat", "_cameraFovRate", "_value", fov);
        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionVideo", "OptionFloat", "_cameraZoomDistanceRate", "_value", zoom);
        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionVideo", "OptionInt", "_cameraShakeScale", "_value", shake);
        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionVideo", "OptionInt", "_screenBlurScale", "_value", blur);

        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionResolution", "OptionStringVector", "_upscaleModeSelect", "_value", "AMD FSR Upscaling 3.1");
        // Ensure scale is one of verified engine enums: AA (100% Native 1080p), 67%, 59%, 50%
        string verifiedScale = scale.Contains("AA") ? "AA" : scale.Contains("50") ? "50%" : scale.Contains("59") ? "59%" : "67%";
        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionResolution", "EnumSelectResolutionScale", "_upscaleResolution", "_select", verifiedScale);

        // Graphics inherited from Low.engineoptiongraphics, overriding ONLY the 3 verified custom quality nodes
        // (Adding extra tags causes BlackSpace Engine XML deserializer to abort during startup!)
        var gfx = doc.SelectSingleNode("/EngineOptionSave/EngineOption/EngineOptionGraphics") as XmlElement;
        if (gfx != null) {
            gfx.SetAttribute("Template", "/OS_M_Windows::Medium.engineoptiongraphics");
            gfx.RemoveAll();
            gfx.SetAttribute("Name", "_graphicsOption");
            gfx.SetAttribute("Template", "/OS_M_Windows::Medium.engineoptiongraphics");
        }
        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionGraphics", "EnumSelectQualityLevel", "_modelQualityLevelSelect", "_select", model);
        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionGraphics", "EnumSelectQualityLevel", "_textureQualityLevelSelect", "_select", tex);
        SetOrAddNodeAttr(doc, "/EngineOptionSave/EngineOption/EngineOptionGraphics", "EnumSelectQualityLevel", "_waterQualityLevelSelect", "_select", water);

        string staging = target + "." + Guid.NewGuid().ToString("N") + ".custom.tmp";
        try { doc.Save(staging); Restore(staging, target, backups); }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    private static void SetOrAddNodeAttr(XmlDocument doc, string parentXPath, string elemTag, string nodeName, string attrName, string val) {
        var node = doc.SelectSingleNode("//*[@Name='" + nodeName + "']") as XmlElement;
        if (node != null) {
            node.SetAttribute(attrName, val);
            return;
        }
        var parent = doc.SelectSingleNode(parentXPath) as XmlElement;
        if (parent != null) {
            var created = doc.CreateElement(elemTag);
            created.SetAttribute("Name", nodeName);
            created.SetAttribute(attrName, val);
            parent.AppendChild(created);
        }
    }
    public static void Restore(string source, string target, string backups) {
        Validate(source);
        Directory.CreateDirectory(backups);
        if (File.Exists(target)) File.Copy(target, Path.Combine(backups, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".xml"));
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.Copy(source, temp);
            Validate(temp);
            if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

internal static class Win32Native {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)] public struct POINT_W32 { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT_W32 ptScreenPos; }
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr lpRect);

    public static bool IsHardwareCursorVisible() {
        try {
            CURSORINFO pci = new CURSORINFO();
            pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            return GetCursorInfo(ref pci) && ((pci.flags & 1) != 0);
        } catch {
            return true;
        }
    }

    public static int GetForegroundPid() {
        try {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            return (int)pid;
        } catch {
            return 0;
        }
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] public struct UNION { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT key; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public UNION data; }

    public static void SendKey(byte vk, int ms) {
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = 1;
        inputs[0].data.key.scan = (ushort)MapVirtualKey(vk, 0);
        inputs[0].data.key.flags = 8;
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(ms);
        inputs[0].data.key.flags = 10;
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void HoldKey(byte vk, bool down) {
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = 1;
        inputs[0].data.key.scan = (ushort)MapVirtualKey(vk, 0);
        inputs[0].data.key.flags = down ? 8U : 10U;
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    public static void ReleaseAllKeys() {
        byte[] vks = new byte[] {
            0x1B, // VK_ESCAPE
            0x31, 0x32, 0x33, 0x34, // 1, 2, 3, 4
            0x09, // VK_TAB
            0x51, 0x57, 0x45, 0x52, // Q, W, E, R
            0x49, // I
            0x0D, // VK_RETURN
            0x11, 0xA2, 0xA3, // VK_CONTROL, LCONTROL, RCONTROL
            0x41, 0x53, 0x44, 0x46, // A, S, D, F
            0x10, 0xA0, 0xA1, // VK_SHIFT, LSHIFT, RSHIFT
            0x4D, // M
            0x12, 0xA4, 0xA5, // VK_MENU (Alt)
            0x20 // VK_SPACE
        };
        try {
            foreach (byte vk in vks) {
                keybd_event(vk, (byte)MapVirtualKey(vk, 0), 2, UIntPtr.Zero);
            }
            mouse_event(4, 0, 0, 0, UIntPtr.Zero);
            mouse_event(16, 0, 0, 0, UIntPtr.Zero);
        } catch { }
    }
}

/// <summary>
/// Dual-State Docked In-Game Cheat HUD & Tabbed Command Center (Best-Practice UX Design)
/// - State 1: Collapsed Dock Pill (Default Always-Visible Handle at Top-Right, ~420x32px):
///   Never disappears into thin air! Always shows a sleek, clickable "[ ◆ 치트 도크 열기 (` / INS) ]" handle + live active cheat badges.
///   Clicking the handle or pressing [` (물결키)] / [INSERT] / [F12] instantly expands the Dock Panel.
/// - State 2: Expanded Tabbed Sidebar Dock (Right-Side Docked Panel, 360x490px):
///   Categorized into 4 tabs ([★추천/프리셋], [⚔전투/성장], [♥생활/수중/제작], [▸공략/매크로]) with instant toggle switches,
///   stepped difficulty multipliers (x1.5, x2, x3, MAX), and 0-latency feedback without Alt-Tabbing!
/// </summary>
class InGameHudOverlayForm : Form {
        readonly Launcher owner;
        public bool InteractiveMenuMode = false;
        public bool StealthHideAll = false;
        public int ActiveHudTab = 0; // 0: 전투·생존, 1: 연애·조련·탈것·수중, 2: 제작·장비·각인·재화, 3: 도둑·특수
        public int WalkthroughStage = 0;
        private System.Windows.Forms.Timer dynamicSensorTimer;
        private bool isDragging = false;
        private Point dragCursorPoint;
        private Point dragFormPoint;

        public bool PinHudMode {
            get { return !StealthHideAll; }
            set { StealthHideAll = !value; UpdateFormBounds(); this.Invalidate(); }
        }

        readonly string[] walkthroughSteps = new string[] {
            "[공략 1단계] 귀환석 해금 -> [연애 호감도 x2] NPC 대화 -> [동물 조련 x2] 야생마 포획 -> [탈것 무한기력] 이동",
            "[공략 2단계] 보스 특수기 시전 시 [Ctrl] 관찰 스킬 흡수 -> [방어-50% + 공격x2.0] 손맛 전투",
            "[공략 3단계] 해안 동굴 잠수 시 [수중호흡 무한 + 수영x1.5] 유물 채집 -> 화염 동료 구출",
            "[공략 4단계] 대장간·모닥불에서 [제작재료 반값 + 대성공x2] 전설 장비 제작 -> [내구도 무한] 유지",
            "[공략 5단계] 심연의 회랑 광원 블룸을 따라 질주(W+Shift) -> 균열 제단 상호작용 돌파"
        };

        struct HudBtn {
            public Rectangle Rect;
            public Action OnClick;
            public string Category;
            public string RowName;
            public string Label;
        }
        List<HudBtn> hits = new List<HudBtn>();
        private string _lastHitmapSig = "";

        public InGameHudOverlayForm(Launcher parent) {
            this.owner = parent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(18, 22, 34);
            DoubleBuffered = true;

            try { selfPid = Process.GetCurrentProcess().Id; } catch { }
            IntPtr forceHandle = this.Handle;
            UpdateFormBounds();

            dynamicSensorTimer = new System.Windows.Forms.Timer { Interval = 60 };

            dynamicSensorTimer.Tick += delegate { PollSensors(); };
            dynamicSensorTimer.Start();
        }

        private readonly bool[] wasKeyDown = new bool[256];
        private int ipcPollTick = 0;
        private int pidRefreshTick = 0;
        private int selfPid = 0;
        private int cachedGamePid = 0;
        private DateTime lastHudToastUtc = DateTime.UtcNow;
        private bool wasActiveContext = false;

        private bool IsKeyJustPressed(int vk) {
            if (vk < 0 || vk >= 256) return false;
            bool isDown = (Win32Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            bool justPressed = isDown && !wasKeyDown[vk];
            wasKeyDown[vk] = isDown;
            return justPressed;
        }

        private void PollSensors() {
            if (this.IsDisposed) return;

            try {
                // Process external AutoPlay / IPC commands ONLY when authorized self-test is active
                if (++ipcPollTick >= 10) {
                    ipcPollTick = 0;
                    string ipcFile = Path.Combine(owner.root, @"tuning\ipc_cmd.txt");
                    if (File.Exists(ipcFile)) {
                        try {
                            string raw = File.ReadAllText(ipcFile, Encoding.UTF8).Trim();
                            File.Delete(ipcFile);
                            if (!owner.IsAuthorizedSelfTestActive()) {
                                owner.Log("⚠️ [보안 차단] '자가 테스트 실시' 승인 없는 외부 IPC 명령을 무시했습니다.");
                            } else {
                                foreach (string part in raw.Split(new char[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                                    int cmdId;
                                    if (int.TryParse(part.Trim(), out cmdId)) {
                                        if (cmdId >= 900 && cmdId <= 903) {
                                            StealthHideAll = false;
                                            ActiveHudTab = cmdId - 900;
                                            _lastHitmapSig = "";
                                            if (!InteractiveMenuMode) SetInteractiveMode(true);
                                            this.Invalidate();
                                            this.Update();
                                            if (InteractiveMenuMode && !Launcher.SelfTestMode && !Launcher.DumpUiMode) ExportHudHitmap();
                                        } else if (cmdId == 910) {
                                            StealthHideAll = false;
                                            _lastHitmapSig = "";
                                            if (!InteractiveMenuMode) SetInteractiveMode(true);
                                            this.Invalidate();
                                            this.Update();
                                            if (InteractiveMenuMode && !Launcher.SelfTestMode && !Launcher.DumpUiMode) ExportHudHitmap();
                                        } else if (cmdId == 911) {
                                            _lastHitmapSig = "";
                                            if (InteractiveMenuMode) SetInteractiveMode(false);
                                            this.Invalidate();
                                            if (!Launcher.SelfTestMode && !Launcher.DumpUiMode) ExportHudHitmap();
                                        } else {
                                            owner.TriggerHotkeyAction(cmdId);
                                        }
                                    }
                                }
                            }
                        } catch { }
                    }
                }

                // Reuse Launcher's cached GetExitCodeProcess handle + 2s snapshot throttle
                if (++pidRefreshTick >= 10) {
                    pidRefreshTick = 0;
                    cachedGamePid = (owner != null && owner.Running()) ? (int)owner.cachedGamePid : 0;
                }

                // Emergency stop check (F12): immediately stops any macro, AutoPlay, and Free-Flight lock
                if ((Win32Native.GetAsyncKeyState(0x7B) & 0x8000) != 0) {
                    if (!wasKeyDown[0x7B]) {
                        wasKeyDown[0x7B] = true;
                        if (owner != null) {
                            owner.EmergencyStopAllAndLand();
                        }
                    }
                } else {
                    wasKeyDown[0x7B] = false;
                }

                int fgPid = Win32Native.GetForegroundPid();
                bool isSelfFg = (fgPid > 0 && fgPid == selfPid);
                bool isGameFg = (cachedGamePid > 0 && fgPid == cachedGamePid);
                bool activeContext = isGameFg || isSelfFg || Launcher.SelfTestMode;

                if (activeContext && !wasActiveContext) {
                    lastHudToastUtc = DateTime.UtcNow;
                }
                wasActiveContext = activeContext;

                // Gate foreground HUD toggles strictly to CrimsonDesert.exe or Launcher HUD!
                // Never intercept Ctrl+0..9 or bare Numpad 0..9 during manual user play (eliminates accidental cheat reset/toggles).
                if (!activeContext) {
                    Array.Clear(wasKeyDown, 0, wasKeyDown.Length);
                } else {
                    bool ctrlDown = (Win32Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
                    bool altDown = (Win32Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
                    if (!ctrlDown && !altDown && IsKeyJustPressed(0x2D)) {
                        if (StealthHideAll || !this.Visible) {
                            StealthHideAll = false;
                            SetInteractiveMode(true);
                        } else {
                            SetInteractiveMode(!InteractiveMenuMode);
                        }
                        return;
                    }

                    if (InteractiveMenuMode && IsKeyJustPressed(0x1B)) {
                        SetInteractiveMode(false);
                        return;
                    }
                }

                // Dynamic DWM Independent Flip Unmap Policy:
                // Unmap the TopMost GDI HUD window whenever the user is actively playing in 3D
                // (game foreground, menu closed, no recent toast in last 1.8s)
                // or whenever switched away to another app. Re-show cleanly only on menu/toast.
                if (!Launcher.SelfTestMode && !Launcher.DumpUiMode) {
                    if (!owner.isHudVisible || StealthHideAll || !activeContext) {
                        if (this.Visible) {
                            Win32Native.ShowWindow(this.Handle, 0); // SW_HIDE
                            this.Visible = false;
                        }
                        return;
                    }
                    if (InteractiveMenuMode) {
                        if (!this.Visible) {
                            UpdateFormBounds();
                            this.Visible = true;
                            Win32Native.ShowWindow(this.Handle, 4); // SW_SHOWNOACTIVATE -> never steal focus
                        }
                        Win32Native.ClipCursor(IntPtr.Zero);
                    } else {
                        bool toastActive = (DateTime.UtcNow - lastHudToastUtc).TotalSeconds < 1.8;
                        bool wantPill = isSelfFg || toastActive;
                        if (wantPill && !this.Visible) {
                            UpdateFormBounds();
                            this.Visible = true;
                            Win32Native.ShowWindow(this.Handle, 4); // SW_SHOWNOACTIVATE
                            this.Invalidate();
                        } else if (!wantPill && this.Visible) {
                            Win32Native.ShowWindow(this.Handle, 0); // SW_HIDE -> restores DXGI Independent Flip!
                            this.Visible = false;
                            return;
                        }
                    }
                }
            } catch { }
        }

        private void UpdateFormBounds() {
            Rectangle sc = Screen.PrimaryScreen.Bounds;
            if (StealthHideAll) {
                this.Bounds = new Rectangle(-32000, -32000, 1, 1);
            } else if (InteractiveMenuMode) {
                // Spacious, Professional In-Game Cheat Dashboard (860x560) - Opaque hardware blit (zero DWM ColorKey stall)
                if (this.Width != 860 || this.Height != 560 || this.Left < -10000) {
                    this.Bounds = new Rectangle(sc.Right - 890, sc.Top + 45, 860, 560);
                }
            } else {
                // Sleek, Minimalist Top-Right Pill (480x36) - Opaque hardware blit
                this.Bounds = new Rectangle(sc.Right - 500, sc.Top + 10, 480, 36);
            }
        }

        private void RestoreGameFocusIfRunning() {
            if (Launcher.SelfTestMode || Launcher.DumpUiMode || owner == null) return;
            try {
                int pid = cachedGamePid > 0 ? cachedGamePid : (int)owner.cachedGamePid;
                if (pid <= 0) return;
                using (Process gp = Process.GetProcessById(pid)) {
                    IntPtr h = gp.MainWindowHandle;
                    if (h != IntPtr.Zero && Win32Native.GetForegroundWindow() != h) {
                        Win32Native.SetForegroundWindow(h);
                    }
                }
            } catch { }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams {
            get {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Win32Native.WS_EX_TOPMOST | Win32Native.WS_EX_TOOLWINDOW | Win32Native.WS_EX_NOACTIVATE;
                if (!InteractiveMenuMode) {
                    cp.ExStyle |= Win32Native.WS_EX_TRANSPARENT;
                }
                return cp;
            }
        }

        protected override void WndProc(ref Message m) {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE) {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            if (m.Msg == 0x0312 && owner != null) {
                owner.TriggerHotkeyAction(m.WParam.ToInt32());
                return;
            }
            base.WndProc(ref m);
        }

        public void NotifyActivity() {
            lastHudToastUtc = DateTime.UtcNow;
            if (!StealthHideAll && owner != null && owner.isHudVisible) {
                if (!this.Visible) {
                    UpdateFormBounds();
                    if (!Launcher.SelfTestMode) Win32Native.ShowWindow(this.Handle, 4);
                    this.Visible = true;
                }
                this.Invalidate();
            }
        }

        public void TogglePinMode() {
            StealthHideAll = !StealthHideAll;
            if (!StealthHideAll) {
                lastHudToastUtc = DateTime.UtcNow;
                this.Visible = true;
                if (!Launcher.SelfTestMode) Win32Native.ShowWindow(this.Handle, 4);
            }
            UpdateFormBounds();
            this.Invalidate();
        }

        public void SetInteractiveMode(bool interactive) {
            InteractiveMenuMode = interactive;
            lastHudToastUtc = DateTime.UtcNow;
            if (interactive) StealthHideAll = false;
            try {
                int ex = Win32Native.GetWindowLong(this.Handle, Win32Native.GWL_EXSTYLE);
                ex |= Win32Native.WS_EX_TOPMOST | Win32Native.WS_EX_TOOLWINDOW | Win32Native.WS_EX_NOACTIVATE;
                if (interactive) ex &= ~Win32Native.WS_EX_TRANSPARENT;
                else ex |= Win32Native.WS_EX_TRANSPARENT;
                Win32Native.SetWindowLong(this.Handle, Win32Native.GWL_EXSTYLE, ex);
                Win32Native.SetWindowPos(this.Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);
                if (interactive && !Launcher.SelfTestMode && !Launcher.DumpUiMode) {
                    Win32Native.ClipCursor(IntPtr.Zero);
                }
            } catch { }
            UpdateFormBounds();
            if (!Launcher.SelfTestMode) {
                if (!this.Visible) {
                    this.Visible = true;
                    Win32Native.ShowWindow(this.Handle, 4); // SW_SHOWNOACTIVATE
                }
                // Only return foreground to CrimsonDesert when closing interactive HUD (WS_EX_NOACTIVATE keeps focus intact when open).
                if (!interactive) {
                    RestoreGameFocusIfRunning();
                }
            }
            _lastHitmapSig = "";
            this.Invalidate();
        }

        public void CycleWalkthrough(int delta) {
            WalkthroughStage = (WalkthroughStage + delta + walkthroughSteps.Length) % walkthroughSteps.Length;
            this.Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) {
                // Check if clicked in Draggable Title Bar (y: 0~38)
                if (InteractiveMenuMode && e.Y <= 38 && e.X < this.Width - 140) {
                    isDragging = true;
                    dragCursorPoint = Cursor.Position;
                    dragFormPoint = this.Location;
                    return;
                }
                for (int i = hits.Count - 1; i >= 0; i--) {
                    var h = hits[i];
                    if (h.Rect.Contains(e.Location) && h.OnClick != null) {
                        h.OnClick();
                        this.Invalidate();
                        break;
                    }
                }
                // Do NOT call RestoreGameFocusIfRunning() on every HUD click: WS_EX_NOACTIVATE + MA_NOACTIVATE keeps focus untouched and avoids click lag/cursor relock.
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if (isDragging) {
                Point dif = Point.Subtract(Cursor.Position, new Size(dragCursorPoint));
                this.Location = Point.Add(dragFormPoint, new Size(dif));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e) {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) {
                isDragging = false;
            }
        }

        private int CountActiveCheats() {
            int c = 0;
            if (owner.damageTakenMode > 0) c++;
            if (owner.playerStaminaMode > 0) c++;
            if (owner.damageDealtMultiplier > 1.05f) c++;
            if (owner.cooldownMode > 0) c++;
            if (owner.expMultiplier > 1) c++;
            if (owner.mountStaminaMode > 0) c++;
            if (owner.speedMultiplier > 1.05f) c++;
            if (owner.jumpMultiplier > 1.05f) c++;
            if (owner.romanceMultiplier > 1) c++;
            if (owner.tamingMultiplier > 1) c++;
            if (owner.mountSpeedMultiplier > 1.05f) c++;
            if (owner.underwaterOxygenMultiplier > 1) c++;
            if (owner.underwaterSpeedMultiplier > 1.05f) c++;
            if (owner.craftCostMode > 0) c++;
            if (owner.craftSuccessMultiplier > 1) c++;
            if (owner.durabilityMode > 0) c++;
            if (owner.gearHiderMode > 0) c++;
            if (owner.weaponImbueMode > 0) c++;
            if (owner.thiefDetectionMode > 0) c++;
            if (owner.pickpocketSuccessMode > 0) c++;
            if (owner.bountyImmunityMode > 0) c++;
            if (owner.thiefGloveFenceMode > 0) c++;
            if (owner.bagExpandSlots > 0) c++;
            if (owner.specialConvenienceMode > 0) c++;
            return c;
        }

        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            hits.Clear();
            if (StealthHideAll) return;

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int actCount = CountActiveCheats();

            // 1. COLLAPSED MODE: Sleek 1-Line Pill at Top-Right
            if (!InteractiveMenuMode) {
                Rectangle pill = new Rectangle(1, 1, this.Width - 3, this.Height - 3);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 18, 22, 34)))
                using (Pen border = new Pen(actCount > 0 ? Color.FromArgb(46, 204, 113) : Color.FromArgb(241, 196, 15), 1.5f)) {
                    g.FillRectangle(bg, pill);
                    g.DrawRectangle(border, pill);
                }
                using (Font f = new Font("Malgun Gothic", 9.0f, FontStyle.Bold))
                using (SolidBrush tb = new SolidBrush(Color.White)) {
                    long curSil = LiveMemoryCheatEngine.GetLiveSilverAmount();
                    string silTxt = curSil >= 100000000L ? string.Format("은화 {0:N0}만G", (curSil / 100L) / 10000L) : (curSil >= 0 ? string.Format("은화 {0:N2}G", curSil / 100.0) : "은화 대기");
                    string txt = string.Format("◆ 치트 메뉴 [INS]  |  활성 {0}종  |  {1}", actCount, silTxt);
                    StringFormat sf = new StringFormat {
                        Alignment = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    };
                    g.DrawString(txt, f, tb, new Rectangle(14, 0, this.Width - 55, this.Height), sf);
                }
                Rectangle closeR = new Rectangle(this.Width - 30, (this.Height - 20) / 2, 22, 20);
                using (SolidBrush cb = new SolidBrush(Color.FromArgb(55, 65, 85)))
                    g.FillEllipse(cb, closeR);
                using (Font cf = new Font("Malgun Gothic", 8.0f, FontStyle.Bold))
                using (SolidBrush ctb = new SolidBrush(Color.FromArgb(200, 210, 230))) {
                    StringFormat csf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("✕", cf, ctb, closeR, csf);
                }
                hits.Add(new HudBtn { Rect = closeR, OnClick = delegate { TogglePinMode(); } });
                hits.Add(new HudBtn { Rect = new Rectangle(1, 1, this.Width - 36, this.Height - 2), OnClick = delegate { SetInteractiveMode(true); } });
                return;
            }

            // 2. EXPANDED MODE: Spacious High-Detail Control Center (860x560)
            Rectangle box = new Rectangle(2, 2, this.Width - 5, this.Height - 5);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(246, 18, 22, 32)))
            using (Pen border = new Pen(Color.FromArgb(241, 196, 15), 2f)) {
                g.FillRectangle(bg, box);
                g.DrawRectangle(border, box);
            }

            using (Font fHead = new Font("Malgun Gothic", 10.0f, FontStyle.Bold))
            using (Font fTab = new Font("Malgun Gothic", 9.2f, FontStyle.Bold))
            using (Font fRow = new Font("Malgun Gothic", 8.8f, FontStyle.Bold))
            using (Font fBtn = new Font("Malgun Gothic", 8.2f, FontStyle.Bold))
            using (Font fTip = new Font("Malgun Gothic", 8.2f, FontStyle.Regular)) {
                // Header (Draggable Zone)
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(28, 35, 52)))
                    g.FillRectangle(hb, 4, 4, this.Width - 9, 36);
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(241, 196, 15))) {
                    long curSil = LiveMemoryCheatEngine.GetLiveSilverAmount();
                    int wHour, wMin;
                    bool hasTime = LiveMemoryCheatEngine.GetLiveWorldTime(out wHour, out wMin);
                    string timeStr = hasTime ? string.Format(" | 월드 시각 {0:D2}:{1:D2}", wHour, wMin) : "";
                    string silTxt = curSil >= 0 ? string.Format("보유 은화: {0:N2}G ({1:N0})", curSil / 100.0, curSil) : "은화 감지 대기(인벤토리 열기)";
                    g.DrawString(string.Format("◆ CRIMSON DESERT 올인원 실시간 에디터  |  {0}{1}", silTxt, timeStr), fHead, tb, 14, 11);
                }

                DrawMenuBtn(g, this.Width - 128, 7, 118, 28, "✕ 닫기 [INS / ESC]", false, Color.FromArgb(230, 57, 70), fBtn, delegate { SetInteractiveMode(false); }, "close", "close");

                // Top Quick Master Action Strip (Presets + Quick Toggles — Zero Overlap)
                int y = 46;
                DrawMenuBtn(g, 12, y, 128, 30, "★ 1. 추천 세트", false, Color.FromArgb(39, 174, 96), fBtn, delegate { owner.ActivateBalancedPreset(); }, "top_bar", "preset_balanced");
                DrawMenuBtn(g, 144, y, 128, 30, "◆ 2. 초월 풀셋", false, Color.FromArgb(230, 57, 70), fBtn, delegate { owner.ActivateSuperPreset(); }, "top_bar", "preset_super");
                DrawMenuBtn(g, 276, y, 106, 30, "↺ 3. 초기화", false, Color.FromArgb(90, 100, 125), fBtn, delegate { owner.ResetAllToVanilla(); }, "top_bar", "reset_vanilla");

                bool hOn = owner.gearHiderMode > 0;
                DrawMenuBtn(g, 386, y, 108, 30, hOn ? "★ 헬멧:숨김ON" : "★ 헬멧:표시", hOn, Color.FromArgb(241, 196, 15), fBtn, delegate { owner.ToggleHelmetVisibility(); }, "top_bar", "helmet_toggle");

                bool tOn = owner.thiefStealthMode > 0;
                DrawMenuBtn(g, 498, y, 108, 30, tOn ? "◈ 도둑:무죄ON" : "◈ 도둑:정상", tOn, Color.FromArgb(46, 204, 113), fBtn, delegate { owner.SetThiefStealthMode(tOn ? 0 : 1); }, "top_bar", "thief_toggle");

                DrawMenuBtn(g, 610, y, 106, 30, owner.isNativeAaClarity ? "▣ 화질:AA선명" : ("▣ 화질:FSR" + owner.activeFsrScaleLabel), owner.isNativeAaClarity, Color.FromArgb(52, 152, 219), fBtn, delegate { owner.ToggleResolutionClarity(); }, "top_bar", "clarity_toggle");

                DrawMenuBtn(g, 720, y, 128, 30, "⚡ FLiNG 트레이너", false, Color.FromArgb(41, 128, 185), fBtn, delegate { owner.LaunchExternalFlingTrainer(); }, "top_bar", "external_fling");

                // 4 Detailed Category Tabs Matching User Master Hub Format
                y += 38;
                int tw = (this.Width - 36) / 4;
                DrawMenuBtn(g, 12, y, tw, 32, "⚔ 탭1: 전투 · 생존 (7종)", ActiveHudTab == 0, Color.FromArgb(52, 152, 219), fTab, delegate { ActiveHudTab = 0; }, "tab_header", "tab_0");
                DrawMenuBtn(g, 16 + tw, y, tw, 32, "♥ 탭2: 연애 · 조련 (6종)", ActiveHudTab == 1, Color.FromArgb(52, 152, 219), fTab, delegate { ActiveHudTab = 1; }, "tab_header", "tab_1");
                DrawMenuBtn(g, 20 + tw * 2, y, tw, 32, "▲ 탭3: 제작 · 장비 (6종)", ActiveHudTab == 2, Color.FromArgb(52, 152, 219), fTab, delegate { ActiveHudTab = 2; }, "tab_header", "tab_2");
                DrawMenuBtn(g, 24 + tw * 3, y, tw, 32, "◈ 탭4: 도둑 · 특수 (6종)", ActiveHudTab == 3, Color.FromArgb(52, 152, 219), fTab, delegate { ActiveHudTab = 3; }, "tab_header", "tab_3");
                y += 40;

                // Tab Content Rendering with Granular Stepped Buttons
                if (ActiveHudTab == 0) {
                    DrawDetailRow(g, ref y, "방어·무적", GetCurrentModeDesc("dmg_taken", owner.damageTakenMode),
                        new[] { "OFF(정상)", "-30%", "-50%추천", "-75%", "완전무적" },
                        new object[] { 0, 1, 2, 3, 4 }, owner.damageTakenMode, v => owner.SetDamageTakenMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "공격력 배율", string.Format("x{0:0.#}배", owner.damageDealtMultiplier),
                        new[] { "OFF(x1)", "x1.2", "x1.5손맛", "x2.0추천", "x2.5", "x3.0", "x5.0", "일격필살" },
                        new object[] { 1.0f, 1.2f, 1.5f, 2.0f, 2.5f, 3.0f, 5.0f, 999.0f }, owner.damageDealtMultiplier, v => owner.SetDamageDealtMultiplier((float)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "기력(스태미나)", GetCurrentModeDesc("stamina", owner.playerStaminaMode),
                        new[] { "OFF(정상)", "소모-30%", "소모-50%", "회복2배추천", "무한기력" },
                        new object[] { 0, 1, 2, 3, 4 }, owner.playerStaminaMode, v => owner.SetPlayerStaminaMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "스킬 쿨타임", GetCurrentModeDesc("cooldown", owner.cooldownMode),
                        new[] { "OFF(정상)", "-30%", "-50%추천", "-75%", "0초(노쿨)" },
                        new object[] { 0, 1, 2, 3, 4 }, owner.cooldownMode, v => owner.SetCooldownMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "경험치(EXP)", "x" + owner.expMultiplier + "배",
                        new[] { "OFF(x1)", "x2배추천", "x3배", "x4배", "x5배", "x7배", "x10배" },
                        new object[] { 1, 2, 3, 4, 5, 7, 10 }, owner.expMultiplier, v => owner.SetExpMultiplier((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "이동/전투속도", string.Format("x{0:0.#}배", owner.speedMultiplier),
                        new[] { "OFF(x1.0)", "x1.15", "x1.3", "x1.5추천", "x2.0", "x3.0" },
                        new object[] { 1.0f, 1.15f, 1.3f, 1.5f, 2.0f, 3.0f }, owner.speedMultiplier, v => owner.SetSpeedMultiplier((float)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "점프 도약력", string.Format("x{0:0.#}배", owner.jumpMultiplier),
                        new[] { "OFF(x1)", "x1.5배", "x2배추천", "x3배", "x4배", "x5배" },
                        new object[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f, 5.0f }, owner.jumpMultiplier, v => owner.SetJumpMultiplier(Convert.ToSingle(v)), fRow, fBtn);
                } else if (ActiveHudTab == 1) {
                    DrawDetailRow(g, ref y, "연애 호감도", owner.romanceMultiplier >= 999 ? "즉시MAX" : "x" + owner.romanceMultiplier + "배",
                        new[] { "OFF(x1)", "x2배추천", "x3배", "x5배", "x10배", "즉시MAX" },
                        new object[] { 1, 2, 3, 5, 10, 999 }, owner.romanceMultiplier, v => owner.SetRomanceMultiplier((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "동물 조련", GetCurrentModeDesc("taming", owner.tamingMultiplier),
                        new[] { "OFF(x1)", "저항-50%추천", "숙련x3", "달인x5", "즉시성공" },
                        new object[] { 1, 2, 3, 5, 999 }, owner.tamingMultiplier, v => owner.SetTamingEase((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "탈것 질주속도", string.Format("x{0:0.#}배", owner.mountSpeedMultiplier),
                        new[] { "OFF(x1.0)", "x1.2", "x1.5추천", "x2.0질주", "x3.0초고속" },
                        new object[] { 1.0f, 1.2f, 1.5f, 2.0f, 3.0f }, owner.mountSpeedMultiplier, v => owner.SetMountSpeed((float)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "탈것 기력소모", GetCurrentModeDesc("mount_sta", owner.mountStaminaMode),
                        new[] { "OFF(정상)", "소모-30%", "소모-50%", "소모-75%", "무한기력" },
                        new object[] { 0, 1, 2, 3, 4 }, owner.mountStaminaMode, v => owner.SetMountStaminaMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "수중 잠수호흡", owner.underwaterOxygenMultiplier >= 999 ? "무한잠수" : "x" + owner.underwaterOxygenMultiplier + "배",
                        new[] { "OFF(x1)", "호흡x2", "호흡x3추천", "호흡x5", "무한잠수" },
                        new object[] { 1, 2, 3, 5, 999 }, owner.underwaterOxygenMultiplier, v => owner.SetUnderwaterOxygen((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "수중 수영속도", string.Format("x{0:0.#}배", owner.underwaterSpeedMultiplier),
                        new[] { "OFF(x1.0)", "x1.2", "x1.5추천", "x2.0빠름", "x3.0초고속" },
                        new object[] { 1.0f, 1.2f, 1.5f, 2.0f, 3.0f }, owner.underwaterSpeedMultiplier, v => owner.SetUnderwaterSpeed((float)v), fRow, fBtn);
                } else if (ActiveHudTab == 2) {
                    DrawDetailRow(g, ref y, "제작 재료할인", GetCurrentModeDesc("craft_cost", owner.craftCostMode),
                        new[] { "OFF(정상)", "-25%할인", "-50%반값추천", "-75%할인", "무재료(0개)" },
                        new object[] { 0, 1, 2, 3, 4 }, owner.craftCostMode, v => owner.SetCraftCost((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "장비 대성공", owner.craftSuccessMultiplier >= 999 ? "전설100%" : "x" + owner.craftSuccessMultiplier + "배",
                        new[] { "OFF(1배)", "성공2배추천", "성공3배", "성공5배", "전설100%" },
                        new object[] { 1, 2, 3, 5, 999 }, owner.craftSuccessMultiplier, v => owner.SetCraftSuccess((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "장비 내구도", GetCurrentModeDesc("durability", owner.durabilityMode),
                        new[] { "OFF(정상)", "소모-30%", "소모-50%", "소모-75%", "무한내구도" },
                        new object[] { 0, 1, 2, 3, 4 }, owner.durabilityMode, v => owner.SetDurabilityMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "외형 숨김(HAWT)", GetCurrentModeDesc("gear_hider", owner.gearHiderMode),
                        new[] { "전체표시(OFF)", "헬멧숨김(투명ON)", "헬멧+망토숨김", "납도무기까지숨김" },
                        new object[] { 0, 1, 2, 3 }, owner.gearHiderMode, v => owner.SetGearHiderMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "무기 속성각인", owner.GetWeaponImbueBadge(),
                        new[] { "각인해제", "화염 폭발", "빙결 제압", "뇌전 연쇄", "흡혈+치명", "전속성 마스터" },
                        new object[] { 0, 1, 2, 3, 4, 5 }, owner.weaponImbueMode, v => owner.SetWeaponImbueMode((int)v), fRow, fBtn);

                    string goldBadge = owner.lastInjectedGoldAmount > 0
                        ? string.Format("+{0:N0}G 주입완료", owner.lastInjectedGoldAmount / 100L)
                        : "수량 직접 선택";
                    DrawDetailRow(g, ref y, "은화(골드) 주입", goldBadge,
                        new[] { "+1,000 G", "+5,000 G", "+1만 G", "+5만 G", "+10만 G", "+50만 G" },
                        new object[] { 100000L, 500000L, 1000000L, 5000000L, 10000000L, 50000000L }, owner.lastInjectedGoldAmount, v => owner.AddGold((long)v), fRow, fBtn);
                } else if (ActiveHudTab == 3) {
                    DrawDetailRow(g, ref y, "도둑질 발각확률", GetCurrentModeDesc("thief_detect", owner.thiefDetectionMode),
                        new[] { "OFF(정상)", "발각-50%", "발각-80%", "발각0%(완전은신추천)" },
                        new object[] { 0, 1, 2, 3 }, owner.thiefDetectionMode, v => owner.SetThiefDetectionMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "소매치기 성공률", GetCurrentModeDesc("pickpocket", owner.pickpocketSuccessMode),
                        new[] { "OFF(정상)", "성공률 x2", "성공률 x3", "100% 무조건성공(추천)" },
                        new object[] { 0, 1, 2, 3 }, owner.pickpocketSuccessMode, v => owner.SetPickpocketSuccessMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "범죄 현상금/수배", GetCurrentModeDesc("bounty", owner.bountyImmunityMode),
                        new[] { "OFF(정상)", "현상금 -50%", "현상금 0G", "영구무죄(악명0추천)" },
                        new object[] { 0, 1, 2, 3 }, owner.bountyImmunityMode, v => owner.SetBountyImmunityMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "대도의 장갑/장물", GetCurrentModeDesc("thief_glove", owner.thiefGloveFenceMode),
                        new[] { "미착용", "기본착용ON", "모든세이브소유", "장물 100% 정상매입" },
                        new object[] { 0, 1, 2, 3 }, owner.thiefGloveFenceMode, v => owner.SetThiefGloveFenceMode((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "인벤토리 가방확장", GetCurrentModeDesc("bag_expand", owner.bagExpandSlots),
                        new[] { "기본(정상)", "가방 +20칸", "가방 +40칸", "가방 +60칸 최대", "창고 900칸" },
                        new object[] { 0, 20, 40, 60, 900 }, owner.bagExpandSlots, v => owner.SetBagExpandSlots((int)v), fRow, fBtn);

                    DrawDetailRow(g, ref y, "수면/낙하/자유비행", GetCurrentModeDesc("convenience", owner.specialConvenienceMode),
                        new[] { "OFF(정상)", "즉시수면(쿨0초)", "낙하피해 0 면제", "체공/자유비행ON" },
                        new object[] { 0, 1, 2, 3 }, owner.specialConvenienceMode, v => owner.SetSpecialConvenienceMode((int)v), fRow, fBtn);
                }

                // Footer Tip
                y = this.Height - 34;
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(160, 215, 255))) {
                    g.DrawString("※ [실시간 직결] 전 항목 FLiNG + atult 역설계 실시간 메모리 훅으로 클릭 즉시 게임에 반영됩니다 | 상단 바 드래그 이동 | 닫기: ESC / INS", fTip, fb, 14, y + 6);
                }
            }
            if (InteractiveMenuMode && !Launcher.SelfTestMode && !Launcher.DumpUiMode) {
                ExportHudHitmap();
            }
        }

        static string JsonEsc(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }

        internal void ExportHudHitmap(string customPath = null) {
            try {
                var sb = new StringBuilder();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                sb.Append("{");
                sb.AppendFormat(inv, "\"active_tab\":{0},\"interactive\":{1},", ActiveHudTab, InteractiveMenuMode ? "true" : "false");
                sb.AppendFormat(inv, "\"bounds\":{{\"x\":{0},\"y\":{1},\"w\":{2},\"h\":{3}}},", this.Left, this.Top, this.Width, this.Height);
                sb.Append("\"buttons\":[");
                bool first = true;
                for (int i = 0; i < hits.Count; i++) {
                    var h = hits[i];
                    if (string.IsNullOrEmpty(h.Label)) continue;
                    int sx = this.Left + h.Rect.X + (h.Rect.Width / 2);
                    int sy = this.Top + h.Rect.Y + (h.Rect.Height / 2);
                    if (!first) sb.Append(",");
                    first = false;
                    sb.AppendFormat(
                        inv,
                        "{{\"cat\":\"{0}\",\"row\":\"{1}\",\"label\":\"{2}\",\"cx\":{3},\"cy\":{4},\"sx\":{5},\"sy\":{6},\"w\":{7},\"h\":{8}}}",
                        JsonEsc(h.Category), JsonEsc(h.RowName), JsonEsc(h.Label),
                        h.Rect.X + (h.Rect.Width / 2), h.Rect.Y + (h.Rect.Height / 2),
                        sx, sy, h.Rect.Width, h.Rect.Height
                    );
                }
                sb.Append("]}");
                string payload = sb.ToString();
                if (string.IsNullOrEmpty(customPath)) {
                    if (payload == _lastHitmapSig) return;
                    _lastHitmapSig = payload;
                }
                string dst = string.IsNullOrEmpty(customPath)
                    ? Path.Combine(owner.root, @"tuning\research\rsi_telemetry\hud_hitmap.json")
                    : customPath;
                string dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                string tmp = dst + ".tmp";
                File.WriteAllText(tmp, payload, new UTF8Encoding(false));
                File.Copy(tmp, dst, true);
                File.Delete(tmp);
            } catch { }
        }

        string GetCurrentModeDesc(string groupKey, object val) {
            int iv = (val is int) ? (int)val : 0;
            switch (groupKey) {
                case "dmg_taken": return iv == 4 ? "완전무적" : (iv > 0 ? "-" + (iv == 1 ? "30%" : iv == 2 ? "50%" : "75%") : "정상");
                case "stamina": return iv == 4 ? "무한기력" : (iv == 3 ? "회복2배" : (iv == 2 ? "소모-50%" : (iv == 1 ? "소모-30%" : "정상")));
                case "cooldown": return iv == 4 ? "0초노쿨" : (iv > 0 ? "쿨감ON" : "정상");
                case "taming": return iv >= 999 ? "즉시성공" : (iv > 1 ? "x" + iv : "기본");
                case "mount_sta": return iv == 4 ? "무한기력" : (iv == 3 ? "소모-75%" : (iv == 2 ? "소모-50%" : (iv == 1 ? "소모-30%" : "정상")));
                case "craft_cost": return iv == 4 ? "무재료" : (iv > 0 ? "할인ON" : "정상");
                case "durability": return iv == 4 ? "무한내구도" : (iv == 3 ? "소모-75%" : (iv == 2 ? "소모-50%" : (iv == 1 ? "소모-30%" : "정상")));
                case "gear_hider": return iv == 1 ? "헬멧숨김" : (iv == 2 ? "헬멧+망토" : (iv == 3 ? "무기까지" : "전체표시"));
                case "thief_detect": return iv == 3 ? "완전은신" : (iv == 2 ? "-80%" : (iv == 1 ? "-50%" : "정상"));
                case "pickpocket": return iv == 3 ? "100%성공" : (iv == 2 ? "성공x3" : (iv == 1 ? "성공x2" : "정상"));
                case "bounty": return iv == 3 ? "영구무죄" : (iv == 2 ? "현상금0G" : (iv == 1 ? "-50%" : "정상"));
                case "thief_glove": return iv == 3 ? "장물100%" : (iv == 2 ? "모든세이브" : (iv == 1 ? "장갑착용" : "미착용"));
                case "bag_expand": return iv == 900 ? "창고900칸" : (iv > 0 ? "+" + iv + "칸" : "기본");
                case "convenience": return iv == 3 ? "비행ON" : (iv == 2 ? "낙하0" : (iv == 1 ? "즉시수면" : "OFF"));
                default: return "적용 중";
            }
        }

        void DrawDetailRow(Graphics g, ref int y, string label, string curStatus, string[] btnLabels, object[] vals, object curVal, Action<object> onSelect, Font fRow, Font fBtn) {
            Rectangle rowBg = new Rectangle(12, y, this.Width - 24, 38);
            using (SolidBrush rb = new SolidBrush(Color.FromArgb(28, 34, 48)))
                g.FillRectangle(rb, rowBg);

            // Left Title + Status Badge (Calculated so never overlaps!)
            using (SolidBrush lb = new SolidBrush(Color.FromArgb(240, 244, 252))) {
                g.DrawString("• " + label, fRow, lb, 18, y + 10);
            }
            if (label.Contains("은화")) {
                hits.Add(new HudBtn { Rect = new Rectangle(12, y, 210, 38), OnClick = delegate { onSelect(1000000L); } });
            }

            SizeF szLbl = g.MeasureString("• " + label, fRow);
            int statX = Math.Max(126, (int)szLbl.Width + 24);

            if (!string.IsNullOrEmpty(curStatus)) {
                using (Font fStat = new Font("Malgun Gothic", 8.0f, FontStyle.Bold))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(241, 196, 15))) {
                    g.DrawString("[" + curStatus + "]", fStat, sb, statX, y + 11);
                }
            }

            int cnt = btnLabels.Length;
            int startX = 226;
            int availW = this.Width - startX - 20;
            int bw = (availW - (cnt - 1) * 4) / cnt;

            for (int i = 0; i < cnt; i++) {
                object v = vals[i];
                bool isSel = Launcher.ValuesEqual(v, curVal);

                Color accent = (i == 0) ? Color.FromArgb(85, 95, 120) : Color.FromArgb(39, 174, 96);
                DrawMenuBtn(g, startX + i * (bw + 4), y + 5, bw, 28, btnLabels[i], isSel, accent, fBtn, delegate { onSelect(v); }, "tab_" + ActiveHudTab, label);
            }
            y += 44;
        }

        void DrawMenuBtn(Graphics g, int x, int y, int w, int h, string text, bool highlight, Color accent, Font font, Action act, string cat = "", string rowName = "") {
            Rectangle r = new Rectangle(x, y, w, h);
            Color bg = highlight ? accent : Color.FromArgb(44, 52, 70);
            Color fg = Color.White;
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, r);
            using (Pen p = new Pen(highlight ? Color.FromArgb(241, 196, 15) : Color.FromArgb(75, 85, 110), highlight ? 1.5f : 1f)) g.DrawRectangle(p, r);
            using (SolidBrush tb = new SolidBrush(fg)) {
                StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, tb, r, sf);
            }
            hits.Add(new HudBtn { Rect = r, OnClick = act, Category = cat ?? "", RowName = rowName ?? "", Label = text ?? "" });
        }
    }

class Launcher : Form {
    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            // // cp.ExStyle |= 0x02000000; (offscreen buffer for all child controls)
            return cp;
        }
    }

    public const string REQUIRED_SELF_TEST_TRIGGER = "자가 테스트 실시";

    internal static bool ValidateTriggerPhrase(string phrase) {
        return phrase != null && string.Equals(phrase.Trim(), REQUIRED_SELF_TEST_TRIGGER, StringComparison.Ordinal);
    }

    static bool JsonHasBoolFlag(string json, string key, bool expectedVal) {
        if (string.IsNullOrEmpty(json)) return false;
        string needle = "\"" + key + "\":";
        int idx = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        int valStart = idx + needle.Length;
        while (valStart < json.Length && char.IsWhiteSpace(json[valStart])) valStart++;
        string want = expectedVal ? "true" : "false";
        return valStart + want.Length <= json.Length &&
               string.Compare(json, valStart, want, 0, want.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    internal bool IsAuthorizedSelfTestActive() {
        try {
            string p = Path.Combine(root, @"tuning\research\rsi_telemetry\auto_play_status.json");
            if (!File.Exists(p)) return false;
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalSeconds > 90.0) return false;
            string txt = File.ReadAllText(p, Encoding.UTF8);
            if (!JsonHasBoolFlag(txt, "trigger_verified", true)) return false;
            if (!JsonHasBoolFlag(txt, "running", true)) return false;
            if (JsonHasBoolFlag(txt, "aborted_by_human_input", true)) return false;
            return true;
        } catch {
            return false;
        }
    }

    internal bool ShouldPreserveXmlForRecentSelfTest() {
        if (IsAuthorizedSelfTestActive()) return true;
        try {
            string p = Path.Combine(root, @"tuning\research\rsi_telemetry\auto_play_status.json");
            if (!File.Exists(p)) return false;
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalSeconds > 90.0) return false;
            string txt = File.ReadAllText(p, Encoding.UTF8);
            if (!JsonHasBoolFlag(txt, "trigger_verified", true)) return false;
            return JsonHasBoolFlag(txt, "preserve_live_xml", true)
                || txt.IndexOf("HUD_EXP", StringComparison.OrdinalIgnoreCase) >= 0
                || txt.IndexOf("HUD_ALL_TABS", StringComparison.OrdinalIgnoreCase) >= 0;
        } catch {
            return false;
        }
    }

    internal void EmergencyStopAllAndLand() {
        try {
            StopAiAutoPlay();
            specialConvenienceMode = 0;
            if (!SelfTestMode) {
                LiveMemoryCheatEngine.GlideNoFallEnabled = false;
                LiveMemoryCheatEngine.DisableFreeFlightImmediate();
            }
            UpdateGroupSelection("convenience", 0);
            Win32Native.ReleaseAllKeys();
            PushCheatStateNow();
            SaveCheatConfigImmediate();
            RefreshHud();
            Log("■ [긴급 착지 & 전체 중단] 자유 비행 해제 + 매크로/자동화 중단 + 키 입력 초기화를 완료했습니다.");
        } catch (Exception ex) {
            Log("긴급 중단/착지 오류: " + ex.Message);
        }
    }

    internal bool PromptSelfTestTriggerDialog() {
        using (Form dlg = new Form()) {
            dlg.Text = "자가 테스트 승인 확인";
            dlg.ClientSize = new Size(440, 165);
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.MaximizeBox = false;
            dlg.MinimizeBox = false;
            dlg.BackColor = Color.FromArgb(20, 25, 38);
            dlg.ForeColor = Color.White;
            Label lbl = new Label {
                Text = "AI 자가 테스트는 사용자 조작을 대신 제어합니다.\r\n진행하려면 아래 입력창에 정확히 \"자가 테스트 실시\" 를 입력하세요:",
                Location = new Point(16, 16),
                Size = new Size(408, 42),
                Font = new Font("Malgun Gothic", 9.0f, FontStyle.Bold)
            };
            TextBox txt = new TextBox {
                Location = new Point(16, 66),
                Size = new Size(408, 28),
                Font = new Font("Malgun Gothic", 10.0f)
            };
            Button ok = new Button {
                Text = "실시 승인",
                DialogResult = DialogResult.OK,
                Location = new Point(236, 110),
                Size = new Size(90, 32),
                BackColor = Color.FromArgb(39, 174, 96),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            Button cancel = new Button {
                Text = "취소",
                DialogResult = DialogResult.Cancel,
                Location = new Point(334, 110),
                Size = new Size(90, 32),
                BackColor = Color.FromArgb(52, 62, 82),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            dlg.Controls.Add(lbl);
            dlg.Controls.Add(txt);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            if (!ValidateTriggerPhrase(txt.Text)) {
                MessageBox.Show(this, "트리거 문구가 일치하지 않아 실행이 차단되었습니다.\n(필수 입력: 자가 테스트 실시)", "자가 테스트 차단", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }
    }

    internal bool ConfirmAutoMacroEnable(string macroName) {
        if (SelfTestMode || IsAuthorizedSelfTestActive()) return true;
        return MessageBox.Show(
            this,
            "[" + macroName + "] 입력 자동화 매크로를 켜시겠습니까?\n(게임 중 키 입력이 자동 전송되며 ESC 또는 F12 키로 즉시 해제할 수 있습니다.)",
            "입력 매크로 실행 확인",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        ) == DialogResult.Yes;
    }

    internal void StopAiAutoPlay() {
        try {
            bool wasMacroActive = isAutoTalk || isAutoSprint || isAutoObserve;
            isAutoTalk = false;
            isAutoSprint = false;
            isAutoObserve = false;
            string flag = Path.Combine(root, @"tuning\stop_autoplay.flag");
            File.WriteAllText(flag, "STOP");
            if (wasMacroActive) {
                Win32Native.ReleaseAllKeys();
                try { System.Media.SystemSounds.Hand.Play(); } catch { }
                Log("■ [매크로/자동화 중단] 활성 입력 매크로 및 키 상태를 즉시 해제했습니다 (RSI 감시 프로세스 유지).");
            }
        } catch (Exception ex) {
            Log("긴급 중단 오류: " + ex.Message);
        }
    }

    readonly Color BG_DARK = Color.FromArgb(18, 20, 28);
    readonly Color CARD_BG = Color.FromArgb(28, 32, 44);
    readonly Color CARD_ALT = Color.FromArgb(36, 41, 56);
    readonly Color ACCENT_RED = Color.FromArgb(230, 57, 70);
    readonly Color ACCENT_GREEN = Color.FromArgb(46, 204, 113);
    readonly Color ACCENT_GOLD = Color.FromArgb(241, 196, 15);
    readonly Color ACCENT_BLUE = Color.FromArgb(52, 152, 219);
    readonly Color ACCENT_PURPLE = Color.FromArgb(155, 89, 182);
    readonly Color TEXT_MAIN = Color.FromArgb(242, 244, 248);
    readonly Color TEXT_MUTED = Color.FromArgb(165, 174, 192);

    internal static string ResolveRootDir() {
        string b = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        while (!string.IsNullOrEmpty(b)) {
            if (Directory.Exists(Path.Combine(b, "tuning")) && Directory.Exists(Path.Combine(b, "bin64")))
                return b;
            var parent = Directory.GetParent(b);
            if (parent == null) break;
            b = parent.FullName;
        }
        return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }
    internal readonly string root = ResolveRootDir();
    readonly string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Pearl Abyss\CD\save\user_engine_option_save.xml");
    readonly SessionBoost boost = new SessionBoost();
    readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 2500 };

    ComboBox profiles;
    Label profileInfo;
    Label lblRuntimeBadge;
    Label lblLiveCheatsStatus;
    Label lblBoostStatus;
    Label lblOverlayStatus;
    Label lblRsiStatus;
    CheckBox boostChoice;
    Label txtLog;
    Label txtModConsole;
    List<string> recentLogLines = new List<string>();
    TabControl mainTabs;
    Panel miniOverlayPanel;
    Panel mainContentPanel;
    Button btnOverlayToggle;
    Button btnInGameHudToggle;

    InGameHudOverlayForm inGameHud;
    internal bool isHudVisible = true;
    internal static bool DumpUiMode = false;
    private DateTime _lastRsiStateMtime = DateTime.MinValue;
    private string _cachedRsiLabel = null;
    private Color _cachedRsiColor = Color.FromArgb(241, 196, 15);
    private readonly Dictionary<int, DateTime> _lastHotkeyUtc = new Dictionary<int, DateTime>();

    bool lastRunState = false;
    string RsiScript { get { return Path.Combine(root, "tuning", "RsiEngine.py"); } }

    // Custom Graphics Controls
    ComboBox cbFov, cbZoom, cbBlur, cbUpscale, cbTex, cbModel, cbWater;

    // Cheat States & Multipliers (Internal so InGameHudOverlayForm can read)
    internal bool isMiniMode = false;

    // 전투 세분화 배율 및 난이도
    internal int maxHealthVal = 0;
    internal int maxStaminaVal = 0;
    internal int maxSpiritVal = 0;
    internal int damageTakenMode = 0; // 0: 100% 정상, 1: -30%, 2: -50% 반감, 3: -75%, 4: 완전 무적
    internal float damageDealtMultiplier = 1.0f; // 1.0, 1.5, 2.0, 3.0, 5.0, 999: 일격
    internal int playerStaminaMode = 0; // 0: 정상, 1: 소모-30%, 2: 소모-50%, 3: 회복2배, 4: 무한
    internal int cooldownMode = 0; // 0: 정상, 1: -30%, 2: -50%, 3: -75%, 4: 0초 노쿨
    internal int expMultiplier = 1;
    internal float speedMultiplier = 1.0f;
    internal float jumpMultiplier = 1.0f;
    internal long currentGoldBonus = 9999999;
    internal long lastInjectedGoldAmount = 0;
    Label lblGoldSaveStatus;

    // 생활·연애·조련·탈것·수중·제작 세분화 배율
    internal int romanceMultiplier = 1; // 1: 기본, 2: 2배, 3: 3배, 5: 5배, 999: 즉시MAX
    internal int tamingMultiplier = 1; // 1: 기본, 2: 저항반감, 3: 숙련3배, 5: 달인5배, 999: 즉시성공
    internal float mountSpeedMultiplier = 1.0f; // 1.0, 1.2, 1.5, 2.0, 3.0
    internal int mountStaminaMode = 0; // 0: 정상, 1: -30%, 2: -50%, 3: -75%, 4: 무한
    internal int underwaterOxygenMultiplier = 1; // 1: 정상, 2: 2배, 3: 3배, 5: 5배, 999: 무한잠수
    internal float underwaterSpeedMultiplier = 1.0f; // 1.0, 1.2, 1.5, 2.0, 3.0
    internal int underwaterGatheringMultiplier = 1; // 1: 정상, 2: 2배, 3: 3배, 5: 초고속
    internal int craftCostMode = 0; // 0: 정상소모, 1: 25%할인, 2: 50%반값, 3: 75%할인, 4: 무재료
    internal int craftSuccessMultiplier = 1; // 1: 정상, 2: 2배, 3: 3배, 5: 5배, 999: 전설 100%
    internal int durabilityMode = 0; // 0: 정상소모, 1: -30%, 2: -50%, 3: -75%, 4: 무한
    internal int thiefStealthMode = 1; // 0: 정상, 1: 도둑질 무죄·발각0%·현상금0%(대도의 장갑 상시)
    internal int thiefDetectionMode = 3;     // 0: 정상, 1: -50%, 2: -80%, 3: 발각0%(완전은신)
    internal int pickpocketSuccessMode = 3;  // 0: 정상, 1: x2, 2: x3, 3: 100% 무조건성공
    internal int bountyImmunityMode = 3;     // 0: 정상, 1: -50%, 2: 0G, 3: 영구무죄(악명0)
    internal int thiefGloveFenceMode = 1;    // 0: 미착용, 1: 기본착용ON, 2: 모든세이브, 3: 장물100%매입
    internal int bagExpandSlots = 60;        // 0: 기본, 20: +20, 40: +40, 60: +60, 900: 창고900
    internal int specialConvenienceMode = 2; // 0: OFF, 1: 즉시수면, 2: 낙하0면제, 3: 자유비행ON
    internal int gearHiderMode = 1; // 0: 전체표시, 1: 투구숨김(기본 ON), 2: 망토숨김, 3: 투구+망토+납도무기 숨김
    internal int weaponImbueMode = 0; // 0: 없음, 1: 화염폭발, 2: 빙결제압, 3: 뇌전연쇄, 4: 흡혈+치명, 5: 전속성마스터
    internal bool isNativeAaClarity = true; // true: 100% 네이티브 1080p AA 고선명 모드 (칼같은 선명도·화질 극대화), false: 67% FSR Quality
    internal string activeFsrScaleLabel = "67%";
    internal bool freezeDaytime = false;
    internal string activeImbue = "없음";
    internal static bool SelfTestMode = false;
    internal bool _suppressConfigSave = false;
    private bool _flingHotkeysYielded = false;

    internal static bool ValuesEqual(object a, object b) {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a is float || a is double || b is float || b is double) {
            try { return Math.Abs(Convert.ToSingle(a) - Convert.ToSingle(b)) < 0.03f; } catch { return false; }
        }
        return a.Equals(b);
    }

    Button btnTopHelmetSwitch;
    Button btnTopThiefSwitch;
    Button btnTopClaritySwitch;
    Dictionary<string, Button> optionMasterSwitches = new Dictionary<string, Button>();
    Dictionary<string, object> optionDefaultOnValues = new Dictionary<string, object>();

    // 하위 호환성 프로퍼티
    internal bool cheatGod { get { return damageTakenMode == 4; } }
    internal bool cheatStamina { get { return playerStaminaMode == 4; } }
    internal bool cheatOneHit { get { return damageDealtMultiplier >= 999.0f; } }
    internal bool cheatNoCool { get { return cooldownMode == 4; } }
    internal bool cheatRomance { get { return romanceMultiplier >= 999; } }
    internal bool cheatTaming { get { return tamingMultiplier >= 999; } }
    internal bool cheatMountSpeed { get { return mountSpeedMultiplier > 1.05f; } }
    internal bool cheatNoMat { get { return craftCostMode == 4; } }
    internal bool cheatGreatSuccess { get { return craftSuccessMultiplier >= 999; } }

    Dictionary<string, List<Button>> optionButtonGroups = new Dictionary<string, List<Button>>();
    Dictionary<string, Button> toggleButtons = new Dictionary<string, Button>();
    List<Button> expButtons = new List<Button>();
    List<Button> speedButtons = new List<Button>();
    List<Button> jumpButtons = new List<Button>();
    List<Button> imbueButtons = new List<Button>();

    internal volatile bool isAutoTalk = false;
    internal volatile bool isAutoSprint = false;
    internal volatile bool isAutoObserve = false;
    internal volatile uint cachedGamePid = 0;
    private string lastHudStateKey = "";

    string Preferences { get { return Path.Combine(root, "tuning", "launcher-preferences.txt"); } }
    string ProfileDir { get { return Path.Combine(root, "tuning", "profiles"); } }
    string BackupDir { get { return Path.Combine(root, "tuning", "backups"); } }
    string ModScript { get { return Path.Combine(root, "tuning", "Apply-CommunityMods.py"); } }

    internal bool IsOverlayActive() {
        string paz0 = Path.Combine(root, @"0008\0.paz");
        return File.Exists(paz0) && (new FileInfo(paz0).Length > 166411120L);
    }

    private IntPtr _cachedGameHandle = IntPtr.Zero;
    private DateTime _lastProcScanUtc = DateTime.MinValue;
    private readonly object _procCacheLock = new object();

    internal bool Running() {
        lock (_procCacheLock) {
            if (_cachedGameHandle != IntPtr.Zero) {
                uint exitCode;
                if (Win32Native.GetExitCodeProcess(_cachedGameHandle, out exitCode) && exitCode == 259U) {
                    return true;
                }
                Win32Native.CloseHandle(_cachedGameHandle);
                _cachedGameHandle = IntPtr.Zero;
                cachedGamePid = 0U;
            }
            DateTime now = DateTime.UtcNow;
            if (cachedGamePid == 0U && (now - _lastProcScanUtc).TotalSeconds < 2.0) {
                return false;
            }
            _lastProcScanUtc = now;
            var list = Process.GetProcessesByName("CrimsonDesert");
            try {
                if (list.Length != 0) {
                    int pid = list[0].Id;
                    cachedGamePid = (uint)pid;
                    _cachedGameHandle = Win32Native.OpenProcess(0x1000U, false, pid);
                    return true;
                }
                cachedGamePid = 0U;
                return false;
            }
            finally { foreach (var p in list) p.Dispose(); }
        }
    }

    public Launcher() {
        Text = "Crimson Desert · 올인원 마스터 런처 & 인게임 HUD · 커스텀 모드 허브 v4.0";
        ClientSize = new Size(1180, 900);
        if (!SelfTestMode) {
            try { long initSil = LiveMemoryCheatEngine.GetSaveFileSilverAmount(); if (initSil > 0) currentGoldBonus = initSil; } catch { }
        }
        // this.DoubleBuffered = true;
        // this.SetStyle(...)
        // this.UpdateStyles();
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BG_DARK;
        ForeColor = TEXT_MAIN;
        Font = new Font("Malgun Gothic", 9.5f, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        BuildUI();
        RefreshProfiles();

        if (!SelfTestMode) {
            RegisterHotkeys();

            // Launch True In-Game HUD Overlay by default and ensure it stays visible
            try {
                inGameHud = new InGameHudOverlayForm(this);
                inGameHud.Show();
                isHudVisible = true;
            } catch { }

            this.Shown += delegate {
                try {
                    if (inGameHud == null || inGameHud.IsDisposed) {
                        inGameHud = new InGameHudOverlayForm(this);
                        inGameHud.Show();
                        isHudVisible = true;
                    }
                    inGameHud.BringToFront();
                    inGameHud.Invalidate();
                } catch { }
            };
        }

        refreshTimer.Tick += delegate { Safe(RefreshRuntime); };
        if (!SelfTestMode) refreshTimer.Start();
        RefreshRuntime();

        bool prevSup = _suppressConfigSave;
        _suppressConfigSave = true;
        try {
            ResetAllToVanilla();
            if (!SelfTestMode) {
                LoadCheatConfig(null);
                SyncClarityFromConfigXml();
            }
        } finally {
            _suppressConfigSave = prevSup;
        }

        var cheatTimer = new System.Windows.Forms.Timer { Interval = 250 };
        try {
            string iniPath = Path.Combine(root, "tuning", "CrimsonDesert_CheatConfig.ini");
            if (File.Exists(iniPath)) _lastExternalIniSyncUtc = File.GetLastWriteTimeUtc(iniPath);
        } catch { }

        cheatTimer.Tick += delegate {
            try {
                SyncExternalIniIfModified();
                bool extFling = LiveMemoryCheatEngine.IsExternalFlingTrainerRunning();
                SyncFlingHotkeyYield(extFling);
                LiveMemoryCheatEngine.ExpMult = (float)expMultiplier;
                LiveMemoryCheatEngine.SyncWithGame(BuildEditorCheatState());
                if (lblLiveCheatsStatus != null && !lblLiveCheatsStatus.IsDisposed) {
                    lblLiveCheatsStatus.Text = extFling
                        ? "[외부 FLiNG 트레이너 실행 중] 내부 훅·Num키 양보 대기 상태"
                        : "[실시간 메모리 반영] " + LiveMemoryCheatEngine.GetLiveCheatStatusString();
                }
            } catch { }
        };
        if (!SelfTestMode) {
            cheatTimer.Start();
            LiveMemoryCheatEngine.ApplyVisualContrastCurve(true);
        }

        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            isAutoTalk = false; isAutoSprint = false; isAutoObserve = false;
            FlushPendingCheatConfigSave();
            UnregisterHotkeys();
            if (!SelfTestMode) {
                LiveMemoryCheatEngine.ApplyVisualContrastCurve(false);
                LiveMemoryCheatEngine.Detach();
            }
            if (inGameHud != null && !inGameHud.IsDisposed) inGameHud.Close();
            lock (_procCacheLock) {
                if (_cachedGameHandle != IntPtr.Zero) {
                    Win32Native.CloseHandle(_cachedGameHandle);
                    _cachedGameHandle = IntPtr.Zero;
                }
            }
            try { boost.Dispose(); } catch { }
        };
        FormClosed += delegate {
            cheatTimer.Stop(); cheatTimer.Dispose();
            refreshTimer.Stop(); refreshTimer.Dispose();
            if (_configSaveDebounceTimer != null) { _configSaveDebounceTimer.Stop(); _configSaveDebounceTimer.Dispose(); }
        };
    }

    internal EditorCheatState BuildEditorCheatState() {
        EditorCheatState s = new EditorCheatState();
        s.DamageTakenMode = damageTakenMode;
        s.DamageDealtMultiplier = damageDealtMultiplier;
        s.PlayerStaminaMode = playerStaminaMode;
        s.CooldownMode = cooldownMode;
        s.ExpMultiplier = (float)expMultiplier;
        s.SpeedMultiplier = speedMultiplier;
        s.JumpMultiplier = jumpMultiplier;
        s.RomanceMultiplier = romanceMultiplier >= 999 ? 999.0f : (float)romanceMultiplier;
        s.AnimalTamingMode = tamingMultiplier >= 999 ? 4 : (tamingMultiplier >= 5 ? 3 : (tamingMultiplier == 3 ? 2 : (tamingMultiplier == 2 ? 1 : 0)));
        s.MountSpeedMultiplier = mountSpeedMultiplier;
        s.MountStaminaMode = mountStaminaMode;
        s.UnderwaterOxygenMode = underwaterOxygenMultiplier >= 999 ? 4 : (underwaterOxygenMultiplier >= 5 ? 3 : (underwaterOxygenMultiplier == 3 ? 2 : (underwaterOxygenMultiplier == 2 ? 1 : 0)));
        s.UnderwaterSpeedMultiplier = underwaterSpeedMultiplier;
        s.CraftMaterialCost = craftCostMode;
        s.CraftGreatSuccess = craftSuccessMultiplier >= 999 ? 4 : (craftSuccessMultiplier >= 5 ? 3 : (craftSuccessMultiplier == 3 ? 2 : (craftSuccessMultiplier == 2 ? 1 : (craftSuccessMultiplier >= 4 ? 3 : 0))));
        s.DurabilityMode = durabilityMode;
        s.GearHiderMode = gearHiderMode;
        s.WeaponImbueMode = weaponImbueMode;
        s.ThiefDetectionMode = thiefDetectionMode;
        s.PickpocketSuccessMode = pickpocketSuccessMode;
        s.BountyImmunityMode = bountyImmunityMode;
        s.ThiefGloveMode = thiefGloveFenceMode;
        s.BagExpandSlots = bagExpandSlots;
        s.SpecialConvenienceMode = specialConvenienceMode;
        s.MaxHealthVal = maxHealthVal;
        s.MaxStaminaVal = maxStaminaVal;
        s.MaxSpiritVal = maxSpiritVal;
        s.FreezeDaytime = freezeDaytime;
        return s;
    }

    internal void PushCheatStateNow() {
        if (SelfTestMode) return;
        try {
            LiveMemoryCheatEngine.ExpMult = (float)expMultiplier;
            LiveMemoryCheatEngine.SyncWithGame(BuildEditorCheatState());
        } catch { }
    }

    private System.Windows.Forms.Timer _configSaveDebounceTimer;
    private volatile bool _configSavePending = false;

    internal void FlushPendingCheatConfigSave() {
        try {
            if (_configSaveDebounceTimer != null) _configSaveDebounceTimer.Stop();
            if (_configSavePending) {
                _configSavePending = false;
                SaveCheatConfigImmediate(null);
            }
        } catch { }
    }

    internal void SaveCheatConfig(string customPath = null) {
        if (_suppressConfigSave) return;
        if (SelfTestMode && string.IsNullOrEmpty(customPath)) return;
        if (string.IsNullOrEmpty(customPath) && !SelfTestMode && IsHandleCreated && !IsDisposed) {
            _configSavePending = true;
            if (_configSaveDebounceTimer == null) {
                _configSaveDebounceTimer = new System.Windows.Forms.Timer { Interval = 90 };
                _configSaveDebounceTimer.Tick += delegate {
                    if (_configSaveDebounceTimer != null) _configSaveDebounceTimer.Stop();
                    if (_configSavePending) {
                        _configSavePending = false;
                        SaveCheatConfigImmediate(null);
                    }
                };
            }
            _configSaveDebounceTimer.Stop();
            _configSaveDebounceTimer.Start();
            return;
        }
        SaveCheatConfigImmediate(customPath);
    }

    internal void SaveCheatConfigImmediate(string customPath = null) {
        if (_suppressConfigSave) return;
        if (SelfTestMode && string.IsNullOrEmpty(customPath)) return;
        try {
            string iniPath = string.IsNullOrEmpty(customPath) ? Path.Combine(root, "tuning", "CrimsonDesert_CheatConfig.ini") : customPath;
            string dir = Path.GetDirectoryName(iniPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("[CheatConfig]");
            sb.AppendLine("DamageTakenMode=" + damageTakenMode);
            sb.AppendLine("DamageDealtMultiplier=" + damageDealtMultiplier.ToString("0.##", inv));
            sb.AppendLine("PlayerStaminaMode=" + playerStaminaMode);
            sb.AppendLine("CooldownMode=" + cooldownMode);
            sb.AppendLine("ExpMultiplier=" + expMultiplier);
            sb.AppendLine("SpeedMultiplier=" + speedMultiplier.ToString("0.##", inv));
            sb.AppendLine("JumpMultiplier=" + jumpMultiplier.ToString("0.##", inv));
            sb.AppendLine("RomanceMultiplier=" + romanceMultiplier);
            sb.AppendLine("TamingMultiplier=" + tamingMultiplier);
            sb.AppendLine("MountSpeedMultiplier=" + mountSpeedMultiplier.ToString("0.##", inv));
            sb.AppendLine("MountStaminaMode=" + mountStaminaMode);
            sb.AppendLine("UnderwaterOxygenMultiplier=" + underwaterOxygenMultiplier);
            sb.AppendLine("UnderwaterSpeed=" + underwaterSpeedMultiplier.ToString("0.##", inv));
            sb.AppendLine("CraftCostMode=" + craftCostMode);
            sb.AppendLine("CraftSuccessMultiplier=" + craftSuccessMultiplier);
            sb.AppendLine("DurabilityMode=" + durabilityMode);
            sb.AppendLine("GearHiderMode=" + gearHiderMode);
            sb.AppendLine("WeaponImbueMode=" + weaponImbueMode);
            sb.AppendLine("ThiefStealthMode=" + thiefStealthMode);
            sb.AppendLine("ThiefDetectionMode=" + thiefDetectionMode);
            sb.AppendLine("PickpocketSuccessMode=" + pickpocketSuccessMode);
            sb.AppendLine("BountyImmunityMode=" + bountyImmunityMode);
            sb.AppendLine("ThiefGloveFenceMode=" + thiefGloveFenceMode);
            sb.AppendLine("BagExpandSlots=" + bagExpandSlots);
            sb.AppendLine("SpecialConvenienceMode=" + specialConvenienceMode);
            sb.AppendLine("MaxHealthVal=" + maxHealthVal);
            sb.AppendLine("MaxStaminaVal=" + maxStaminaVal);
            sb.AppendLine("MaxSpiritVal=" + maxSpiritVal);
            sb.AppendLine("FreezeDaytime=" + (freezeDaytime ? "1" : "0"));
            sb.AppendLine("UpdatedAt=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv));
            File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(false));
            if (string.IsNullOrEmpty(customPath)) _lastExternalIniSyncUtc = File.GetLastWriteTimeUtc(iniPath);
        } catch { }
    }

    internal void LoadCheatConfig(string customPath = null) {
        try {
            string iniPath = string.IsNullOrEmpty(customPath) ? Path.Combine(root, "tuning", "CrimsonDesert_CheatConfig.ini") : customPath;
            if (!File.Exists(iniPath)) return;
            bool prevSup = _suppressConfigSave;
            bool prevSave = suppressSaveSync;
            _suppressConfigSave = true;
            suppressSaveSync = true;
            try {
                ApplyIniLines(File.ReadAllLines(iniPath, Encoding.UTF8));
                if (string.IsNullOrEmpty(customPath)) _lastExternalIniSyncUtc = File.GetLastWriteTimeUtc(iniPath);
            } finally {
                _suppressConfigSave = prevSup;
                suppressSaveSync = prevSave;
            }
            UpdateTopMasterBar();
            RefreshHud();
        } catch { }
    }

    private DateTime _lastExternalIniSyncUtc = DateTime.MinValue;
    void SyncExternalIniIfModified() {
        try {
            if (_configSavePending) { FlushPendingCheatConfigSave(); return; }
            string iniPath = Path.Combine(root, "tuning", "CrimsonDesert_CheatConfig.ini");
            if (!File.Exists(iniPath)) return;
            DateTime wt = File.GetLastWriteTimeUtc(iniPath);
            if (wt <= _lastExternalIniSyncUtc) return;
            _lastExternalIniSyncUtc = wt;
            bool prevSup = _suppressConfigSave;
            bool prevSave = suppressSaveSync;
            _suppressConfigSave = true;
            suppressSaveSync = true;
            try {
                ApplyIniLines(File.ReadAllLines(iniPath, Encoding.UTF8));
            } finally {
                _suppressConfigSave = prevSup;
                suppressSaveSync = prevSave;
            }
            UpdateTopMasterBar();
            RefreshHud();
        } catch { }
    }

    void ApplyIniLines(string[] lines) {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        long pendingAddSil = 0L;
        foreach (string raw in lines) {
            string line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("[") || line.StartsWith("#") || line.StartsWith(";")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line.Substring(0, eq).Trim();
            string v = line.Substring(eq + 1).Trim();
            int iv; float fv;
            switch (k) {
                case "DamageTakenMode": if (int.TryParse(v, out iv)) { damageTakenMode = Math.Max(0, Math.Min(4, iv)); UpdateGroupSelection("dmg_taken", damageTakenMode); } break;
                case "DamageDealtMultiplier": if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out fv)) { damageDealtMultiplier = fv; UpdateGroupSelection("dmg_dealt", fv); } break;
                case "PlayerStaminaMode": if (int.TryParse(v, out iv)) { playerStaminaMode = Math.Max(0, Math.Min(4, iv)); UpdateGroupSelection("stamina", playerStaminaMode); } break;
                case "CooldownMode": if (int.TryParse(v, out iv)) { cooldownMode = Math.Max(0, Math.Min(4, iv)); UpdateGroupSelection("cooldown", cooldownMode); } break;
                case "ExpMultiplier": if (int.TryParse(v, out iv)) { expMultiplier = Math.Max(1, iv); UpdateGroupSelection("exp", expMultiplier); } break;
                case "SpeedMultiplier": if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out fv)) { speedMultiplier = fv; UpdateGroupSelection("speed", fv); } break;
                case "JumpMultiplier": if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out fv)) { jumpMultiplier = Math.Max(1.0f, fv); UpdateGroupSelection("jump", jumpMultiplier); } break;
                case "RomanceMultiplier": if (int.TryParse(v, out iv)) { romanceMultiplier = Math.Max(1, iv); UpdateGroupSelection("romance", romanceMultiplier); } break;
                case "TamingMultiplier":
                case "TamingEaseMode": if (int.TryParse(v, out iv)) { tamingMultiplier = Math.Max(1, iv); UpdateGroupSelection("taming", tamingMultiplier); } break;
                case "MountSpeedMultiplier": if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out fv)) { mountSpeedMultiplier = fv; UpdateGroupSelection("mount_speed", fv); } break;
                case "MountStaminaMode": if (int.TryParse(v, out iv)) { mountStaminaMode = Math.Max(0, Math.Min(4, iv)); UpdateGroupSelection("mount_sta", mountStaminaMode); } break;
                case "UnderwaterOxygenMultiplier":
                case "UnderwaterOxygen": if (int.TryParse(v, out iv)) { underwaterOxygenMultiplier = Math.Max(1, iv); UpdateGroupSelection("und_oxygen", underwaterOxygenMultiplier); } break;
                case "UnderwaterSpeed":
                case "UnderwaterSpeedMultiplier": if (float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out fv)) { underwaterSpeedMultiplier = fv; UpdateGroupSelection("und_speed", fv); } break;
                case "CraftCostMode":
                case "CraftMaterialCost": if (int.TryParse(v, out iv)) { craftCostMode = Math.Max(0, Math.Min(4, iv)); UpdateGroupSelection("craft_cost", craftCostMode); } break;
                case "CraftSuccessMultiplier":
                case "CraftGreatSuccess": if (int.TryParse(v, out iv)) { craftSuccessMultiplier = Math.Max(1, iv); UpdateGroupSelection("craft_succ", craftSuccessMultiplier); } break;
                case "DurabilityMode": if (int.TryParse(v, out iv)) { durabilityMode = Math.Max(0, Math.Min(4, iv)); UpdateGroupSelection("durability", durabilityMode); } break;
                case "GearHiderMode":
                case "GearHideMode": if (int.TryParse(v, out iv)) { gearHiderMode = Math.Max(0, Math.Min(3, iv)); UpdateGroupSelection("gear_hider", gearHiderMode); } break;
                case "WeaponImbueMode": if (int.TryParse(v, out iv)) { weaponImbueMode = Math.Max(0, Math.Min(5, iv)); activeImbue = ImbueLabelForMode(weaponImbueMode); UpdateWeaponImbueButtons(); } break;
                case "ActiveWeaponImbue": if (!string.IsNullOrEmpty(v)) { weaponImbueMode = ParseImbueMode(v); activeImbue = ImbueLabelForMode(weaponImbueMode); UpdateWeaponImbueButtons(); } break;
                case "ThiefStealthMode": if (int.TryParse(v, out iv)) { thiefStealthMode = Math.Max(0, Math.Min(2, iv)); UpdateGroupSelection("thief_stealth", thiefStealthMode); } break;
                case "ThiefDetectionMode": if (int.TryParse(v, out iv)) { thiefDetectionMode = Math.Max(0, Math.Min(3, iv)); UpdateGroupSelection("thief_detect", thiefDetectionMode); } break;
                case "PickpocketSuccessMode": if (int.TryParse(v, out iv)) { pickpocketSuccessMode = Math.Max(0, Math.Min(3, iv)); UpdateGroupSelection("pickpocket", pickpocketSuccessMode); } break;
                case "BountyImmunityMode": if (int.TryParse(v, out iv)) { bountyImmunityMode = Math.Max(0, Math.Min(3, iv)); UpdateGroupSelection("bounty", bountyImmunityMode); } break;
                case "ThiefGloveFenceMode":
                case "ThiefGloveMode": if (int.TryParse(v, out iv)) { thiefGloveFenceMode = Math.Max(0, Math.Min(3, iv)); UpdateGroupSelection("thief_glove", thiefGloveFenceMode); } break;
                case "BagExpandSlots": if (int.TryParse(v, out iv)) { bagExpandSlots = Math.Max(0, iv); UpdateGroupSelection("bag_expand", bagExpandSlots); } break;
                case "SpecialConvenienceMode": if (int.TryParse(v, out iv)) { specialConvenienceMode = Math.Max(0, Math.Min(3, iv)); LiveMemoryCheatEngine.GlideNoFallEnabled = (specialConvenienceMode >= 2); UpdateGroupSelection("convenience", specialConvenienceMode); } break;
                case "MaxHealthVal":
                case "MaxHealth": if (int.TryParse(v, out iv)) maxHealthVal = iv; break;
                case "MaxStaminaVal":
                case "MaxStamina": if (int.TryParse(v, out iv)) maxStaminaVal = iv; break;
                case "MaxSpiritVal":
                case "MaxSpirit": if (int.TryParse(v, out iv)) maxSpiritVal = iv; break;
                case "FreezeDaytime": freezeDaytime = (v == "1"); if (!SelfTestMode) LiveMemoryCheatEngine.SetFreezeDaytime(freezeDaytime); break;
                case "AddHoursTrigger":
                    if (!SelfTestMode && int.TryParse(v, out iv) && iv > 0) LiveMemoryCheatEngine.AdvanceDaytimeHours(iv);
                    break;
                case "PendingAddSilver":
                    if (!SelfTestMode && int.TryParse(v, out iv) && iv > 0) pendingAddSil += iv * 100L;
                    break;
            }
        }
        thiefStealthMode = (thiefDetectionMode > 0 || pickpocketSuccessMode > 0 || bountyImmunityMode > 0) ? Math.Max(1, thiefStealthMode) : 0;
        UpdateGroupSelection("thief_stealth", thiefStealthMode);
        if (!SelfTestMode && pendingAddSil > 0) {
            AddGold(pendingAddSil);
            bool savedSup = _suppressConfigSave;
            _suppressConfigSave = false;
            try { SaveCheatConfigImmediate(null); } finally { _suppressConfigSave = savedSup; }
        }
    }

    void BuildUI() {
        this.SuspendLayout();
        // 1. Top Header Bar
        Panel header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = CARD_BG, Padding = new Padding(16, 6, 16, 6) };
        Label title = new Label {
            Text = "CRIMSON DESERT · MASTER HUB v4.0",
            Font = new Font("Segoe UI", 14.5f, FontStyle.Bold),
            ForeColor = ACCENT_RED,
            BackColor = CARD_BG,
            AutoSize = false,
            Width = 450,
            Height = 28,
            Location = new Point(16, 4)
        };

        Label subtitle = new Label {
            Text = "무병목 100% 선명 그래픽(AA) + 스마트 인게임 HUD + 커스텀 배율/생활 치트",
            Font = new Font("Malgun Gothic", 7.8f),
            ForeColor = TEXT_MUTED,
            BackColor = CARD_BG,
            AutoSize = false,
            Width = 456,
            Height = 20,
            Location = new Point(18, 36)
        };

        lblRuntimeBadge = new Label {
            Text = "● 게임 대기 중",
            Font = new Font("Malgun Gothic", 9f, FontStyle.Bold),
            ForeColor = ACCENT_GOLD,
            BackColor = CARD_BG,
            AutoSize = false,
            Width = 230,
            Height = 20,
            Location = new Point(475, 5)
        };

        btnInGameHudToggle = MakeButton("◆ 인게임 투명 HUD: ON (INS)", 230, 30, ACCENT_GREEN, Color.Black, delegate { ToggleInGameHud(); });
        btnInGameHudToggle.Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold);
        btnInGameHudToggle.Location = new Point(475, 28);

        btnOverlayToggle = MakeButton("◆ 미니 리모컨 (HOME)", 190, 30, CARD_ALT, TEXT_MAIN, delegate { ToggleMiniOverlay(); });
        btnOverlayToggle.Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold);
        btnOverlayToggle.Location = new Point(713, 28);

        Button btnPlay = MakeButton("▶ 게임 즉시 실행", 240, 50, ACCENT_RED, Color.White, delegate { LaunchGame(); });
        btnPlay.Font = new Font("Malgun Gothic", 10.5f, FontStyle.Bold);
        btnPlay.Location = new Point(915, 8);

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(lblRuntimeBadge);
        header.Controls.Add(btnInGameHudToggle);
        header.Controls.Add(btnOverlayToggle);
        header.Controls.Add(btnPlay);
        Controls.Add(header);

        // 2. Bottom Log Console
        Panel bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = CARD_BG, Padding = new Padding(0) };
        Label lblLogHeader = new Label {
            Text = "● 실시간 인게임 HUD · 치트 · 커스텀 모드 상태 로그 ([INSERT] 인게임 모드메뉴 / [PgUp·PgDn] 인게임 공략 단계 전환)",
            Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold),
            ForeColor = ACCENT_GOLD,
            BackColor = CARD_BG,
            AutoSize = false,
            Location = new Point(12, 4),
            Size = new Size(1152, 18)
        };
        txtLog = new Label {
            AutoSize = false,
            Location = new Point(12, 24),
            Size = new Size(1152, 54),
            BackColor = Color.FromArgb(14, 16, 22),
            ForeColor = Color.LightGreen,
            Font = new Font("Consolas", 8.6f),
            Padding = new Padding(8, 3, 8, 3),
            Text = "[대기] 실시간 인게임 HUD · 메모리 치트 · 32개 에디터 엔진 준비 완료 (100,000,000.00 G 실물 동기화됨)"
        };
        bottomBar.Controls.Add(lblLogHeader);
        bottomBar.Controls.Add(txtLog);
        Controls.Add(bottomBar);

        Panel liveStatusPanel = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Color.FromArgb(14, 18, 26), Padding = new Padding(16, 5, 16, 5) };
        lblLiveCheatsStatus = new Label {
            Text = "[실시간 메모리 반영] 게임 연결 대기 중...",
            Font = new Font("Malgun Gothic", 9.2f, FontStyle.Bold),
            ForeColor = Color.FromArgb(46, 204, 113),
            BackColor = Color.Transparent,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        liveStatusPanel.Controls.Add(lblLiveCheatsStatus);

        // 2.5 Always-Visible Top Master Switch Bar (Helmet ON/OFF, Thief ON/OFF, 100% Native AA Clarity, Presets, FLiNG Trainer)
        Panel topMasterBar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(22, 28, 40), Padding = new Padding(12, 3, 12, 3) };

        btnTopHelmetSwitch = MakeButton("★ 헬멧: 숨김(투명) ON", 190, 34, ACCENT_GREEN, Color.Black, delegate { ToggleHelmetVisibility(); });
        btnTopHelmetSwitch.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnTopHelmetSwitch.Location = new Point(12, 3);

        btnTopThiefSwitch = MakeButton("◈ 도둑질 무죄: ON", 160, 34, ACCENT_GREEN, Color.Black, delegate { SetThiefStealthMode(thiefStealthMode > 0 ? 0 : 1); });
        btnTopThiefSwitch.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnTopThiefSwitch.Location = new Point(208, 3);

        btnTopClaritySwitch = MakeButton("▣ 화질: 100% Native AA 초고선명", 185, 34, ACCENT_GREEN, Color.Black, delegate { ToggleResolutionClarity(); });
        btnTopClaritySwitch.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnTopClaritySwitch.Location = new Point(374, 3);

        Button btnTopBal = MakeButton("★ 추천세트 적용", 136, 34, Color.FromArgb(39, 174, 96), Color.White, delegate { ActivateBalancedPreset(); });
        btnTopBal.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnTopBal.Location = new Point(565, 3);

        Button btnTopSup = MakeButton("◆ 초월세트 적용", 136, 34, ACCENT_RED, Color.White, delegate { ActivateSuperPreset(); });
        btnTopSup.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnTopSup.Location = new Point(707, 3);

        Button btnTopOff = MakeButton("↺ 전체 초기화", 125, 34, CARD_ALT, Color.White, delegate { ResetAllToVanilla(); });
        btnTopOff.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnTopOff.Location = new Point(849, 3);

        Button btnTopFling = MakeButton("⚡ FLiNG 트레이너 실행", 186, 34, Color.FromArgb(41, 128, 185), Color.White, delegate { LaunchExternalFlingTrainer(); });
        btnTopFling.Font = new Font("Malgun Gothic", 8.6f, FontStyle.Bold);
        btnTopFling.Location = new Point(980, 3);

        topMasterBar.Controls.Add(btnTopHelmetSwitch);
        topMasterBar.Controls.Add(btnTopThiefSwitch);
        topMasterBar.Controls.Add(btnTopClaritySwitch);
        topMasterBar.Controls.Add(btnTopBal);
        topMasterBar.Controls.Add(btnTopSup);
        topMasterBar.Controls.Add(btnTopOff);
        topMasterBar.Controls.Add(btnTopFling);

        // 3. Main Content Panel (5 Tabs)
        mainContentPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 2, 8, 2), BackColor = BG_DARK };
        mainTabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Malgun Gothic", 9.5f, FontStyle.Bold) };

        TabPage tabGraphics = new TabPage(" ▶ 1~2. 그래픽·시야·부스터 ") { BackColor = BG_DARK, Padding = new Padding(3) };
        TabPage tabCombatCheat = new TabPage(" ⚔ 3. 전투·돈·EXP·속도·점프 ") { BackColor = BG_DARK, Padding = new Padding(3) };
        TabPage tabLifeCheat = new TabPage(" ★ 4. 연애·테이밍·장비·기술 ") { BackColor = BG_DARK, Padding = new Padding(3) };
        TabPage tabGuide = new TabPage(" ▸ 5. 인게임 HUD 공략·매크로 ") { BackColor = BG_DARK, Padding = new Padding(3) };
        TabPage tabMods = new TabPage(" ◈ 6. 유저 커스텀 모드 & 세이브 에디터 ") { BackColor = BG_DARK, Padding = new Padding(3) };

        BuildGraphicsTab(tabGraphics);
        BuildCombatCheatTab(tabCombatCheat);
        BuildLifeCheatTab(tabLifeCheat);
        BuildGuideTab(tabGuide);
        BuildCustomModsTab(tabMods);

        mainTabs.TabPages.Add(tabGraphics);
        mainTabs.TabPages.Add(tabCombatCheat);
        mainTabs.TabPages.Add(tabLifeCheat);
        mainTabs.TabPages.Add(tabGuide);
        mainTabs.TabPages.Add(tabMods);
        mainContentPanel.Controls.Add(mainTabs);

        // Canonical WinForms Docking Order: Fill at index 0, Bottom next, then Top-most at the end
        Controls.Add(mainContentPanel); // Fill (Index 0)
        Controls.Add(bottomBar);        // Bottom
        Controls.Add(liveStatusPanel);  // Top (Below topMasterBar)
        Controls.Add(topMasterBar);     // Top (Below header)
        Controls.Add(header);           // Top (Top-most)

        // 4. Mini Overlay Panel
        BuildMiniOverlayPanel();
        this.ResumeLayout(true);
        Log("마스터 허브 v4.0 가동 완료! 화면 상단에 [투명 클릭통과 인게임 HUD]가 활성화되었습니다. 게임 중 [INSERT] 키로 인게임 모드 메뉴를 직접 클릭할 수 있습니다.");
    }

    void ToggleInGameHud() {
        if (inGameHud == null || inGameHud.IsDisposed) {
            inGameHud = new InGameHudOverlayForm(this);
            inGameHud.Show();
            isHudVisible = true;
        } else {
            isHudVisible = !isHudVisible;
            inGameHud.Visible = isHudVisible;
        }
        btnInGameHudToggle.Text = isHudVisible ? "◆ 인게임 투명 HUD: ON (INS)" : "◆ 인게임 투명 HUD: OFF";
        btnInGameHudToggle.BackColor = isHudVisible ? ACCENT_GREEN : CARD_ALT;
        btnInGameHudToggle.ForeColor = isHudVisible ? Color.Black : TEXT_MUTED;
        Log("인게임 투명 HUD 오버레이: " + (isHudVisible ? "표시됨 (마우스 클릭 100% 통과)" : "숨김"));
    }

    internal void RefreshHud() {
        if (inGameHud != null && !inGameHud.IsDisposed) {
            string stateKey = string.Format(
                "{0}|{1}|{2}|{3}|{4}|{5}|{6:0.##}|{7}|{8}|{9}|{10}|{11}|{12}|{13}|{14}|{15}|{16}|{17}|{18}|{19}|{20}|{21}|{22}|{23}|{24}|{25}|{26}|{27}",
                damageTakenMode, damageDealtMultiplier, playerStaminaMode, cooldownMode, expMultiplier, speedMultiplier, jumpMultiplier,
                currentGoldBonus, romanceMultiplier, tamingMultiplier, mountSpeedMultiplier, mountStaminaMode,
                underwaterOxygenMultiplier, underwaterSpeedMultiplier, craftCostMode, craftSuccessMultiplier, durabilityMode,
                gearHiderMode, weaponImbueMode, thiefStealthMode, thiefDetectionMode, pickpocketSuccessMode,
                bountyImmunityMode, thiefGloveFenceMode, bagExpandSlots, specialConvenienceMode,
                lastInjectedGoldAmount, isNativeAaClarity ? 1 : 0);
            if (stateKey != lastHudStateKey) {
                bool wasInit = string.IsNullOrEmpty(lastHudStateKey);
                lastHudStateKey = stateKey;
                if (!wasInit) inGameHud.NotifyActivity();
                else if (inGameHud.Visible) inGameHud.Invalidate();
            }
        }
    }

    void BuildGraphicsTab(TabPage page) {
        TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        page.Controls.Add(grid);

        Panel leftCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(4), Padding = new Padding(14), AutoScroll = true };
        int y = 12;
        AddSectionTitle(leftCard, "◆ 원클릭 검증 프리셋 (시야·화질·프레임)", ref y);

        profiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 502, Location = new Point(14, y), BackColor = CARD_ALT, ForeColor = TEXT_MAIN, Font = new Font("Malgun Gothic", 9.2f, FontStyle.Bold) };
        leftCard.Controls.Add(profiles);
        y += 34;

        profileInfo = new Label { Width = 502, Height = 72, Location = new Point(14, y), ForeColor = TEXT_MUTED, BackColor = Color.FromArgb(20, 23, 32), Padding = new Padding(8), Font = new Font("Malgun Gothic", 8.5f) };
        leftCard.Controls.Add(profileInfo);
        y += 80;

        profiles.SelectedIndexChanged += delegate {
            if (profiles.SelectedItem == null) return;
            string info = Path.Combine(ProfileDir, profiles.SelectedItem.ToString() + ".txt");
            profileInfo.Text = File.Exists(info) && new FileInfo(info).Length <= 4096 ? File.ReadAllText(info, Encoding.UTF8) : "프리셋 설명 정보가 없습니다.";
        };

        Button btnApplyPreset = MakeButton("✅ 선택한 프리셋 즉시 적용", 244, 38, ACCENT_GREEN, Color.Black, delegate {
            Guard();
            if (profiles.SelectedItem == null) throw new InvalidOperationException("적용할 프로필을 선택해 주세요.");
            ConfigStore.RestoreGraphics(Path.Combine(ProfileDir, profiles.SelectedItem.ToString()), config, BackupDir);
            Log("프리셋 적용 완료: " + profiles.SelectedItem.ToString() + " (언어·조작·난이도 보존)");
        });
        btnApplyPreset.Location = new Point(14, y);

        Button btnSavePreset = MakeButton("■ 현재 설정 프로필 저장", 244, 38, CARD_ALT, TEXT_MAIN, delegate {
            Guard(); ConfigStore.Validate(config); Directory.CreateDirectory(ProfileDir);
            using (var dialog = new SaveFileDialog { InitialDirectory = ProfileDir, Filter = "설정 프로필 (*.xml)|*.xml", FileName = "커스텀-" + DateTime.Now.ToString("MMdd-HHmm") + ".xml", OverwritePrompt = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string path = Path.GetFullPath(dialog.FileName);
                if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(ProfileDir), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("프로필 폴더 안에 저장해 주세요.");
                ConfigStore.Restore(config, path, BackupDir); RefreshProfiles();
                Log("현재 설정을 새 프로필로 저장했습니다: " + Path.GetFileName(path));
            }
        });
        btnSavePreset.Location = new Point(272, y);
        leftCard.Controls.Add(btnApplyPreset);
        leftCard.Controls.Add(btnSavePreset);
        y += 50;

        AddSectionTitle(leftCard, "⚡ CPU 세션 부스터 (스터터링·끊김 억제)", ref y);
        bool prefOn = !File.Exists(Preferences) || File.ReadAllText(Preferences).Trim() != "boost=off";
        if (!File.Exists(Preferences)) File.WriteAllText(Preferences, "boost=on");

        boostChoice = new CheckBox {
            Text = "세션 부스터 활성화 (게임 CPU 우선순위 AboveNormal 자동 유지)",
            Checked = prefOn,
            AutoSize = false,
            Width = 502,
            Height = 22,
            ForeColor = ACCENT_GOLD,
            Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold),
            Location = new Point(14, y)
        };
        leftCard.Controls.Add(boostChoice);
        y += 24;

        lblBoostStatus = new Label {
            AutoSize = false,
            Width = 502,
            Height = 20,
            Location = new Point(14, y),
            ForeColor = TEXT_MUTED,
            Font = new Font("Malgun Gothic", 8.4f)
        };
        leftCard.Controls.Add(lblBoostStatus);
        y += 26;

        boostChoice.CheckedChanged += delegate { Safe(delegate {
            boost.Enabled = boostChoice.Checked;
            lblBoostStatus.Text = boost.Refresh(Path.Combine(root, @"bin64\CrimsonDesert.exe"));
            File.WriteAllText(Preferences, boostChoice.Checked ? "boost=on" : "boost=off");
            Log("세션 부스터 설정 변경: " + (boostChoice.Checked ? "ON (AboveNormal)" : "OFF (기본값)"));
        }); };

        AddSectionTitle(leftCard, "▸ 프레임 계측 및 백업 복구 도구", ref y);
        Button btnBench = MakeButton("▸ 60초 FPS·VRAM 실측 기록", 244, 36, CARD_ALT, TEXT_MAIN, delegate {
            if (!Running()) throw new InvalidOperationException("게임 실행 중에 측정할 수 있습니다.");
            string script = Path.Combine(root, @"tuning\Collect-Session.ps1");
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -WindowStyle Hidden -File \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true });
            Log("60초 PresentMon FPS 및 VRAM 실측 수집을 시작했습니다.");
        });
        btnBench.Location = new Point(14, y);

        Button btnRestore = MakeButton("◀ 자동 백업에서 설정 복원", 244, 36, CARD_ALT, TEXT_MAIN, delegate {
            Guard(); Directory.CreateDirectory(BackupDir);
            using (var dialog = new OpenFileDialog { InitialDirectory = BackupDir, Filter = "설정 백업 (*.xml)|*.xml", CheckFileExists = true }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                ConfigStore.Restore(dialog.FileName, config, BackupDir);
                Log("백업 설정 파일을 복원했습니다: " + Path.GetFileName(dialog.FileName));
            }
        });
        btnRestore.Location = new Point(272, y);
        leftCard.Controls.Add(btnBench);
        leftCard.Controls.Add(btnRestore);
        y += 42;

        Button btnOpenFolder = MakeButton("▸ 세이브/설정 폴더 열기", 244, 34, CARD_ALT, TEXT_MUTED, delegate {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Path.GetDirectoryName(config) + "\"") { UseShellExecute = true });
        });
        btnOpenFolder.Location = new Point(14, y);

        Button btnStandaloneTrainer = MakeButton("◈ 독립 트레이너 창 열기", 244, 34, CARD_ALT, TEXT_MUTED, delegate {
            string tExe = Path.Combine(root, @"tuning\CrimsonTrainer.exe");
            if (File.Exists(tExe)) Process.Start(new ProcessStartInfo(tExe) { WorkingDirectory = Path.GetDirectoryName(tExe), UseShellExecute = true });
        });
        btnStandaloneTrainer.Location = new Point(272, y);
        leftCard.Controls.Add(btnOpenFolder);
        leftCard.Controls.Add(btnStandaloneTrainer);
        y += 44;

        AddSectionTitle(leftCard, "↺ 영구 자가 개선 RSI 엔진 (Recursive Self-Improvement)", ref y);
        lblRsiStatus = new Label {
            Text = "RSI 상태: 감시 준비 완료 · 게임 세션 누적 시 최적화 자율 진화",
            AutoSize = false,
            Width = 502,
            Height = 22,
            Location = new Point(14, y),
            ForeColor = ACCENT_GOLD,
            Font = new Font("Malgun Gothic", 8.6f, FontStyle.Bold)
        };
        leftCard.Controls.Add(lblRsiStatus);
        y += 26;

        Button btnRsiEvolve = MakeButton("↺ 텔레메트리 RSI 분석·세대 진화", 244, 38, ACCENT_BLUE, Color.White, delegate {
            RunRsiEngineCommand("--evolve", "RSI 세션 분석 및 자율 최적화 세대 진화");
        });
        btnRsiEvolve.Font = new Font("Malgun Gothic", 8.6f, FontStyle.Bold);
        btnRsiEvolve.Location = new Point(14, y);

        Button btnRsiRollback = MakeButton("◀ 직전 최적 세대로 롤백 복원", 244, 38, CARD_ALT, TEXT_MUTED, delegate {
            RunRsiEngineCommand("--rollback", "RSI 직전 세대 스냅샷 롤백");
        });
        btnRsiRollback.Font = new Font("Malgun Gothic", 8.6f, FontStyle.Bold);
        btnRsiRollback.Location = new Point(272, y);
        leftCard.Controls.Add(btnRsiEvolve);
        leftCard.Controls.Add(btnRsiRollback);

        grid.Controls.Add(leftCard, 0, 0);

        Panel rightCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(4), Padding = new Padding(14) };
        int ry = 12;
        AddSectionTitle(rightCard, "◈ 세부 그래픽·시야·렌더링 직접 튜닝기", ref ry);

        cbFov = AddTuningRow(rightCard, "시야각 (Camera FOV):", new[] { "1.150000 (FOV 65 · 표준)", "1.200000 (FOV 70 · 광각 추천)", "1.250000 (FOV 72 · 선명한 광시야)", "1.300000 (FOV 75+ · 울트라와이드)", "1.400000 (FOV 85 · 초광각 파노라마)" }, 2, ref ry);
        cbZoom = AddTuningRow(rightCard, "카메라 거리 (Zoom):", new[] { "1.000000 (기본 근접)", "1.100000 (전투 표준 거리)", "1.180000 (넓은 풍경 조망 추천)", "1.250000 (시네마틱 원경)", "1.350000 (최대 줌아웃)" }, 2, ref ry);
        cbBlur = AddTuningRow(rightCard, "흔들림 & 모션블러:", new[] { "0 (완전 제거 · 선명도 극대화 추천)", "25 (약한 현장감)", "50 (보통)" }, 0, ref ry);
        cbUpscale = AddTuningRow(rightCard, "해상도 스케일 (선명도):", new[] { "AA (100% 네이티브 1080p · 고사양)", "67% (Quality · GTX 1070 튕김방지 추천)", "59% (Balanced · 전투 프레임 균형)", "50% (Performance · 고프레임 우선)" }, 1, ref ry);
        cbTex = AddTuningRow(rightCard, "텍스처 품질 (VRAM 8G):", new[] { "High (고해상도 텍스처 · GTX 1070 추천)", "Medium (중간 · VRAM 절약)", "Low (낮음)" }, 0, ref ry);
        cbModel = AddTuningRow(rightCard, "모델 & 지형 디테일:", new[] { "High (고품질 디테일 · 고사양)", "Medium (풍경·건물 균형 · GTX 1070 튕김방지 추천)", "Low (전투 드로우콜 부하 최소화)" }, 1, ref ry);
        cbWater = AddTuningRow(rightCard, "물 & 환경 셰이더:", new[] { "High (고품질 수면 반사 · 고사양)", "Medium (자연스러운 수면 · GTX 1070 튕김방지 추천)", "Low (성능 우선)" }, 1, ref ry);

        ry += 8;
        Button btnApplyCustom = MakeButton("✨ 위 커스텀 그래픽·시야 설정을 게임 엔진에 즉시 반영", 502, 44, ACCENT_BLUE, Color.White, delegate {
            Guard();
            string fov = cbFov.Text.Split(' ')[0];
            string zoom = cbZoom.Text.Split(' ')[0];
            string blur = cbBlur.Text.Split(' ')[0];
            string scale = cbUpscale.Text.Split(' ')[0];
            string tex = cbTex.Text.Split(' ')[0];
            string model = cbModel.Text.Split(' ')[0];
            string water = cbWater.Text.Split(' ')[0];
            ConfigStore.ApplyCustomGraphics(config, BackupDir, fov, zoom, blur, blur, scale, tex, model, water);
            Log(string.Format("커스텀 그래픽 반영 완료: FOV={0}, Zoom={1}, FSR={2}, Texture={3}, Model={4}", fov, zoom, scale, tex, model));
            MessageBox.Show(this, "커스텀 그래픽 및 시야 설정이 엔진 설정 파일에 즉시 반영되었습니다!\n(기존 설정은 backups 폴더에 자동 백업됨)", "적용 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        btnApplyCustom.Font = new Font("Malgun Gothic", 9.8f, FontStyle.Bold);
        btnApplyCustom.Location = new Point(14, ry);
        rightCard.Controls.Add(btnApplyCustom);
        ry += 52;

        Label lblStudioTip = new Label {
            Text = "※ 팁: GTX 1070(8GB VRAM)에서는 [텍스처 High + FSR 67% + 흔들림/블러 0] 조합이 가장 선명하고 잔상 없는 최상의 그래픽과 부드러운 프레임을 제공합니다.",
            Width = 502, Height = 58, Location = new Point(14, ry), ForeColor = ACCENT_GOLD, Font = new Font("Malgun Gothic", 8.5f)
        };
        rightCard.Controls.Add(lblStudioTip);

        grid.Controls.Add(rightCard, 1, 0);
    }

    ComboBox AddTuningRow(Panel parent, string labelText, string[] items, int defaultIdx, ref int y) {
        Label lbl = new Label {
            Text = labelText,
            AutoSize = false,
            Width = 158,
            Height = 22,
            Location = new Point(14, y + 3),
            BackColor = Color.FromArgb(28, 32, 44),
            ForeColor = TEXT_MAIN,
            Font = new Font("Malgun Gothic", 8.4f)
        };
        ComboBox cb = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 348,
            Location = new Point(174, y),
            BackColor = CARD_ALT,
            ForeColor = TEXT_MAIN,
            Font = new Font("Malgun Gothic", 8.0f)
        };
        cb.Items.AddRange(items);
        cb.SelectedIndex = defaultIdx;
        parent.Controls.Add(lbl);
        parent.Controls.Add(cb);
        y += 34;
        return cb;
    }

    string GetCheatUsageTip(string groupKey) {
        switch (groupKey) {
        case "dmg_taken": return "※ 적 피격 데미지를 줄이거나 완전무적(HP고정)으로 보호합니다.";
        case "dmg_dealt": return "※ 타격감을 살리는 배율부터 보스 1방 처치(일격필살)까지 선택합니다.";
        case "stamina":   return "※ 전력 질주[Shift]·회피·강공격 시 기력이 닳지 않게 유지합니다.";
        case "cooldown":  return "※ 전투 스킬 대기시간을 줄이고 투기(Spirit)를 즉시 채웁니다.";
        case "thief_stealth": return "※ 주민 소매치기·상점 절도 시 범죄 발각과 현상금을 차단합니다.";
        case "exp":       return "※ 사냥 및 퀘스트 완료 시 획득하는 경험치 배율을 높입니다.";
        case "speed":     return "※ 기본 x1.0이 물리엔진에 안전하며 필드 이동 시에만 높이세요.";
        case "jump":      return "※ 성벽·절벽 등반 시 도약 높이를 높입니다 (방어/무적 병행 권장).";
        case "romance":   return "※ NPC 대화[E]·선물·인연 퀘스트 시 호감도가 배율만큼 급상승합니다!";
        case "taming":    return "※ 야생마 탑승 후 [Space 연타] 조련 시 저항이 즉시 꺾입니다!";
        case "mount_speed": return "※ 말·곰·와이번 탑승 질주[Shift] 속도를 높입니다 (x1.0~1.5 안전).";
        case "mount_sta": return "※ 말·흑성룡·와이번 탑승 질주[Shift] 시 기력이 무한 유지됩니다!";
        case "und_oxygen": return "※ 강·해안 동굴 잠수[Space/C] 채집 시 산소 감소 없이 잠수합니다!";
        case "und_speed": return "※ 수중 유물 탐색 및 해저 유영 시 헤엄 속도를 쾌적하게 높입니다.";
        case "craft_cost": return "※ 대장간·연금술·요리 시 필요한 재료 개수를 할인하거나 0개로 합니다!";
        case "craft_succ": return "※ 무기·방어구 제작 및 개조 시 전설 등급 대성공 확률을 높입니다!";
        case "durability": return "※ 전투 시 무기·방어구·도구 내구도 마모를 막아 수리비를 아낍니다.";
        case "gear_hider": return "※ 투구 방어력·옵션은 100% 유지한 채 헬멧 외형만 투명하게 숨깁니다!";
        default: return "※ 우측 상단 [ON/OFF] 스위치로 이 치트만 개별적으로 켜고 끕니다.";
        }
    }

    void AddOptionButtonGroup(Panel parent, string groupKey, string desc, string[] labels, object[] values, object currentValue, Action<object> onSelect, ref int y) {
        int rowW = 508;
        Panel row = new Panel { Width = rowW, Height = 62, BackColor = CARD_ALT, Location = new Point(6, y) };
        Label lblDesc = new Label {
            Text = desc,
            BackColor = CARD_ALT,
            ForeColor = TEXT_MAIN,
            Font = new Font("Malgun Gothic", 8.6f, FontStyle.Bold),
            AutoSize = false,
            Width = 380,
            Height = 17,
            Location = new Point(8, 2)
        };
        row.Controls.Add(lblDesc);

        object offVal = values[0];
        object defOnVal = values.Length > 2 ? values[2] : values[values.Length - 1];
        if (groupKey == "stamina" || groupKey == "mount_sta" || groupKey == "durability") defOnVal = values[values.Length - 1];
        if (groupKey == "thief_stealth" || groupKey == "gear_hider") defOnVal = values[1];
        optionDefaultOnValues[groupKey] = defOnVal;

        bool isCurrentlyOn = !ValuesEqual(currentValue, offVal);
        Button btnMaster = MakeButton(
            isCurrentlyOn ? "✔ 켜짐 ON" : "○ 꺼짐 OFF",
            104, 21,
            isCurrentlyOn ? Color.FromArgb(39, 174, 96) : Color.FromArgb(62, 68, 88),
            Color.White,
            null
        );
        btnMaster.Padding = new Padding(0);
        btnMaster.Font = new Font("Malgun Gothic", 7.6f, FontStyle.Bold);
        btnMaster.Location = new Point(rowW - 112, 1);
        btnMaster.Tag = offVal;
        btnMaster.Click += delegate {
            bool anyOn = btnMaster.Text.StartsWith("✔");
            object targetVal = anyOn ? offVal : optionDefaultOnValues[groupKey];
            onSelect(targetVal);
            UpdateGroupSelection(groupKey, targetVal);
        };
        row.Controls.Add(btnMaster);
        optionMasterSwitches[groupKey] = btnMaster;

        int btnCount = labels.Length;
        int btnW = (rowW - 16 - (btnCount - 1) * 4) / btnCount;
        int bx = 8;
        List<Button> btnList = new List<Button>();

        for (int i = 0; i < btnCount; i++) {
            object val = values[i];
            string lbl = labels[i];
            bool isSel = ValuesEqual(val, currentValue);
            Color selBg = i == 0 ? Color.FromArgb(80, 90, 118) : Color.FromArgb(39, 174, 96);
            Button b = MakeButton(lbl, btnW, 22, isSel ? selBg : Color.FromArgb(46, 52, 68), isSel ? Color.White : TEXT_MUTED, null);
            b.Padding = new Padding(0);
            b.Font = new Font("Malgun Gothic", 7.8f, FontStyle.Bold);
            b.Tag = val;
            b.Location = new Point(bx, 23);
            b.Click += delegate {
                onSelect(val);
                UpdateGroupSelection(groupKey, val);
            };
            row.Controls.Add(b);
            btnList.Add(b);
            bx += btnW + 4;
        }

        Label lblTip = new Label {
            Text = GetCheatUsageTip(groupKey),
            ForeColor = Color.FromArgb(155, 215, 255),
            Font = new Font("Malgun Gothic", 7.8f, FontStyle.Regular),
            AutoSize = false,
            Width = rowW - 16,
            Height = 15,
            Location = new Point(8, 46)
        };
        row.Controls.Add(lblTip);
        optionButtonGroups[groupKey] = btnList;
        parent.Controls.Add(row);
        y += 64;
    }

    void UpdateGroupSelection(string groupKey, object selectedVal) {
        List<Button> list;
        if (optionButtonGroups.TryGetValue(groupKey, out list)) {
            for (int i = 0; i < list.Count; i++) {
                var b = list[i];
                bool match = ValuesEqual(b.Tag, selectedVal);
                Color activeBg = (i == 0) ? Color.FromArgb(80, 90, 118) : Color.FromArgb(39, 174, 96);
                b.BackColor = match ? activeBg : Color.FromArgb(46, 52, 68);
                b.ForeColor = match ? Color.White : TEXT_MUTED;
            }
            Button masterBtn;
            if (optionMasterSwitches.TryGetValue(groupKey, out masterBtn) && list.Count > 0) {
                bool isOff = ValuesEqual(list[0].Tag, selectedVal);
                masterBtn.Text = isOff ? "○ 꺼짐 OFF" : "✔ 켜짐 ON";
                masterBtn.BackColor = isOff ? Color.FromArgb(62, 68, 88) : Color.FromArgb(39, 174, 96);
                masterBtn.ForeColor = Color.White;
            }
        }
        UpdateTopMasterBar();
        RefreshHud();
    }

    void UpdateTopMasterBar() {
        if (btnTopHelmetSwitch != null) {
        bool hOn = gearHiderMode > 0;
        btnTopHelmetSwitch.Text = hOn ? "★ 헬멧: 숨김(투명) ON" : "★ 헬멧: 보이기(표시) OFF";
        btnTopHelmetSwitch.BackColor = hOn ? ACCENT_GREEN : Color.FromArgb(65, 72, 92);
        btnTopHelmetSwitch.ForeColor = hOn ? Color.Black : Color.White;
        }
        if (btnTopThiefSwitch != null) {
        bool tOn = thiefStealthMode > 0;
        btnTopThiefSwitch.Text = tOn ? "◈ 도둑질 무죄: ON" : "◈ 도둑질 무죄: OFF";
        btnTopThiefSwitch.BackColor = tOn ? ACCENT_GREEN : Color.FromArgb(65, 72, 92);
        btnTopThiefSwitch.ForeColor = tOn ? Color.Black : Color.White;
        }
        if (btnTopClaritySwitch != null) {
        btnTopClaritySwitch.Text = isNativeAaClarity ? "▣ 화질: 100% 선명(AA)" : ("▣ 화질: " + activeFsrScaleLabel + " FSR 최적화");
        btnTopClaritySwitch.BackColor = isNativeAaClarity ? ACCENT_BLUE : Color.FromArgb(65, 72, 92);
        btnTopClaritySwitch.ForeColor = Color.White;
        }
    }

    internal void WriteHelmetConfigJson(int mode) {
        if (SelfTestMode || _suppressConfigSave) return;
        try {
            string cfgPath = Path.Combine(root, "tuning", "helmet_visibility.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
            string json = string.Format(
                "{{\"hide_helmet\": {0}, \"hide_cloak\": {1}, \"hide_sheathed\": {2}, \"mode\": {3}, \"all_weapons_usable\": false}}",
                mode >= 1 ? "true" : "false",
                mode >= 2 ? "true" : "false",
                mode >= 3 ? "true" : "false",
                mode);
            File.WriteAllText(cfgPath, json, new UTF8Encoding(false));
        } catch { }
    }

    internal void SyncHelmetJson(bool hide) {
        WriteHelmetConfigJson(hide ? 1 : 0);
    }

    internal void LaunchExternalFlingTrainer() {
        try {
            if (IsAuthorizedSelfTestActive()) {
                Log("⚡ [FLiNG 트레이너 안전 보호] 자가 테스트 진행 중에는 외부 트레이너 실행·내부 훅 해제를 건너뜁니다.");
                return;
            }

            IntPtr existingHwnd = IntPtr.Zero;
            int selfPid = Process.GetCurrentProcess().Id;
            try {
                foreach (var pr in Process.GetProcesses()) {
                    try {
                        if (pr.Id != selfPid &&
                            !pr.ProcessName.Equals("CrimsonLauncher", StringComparison.OrdinalIgnoreCase) &&
                            !pr.ProcessName.Equals("CrimsonTrainer", StringComparison.OrdinalIgnoreCase) &&
                            (pr.ProcessName.IndexOf("Plus 12 Trainer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             pr.ProcessName.IndexOf("FLiNG_Trainer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             pr.ProcessName.IndexOf("Crimson Desert Enhanced", StringComparison.OrdinalIgnoreCase) >= 0)) {
                            if (pr.MainWindowHandle != IntPtr.Zero) {
                                existingHwnd = pr.MainWindowHandle;
                            }
                        }
                    } catch { }
                    try { pr.Dispose(); } catch { }
                }
            } catch { }

            string tPath = Path.Combine(root, @"tuning\Crimson Desert-Plus-12-Trainer\Crimson Desert Enhanced v1.0-v2.0x Plus 12 Trainer Updated 2026.10.30.exe");
            if (!File.Exists(tPath)) tPath = Path.Combine(root, "FLiNG_Trainer_Plus12.exe");

            if (existingHwnd == IntPtr.Zero && !File.Exists(tPath)) {
                Log("오류: FLiNG 트레이너 실행 파일(Updated 2026.10.30.exe)을 찾지 못했습니다. (내부 18개 훅 유지)");
                return;
            }

            if (Running() && !SelfTestMode && existingHwnd == IntPtr.Zero) {
                if (MessageBox.Show(
                    this,
                    "게임 실행 중 외부 FLiNG 트레이너를 열면 내부 18개 메모리 훅을 일시 해제하고 양보합니다.\n계속 실행하시겠습니까?",
                    "외부 FLiNG 트레이너 실행 확인",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                ) != DialogResult.Yes) {
                    return;
                }
            }

            // Notify SessionBoost & yield Num0..9 / .text hooks ONLY after target availability & confirmation
            LiveMemoryCheatEngine.NotifyExternalFlingLaunched();
            LiveMemoryCheatEngine.RevertAllHooks();
            SyncFlingHotkeyYield(true);

            if (existingHwnd != IntPtr.Zero) {
                Win32Native.SetForegroundWindow(existingHwnd);
                Log("⚡ [FLiNG Plus 12 트레이너] 실행 중인 외부 트레이너 창을 전면으로 전환했습니다 (내부 훅/Num키 양보 유지).");
                return;
            }

            Process.Start(new ProcessStartInfo {
                FileName = tPath,
                WorkingDirectory = Path.GetDirectoryName(tPath),
                UseShellExecute = true,
                Verb = "runas"
            });
            Log("⚡ [FLiNG Plus 12 트레이너 Updated 2026.10.30] 관리자 권한 실행 + 내부 메모리 훅/Num키 양보 완료!");
        } catch (Exception ex) {
            Log("FLiNG 트레이너 실행 오류: " + ex.Message);
        }
    }

    internal void ToggleHelmetVisibility() {
        int nextMode = (gearHiderMode > 0) ? 0 : 1;
        SetGearHiderMode(nextMode);
    }

    void SyncClarityFromConfigXml() {
        try {
            if (!File.Exists(config)) return;
            var doc = ConfigStore.Load(config);
            var node = doc.SelectSingleNode("//*[@Name='_upscaleResolution']") as XmlElement;
            if (node != null) {
                string sel = node.GetAttribute("_select");
                if (!string.IsNullOrEmpty(sel)) {
                    isNativeAaClarity = string.Equals(sel, "AA", StringComparison.OrdinalIgnoreCase);
                    if (!isNativeAaClarity) activeFsrScaleLabel = sel;
                    UpdateTopMasterBar();
                }
            }
        } catch { }
    }

    internal void ToggleResolutionClarity() {
        if ((Running() || IsAuthorizedSelfTestActive()) && !SelfTestMode) {
            Log("▣ [화질/XML 보호] 게임 실행 또는 자가 테스트 중에는 user_engine_option_save.xml 덮어쓰기 및 100% AA 전환을 건너뜁니다.");
            return;
        }
        isNativeAaClarity = !isNativeAaClarity;
        UpdateTopMasterBar();
        try {
            string scale = isNativeAaClarity ? "AA" : "67%";
            string tex = isNativeAaClarity ? "Medium" : "High";
            string model = "High";
            string water = "High";
            if (File.Exists(config)) {
                ConfigStore.ApplyCustomGraphics(config, BackupDir, "1.250000", "1.180000", "0", "0", scale, tex, model, water);
            }
            Log(isNativeAaClarity ? "▣ [100% 네이티브 AA 모드] 해상도를 100% AA로 변경했습니다. (주의: GTX 1070에서 활공/밀집 지형 시 GPU 과부하 가능성이 있으므로 67% FSR 모드를 권장합니다)" : "▣ [GTX 1070 튕김방지 모드 ON] FSR 67% Quality + 텍스처 High + 모델/물 High 조합으로 고화질·고프레임을 동시 달성했습니다!");
        } catch { }
    }

    void BuildCombatCheatTab(TabPage page) {
        Panel noticeBar = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Color.FromArgb(20, 52, 36), Padding = new Padding(8, 2, 8, 2) };
        Label lblNotice = new Label {
            Text = "※ [조작키 충돌 방지 완료] 굴뚝 청소(F2) 등 인게임 조작키(F1~F12) 100% 정상 통과! 치트는 화면 버튼 클릭 또는 [Ctrl+Alt+F1~F10]",
            ForeColor = Color.FromArgb(120, 255, 175),
            Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold),
            AutoSize = false,
            Width = 1120,
            Height = 19,
            Location = new Point(10, 3)
        };
        noticeBar.Controls.Add(lblNotice);

        TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        // Canonical WinForms Docking Order: Fill at index 0, Top at index 1
        page.Controls.Add(grid);
        page.Controls.Add(noticeBar);

        Panel leftCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(3), Padding = new Padding(8, 4, 8, 4), AutoScroll = false };
        int y = 4;
        AddSectionTitle(leftCard, "■ 전투 방어 · 받는 피해 조절 [마우스 클릭 / Ctrl+Alt+F1]", ref y);
        AddOptionButtonGroup(leftCard, "dmg_taken", "■ 받는 피해 감소율",
            new[] { "OFF(정상)", "-30%", "-50%추천", "-75%", "완전무적" },
            new object[] { 0, 1, 2, 3, 4 }, damageTakenMode,
            val => { SetDamageTakenMode((int)val); }, ref y);

        AddSectionTitle(leftCard, "⚔ 전투 공격력 · 주는 피해 배율 [마우스 클릭 / Ctrl+Alt+F3]", ref y);
        AddOptionButtonGroup(leftCard, "dmg_dealt", "⚔ 공격력 배율",
            new[] { "x1.0", "x1.5", "x2.0추천", "x3.0", "x5.0", "일격필살" },
            new object[] { 1.0f, 1.5f, 2.0f, 3.0f, 5.0f, 999.0f }, damageDealtMultiplier,
            val => { SetDamageDealtMultiplier((float)val); }, ref y);

        AddSectionTitle(leftCard, "⚡ 기력(스태미나) 소모율 [마우스 클릭 / Ctrl+Alt+F2]", ref y);
        AddOptionButtonGroup(leftCard, "stamina", "⚡ 기력 소모 조절",
            new[] { "OFF(정상)", "소모-30%", "소모-50%", "회복2배추천", "무한기력" },
            new object[] { 0, 1, 2, 3, 4 }, playerStaminaMode,
            val => { SetPlayerStaminaMode((int)val); }, ref y);

        AddSectionTitle(leftCard, "◆ 스킬 쿨타임 단축 [마우스 클릭 / Ctrl+Alt+F4]", ref y);
        AddOptionButtonGroup(leftCard, "cooldown", "◆ 스킬 쿨타임",
            new[] { "OFF(정상)", "-30%단축", "-50%추천", "-75%단축", "0초(노쿨)" },
            new object[] { 0, 1, 2, 3, 4 }, cooldownMode,
            val => { SetCooldownMode((int)val); }, ref y);

        AddSectionTitle(leftCard, "◈ 도둑질·소매치기 무죄/은신 토글 (범죄차단·대도의장갑)", ref y);
        AddOptionButtonGroup(leftCard, "thief_stealth", "◈ 도둑질·절도 치트 (범죄차단·현상금0%)",
            new[] { "OFF(정상)", "ON:절도무죄", "ON:대도의장갑" },
            new object[] { 0, 1, 2 }, thiefStealthMode,
            val => { SetThiefStealthMode((int)val); }, ref y);

        grid.Controls.Add(leftCard, 0, 0);

        Panel rightCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(3), Padding = new Padding(8, 4, 8, 4), AutoScroll = false };
        int ry = 4;
        AddSectionTitle(rightCard, "★ 재화(골드/은화) 즉시 세이브 주입", ref ry);
        Panel goldRow = new Panel { Width = 508, Height = 58, BackColor = CARD_ALT, Location = new Point(6, ry) };
        lblGoldSaveStatus = new Label {
            Text = string.Format("현재 세이브 은화: {0:N2} G (실물 반영)", currentGoldBonus / 100.0),
            ForeColor = ACCENT_GOLD,
            Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold),
            AutoSize = false,
            Width = 490,
            Height = 19,
            Location = new Point(8, 4)
        };
        goldRow.Controls.Add(lblGoldSaveStatus);

        int gx = 8, gy = 26;
        foreach (long amt in new[] { 1000000L, 5000000L, 10000000L, 100000000L, 56511L }) {
            long a = amt;
            string lbl = a == 56511L ? "순정복구" : (a >= 100000000L ? "+100만G" : a >= 10000000L ? "+10만G" : a >= 5000000L ? "+5만G" : "+1만G");
            Button b = MakeButton(lbl, 92, 26, Color.FromArgb(50, 56, 74), ACCENT_GOLD, delegate { AddGold(a); });
            b.Font = new Font("Malgun Gothic", 8.2f, FontStyle.Bold);
            b.Location = new Point(gx, gy); goldRow.Controls.Add(b); gx += 96;
        }
        rightCard.Controls.Add(goldRow);
        ry += 62;

        AddSectionTitle(rightCard, "✨ 경험치(EXP) 획득 배율 [마우스 클릭 / Ctrl+Alt+F5]", ref ry);
        AddOptionButtonGroup(rightCard, "exp", "✨ 경험치 배율",
            new[] { "x1", "x2추천", "x3", "x4", "x5", "x7", "x10" },
            new object[] { 1, 2, 3, 4, 5, 7, 10 }, expMultiplier,
            val => { SetExpMultiplier((int)val); }, ref ry);

        AddSectionTitle(rightCard, "⚡ 이동/전투 속도 배율 [마우스 클릭 / Ctrl+Alt+F6]", ref ry);
        AddOptionButtonGroup(rightCard, "speed", "⚡ 이동 속도",
            new[] { "x1.0기본", "x1.5자연", "x2.0추천", "x3.0", "x4.0" },
            new object[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f }, speedMultiplier,
            val => { SetSpeedMultiplier((float)val); }, ref ry);

        AddSectionTitle(rightCard, "▲ 점프 도약력 배율 [마우스 클릭 / Ctrl+Alt+F7]", ref ry);
        AddOptionButtonGroup(rightCard, "jump", "▲ 점프력",
            new[] { "OFF(x1)", "x1.5배", "x2배추천", "x3배", "x4배", "x5배" },
            new object[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f, 5.0f }, jumpMultiplier,
            val => { SetJumpMultiplier(Convert.ToSingle(val)); }, ref ry);

        ry += 2;
        AddSectionTitle(rightCard, "◆ 원클릭 프리셋 모드", ref ry);
        Button btnBalanced = MakeButton("★ 밸런스 추천세트 (방어50% + 공2배 + 무한기력)", 508, 30, ACCENT_GREEN, Color.Black, delegate {
            ActivateBalancedPreset();
        });
        btnBalanced.Location = new Point(6, ry); rightCard.Controls.Add(btnBalanced); ry += 33;

        Button btnSuper = MakeButton("◆ 초월 풀세트 (완전무적 + 일격필살 + 0초노쿨)", 508, 30, ACCENT_RED, Color.White, delegate {
            ActivateSuperPreset();
        });
        btnSuper.Location = new Point(6, ry); rightCard.Controls.Add(btnSuper); ry += 33;

        Button btnVanilla = MakeButton("↺ 바닐라 순정 복귀 (모든 치트/배율 1배 초기화)", 508, 30, CARD_ALT, TEXT_MUTED, delegate {
            ResetAllToVanilla();
        });
        btnVanilla.Location = new Point(6, ry); rightCard.Controls.Add(btnVanilla);

        grid.Controls.Add(rightCard, 1, 0);
    }

    void BuildLifeCheatTab(TabPage page) {
        TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        page.Controls.Add(grid);

        Panel leftCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(3), Padding = new Padding(8, 4, 8, 4), AutoScroll = false };
        int y = 4;
        AddSectionTitle(leftCard, "♥ 연애 · NPC 호감도 획득 배율", ref y);
        AddOptionButtonGroup(leftCard, "romance", "♥ 호감도 획득 배율",
            new[] { "x1기본", "x2수월", "x3추천", "x5쾌속", "x10배", "즉시MAX" },
            new object[] { 1, 2, 3, 5, 10, 999 }, romanceMultiplier,
            val => { SetRomanceMultiplier((int)val); }, ref y);

        AddSectionTitle(leftCard, "▸ 동물 길들이기(테이밍) 난이도 완화", ref y);
        AddOptionButtonGroup(leftCard, "taming", "▸ 조련 난이도",
            new[] { "x1기본", "저항-50%", "숙련3배", "달인5배", "즉시성공" },
            new object[] { 1, 2, 3, 5, 999 }, tamingMultiplier,
            val => { SetTamingEase((int)val); }, ref y);

        AddSectionTitle(leftCard, "▸ 탈것 질주 속도 배율", ref y);
        AddOptionButtonGroup(leftCard, "mount_speed", "▸ 탈것 속도",
            new[] { "x1.0기본", "x1.5자연", "x2.0추천", "x3.0질주", "x4.0" },
            new object[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f }, mountSpeedMultiplier,
            val => { SetMountSpeed((float)val); }, ref y);

        AddSectionTitle(leftCard, "▸ 탈것 스태미나 소모율", ref y);
        AddOptionButtonGroup(leftCard, "mount_sta", "▸ 탈것 기력 소모",
            new[] { "OFF(정상)", "소모-30%", "소모-50%", "소모-75%", "무한기력" },
            new object[] { 0, 1, 2, 3, 4 }, mountStaminaMode,
            val => { SetMountStaminaMode((int)val); }, ref y);

        AddSectionTitle(leftCard, "● 수중 작업 · 잠수 호흡(산소) [신규]", ref y);
        AddOptionButtonGroup(leftCard, "und_oxygen", "● 수중 호흡량",
            new[] { "OFF(정상)", "호흡2배", "호흡3배", "호흡5배", "무한잠수" },
            new object[] { 1, 2, 3, 5, 999 }, underwaterOxygenMultiplier,
            val => { SetUnderwaterOxygen((int)val); }, ref y);

        AddSectionTitle(leftCard, "● 수중 수영/잠수 이동속도 [신규]", ref y);
        AddOptionButtonGroup(leftCard, "und_speed", "● 수중 수영속도",
            new[] { "x1.0기본", "x1.5쾌적", "x2.0빠름", "x3.0초고속" },
            new object[] { 1.0f, 1.5f, 2.0f, 3.0f }, underwaterSpeedMultiplier,
            val => { SetUnderwaterSpeed((float)val); }, ref y);

        grid.Controls.Add(leftCard, 0, 0);

        Panel rightCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(3), Padding = new Padding(8, 4, 8, 4), AutoScroll = false };
        int ry = 4;
        AddSectionTitle(rightCard, "▲ 장비 제작 · 재료 소모율", ref ry);
        AddOptionButtonGroup(rightCard, "craft_cost", "▲ 제작 재료 소모",
            new[] { "OFF(정상)", "-25%할인", "-50%반값", "-75%할인", "무재료0개" },
            new object[] { 0, 1, 2, 3, 4 }, craftCostMode,
            val => { SetCraftCost((int)val); }, ref ry);

        AddSectionTitle(rightCard, "✨ 장비 제작/강화 대성공 확률", ref ry);
        AddOptionButtonGroup(rightCard, "craft_succ", "✨ 대성공 확률",
            new[] { "OFF(1배)", "성공2배추천", "성공3배", "성공5배", "전설100%" },
            new object[] { 1, 2, 3, 5, 999 }, craftSuccessMultiplier,
            val => { SetCraftSuccess((int)val); }, ref ry);

        AddSectionTitle(rightCard, "■ 장비 내구도 소모율", ref ry);
        AddOptionButtonGroup(rightCard, "durability", "■ 내구도 소모",
            new[] { "OFF(정상)", "소모-30%", "소모-50%", "소모-75%", "무한내구도" },
            new object[] { 0, 1, 2, 3, 4 }, durabilityMode,
            val => { SetDurabilityMode((int)val); }, ref ry);

        AddSectionTitle(rightCard, "★ 장비 외형 표시/숨김 토글 (헬멧·망토·납도무기)", ref ry);
        AddOptionButtonGroup(rightCard, "gear_hider", "★ 투구·망토·무기 숨김 토글",
            new[] { "전체표시", "헬멧숨김", "헬멧+망토", "무기까지숨김" },
            new object[] { 0, 1, 2, 3 }, gearHiderMode,
            val => { SetGearHiderMode((int)val); }, ref ry);

        ry += 2;
        AddSectionTitle(rightCard, "◈ 장비 부여 기술 · 속성 특수 각인", ref ry);
        string[] imbues = new[] {
            "◆ 화염 폭발 기술 각인", "❄ 빙결 제압 기술 각인",
            "⚡ 뇌전 연쇄 기술 각인", "◆ 흡혈 + 치명타 100%",
            "★ 전속성 마스터 각인", "○ 각인 해제 (OFF)"
        };
        int[] imbueModes = new[] { 1, 2, 3, 4, 5, 0 };
        int ix = 6, iy = ry;
        for (int i = 0; i < imbues.Length; i++) {
            string imb = imbues[i];
            int iMode = imbueModes[i];
            Button b = MakeButton(imb, 250, 32, iMode == weaponImbueMode ? ACCENT_BLUE : CARD_ALT, TEXT_MAIN, delegate {
                SetWeaponImbueMode(iMode);
            });
            b.Tag = iMode;
            b.Font = new Font("Malgun Gothic", 8.6f, FontStyle.Bold);
            b.Location = new Point(ix, iy);
            rightCard.Controls.Add(b); imbueButtons.Add(b);
            if (i % 2 == 0) ix = 264; else { ix = 6; iy += 35; }
        }

        grid.Controls.Add(rightCard, 1, 0);
    }

    private readonly object saveCheatLock = new object();
    private bool suppressSaveSync = false; // Enabled for manual user cheats

    internal void RunSaveCheatAsync(string args) { RunSaveCheatAsync(args, ""); }
    internal void RunSaveCheatAsync(string args, string label) { /* Pure RAM mode: never automatically touch disk save files */ return; }

    internal void RunManualSaveCheatAsync(string args, string label) {
        string pyScript = Path.Combine(root, @"tuning\Apply-RealCheats.py");
        if (!File.Exists(pyScript)) return;
        Log("♥ [SWISS Knife 수동 세이브 패치] " + label + " 실행 중...");
        new Thread(delegate() {
            lock (saveCheatLock) {
                try {
                    var psi = new ProcessStartInfo("python", "\"" + pyScript + "\" " + args) {
                        WorkingDirectory = Path.GetDirectoryName(pyScript),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var p = Process.Start(psi)) {
                        string stdout = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(15000);
                        if (!string.IsNullOrWhiteSpace(stdout)) {
                            foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                                Log(line.Trim());
                            }
                        }
                    }
                } catch (Exception ex) {
                    Log("세이브 패치 오류: " + ex.Message);
                }
            }
        }) { IsBackground = true }.Start();
    }

    // --- 세분화된 배율 변경 메소드들 (실시간 메모리 + 전 슬롯 세이브 연동) ---
    internal void SetDamageTakenMode(int mode) {
        mode = Math.Max(0, Math.Min(4, mode));
        damageTakenMode = mode;
        UpdateGroupSelection("dmg_taken", mode);
        if (!SelfTestMode) LiveMemoryCheatEngine.SetDefenseMode(mode);
        string[] desc = { "정상 피해 100%", "피해 30% 감소", "피해 50% 반감 (추천)", "피해 75% 대폭 감소", "완전 무적 (God Mode)" };
        Log("■ [방어 난이도] " + desc[mode] + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetDamageMultiplier(float m) { SetDamageDealtMultiplier(m); }

    internal void SetDamageDealtMultiplier(float m) {
        damageDealtMultiplier = m;
        UpdateGroupSelection("dmg_dealt", m);
        if (!SelfTestMode) LiveMemoryCheatEngine.SetDamageMultiplier(m);
        Log(m >= 999 ? "⚔ [공격력] 일격필살(One-Hit Kill, 실시간 메모리 훅) 활성화!" : string.Format("⚔ [공격력] 주는 피해 x{0:0.#}배 (실시간 무기 데미지 메모리 훅) 적용!", m));
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetPlayerStaminaMode(int mode) {
        mode = Math.Max(0, Math.Min(4, mode));
        playerStaminaMode = mode;
        UpdateGroupSelection("stamina", mode);
        if (!SelfTestMode) LiveMemoryCheatEngine.SetStaminaMode(mode);
        string[] desc = { "OFF(정상)", "소모 -30%", "소모 -50%", "회복 2배 (추천)", "무한 기력 (Infinite)" };
        Log("⚡ [기력(스태미나)] " + desc[mode] + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetCooldownMode(int mode) {
        mode = Math.Max(0, Math.Min(4, mode));
        cooldownMode = mode;
        UpdateGroupSelection("cooldown", mode);
        if (!SelfTestMode) LiveMemoryCheatEngine.SetCooldownMode(mode);
        string[] desc = { "기본 정상 쿨타임", "쿨타임 30% 단축", "쿨타임 50% 단축 (추천)", "쿨타임 75% 단축", "스킬 0초 노쿨타임" };
        Log("◆ [스킬 쿨타임] " + desc[mode] + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetRomanceMultiplier(int m) {
        romanceMultiplier = m;
        UpdateGroupSelection("romance", m);
        Log(m >= 999 ? "♥ [연애/호감도] NPC 호감도 즉시 100% MAX 해금!" : string.Format("♥ [연애/호감도] 대화/선물 호감도 획득량 x{0}배 적용!", m));
        if (!SelfTestMode && !DumpUiMode && !_suppressConfigSave && !suppressSaveSync && !Running()) {
            if (m >= 999) {
                RunManualSaveCheatAsync("--romance-max", "NPC 호감도·연애 최대치 & 일일 제한 해제 세이브 반영");
            } else if (m > 1) {
                RunManualSaveCheatAsync("--reset-friendly-daily", "NPC 일일 호감도 대화·선물 횟수 제한 초기화");
            }
        }
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetTamingEase(int m) {
        tamingMultiplier = m;
        UpdateGroupSelection("taming", m);
        Log(m >= 999 ? "▸ [동물 길들이기] 미니게임 즉시 성공(100%) 활성화!" : string.Format("▸ [동물 길들이기] 저항 완화 및 조련 속도 x{0}배 적용!", m));
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetMountSpeed(float m) {
        mountSpeedMultiplier = m;
        UpdateGroupSelection("mount_speed", m);
        Log(string.Format("▸ [탈것 질주] 탈것 달리기 속도 x{0:0.#}배 (실시간 이동 벡터 훅) 적용!", m));
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetMountStaminaMode(int mode) {
        mode = Math.Max(0, Math.Min(4, mode));
        mountStaminaMode = mode;
        UpdateGroupSelection("mount_sta", mode);
        if (!SelfTestMode) LiveMemoryCheatEngine.SetMountStaminaMode(mode);
        string[] desc = { "OFF(정상 소모)", "소모 -30%", "소모 -50%", "소모 -75%", "무한 기력 (Infinite)" };
        Log("▸ [탈것 기력] " + desc[mode] + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetUnderwaterOxygen(int m) {
        underwaterOxygenMultiplier = m;
        UpdateGroupSelection("und_oxygen", m);
        Log(m >= 999 ? "● [수중 잠수] 산소 게이지 무한 (무제한 잠수) 활성화!" : string.Format("● [수중 호흡] 잠수 산소 호흡량 x{0}배 적용!", m));
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetUnderwaterSpeed(float m) {
        underwaterSpeedMultiplier = m;
        UpdateGroupSelection("und_speed", m);
        Log(string.Format("● [수중 수영] 잠수/헤엄 이동속도 x{0:0.#}배 적용!", m));
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetCraftCost(int mode) {
        mode = Math.Max(0, Math.Min(4, mode));
        craftCostMode = mode;
        UpdateGroupSelection("craft_cost", mode);
        string[] desc = { "정상 재료 소모", "제작 재료 25% 할인", "제작 재료 50% 반값 (추천)", "제작 재료 75% 할인", "재료 무소모 (0개 제작)" };
        Log("▲ [장비 제작비용] " + desc[mode] + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetCraftSuccess(int m) {
        craftSuccessMultiplier = m;
        UpdateGroupSelection("craft_succ", m);
        Log(m >= 999 ? "✨ [제작/강화] 전설 등급 대성공 확률 100% 확정!" : string.Format("✨ [제작/강화] 대성공/강화 확률 x{0}배 적용!", m));
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetDurabilityMode(int mode) {
        mode = Math.Max(0, Math.Min(4, mode));
        durabilityMode = mode;
        UpdateGroupSelection("durability", mode);
        string[] desc = { "정상 내구도 소모", "내구도 소모 -30%", "내구도 소모 -50%", "내구도 소모 -75%", "무기/방어구 무한 내구도" };
        Log("■ [장비 내구도] " + desc[mode] + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    void SyncThiefMasterState() {
        thiefStealthMode = (thiefDetectionMode > 0 || pickpocketSuccessMode > 0 || bountyImmunityMode > 0) ? Math.Max(1, thiefStealthMode) : 0;
        UpdateGroupSelection("thief_stealth", thiefStealthMode);
        UpdateTopMasterBar();
    }

    internal void SetThiefDetectionMode(int mode) {
        mode = Math.Max(0, Math.Min(3, mode));
        thiefDetectionMode = mode;
        UpdateGroupSelection("thief_detect", mode);
        SyncThiefMasterState();
        string txt = mode == 3 ? "발각 0% (완전은신)" : (mode == 2 ? "발각 -80%" : (mode == 1 ? "발각 -50%" : "정상(OFF)"));
        Log("◈ [도둑질 발각] " + txt + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetPickpocketSuccessMode(int mode) {
        mode = Math.Max(0, Math.Min(3, mode));
        pickpocketSuccessMode = mode;
        UpdateGroupSelection("pickpocket", mode);
        SyncThiefMasterState();
        string txt = mode == 3 ? "100% 무조건 성공" : (mode == 2 ? "성공률 x3배" : (mode == 1 ? "성공률 x2배" : "정상(OFF)"));
        Log("◈ [소매치기 성공률] " + txt + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetBountyImmunityMode(int mode) {
        mode = Math.Max(0, Math.Min(3, mode));
        bountyImmunityMode = mode;
        UpdateGroupSelection("bounty", mode);
        SyncThiefMasterState();
        string txt = mode == 3 ? "영구 무죄 (악명 0)" : (mode == 2 ? "현상금 0G 즉시 면제" : (mode == 1 ? "현상금 -50%" : "정상(OFF)"));
        Log("◈ [범죄 현상금/수배] " + txt + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetThiefGloveFenceMode(int mode) {
        mode = Math.Max(0, Math.Min(3, mode));
        thiefGloveFenceMode = mode;
        UpdateGroupSelection("thief_glove", mode);
        string txt = mode == 3 ? "장물 100% 정상매입" : (mode == 2 ? "모든 세이브 슬롯 소유" : (mode == 1 ? "대도의 장갑 착용ON" : "미착용"));
        Log("◈ [대도의 장갑/장물] " + txt + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetBagExpandSlots(int slots) {
        bagExpandSlots = Math.Max(0, slots);
        UpdateGroupSelection("bag_expand", bagExpandSlots);
        string txt = bagExpandSlots == 900 ? "창고 900칸 확장" : (bagExpandSlots > 0 ? string.Format("가방 +{0}칸 확장", bagExpandSlots) : "기본 슬롯");
        Log("◈ [인벤토리 확장] " + txt + " 설정 완료!");
        if (!SelfTestMode && !_suppressConfigSave && !suppressSaveSync && bagExpandSlots > 0 && !Running()) {
            RunManualSaveCheatAsync("--bag-expand 252", "가방 700칸 확장 반영");
        }
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetSpecialConvenienceMode(int mode) {
        mode = Math.Max(0, Math.Min(3, mode));
        specialConvenienceMode = mode;
        if (!SelfTestMode) {
            LiveMemoryCheatEngine.GlideNoFallEnabled = (mode >= 2);
            if (mode == 1 && !_suppressConfigSave && !suppressSaveSync) {
                LiveMemoryCheatEngine.TriggerImmediateRest();
            }
        }
        UpdateGroupSelection("convenience", mode);
        string txt = mode == 3 ? "체공/자유 비행(Fly ON)" : (mode == 2 ? "낙하 피해 0 면제" : (mode == 1 ? "침대 즉시 수면(쿨 0초 +1h)" : "정상(OFF)"));
        Log("◈ [특수 편의 치트] " + txt + " 설정 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetThiefStealthMode(int mode) {
        mode = Math.Max(0, Math.Min(2, mode));
        thiefStealthMode = mode;
        if (mode > 0) {
            thiefDetectionMode = 3;
            pickpocketSuccessMode = 3;
            bountyImmunityMode = 3;
            thiefGloveFenceMode = mode >= 2 ? 3 : 1;
        } else {
            thiefDetectionMode = 0;
            pickpocketSuccessMode = 0;
            bountyImmunityMode = 0;
            thiefGloveFenceMode = 0;
        }
        UpdateGroupSelection("thief_stealth", mode);
        UpdateGroupSelection("thief_detect", thiefDetectionMode);
        UpdateGroupSelection("pickpocket", pickpocketSuccessMode);
        UpdateGroupSelection("bounty", bountyImmunityMode);
        UpdateGroupSelection("thief_glove", thiefGloveFenceMode);
        UpdateTopMasterBar();
        Log(mode > 0 ? "◈ [도둑질 통합 치트] 완전 은신 + 100% 소매치기 + 영구 무죄 + 대도의 장갑 가동!" : "◈ [도둑질 통합 치트] 도둑질/절도 정상 페널티 복구.");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetGearHiderMode(int mode) {
        mode = Math.Max(0, Math.Min(3, mode));
        gearHiderMode = mode;
        UpdateGroupSelection("gear_hider", mode);
        UpdateTopMasterBar();
        WriteHelmetConfigJson(mode);
        if (!SelfTestMode && !_suppressConfigSave && !suppressSaveSync && !Running()) {
            new Thread(delegate() {
                try {
                    string pyScript = Path.Combine(root, @"tuning\Apply-CommunityMods.py");
                    if (File.Exists(pyScript)) {
                        var psi = new ProcessStartInfo("python", "\"" + pyScript + "\" mod-gear-hider " + mode) {
                            WorkingDirectory = Path.GetDirectoryName(pyScript),
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var pr = Process.Start(psi)) { pr.WaitForExit(15000); }
                    }
                } catch { }
            }) { IsBackground = true }.Start();
        }
        string[] desc = { "헬멧 보이기 (모든 투구 외형 기본 표시 ON)", "헬멧 숨김 ON (방어력·스탯 100% 유지 + 맨얼굴/헤어 노출)", "헬멧 + 망토(Cloak) 외형 숨김 ON", "헬멧 + 망토 + 비전투 납도 무기 외형 숨김 ON" };
        Log("★ [장비 외형 표시/숨김 토글] " + desc[mode] + " 적용 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal string ImbueLabelForMode(int mode) {
        switch (mode) {
            case 1: return "화염 폭발 기술 각인";
            case 2: return "빙결 제압 기술 각인";
            case 3: return "뇌전 연쇄";
            case 4: return "흡혈+치명";
            case 5: return "전속성 마스터";
            default: return "없음";
        }
    }

    internal string GetWeaponImbueBadge() {
        switch (weaponImbueMode) {
            case 1: return "화염 폭발";
            case 2: return "빙결 제압";
            case 3: return "뇌전 연쇄";
            case 4: return "흡혈+치명";
            case 5: return "전속성 마스터";
            default: return "없음";
        }
    }

    internal int ParseImbueMode(string label) {
        if (string.IsNullOrEmpty(label) || label.Contains("없음") || label.Contains("해제")) return 0;
        if (label.Contains("화염")) return 1;
        if (label.Contains("빙결")) return 2;
        if (label.Contains("뇌전")) return 3;
        if (label.Contains("흡혈")) return 4;
        if (label.Contains("전속성") || label.Contains("마스터") || label.Contains("슈퍼아머")) return 5;
        return 0;
    }

    internal void UpdateWeaponImbueButtons() {
        foreach (var btn in imbueButtons) {
            bool isMatch = (btn.Tag is int) && ((int)btn.Tag == weaponImbueMode);
            btn.BackColor = isMatch ? (weaponImbueMode == 0 ? Color.FromArgb(80, 90, 118) : ACCENT_BLUE) : CARD_ALT;
            btn.ForeColor = isMatch ? Color.White : TEXT_MAIN;
        }
    }

    internal void SetWeaponImbueMode(int mode) {
        mode = Math.Max(0, Math.Min(5, mode));
        weaponImbueMode = mode;
        activeImbue = ImbueLabelForMode(mode);
        UpdateWeaponImbueButtons();
        Log(mode == 0 ? "◈ [장비 속성각인] 각인 해제 (기본 무기)" : "◈ [장비 속성각인] [" + activeImbue + "] 실시간 공격 배율/흡혈 각인 적용 완료!");
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
    }

    internal void SetImbueQuick(string imb) {
        SetWeaponImbueMode(ParseImbueMode(imb));
    }

    internal void CycleThiefStealth() { SetThiefStealthMode((thiefStealthMode + 1) % 3); }
    internal void CycleGearHider() { SetGearHiderMode((gearHiderMode + 1) % 4); }

    internal void CycleRomance() { int[] v = { 1, 2, 3, 5, 10, 999 }; int i = Array.IndexOf(v, romanceMultiplier); SetRomanceMultiplier(v[(i + 1) % v.Length]); }
    internal void CycleTaming() { int[] v = { 1, 2, 3, 5, 999 }; int i = Array.IndexOf(v, tamingMultiplier); SetTamingEase(v[(i + 1) % v.Length]); }
    internal void CycleMountSpeed() { float[] v = { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f }; int idx = 0; for (int i = 0; i < v.Length; i++) if (Math.Abs(v[i] - mountSpeedMultiplier) < 0.05f) idx = i; SetMountSpeed(v[(idx + 1) % v.Length]); }
    internal void CycleUnderwater() { int[] v = { 1, 2, 3, 5, 999 }; int i = Array.IndexOf(v, underwaterOxygenMultiplier); SetUnderwaterOxygen(v[(i + 1) % v.Length]); }
    internal void CycleCraftCost() { int[] v = { 0, 1, 2, 3, 4 }; int i = Array.IndexOf(v, craftCostMode); SetCraftCost(v[(i + 1) % v.Length]); }

    internal void ActivateBalancedPreset() {
        bool prev = suppressSaveSync;
        bool prevCfg = _suppressConfigSave;
        suppressSaveSync = true;
        _suppressConfigSave = true;
        try {
            SetDamageTakenMode(2); // 피해 50% 반감
            SetDamageDealtMultiplier(2.0f); // 공격력 2.0배
            SetPlayerStaminaMode(4); // 무한 기력(스태미나)
            SetCooldownMode(2); // 쿨타임 50% 단축
            SetExpMultiplier(2);
            SetSpeedMultiplier(1.5f); // 이동속도 1.5배 추천
            SetJumpMultiplier(2.0f); // 점프 도약 2배 추천
            SetRomanceMultiplier(2); // 호감도 2배
            SetTamingEase(2); // 조련 저항 반감
            SetMountSpeed(1.5f); // 탈것 속도 1.5배 추천
            SetMountStaminaMode(4); // 탈것 무한기력
            SetUnderwaterOxygen(3); // 수중호흡 3배
            SetUnderwaterSpeed(1.0f); // 수영 속도 1.0배 (순정 물리 보존)
            SetCraftCost(2); // 제작 반값
            SetCraftSuccess(2); // 대성공 2배
            SetDurabilityMode(4); // 무한 내구도
            SetGearHiderMode(1); // 헬멧 투명 숨김
            SetWeaponImbueMode(4); // 흡혈+치명 각인
            SetThiefStealthMode(1);
            SetBagExpandSlots(60);
            SetSpecialConvenienceMode(2); // 낙하 피해 면제
        } finally {
            suppressSaveSync = prev;
            _suppressConfigSave = prevCfg;
        }
        SaveCheatConfig();
        PushCheatStateNow();
        UpdateTopMasterBar();
        RefreshHud();
        Log("★ [밸런스 몰입 추천세트 가동] 방어50% + 공2배 + 무한기력 + 도둑무죄 + 내구무한 활성화!");
    }

    internal void ActivateSuperPreset() {
        bool prev = suppressSaveSync;
        bool prevCfg = _suppressConfigSave;
        suppressSaveSync = true;
        _suppressConfigSave = true;
        try {
            SetDamageTakenMode(4); // 완전 무적
            SetDamageDealtMultiplier(999.0f); // 일격필살
            SetPlayerStaminaMode(4); // 무한기력
            SetCooldownMode(4); // 0초 노쿨
            SetExpMultiplier(5);
            SetSpeedMultiplier(2.0f);
            SetJumpMultiplier(4.0f);
            SetRomanceMultiplier(999);
            SetTamingEase(999);
            SetMountSpeed(3.0f);
            SetMountStaminaMode(4);
            SetUnderwaterOxygen(999);
            SetUnderwaterSpeed(2.0f);
            SetCraftCost(4);
            SetCraftSuccess(999);
            SetDurabilityMode(4);
            SetGearHiderMode(1);
            SetWeaponImbueMode(5);
            SetThiefStealthMode(2);
            SetBagExpandSlots(60);
            SetSpecialConvenienceMode(3);
        } finally {
            suppressSaveSync = prev;
            _suppressConfigSave = prevCfg;
        }
        SaveCheatConfig();
        PushCheatStateNow();
        UpdateTopMasterBar();
        RefreshHud();
        Log("◆ [초월 치트 풀세트 가동] 모든 제약 해제 및 올맥스 적용!");
    }

    internal void ResetAllToVanilla() {
        bool prev = suppressSaveSync;
        bool prevCfg = _suppressConfigSave;
        suppressSaveSync = true;
        _suppressConfigSave = true;
        try {
            lastInjectedGoldAmount = 0;
            SetThiefStealthMode(0);
            SetThiefDetectionMode(0);
            SetPickpocketSuccessMode(0);
            SetBountyImmunityMode(0);
            SetThiefGloveFenceMode(0);
            SetBagExpandSlots(0);
            SetSpecialConvenienceMode(0);
            SetDamageTakenMode(0);
            SetDamageDealtMultiplier(1.0f);
            SetPlayerStaminaMode(0);
            SetCooldownMode(0);
            SetExpMultiplier(1);
            SetSpeedMultiplier(1.0f);
            SetJumpMultiplier(1.0f);
            SetRomanceMultiplier(1);
            SetTamingEase(1);
            SetMountSpeed(1.0f);
            SetMountStaminaMode(0);
            SetUnderwaterOxygen(1);
            SetUnderwaterSpeed(1.0f);
            SetCraftCost(0);
            SetCraftSuccess(1);
            SetDurabilityMode(0);
            SetGearHiderMode(0);
            SetWeaponImbueMode(0);
        } finally {
            suppressSaveSync = prev;
            _suppressConfigSave = prevCfg;
        }
        SaveCheatConfig();
        PushCheatStateNow();
        UpdateTopMasterBar();
        RefreshHud();
        Log("↺ [바닐라 순정 복귀] 모든 배율과 난이도가 오리지널 기본 상태로 리셋되었습니다.");
    }

    internal void ActivateAllLifeCheats() {
        bool prev = suppressSaveSync;
        bool prevCfg = _suppressConfigSave;
        suppressSaveSync = true;
        _suppressConfigSave = true;
        try {
            SetRomanceMultiplier(999);
            SetTamingEase(999);
            SetMountSpeed(3.0f);
            SetMountStaminaMode(4);
            SetCraftCost(4);
            SetCraftSuccess(999);
            SetDurabilityMode(4);
        } finally {
            suppressSaveSync = prev;
            _suppressConfigSave = prevCfg;
        }
        SaveCheatConfig();
        PushCheatStateNow();
        UpdateTopMasterBar();
        RefreshHud();
        Log("♥ [생활·연애·테이밍·장비제작 올인원 ON] 모든 생활 및 제작 치트가 활성화되었습니다!");
    }

    internal void ToggleLifeCheat(string key) {
        if (key == "romance") CycleRomance();
        else if (key == "taming") CycleTaming();
        else if (key == "nomat") CycleCraftCost();
        RefreshHud();
    }

    void BuildGuideTab(TabPage page) {
        TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        page.Controls.Add(grid);

        Panel leftCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(4), Padding = new Padding(14) };
        int y = 12;
        AddSectionTitle(leftCard, "▸ 인게임 자동 매크로 [마우스 클릭 / Ctrl+Alt+F8~F10]", ref y);

        AddToggleRow(leftCard, "autotalk", "대화/컷씬 고속 스킵 (E/Space 연타)", "Ctrl+Alt+F8", ref y, delegate { ToggleAutoTalk(!isAutoTalk); });
        AddToggleRow(leftCard, "autosprint", "퀘스트 방향 자동 전력 질주 (W+Shift)", "Ctrl+Alt+F9", ref y, delegate { ToggleAutoSprint(!isAutoSprint); });
        AddToggleRow(leftCard, "autoobserve", "'보고 배우기(Ctrl)' 흡수 & 자동 루팅", "Ctrl+Alt+F10", ref y, delegate { ToggleAutoObserve(!isAutoObserve); });

        y += 6;
        AddSectionTitle(leftCard, "◆ 인게임 HUD 오버레이 & 모드 메뉴 단축키 안내", ref y);

        Button btnPrevWalk = MakeButton("◀ 공략 이전 단계 (PgUp)", 242, 36, CARD_ALT, ACCENT_GOLD, delegate {
            if (inGameHud != null) inGameHud.CycleWalkthrough(-1);
        });
        btnPrevWalk.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnPrevWalk.Location = new Point(14, y);

        Button btnNextWalk = MakeButton("공략 다음 단계 (PgDn) ▶", 242, 36, CARD_ALT, ACCENT_GOLD, delegate {
            if (inGameHud != null) inGameHud.CycleWalkthrough(1);
        });
        btnNextWalk.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnNextWalk.Location = new Point(268, y);
        leftCard.Controls.Add(btnPrevWalk);
        leftCard.Controls.Add(btnNextWalk);
        y += 44;

        Button btnOpenHtml = MakeButton("▸ 브라우저용 대화면 HUD 공략집 열기 (체크리스트 내장)", 496, 38, ACCENT_RED, Color.White, delegate {
            string htmlGuide = Path.Combine(root, @"tuning\research\CrimsonGuide.html");
            if (File.Exists(htmlGuide)) Process.Start(new ProcessStartInfo(htmlGuide) { UseShellExecute = true });
            else MessageBox.Show(this, "CrimsonGuide.html 파일을 찾지 못했습니다.");
        });
        btnOpenHtml.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnOpenHtml.Location = new Point(14, y);
        leftCard.Controls.Add(btnOpenHtml);
        y += 44;

        Button btnRunAiAutoPlay = MakeButton("★ [정규 엔진] AI 실가동 게임 자동화 플레이 & 혹사 테스트 가동", 496, 40, Color.FromArgb(39, 174, 96), Color.White, delegate {
            if (!PromptSelfTestTriggerDialog()) {
                Log("■ [자가 테스트 보류] '자가 테스트 실시' 승인 입력이 취소되었거나 일치하지 않습니다.");
                return;
            }
            string autoScript = Path.Combine(root, @"tuning\CrimsonAutoPlayEngine.py");
            string flagFile = Path.Combine(root, @"tuning\stop_autoplay.flag");
            try { if (File.Exists(flagFile)) File.Delete(flagFile); } catch { }
            if (File.Exists(autoScript)) {
                Process.Start(new ProcessStartInfo("python", "\"" + autoScript + "\" --trigger-phrase \"" + REQUIRED_SELF_TEST_TRIGGER + "\" --mode hud-all-tabs-experiment --duration-min 8 --keep-launcher --auto-rsi") {
                    WorkingDirectory = Path.Combine(root, "tuning"),
                    UseShellExecute = true
                });
                Log("▶ [승인된 AI 자가 테스트 가동] 전 탭 버튼 검증 + 스마트 자율 플레이 시작! (중단: F12 / Pause 또는 물리 키 입력)");
            } else {
                MessageBox.Show(this, "CrimsonAutoPlayEngine.py 파일을 찾지 못했습니다.");
            }
        });
        btnRunAiAutoPlay.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnRunAiAutoPlay.Location = new Point(14, y);
        leftCard.Controls.Add(btnRunAiAutoPlay);
        y += 46;

        Button btnStopAiAutoPlay = MakeButton("■ [긴급 중단] AI 자동화 테스트 즉시 강제 종료 (F12 / Pause)", 496, 38, ACCENT_RED, Color.White, delegate {
            StopAiAutoPlay();
        });
        btnStopAiAutoPlay.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnStopAiAutoPlay.Location = new Point(14, y);
        leftCard.Controls.Add(btnStopAiAutoPlay);
        y += 44;

        Label lblShortcutTip = new Label {
            Text = "◆ [3~5번 인게임 HUD 완벽 통합 사용법]\r\n" +
                   "• 평소에는 화면 상단에 [투명 클릭통과 HUD 바]가 떠서 시야 방해나 마우스 간섭 없이(100% 통과) 치트/배율/공략을 보여줍니다.\r\n" +
                   "• 게임 도중 [INSERT] 키를 누르면 인게임 화면 위에 [Trinity 스타일 모드 메뉴]가 펼쳐져 알트탭 없이 마우스로 치트와 모드를 직접 클릭할 수 있습니다!\r\n" +
                   "• [PgUp / PgDn] 키로 인게임 상단 공략 가이드를 실시간 전환하세요.",
            Width = 496, Height = 135, Location = new Point(14, y), ForeColor = ACCENT_GOLD, Font = new Font("Malgun Gothic", 8.4f)
        };
        leftCard.Controls.Add(lblShortcutTip);

        grid.Controls.Add(leftCard, 0, 0);

        Panel rightCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(4), Padding = new Padding(14) };
        int ry = 12;
        AddSectionTitle(rightCard, "◆ 인게임 핵심 진행 루트 & 보스 공략 요약", ref ry);

        Panel pnlGuide = new Panel { Location = new Point(14, ry), Width = 510, Height = 505, BackColor = Color.FromArgb(16, 18, 26), Padding = new Padding(12) };
        Label rtbGuide = new Label {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(16, 18, 26),
            ForeColor = TEXT_MAIN,
            Font = new Font("Malgun Gothic", 9.0f),
            Text =
                "■ [1단계] 초반 빠른 정착 & 이동 루트\r\n" +
                "  • 귀환석 해금: 각 지역 거점 비석을 최우선 활성화하여 패스트트래블 열기\r\n" +
                "  • 야생마 길들이기: [생활 치트 -> 동물 길들이기 100%]를 켜고 야생마 포획 후 [탈것 속도 x3]으로 대륙 횡단\r\n\r\n" +
                "■ [2단계] 전투 & '보고 배우기(스킬 강탈)' 핵심 팁\r\n" +
                "  • 적 보스나 정예병이 특수 기술을 쓸 때 [Ctrl] 키(또는 관찰 매크로)로 관찰하면 즉시 내 스킬로 습득!\r\n" +
                "  • 난전 시 [방어 치트] + [무한 기력] + [◈ 뇌전/흡혈 기술 부여]를 켜면 막힘없는 호쾌한 액션 가능\r\n\r\n" +
                "■ [3단계] 동료 구출 미션 (화염 지대 돌파)\r\n" +
                "  • 불길에 쓰러진 동료 3명에게 접근해 [E] 키로 부축 후 안전 지대로 이동\r\n" +
                "  • 적 매복조 3그룹은 [공격력 배율 치트] 또는 [점프 x4 + 공중 강공격]으로 즉시 제압\r\n\r\n" +
                "■ [4단계] 연애 · 생활 · 전설 장비 제작\r\n" +
                "  • 주요 거점 NPC에게 선물/대화 전 [♥ 호감도 100% MAX]를 켜면 즉시 특수 인연 이벤트 달성\r\n" +
                "  • 대장간에서 [▲ 무재료 제작 + 대성공 100%]를 켜고 최고 등급 무기와 방어구 세트를 즉시 완성!\r\n\r\n" +
                "■ [5단계] 유저 커스텀 모드 & 흑성룡(Black Star Dragon) 제압\r\n" +
                "  • [6. 유저 커스텀 모드] 탭에서 드래곤 무제한 탑승 오버레이(0036)와 세이브 지식팩을 원클릭 적용하세요!"
        };
        pnlGuide.Controls.Add(rtbGuide);
        rightCard.Controls.Add(pnlGuide);

        grid.Controls.Add(rightCard, 1, 0);
    }

    void BuildCustomModsTab(TabPage page) {
        TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        page.Controls.Add(grid);

        // Left: Engine Data Overlay Mods (0036/) + Save File Enhancements
        Panel leftCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(4), Padding = new Padding(14) };
        int y = 12;

        AddSectionTitle(leftCard, "▶ 비파괴 엔진 오버레이 모드 (0036/ · 원본 손상 0%)", ref y);
        lblOverlayStatus = new Label {
            Text = IsOverlayActive() ? "● 현재 0036 커스텀 모드 오버레이 활성화됨 (meta/0.papgt 연동)" : "○ 현재 순정 상태 (오버레이 미적용)",
            ForeColor = IsOverlayActive() ? ACCENT_GREEN : ACCENT_GOLD,
            Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold),
            AutoSize = false,
            Width = 496,
            Height = 20,
            Location = new Point(14, y)
        };
        leftCard.Controls.Add(lblOverlayStatus);
        y += 24;

        Button btnModAll = MakeButton("★ 추천 엔진 커스텀 모드 4종 통합 패키지 원클릭 이식 (0036/)", 496, 40, ACCENT_RED, Color.White, delegate {
            Guard();
            RunModEngineCommand("mod-all-pack", "추천 엔진 모드 4종 통합 오버레이(0036) 패치");
        });
        btnModAll.Font = new Font("Malgun Gothic", 8.8f, FontStyle.Bold);
        btnModAll.Location = new Point(14, y);
        leftCard.Controls.Add(btnModAll);
        y += 46;

        Button btnModDragon = MakeButton("◆ 흑성룡 무제한 탑승·전지역 소환", 242, 36, CARD_ALT, TEXT_MAIN, delegate {
            Guard(); RunModEngineCommand("mod-dragon-unl", "드래곤 탑승 무제한 & 소환 1초 모드");
        });
        btnModDragon.Font = new Font("Malgun Gothic", 8.4f, FontStyle.Bold);
        btnModDragon.Location = new Point(14, y);

        Button btnModVeh = MakeButton("▸ 탈것 34종 재소환 쿨타임 0초", 242, 36, CARD_ALT, TEXT_MAIN, delegate {
            Guard(); RunModEngineCommand("mod-vehicle-nocd", "탈것 34종 쿨타임 0초 모드");
        });
        btnModVeh.Font = new Font("Malgun Gothic", 8.4f, FontStyle.Bold);
        btnModVeh.Location = new Point(268, y);
        leftCard.Controls.Add(btnModDragon);
        leftCard.Controls.Add(btnModVeh);
        y += 42;

        Button btnModStorage = MakeButton("■ 창고·야영지 900칸 + 가방 700칸", 242, 36, CARD_ALT, TEXT_MAIN, delegate {
            Guard(); RunModEngineCommand("mod-storage-900", "창고 900칸 & 가방 확장 모드");
        });
        btnModStorage.Font = new Font("Malgun Gothic", 8.4f, FontStyle.Bold);
        btnModStorage.Location = new Point(14, y);

        Button btnModStack = MakeButton("◆ 아이템 최대 중첩 9,999개 확장", 242, 36, CARD_ALT, TEXT_MAIN, delegate {
            Guard(); RunModEngineCommand("mod-stack-9999", "아이템 중첩 9,999개 확장 모드");
        });
        btnModStack.Font = new Font("Malgun Gothic", 8.4f, FontStyle.Bold);
        btnModStack.Location = new Point(268, y);
        leftCard.Controls.Add(btnModStorage);
        leftCard.Controls.Add(btnModStack);
        y += 42;

        Button btnModUninstall = MakeButton("◀ 0036 엔진 모드 오버레이 제거 (순정 복구)", 496, 34, CARD_ALT, TEXT_MUTED, delegate {
            Guard(); RunModEngineCommand("mod-uninstall", "엔진 오버레이 제거 및 순정 복구");
        });
        btnModUninstall.Font = new Font("Malgun Gothic", 8.6f, FontStyle.Bold);
        btnModUninstall.Location = new Point(14, y);
        leftCard.Controls.Add(btnModUninstall);
        y += 42;

        // Save File Editor Section
        AddSectionTitle(leftCard, "■ 세이브 에디터 & 커뮤니티 지식팩 주입 (slot0~slot2)", ref y);

        Button btnSaveGear = MakeButton("⚔ 장착 장비 내구도(65535)·예리도(100)·강화(+15) & 소비템 999개", 496, 38, ACCENT_PURPLE, Color.White, delegate {
            RunModEngineCommand("save-gear-max", "전 슬롯 세이브 장비 MAX & 아이템 999개 패치");
        });
        btnSaveGear.Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold);
        btnSaveGear.Location = new Point(14, y);
        leftCard.Controls.Add(btnSaveGear);
        y += 44;

        Button btnSaveKnowAll = MakeButton("★ 커뮤니티 지식팩 5종 올인원 주입 (원소·지도·흑성룡·염색·어비스)", 496, 38, ACCENT_BLUE, Color.White, delegate {
            RunModEngineCommand("save-knowledge-all", "커뮤니티 지식팩 5종 일괄 주입");
        });
        btnSaveKnowAll.Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold);
        btnSaveKnowAll.Location = new Point(14, y);
        leftCard.Controls.Add(btnSaveKnowAll);
        y += 44;

        Button btnSaveBag = MakeButton("■ 가방 700칸 대확장", 160, 36, CARD_ALT, ACCENT_GOLD, delegate {
            RunModEngineCommand("save-bag-max", "세이브 가방 700칸 대확장 (Nexus #56)");
        });
        btnSaveBag.Font = new Font("Malgun Gothic", 8.4f, FontStyle.Bold);
        btnSaveBag.Location = new Point(14, y);

        Button btnSaveStatus = MakeButton("▸ 세이브 정밀 진단", 160, 36, CARD_ALT, TEXT_MAIN, delegate {
            RunModEngineCommand("save-status", "세이브 슬롯 상태 진단");
        });
        btnSaveStatus.Font = new Font("Malgun Gothic", 8.4f, FontStyle.Bold);
        btnSaveStatus.Location = new Point(182, y);

        Button btnSaveRestore = MakeButton("◀ 세이브 원본 복원", 160, 36, CARD_ALT, TEXT_MUTED, delegate {
            RunModEngineCommand("save-restore", "세이브 원본(.original.bak) 복원");
        });
        btnSaveRestore.Font = new Font("Malgun Gothic", 8.4f, FontStyle.Bold);
        btnSaveRestore.Location = new Point(350, y);
        leftCard.Controls.Add(btnSaveBag);
        leftCard.Controls.Add(btnSaveStatus);
        leftCard.Controls.Add(btnSaveRestore);

        grid.Controls.Add(leftCard, 0, 0);

        // Right: Mod Console & Curated Mod Descriptions
        Panel rightCard = new Panel { Dock = DockStyle.Fill, BackColor = CARD_BG, Margin = new Padding(4), Padding = new Padding(14) };
        int ry = 12;
        AddSectionTitle(rightCard, "▸ 유저 커스텀 모드 & 세이브 패치 실행 결과", ref ry);

        Panel pnlModConsole = new Panel { Location = new Point(14, ry), Width = 510, Height = 505, BackColor = Color.FromArgb(14, 16, 22), Padding = new Padding(12) };
        txtModConsole = new Label {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(14, 16, 22),
            ForeColor = Color.FromArgb(180, 240, 200),
            Font = new Font("Malgun Gothic", 8.8f),
            Text =
                "=== [선별 이식된 글로벌 커뮤니티 모드 & 세이브 에디터 목록] ===\r\n\r\n" +
                "1. [엔진 오버레이 0036/ 모드 (원본 0008/0.paz 손상 0%)]\r\n" +
                "  • 흑성룡(Dragon) 무제한 탑승: 탑승 제한시간 999,999초 & 전 지역 자유 탑승\r\n" +
                "  • 탈것 34종 쿨타임 0초: 말·전투곰·와이번 등 사망/재소환 대기시간 즉시 해제\r\n" +
                "  • 창고/가방 대확장: 야영지/창고/은행 900칸 & 캐릭터 가방 최대 700칸 (Nexus #56 검증)\r\n" +
                "  • 아이템 중첩 9,999개: 모든 소비/재료 아이템 최대 스택 9,999개로 확장\r\n\r\n" +
                "2. [ChaCha20 + HMAC-SHA256 + LZ4 세이브 에디터 패치]\r\n" +
                "  • 장착 장비 MAX: 내구도 65,535(무한) + 예리도 100 + 강화 +15 자동 패치\r\n" +
                "  • 커뮤니티 지식팩(Knowledge) 5종:\r\n" +
                "    - Elemental Powers (원소 마법/스킬 144종 해금)\r\n" +
                "    - Reveal Nodes & Waypoints (파이웰 전역 지도/거점 390곳 개방)\r\n" +
                "    - Black Star Dragon MoveSet (흑성룡 브레스/공중기 해금)\r\n" +
                "    - Unlock All Dyes (모든 염색약 팔레트 119종 해금)\r\n" +
                "    - Abyss Gate Unlock (어비스 게이트 기술 해금)\r\n\r\n" +
                "※ 모든 작업 전 원본 파일(.bak / .original.bak)이 자동 백업됩니다."
        };
        pnlModConsole.Controls.Add(txtModConsole);
        rightCard.Controls.Add(pnlModConsole);

        grid.Controls.Add(rightCard, 1, 0);
    }

    internal void RunModEngineCommand(string cmdArg, string friendlyTitle) {
        if (!File.Exists(ModScript)) {
            Log("오류: Apply-CommunityMods.py 스크립트를 찾을 수 없습니다.");
            return;
        }
        Log("◈ [" + friendlyTitle + "] 실행 중...");
        new Thread(delegate() {
            try {
                ProcessStartInfo psi = new ProcessStartInfo("python", "\"" + ModScript + "\" " + cmdArg) {
                    WorkingDirectory = Path.GetDirectoryName(ModScript),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi)) {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    string combined = (stdout + "\r\n" + stderr).Trim();
                    this.Invoke(new Action(delegate {
                        if (txtModConsole != null) {
                            txtModConsole.Text = "▶ [" + friendlyTitle + " 실행 결과]\r\n\r\n" + combined;
                        }
                        if (lblOverlayStatus != null) {
                            bool act = IsOverlayActive();
                            lblOverlayStatus.Text = act ? "● 현재 0036 커스텀 모드 오버레이 활성화됨 (meta/0.papgt 연동)" : "○ 현재 순정 상태 (오버레이 미적용)";
                            lblOverlayStatus.ForeColor = act ? ACCENT_GREEN : ACCENT_GOLD;
                        }
                        RefreshHud();
                        Log("✅ [" + friendlyTitle + "] 완료: " + (combined.Split('\n')[0]).Trim());
                    }));
                }
            } catch (Exception ex) {
                this.Invoke(new Action(delegate { Log("모드 실행 오류: " + ex.Message); }));
            }
        }) { IsBackground = true }.Start();
    }

    void BuildMiniOverlayPanel() {
        miniOverlayPanel = new Panel { Dock = DockStyle.Fill, BackColor = BG_DARK, Padding = new Padding(10), Visible = false };
        int y = 8;

        Label lblMiniTitle = new Label {
            Text = "⚡ CRIMSON MINI HUD (HOME: 복귀)",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = ACCENT_RED,
            AutoSize = true,
            Location = new Point(10, y)
        };
        Button btnExpand = MakeButton("▸ 큰 창으로 복귀", 120, 26, CARD_ALT, TEXT_MAIN, delegate { ToggleMiniOverlay(); });
        btnExpand.Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold);
        btnExpand.Location = new Point(235, y - 2);
        miniOverlayPanel.Controls.Add(lblMiniTitle);
        miniOverlayPanel.Controls.Add(btnExpand);
        y += 32;

        AddMiniButton("★ [추천] 밸런스 몰입 추천세트 가동", ref y, delegate { ActivateBalancedPreset(); });
        AddMiniButton("■ [Ctrl+Alt+F1] 방어/피해감소 순환 (정상~무적)", ref y, delegate { TriggerHotkeyAction(101); });
        AddMiniButton("⚡ [Ctrl+Alt+F2] 스태미나 모드 순환 (정상~무한)", ref y, delegate { TriggerHotkeyAction(102); });
        AddMiniButton("⚔ [Ctrl+Alt+F3] 공격력 배율 순환 (x1~일격)", ref y, delegate { TriggerHotkeyAction(103); });
        AddMiniButton("◆ [Ctrl+Alt+F4] 쿨타임 단축 순환 (정상~0초)", ref y, delegate { TriggerHotkeyAction(104); });
        AddMiniButton("★ 골드 +100,000 G 세이브 주입", ref y, delegate { AddGold(100000); });
        AddMiniButton("✨ [Ctrl+Alt+F5] 경험치 배율 순환 (x1~x10)", ref y, delegate { CycleExp(); });
        AddMiniButton("▸ [Ctrl+Alt+F6] 이동속도 배율 순환 (x1~x4)", ref y, delegate { CycleSpeed(); });
        AddMiniButton("♥ 연애 호감도 배율 순환 (x1~MAX)", ref y, delegate { CycleRomance(); });
        AddMiniButton("▸ 조련 난이도 완화 순환 (x1~즉시)", ref y, delegate { CycleTaming(); });
        AddMiniButton("● 수중 호흡/잠수 배율 순환 (x1~무한)", ref y, delegate { CycleUnderwater(); });
        AddMiniButton("▸ [Ctrl+Alt+F8] 대화/컷씬 자동스킵 토글", ref y, delegate { ToggleAutoTalk(!isAutoTalk); });

        Controls.Add(miniOverlayPanel);
    }

    void AddMiniButton(string text, ref int y, Action onClick) {
        Button b = MakeButton(text, 345, 32, CARD_BG, TEXT_MAIN, onClick);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Padding = new Padding(10, 0, 0, 0);
        b.Location = new Point(10, y);
        miniOverlayPanel.Controls.Add(b);
        y += 36;
    }

    void ToggleMiniOverlay() {
        isMiniMode = !isMiniMode;
        if (isMiniMode) {
            this.TopMost = true;
            this.Opacity = 0.90;
            mainContentPanel.Visible = false;
            miniOverlayPanel.Visible = true;
            miniOverlayPanel.BringToFront();
            this.ClientSize = new Size(370, 490);
            Rectangle screen = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(screen.Right - this.Width - 15, screen.Top + 115);
            btnOverlayToggle.Text = "▸ 일반 창 복귀 (HOME)";
            Log("◆ 미니 리모컨 모드 활성화 (항상 위 고정 · 투명도 90%)");
        } else {
            this.TopMost = false;
            this.Opacity = 1.0;
            miniOverlayPanel.Visible = false;
            mainContentPanel.Visible = true;
            mainContentPanel.SendToBack();
            this.ClientSize = new Size(1180, 900);
            this.CenterToScreen();
            btnOverlayToggle.Text = "◆ 미니 리모컨 (HOME)";
            Log("일반 마스터 대시보드 모드로 복귀했습니다.");
        }
    }

    void AddSectionTitle(Panel parent, string text, ref int y) {
        Label lbl = new Label {
            Text = text,
            Font = new Font("Malgun Gothic", 9.0f, FontStyle.Bold),
            ForeColor = ACCENT_RED,
            BackColor = Color.FromArgb(28, 32, 44),
            AutoSize = false,
            Width = 500,
            Height = 18,
            Location = new Point(8, y)
        };
        parent.Controls.Add(lbl);
        y += 20;
    }

    void AddToggleRow(Panel parent, string key, string title, string badge, ref int y, Action onClick) {
        Panel row = new Panel { Width = 496, Height = 38, BackColor = CARD_ALT, Location = new Point(14, y) };
        Label lblBadge = new Label { Text = "[" + badge + "]", ForeColor = ACCENT_GOLD, Font = new Font("Consolas", 8.6f, FontStyle.Bold), AutoSize = true, Location = new Point(8, 10) };
        Label lblTitle = new Label { Text = title, ForeColor = TEXT_MAIN, Font = new Font("Malgun Gothic", 8.5f, FontStyle.Bold), AutoSize = true, Location = new Point(118, 10) };
        Button btn = MakeButton("OFF", 76, 28, Color.FromArgb(55, 60, 75), TEXT_MUTED, onClick);
        btn.Location = new Point(412, 5);
        toggleButtons[key] = btn;
        row.Controls.Add(lblBadge);
        row.Controls.Add(lblTitle);
        row.Controls.Add(btn);
        parent.Controls.Add(row);
        y += 44;
    }

    void UpdateToggleUI(string key, bool state) {
        Button btn;
        if (toggleButtons.TryGetValue(key, out btn)) {
            btn.Text = state ? "ON ✓" : "OFF";
            btn.BackColor = state ? ACCENT_GREEN : Color.FromArgb(55, 60, 75);
            btn.ForeColor = state ? Color.Black : TEXT_MUTED;
        }
    }

    internal void AddGold(long amount) {
        lastInjectedGoldAmount = amount;
        long liveSil = SelfTestMode ? -1L : LiveMemoryCheatEngine.ReadExactSilverNow();
        long target;
        if (amount == 56511L) {
            target = 56511L;
        } else if (liveSil >= 0) {
            target = liveSil + amount;
        } else if (currentGoldBonus > 0 && currentGoldBonus != 9999999L && currentGoldBonus < 5000000000L) {
            target = currentGoldBonus + amount;
        } else {
            target = (SelfTestMode ? 0L : 187706L) + amount;
        }
        currentGoldBonus = target;
        if (!SelfTestMode) LiveMemoryCheatEngine.InjectGoldToMemory(target);
        if (lblGoldSaveStatus != null) {
            lblGoldSaveStatus.Text = string.Format("현재 실시간 은화: {0:N2} G ({1:N0} 은화)", target / 100.0, target);
        }
        if (!SelfTestMode) System.Media.SystemSounds.Asterisk.Play();
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
        Log(string.Format("★ [은화 주입] 목표: {0:N2}G ({1:N0} 은화) -> 게임 메모리 실시간 직결 반영 완료!", target / 100.0, target));
    }

    internal void SetExpMultiplier(int mult) {
        expMultiplier = Math.Max(1, mult);
        UpdateGroupSelection("exp", expMultiplier);
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
        Log(string.Format("★ [경험치 배율 변경] EXP 획득량 x{0}배 모드 적용 완료!", expMultiplier));
    }

    internal void SetSpeedMultiplier(float mult) {
        speedMultiplier = mult;
        UpdateGroupSelection("speed", mult);
        if (!SelfTestMode) LiveMemoryCheatEngine.SetSpeedMultiplier(mult);
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
        Log(string.Format("▲ [수평 이동속도] 이동 속도 x{0:0.##}배 (실시간 메모리 RVA+0x3BB05B7 X/Z 벡터 가속) 즉시 적용!", mult));
    }

    internal void SetJumpMultiplier(float mult) {
        jumpMultiplier = mult;
        UpdateGroupSelection("jump", mult);
        if (!SelfTestMode) LiveMemoryCheatEngine.SetJumpMultiplier(mult);
        PushCheatStateNow();
        SaveCheatConfig();
        RefreshHud();
        Log(string.Format("▲ [점프 도약력] 스페이스바 원샷 도약 x{0:0.##}배 (실시간 메모리 RVA+0x3BB05B7 1회성 수직 임펄스) 즉시 적용!", mult));
    }

    internal void SetJumpMultiplier(int mult) {
        SetJumpMultiplier((float)mult);
    }

    internal void CycleExp() {
        int[] vals = new[] { 1, 2, 3, 4, 5, 7, 10 };
        int idx = Array.IndexOf(vals, expMultiplier);
        SetExpMultiplier(vals[(idx + 1) % vals.Length]);
    }

    internal void CycleSpeed() {
        float[] vals = new[] { 1.0f, 1.15f, 1.3f, 1.5f, 2.0f, 3.0f };
        int idx = 0;
        for (int i = 0; i < vals.Length; i++) if (Math.Abs(vals[i] - speedMultiplier) < 0.05f) idx = i;
        SetSpeedMultiplier(vals[(idx + 1) % vals.Length]);
    }

    internal void CycleJump() {
        float[] vals = new[] { 1.0f, 1.5f, 2.0f, 3.0f, 4.0f, 5.0f };
        int idx = 0;
        for (int i = 0; i < vals.Length; i++) if (Math.Abs(vals[i] - jumpMultiplier) < 0.05f) idx = i;
        SetJumpMultiplier(vals[(idx + 1) % vals.Length]);
    }

    internal void ToggleAutoTalk(bool enable) {
        if (enable && !ConfirmAutoMacroEnable("대화/컷씬 자동 스킵(E/Space)")) {
            enable = false;
        }
        isAutoTalk = enable;
        UpdateToggleUI("autotalk", enable);
        RefreshHud();
        if (enable) {
            new Thread(delegate() {
                Log("▸ [매크로 ON] 대화/컷씬 고속 자동 스킵(E/Space) 시작 (ESC / F12 키로 즉시 중단)");
                while (isAutoTalk) {
                    if ((Win32Native.GetAsyncKeyState(0x7B) & 0x8000) != 0 || (Win32Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) {
                        isAutoTalk = false;
                        try { BeginInvoke((Action)delegate { UpdateToggleUI("autotalk", false); RefreshHud(); }); } catch { }
                        break;
                    }
                    try {
                        uint targetPid = cachedGamePid;
                        if (targetPid != 0) {
                            uint pid; Win32Native.GetWindowThreadProcessId(Win32Native.GetForegroundWindow(), out pid);
                            if (pid == targetPid) {
                                Win32Native.SendKey(0x45, 50);
                                Thread.Sleep(80);
                                if (!isAutoTalk) break;
                                Win32Native.SendKey(0x20, 50);
                            }
                        }
                    } catch { }
                    Thread.Sleep(110);
                }
                Log("▸ [매크로 OFF] 대화/컷씬 자동 스킵 종료");
            }) { IsBackground = true }.Start();
        }
    }

    internal void ToggleAutoSprint(bool enable) {
        if (enable && !ConfirmAutoMacroEnable("전방 자동 전력 질주(W+Shift)")) {
            enable = false;
        }
        isAutoSprint = enable;
        UpdateToggleUI("autosprint", enable);
        RefreshHud();
        if (enable) {
            new Thread(delegate() {
                Log("▸ [매크로 ON] 전방 자동 전력 질주(W+Shift) 시작 (ESC / F12 키로 즉시 중단)");
                try {
                    while (isAutoSprint) {
                        if ((Win32Native.GetAsyncKeyState(0x7B) & 0x8000) != 0 || (Win32Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) {
                            isAutoSprint = false;
                            try { BeginInvoke((Action)delegate { UpdateToggleUI("autosprint", false); RefreshHud(); }); } catch { }
                            break;
                        }
                        try {
                            uint targetPid = cachedGamePid;
                            if (targetPid != 0) {
                                uint pid; Win32Native.GetWindowThreadProcessId(Win32Native.GetForegroundWindow(), out pid);
                                if (pid == targetPid) {
                                    bool holding = false;
                                    try {
                                        Win32Native.HoldKey(0x10, true);
                                        Win32Native.HoldKey(0x57, true);
                                        holding = true;
                                        for (int slice = 0; slice < 9 && isAutoSprint; slice++) {
                                            if ((Win32Native.GetAsyncKeyState(0x7B) & 0x8000) != 0 || (Win32Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) {
                                                isAutoSprint = false;
                                                break;
                                            }
                                            Thread.Sleep(100);
                                        }
                                    } finally {
                                        if (holding) {
                                            Win32Native.HoldKey(0x57, false);
                                            Win32Native.HoldKey(0x10, false);
                                        }
                                    }
                                }
                            }
                        } catch { }
                        if (isAutoSprint) Thread.Sleep(180);
                    }
                } finally {
                    Win32Native.HoldKey(0x57, false);
                    Win32Native.HoldKey(0x10, false);
                }
                Log("▸ [매크로 OFF] 자동 전력 질주 종료");
            }) { IsBackground = true }.Start();
        }
    }

    internal void ToggleAutoObserve(bool enable) {
        if (enable && !ConfirmAutoMacroEnable("보고 배우기(Ctrl) 스킬 흡수")) {
            enable = false;
        }
        isAutoObserve = enable;
        UpdateToggleUI("autoobserve", enable);
        RefreshHud();
        if (enable) {
            new Thread(delegate() {
                Log("▸ [매크로 ON] '보고 배우기(Ctrl)' 스킬 흡수 & 자동 루팅 시작 (ESC / F12 키로 즉시 중단)");
                try {
                    while (isAutoObserve) {
                        if ((Win32Native.GetAsyncKeyState(0x7B) & 0x8000) != 0 || (Win32Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) {
                            isAutoObserve = false;
                            try { BeginInvoke((Action)delegate { UpdateToggleUI("autoobserve", false); RefreshHud(); }); } catch { }
                            break;
                        }
                        try {
                            uint targetPid = cachedGamePid;
                            if (targetPid != 0) {
                                uint pid; Win32Native.GetWindowThreadProcessId(Win32Native.GetForegroundWindow(), out pid);
                                if (pid == targetPid) {
                                    bool holding = false;
                                    try {
                                        Win32Native.HoldKey(0x11, true);
                                        holding = true;
                                        Win32Native.SendKey(0x45, 50);
                                        for (int slice = 0; slice < 6 && isAutoObserve; slice++) {
                                            if ((Win32Native.GetAsyncKeyState(0x7B) & 0x8000) != 0 || (Win32Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) {
                                                isAutoObserve = false;
                                                break;
                                            }
                                            Thread.Sleep(100);
                                        }
                                    } finally {
                                        if (holding) Win32Native.HoldKey(0x11, false);
                                    }
                                }
                            }
                        } catch { }
                        if (isAutoObserve) Thread.Sleep(200);
                    }
                } finally {
                    Win32Native.HoldKey(0x11, false);
                }
                Log("▸ [매크로 OFF] 스킬 흡수 & 자동 루팅 종료");
            }) { IsBackground = true }.Start();
        }
    }

    void VerifyIntegrityBeforeLaunch() {
        try {
            // 1. Remove any legacy external 0036/ overlay while preserving native 0008/0.paz transplantation
            string mod36 = Path.Combine(root, "0036");
            if (Directory.Exists(mod36)) Directory.Delete(mod36, true);

            // 2. Preserve evolved RSI graphics XML; restore master profile only if missing or invalid
            bool validConfig = false;
            if (File.Exists(config)) {
                try { ConfigStore.Validate(config); validConfig = true; } catch { validConfig = false; }
            }
            if (validConfig) {
                SyncClarityFromConfigXml();
            } else {
                string masterXml = Path.Combine(ProfileDir, "00-장시간무병목-고화질마스터-GTX1070최적.xml");
                if (File.Exists(masterXml)) {
                    ConfigStore.RestoreGraphics(masterXml, config, BackupDir);
                    SyncClarityFromConfigXml();
                }
            }
        } catch { }
    }

    void LaunchGame() {
        if (Running()) {
            Log("⚠️ 게임 프로세스가 이미 실행 중입니다. (PID: " + cachedGamePid + ")");
            MessageBox.Show(this, "게임 프로세스가 이미 실행 중입니다. (PID: " + cachedGamePid + ")\n이미 실행 중인 게임 창으로 전환해주세요.", "실행 알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        VerifyIntegrityBeforeLaunch();
        string exe = Path.Combine(root, @"bin64\CrimsonDesert.exe");
        if (!File.Exists(exe)) {
            Log("❌ 게임 실행 파일을 찾지 못했습니다: " + exe);
            MessageBox.Show(this, "게임 실행 파일을 찾지 못했습니다.\n경로: " + exe, "실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        try {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true, Verb = "runas" });
        Log("▶ 무결성 사전 검증(PAPGT·XML·세이브) 완료 후 붉은사막(CrimsonDesert.exe) 실행! 스텔스 HUD와 세션 부스터가 자동 연동됩니다.");
        } catch (Exception ex) {
            Log("❌ 게임 실행 중 오류 발생: " + ex.Message);
            MessageBox.Show(this, "게임 실행 중 오류가 발생했습니다:\n" + ex.Message, "실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void RefreshProfiles() {
        profiles.Items.Clear();
        if (Directory.Exists(ProfileDir)) {
            var files = Directory.GetFiles(ProfileDir, "*.xml");
            Array.Sort(files);
            foreach (var f in files) profiles.Items.Add(Path.GetFileName(f));
        }
        if (profiles.Items.Count > 0) profiles.SelectedIndex = 0;
    }

    void RefreshRuntime() {
        bool isRun = Running();
        lblRuntimeBadge.Text = isRun ? "● 게임 실행 중 (HUD 연동)" : "○ 게임 프로세스 대기 중";
        lblRuntimeBadge.ForeColor = isRun ? ACCENT_GREEN : ACCENT_GOLD;

        // Auto-detect game termination -> trigger zero-overhead Post-Session RSI Evolution Loop
        if (lastRunState && !isRun) {
            if (ShouldPreserveXmlForRecentSelfTest()) {
                Log("▶ [RSI 엔진 보호] '자가 테스트 실시' 시연 종료 감지 — 사용자 고화질 XML 보존 모드로 분석을 위임합니다.");
            } else {
                Log("▶ [RSI 엔진 감지] 게임 플레이 세션 종료 확인! 유휴 자원을 활용하여 재귀적 자가 개선(RSI) 분석 및 최적화를 자동 수행합니다...");
                RunRsiEngineCommand("--evolve", "게임 종료 후 RSI 자율 세대 진화");
            }
        }
        lastRunState = isRun;

        try {
            boost.Enabled = boostChoice.Checked;
            lblBoostStatus.Text = boost.Refresh(Path.Combine(root, @"bin64\CrimsonDesert.exe"));
        } catch (Exception ex) {
            lblBoostStatus.Text = "부스터 오류: " + ex.Message;
            boost.Enabled = false;
        }
        UpdateRsiStatus();
        RefreshHud();
    }

    void UpdateRsiStatus() {
        if (lblRsiStatus == null) return;
        try {
            string stateFile = Path.Combine(root, @"tuning\research\rsi_telemetry\rsi_state.json");
            if (File.Exists(stateFile)) {
                DateTime mtime = File.GetLastWriteTimeUtc(stateFile);
                if (_cachedRsiLabel != null && mtime == _lastRsiStateMtime) {
                    if (lblRsiStatus.Text != _cachedRsiLabel) lblRsiStatus.Text = _cachedRsiLabel;
                    lblRsiStatus.ForeColor = _cachedRsiColor;
                    return;
                }
                _lastRsiStateMtime = mtime;
                string json = File.ReadAllText(stateFile, Encoding.UTF8);
                var genMatch = System.Text.RegularExpressions.Regex.Match(json, "\"current_generation\"\\s*:\\s*(\\d+)");
                var armMatch = System.Text.RegularExpressions.Regex.Match(json, "\"active_arm\"\\s*:\\s*\"([^\"]+)\"");
                var scaleMatch = System.Text.RegularExpressions.Regex.Match(json, "\"scale_display\"\\s*:\\s*\"([^\"]+)\"");
                var texMatch = System.Text.RegularExpressions.Regex.Match(json, "\"texture\"\\s*:\\s*\"([^\"]+)\"");
                var rewardMatch = System.Text.RegularExpressions.Regex.Match(json, "\"last_reward_score\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)");
                var fpsMatch = System.Text.RegularExpressions.Regex.Match(json, "\"last_measured_fps\"\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)");
                if (genMatch.Success && scaleMatch.Success) {
                    string armTxt = armMatch.Success ? armMatch.Groups[1].Value : ("FSR " + scaleMatch.Groups[1].Value);
                    string texTxt = texMatch.Success ? texMatch.Groups[1].Value : "Medium";
                    string tail = (fpsMatch.Success && rewardMatch.Success)
                        ? string.Format("실측 {0} FPS · 보상 {1}점", fpsMatch.Groups[1].Value, rewardMatch.Groups[1].Value)
                        : "자율 밴딧 최적화 가동";
                    _cachedRsiLabel = string.Format("RSI Gen #{0} | {1} ({2}/Tex:{3}) | {4}",
                        genMatch.Groups[1].Value, armTxt, scaleMatch.Groups[1].Value, texTxt, tail);
                    _cachedRsiColor = ACCENT_GREEN;
                    lblRsiStatus.Text = _cachedRsiLabel;
                    lblRsiStatus.ForeColor = _cachedRsiColor;
                    return;
                }
            }
        } catch { }
        _cachedRsiLabel = "RSI 상태: 감시 준비 완료 · 게임 세션 누적 시 최적화 자율 진화";
        _cachedRsiColor = ACCENT_GOLD;
        lblRsiStatus.Text = _cachedRsiLabel;
        lblRsiStatus.ForeColor = _cachedRsiColor;
    }

    internal void RunRsiEngineCommand(string cmdArg, string friendlyTitle) {
        if (!File.Exists(RsiScript)) {
            Log("오류: RsiEngine.py 스크립트를 찾을 수 없습니다.");
            return;
        }
        Log("↺ [" + friendlyTitle + "] 시작...");
        new Thread(delegate() {
            try {
                ProcessStartInfo psi = new ProcessStartInfo("python", "\"" + RsiScript + "\" " + cmdArg) {
                    WorkingDirectory = Path.GetDirectoryName(RsiScript),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi)) {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    string combined = (stdout + "\r\n" + stderr).Trim();
                    this.Invoke(new Action(delegate {
                        RefreshProfiles();
                        UpdateRsiStatus();
                        Log("✅ [" + friendlyTitle + "] 완료:\r\n" + combined);
                    }));
                }
            } catch (Exception ex) {
                this.Invoke(new Action(delegate { Log("RSI 실행 오류: " + ex.Message); }));
            }
        }) { IsBackground = true }.Start();
    }

    void Guard() { if (Running()) throw new InvalidOperationException("게임을 종료한 후 그래픽 설정이나 엔진 오버레이 모드를 변경해 주세요."); }
    void Safe(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "알림", MessageBoxButtons.OK, MessageBoxIcon.Information); } }

    Button MakeButton(string text, int w, int h, Color bg, Color fg, Action onClick) {
        var b = new Button {
            Text = text, Width = w, Height = h, BackColor = bg, ForeColor = fg,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Malgun Gothic", 9f, FontStyle.Bold)
        };
        b.UseVisualStyleBackColor = false;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(60, 68, 88);
        b.Click += delegate { Safe(onClick); };
        return b;
    }

    internal void Log(string msg) {
        if (txtLog == null) return;
        string line = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), msg);
        Action updateLog = delegate {
            recentLogLines.Add(line);
            if (recentLogLines.Count > 3) recentLogLines.RemoveAt(0);
            txtLog.Text = string.Join("\r\n", recentLogLines.ToArray());
        };
        if (txtLog.InvokeRequired) {
            txtLog.Invoke(updateLog);
        } else {
            updateLog();
        }
    }

    void SyncFlingHotkeyYield(bool extRunning) {
        if (SelfTestMode || !this.IsHandleCreated) return;
        if (extRunning && !_flingHotkeysYielded) {
            _flingHotkeysYielded = true;
            Log("⚡ [외부 FLiNG 트레이너 감지] Num 0~9 단축키 및 내부 훅을 외부 트레이너에 안전 양보했습니다.");
        } else if (!extRunning && _flingHotkeysYielded) {
            _flingHotkeysYielded = false;
            Log("⚡ [외부 FLiNG 트레이너 종료] Num 0~9 단축키 및 내부 메모리 훅 제어권을 복구했습니다.");
        }
    }

    void RegisterHotkeys() {
        if (SelfTestMode || !this.IsHandleCreated) return;
        try {
            // Bare Num 0~9 and Insert are foreground-gated inside InGameHudOverlayForm.PollSensors().
            // Auto-macro hotkeys (F8..F10) and bare Pause are NOT globally registered so manual gameplay is never interrupted.
            _flingHotkeysYielded = LiveMemoryCheatEngine.IsExternalFlingTrainerRunning();
            uint modCtrlAlt = 0x0003;
            for (int i = 0; i < 7; i++) Win32Native.RegisterHotKey(this.Handle, 101 + i, modCtrlAlt, (uint)(Keys.F1 + i));
            Win32Native.RegisterHotKey(this.Handle, 111, modCtrlAlt, (uint)Keys.F11);
            Win32Native.RegisterHotKey(this.Handle, 200, 0x0002, (uint)Keys.Home);     // Ctrl + Home (Mini remote)
            Win32Native.RegisterHotKey(this.Handle, 202, 0x0002, (uint)Keys.PageUp);   // Ctrl + PgUp
            Win32Native.RegisterHotKey(this.Handle, 203, 0x0002, (uint)Keys.PageDown); // Ctrl + PgDn
            Win32Native.RegisterHotKey(this.Handle, 204, 0x0002, 0xC0);               // Ctrl + ` / ~
            Win32Native.RegisterHotKey(this.Handle, 112, 0x0003, (uint)Keys.F12);      // Ctrl+Alt+F12: Emergency Stop AutoPlay
        } catch { }
    }

    void UnregisterHotkeys() {
        for (int id = 101; id <= 113; id++) Win32Native.UnregisterHotKey(this.Handle, id);
        for (int id = 200; id <= 204; id++) Win32Native.UnregisterHotKey(this.Handle, id);
        for (int id = 301; id <= 320; id++) Win32Native.UnregisterHotKey(this.Handle, id);
    }

    internal void TriggerHotkeyAction(int id) {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime prevUtc;
        if (_lastHotkeyUtc.TryGetValue(id, out prevUtc) && (nowUtc - prevUtc).TotalMilliseconds < 160.0) {
            return;
        }
        _lastHotkeyUtc[id] = nowUtc;
        if (id < 900 || id > 919) System.Media.SystemSounds.Beep.Play();
        switch (id) {
            case 301: case 311:
                SetDamageTakenMode(damageTakenMode >= 4 ? 0 : 4);
                Log(damageTakenMode >= 4 ? "■ [Num 1 / Ctrl+1] FLiNG 무적(God Mode) ON" : "■ [Num 1 / Ctrl+1] 무적(God Mode) OFF");
                break;
            case 302: case 312:
                SetPlayerStaminaMode(playerStaminaMode >= 1 ? 0 : 4);
                Log(playerStaminaMode >= 1 ? "⚡ [Num 2 / Ctrl+2] 무한 기력(Infinite Stamina) ON" : "⚡ [Num 2 / Ctrl+2] 무한 기력 OFF");
                break;
            case 303: case 313:
                SetCooldownMode(cooldownMode >= 1 ? 0 : 4);
                Log(cooldownMode >= 1 ? "◈ [Num 3 / Ctrl+3] 정기/마나/쿨타임 무제한 ON" : "◈ [Num 3 / Ctrl+3] 정기/쿨타임 OFF");
                break;
            case 304: case 314:
                SetDamageTakenMode(damageTakenMode == 2 ? 0 : 2);
                Log(damageTakenMode == 2 ? "■ [Num 4 / Ctrl+4] 슈퍼 방어력(1000배) ON" : "■ [Num 4 / Ctrl+4] 슈퍼 방어력 OFF");
                break;
            case 305: case 315:
                SetDamageDealtMultiplier(damageDealtMultiplier >= 10.0f ? 1.0f : 999.0f);
                Log(damageDealtMultiplier >= 10.0f ? "⚔ [Num 5 / Ctrl+5] 일격필살(10억 데미지) ON" : "⚔ [Num 5 / Ctrl+5] 공격력 순정 복구");
                break;
            case 306: case 316:
                SetMountStaminaMode(mountStaminaMode >= 1 ? 0 : 4);
                Log(mountStaminaMode >= 1 ? "▸ [Num 6 / Ctrl+6] 탈것 무한 기력 ON" : "▸ [Num 6 / Ctrl+6] 탈것 기력 OFF");
                break;
            case 307: case 317:
                AddGold(100000);
                Log("★ [Num 7 / Ctrl+7] 은화 +100,000 G 주입 완료 (실시간 RAM + 세이브)");
                break;
            case 308: case 318:
                AddGold(1000000);
                Log("★ [Num 8 / Ctrl+8] 은화 +1,000,000 G 주입 완료 (실시간 RAM + 세이브)");
                break;
            case 309: case 319:
                ActivateBalancedPreset();
                Log("★ [Num 9 / Ctrl+9] 추천 몰입 밸런스 세트 가동");
                break;
            case 310: case 320:
                ResetAllToVanilla();
                Log("↺ [Num 0 / Ctrl+0] 모든 치트 순정(Vanilla) 초기화");
                break;
            case 101: SetDamageTakenMode((damageTakenMode + 1) % 5); break; // F1: 방어 모드 순환
            case 102: SetPlayerStaminaMode((playerStaminaMode + 1) % 5); break; // F2: 기력 모드 순환
            case 103: // F3: 공격력 순환
                float[] atks = { 1.0f, 1.5f, 2.0f, 3.0f, 5.0f, 999.0f };
                int aidx = 0; for (int i = 0; i < atks.Length; i++) if (Math.Abs(atks[i] - damageDealtMultiplier) < 0.05f) aidx = i;
                SetDamageDealtMultiplier(atks[(aidx + 1) % atks.Length]);
                break;
            case 104: SetCooldownMode((cooldownMode + 1) % 5); break; // F4: 쿨타임 순환
            case 105: CycleExp(); break;
            case 106: CycleSpeed(); break;
            case 107: CycleJump(); break;
            case 108: ToggleAutoTalk(!isAutoTalk); break;
            case 109: ToggleAutoSprint(!isAutoSprint); break;
            case 110: ToggleAutoObserve(!isAutoObserve); break;
            case 111: // F11: HUD 상시 도크 노출 / 완전 숨김 전환
                if (inGameHud != null && !inGameHud.IsDisposed) {
                    isHudVisible = true;
                    inGameHud.TogglePinMode();
                    Log(inGameHud.PinHudMode
                        ? "◆ [Ctrl+Alt+F11] 우측 상단 미니 치트 도크 노출 모드 ON"
                        : "◆ [Ctrl+Alt+F11] HUD 완전 숨김 모드 ON (다시 보려면 Ctrl+Alt+F11 또는 Ctrl+` / INSERT 입력)");
                }
                break;
            case 200: ToggleMiniOverlay(); break;
            case 112: // Ctrl+Alt+F12
            case 113: // Pause
                EmergencyStopAllAndLand();
                break;
            case 201: // INSERT
            case 204: // ` / ~ (물결키)
                if (inGameHud != null && !inGameHud.IsDisposed) {
                    isHudVisible = true;
                    inGameHud.SetInteractiveMode(!inGameHud.InteractiveMenuMode);
                    Log(inGameHud.InteractiveMenuMode
                        ? "◆ [` / INSERT] 인게임 4탭 치트 사이드바(Command Dock) 열림 — 마우스로 모든 스위치를 직접 클릭하세요!"
                        : "◆ [` / INSERT] 인게임 치트 사이드바 접힘 — 3.5초 알림 후 3D 게임 중 자동 비노출(Independent Flip) 전환");
                }
                break;
            case 202:
                if (inGameHud != null) inGameHud.CycleWalkthrough(-1);
                break;
            case 203:
                if (inGameHud != null) inGameHud.CycleWalkthrough(1);
                break;
            case 900:
            case 901:
            case 902:
            case 903:
                if (IsAuthorizedSelfTestActive() && inGameHud != null && !inGameHud.IsDisposed) {
                    inGameHud.StealthHideAll = false;
                    inGameHud.ActiveHudTab = id - 900;
                    if (!inGameHud.InteractiveMenuMode) inGameHud.SetInteractiveMode(true);
                    inGameHud.Invalidate();
                    inGameHud.Update();
                }
                break;
            case 910:
                if (IsAuthorizedSelfTestActive() && inGameHud != null && !inGameHud.IsDisposed) {
                    inGameHud.StealthHideAll = false;
                    if (!inGameHud.InteractiveMenuMode) inGameHud.SetInteractiveMode(true);
                    inGameHud.Invalidate();
                    inGameHud.Update();
                }
                break;
            case 911:
                if (IsAuthorizedSelfTestActive() && inGameHud != null && !inGameHud.IsDisposed) {
                    if (inGameHud.InteractiveMenuMode) inGameHud.SetInteractiveMode(false);
                    inGameHud.Invalidate();
                }
                break;
        }
        if (id != 111 && id != 112 && id != 113 && !(id >= 900 && id <= 919) && inGameHud != null && !inGameHud.IsDisposed && !inGameHud.StealthHideAll) {
            inGameHud.NotifyActivity();
        }
    }

    protected override void WndProc(ref Message m) {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY) {
            TriggerHotkeyAction(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }

    [STAThread] static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "--boost-probe") {
            string basePath = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
            string log = Path.Combine(basePath, @"tuning\research\session-boost-probe.txt");
            var session = new SessionBoost { Enabled = true };
            try {
                var live = Process.GetProcessesByName("CrimsonDesert");
                try { if (live.Length != 1) throw new InvalidOperationException("Boost probe requires exactly one running game."); }
                finally { foreach (var p in live) p.Dispose(); }
                string applied = session.Refresh(Path.Combine(basePath, @"bin64\CrimsonDesert.exe"));
                session.Restore(); File.WriteAllText(log, applied + "\r\nRestore completed.\r\n"); return 0;
            } catch (Exception ex) { File.WriteAllText(log, ex.ToString()); return 1; }
            finally { session.Dispose(); }
        }
        if (args != null && args.Length >= 2 && args[0] == "--dump-ui") {
            string[] parts = new string[args.Length - 1];
            Array.Copy(args, 1, parts, 0, args.Length - 1);
            string dumpDir = string.Join(" ", parts).Trim('"', '\'');
            Launcher.DumpUiMode = true;
            try {
                Directory.CreateDirectory(dumpDir);
                Launcher launcher = new Launcher();
                launcher.TopMost = true;
                launcher.Show();
                launcher.BringToFront();
                launcher.ActivateBalancedPreset();
                Application.DoEvents();
                Thread.Sleep(250);
                List<string> overlaps = new List<string>();
                Action<Control, string> auditCtrl = null;
                auditCtrl = delegate(Control parent, string prefix) {
                    for (int i = 0; i < parent.Controls.Count; i++) {
                        Control c1 = parent.Controls[i];
                        if (!c1.Visible) continue;
                        for (int j = i + 1; j < parent.Controls.Count; j++) {
                            Control c2 = parent.Controls[j];
                            if (!c2.Visible) continue;
                            if (c1.Bounds.IntersectsWith(c2.Bounds)) {
                                overlaps.Add(string.Format("{0}: [{1} '{2}' {3}] OVERLAPS [{4} '{5}' {6}]",
                                    prefix, c1.GetType().Name, c1.Text, c1.Bounds, c2.GetType().Name, c2.Text, c2.Bounds));
                            }
                        }
                        auditCtrl(c1, prefix + "/" + c1.GetType().Name);
                    }
                };
                for (int tabIdx = 0; tabIdx < 5; tabIdx++) {
                    launcher.mainTabs.SelectedIndex = tabIdx;
                    launcher.Refresh();
                    Application.DoEvents();
                    Thread.Sleep(200);
                    Application.DoEvents();
                    auditCtrl(launcher, "Tab" + (tabIdx + 1));
                    using (Bitmap tbmp = new Bitmap(launcher.Width, launcher.Height)) {
                        launcher.DrawToBitmap(tbmp, new Rectangle(0, 0, launcher.Width, launcher.Height));
                        tbmp.Save(Path.Combine(dumpDir, string.Format("launcher_tab{0}.png", tabIdx + 1)), System.Drawing.Imaging.ImageFormat.Png);
                        if (tabIdx == 0) tbmp.Save(Path.Combine(dumpDir, "launcher_main_window.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                if (launcher.inGameHud != null && !launcher.inGameHud.IsDisposed) {
                    launcher.inGameHud.SetInteractiveMode(true);
                    launcher.inGameHud.Location = new Point(150, 120);
                    launcher.inGameHud.BringToFront();
                    for (int hTab = 0; hTab < 4; hTab++) {
                        launcher.inGameHud.ActiveHudTab = hTab;
                        launcher.inGameHud.Invalidate();
                        launcher.inGameHud.Refresh();
                        Application.DoEvents();
                        Thread.Sleep(180);
                        using (Bitmap hbmp = new Bitmap(launcher.inGameHud.Width, launcher.inGameHud.Height)) {
                            launcher.inGameHud.DrawToBitmap(hbmp, new Rectangle(0, 0, launcher.inGameHud.Width, launcher.inGameHud.Height));
                            hbmp.Save(Path.Combine(dumpDir, string.Format("hud_editor_tab{0}.png", hTab + 1)), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                }
                File.WriteAllText(Path.Combine(dumpDir, "ui_overlap_audit.txt"),
                    overlaps.Count == 0 ? "OVERLAP_COUNT=0" : string.Join("\r\n", overlaps.ToArray()), Encoding.UTF8);
                try { if (launcher.inGameHud != null) launcher.inGameHud.Close(); } catch { }
                try { launcher.Close(); } catch { }
                Environment.Exit(0);
                return 0;
            } catch (Exception ex) {
                File.WriteAllText(Path.Combine(dumpDir, "dump_error.txt"), ex.ToString(), Encoding.UTF8);
                return 1;
            }
        }

        if (args.Length == 1 && args[0] == "--self-test") {
            string dir = Path.Combine(Path.GetTempPath(), "CrimsonLauncherTest-" + Guid.NewGuid().ToString("N"));
            try {
                Directory.CreateDirectory(dir); string source = Path.Combine(dir, "source.xml"), target = Path.Combine(dir, "target.xml"), backups = Path.Combine(dir, "backups");
                File.WriteAllText(source, "<EngineOptionSave _selectPreset='Medium'><Unknown value='preserve'/></EngineOptionSave>");
                File.WriteAllText(target, "<EngineOptionSave _selectPreset='Low'/>");
                string before = File.ReadAllText(target); ConfigStore.Restore(source, target, backups);
                if (File.ReadAllText(target) != File.ReadAllText(source) || File.ReadAllText(Directory.GetFiles(backups)[0]) != before) throw new Exception("Round-trip/backup failed");
                File.WriteAllText(source, "<!DOCTYPE x [<!ENTITY test SYSTEM 'file:///nonexistent'>]><EngineOptionSave>&test;</EngineOptionSave>");
                bool rejected = false; try { ConfigStore.Validate(source); } catch (XmlException) { rejected = true; }
                if (!rejected) throw new Exception("DTD validation failed");
                string safeTarget = File.ReadAllText(target); int backupCount = Directory.GetFiles(backups).Length;
                rejected = false; try { ConfigStore.Restore(source, target, backups); } catch (XmlException) { rejected = true; }
                if (!rejected || File.ReadAllText(target) != safeTarget || Directory.GetFiles(backups).Length != backupCount) throw new Exception("Invalid XML changed existing settings");
                File.WriteAllText(source, "<OtherDocument/>"); rejected = false;
                try { ConfigStore.Restore(source, target, backups); } catch (InvalidDataException) { rejected = true; }
                if (!rejected || File.ReadAllText(target) != safeTarget) throw new Exception("Wrong root accepted");
                File.WriteAllText(source, "<EngineOptionSave _selectPreset='Medium'><EngineOption Template='/OS_M_Windows::Medium.engineoption'><EngineOptionVideo Name='_videoOption'><Test value='new'/></EngineOptionVideo><EngineOptionLanguage Name='_languageOption' language='ENG' difficulty='Hard'/></EngineOption></EngineOptionSave>");
                File.WriteAllText(target, "<EngineOptionSave _selectPreset='Low'><EngineOption Template='/OS_M_Windows::Low.engineoption'><EngineOptionControls Name='_controlsOption' custom='keep'/><EngineOptionLanguage Name='_languageOption' language='KOR' difficulty='Easy'/></EngineOption></EngineOptionSave>");
                ConfigStore.RestoreGraphics(source, target, backups); var merged = ConfigStore.Load(target);
                if (merged.SelectSingleNode("//EngineOptionLanguage").Attributes["difficulty"].Value != "Easy"
                    || merged.SelectSingleNode("//EngineOptionLanguage").Attributes["language"].Value != "KOR"
                    || merged.SelectSingleNode("//EngineOptionControls").Attributes["custom"].Value != "keep"
                    || merged.SelectSingleNode("//EngineOptionVideo/Test").Attributes["value"].Value != "new"
                    || merged.DocumentElement.GetAttribute("_selectPreset") != "Medium") throw new Exception("Graphics-only merge failed");

                // Gate 2: All PE hook RVA/instruction signatures match CrimsonDesert.exe v1.0.0.2625
                string rootDir = Launcher.ResolveRootDir();
                string sigReport;
                if (!LiveMemoryCheatEngine.VerifyAllHookSignatures(Path.Combine(rootDir, @"bin64\CrimsonDesert.exe"), out sigReport))
                    throw new Exception("Hook signatures failed: " + sigReport);

                // Gate 3: All 25 editor rows toggle distinct state -> INI round-trip -> vanilla reset -> reload
                string tempIni = Path.Combine(dir, "cheat_roundtrip.ini");
                Launcher.SelfTestMode = true;
                try {
                    using (Launcher l = new Launcher()) {
                        l.SetDamageTakenMode(2);
                        l.SetDamageDealtMultiplier(2.5f);
                        l.SetPlayerStaminaMode(4);
                        l.SetCooldownMode(3);
                        l.SetExpMultiplier(4);
                        l.SetSpeedMultiplier(1.5f);
                        l.SetJumpMultiplier(1.5f);
                        l.SetRomanceMultiplier(5);
                        l.SetTamingEase(3);
                        l.SetMountSpeed(2.0f);
                        l.SetMountStaminaMode(4);
                        l.SetUnderwaterOxygen(5);
                        l.SetUnderwaterSpeed(2.0f);
                        l.SetCraftCost(3);
                        l.SetCraftSuccess(5);
                        l.SetDurabilityMode(4);
                        l.SetGearHiderMode(2);
                        l.SetWeaponImbueMode(4);
                        long gBefore = l.currentGoldBonus;
                        l.AddGold(10000L);
                        if (l.currentGoldBonus == gBefore) throw new Exception("Gate 3 AddGold failed");
                        l.SetThiefDetectionMode(2);
                        l.SetPickpocketSuccessMode(3);
                        l.SetBountyImmunityMode(2);
                        l.SetThiefGloveFenceMode(3);
                        l.SetBagExpandSlots(60);
                        l.SetSpecialConvenienceMode(3);

                        Action<EditorCheatState, string> assertNonDefault = delegate(EditorCheatState st, string phase) {
                            if (st.DamageTakenMode != 2 || Math.Abs(st.DamageDealtMultiplier - 2.5f) > 0.01f
                                || st.PlayerStaminaMode != 4 || st.CooldownMode != 3
                                || Math.Abs(st.ExpMultiplier - 4.0f) > 0.01f || Math.Abs(st.SpeedMultiplier - 1.5f) > 0.01f
                                || Math.Abs(st.JumpMultiplier - 1.5f) > 0.01f || Math.Abs(st.RomanceMultiplier - 5.0f) > 0.01f
                                || st.AnimalTamingMode != 2 || Math.Abs(st.MountSpeedMultiplier - 2.0f) > 0.01f
                                || st.MountStaminaMode != 4 || st.UnderwaterOxygenMode != 3
                                || Math.Abs(st.UnderwaterSpeedMultiplier - 2.0f) > 0.01f || st.CraftMaterialCost != 3
                                || st.CraftGreatSuccess != 3 || st.DurabilityMode != 4
                                || st.GearHiderMode != 2 || st.WeaponImbueMode != 4
                                || st.ThiefDetectionMode != 2 || st.PickpocketSuccessMode != 3
                                || st.BountyImmunityMode != 2 || st.ThiefGloveMode != 3
                                || st.BagExpandSlots != 60 || st.SpecialConvenienceMode != 3) {
                                throw new Exception("Gate 3 state mismatch at " + phase);
                            }
                        };

                        assertNonDefault(l.BuildEditorCheatState(), "initial toggle");
                        l.SaveCheatConfig(tempIni);
                        l.ResetAllToVanilla();
                        EditorCheatState rst = l.BuildEditorCheatState();
                        if (rst.DamageTakenMode != 0 || Math.Abs(rst.DamageDealtMultiplier - 1.0f) > 0.01f
                            || rst.PlayerStaminaMode != 0 || rst.CooldownMode != 0
                            || Math.Abs(rst.ExpMultiplier - 1.0f) > 0.01f || Math.Abs(rst.SpeedMultiplier - 1.0f) > 0.01f
                            || Math.Abs(rst.JumpMultiplier - 1.0f) > 0.01f || Math.Abs(rst.RomanceMultiplier - 1.0f) > 0.01f
                            || rst.AnimalTamingMode != 0 || Math.Abs(rst.MountSpeedMultiplier - 1.0f) > 0.01f
                            || rst.MountStaminaMode != 0 || rst.UnderwaterOxygenMode != 0
                            || Math.Abs(rst.UnderwaterSpeedMultiplier - 1.0f) > 0.01f || rst.CraftMaterialCost != 0
                            || rst.CraftGreatSuccess != 0 || rst.DurabilityMode != 0
                            || rst.GearHiderMode != 0 || rst.WeaponImbueMode != 0
                            || rst.ThiefDetectionMode != 0 || rst.PickpocketSuccessMode != 0
                            || rst.BountyImmunityMode != 0 || rst.ThiefGloveMode != 0
                            || rst.BagExpandSlots != 0 || rst.SpecialConvenienceMode != 0) {
                            throw new Exception("Gate 3 ResetAllToVanilla failed");
                        }
                        l.LoadCheatConfig(tempIni);
                        assertNonDefault(l.BuildEditorCheatState(), "INI reload");
                    }
                } finally {
                    Launcher.SelfTestMode = false;
                }

                // Gate 4: External FLiNG v1.0.0.14 trainer paths & file sizes
                string srcFling = Path.Combine(rootDir, @"tuning\Crimson Desert-Plus-12-Trainer\Crimson Desert Enhanced v1.0-v2.0x Plus 12 Trainer Updated 2026.10.30.exe");
                string rootFling = Path.Combine(rootDir, "FLiNG_Trainer_Plus12.exe");
                if (!File.Exists(srcFling) || new FileInfo(srcFling).Length < 1700000L)
                    throw new Exception("Gate 4 missing/invalid Updated 2026.10.30 FLiNG trainer");
                if (!File.Exists(rootFling) || new FileInfo(rootFling).Length < 1700000L)
                    throw new Exception("Gate 4 missing/invalid root FLiNG_Trainer_Plus12.exe");

                // Gate 5: Non-activating InGameHudOverlayForm & zero background Python spawn
                int pyBefore = Process.GetProcessesByName("python").Length;
                Stopwatch silSw = Stopwatch.StartNew();
                long silCheck = LiveMemoryCheatEngine.GetSaveFileSilverAmount();
                silSw.Stop();
                if (silCheck < -1000L || silSw.ElapsedMilliseconds > 250)
                    throw new Exception("Gate 5 GetSaveFileSilverAmount slow: " + silSw.ElapsedMilliseconds + "ms");
                int pyAfter = Process.GetProcessesByName("python").Length;
                if (pyAfter > pyBefore)
                    throw new Exception(string.Format("Gate 5 spawned python process ({0} -> {1})", pyBefore, pyAfter));

                Launcher.SelfTestMode = true;
                try {
                    using (Launcher l5 = new Launcher())
                    using (InGameHudOverlayForm hud5 = new InGameHudOverlayForm(l5)) {
                        IntPtr hh = hud5.Handle;
                        int exCol = Win32Native.GetWindowLong(hh, Win32Native.GWL_EXSTYLE);
                        if ((exCol & Win32Native.WS_EX_NOACTIVATE) == 0 || (exCol & Win32Native.WS_EX_TRANSPARENT) == 0)
                            throw new Exception(string.Format("Gate 5 collapsed ExStyle invalid: 0x{0:X}", exCol));
                        hud5.SetInteractiveMode(true);
                        int exExp = Win32Native.GetWindowLong(hh, Win32Native.GWL_EXSTYLE);
                        if ((exExp & Win32Native.WS_EX_NOACTIVATE) == 0 || (exExp & Win32Native.WS_EX_TRANSPARENT) != 0)
                            throw new Exception(string.Format("Gate 5 expanded ExStyle invalid: 0x{0:X}", exExp));
                        hud5.SetInteractiveMode(false);
                        int exReCol = Win32Native.GetWindowLong(hh, Win32Native.GWL_EXSTYLE);
                        if ((exReCol & Win32Native.WS_EX_NOACTIVATE) == 0 || (exReCol & Win32Native.WS_EX_TRANSPARENT) == 0)
                            throw new Exception(string.Format("Gate 5 re-collapsed ExStyle invalid: 0x{0:X}", exReCol));

                        // Gate 6: Trigger gate, unauthorized self-test default false, and HUD hitmap export (7 top + 4 tabs + 129 row buttons)
                        if (!Launcher.ValidateTriggerPhrase(Launcher.REQUIRED_SELF_TEST_TRIGGER) || Launcher.ValidateTriggerPhrase("시작") || Launcher.ValidateTriggerPhrase(""))
                            throw new Exception("Gate 6 ValidateTriggerPhrase failed");
                        if (l5.IsAuthorizedSelfTestActive())
                            throw new Exception("Gate 6 IsAuthorizedSelfTestActive should be false by default");
                        string hitDir = Path.Combine(dir, "hitmaps");
                        Directory.CreateDirectory(hitDir);
                        int[] expectedTabCounts = new int[] { 42, 31, 31, 25 };
                        int totalRowBtns = 0;
                        hud5.Show();
                        hud5.SetInteractiveMode(true);
                        for (int tabIdx = 0; tabIdx < 4; tabIdx++) {
                            hud5.ActiveHudTab = tabIdx;
                            hud5.Invalidate();
                            hud5.Update();
                            Application.DoEvents();
                            string hp = Path.Combine(hitDir, "tab" + tabIdx + ".json");
                            hud5.ExportHudHitmap(hp);
                            if (!File.Exists(hp)) throw new Exception("Gate 6 missing hitmap " + hp);
                            string hj = File.ReadAllText(hp, Encoding.UTF8);
                            string catNeedle = "\"cat\":\"tab_" + tabIdx + "\"";
                            int catCount = 0;
                            int pos = 0;
                            while ((pos = hj.IndexOf(catNeedle, pos, StringComparison.Ordinal)) >= 0) {
                                catCount++;
                                pos += catNeedle.Length;
                            }
                            if (catCount != expectedTabCounts[tabIdx])
                                throw new Exception(string.Format("Gate 6 tab {0} row button count mismatch: got {1} expected {2}", tabIdx, catCount, expectedTabCounts[tabIdx]));
                            totalRowBtns += catCount;
                            if (tabIdx == 0) {
                                int topCount = 0;
                                int tp = 0;
                                while ((tp = hj.IndexOf("\"cat\":\"top_bar\"", tp, StringComparison.Ordinal)) >= 0) { topCount++; tp += 15; }
                                int hdrCount = 0;
                                int hp2 = 0;
                                while ((hp2 = hj.IndexOf("\"cat\":\"tab_header\"", hp2, StringComparison.Ordinal)) >= 0) { hdrCount++; hp2 += 18; }
                                if (topCount != 7 || hdrCount != 4)
                                    throw new Exception(string.Format("Gate 6 top/header counts mismatch: top={0} hdr={1}", topCount, hdrCount));
                            }
                        }
                        hud5.SetInteractiveMode(false);
                        if (totalRowBtns != 129)
                            throw new Exception("Gate 6 total row button count mismatch: " + totalRowBtns);
                    }
                } finally {
                    Launcher.SelfTestMode = false;
                }

                // Gate 7: Verify GlobalItemTableManager PE signature & no hardcoded 10B bogus silver regression
                if (sigReport != "RVA_VERIFY_OK=20/20")
                    throw new Exception("Gate 7 signature count mismatch: " + sigReport);
                if (silCheck >= 5000000000L)
                    throw new Exception("Gate 7 bogus silver cache detected: " + silCheck);

                Console.WriteLine("[SELF-TEST PASS] Gates 1..7 OK (" + sigReport + " + Silver/Arrow Guard + Trigger Gate + 129 HUD Hitmap Buttons + Non-Activating HUD Verified)");
                return 0;
            } catch (Exception ex) { Console.Error.WriteLine("[SELF-TEST FAIL] " + ex); return 1; } finally {
                string full = Path.GetFullPath(dir);
                string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).StartsWith("CrimsonLauncherTest-", StringComparison.Ordinal)
                    && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }
        try {
            // Application.EnableVisualStyles();
            // Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_crash.log"), e.ExceptionObject.ToString());
            };
            Application.ThreadException += (s, e) => {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_crash.log"), e.Exception.ToString());
            };
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "CrimsonDesert_Master_Launcher_Mutex", out createdNew)) {
                if (!createdNew) {
                    var procs = Process.GetProcessesByName("CrimsonLauncher");
                    foreach (var p in procs) {
                        if (p.Id != Process.GetCurrentProcess().Id) {
                            try { p.Kill(); } catch { }
                        }
                        p.Dispose();
                    }
                    Thread.Sleep(200);
                }
                bool startMinimized = false;
                if (args != null) {
                    foreach (string a in args) {
                        if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) startMinimized = true;
                    }
                }
                Launcher app = new Launcher();
                if (startMinimized) {
                    app.WindowState = FormWindowState.Minimized;
                    app.ShowInTaskbar = true;
                }
                Application.Run(app);
            }
            return 0;
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_crash.log"), ex.ToString());
            return 1;
        }
    }
}
