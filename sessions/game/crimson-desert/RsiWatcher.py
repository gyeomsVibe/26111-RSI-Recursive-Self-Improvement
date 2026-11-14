# -*- coding: utf-8 -*-
"""
Crimson Desert RSI Telemetry & Self-Improvement Watcher (v4)
- Zero paid API tokens: local deterministic process/GPU/ETW/crash telemetry daemon.
- Watches CrimsonDesert.exe runtime without polling loops that disturb gameplay:
  * Polls process & window state at a gentle 2.0s cadence (5.0s when idle).
  * Triggers CdRecorder via EditorEvidence.ensure_recorder(session_id) once per session.
  * Launches non-blocking 20s PresentMon-2.6.0-x64.exe micro-bursts (*-rsi-frames.csv)
    during sustained foreground gameplay (~120s cadence, only when no PresentMon runs).
  * Monitors Pearl Abyss DumpCache (last_crash + .dmp files) and records deduplicated
    crash records into crash_events.jsonl for RsiEngine v4.
  * Ingests live_editor_health.json summary into active_status.json (atomic replace).
  * Triggers RsiEngine.py --evolve on game exit and --apply-pending when idle.
"""

from __future__ import annotations

import csv
import ctypes
import datetime
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from typing import Any, Callable, Dict, Optional, Tuple

TUNING_DIR = os.path.dirname(os.path.abspath(__file__))
RESEARCH_DIR = os.path.join(TUNING_DIR, "research")
SESSIONS_DIR = os.path.join(RESEARCH_DIR, "sessions")
RSI_DIR = os.path.join(RESEARCH_DIR, "rsi_telemetry")
ACTIVE_STATUS_JSON = os.path.join(RSI_DIR, "active_status.json")
LIVE_EDITOR_HEALTH_JSON = os.path.join(RSI_DIR, "live_editor_health.json")
LIVE_TOGGLE_AUDIT_JSON = os.path.join(RSI_DIR, "live_toggle_audit.json")
CHEAT_INI_PATH = os.path.join(TUNING_DIR, "CrimsonDesert_CheatConfig.ini")
CRASH_EVENTS_JSONL = os.path.join(RSI_DIR, "crash_events.jsonl")
RSI_STATE_JSON = os.path.join(RSI_DIR, "rsi_state.json")
AUTO_PLAY_STATUS_JSON = os.path.join(RSI_DIR, "auto_play_status.json")
WATCHER_LOG = os.path.join(RSI_DIR, "rsi_watcher.log")
RSI_ENGINE_PY = os.path.join(TUNING_DIR, "RsiEngine.py")
PRESENTMON_EXE = os.path.join(TUNING_DIR, "tools", "PresentMon-2.6.0-x64.exe")

LOCAL_APP_DATA = os.environ.get("LOCALAPPDATA") or os.path.expandvars(r"%LOCALAPPDATA%")
DUMP_CACHE_DIR = os.path.join(LOCAL_APP_DATA, "Pearl Abyss", "DumpCache")
LAST_CRASH_FILE = os.path.join(DUMP_CACHE_DIR, "last_crash")

CREATE_NO_WINDOW = 0x08000000
BELOW_NORMAL_PRIORITY_CLASS = 0x00004000
NORMAL_PRIORITY_CLASS = 0x00000020
ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000
PROCESS_SET_INFORMATION = 0x0200
TARGET_SESSION_SEC = 1800
PRESENTMON_INTERVAL_SEC = 600.0
PRESENTMON_WARMUP_SEC = 25.0
PRESENTMON_DURATION_SEC = 20

INI_DEFAULTS = {
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

if TUNING_DIR not in sys.path:
    sys.path.insert(0, TUNING_DIR)

try:
    import EditorEvidence  # type: ignore
except Exception:
    EditorEvidence = None  # type: ignore

for _d in (RESEARCH_DIR, SESSIONS_DIR, RSI_DIR):
    os.makedirs(_d, exist_ok=True)


# ── Windows native helpers (Toolhelp32 + GetProcessMemoryInfo + ForegroundWindow) ──

kernel32 = ctypes.windll.kernel32
user32 = ctypes.windll.user32
psapi = ctypes.windll.psapi

TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_VM_READ = 0x0010


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", ctypes.c_uint32),
        ("cntUsage", ctypes.c_uint32),
        ("th32ProcessID", ctypes.c_uint32),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", ctypes.c_uint32),
        ("cntThreads", ctypes.c_uint32),
        ("th32ParentProcessID", ctypes.c_uint32),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", ctypes.c_uint32),
        ("szExeFile", ctypes.c_wchar * 260),
    ]


