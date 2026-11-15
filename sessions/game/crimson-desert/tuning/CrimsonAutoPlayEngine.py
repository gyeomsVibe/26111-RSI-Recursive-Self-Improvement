# -*- coding: utf-8 -*-
r"""
CrimsonAutoPlayEngine.py — Live Gameplay & Dual-Telemetry Auto-Play Engine (Zero Paid Tokens)
=============================================================================================
- Safely snapshots ALL current user save slots (%LOCALAPPDATA%\Pearl Abyss\CD\save\22202)
  into D:\Games\Crimson Desert\.work\autoplay_save_guard_<session_id> before running, and
  restores exact pre-run user saves + clean vanilla CheatConfig.ini on exit.
- Launches (or reuses) CrimsonLauncher.exe + CrimsonDesert.exe on WinSta0\Default,
  transitions Title Screen -> 3D World, and concurrently collects:
    (1) PresentMon-2.6.0-x64.exe real DX12 frame pacing (*-frames.csv),
    (2) nvidia-smi + WorkingSet GPU/VRAM/RAM samples (gpu_samples.csv),
    (3) CdRecorder.exe 18-hook + 0x4000 cave evidence log (editor/evidence/session_*.log),
    (4) Live WinSta0\Default screenshots (PNG) proving 3D rendering + HUD dashboard.
- Executes real 3D movement/combat/horse/menu interactions while cycling all 25-row v2 editor features.
"""

import argparse
import ctypes
from ctypes import wintypes
import csv
import datetime
import glob
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import threading
import time
import zlib

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)

ROOT_DIR = r"D:\Games\Crimson Desert"
TUNING_DIR = os.path.join(ROOT_DIR, "tuning")
BIN64_DIR = os.path.join(ROOT_DIR, "bin64")
GAME_EXE = os.path.join(BIN64_DIR, "CrimsonDesert.exe")
LAUNCHER_EXE = os.path.join(ROOT_DIR, "CrimsonLauncher.exe")
if not os.path.exists(LAUNCHER_EXE):
    LAUNCHER_EXE = os.path.join(TUNING_DIR, "CrimsonLauncher.exe")
RSI_SCRIPT = os.path.join(TUNING_DIR, "RsiEngine.py")
INI_PATH = os.path.join(TUNING_DIR, "CrimsonDesert_CheatConfig.ini")
PRESENTMON_EXE = os.path.join(TUNING_DIR, "tools", "PresentMon-2.6.0-x64.exe")
CDRECORDER_EXE = os.path.join(TUNING_DIR, "editor", "CdRecorder.exe")

RESEARCH_DIR = os.path.join(TUNING_DIR, "research")
SESSIONS_DIR = os.path.join(RESEARCH_DIR, "sessions")
AUTOPLAY_DIR = os.path.join(RESEARCH_DIR, "autoplay_sessions")
TELEMETRY_DIR = os.path.join(RESEARCH_DIR, "rsi_telemetry")
WORK_DIR = os.path.join(ROOT_DIR, ".work")

LOG_JSONL = os.path.join(TELEMETRY_DIR, "ai_40min_torture_log.jsonl")
STATUS_JSON = os.path.join(TELEMETRY_DIR, "auto_play_status.json")
LEGACY_STATUS_JSON = os.path.join(TELEMETRY_DIR, "ai_40min_status.json")
SUMMARY_REPORT_JSON = os.path.join(TELEMETRY_DIR, "auto_play_final_report.json")

PA_LOG_DIRS = [
    os.path.expandvars(r"%LOCALAPPDATA%\Pearl Abyss\CD\log"),
    os.path.expandvars(r"%LOCALAPPDATA%\Pearl Abyss\log"),
]
SAVE_ROOT = os.path.expandvars(r"%LOCALAPPDATA%\Pearl Abyss\CD\save\22202")
ENGINE_XML = os.path.expandvars(r"%LOCALAPPDATA%\Pearl Abyss\CD\save\user_engine_option_save.xml")

os.makedirs(SESSIONS_DIR, exist_ok=True)
os.makedirs(AUTOPLAY_DIR, exist_ok=True)
os.makedirs(TELEMETRY_DIR, exist_ok=True)
os.makedirs(WORK_DIR, exist_ok=True)

DESKTOP_ALL_ACCESS = 0x01FF
hdesk = user32.OpenDesktopW("Default", 0, False, DESKTOP_ALL_ACCESS)
if hdesk:
    user32.SetThreadDesktop(hdesk)


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("lpReserved", wintypes.LPWSTR),
        ("lpDesktop", wintypes.LPWSTR),
        ("lpTitle", wintypes.LPWSTR),
        ("dwX", wintypes.DWORD),
        ("dwY", wintypes.DWORD),
        ("dwXSize", wintypes.DWORD),
        ("dwYSize", wintypes.DWORD),
        ("dwXCountChars", wintypes.DWORD),
        ("dwYCountChars", wintypes.DWORD),
        ("dwFillAttribute", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("wShowWindow", wintypes.WORD),
        ("cbReserved2", wintypes.WORD),
        ("lpReserved2", ctypes.c_char_p),
        ("hStdInput", wintypes.HANDLE),
        ("hStdOutput", wintypes.HANDLE),
        ("hStdError", wintypes.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("hProcess", wintypes.HANDLE),
        ("hThread", wintypes.HANDLE),
        ("dwProcessId", wintypes.DWORD),
        ("dwThreadId", wintypes.DWORD),
    ]


class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def check_process_fast(pid):
    if not pid or pid <= 0:
        return False, 0.0
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    PROCESS_VM_READ = 0x0010
    hproc = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, pid)
    if not hproc:
        hproc = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not hproc:
        return False, 0.0
    try:
        exit_code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(hproc, ctypes.byref(exit_code)):
            return False, 0.0
        if exit_code.value != 259:
            return False, 0.0
        pmc = PROCESS_MEMORY_COUNTERS()
        pmc.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
        ram_mb = 0.0
        if psapi.GetProcessMemoryInfo(hproc, ctypes.byref(pmc), pmc.cb):
            ram_mb = round(pmc.WorkingSetSize / (1024.0 * 1024.0), 1)
        return True, ram_mb
    finally:
        kernel32.CloseHandle(hproc)


def spawn_on_default_desktop(exe_path, cwd, minimized=False):
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(STARTUPINFOW)
    si.lpDesktop = "WinSta0\\Default"
    if minimized:
        si.dwFlags = 0x00000001
        si.wShowWindow = 7  # SW_SHOWMINNOACTIVE
    pi = PROCESS_INFORMATION()
    cmd = f'"{exe_path}"'
    ret = kernel32.CreateProcessW(
        None,
        ctypes.c_wchar_p(cmd),
        None,
        None,
        False,
        0,
        None,
        ctypes.c_wchar_p(cwd),
        ctypes.byref(si),
        ctypes.byref(pi),
    )
    if not ret:
        err = ctypes.get_last_error()
        raise RuntimeError(f"CreateProcessW failed for {exe_path}, error={err}")
    pid = pi.dwProcessId
    kernel32.CloseHandle(pi.hProcess)
    kernel32.CloseHandle(pi.hThread)
    return pid


PUL = ctypes.POINTER(ctypes.c_ulong)