class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("cb", ctypes.c_uint32),
        ("PageFaultCount", ctypes.c_uint32),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def log_msg(msg: str) -> None:
    line = f"[{datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    try:
        # Rotate log if > 1 MB to prevent unbounded growth
        if os.path.exists(WATCHER_LOG) and os.path.getsize(WATCHER_LOG) > 1024 * 1024:
            bak = WATCHER_LOG + ".1"
            if os.path.exists(bak):
                os.remove(bak)
            os.replace(WATCHER_LOG, bak)
        with open(WATCHER_LOG, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


def get_game_pid(exe_name: str = "CrimsonDesert.exe") -> Optional[int]:
    """Return PID of the first matching process name (case-insensitive), or None."""
    target = exe_name.lower()
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == ctypes.c_void_p(-1).value or not snap:
        return None
    try:
        pe = PROCESSENTRY32W()
        pe.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        if not kernel32.Process32FirstW(snap, ctypes.byref(pe)):
            return None
        while True:
            if (pe.szExeFile or "").lower() == target:
                return int(pe.th32ProcessID)
            if not kernel32.Process32NextW(snap, ctypes.byref(pe)):
                break
    finally:
        kernel32.CloseHandle(snap)
    return None


def is_presentmon_running() -> bool:
    """True if any PresentMon process is already running so bursts never collide."""
    for name in ("PresentMon-2.6.0-x64.exe", "PresentMon.exe"):
        if get_game_pid(name) is not None:
            return True
    return False


def is_autoplay_active(max_age_sec: float = 25.0) -> bool:
    """True while CrimsonAutoPlayEngine.py is actively managing its full-session telemetry."""
    ap_status = os.path.join(RSI_DIR, "auto_play_status.json")
    try:
        if os.path.exists(ap_status):
            return (time.time() - os.path.getmtime(ap_status)) <= max_age_sec
    except Exception:
        pass
    return False


def get_process_ws_mb(pid: Optional[int]) -> float:
    """Query process working set in MB using Win32 GetProcessMemoryInfo."""
    if not pid or pid <= 0:
        return 0.0
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, int(pid))
    if not h:
        h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
    if not h:
        return 0.0
    try:
        pmc = PROCESS_MEMORY_COUNTERS()
        pmc.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
        if psapi.GetProcessMemoryInfo(h, ctypes.byref(pmc), pmc.cb):
            return round(pmc.WorkingSetSize / (1024.0 * 1024.0), 1)
    except Exception:
        pass
    finally:
        kernel32.CloseHandle(h)
    return 0.0


def set_self_below_normal_priority() -> bool:
    """Lower RsiWatcher process priority to BELOW_NORMAL so telemetry never competes with game threads."""
    try:
        return bool(kernel32.SetPriorityClass(kernel32.GetCurrentProcess(), BELOW_NORMAL_PRIORITY_CLASS))
    except Exception:
        return False


def elevate_game_priority_once(pid: Optional[int]) -> Optional[str]:
    """Gently raise CrimsonDesert.exe from NORMAL (0x20) to ABOVE_NORMAL (0x8000) if not already elevated."""
    if not pid or pid <= 0:
        return None
    h = kernel32.OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))
    if not h:
        return None
    try:
        cur = int(kernel32.GetPriorityClass(h))
        if cur == NORMAL_PRIORITY_CLASS:
            if kernel32.SetPriorityClass(h, ABOVE_NORMAL_PRIORITY_CLASS):
                return "NORMAL->ABOVE_NORMAL"
        elif cur:
            return f"UNCHANGED(0x{cur:X})"
    except Exception:
        pass
    finally:
        kernel32.CloseHandle(h)
    return None


def is_external_fling_running() -> bool:
    """Return True if an external FLiNG trainer process is active (internal hooks intentionally yield)."""
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == ctypes.c_void_p(-1).value or not snap:
        return False
    try:
        pe = PROCESSENTRY32W()
        pe.dwSize = ctypes.sizeof(PROCESSENTRY32W)
        if not kernel32.Process32FirstW(snap, ctypes.byref(pe)):
            return False
        while True:
            name = (pe.szExeFile or "").lower()
            if (
                name not in ("crimsonlauncher.exe", "crimsontrainer.exe")
                and (
                    "plus 12 trainer" in name
                    or "fling_trainer" in name
                    or "crimson desert enhanced" in name
                )
            ):
                return True
            if not kernel32.Process32NextW(snap, ctypes.byref(pe)):
                break
    finally:
        kernel32.CloseHandle(snap)
    return False


def is_game_foreground(game_pid: Optional[int]) -> bool:
    """Return True if the foreground window belongs to CrimsonDesert.exe or the live editor overlay."""
    if not game_pid:
        return False
    try:
        hwnd = user32.GetForegroundWindow()
        if not hwnd:
            return False
        fg_pid_c = ctypes.c_uint32(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(fg_pid_c))
        fg_pid = int(fg_pid_c.value)
        if fg_pid == int(game_pid):
            return True
        for overlay_exe in ("CrimsonLauncher.exe", "CrimsonTrainer.exe"):
            opid = get_game_pid(overlay_exe)
            if opid and fg_pid == int(opid):
                return True
        return False
    except Exception:
        return False


def query_gpu_stats() -> Dict[str, float]:
    """Query NVIDIA GPU utilization, VRAM used/total (MB), and temp (C)."""
    stats = {"gpu_util": 0.0, "vram_mb": 0.0, "vram_total_mb": 0.0, "gpu_temp_c": 0.0}
    try:
        res = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            text=True,
            timeout=2.5,
            creationflags=CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
        )
        if res.returncode == 0 and res.stdout.strip():
            parts = [p.strip() for p in res.stdout.strip().splitlines()[0].split(",")]
            if len(parts) >= 4:
                stats["gpu_util"] = float(parts[0])
                stats["vram_mb"] = float(parts[1])
                stats["vram_total_mb"] = float(parts[2])
                stats["gpu_temp_c"] = float(parts[3])
    except Exception:
        pass
    return stats