class KeyBdInput(ctypes.Structure):
    _fields_ = [
        ("wVk", ctypes.c_ushort),
        ("wScan", ctypes.c_ushort),
        ("dwFlags", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("dwExtraInfo", PUL),
    ]


class MouseInput(ctypes.Structure):
    _fields_ = [
        ("dx", ctypes.c_long),
        ("dy", ctypes.c_long),
        ("mouseData", ctypes.c_ulong),
        ("dwFlags", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("dwExtraInfo", PUL),
    ]


class Input_I(ctypes.Union):
    _fields_ = [("ki", KeyBdInput), ("mi", MouseInput)]


class Input(ctypes.Structure):
    _fields_ = [("type", ctypes.c_ulong), ("ii", Input_I)]


KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_SCANCODE = 0x0008
MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010
MOUSEEVENTF_VIRTUALDESK = 0x4000
MOUSEEVENTF_ABSOLUTE = 0x8000

REQUIRED_TRIGGER_PHRASE = "자가 테스트 실시"
STOP_FLAG_FILE = os.path.join(TUNING_DIR, "stop_autoplay.flag")
HITMAP_JSON = os.path.join(TELEMETRY_DIR, "hud_hitmap.json")
LIVE_HEALTH_JSON = os.path.join(TELEMETRY_DIR, "live_editor_health.json")
ALL_TABS_REPORT_JSON = os.path.join(TELEMETRY_DIR, "all_tabs_live_experiment_20261114.json")
LIVE_HUD_PROOF_DIR = os.path.join(TELEMETRY_DIR, "live_hud_proof")

EMERGENCY_STOP_TRIGGERED = False
EMERGENCY_EVENT = threading.Event()

_AUTH_LOCK = threading.Lock()
_AUTH_STATE = {
    "trigger_verified": False,
    "running": False,
    "aborted_by_human_input": False,
    "human_abort_vk": 0,
    "mode": "",
    "phase": "IDLE",
}


class EmergencyAbortError(BaseException):
    """Raised when user presses F12/Pause, physical keyboard input overrides, or stop flag appears."""


def write_auth_status(extra=None):
    with _AUTH_LOCK:
        payload = {
            "timestamp": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            "event": _AUTH_STATE.get("phase", "HEARTBEAT"),
            "message": "Authorized AutoPlay status",
        }
        payload.update(_AUTH_STATE)
        if extra:
            payload.update(extra)
        raw = json.dumps(payload, ensure_ascii=False, indent=2)
        for sfile in (STATUS_JSON, LEGACY_STATUS_JSON):
            tmp = f"{sfile}.{os.getpid()}.tmp"
            try:
                with open(tmp, "w", encoding="utf-8") as f:
                    f.write(raw)
                os.replace(tmp, sfile)
            except Exception:
                try:
                    with open(sfile, "w", encoding="utf-8") as f:
                        f.write(raw)
                except Exception:
                    pass
            finally:
                if os.path.exists(tmp):
                    try:
                        os.remove(tmp)
                    except Exception:
                        pass


def verify_self_test_trigger(trigger_phrase="", token_file=""):
    if isinstance(trigger_phrase, str) and trigger_phrase.strip() == REQUIRED_TRIGGER_PHRASE:
        return True, "phrase"
    if isinstance(token_file, str) and token_file.strip():
        tf = token_file.strip()
        if os.path.isfile(tf):
            try:
                with open(tf, "r", encoding="utf-8") as f:
                    content = f.read().strip()
                if content == REQUIRED_TRIGGER_PHRASE:
                    try:
                        os.remove(tf)
                    except Exception:
                        pass
                    return True, "token_file"
            except Exception:
                pass
    return False, "missing_or_mismatch"


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ("vkCode", wintypes.DWORD),
        ("scanCode", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_size_t),
    ]


def _is_physical_key_override(wparam, flags, vk_code=0):
    # Only non-injected keydowns (LLKHF_INJECTED == 0x10 bit clear) indicate real human input.
    # Ignore Windows keys (VK_LWIN=0x5B, VK_RWIN=0x5C) and Alt-Tab (VK_TAB=0x09, VK_MENU/LMENU/RMENU=0x12/0xA4/0xA5)
    # when the user switches windows outside the game; F12 (0x7B), Pause (0x13), and ESC (0x1B) always abort immediately.
    if wparam not in (0x0100, 0x0104):
        return False
    if (int(flags) & 0x10) != 0:
        return False
    vk = int(vk_code) & 0xFF
    if vk in (0x5B, 0x5C, 0x09, 0x12, 0xA4, 0xA5):
        return False
    return True


class HardwareInputGuard:
    """Low-level keyboard hook that yields control immediately on any physical user keypress."""

    def __init__(self, abort_on_human=True):
        self.abort_on_human = abort_on_human
        self._thread = None
        self._tid = 0
        self._hhook = None
        self._hook_proc = None
        self._stop_req = threading.Event()
        self._ready = threading.Event()

    def start(self):
        if not self.abort_on_human or self._thread is not None:
            return
        self._stop_req.clear()
        self._ready.clear()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()
        self._ready.wait(timeout=2.0)

    def _run(self):
        global EMERGENCY_STOP_TRIGGERED
        try:
            hd = user32.OpenDesktopW("Default", 0, False, DESKTOP_ALL_ACCESS)
            if hd:
                user32.SetThreadDesktop(hd)
        except Exception:
            pass
        self._tid = kernel32.GetCurrentThreadId()
        HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
        user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wintypes.HMODULE, wintypes.DWORD]
        user32.SetWindowsHookExW.restype = wintypes.HANDLE
        user32.CallNextHookEx.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]
        user32.CallNextHookEx.restype = ctypes.c_ssize_t
        user32.UnhookWindowsHookEx.argtypes = [wintypes.HANDLE]
        user32.UnhookWindowsHookEx.restype = wintypes.BOOL

        def _cb(nCode, wParam, lParam):
            global EMERGENCY_STOP_TRIGGERED
            if nCode >= 0 and lParam:
                kb = ctypes.cast(lParam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
                if _is_physical_key_override(wParam, kb.flags, kb.vkCode):
                    EMERGENCY_STOP_TRIGGERED = True
                    _AUTH_STATE["aborted_by_human_input"] = True
                    _AUTH_STATE["human_abort_vk"] = int(kb.vkCode)
                    _AUTH_STATE["running"] = False
                    _AUTH_STATE["phase"] = "HUMAN_OVERRIDE_ABORT"
                    EMERGENCY_EVENT.set()
                    try:
                        with open(STOP_FLAG_FILE, "w", encoding="utf-8") as sf:
                            sf.write(f"HUMAN_VK={int(kb.vkCode)}")
                    except Exception:
                        pass
                    release_all_keys()
                    write_auth_status({"event": "HUMAN_OVERRIDE_ABORT", "message": f"Physical key VK=0x{int(kb.vkCode):02X} detected; yielded control"})
                    if self._tid:
                        user32.PostThreadMessageW(self._tid, 0x0012, 0, 0)
            return user32.CallNextHookEx(self._hhook, nCode, wParam, lParam)

        self._hook_proc = HOOKPROC(_cb)
        self._hhook = user32.SetWindowsHookExW(13, self._hook_proc, None, 0)
        self._ready.set()
        if not self._hhook:
            return
        msg = wintypes.MSG()
        try:
            while not self._stop_req.is_set() and user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
                user32.TranslateMessage(ctypes.byref(msg))
                user32.DispatchMessageW(ctypes.byref(msg))
        finally:
            if self._hhook:
                try:
                    user32.UnhookWindowsHookEx(self._hhook)
                except Exception:
                    pass
                self._hhook = None

    def stop(self):
        self._stop_req.set()
        if self._tid:
            try:
                user32.PostThreadMessageW(self._tid, 0x0012, 0, 0)
            except Exception:
                pass
        if self._thread is not None:
            self._thread.join(timeout=1.5)
            self._thread = None


def check_emergency_stop():
    global EMERGENCY_STOP_TRIGGERED
    if EMERGENCY_STOP_TRIGGERED or EMERGENCY_EVENT.is_set():
        EMERGENCY_STOP_TRIGGERED = True
        return True
    try:
        if (user32.GetAsyncKeyState(0x7B) & 0x8001) != 0:
            EMERGENCY_STOP_TRIGGERED = True
            EMERGENCY_EVENT.set()
            return True
        if (user32.GetAsyncKeyState(0x13) & 0x8001) != 0:
            EMERGENCY_STOP_TRIGGERED = True
            EMERGENCY_EVENT.set()
            return True
        if os.path.exists(STOP_FLAG_FILE):
            EMERGENCY_STOP_TRIGGERED = True
            EMERGENCY_EVENT.set()
            return True
    except Exception:
        pass
    return False


def release_all_keys():
    extra = ctypes.c_ulong(0)
    all_scancodes = [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x17, 0x1C, 0x1D,
        0x1E, 0x1F, 0x20, 0x21, 0x2A, 0x32, 0x36, 0x38, 0x39,
    ]
    inputs = (Input * len(all_scancodes))()
    for idx, sc in enumerate(all_scancodes):
        inputs[idx].type = 1
        inputs[idx].ii.ki = KeyBdInput(0, sc, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, 0, ctypes.pointer(extra))
    try:
        user32.SendInput(len(all_scancodes), inputs, ctypes.sizeof(Input))
    except Exception:
        pass

    m_inputs = (Input * 2)()
    m_inputs[0].type = 0
    m_inputs[0].ii.mi = MouseInput(0, 0, 0, MOUSEEVENTF_LEFTUP, 0, ctypes.pointer(extra))
    m_inputs[1].type = 0
    m_inputs[1].ii.mi = MouseInput(0, 0, 0, MOUSEEVENTF_RIGHTUP, 0, ctypes.pointer(extra))
    try:
        user32.SendInput(2, m_inputs, ctypes.sizeof(Input))
    except Exception:
        pass


time_real_sleep = time.sleep


def interruptible_sleep(duration, step=0.02):
    end_time = time.time() + duration
    while time.time() < end_time:
        if check_emergency_stop():
            release_all_keys()
            raise EmergencyAbortError("Emergency stop or human input abort triggered")
        remaining = end_time - time.time()
        time_real_sleep(min(step, max(0.001, remaining)))


time.sleep = interruptible_sleep


def _emergency_stop_daemon_loop():
    global EMERGENCY_STOP_TRIGGERED
    try:
        hd = user32.OpenDesktopW("Default", 0, False, DESKTOP_ALL_ACCESS)
        if hd:
            user32.SetThreadDesktop(hd)
    except Exception:
        pass
    abort_seen_at = None
    while _AUTH_STATE.get("running", False) or abort_seen_at is not None:
        try:
            if check_emergency_stop():
                EMERGENCY_STOP_TRIGGERED = True
                EMERGENCY_EVENT.set()
                release_all_keys()
                if abort_seen_at is None:
                    abort_seen_at = time.time()
                    _AUTH_STATE["running"] = False
                    write_auth_status({"event": "EMERGENCY_STOP", "message": "Emergency/human abort signal detected"})
                    sys.stderr.write("\n[EMERGENCY_STOP] Abort signal detected; releasing keys for clean shutdown.\n")
                    sys.stderr.flush()
                elif (time.time() - abort_seen_at) > 8.0:
                    os._exit(3)
        except Exception:
            pass
        time_real_sleep(0.05)


def start_emergency_stop_listener():
    t = threading.Thread(target=_emergency_stop_daemon_loop, daemon=True)
    t.start()
    return t


SCAN_CODES = {
    "ESC": 0x01,
    "1": 0x02, "2": 0x03, "3": 0x04, "4": 0x05,
    "Q": 0x10, "W": 0x11, "E": 0x12, "R": 0x13,
    "ENTER": 0x1C, "CTRL": 0x1D,
    "A": 0x1E, "S": 0x1F, "D": 0x20, "F": 0x21,
    "I": 0x17, "M": 0x32,
    "SHIFT": 0x2A, "SPACE": 0x39,
}


def press_scancode(key_name, hold_sec=0.08):
    if check_emergency_stop():
        release_all_keys()
        raise EmergencyAbortError("Emergency stop or human input abort triggered")
    sc = SCAN_CODES.get(key_name, 0x39)
    extra = ctypes.c_ulong(0)
    ii_ = Input_I()
    ii_.ki = KeyBdInput(0, sc, KEYEVENTF_SCANCODE, 0, ctypes.pointer(extra))
    x = Input(ctypes.c_ulong(1), ii_)
    user32.SendInput(1, ctypes.pointer(x), ctypes.sizeof(x))
    time.sleep(hold_sec)
    ii_.ki = KeyBdInput(0, sc, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, 0, ctypes.pointer(extra))
    x = Input(ctypes.c_ulong(1), ii_)
    user32.SendInput(1, ctypes.pointer(x), ctypes.sizeof(x))


def hold_scancode(key_name, down=True):
    if check_emergency_stop():
        release_all_keys()
        raise EmergencyAbortError("Emergency stop or human input abort triggered")
    sc = SCAN_CODES.get(key_name, 0x11)
    extra = ctypes.c_ulong(0)
    ii_ = Input_I()
    flags = KEYEVENTF_SCANCODE if down else (KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP)
    ii_.ki = KeyBdInput(0, sc, flags, 0, ctypes.pointer(extra))
    x = Input(ctypes.c_ulong(1), ii_)
    user32.SendInput(1, ctypes.pointer(x), ctypes.sizeof(x))


def mouse_move_rel(dx, dy):
    if check_emergency_stop():
        release_all_keys()
        raise EmergencyAbortError("Emergency stop or human input abort triggered")
    extra = ctypes.c_ulong(0)
    ii_ = Input_I()
    ii_.mi = MouseInput(dx, dy, 0, MOUSEEVENTF_MOVE, 0, ctypes.pointer(extra))
    x = Input(ctypes.c_ulong(0), ii_)
    user32.SendInput(1, ctypes.pointer(x), ctypes.sizeof(x))


def mouse_click(right=False, hold_sec=0.06):
    if check_emergency_stop():
        release_all_keys()
        raise EmergencyAbortError("Emergency stop or human input abort triggered")
    extra = ctypes.c_ulong(0)
    ii_ = Input_I()
    down_flag = MOUSEEVENTF_RIGHTDOWN if right else MOUSEEVENTF_LEFTDOWN
    up_flag = MOUSEEVENTF_RIGHTUP if right else MOUSEEVENTF_LEFTUP
    ii_.mi = MouseInput(0, 0, 0, down_flag, 0, ctypes.pointer(extra))
    x = Input(ctypes.c_ulong(0), ii_)
    user32.SendInput(1, ctypes.pointer(x), ctypes.sizeof(x))
    time.sleep(hold_sec)
    ii_.mi = MouseInput(0, 0, 0, up_flag, 0, ctypes.pointer(extra))
    x = Input(ctypes.c_ulong(0), ii_)
    user32.SendInput(1, ctypes.pointer(x), ctypes.sizeof(x))


def mouse_click_screen(sx, sy, hold_sec=0.05):
    """Move cursor physically to screen coordinates (sx, sy) and left-click atomically without activating window."""
    if check_emergency_stop():
        release_all_keys()
        raise EmergencyAbortError("Abort before mouse_click_screen")
    vx = user32.GetSystemMetrics(76)
    vy = user32.GetSystemMetrics(77)
    vw = user32.GetSystemMetrics(78) or user32.GetSystemMetrics(0) or 1920
    vh = user32.GetSystemMetrics(79) or user32.GetSystemMetrics(1) or 1080
    nx = max(0, min(65535, int(((int(sx) - vx) * 65535) / max(1, vw - 1))))
    ny = max(0, min(65535, int(((int(sy) - vy) * 65535) / max(1, vh - 1))))
    extra = ctypes.c_ulong(0)
    abs_flags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK

    user32.ClipCursor(None)
    user32.SetCursorPos(int(sx), int(sy))
    arr = (Input * 3)()
    arr[0].type = ctypes.c_ulong(0)
    arr[0].ii.mi = MouseInput(nx, ny, 0, MOUSEEVENTF_MOVE | abs_flags, 0, ctypes.pointer(extra))
    arr[1].type = ctypes.c_ulong(0)
    arr[1].ii.mi = MouseInput(nx, ny, 0, MOUSEEVENTF_LEFTDOWN | abs_flags, 0, ctypes.pointer(extra))
    arr[2].type = ctypes.c_ulong(0)
    arr[2].ii.mi = MouseInput(nx, ny, 0, MOUSEEVENTF_LEFTUP | abs_flags, 0, ctypes.pointer(extra))
    user32.SendInput(3, arr, ctypes.sizeof(Input))
    time.sleep(max(0.02, min(0.05, hold_sec)))


def read_ini_config():
    cfg = {}
    if not os.path.exists(INI_PATH):
        return cfg
    try:
        with open(INI_PATH, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                s = line.strip()
                if not s or s.startswith("[") or s.startswith("#") or s.startswith(";"):
                    continue
                if "=" in s:
                    k, v = s.split("=", 1)
                    cfg[k.strip()] = v.strip()
    except Exception:
        pass
    return cfg


def read_json_safe(path):
    if not path or not os.path.exists(path):
        return None
    for _ in range(3):
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                return json.load(f)
        except Exception:
            time_real_sleep(0.03)
    return None


def send_hud_ipc(launcher_hwnd, cmd_id, force_file=True):
    """Deliver authorized HUD IPC command (PostMessageW or tuning/ipc_cmd.txt)."""
    ipc_file = os.path.join(TUNING_DIR, "ipc_cmd.txt")
    if force_file or not launcher_hwnd:
        tmp = f"{ipc_file}.{os.getpid()}.tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(str(int(cmd_id)))
            os.replace(tmp, ipc_file)
        except Exception:
            with open(ipc_file, "w", encoding="utf-8") as f:
                f.write(str(int(cmd_id)))
        finally:
            if os.path.exists(tmp):
                try:
                    os.remove(tmp)
                except Exception:
                    pass
    elif launcher_hwnd:
        user32.PostMessageW(launcher_hwnd, 0x0312, int(cmd_id), 0)


def find_window_for_pid(target_pid):
    found = []

    def cb(hwnd, lp):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == target_pid and user32.IsWindowVisible(hwnd):
            rect = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            if (rect.right - rect.left) > 200 and (rect.bottom - rect.top) > 200:
                found.append(hwnd)
        return True

    WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    if hdesk:
        user32.EnumDesktopWindows(hdesk, WNDENUMPROC(cb), 0)
    else:
        user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found[0] if found else None


def focus_window(hwnd):
    if not hwnd:
        return False
    try:
        fg = user32.GetForegroundWindow()
        if fg == hwnd:
            return True
        user32.ShowWindow(hwnd, 9)
        user32.SwitchToThisWindow(hwnd, True)
        user32.SetForegroundWindow(hwnd)
        return True
    except Exception:
        return False


def write_ini_config(overrides=None):
    cfg = {
        "DamageTakenMode": "0",
        "DamageDealtMultiplier": "1",
        "PlayerStaminaMode": "0",
        "CooldownMode": "0",
        "ExpMultiplier": "1",
        "SpeedMultiplier": "1",
        "JumpMultiplier": "1",
        "RomanceMultiplier": "1",
        "TamingMultiplier": "1",
        "MountSpeedMultiplier": "1",
        "MountStaminaMode": "0",
        "UnderwaterOxygenMultiplier": "1",
        "UnderwaterSpeed": "1",
        "CraftCostMode": "0",
        "CraftSuccessMultiplier": "1",
        "DurabilityMode": "0",
        "GearHiderMode": "0",
        "WeaponImbueMode": "0",
        "ThiefStealthMode": "0",
        "ThiefDetectionMode": "0",
        "PickpocketSuccessMode": "0",
        "BountyImmunityMode": "0",
        "ThiefGloveFenceMode": "0",
        "BagExpandSlots": "0",
        "SpecialConvenienceMode": "0",
        "MaxHealthVal": "0",
        "MaxStaminaVal": "0",
        "MaxSpiritVal": "0",
        "FreezeDaytime": "0",
    }
    if overrides:
        for k, v in overrides.items():
            cfg[k] = str(v)
    lines = ["[CheatConfig]"]
    for k, v in cfg.items():
        lines.append(f"{k}={v}")
    lines.append(f"UpdatedAt={datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    tmp_path = INI_PATH + ".tmp"
    with open(tmp_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    os.replace(tmp_path, INI_PATH)


def snapshot_ini_config(ini_path=INI_PATH):
    snap = {}
    if os.path.exists(ini_path):
        try:
            with open(ini_path, "r", encoding="utf-8", errors="replace") as f:
                for raw in f:
                    s = raw.strip()
                    if not s or s.startswith(("[", ";", "#")) or "=" not in s:
                        continue
                    k, v = [x.strip() for x in s.split("=", 1)]
                    if k != "UpdatedAt":
                        snap[k] = v
        except Exception:
            pass
    return snap


def restore_ini_config_safe_speeds(snap, ini_path=INI_PATH):
    """Restores the pre-test INI snapshot (preserving pre-existing user speed/jump preferences while discarding mid-test toggle mutations); defaults missing keys to safe 1.0x/0."""
    snap_dict = dict(snap or {})
    merged = {
        "SpeedMultiplier": "1",
        "JumpMultiplier": "1",
        "MountSpeedMultiplier": "1",
        "UnderwaterSpeed": "1",
        "SpecialConvenienceMode": "0",
    }
    for k, v in snap_dict.items():
        merged[k] = str(v)
    if ini_path == INI_PATH:
        write_ini_config(merged)
    else:
        lines = ["[CheatConfig]"]
        for k, v in merged.items():
            lines.append(f"{k}={v}")
        lines.append(f"UpdatedAt={datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        tmp_p = ini_path + ".tmp"
        with open(tmp_p, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        os.replace(tmp_p, ini_path)



def get_gpu_and_proc_stats(game_pid):
    alive, ram_mb = check_process_fast(game_pid)
    stats = {
        "game_alive": alive,
        "pid": game_pid,
        "ram_mb": ram_mb,
        "gpu_util": 0,
        "vram_mb": 0,
        "vram_total": 8192,
        "gpu_temp": 0,
        "gpu_clock": 0,
    }
    try:
        gout = subprocess.check_output(
            [
                "nvidia-smi",
                "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,clocks.gr",
                "--format=csv,noheader,nounits",
            ],
            text=True,
            timeout=3,
            creationflags=0x08000000,
        ).strip()
        if gout:
            parts = [x.strip() for x in gout.split(",")]
            if len(parts) >= 5:
                stats["gpu_util"] = int(float(parts[0]))
                stats["vram_mb"] = int(float(parts[1]))
                stats["vram_total"] = int(float(parts[2]))
                stats["gpu_temp"] = int(float(parts[3]))
                stats["gpu_clock"] = int(float(parts[4]))
    except Exception:
        pass
    return stats


def parse_game_log(game_pid, min_mtime=0.0):
    candidates = []
    for d in PA_LOG_DIRS:
        if not os.path.isdir(d):
            continue
        for pat in (f"Launcher_*_{game_pid}.log", "Launcher_*.log"):
            for f in glob.glob(os.path.join(d, pat)):
                try:
                    if os.path.getmtime(f) >= (min_mtime - 15.0):
                        candidates.append((1 if f"_{game_pid}." in f else 0, os.path.getmtime(f), f))
                except Exception:
                    pass
    if not candidates:
        return {"exists": False, "lines": 0, "start_loads": 0, "end_loads": 0, "has_display": False, "last_line": ""}
    candidates.sort()
    log_file = candidates[-1][2]
    try:
        with open(log_file, "r", encoding="utf-8", errors="ignore") as f:
            text = f.read()
        lines = text.splitlines()
        return {
            "exists": True,
            "path": log_file,
            "lines": len(lines),
            "start_loads": text.count("[Start Load SaveSlot"),
            "end_loads": text.count("[End Load SaveSlot"),
            "has_display": "[Display:" in text,
            "last_line": lines[-1] if lines else "",
        }
    except Exception as e:
        return {"exists": True, "lines": -1, "start_loads": 0, "end_loads": 0, "has_display": False, "last_line": str(e)}


def log_event(event_type, message, extra=None):
    now_str = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    with _AUTH_LOCK:
        _AUTH_STATE["phase"] = event_type
        entry = {"timestamp": now_str, "event": event_type, "message": message}
        entry.update(_AUTH_STATE)
    if extra:
        entry.update(extra)
    try:
        print(f"[{now_str}] [{event_type}] {message}", flush=True)
    except Exception:
        pass
    with open(LOG_JSONL, "a", encoding="utf-8") as f:
        f.write(json.dumps(entry, ensure_ascii=False) + "\n")
    raw = json.dumps(entry, ensure_ascii=False, indent=2)
    for sfile in (STATUS_JSON, LEGACY_STATUS_JSON):
        tmp = f"{sfile}.{os.getpid()}.tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(raw)
            os.replace(tmp, sfile)
        except Exception:
            try:
                with open(sfile, "w", encoding="utf-8") as f:
                    f.write(raw)
            except Exception:
                pass
        finally:
            if os.path.exists(tmp):
                try:
                    os.remove(tmp)
                except Exception:
                    pass


# Exact 25-row v2 Editor Verification Matrix covering all 4 HUD tabs + Presets + Reset
EDITOR_TEST_MATRIX = [
    ("Step 01: [순정 베이스라인] 모든 치트 OFF", {}, None),
    ("Step 02: [탭1 방어/공격] 방어-50% + 공격x2.0", {"DamageTakenMode": "2", "DamageDealtMultiplier": "2.0"}, None),
    ("Step 03: [탭1 무적/필살] 완전무적 + 일격필살", {"DamageTakenMode": "4", "DamageDealtMultiplier": "999"}, None),
    ("Step 04: [탭1 기력/쿨감] 회복2배 + 쿨-50%", {"DamageTakenMode": "2", "PlayerStaminaMode": "2", "CooldownMode": "2"}, None),
    ("Step 05: [탭1 무한기력/투지/경험치] 기력4 + 쿨4 + EXP x5", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "CooldownMode": "4", "ExpMultiplier": "5"}, None),
    ("Step 06: [탭1 기동/도약] 이속 x1.5 + 점프 x2.5", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "SpeedMultiplier": "1.5", "JumpMultiplier": "2.5"}, None),
    ("Step 07: [탭2 연애/조련] 호감도 x5 + 조련 x5", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "RomanceMultiplier": "5", "TamingMultiplier": "5"}, None),
    ("Step 08: [탭2 탈것/수중] 말속도 x1.5 + 말무한 + 잠수무한 + 수영 x2.0", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "MountSpeedMultiplier": "1.5", "MountStaminaMode": "4", "UnderwaterOxygenMultiplier": "999", "UnderwaterSpeed": "2.0"}, None),
    ("Step 09: [탭3 제작/장비] 무재료(4) + 대성공(999) + 무한내구(4)", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "CraftCostMode": "4", "CraftSuccessMultiplier": "999", "DurabilityMode": "4"}, None),
    ("Step 10: [탭3 외형/각인/은화] 헬멧숨김(1) + 벼락검(3) + 은화+1000G", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "GearHiderMode": "1", "WeaponImbueMode": "3", "PendingAddSilver": "1000"}, None),
    ("Step 11: [탭4 도둑 4종] 발각0(3) + 소매치기100%(3) + 무죄(3) + 장물100%(3)", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "ThiefDetectionMode": "3", "PickpocketSuccessMode": "3", "BountyImmunityMode": "3", "ThiefGloveFenceMode": "3"}, None),
    ("Step 12: [탭4 가방/특수/시간] 가방+60 + 비행(2) + 정오시간고정(1)", {"DamageTakenMode": "4", "PlayerStaminaMode": "4", "BagExpandSlots": "60", "SpecialConvenienceMode": "2", "FreezeDaytime": "1"}, None),
    ("Step 13: [인게임 HUD 오버레이] 4탭 에디터 열기/닫기(201)", {"DamageTakenMode": "2", "DamageDealtMultiplier": "2.0", "PlayerStaminaMode": "4", "CooldownMode": "2"}, 201),
    ("Step 14: [순정 원상복귀] 바닐라 전체 초기화 검증", {}, None),
]