def _atomic_write_json(target_path: str, payload: Dict[str, Any]) -> None:
    parent = os.path.dirname(target_path)
    os.makedirs(parent, exist_ok=True)
    tmp_path = f"{target_path}.tmp.{os.getpid()}"
    try:
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        os.replace(tmp_path, target_path)
    except Exception:
        try:
            if os.path.exists(tmp_path):
                os.remove(tmp_path)
        except Exception:
            pass


def unload_ollama_vram_for_game(
    host: Optional[str] = None,
    timeout_sec: float = 1.5,
    opener: Optional[Callable[[Any, float], bytes]] = None,
) -> int:
    """Evict loaded Ollama models (keep_alive=0) when CrimsonDesert starts so GTX 1070 VRAM stays 100% free."""
    base = (host or os.environ.get("OLLAMA_HOST") or "http://127.0.0.1:11434").rstrip("/")
    if not base.startswith("http"):
        base = "http://" + base

    def _default_open(req: Any, to: float) -> bytes:
        with urllib.request.urlopen(req, timeout=to) as resp:
            return resp.read()

    call = opener or _default_open
    try:
        ps_req = urllib.request.Request(f"{base}/api/ps", method="GET")
        ps_data = json.loads(call(ps_req, timeout_sec).decode("utf-8", errors="replace"))
    except Exception:
        return 0

    models = ps_data.get("models") if isinstance(ps_data, dict) else None
    if not isinstance(models, list) or not models:
        return 0

    unloaded = 0
    for entry in models:
        name = (entry.get("name") or entry.get("model") or "") if isinstance(entry, dict) else ""
        if not name:
            continue
        try:
            body = json.dumps({"model": name, "keep_alive": 0}).encode("utf-8")
            gen_req = urllib.request.Request(
                f"{base}/api/generate",
                data=body,
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            call(gen_req, timeout_sec)
            unloaded += 1
        except Exception:
            pass
    return unloaded


def read_live_editor_health(path: str = LIVE_EDITOR_HEALTH_JSON) -> Optional[Dict[str, Any]]:
    """Read summarized live editor hook health written by SessionBoost.cs."""
    if not os.path.exists(path):
        return None
    try:
        mtime = os.path.getmtime(path)
        age_sec = round(max(0.0, time.time() - mtime), 1)
        with open(path, "r", encoding="utf-8") as f:
            raw = json.load(f)
        if not isinstance(raw, dict):
            return None
        hooks = raw.get("installed_hooks")
        if isinstance(hooks, list):
            hooks_summary = f"{len(hooks)} installed"
        elif isinstance(hooks, dict):
            hooks_summary = f"{len(hooks)} entries"
        else:
            hooks_summary = str(hooks) if hooks is not None else "unknown"
        return {
            "active_feature_count": int(raw.get("active_feature_count", 0) or 0),
            "player_ptr_valid": bool(raw.get("player_ptr_valid", False)),
            "world_loaded": bool(raw.get("world_loaded", False)),
            "cave_addr": raw.get("cave_addr"),
            "live_silver": raw.get("live_silver"),
            "installed_hooks": hooks if isinstance(hooks, list) else [],
            "installed_hooks_summary": hooks_summary,
            "hit_counters": raw.get("hit_counters") if isinstance(raw.get("hit_counters"), dict) else {},
            "age_sec": age_sec,
            "stale": age_sec > 30.0,
        }
    except Exception:
        return None


def read_cheat_ini(path: str = CHEAT_INI_PATH) -> Dict[str, str]:
    """Read key=value entries from CrimsonDesert_CheatConfig.ini safely."""
    cfg: Dict[str, str] = {}
    if not os.path.exists(path):
        return cfg
    try:
        with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
            for line in f:
                s = line.strip()
                if not s or s.startswith(("[", ";", "#")) or "=" not in s:
                    continue
                k, v = [x.strip() for x in s.split("=", 1)]
                if k and k.lower() != "updatedat":
                    cfg[k] = v
    except Exception:
        pass
    return cfg


def compute_ini_deltas(
    prev_ini: Dict[str, str],
    curr_ini: Dict[str, str],
    timestamp: Optional[str] = None,
) -> list:
    """Return [{timestamp, key, old, new}] for changed keys."""
    ts = timestamp or datetime.datetime.now().isoformat(timespec="seconds")
    deltas = []
    for k in sorted(set(prev_ini.keys()) | set(curr_ini.keys())):
        old_v = prev_ini.get(k, "<unset>")
        new_v = curr_ini.get(k, "<unset>")
        if old_v != new_v:
            deltas.append({"timestamp": ts, "key": k, "old": old_v, "new": new_v})
    return deltas


def build_live_toggle_audit_payload(
    session_id: Optional[str],
    curr_ini: Dict[str, str],
    toggled_keys_in_session: list,
    ed_health: Optional[Dict[str, Any]],
    ext_fling: bool = False,
) -> Dict[str, Any]:
    """Build real-time INI toggle <-> hook/cave reflection audit JSON payload."""
    non_defaults = {}
    for k, v in sorted(curr_ini.items()):
        if k in INI_DEFAULTS and str(v) != str(INI_DEFAULTS[k]):
            non_defaults[k] = v
    anomalies = []
    if ed_health and ed_health.get("world_loaded") and not ed_health.get("stale"):
        hooks = ed_health.get("installed_hooks") or []
        if non_defaults and not hooks and not ext_fling:
            anomalies.append(
                f"world_loaded=True with {len(non_defaults)} active INI keys but installed_hooks is empty"
            )
    return {
        "session_id": session_id,
        "updated_at": datetime.datetime.now().isoformat(timespec="seconds"),
        "active_ini_non_defaults": non_defaults,
        "toggled_keys_in_session": list(toggled_keys_in_session[-200:]),
        "editor_health": {
            "cave_addr": ed_health.get("cave_addr") if ed_health else None,
            "installed_hooks": ed_health.get("installed_hooks", []) if ed_health else [],
            "hit_counters": ed_health.get("hit_counters", {}) if ed_health else {},
            "player_ptr_valid": bool(ed_health.get("player_ptr_valid", False)) if ed_health else False,
            "live_silver": ed_health.get("live_silver") if ed_health else None,
            "stale": bool(ed_health.get("stale", True)) if ed_health else True,
            "age_sec": ed_health.get("age_sec") if ed_health else None,
        },
        "external_fling_active": bool(ext_fling),
        "anomalies": anomalies,
    }


def update_status(status_dict: Dict[str, Any], target_path: str = ACTIVE_STATUS_JSON) -> None:
    """Atomically write watcher status JSON (preserving legacy UI keys + v4.1 metrics)."""
    payload = dict(status_dict)
    payload.setdefault("session_duration_target_sec", TARGET_SESSION_SEC)
    payload.setdefault("elapsed_sec", 0)
    payload["last_heartbeat"] = datetime.datetime.now().isoformat()
    payload["watcher_version"] = 4.1
    payload["pid"] = os.getpid()
    _atomic_write_json(target_path, payload)


def check_recent_game_crashes(
    since_epoch: Optional[float] = None,
    last_crash_path: str = LAST_CRASH_FILE,
    dump_dir: str = DUMP_CACHE_DIR,
) -> Optional[Dict[str, Any]]:
    """Inspect Pearl Abyss DumpCache (last_crash + recent .dmp) and return crash info if fresh."""
    cutoff = (since_epoch - 5.0) if since_epoch else (time.time() - 600.0)
    crash_epoch = 0.0
    parts = []

    if os.path.exists(last_crash_path):
        try:
            mt = os.path.getmtime(last_crash_path)
            if mt >= cutoff:
                crash_epoch = max(crash_epoch, mt)
                with open(last_crash_path, "r", encoding="utf-8", errors="ignore") as f:
                    txt = " ".join(f.read().strip().splitlines())[:240]
                parts.append(f"last_crash={txt or 'present'}")
        except Exception:
            pass

    if os.path.isdir(dump_dir):
        try:
            newest_dmp = None
            newest_mt = 0.0
            for fn in os.listdir(dump_dir):
                if fn.lower().endswith(".dmp"):
                    fp = os.path.join(dump_dir, fn)
                    mt = os.path.getmtime(fp)
                    if mt >= cutoff and mt > newest_mt:
                        newest_mt = mt
                        newest_dmp = fn
            if newest_dmp:
                crash_epoch = max(crash_epoch, newest_mt)
                parts.append(f"dmp={newest_dmp}")
        except Exception:
            pass

    if not parts:
        return None
    return {
        "epoch": round(crash_epoch or time.time(), 3),
        "summary": " | ".join(parts),
    }


def record_crash_event_once(
    crash_info: Dict[str, Any],
    last_recorded_epoch: float,
    jsonl_path: str = CRASH_EVENTS_JSONL,
) -> float:
    """Append a deduplicated crash event line {epoch, summary} for RsiEngine.py v4."""
    ep = float(crash_info.get("epoch", 0.0) or 0.0)
    if ep <= last_recorded_epoch + 0.5:
        return last_recorded_epoch
    rec = {
        "epoch": ep,
        "summary": str(crash_info.get("summary", "crash_detected")),
        "recorded_at": datetime.datetime.now().isoformat(),
    }
    try:
        os.makedirs(os.path.dirname(jsonl_path), exist_ok=True)
        with open(jsonl_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
        if os.path.abspath(jsonl_path) == os.path.abspath(CRASH_EVENTS_JSONL):
            log_msg(f"CRASH_RECORDED: {rec['summary']} (epoch={ep})")
        return ep
    except Exception as ex:
        if os.path.abspath(jsonl_path) == os.path.abspath(CRASH_EVENTS_JSONL):
            log_msg(f"CRASH_RECORD_ERR: {ex}")
        return last_recorded_epoch


def start_telemetry_session(session_id: Optional[str] = None) -> Tuple[str, str]:
    """Create a session ID and watcher telemetry CSV path in SESSIONS_DIR."""
    sid = session_id or datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    csv_path = os.path.join(SESSIONS_DIR, f"{sid}-watcher.csv")
    try:
        with open(csv_path, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(
                [
                    "timestamp",
                    "elapsed_sec",
                    "game_pid",
                    "foreground",
                    "ram_mb",
                    "gpu_util",
                    "vram_mb",
                    "gpu_temp_c",
                    "editor_active_features",
                    "editor_player_ptr_valid",
                ]
            )
    except Exception as ex:
        log_msg(f"SESSION_CSV_INIT_ERR: {ex}")
    return sid, csv_path


def append_telemetry_row(
    csv_path: str,
    elapsed_sec: float,
    pid: int,
    fg: bool,
    ram_mb: float,
    gpu: Dict[str, float],
    ed_health: Optional[Dict[str, Any]],
) -> None:
    try:
        with open(csv_path, "a", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow(
                [
                    datetime.datetime.now().isoformat(timespec="seconds"),
                    round(elapsed_sec, 1),
                    pid,
                    1 if fg else 0,
                    ram_mb,
                    gpu.get("gpu_util", 0.0),
                    gpu.get("vram_mb", 0.0),
                    gpu.get("gpu_temp_c", 0.0),
                    ed_health.get("active_feature_count", "") if ed_health else "",
                    (1 if ed_health.get("player_ptr_valid") else 0) if ed_health else "",
                ]
            )
    except Exception:
        pass


def launch_presentmon_burst(session_tag: str) -> Optional[subprocess.Popen]:
    """Start non-blocking 20s PresentMon micro-burst if safe and not already running."""
    if not os.path.exists(PRESENTMON_EXE):
        return None
    if is_presentmon_running():
        return None
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_csv = os.path.join(SESSIONS_DIR, f"{stamp}-{session_tag}-rsi-frames.csv")
    cmd = [
        PRESENTMON_EXE,
        "--process_name",
        "CrimsonDesert.exe",
        "--output_file",
        out_csv,
        "--stop_existing_session",
        "--timed",
        str(PRESENTMON_DURATION_SEC),
        "--terminate_after_timed",
        "--no_console_stats",
    ]
    try:
        proc = subprocess.Popen(
            cmd,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
        )
        log_msg(f"PRESENTMON_BURST_STARTED: pid={proc.pid} out={os.path.basename(out_csv)}")
        return proc
    except Exception as ex:
        log_msg(f"PRESENTMON_BURST_ERR: {ex}")
        return None


def trigger_rsi_engine(reason: str = "manual", timeout_sec: float = 30.0) -> int:
    """Invoke RsiEngine.py --evolve synchronously (zero paid tokens)."""
    if not os.path.exists(RSI_ENGINE_PY):
        return -1
    cmd = [sys.executable, RSI_ENGINE_PY, "--evolve", "--reason", reason]
    try:
        if os.path.exists(AUTO_PLAY_STATUS_JSON) and (time.time() - os.path.getmtime(AUTO_PLAY_STATUS_JSON)) <= 90.0:
            with open(AUTO_PLAY_STATUS_JSON, "r", encoding="utf-8") as sf:
                sdata = json.load(sf)
            if isinstance(sdata, dict) and sdata.get("trigger_verified") is True and (
                sdata.get("running") is True
                or sdata.get("preserve_live_xml") is True
                or "HUD_" in str(sdata.get("event", ""))
            ):
                cmd.append("--preserve-live-xml")
    except Exception:
        pass
    try:
        res = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=timeout_sec,
            creationflags=CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
        )
        log_msg(f"RSI_ENGINE_EVOLVE ({reason}): exit={res.returncode}")
        return res.returncode
    except Exception as ex:
        log_msg(f"RSI_ENGINE_EVOLVE_ERR ({reason}): {ex}")
        return -1


def check_and_apply_pending_on_idle(timeout_sec: float = 20.0) -> bool:
    """When CrimsonDesert.exe is idle, apply queued RSI pending config if marked in rsi_state.json."""
    if get_game_pid("CrimsonDesert.exe") is not None:
        return False
    if not os.path.exists(RSI_STATE_JSON) or not os.path.exists(RSI_ENGINE_PY):
        return False
    try:
        with open(RSI_STATE_JSON, "r", encoding="utf-8") as f:
            st = json.load(f)
        if not st.get("pending_apply_on_exit"):
            return False
        res = subprocess.run(
            [sys.executable, RSI_ENGINE_PY, "--apply-pending"],
            capture_output=True,
            text=True,
            timeout=timeout_sec,
            creationflags=CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS,
        )
        log_msg(f"RSI_APPLY_PENDING: exit={res.returncode}")
        return res.returncode == 0
    except Exception as ex:
        log_msg(f"RSI_APPLY_PENDING_ERR: {ex}")
        return False


def _load_last_recorded_crash_epoch(jsonl_path: str = CRASH_EVENTS_JSONL) -> float:
    last_ep = 0.0
    if not os.path.exists(jsonl_path):
        return last_ep
    try:
        with open(jsonl_path, "r", encoding="utf-8", errors="ignore") as f:
            for line in f:
                s = line.strip()
                if not s:
                    continue
                try:
                    obj = json.loads(s)
                    last_ep = max(last_ep, float(obj.get("epoch", 0.0) or 0.0))
                except Exception:
                    continue
    except Exception:
        pass
    return last_ep


def run_self_test() -> int:
    """Deterministic offline self-test with zero mutations to live telemetry state."""
    required = [TUNING_DIR, RESEARCH_DIR, SESSIONS_DIR, RSI_DIR, RSI_ENGINE_PY, PRESENTMON_EXE]
    missing = [p for p in required if not os.path.exists(p)]
    if missing:
        print(f"[RSI-WATCHER-SELF-TEST FAIL] Missing paths: {missing}")
        return 2

    _ = get_game_pid("System")
    _ = is_presentmon_running()
    gpu = query_gpu_stats()
    if "gpu_util" not in gpu or "vram_mb" not in gpu:
        print("[RSI-WATCHER-SELF-TEST FAIL] query_gpu_stats schema invalid")
        return 3

    with tempfile.TemporaryDirectory() as tmp:
        tmp_health = os.path.join(tmp, "live_editor_health.json")
        with open(tmp_health, "w", encoding="utf-8") as f:
            json.dump(
                {
                    "active_feature_count": 4,
                    "player_ptr_valid": True,
                    "live_silver": 187706,
                    "installed_hooks": ["h1", "h2"],
                },
                f,
            )
        ed = read_live_editor_health(tmp_health)
        assert ed is not None and ed["active_feature_count"] == 4 and ed["player_ptr_valid"] is True

        tmp_status = os.path.join(tmp, "active_status.json")
        update_status(
            {
                "state": "SELF_TEST",
                "description": "v4 self test",
                "elapsed_sec": 12,
                "editor_health": ed,
            },
            target_path=tmp_status,
        )
        with open(tmp_status, "r", encoding="utf-8") as f:
            loaded = json.load(f)
        assert loaded["state"] == "SELF_TEST" and loaded["watcher_version"] == 4.1

        deltas = compute_ini_deltas({"DamageTakenMode": "0", "ExpMultiplier": "1"}, {"DamageTakenMode": "2", "ExpMultiplier": "2"}, timestamp="2026-11-14T14:25:00")
        assert len(deltas) == 2 and deltas[0]["key"] == "DamageTakenMode" and deltas[0]["new"] == "2"
        tmp_audit = os.path.join(tmp, "live_toggle_audit.json")
        audit_payload = build_live_toggle_audit_payload("selftest", {"DamageTakenMode": "2"}, deltas, ed, ext_fling=False)
        _atomic_write_json(tmp_audit, audit_payload)
        with open(tmp_audit, "r", encoding="utf-8") as f:
            audit_loaded = json.load(f)
        assert audit_loaded["session_id"] == "selftest" and audit_loaded["active_ini_non_defaults"].get("DamageTakenMode") == "2"
        assert audit_loaded["anomalies"] == []

        tmp_crashes = os.path.join(tmp, "crash_events.jsonl")
        ep1 = record_crash_event_once({"epoch": 1000.0, "summary": "test_crash"}, 0.0, jsonl_path=tmp_crashes)
        ep2 = record_crash_event_once({"epoch": 1000.2, "summary": "dup_crash"}, ep1, jsonl_path=tmp_crashes)
        assert ep1 == 1000.0 and ep2 == 1000.0
        with open(tmp_crashes, "r", encoding="utf-8") as f:
            lines = [ln for ln in f.read().splitlines() if ln.strip()]
        assert len(lines) == 1

    # Verify Ollama VRAM eviction works via mock API opener and fails closed cleanly on dead port
    seen_calls = []

    def _mock_opener(req: Any, to: float) -> bytes:
        url = req.full_url if hasattr(req, "full_url") else str(req)
        payload = req.data.decode("utf-8") if getattr(req, "data", None) else ""
        seen_calls.append((url, payload))
        if url.endswith("/api/ps"):
            return json.dumps({"models": [{"name": "qwen3:1.7b"}]}).encode("utf-8")
        return json.dumps({"done": True}).encode("utf-8")

    assert unload_ollama_vram_for_game(host="http://127.0.0.1:11434", opener=_mock_opener) == 1
    assert len(seen_calls) == 2 and '"keep_alive": 0' in seen_calls[1][1]
    assert unload_ollama_vram_for_game(host="http://127.0.0.1:59999", timeout_sec=0.2) == 0

    if PRESENTMON_INTERVAL_SEC < 600.0:
        print(f"[RSI-WATCHER-SELF-TEST FAIL] PRESENTMON_INTERVAL_SEC={PRESENTMON_INTERVAL_SEC} < 600.0")
        return 5
    with open(__file__, "r", encoding="utf-8") as sf:
        src_lines = [
            ln for ln in sf.read().splitlines()
            if not ln.lstrip().startswith("#")
            and not ln.lstrip().startswith('"""')
            and "CONTROL_GUARD_" not in ln
            and "forbidden_api" not in ln
        ]
    logic_blob = "\n".join(src_lines)
    for forbidden_api in ("SendInput", "SetForegroundWindow", "keybd_event", "mouse_event", "CrimsonAutoPlayEngine"):
        if forbidden_api in logic_blob:
            print(f"[RSI-WATCHER-SELF-TEST FAIL] CONTROL_GUARD_HIT: {forbidden_api}")
            return 6

    print("[RSI-WATCHER-SELF-TEST PASS]")
    return 0


def run_loop(once: bool = False) -> None:
    set_self_below_normal_priority()
    log_msg("RsiWatcher v4.1 started (BELOW_NORMAL priority).")
    current_session_id: Optional[str] = None
    current_session_file: Optional[str] = None
    session_start_time: Optional[float] = None
    recorder_started_session: Optional[str] = None
    priority_elevated_pid: Optional[int] = None
    fg_accumulated_sec = 0.0
    last_loop_ts = time.time()
    last_sample_ts = 0.0
    last_gpu_ts = 0.0
    last_gpu_stats: Dict[str, float] = {"gpu_util": 0.0, "vram_mb": 0.0, "vram_total_mb": 0.0, "gpu_temp_c": 0.0}
    last_presentmon_ts = 0.0
    last_idle_pending_check_ts = 0.0
    presentmon_proc: Optional[subprocess.Popen] = None
    last_crash_epoch = _load_last_recorded_crash_epoch()
    last_crash_summary = ""
    prev_ini: Dict[str, str] = {}
    last_ini_mtime = 0.0
    last_health_mtime = 0.0
    toggled_keys_in_session: list = []

    while True:
        now = time.time()
        dt = max(0.0, min(10.0, now - last_loop_ts))
        last_loop_ts = now

        if presentmon_proc is not None and presentmon_proc.poll() is not None:
            log_msg(f"PRESENTMON_BURST_DONE: rc={presentmon_proc.returncode}")
            presentmon_proc = None

        pid = get_game_pid("CrimsonDesert.exe")
        running = pid is not None
        ed_health = read_live_editor_health()

        # Always watch DumpCache so crashes while launcher/game exit are never missed
        crash_info = check_recent_game_crashes(since_epoch=max(last_crash_epoch, now - 600.0))
        if crash_info and crash_info["epoch"] > last_crash_epoch + 0.5:
            last_crash_epoch = record_crash_event_once(crash_info, last_crash_epoch)
            last_crash_summary = crash_info["summary"]

        if running:
            if not current_session_file or not session_start_time:
                current_session_id, current_session_file = start_telemetry_session()
                session_start_time = now
                fg_accumulated_sec = 0.0
                recorder_started_session = None
                priority_elevated_pid = None
                toggled_keys_in_session = []
                prev_ini = read_cheat_ini()
                try:
                    last_ini_mtime = os.path.getmtime(CHEAT_INI_PATH) if os.path.exists(CHEAT_INI_PATH) else 0.0
                except Exception:
                    last_ini_mtime = 0.0
                try:
                    last_health_mtime = os.path.getmtime(LIVE_EDITOR_HEALTH_JSON) if os.path.exists(LIVE_EDITOR_HEALTH_JSON) else 0.0
                except Exception:
                    last_health_mtime = 0.0
                _atomic_write_json(
                    LIVE_TOGGLE_AUDIT_JSON,
                    build_live_toggle_audit_payload(
                        current_session_id,
                        prev_ini,
                        toggled_keys_in_session,
                        ed_health,
                        ext_fling=is_external_fling_running(),
                    ),
                )
                log_msg(f"GAME_SESSION_START: pid={pid} sid={current_session_id}")
                unloaded_cnt = unload_ollama_vram_for_game()
                if unloaded_cnt > 0:
                    log_msg(f"OLLAMA_VRAM_UNLOADED_FOR_GAME: models={unloaded_cnt}")

            if priority_elevated_pid != pid:
                prio_note = elevate_game_priority_once(pid)
                priority_elevated_pid = pid
                if prio_note:
                    log_msg(f"GAME_PRIORITY_CHECK: pid={pid} {prio_note}")

            elapsed = round(now - session_start_time, 1)
            fg = is_game_foreground(pid)
            if fg:
                fg_accumulated_sec += dt

            # Trigger CdRecorder ONCE per session as soon as foreground OR elapsed >= 5s
            if (
                (fg or elapsed >= 5.0)
                and EditorEvidence is not None
                and current_session_id
                and recorder_started_session != current_session_id
            ):
                try:
                    if EditorEvidence.ensure_recorder(current_session_id):
                        recorder_started_session = current_session_id
                        log_msg(f"CD_RECORDER_ENSURED: sid={current_session_id}")
                except Exception as ex:
                    log_msg(f"CD_RECORDER_ERR: {ex}")
                    recorder_started_session = current_session_id

            # Non-blocking 20s PresentMon micro-burst every ~120s during active 3D foreground play (skip during AutoPlay)
            if (
                fg
                and fg_accumulated_sec >= PRESENTMON_WARMUP_SEC
                and (now - last_presentmon_ts) >= PRESENTMON_INTERVAL_SEC
                and presentmon_proc is None
                and not is_presentmon_running()
                and not is_autoplay_active()
            ):
                presentmon_proc = launch_presentmon_burst("watcher")
                last_presentmon_ts = now

            # Track real-time INI / live_editor_health changes into live_toggle_audit.json
            try:
                cur_ini_mtime = os.path.getmtime(CHEAT_INI_PATH) if os.path.exists(CHEAT_INI_PATH) else 0.0
            except Exception:
                cur_ini_mtime = last_ini_mtime
            try:
                cur_health_mtime = os.path.getmtime(LIVE_EDITOR_HEALTH_JSON) if os.path.exists(LIVE_EDITOR_HEALTH_JSON) else 0.0
            except Exception:
                cur_health_mtime = last_health_mtime

            if cur_ini_mtime != last_ini_mtime or cur_health_mtime != last_health_mtime:
                curr_ini = read_cheat_ini() if cur_ini_mtime != last_ini_mtime else prev_ini
                deltas = compute_ini_deltas(prev_ini, curr_ini) if cur_ini_mtime != last_ini_mtime else []
                if deltas:
                    toggled_keys_in_session.extend(deltas)
                    for d in deltas:
                        log_msg(f"LIVE_INI_TOGGLE: {d['key']} {d['old']} -> {d['new']}")
                    prev_ini = curr_ini
                last_ini_mtime = cur_ini_mtime
                last_health_mtime = cur_health_mtime
                _atomic_write_json(
                    LIVE_TOGGLE_AUDIT_JSON,
                    build_live_toggle_audit_payload(
                        current_session_id,
                        curr_ini,
                        toggled_keys_in_session,
                        ed_health,
                        ext_fling=is_external_fling_running(),
                    ),
                )

            ram_mb = get_process_ws_mb(pid)
            gpu_poll_interval = 30.0 if running else 8.0
            if (now - last_gpu_ts) >= gpu_poll_interval or last_gpu_ts == 0.0:
                last_gpu_stats = query_gpu_stats()
                last_gpu_ts = now
            gpu = last_gpu_stats

            if (now - last_sample_ts) >= 5.0:
                append_telemetry_row(current_session_file, elapsed, pid or 0, fg, ram_mb, gpu, ed_health)
                last_sample_ts = now

            update_status(
                {
                    "state": "TRACKING_GAME_FOREGROUND" if fg else "TRACKING_GAME_BACKGROUND",
                    "description": (
                        f"CrimsonDesert.exe 감시 중 (PID {pid}, GPU {gpu['gpu_util']:.0f}%, VRAM {gpu['vram_mb']:.0f}MB)"
                    ),
                    "session_id": current_session_id,
                    "session_duration_target_sec": TARGET_SESSION_SEC,
                    "elapsed_sec": int(elapsed),
                    "game_pid": pid,
                    "game_foreground": fg,
                    "ram_mb": ram_mb,
                    "gpu_util": gpu["gpu_util"],
                    "vram_mb": gpu["vram_mb"],
                    "presentmon_active": is_presentmon_running(),
                    "editor_health": ed_health,
                    "last_crash_epoch": last_crash_epoch or None,
                    "last_crash_summary": last_crash_summary or None,
                }
            )

        else:
            # Handle clean or crashed exit transition
            if current_session_file and session_start_time:
                elapsed = int(round(now - session_start_time))
                log_msg(f"GAME_SESSION_ENDED: sid={current_session_id} elapsed={elapsed}s")
                if presentmon_proc is not None:
                    try:
                        presentmon_proc.wait(timeout=5.0)
                    except Exception:
                        pass
                    presentmon_proc = None

                # Final crash sweep after exit
                exit_crash = check_recent_game_crashes(since_epoch=session_start_time)
                if exit_crash and exit_crash["epoch"] > last_crash_epoch + 0.5:
                    last_crash_epoch = record_crash_event_once(exit_crash, last_crash_epoch)
                    last_crash_summary = exit_crash["summary"]

                time.sleep(2.5)  # Allow closing PresentMon/AutoPlay reports to flush before RSI eval
                trigger_rsi_engine(reason=f"game_exited_after_{elapsed}s")
                check_and_apply_pending_on_idle()

                current_session_id = None
                current_session_file = None
                session_start_time = None
                recorder_started_session = None
                priority_elevated_pid = None
                toggled_keys_in_session = []
                fg_accumulated_sec = 0.0
            elif (now - last_idle_pending_check_ts) >= 15.0:
                last_idle_pending_check_ts = now
                check_and_apply_pending_on_idle()

            update_status(
                {
                    "state": "IDLE_WAITING_FOR_GAME",
                    "description": "CrimsonDesert.exe 실행 대기 중 (RSI v4.1 자율 감시 활성)",
                    "session_duration_target_sec": TARGET_SESSION_SEC,
                    "elapsed_sec": 0,
                    "game_pid": None,
                    "game_foreground": False,
                    "presentmon_active": is_presentmon_running(),
                    "editor_health": ed_health,
                    "last_crash_epoch": last_crash_epoch or None,
                    "last_crash_summary": last_crash_summary or None,
                }
            )

        if once:
            break
        time.sleep(2.0 if running else 4.0)


def main() -> int:
    args = sys.argv[1:]
    if "--self-test" in args:
        return run_self_test()
    once = "--once" in args
    run_loop(once=once)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