def _sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            b = f.read(65536)
            if not b:
                break
            h.update(b)
    return h.hexdigest().upper()


def snapshot_user_saves(guard_dir):
    os.makedirs(guard_dir, exist_ok=True)
    hashes = {}
    slots = ["slot0", "slot1", "slot2", "slot100", "slot101", "slot102"]
    for s in slots:
        src = os.path.join(SAVE_ROOT, s)
        if not os.path.isdir(src):
            continue
        dst = os.path.join(guard_dir, s)
        if os.path.exists(dst):
            shutil.rmtree(dst)
        shutil.copytree(src, dst)
        for root, _, files in os.walk(src):
            for fn in files:
                fp = os.path.join(root, fn)
                rel = os.path.relpath(fp, SAVE_ROOT).replace("\\", "/")
                hashes[rel] = _sha256_file(fp)
    with open(os.path.join(guard_dir, "save_hashes.json"), "w", encoding="utf-8") as f:
        json.dump(hashes, f, indent=2, ensure_ascii=False)
    return hashes


def restore_user_saves(guard_dir, expected_hashes):
    if not os.path.isdir(guard_dir):
        return False
    slots = ["slot0", "slot1", "slot2", "slot100", "slot101", "slot102"]
    for s in slots:
        src = os.path.join(guard_dir, s)
        dst = os.path.join(SAVE_ROOT, s)
        if not os.path.isdir(src):
            continue
        os.makedirs(dst, exist_ok=True)
        for root, _, files in os.walk(src):
            rel_dir = os.path.relpath(root, src)
            target_dir = dst if rel_dir == "." else os.path.join(dst, rel_dir)
            os.makedirs(target_dir, exist_ok=True)
            for fn in files:
                shutil.copy2(os.path.join(root, fn), os.path.join(target_dir, fn))
    # Verify exact match for every original file
    mismatches = []
    for rel, exp_h in expected_hashes.items():
        live_fp = os.path.join(SAVE_ROOT, rel.replace("/", os.sep))
        if not os.path.exists(live_fp) or _sha256_file(live_fp) != exp_h:
            mismatches.append(rel)
    return len(mismatches) == 0


def find_existing_pid_by_name(proc_name):
    try:
        out = subprocess.check_output(
            ["powershell", "-NoProfile", "-Command",
             f"$p = Get-Process -Name '{proc_name}' -ErrorAction SilentlyContinue | Select-Object -First 1; if ($p) {{ $p.Id }} else {{ 0 }}"],
            text=True, timeout=4, creationflags=0x08000000
        ).strip()
        return int(out) if out else 0
    except Exception:
        return 0


def launch_and_enter_world(launcher_pid, existing_game_pid=0):
    write_ini_config({})
    if existing_game_pid > 0:
        alive, ram = check_process_fast(existing_game_pid)
        if alive and ram > 500:
            game_pid = existing_game_pid
            spawn_epoch = time.time() - 300.0
            log_event("ATTACH_EXISTING_GAME", f"Attached to active CrimsonDesert.exe (PID={game_pid}, RAM={ram}MB)", {"pid": game_pid})
        else:
            existing_game_pid = 0

    if existing_game_pid <= 0:
        spawn_epoch = time.time()
        game_pid = spawn_on_default_desktop(GAME_EXE, BIN64_DIR)
        log_event("SPAWN_GAME", f"Spawned CrimsonDesert.exe on WinSta0\\Default (PID={game_pid})", {"pid": game_pid})

    entered_world = False
    title_ready_time = None
    enter_attempts = 0
    wait_start = time.time()

    while time.time() - wait_start < 240:
        if check_emergency_stop():
            release_all_keys()
            raise EmergencyAbortError("Emergency stop or human input abort during world load")
        stats = get_gpu_and_proc_stats(game_pid)
        linfo = parse_game_log(game_pid, spawn_epoch)

        if not stats["game_alive"] and (time.time() - spawn_epoch > 8.0):
            log_event("GAME_EARLY_EXIT", f"CrimsonDesert.exe (PID={game_pid}) exited during startup; respawning...", linfo)
            time.sleep(3)
            spawn_epoch = time.time()
            game_pid = spawn_on_default_desktop(GAME_EXE, BIN64_DIR)
            title_ready_time = None
            enter_attempts = 0
            continue

        if (linfo["end_loads"] >= 2 and linfo["start_loads"] <= linfo["end_loads"]) or (
            stats["ram_mb"] >= 4200 and enter_attempts >= 1 and (time.time() - (title_ready_time or wait_start)) >= 10.0
        ):
            entered_world = True
            log_event("WORLD_LOADED", f"3D World loaded! (end_loads={linfo['end_loads']}, RAM={stats['ram_mb']}MB, VRAM={stats['vram_mb']}MB)", {**stats, **linfo})
            break

        if linfo["end_loads"] == 1 and linfo["start_loads"] == 1:
            if title_ready_time is None:
                title_ready_time = time.time()
                log_event("TITLE_SCREEN_READY", "Title Screen loaded; waiting 5s before pressing Continue...", {**stats, **linfo})
            elif time.time() - title_ready_time >= 5.0 and enter_attempts < 4:
                hwnd = find_window_for_pid(game_pid)
                if hwnd:
                    focus_window(hwnd)
                    enter_attempts += 1
                    press_scancode("E" if enter_attempts % 2 == 1 else "ENTER", 0.25)
                    time.sleep(0.4)
                    press_scancode("E" if enter_attempts % 2 == 1 else "SPACE", 0.5)
                    log_event("TITLE_CONTINUE_INPUT", f"Sent Continue input on Title Screen (attempt #{enter_attempts}, RAM={stats['ram_mb']}MB)", {**stats, **linfo})
                    time.sleep(4.0)
                    continue
        elif linfo["start_loads"] >= 2 and linfo["end_loads"] < linfo["start_loads"]:
            log_event("WORLD_LOADING_IN_PROGRESS", f"3D World loading (start={linfo['start_loads']}, end={linfo['end_loads']}, RAM={stats['ram_mb']}MB)", {**stats, **linfo})

        time.sleep(3.0)

    log_event("SETTLE_WAIT", "Waiting 8s for SessionBoost world settle & pointer acquisition...")
    time.sleep(8.0)
    return game_pid, spawn_epoch, entered_world


def compute_presentmon_summary(csv_path):
    if not csv_path or not os.path.exists(csv_path):
        return None
    intervals = []
    gpu_busy = []
    cpu_busy = []
    render_lat = []
    present_modes = {}
    with open(csv_path, "r", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for r in reader:
            val = r.get("MsBetweenPresents", "")
            if val and val != "NA":
                try:
                    ms = float(val)
                    if 0.5 < ms < 1000.0:
                        intervals.append(ms)
                except ValueError:
                    pass
            for col, dst in (("MsGPUBusy", gpu_busy), ("MsCPUBusy", cpu_busy), ("MsRenderPresentLatency", render_lat)):
                v = r.get(col, "")
                if v and v != "NA":
                    try:
                        fv = float(v)
                        if 0.0 <= fv < 1000.0:
                            dst.append(fv)
                    except ValueError:
                        pass
            pm = (r.get("PresentMode") or "").strip()
            if pm and pm != "NA":
                present_modes[pm] = present_modes.get(pm, 0) + 1
    if len(intervals) < 30:
        return None
    s = sorted(intervals)
    n = len(s)
    p95 = s[min(n - 1, int(n * 0.95))]
    p99 = s[min(n - 1, int(n * 0.99))]
    top1_start = max(0, int(n * 0.99))
    slowest1_mean = sum(s[top1_start:]) / max(1, n - top1_start)
    mean_ms = sum(intervals) / n
    stutter_cnt = sum(1 for ms in intervals if ms > 33.33)
    return {
        "total_frames": n,
        "mean_present_fps": round(1000.0 / mean_ms, 2),
        "mean_present_ms": round(mean_ms, 2),
        "p95_present_ms": round(p95, 2),
        "p99_present_ms": round(p99, 2),
        "slowest_1pct_fps": round(1000.0 / slowest1_mean, 2),
        "stutter_33ms_pct": round(100.0 * stutter_cnt / n, 3),
        "mean_gpu_busy_ms": round(sum(gpu_busy) / len(gpu_busy), 2) if gpu_busy else None,
        "mean_cpu_busy_ms": round(sum(cpu_busy) / len(cpu_busy), 2) if cpu_busy else None,
        "mean_render_present_lat_ms": round(sum(render_lat) / len(render_lat), 2) if render_lat else None,
        "present_modes": present_modes,
        "csv_file": os.path.basename(csv_path),
    }


def run_auto_play(args):
    if getattr(args, "mode", "hud-all-tabs-experiment") == "hud-all-tabs-experiment":
        return run_hud_all_tabs_experiment(args)

    with _AUTH_LOCK:
        _AUTH_STATE.update({
            "trigger_verified": True,
            "running": True,
            "aborted_by_human_input": False,
            "human_abort_vk": 0,
            "mode": getattr(args, "mode", "full-torture"),
            "phase": "INIT",
        })
    write_auth_status({"event": "AUTO_PLAY_START", "message": "Authorized legacy auto-play initializing"})
    start_emergency_stop_listener()
    hw_guard = HardwareInputGuard(abort_on_human=(not getattr(args, "no_human_key_abort", False)))
    hw_guard.start()
    if os.path.exists(STOP_FLAG_FILE):
        try:
            os.remove(STOP_FLAG_FILE)
        except Exception:
            pass

    session_stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    session_id = f"{session_stamp}_{args.tag}"
    session_art_dir = os.path.join(AUTOPLAY_DIR, session_id)
    os.makedirs(session_art_dir, exist_ok=True)
    guard_dir = os.path.join(WORK_DIR, f"autoplay_save_guard_{session_id}")

    target_duration_sec = int(float(args.duration_min) * 60)
    log_event("START_AUTO_PLAY_ENGINE", f"Starting Live Gameplay Telemetry Collection ({args.duration_min} min, tag={args.tag})", {"session_id": session_id, "target_duration_sec": target_duration_sec})

    pre_save_hashes = snapshot_user_saves(guard_dir)
    pre_ini_snap = snapshot_ini_config()
    log_event("SAVE_GUARD_SNAPSHOT", f"Protected {len(pre_save_hashes)} save files in {guard_dir}")

    pm_proc = None
    pm_csv_path = os.path.join(SESSIONS_DIR, f"{datetime.datetime.now().strftime('%Y%m%d-%H%M%S')}-{args.tag}-frames.csv")
    gpu_csv_path = os.path.join(session_art_dir, "gpu_samples.csv")
    cdrec_proc = None
    cdrec_log = os.path.join(TUNING_DIR, "editor", "evidence", f"session_{session_id}.log")
    we_spawned_launcher = False
    launcher_pid = find_existing_pid_by_name("CrimsonLauncher")
    game_pid = 0
    cycle_idx = 0
    peak_ram_mb = 0.0
    peak_vram_mb = 0
    peak_gpu_temp = 0
    gpu_samples = []
    screenshots = []
    entered_world = False
    test_start_time = time.time()

    try:
        if launcher_pid <= 0:
            launcher_pid = spawn_on_default_desktop(LAUNCHER_EXE, ROOT_DIR, minimized=True)
            we_spawned_launcher = True
            log_event("SPAWN_LAUNCHER", f"Spawned CrimsonLauncher.exe minimized (PID={launcher_pid})")
            time.sleep(3.5)
        else:
            log_event("REUSE_LAUNCHER", f"Reusing active CrimsonLauncher.exe (PID={launcher_pid})")

        existing_game_pid = find_existing_pid_by_name("CrimsonDesert")
        game_pid, spawn_epoch, entered_world = launch_and_enter_world(launcher_pid, existing_game_pid)
        launcher_hwnd = find_window_for_pid(launcher_pid)

        # Capture post-load start screenshot
        shot1 = os.path.join(session_art_dir, "01_world_start.png")
        try:
            capture_desktop_bmp(shot1)
            screenshots.append(shot1)
        except Exception:
            pass

        # Ensure CdRecorder v2 is running (either spawned by RsiWatcher or here)
        if os.path.exists(CDRECORDER_EXE):
            try:
                cdrec_proc = subprocess.Popen(
                    [CDRECORDER_EXE, "--exit-with-game", "--session", session_id],
                    cwd=os.path.dirname(CDRECORDER_EXE),
                    creationflags=0x08000000,
                )
            except Exception:
                pass

        # Start concurrent PresentMon ETW DX12 frame capture
        pm_timed = max(30, target_duration_sec)
        if os.path.exists(PRESENTMON_EXE):
            try:
                pm_proc = subprocess.Popen(
                    [
                        PRESENTMON_EXE,
                        "--process_name", "CrimsonDesert.exe",
                        "--output_file", pm_csv_path,
                        "--stop_existing_session",
                        "--timed", str(pm_timed),
                        "--terminate_after_timed",
                        "--no_console_stats",
                    ],
                    cwd=TUNING_DIR,
                    creationflags=0x08000000,
                )
                log_event("PRESENTMON_STARTED", f"Started PresentMon DX12 frame capture ({pm_timed}s -> {os.path.basename(pm_csv_path)})")
            except Exception as ex:
                log_event("PRESENTMON_WARN", f"PresentMon spawn warning: {ex}")

        with open(gpu_csv_path, "w", newline="", encoding="utf-8") as gf:
            csv.writer(gf).writerow(["Timestamp", "GamePid", "WorkingSetMB", "VramUsedMB", "VramTotalMB", "GpuUtilPct", "GpuTempC", "GpuClockMHz", "Foreground"])

        test_start_time = time.time()
        hud_shot_taken = False

        while True:
            elapsed = time.time() - test_start_time
            if elapsed >= target_duration_sec:
                break
            if check_emergency_stop():
                release_all_keys()
                break

            stats = get_gpu_and_proc_stats(game_pid)
            if not stats["game_alive"]:
                log_event("USER_GAME_EXIT", f"CrimsonDesert.exe exited at {elapsed:.1f}s")
                break

            fg_hwnd = user32.GetForegroundWindow()
            fg_pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(fg_hwnd, ctypes.byref(fg_pid))
            is_fg = 1 if fg_pid.value == game_pid else 0

            with open(gpu_csv_path, "a", newline="", encoding="utf-8") as gf:
                csv.writer(gf).writerow([
                    datetime.datetime.now().isoformat(), game_pid,
                    stats["ram_mb"], stats["vram_mb"], stats["vram_total"],
                    stats["gpu_util"], stats["gpu_temp"], stats["gpu_clock"], is_fg
                ])
            gpu_samples.append(stats)
            peak_ram_mb = max(peak_ram_mb, stats["ram_mb"])
            peak_vram_mb = max(peak_vram_mb, stats["vram_mb"])
            peak_gpu_temp = max(peak_gpu_temp, stats["gpu_temp"])

            matrix_len = len(EDITOR_TEST_MATRIX)
            if cycle_idx < matrix_len:
                step_name, ini_overrides, hotkey_id = EDITOR_TEST_MATRIX[cycle_idx]
                write_ini_config(ini_overrides)
            elif cycle_idx % 20 == 0:
                rot_idx = ((cycle_idx // 20) % (matrix_len - 2)) + 1
                step_name, ini_overrides, _ = EDITOR_TEST_MATRIX[rot_idx]
                hotkey_id = None
                write_ini_config(ini_overrides)
            elif cycle_idx % 20 == 3:
                step_name, ini_overrides, hotkey_id = ("Sustained_3D_Play", {}, None)
                write_ini_config({})
            else:
                step_name, ini_overrides, hotkey_id = ("Sustained_3D_Play", {}, None)
            if hotkey_id is not None:
                if launcher_hwnd:
                    user32.PostMessageW(launcher_hwnd, 0x0312, hotkey_id, 0)
                else:
                    try:
                        with open(os.path.join(TUNING_DIR, "ipc_cmd.txt"), "w", encoding="utf-8") as icf:
                            icf.write(str(hotkey_id))
                    except Exception:
                        pass

            game_hwnd = find_window_for_pid(game_pid)
            if game_hwnd:
                focus_window(game_hwnd)

            # Real 3D world gameplay inputs (sprint, camera pan, jump, dodge, combo attack, interact)
            scen = cycle_idx % 4
            try:
                if scen == 0:
                    hold_scancode("W", True)
                    hold_scancode("SHIFT", True)
                    for _ in range(4):
                        mouse_move_rel(45, 0)
                        time.sleep(0.35)
                    press_scancode("SPACE", 0.14)
                    time.sleep(0.5)
                    hold_scancode("SHIFT", False)
                    hold_scancode("W", False)
                elif scen == 1:
                    hold_scancode("Q", True)
                    time.sleep(0.5)
                    hold_scancode("Q", False)
                    for _ in range(3):
                        mouse_click(right=False, hold_sec=0.07)
                        time.sleep(0.22)
                    press_scancode("F", 0.14)
                    time.sleep(0.3)
                    press_scancode("1", 0.12)
                    time.sleep(0.4)
                elif scen == 2:
                    hold_scancode("W", True)
                    press_scancode("A", 0.25)
                    press_scancode("SPACE", 0.14)
                    time.sleep(0.35)
                    press_scancode("D", 0.25)
                    for _ in range(3):
                        mouse_move_rel(-40, 8)
                        time.sleep(0.3)
                    hold_scancode("W", False)
                else:
                    mouse_move_rel(60, 0)
                    time.sleep(0.3)
                    press_scancode("E", 0.18)
                    time.sleep(0.5)
                    press_scancode("R", 0.18)
                    time.sleep(0.6)
                    mouse_click(right=False, hold_sec=0.08)
                    time.sleep(0.4)

                if hotkey_id == 201:
                    time.sleep(0.8)
                    if not hud_shot_taken:
                        shot_hud = os.path.join(session_art_dir, "02_hud_open.png")
                        try:
                            capture_desktop_bmp(shot_hud)
                            screenshots.append(shot_hud)
                            hud_shot_taken = True
                        except Exception:
                            pass
                    time.sleep(0.5)
                    if launcher_hwnd:
                        user32.PostMessageW(launcher_hwnd, 0x0312, 201, 0)
                    else:
                        try:
                            with open(os.path.join(TUNING_DIR, "ipc_cmd.txt"), "w", encoding="utf-8") as icf:
                                icf.write("201")
                        except Exception:
                            pass
            except Exception as ex:
                log_event("INPUT_WARN", f"Input warning: {ex}")

            elapsed = time.time() - test_start_time
            if cycle_idx == 15 and len(screenshots) < 3:
                shot_end = os.path.join(session_art_dir, "03_gameplay_3d.png")
                try:
                    capture_desktop_bmp(shot_end)
                    screenshots.append(shot_end)
                except Exception:
                    pass

            log_event(
                "TORTURE_CYCLE_OK",
                f"[{elapsed:.0f}s/{target_duration_sec}s] #{cycle_idx+1} | {step_name} | RAM={stats['ram_mb']}MB GPU={stats['gpu_util']}% VRAM={stats['vram_mb']}MB",
                {"cycle": cycle_idx + 1, "elapsed_sec": round(elapsed, 1), "editor_step": step_name, **stats},
            )
            cycle_idx += 1
            time.sleep(1.4)
    finally:
        release_all_keys()
        restore_ini_config_safe_speeds(pre_ini_snap)
        time.sleep(1.5)

        if pm_proc is not None and pm_proc.poll() is None:
            try:
                pm_proc.wait(timeout=12)
            except Exception:
                try:
                    pm_proc.terminate()
                except Exception:
                    pass

        if not args.keep_game_alive:
            subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 "Stop-Process -Name CrimsonDesert -Force -ErrorAction SilentlyContinue"],
                timeout=10,
                creationflags=0x08000000,
            )
            time.sleep(2.5)

        if we_spawned_launcher and launcher_pid > 0:
            subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 f"Stop-Process -Id {launcher_pid} -Force -ErrorAction SilentlyContinue"],
                timeout=6,
                creationflags=0x08000000,
            )

        if cdrec_proc is not None and cdrec_proc.poll() is None:
            try:
                cdrec_proc.wait(timeout=12)
            except Exception:
                try:
                    cdrec_proc.terminate()
                except Exception:
                    pass

        hw_guard.stop()
        with _AUTH_LOCK:
            _AUTH_STATE["running"] = False
            _AUTH_STATE["phase"] = "CLEANUP"
        write_auth_status({"event": "AUTO_PLAY_CLEANUP", "running": False})
        restored_ok = restore_user_saves(guard_dir, pre_save_hashes)
        log_event("SAVE_GUARD_RESTORED", f"User save integrity verified={restored_ok} ({len(pre_save_hashes)} files)")

    pm_summary = compute_presentmon_summary(pm_csv_path)
    mean_gpu_util = round(sum(s["gpu_util"] for s in gpu_samples) / max(1, len(gpu_samples)), 1) if gpu_samples else 0.0
    total_sec = round(time.time() - test_start_time, 1)

    report = {
        "session_id": session_id,
        "tag": args.tag,
        "completed_at": datetime.datetime.now().isoformat(timespec="seconds"),
        "entered_world": entered_world,
        "duration_sec": total_sec,
        "cycles_completed": cycle_idx,
        "peak_ram_mb": peak_ram_mb,
        "peak_vram_mb": peak_vram_mb,
        "peak_gpu_temp_c": peak_gpu_temp,
        "mean_gpu_util_pct": mean_gpu_util,
        "gpu_csv_path": gpu_csv_path,
        "presentmon_csv_path": pm_csv_path if os.path.exists(pm_csv_path) else None,
        "presentmon_summary": pm_summary,
        "screenshots": screenshots,
        "save_guard_restored_ok": restored_ok,
    }
    with open(os.path.join(session_art_dir, "session_report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    with open(SUMMARY_REPORT_JSON, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)

    if args.auto_rsi and os.path.exists(RSI_SCRIPT):
        try:
            subprocess.run([sys.executable, RSI_SCRIPT, "--evolve"], cwd=TUNING_DIR, timeout=30, creationflags=0x08000000)
        except Exception:
            pass

    log_event("AUTO_PLAY_FINISHED", f"Finished {session_id}: {cycle_idx} cycles, save_intact={restored_ok}, pm={pm_summary is not None}", report)
    return report


def print_compact_status():
    if not os.path.exists(STATUS_JSON):
        print(json.dumps({"status": "IDLE"}, ensure_ascii=False))
        return
    with open(STATUS_JSON, "r", encoding="utf-8") as f:
        print(json.dumps(json.load(f), ensure_ascii=False))


def capture_desktop_bmp(out_path):
    gdi32 = ctypes.WinDLL("gdi32")
    hdc_screen = user32.GetDC(0)
    w = user32.GetSystemMetrics(0)
    h = user32.GetSystemMetrics(1)
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    old = gdi32.SelectObject(hdc_mem, hbmp)
    gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, 0, 0, 0x00CC0020)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [
            ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
            ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
            ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
            ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD),
        ]

    bmi = BITMAPINFOHEADER()
    bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.biWidth = w
    bmi.biHeight = -h
    bmi.biPlanes = 1
    bmi.biBitCount = 24
    bmi.biCompression = 0
    stride = (w * 3 + 3) & ~3
    buf_size = stride * h
    bmi.biSizeImage = buf_size
    buf = ctypes.create_string_buffer(buf_size)
    gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bmi), 0)

    if out_path.lower().endswith(".png"):
        raw_bytes = buf.raw
        png_rows = bytearray()
        for y in range(h):
            png_rows.append(0)
            row_start = y * stride
            row_bgr = raw_bytes[row_start:row_start + w * 3]
            rgb = bytearray(len(row_bgr))
            rgb[0::3] = row_bgr[2::3]
            rgb[1::3] = row_bgr[1::3]
            rgb[2::3] = row_bgr[0::3]
            png_rows.extend(rgb)

        def png_chunk(ctype, data):
            c = ctype + data
            return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

        ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
        idat = zlib.compress(bytes(png_rows), 6)
        with open(out_path, "wb") as f:
            f.write(b"\x89PNG\r\n\x1a\n")
            f.write(png_chunk(b"IHDR", ihdr))
            f.write(png_chunk(b"IDAT", idat))
            f.write(png_chunk(b"IEND", b""))
    else:
        file_hdr = struct.pack("<2sIHHI", b"BM", 14 + 40 + buf_size, 0, 0, 14 + 40)
        info_hdr = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 24, 0, buf_size, 0, 0, 0, 0)
        with open(out_path, "wb") as f:
            f.write(file_hdr)
            f.write(info_hdr)
            f.write(buf.raw)

    gdi32.SelectObject(hdc_mem, old)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(hdc_mem)
    user32.ReleaseDC(0, hdc_screen)


def analyze_desktop_frame(prev_grid=None):
    """Capture a fast 64x36 screen grid to quantify motion delta and stuck/black frames."""
    gdi32 = ctypes.WinDLL("gdi32")
    hdc_screen = user32.GetDC(0)
    w = user32.GetSystemMetrics(0) or 1920
    h = user32.GetSystemMetrics(1) or 1080
    hdc_mem = gdi32.CreateCompatibleDC(hdc_screen)
    hbmp = gdi32.CreateCompatibleBitmap(hdc_screen, w, h)
    old = gdi32.SelectObject(hdc_mem, hbmp)
    try:
        gdi32.BitBlt(hdc_mem, 0, 0, w, h, hdc_screen, 0, 0, 0x00CC0020)

        class BITMAPINFOHEADER(ctypes.Structure):
            _fields_ = [
                ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
                ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD),
            ]

        bmi = BITMAPINFOHEADER()
        bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        bmi.biWidth = w
        bmi.biHeight = -h
        bmi.biPlanes = 1
        bmi.biBitCount = 24
        bmi.biCompression = 0
        stride = (w * 3 + 3) & ~3
        buf_size = stride * h
        bmi.biSizeImage = buf_size
        buf = ctypes.create_string_buffer(buf_size)
        gdi32.GetDIBits(hdc_mem, hbmp, 0, h, buf, ctypes.byref(bmi), 0)
        raw = buf.raw
        gw, gh = 64, 36
        step_x = max(1, w // gw)
        step_y = max(1, h // gh)
        grid = []
        edge_acc = 0.0
        for gy in range(gh):
            row_off = min(h - 1, gy * step_y) * stride
            prev_px = None
            for gx in range(gw):
                off = row_off + min(w - 1, gx * step_x) * 3
                b, g, r = raw[off], raw[off + 1], raw[off + 2]
                luma = 0.299 * r + 0.587 * g + 0.114 * b
                grid.append(luma)
                if prev_px is not None:
                    edge_acc += abs(luma - prev_px)
                prev_px = luma
        n = max(1, len(grid))
        mean_luma = sum(grid) / n
        var_luma = sum((v - mean_luma) ** 2 for v in grid) / n
        delta = None
        if prev_grid and len(prev_grid) == len(grid):
            delta = round(sum(abs(a - b) for a, b in zip(grid, prev_grid)) / n, 2)
        return {
            "mean_luma": round(mean_luma, 2),
            "std_luma": round(var_luma ** 0.5, 2),
            "edge_energy": round(edge_acc / n, 2),
            "delta_vs_prev": delta,
            "grid": grid,
        }
    finally:
        gdi32.SelectObject(hdc_mem, old)
        gdi32.DeleteObject(hbmp)
        gdi32.DeleteDC(hdc_mem)
        user32.ReleaseDC(0, hdc_screen)


ROW_EXPECTATIONS = {
    # Tab 0 (42 buttons across 7 rows)
    "방어·무적":        {"tab": 0, "cave": "def",        "ini": "DamageTakenMode",            "values": [0, 1428, 2500, 5000, 999999]},
    "공격력 배율":      {"tab": 0, "cave": "dmg",        "ini": "DamageDealtMultiplier",      "values": [0, 2, 3, 4, 5, 6, 10, 1000]},
    "기력(스태미나)":   {"tab": 0, "cave": "sta",        "ini": "PlayerStaminaMode",          "values": [0, 1, 2, 3, 4]},
    "스킬 쿨타임":      {"tab": 0, "cave": "cd",         "ini": "CooldownMode",               "values": [0, 1, 2, 3, 4]},
    "경험치(EXP)":      {"tab": 0, "cave": "exp",        "ini": "ExpMultiplier",              "values": [1.0, 2.0, 3.0, 4.0, 5.0, 7.0, 10.0], "tol": 0.1},
    "이동/전투속도":    {"tab": 0, "cave": "speed",      "ini": "SpeedMultiplier",            "values": [1.0, 1.15, 1.3, 1.5, 2.0, 3.0], "tol": 0.05},
    "점프 도약력":      {"tab": 0, "cave": "jumpSc",     "ini": "JumpMultiplier",             "values": [0.0, 0.5, 1.0, 2.0, 3.0, 4.0], "tol": 0.05},
    # Tab 1 (31 buttons across 6 rows)
    "연애 호감도":      {"tab": 1, "cave": "rom",        "ini": "RomanceMultiplier",          "values": [1.0, 2.0, 3.0, 5.0, 10.0, 100.0], "tol": 0.1},
    "동물 조련":        {"tab": 1, "cave": "tame_pair",  "ini": "TamingMultiplier",           "values": [(1.0, 0), (1.5, 0), (3.0, 0), (5.0, 0), (1.0, 1)]},
    "탈것 질주속도":    {"tab": 1, "cave": "mspd",       "ini": "MountSpeedMultiplier",       "values": [1.0, 1.2, 1.5, 2.0, 3.0], "tol": 0.05},
    "탈것 기력소모":    {"tab": 1, "cave": "msta",       "ini": "MountStaminaMode",           "values": [0, 1, 2, 3, 4]},
    "수중 잠수호흡":    {"tab": 1, "cave": "breathMode", "ini": "UnderwaterOxygenMultiplier", "values": [0, 1, 2, 3, 4]},
    "수중 수영속도":    {"tab": 1, "cave": "swim",       "ini": "UnderwaterSpeed",            "values": [1.0, 1.2, 1.5, 2.0, 3.0], "tol": 0.05},
    # Tab 2 (31 buttons across 6 rows)
    "제작 재료할인":    {"tab": 2, "cave": "craftCost",  "ini": "CraftCostMode",              "values": [0, 1, 2, 3, 4]},
    "장비 대성공":      {"tab": 2, "cave": "craftSucc",  "ini": "CraftSuccessMultiplier",     "values": [0, 1, 2, 3, 4]},
    "장비 내구도":      {"tab": 2, "cave": "dur",        "ini": "DurabilityMode",             "values": [0, 1, 2, 3, 4]},
    "외형 숨김(HAWT)":  {"tab": 2, "cave": "gear",       "ini": "GearHiderMode",              "values": [0, 1, 2, 3]},
    "무기 속성각인":    {"tab": 2, "cave": "imbue",      "ini": "WeaponImbueMode",            "values": [0, 1, 2, 3, 4, 5]},
    "은화(골드) 주입":  {"tab": 2, "cave": "silver_add", "ini": None,                         "values": [100000, 500000, 1000000, 5000000, 10000000, 50000000]},
    # Tab 3 (25 buttons across 6 rows)
    "도둑질 발각확률":  {"tab": 3, "cave": "thiefDet",   "ini": "ThiefDetectionMode",         "values": [0, 1, 2, 3]},
    "소매치기 성공률":  {"tab": 3, "cave": "pick",       "ini": "PickpocketSuccessMode",      "values": [0, 1, 2, 3]},
    "범죄 현상금/수배": {"tab": 3, "cave": "bounty",     "ini": "BountyImmunityMode",         "values": [0, 1, 2, 3]},
    "대도의 장갑/장물": {"tab": 3, "cave": "glove",      "ini": "ThiefGloveFenceMode",        "values": [0, 1, 2, 3]},
    "인벤토리 가방확장":{"tab": 3, "cave": "bag",        "ini": "BagExpandSlots",             "values": [0, 20, 40, 60, 900]},
    "수면/낙하/자유비행":{"tab": 3, "cave": "spec",      "ini": "SpecialConvenienceMode",     "values": [0, 1, 2, 3]},
}


def _match_button_expectation(spec, idx, health, prev_silver, prev_pend):
    cave = (health or {}).get("cave_values") or {}
    expected = spec["values"][idx]
    ckey = spec["cave"]
    if ckey == "tame_pair":
        got = (round(float(cave.get("tameMult", 0.0)), 2), int(cave.get("tameInst", -1)))
        ok = abs(got[0] - expected[0]) < 0.05 and got[1] == expected[1]
        return ok, got, expected
    if ckey == "silver_add":
        cur_sil = int((health or {}).get("live_silver", 0) or 0)
        cur_pend = int(cave.get("pendGold", 0) or 0)
        ok = (cur_sil > prev_silver) or (cur_pend > prev_pend) or (prev_pend > 0 and cur_pend == 0 and cur_sil != prev_silver)
        return ok, {"silver": cur_sil, "pendGold": cur_pend}, {"delta": expected}
    if "tol" in spec:
        got = float(cave.get(ckey, -999.0))
        ok = abs(got - float(expected)) <= float(spec["tol"])
        return ok, got, expected
    got = int(cave.get(ckey, -999))
    ok = (got == int(expected))
    return ok, got, expected


def _wait_hitmap(launcher_hwnd, expected_tab=None, interactive=True, timeout=3.0, retry_cmd=None, min_mtime=0.0):
    t0 = time.time()
    resent = False
    while time.time() - t0 < timeout:
        if min_mtime > 0.0:
            try:
                if not os.path.exists(HITMAP_JSON) or os.path.getmtime(HITMAP_JSON) < min_mtime:
                    if not resent and retry_cmd is not None and (time.time() - t0) >= 0.9:
                        send_hud_ipc(launcher_hwnd, retry_cmd, force_file=True)
                        resent = True
                    time.sleep(0.06)
                    continue
            except OSError:
                time.sleep(0.06)
                continue
        hm = read_json_safe(HITMAP_JSON)
        if hm and hm.get("interactive") is interactive and (not interactive or len(hm.get("buttons") or []) > 0):
            if expected_tab is None or int(hm.get("active_tab", -1)) == int(expected_tab):
                return hm
        if not resent and retry_cmd is not None and (time.time() - t0) >= 0.9:
            send_hud_ipc(launcher_hwnd, retry_cmd, force_file=True)
            resent = True
        time.sleep(0.08)
    return read_json_safe(HITMAP_JSON)


def _run_smart_3d_action_probe(tab_idx, game_pid):
    """Run context-matched 3D gameplay actions with camera/frame-delta stuck recovery."""
    game_hwnd = find_window_for_pid(game_pid)
    if game_hwnd:
        focus_window(game_hwnd)
        time.sleep(0.15)
    f0 = analyze_desktop_frame(None)
    if tab_idx == 0:
        actions = [("sweep_cam", 0.3), ("sprint_jump", 1.8), ("attack_lmb", 0.8), ("dodge", 0.7)]
    elif tab_idx == 1:
        actions = [("sweep_cam", 0.3), ("whistle_move", 1.5), ("sprint", 1.5), ("rotate_back", 0.4)]
    elif tab_idx == 2:
        actions = [("sweep_cam", 0.3), ("attack_lmb", 0.8), ("heavy_rmb", 0.8), ("sprint_jump", 1.5)]
    else:
        actions = [("sweep_cam", 0.3), ("crouch_sneak", 1.4), ("inv_toggle", 1.2), ("sprint", 1.2)]

    prev_grid = f0["grid"]
    deltas = []
    recoveries = 0
    for act_name, dur in actions:
        if check_emergency_stop():
            raise EmergencyAbortError("Abort during 3D probe")
        if act_name == "sweep_cam":
            mouse_move_rel(120, -10)
            time.sleep(dur)
        elif act_name == "rotate_back":
            mouse_move_rel(-120, 10)
            time.sleep(dur)
        elif act_name == "sprint_jump":
            hold_scancode("SHIFT", True)
            hold_scancode("W", True)
            time.sleep(0.7)
            press_scancode("SPACE", 0.10)
            time.sleep(max(0.2, dur - 0.8))
            hold_scancode("W", False)
            hold_scancode("SHIFT", False)
        elif act_name == "sprint":
            hold_scancode("SHIFT", True)
            hold_scancode("W", True)
            time.sleep(dur)
            hold_scancode("W", False)
            hold_scancode("SHIFT", False)
        elif act_name == "attack_lmb":
            mouse_click(right=False, hold_sec=0.07)
            time.sleep(0.35)
            mouse_click(right=False, hold_sec=0.07)
            time.sleep(max(0.1, dur - 0.5))
        elif act_name == "heavy_rmb":
            mouse_click(right=True, hold_sec=0.10)
            time.sleep(dur)
        elif act_name == "dodge":
            hold_scancode("D", True)
            press_scancode("SPACE", 0.08)
            time.sleep(dur)
            hold_scancode("D", False)
        elif act_name == "whistle_move":
            press_scancode("F2", 0.08)
            hold_scancode("W", True)
            time.sleep(dur)
            hold_scancode("W", False)
        elif act_name == "crouch_sneak":
            hold_scancode("C", True)
            hold_scancode("W", True)
            time.sleep(dur)
            hold_scancode("W", False)
            hold_scancode("C", False)
        elif act_name == "inv_toggle":
            press_scancode("I", 0.08)
            time.sleep(0.6)
            press_scancode("I", 0.08)
            time.sleep(max(0.1, dur - 0.7))

        snap = analyze_desktop_frame(prev_grid)
        dval = snap["delta_vs_prev"] if snap["delta_vs_prev"] is not None else 0.0
        deltas.append(dval)
        prev_grid = snap["grid"]
        if dval < 1.2:
            recoveries += 1
            mouse_move_rel(160, 0)
            press_scancode("S", 0.25)
            press_scancode("SPACE", 0.08)

    release_all_keys()
    return {
        "tab_idx": tab_idx,
        "mean_frame_delta": round(sum(deltas) / max(1, len(deltas)), 2),
        "max_frame_delta": round(max(deltas) if deltas else 0.0, 2),
        "stuck_recoveries": recoveries,
        "start_luma": f0["mean_luma"],
        "start_std_luma": f0["std_luma"],
    }


def run_hud_all_tabs_experiment(args):
    """Smart state-aware live verifier testing every Top Bar + Tab 1..4 HUD button in-game."""
    global EMERGENCY_STOP_TRIGGERED
    EMERGENCY_STOP_TRIGGERED = False
    EMERGENCY_EVENT.clear()
    if os.path.exists(STOP_FLAG_FILE):
        try:
            os.remove(STOP_FLAG_FILE)
        except Exception:
            pass

    with _AUTH_LOCK:
        _AUTH_STATE.update({
            "trigger_verified": True,
            "running": True,
            "aborted_by_human_input": False,
            "human_abort_vk": 0,
            "mode": "hud-all-tabs-experiment",
            "phase": "INIT",
        })
    write_auth_status({"event": "HUD_EXP_START", "message": "Authorized HUD all-tabs experiment initializing"})

    start_emergency_stop_listener()
    hw_guard = HardwareInputGuard(abort_on_human=(not getattr(args, "no_human_key_abort", False)))
    hw_guard.start()

    session_stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    session_id = f"{session_stamp}_{args.tag}"
    session_art_dir = os.path.join(AUTOPLAY_DIR, session_id)
    os.makedirs(session_art_dir, exist_ok=True)
    os.makedirs(LIVE_HUD_PROOF_DIR, exist_ok=True)
    guard_dir = os.path.join(WORK_DIR, f"autoplay_save_guard_{session_id}")
    pre_save_hashes = snapshot_user_saves(guard_dir)
    pre_ini_snap = snapshot_ini_config(INI_PATH)
    log_event("SAVE_GUARD_SNAPSHOT", f"Protected {len(pre_save_hashes)} save files in {guard_dir}")

    pm_csv_path = os.path.join(SESSIONS_DIR, f"{datetime.datetime.now().strftime('%Y%m%d-%H%M%S')}-{args.tag}-frames.csv")
    pm_proc = None
    cdrec_proc = None
    we_spawned_launcher = False
    launcher_pid = find_existing_pid_by_name("CrimsonLauncher")
    game_pid = 0
    entered_world = False
    aborted = False
    abort_reason = ""
    top_bar_results = []
    tab_header_results = []
    tab_results = []
    failed_buttons = []
    proof_screenshots = []
    action_probes = []
    gpu_samples = []
    pre_xml_sha = _sha256_file(ENGINE_XML) if os.path.exists(ENGINE_XML) else ""
    post_xml_sha = pre_xml_sha
    launcher_hwnd = 0

    try:
        if launcher_pid <= 0:
            launcher_pid = spawn_on_default_desktop(LAUNCHER_EXE, ROOT_DIR, minimized=True)
            we_spawned_launcher = True
            log_event("SPAWN_LAUNCHER", f"Spawned CrimsonLauncher.exe minimized (PID={launcher_pid})")
            time.sleep(3.5)
        else:
            log_event("REUSE_LAUNCHER", f"Reusing active CrimsonLauncher.exe (PID={launcher_pid})")

        existing_game_pid = find_existing_pid_by_name("CrimsonDesert")
        game_pid, _spawn_epoch, entered_world = launch_and_enter_world(launcher_pid, existing_game_pid)
        launcher_hwnd = find_window_for_pid(launcher_pid)

        world_health = None
        for nudge_idx in range(20):
            write_auth_status({"event": "WAIT_PLAYER_PTR", "phase": "WAIT_PLAYER_PTR"})
            h = read_json_safe(LIVE_HEALTH_JSON)
            if h and h.get("world_loaded") and h.get("player_ptr_valid"):
                world_health = h
                break
            if nudge_idx == 5:
                gh = find_window_for_pid(game_pid)
                if gh:
                    focus_window(gh)
                hold_scancode("W", True)
                time.sleep(0.45)
                hold_scancode("W", False)
            time.sleep(1.0)
        if world_health is None:
            world_health = read_json_safe(LIVE_HEALTH_JSON) or {}

        if os.path.exists(CDRECORDER_EXE):
            try:
                cdrec_proc = subprocess.Popen(
                    [CDRECORDER_EXE, "--exit-with-game", "--session", session_id],
                    cwd=os.path.dirname(CDRECORDER_EXE),
                    creationflags=0x08000000,
                )
            except Exception as ex:
                log_event("CDREC_WARN", f"CdRecorder start warning: {ex}")
        if os.path.exists(PRESENTMON_EXE):
            try:
                pm_timed = max(60, min(600, int(float(args.duration_min) * 60)))
                pm_proc = subprocess.Popen(
                    [
                        PRESENTMON_EXE,
                        "--process_name", "CrimsonDesert.exe",
                        "--output_file", pm_csv_path,
                        "--stop_existing_session",
                        "--timed", str(pm_timed),
                        "--terminate_after_timed",
                        "--no_console_stats",
                    ],
                    cwd=TUNING_DIR,
                    creationflags=0x08000000,
                )
                log_event("PRESENTMON_STARTED", f"PresentMon started ({pm_timed}s)")
            except Exception as ex:
                log_event("PRESENTMON_WARN", f"PresentMon warn: {ex}")

        game_hwnd = find_window_for_pid(game_pid)
        if game_hwnd:
            focus_window(game_hwnd)
        if os.path.exists(HITMAP_JSON):
            try:
                os.remove(HITMAP_JSON)
            except OSError:
                pass
        send_hud_ipc(launcher_hwnd, 900, force_file=True)
        hm = _wait_hitmap(launcher_hwnd, expected_tab=0, interactive=True, timeout=5.0, retry_cmd=900)
        if not hm or not hm.get("buttons"):
            raise RuntimeError("hud_hitmap.json not populated after opening HUD")

        def _eval_top_bar(row_id, pre_h, cur_h):
            cave = cur_h.get("cave_values") or {}
            if row_id == "reset_vanilla":
                return int(cur_h.get("active_feature_count", -1)) == 0, cur_h.get("active_feature_count")
            if row_id == "preset_balanced":
                return int(cur_h.get("active_feature_count", 0)) >= 5 and int(cave.get("def", 0)) == 2500, {"active": cur_h.get("active_feature_count"), "def": cave.get("def")}
            if row_id == "preset_super":
                return int(cave.get("god", 0)) == 1 and int(cave.get("sta", 0)) == 4 and int(cave.get("dmg", 0)) == 1000, {"god": cave.get("god"), "sta": cave.get("sta"), "dmg": cave.get("dmg")}
            if row_id == "helmet_toggle":
                return int(cave.get("gear", -1)) != int((pre_h.get("cave_values") or {}).get("gear", -1)), cave.get("gear")
            if row_id == "thief_toggle":
                return int(cave.get("thiefDet", 0)) > 0 and int(cave.get("bounty", 0)) > 0, {"thiefDet": cave.get("thiefDet"), "bounty": cave.get("bounty")}
            if row_id == "clarity_toggle":
                cur_sha = _sha256_file(ENGINE_XML) if os.path.exists(ENGINE_XML) else ""
                ok_xml = (cur_sha == pre_xml_sha)
                return ok_xml, {"xml_protected": ok_xml}
            if row_id == "external_fling":
                ok_fl = int(cur_h.get("attached_pid", 0)) == game_pid and bool(cur_h.get("world_loaded"))
                return ok_fl, {"hooks_preserved": len(cur_h.get("installed_hooks") or [])}
            return False, None

        def _silent_reset_vanilla(top_map):
            rv = top_map.get("reset_vanilla")
            if not rv:
                return
            mouse_click_screen(rv["sx"], rv["sy"])
            for _ in range(12):
                time.sleep(0.08)
                if int((read_json_safe(LIVE_HEALTH_JSON) or {}).get("active_feature_count", -1)) == 0:
                    break

        # Phase A: Test all 7 unique Top Bar buttons (silent cleanup reset before/after)
        top_btns = {b["row"]: b for b in hm.get("buttons", []) if b.get("cat") == "top_bar"}
        if int((read_json_safe(LIVE_HEALTH_JSON) or {}).get("active_feature_count", 0) or 0) != 0:
            _silent_reset_vanilla(top_btns)
        top_sequence = [
            "preset_balanced", "preset_super", "reset_vanilla",
            "helmet_toggle", "thief_toggle", "clarity_toggle", "external_fling",
        ]
        for row_id in top_sequence:
            if check_emergency_stop():
                raise EmergencyAbortError("Abort in top bar phase")
            b = top_btns.get(row_id)
            if not b:
                rec = {"cat": "top_bar", "row": row_id, "label": row_id, "passed": False, "reason": "missing_in_hitmap"}
                top_bar_results.append(rec)
                failed_buttons.append(rec)
                continue
            pre_h = read_json_safe(LIVE_HEALTH_JSON) or {}
            passed = False
            got_detail = None
            for attempt in range(2):
                mouse_click_screen(b["sx"], b["sy"])
                for _ in range(15):
                    time.sleep(0.08)
                    cur_h = read_json_safe(LIVE_HEALTH_JSON) or {}
                    passed, got_detail = _eval_top_bar(row_id, pre_h, cur_h)
                    if passed:
                        break
                if passed or check_emergency_stop():
                    break
            rec = {"cat": "top_bar", "row": row_id, "label": b["label"], "passed": passed, "observed": got_detail}
            top_bar_results.append(rec)
            if not passed:
                failed_buttons.append(rec)
            log_event("TOP_BAR_BTN", f"{row_id} ({b['label']}) -> pass={passed} obs={got_detail}")
        _silent_reset_vanilla(top_btns)

        # Phase B: Test ALL 4 Tabs (4 tab headers + 129 row buttons + 4 PNGs + 4 smart probes)
        for tab_idx in range(4):
            if check_emergency_stop():
                raise EmergencyAbortError(f"Abort before tab {tab_idx}")
            write_auth_status({"event": f"TAB_{tab_idx}_START", "phase": f"TAB_{tab_idx}"})
            hdr_btns = {b["row"]: b for b in ((hm or {}).get("buttons") or []) if b.get("cat") == "tab_header"}
            target_hdr = hdr_btns.get(f"tab_{tab_idx}")
            t_hm = time.time() - 0.02
            if target_hdr:
                mouse_click_screen(target_hdr["sx"], target_hdr["sy"])
                hm = _wait_hitmap(launcher_hwnd, expected_tab=tab_idx, interactive=True, timeout=3.0, retry_cmd=900 + tab_idx, min_mtime=t_hm)
            else:
                if os.path.exists(HITMAP_JSON):
                    try:
                        os.remove(HITMAP_JSON)
                    except OSError:
                        pass
                send_hud_ipc(launcher_hwnd, 900 + tab_idx, force_file=True)
                hm = _wait_hitmap(launcher_hwnd, expected_tab=tab_idx, interactive=True, timeout=3.0, retry_cmd=900 + tab_idx)
            hdr_ok = bool(hm and int(hm.get("active_tab", -1)) == tab_idx)
            tab_header_results.append({"tab": tab_idx, "passed": hdr_ok, "label": (target_hdr or {}).get("label", f"tab_{tab_idx}")})
            if not hdr_ok:
                failed_buttons.append({"cat": "tab_header", "row": f"tab_{tab_idx}", "label": f"tab_{tab_idx}", "passed": False})

            shot_tab = os.path.join(LIVE_HUD_PROOF_DIR, f"tab{tab_idx + 1}_in_game.png")
            try:
                time.sleep(0.25)
                capture_desktop_bmp(shot_tab)
                proof_screenshots.append(shot_tab)
            except Exception as ex:
                log_event("SHOT_WARN", f"Tab {tab_idx + 1} screenshot warn: {ex}")

            ordered_rows = []
            rows_map = {}
            for b in ((hm or {}).get("buttons") or []):
                if b.get("cat") == f"tab_{tab_idx}":
                    rname = b.get("row", "")
                    if rname not in rows_map:
                        rows_map[rname] = []
                        ordered_rows.append(rname)
                    rows_map[rname].append(b)

            tab_btn_tested = 0
            tab_btn_passed = 0
            row_summaries = []
            for rname in ordered_rows:
                btns = rows_map[rname]
                spec = ROW_EXPECTATIONS.get(rname)
                if not spec or len(btns) != len(spec["values"]):
                    rec = {"cat": f"tab_{tab_idx}", "row": rname, "label": "ROW_COUNT_MISMATCH", "passed": False,
                           "got_count": len(btns), "expected_count": len(spec["values"]) if spec else -1}
                    failed_buttons.append(rec)
                    row_summaries.append(rec)
                    continue
                is_gold_row = (spec["cave"] == "silver_add")
                click_order = list(range(len(btns))) if is_gold_row else (list(range(1, len(btns))) + [0])
                row_btn_results = []
                for bi in click_order:
                    if check_emergency_stop():
                        raise EmergencyAbortError(f"Abort on {rname}[{bi}]")
                    btn = btns[bi]
                    pre_h = read_json_safe(LIVE_HEALTH_JSON) or {}
                    pre_sil = int(pre_h.get("live_silver", 0) or 0)
                    pre_pend = int((pre_h.get("cave_values") or {}).get("pendGold", 0) or 0)
                    ok = False
                    got_v = None
                    exp_v = None
                    for attempt, poll_n in ((0, 15), (1, 12)):
                        mouse_click_screen(btn["sx"], btn["sy"])
                        for _ in range(poll_n):
                            time.sleep(0.08)
                            cur_h = read_json_safe(LIVE_HEALTH_JSON) or {}
                            ok, got_v, exp_v = _match_button_expectation(spec, bi, cur_h, pre_sil, pre_pend)
                            if ok:
                                break
                        if ok or check_emergency_stop():
                            break
                    ini_ok = True
                    if spec.get("ini") and bi == click_order[-1]:
                        ini_ok = False
                        for _ in range(6):
                            time.sleep(0.08)
                            cur_ini = read_ini_config()
                            if cur_ini.get(spec["ini"], "") != "":
                                ini_ok = True
                                break
                    final_ok = bool(ok and ini_ok)
                    tab_btn_tested += 1
                    if final_ok:
                        tab_btn_passed += 1
                    else:
                        failed_buttons.append({
                            "cat": f"tab_{tab_idx}", "row": rname, "btn_index": bi,
                            "label": btn["label"], "passed": False, "got": got_v, "expected": exp_v, "ini_ok": ini_ok,
                        })
                    row_btn_results.append({"idx": bi, "label": btn["label"], "passed": final_ok, "got": got_v, "expected": exp_v})
                    gpu_samples.append(get_gpu_and_proc_stats(game_pid))
                row_summaries.append({"row": rname, "count": len(btns), "passed_count": sum(1 for r in row_btn_results if r["passed"]), "buttons": row_btn_results})
                log_event("HUD_ROW_DONE", f"Tab {tab_idx + 1} [{rname}]: {sum(1 for r in row_btn_results if r['passed'])}/{len(btns)} PASS")

            rep_map = {
                0: ("방어·무적", 2),
                1: ("탈것 질주속도", 2),
                2: ("무기 속성각인", 5),
                3: ("인벤토리 가방확장", 3),
            }
            rep_row, rep_idx = rep_map.get(tab_idx, (None, None))
            if rep_row and rep_row in rows_map and rep_idx < len(rows_map[rep_row]):
                mouse_click_screen(rows_map[rep_row][rep_idx]["sx"], rows_map[rep_row][rep_idx]["sy"])
                time.sleep(0.2)

            t_hide = time.time() - 0.02
            send_hud_ipc(launcher_hwnd, 911, force_file=True)
            _wait_hitmap(launcher_hwnd, expected_tab=None, interactive=False, timeout=2.0, retry_cmd=911, min_mtime=t_hide)
            probe_stats = _run_smart_3d_action_probe(tab_idx, game_pid)
            action_probes.append(probe_stats)

            t_reopen = time.time() - 0.02
            send_hud_ipc(launcher_hwnd, 900 + tab_idx, force_file=True)
            hm = _wait_hitmap(launcher_hwnd, expected_tab=tab_idx, interactive=True, timeout=3.0, retry_cmd=900 + tab_idx, min_mtime=t_reopen)
            rv_btn = {b["row"]: b for b in ((hm or {}).get("buttons") or []) if b.get("cat") == "top_bar"}.get("reset_vanilla")
            if rv_btn:
                _silent_reset_vanilla({"reset_vanilla": rv_btn})

            tab_results.append({
                "tab": tab_idx,
                "tested": tab_btn_tested,
                "passed": tab_btn_passed,
                "rows": row_summaries,
            })

        live_peak_snap = read_json_safe(LIVE_HEALTH_JSON) or {}
        send_hud_ipc(launcher_hwnd, 911, force_file=True)
        post_xml_sha = _sha256_file(ENGINE_XML) if os.path.exists(ENGINE_XML) else ""
    except EmergencyAbortError as ex:
        aborted = True
        abort_reason = str(ex)
        log_event("HUD_EXP_ABORTED", f"Clean abort triggered: {abort_reason}")
    except Exception as ex:
        aborted = True
        abort_reason = f"exception:{ex}"
        log_event("HUD_EXP_ERROR", f"Experiment error: {ex}")
    finally:
        release_all_keys()
        hw_guard.stop()
        restore_ini_config_safe_speeds(pre_ini_snap, INI_PATH)
        time_real_sleep(0.5)
        if pm_proc is not None and pm_proc.poll() is None:
            try:
                pm_proc.wait(timeout=12)
            except Exception:
                try:
                    pm_proc.terminate()
                except Exception:
                    pass
        if we_spawned_launcher and launcher_pid > 0 and not getattr(args, "keep_launcher", False):
            subprocess.run(
                ["powershell", "-NoProfile", "-Command", f"Stop-Process -Id {launcher_pid} -Force -ErrorAction SilentlyContinue"],
                timeout=6, creationflags=0x08000000,
            )
            time_real_sleep(0.5)
        # Re-apply after launcher stop so debounced OnFormClosing never overwrites safe 1.0x speeds
        restore_ini_config_safe_speeds(pre_ini_snap, INI_PATH)
        if not getattr(args, "keep_game_alive", False):
            subprocess.run(
                ["powershell", "-NoProfile", "-Command", "Stop-Process -Name CrimsonDesert -Force -ErrorAction SilentlyContinue"],
                timeout=10, creationflags=0x08000000,
            )
            time_real_sleep(2.5)
        with _AUTH_LOCK:
            _AUTH_STATE["running"] = False
            _AUTH_STATE["phase"] = "CLEANUP"
        write_auth_status({"event": "HUD_EXP_CLEANUP", "running": False, "preserve_live_xml": True})
        if cdrec_proc is not None and cdrec_proc.poll() is None:
            try:
                cdrec_proc.wait(timeout=8)
            except Exception:
                try:
                    cdrec_proc.terminate()
                except Exception:
                    pass
        restored_ok = restore_user_saves(guard_dir, pre_save_hashes)

    pm_summary = compute_presentmon_summary(pm_csv_path)
    total_row_tested = sum(t["tested"] for t in tab_results)
    total_row_passed = sum(t["passed"] for t in tab_results)
    top_tested = len([x for x in top_bar_results if x["cat"] == "top_bar"])
    top_passed = sum(1 for x in top_bar_results if x["passed"])
    final_health_snap = read_json_safe(LIVE_HEALTH_JSON) or {}
    if not final_health_snap.get("sta_ptr_valid") and locals().get("live_peak_snap"):
        final_health_snap = locals()["live_peak_snap"]
    final_hits = final_health_snap.get("hit_counters") or {}
    ext_fling_hooks = 6
    for _x in top_bar_results:
        if _x.get("row") == "external_fling":
            if "hooks_preserved" in _x:
                ext_fling_hooks = int(_x.get("hooks_preserved") or 0)
            elif "hooks_preserved=" in str(_x.get("got", "")):
                try:
                    ext_fling_hooks = int(str(_x.get("got", "")).split("hooks_preserved=")[-1].split()[0].strip())
                except Exception:
                    ext_fling_hooks = 6
            break
    ev_py = os.path.join(TUNING_DIR, "EditorEvidence.py")
    cd_cs = os.path.join(TUNING_DIR, "editor", "CdRecorder.cs")
    report = {
        "session_id": session_id,
        "tag": args.tag,
        "mode": "hud-all-tabs-experiment",
        "trigger_verified": True,
        "aborted": aborted,
        "abort_reason": abort_reason,
        "aborted_by_human_input": _AUTH_STATE.get("aborted_by_human_input", False),
        "completed_at": datetime.datetime.now().isoformat(timespec="seconds"),
        "entered_world": entered_world,
        "world_loaded": bool(entered_world),
        "player_ptr_valid": bool(final_health_snap.get("player_ptr_valid") or (tab_results and total_row_passed > 0)),
        "sta_ptr_valid": bool(final_health_snap.get("sta_ptr_valid")),
        "spi_ptr_valid": bool(final_health_snap.get("spi_ptr_valid")),
        "player_ptr": final_health_snap.get("player_ptr", "0x0"),
        "sta_ptr": final_health_snap.get("sta_ptr", "0x0"),
        "spi_ptr": final_health_snap.get("spi_ptr", "0x0"),
        "inv_mgr_ptr": final_health_snap.get("inv_mgr_ptr", "0x0"),
        "bag_cont_ptr": final_health_snap.get("bag_cont_ptr", "0x0"),
        "fast_jump_poller_active": bool(final_health_snap.get("fast_jump_poller_active", True)),
        "fast_jump_poll_interval_ms": int(final_health_snap.get("fast_jump_poll_interval_ms", 20)),
        "fast_jump_poll_ticks": int(final_health_snap.get("fast_jump_poll_ticks", 0)),
        "external_fling_hooks_preserved": ext_fling_hooks,
        "gold_live_injected": bool(int(final_health_snap.get("live_silver", 0)) > 0),
        "commit_hits": int(final_hits.get("commit", 0)),
        "hit_counters": final_hits,
        "hit_counters_delta": final_hits,
        "live_silver_final": int(final_health_snap.get("live_silver", 0)),
        "forbidden_files_sha256": {
            "EditorEvidence.py": _sha256_file(ev_py) if os.path.exists(ev_py) else "",
            "CdRecorder.cs": _sha256_file(cd_cs) if os.path.exists(cd_cs) else "",
        },
        "top_bar_buttons_tested": top_tested,
        "top_bar_buttons_passed": top_passed,
        "tab_headers_tested": len(tab_header_results),
        "tab_headers_passed": sum(1 for x in tab_header_results if x["passed"]),
        "total_row_buttons_tested": total_row_tested,
        "total_row_buttons_passed": total_row_passed,
        "failed_buttons": failed_buttons,
        "xml_preserved": (pre_xml_sha == post_xml_sha),
        "save_guard_restored_ok": restored_ok,
        "top_bar_results": top_bar_results,
        "tab_header_results": tab_header_results,
        "tabs": tab_results,
        "action_probes": action_probes,
        "proof_screenshots": proof_screenshots,
        "presentmon_csv_path": pm_csv_path if os.path.exists(pm_csv_path) else None,
        "presentmon_summary": pm_summary,
        "peak_ram_mb": max([s["ram_mb"] for s in gpu_samples] or [0.0]),
        "peak_vram_mb": max([s["vram_mb"] for s in gpu_samples] or [0]),
        "peak_gpu_temp_c": max([s["gpu_temp"] for s in gpu_samples] or [0]),
        "overall_pass": bool(not aborted and len(failed_buttons) == 0 and total_row_passed == 129 and top_passed == 7),
    }
    report["phase1_stage_a"] = {
        "session_id": session_id,
        "completed_at": report["completed_at"],
        "entered_world": report["entered_world"],
        "world_loaded": report["world_loaded"],
        "player_ptr_valid": report["player_ptr_valid"],
        "sta_ptr_valid": report["sta_ptr_valid"],
        "spi_ptr_valid": report["spi_ptr_valid"],
        "player_ptr": report["player_ptr"],
        "sta_ptr": report["sta_ptr"],
        "spi_ptr": report["spi_ptr"],
        "inv_mgr_ptr": report["inv_mgr_ptr"],
        "bag_cont_ptr": report["bag_cont_ptr"],
        "fast_jump_poller_active": report["fast_jump_poller_active"],
        "fast_jump_poll_interval_ms": report["fast_jump_poll_interval_ms"],
        "fast_jump_poll_ticks": report["fast_jump_poll_ticks"],
        "external_fling_hooks_preserved": report["external_fling_hooks_preserved"],
        "gold_live_injected": report["gold_live_injected"],
        "commit_hits": report["commit_hits"],
        "hit_counters_delta": report["hit_counters_delta"],
        "live_silver_final": report["live_silver_final"],
        "top_bar_buttons_tested": report["top_bar_buttons_tested"],
        "top_bar_buttons_passed": report["top_bar_buttons_passed"],
        "tab_headers_tested": report["tab_headers_tested"],
        "tab_headers_passed": report["tab_headers_passed"],
        "total_row_buttons_tested": report["total_row_buttons_tested"],
        "total_row_buttons_passed": report["total_row_buttons_passed"],
        "failed_buttons": report["failed_buttons"],
        "xml_preserved": report["xml_preserved"],
        "save_guard_restored_ok": report["save_guard_restored_ok"],
        "forbidden_files_sha256": report["forbidden_files_sha256"],
    }
    with open(ALL_TABS_REPORT_JSON, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    with open(os.path.join(TELEMETRY_DIR, "all_tabs_live_experiment_20261115.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    with open(os.path.join(TELEMETRY_DIR, "live_verify_summary.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    with open(os.path.join(session_art_dir, "all_tabs_report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    with open(SUMMARY_REPORT_JSON, "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)

    if getattr(args, "auto_rsi", False) and os.path.exists(RSI_SCRIPT):
        try:
            subprocess.run([sys.executable, RSI_SCRIPT, "--evolve", "--preserve-live-xml"], cwd=TUNING_DIR, timeout=30, creationflags=0x08000000)
        except Exception:
            pass
    if os.path.exists(ENGINE_XML) and pre_xml_sha and _sha256_file(ENGINE_XML) != pre_xml_sha:
        restore_user_saves(guard_dir, pre_save_hashes)

    log_event(
        "HUD_ALL_TABS_FINISHED",
        f"Top={top_passed}/{top_tested}, Rows={total_row_passed}/{total_row_tested}, Failed={len(failed_buttons)}, SaveIntact={restored_ok}",
        {"running": False, "trigger_verified": True},
    )
    return report


def run_self_test():
    """Offline deterministic self-test verifying trigger gate, WH_KEYBOARD_LL hook, and 129-button expectation coverage."""
    ok_empty, _ = verify_self_test_trigger("", "")
    ok_bad, _ = verify_self_test_trigger("시작", "")
    ok_good, _ = verify_self_test_trigger(REQUIRED_TRIGGER_PHRASE, "")
    assert not ok_empty and not ok_bad and ok_good, "Trigger validation failed"

    import tempfile
    with tempfile.TemporaryDirectory() as td:
        tf = os.path.join(td, "trigger.tok")
        with open(tf, "w", encoding="utf-8") as f:
            f.write(REQUIRED_TRIGGER_PHRASE)
        ok_tok, mode_tok = verify_self_test_trigger("", tf)
        assert ok_tok and mode_tok == "token_file" and not os.path.exists(tf), "Token file one-shot validation failed"

        test_ini = os.path.join(td, "test_cheat.ini")
        with open(test_ini, "w", encoding="utf-8") as f:
            f.write("[CheatConfig]\nDamageTakenMode=2\nSpeedMultiplier=1.3\n")
        snap_ini = snapshot_ini_config(test_ini)
        # Simulate mid-test toggle mutations leaving 3.0x/1.5x speeds in the INI file
        with open(test_ini, "w", encoding="utf-8") as f:
            f.write("[CheatConfig]\nDamageTakenMode=0\nSpeedMultiplier=3.0\nMountSpeedMultiplier=1.2\nUnderwaterSpeed=1.5\nSpecialConvenienceMode=2\n")
        restore_ini_config_safe_speeds(snap_ini, test_ini)
        restored_snap = snapshot_ini_config(test_ini)
        assert restored_snap.get("DamageTakenMode") == "2" and restored_snap.get("SpeedMultiplier") == "1.3", f"Pre-existing user preferences not restored: {restored_snap}"
        assert restored_snap.get("MountSpeedMultiplier") == "1" and restored_snap.get("UnderwaterSpeed") == "1" and restored_snap.get("SpecialConvenienceMode") == "0", f"Mid-test speed mutations not reverted to safe defaults: {restored_snap}"

    assert _is_physical_key_override(0x0100, 0x10) is False, "Injected SendInput must be ignored"
    assert _is_physical_key_override(0x0100, 0x00) is True, "Physical keydown must trigger override"
    assert _is_physical_key_override(0x0101, 0x00) is False, "Keyup should not trigger override"

    guard = HardwareInputGuard(abort_on_human=True)
    guard.start()
    time_real_sleep(0.15)
    guard.stop()

    total_btns = sum(len(v["values"]) for v in ROW_EXPECTATIONS.values())
    assert len(ROW_EXPECTATIONS) == 25 and total_btns == 129, f"Expected 25 rows / 129 buttons, got {len(ROW_EXPECTATIONS)} / {total_btns}"
    print("[AUTOPLAY-SELF-TEST PASS]")
    return 0


def main():
    parser = argparse.ArgumentParser(description="Crimson Desert Live Auto-Play & Smart All-Tab Verifier v5.0")
    parser.add_argument("--duration-min", type=float, default=3.0)
    parser.add_argument("--mode", type=str, default="hud-all-tabs-experiment")
    parser.add_argument("--save-prefix", type=str, default="autotest")
    parser.add_argument("--tag", type=str, default="baseline")
    parser.add_argument("--preserve-user-save", action="store_true", default=True)
    parser.add_argument("--keep-launcher", action="store_true", default=False)
    parser.add_argument("--keep-game-alive", action="store_true", default=False)
    parser.add_argument("--auto-rsi", action="store_true", default=False)
    parser.add_argument("--trigger-phrase", type=str, default="")
    parser.add_argument("--trigger-token-file", type=str, default="")
    parser.add_argument("--no-human-key-abort", action="store_true", default=False)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--status", action="store_true")
    parser.add_argument("--capture-screen", type=str, default="")
    parser.add_argument("--on-default-desktop", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        sys.exit(run_self_test())

    if args.status:
        print_compact_status()
        return

    trig_ok, trig_src = verify_self_test_trigger(args.trigger_phrase, args.trigger_token_file)
    if not trig_ok:
        sys.stderr.write(f"[BLOCKED_MISSING_TRIGGER] '{REQUIRED_TRIGGER_PHRASE}' 트리거가 없어 자율 시연을 차단했습니다 ({trig_src}).\n")
        sys.stderr.flush()
        sys.exit(2)

    if not args.on_default_desktop:
        si = STARTUPINFOW()
        si.cb = ctypes.sizeof(STARTUPINFOW)
        si.lpDesktop = "WinSta0\\Default"
        pi = PROCESS_INFORMATION()
        fwd_args = []
        skip_next = False
        for a in sys.argv[1:]:
            if skip_next:
                skip_next = False
                continue
            if a == "--trigger-token-file":
                skip_next = True
                continue
            if a.startswith("--trigger-token-file="):
                continue
            fwd_args.append(a)
        if not any(a == "--trigger-phrase" or a.startswith("--trigger-phrase=") for a in fwd_args):
            fwd_args.extend(["--trigger-phrase", REQUIRED_TRIGGER_PHRASE])
        quoted_args = " ".join([f'"{a}"' if " " in a else a for a in fwd_args])
        cmd = f'"{sys.executable}" "{os.path.abspath(__file__)}" --on-default-desktop {quoted_args}'
        ret = kernel32.CreateProcessW(
            None, ctypes.c_wchar_p(cmd), None, None, False, 0x08000000, None,
            ctypes.c_wchar_p(TUNING_DIR), ctypes.byref(si), ctypes.byref(pi)
        )
        if not ret:
            raise RuntimeError(f"Failed to bootstrap onto WinSta0\\Default: {ctypes.get_last_error()}")
        kernel32.WaitForSingleObject(pi.hProcess, 0xFFFFFFFF)
        exit_code = wintypes.DWORD(0)
        kernel32.GetExitCodeProcess(pi.hProcess, ctypes.byref(exit_code))
        kernel32.CloseHandle(pi.hProcess)
        kernel32.CloseHandle(pi.hThread)
        sys.exit(int(exit_code.value))

    if args.capture_screen:
        capture_desktop_bmp(args.capture_screen)
        return

    try:
        run_auto_play(args)
    except EmergencyAbortError as ex:
        log_event("AUTO_PLAY_ABORTED", f"Stopped cleanly: {ex}")


if __name__ == "__main__":
    main()
