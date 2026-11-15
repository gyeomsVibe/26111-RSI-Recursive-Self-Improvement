# -*- coding: utf-8 -*-
"""
Crimson Desert - Recursive Self-Improvement (RSI) Autonomous Optimizer v4
=========================================================================
Closed-loop, evidence-gated performance & editor environment optimizer:
- Ingests real DX12 PresentMon v2 frame pacing CSVs (*-frames.csv) paired with GPU/RAM telemetry.
- Zero fake FPS hallucination when PresentMon telemetry is present.
- Multi-objective utility reward (FPS + P99 frame pacing + Stutter + Latency/Independent Flip + Clarity + Headroom).
- Contextual Safe UCB1 + EWMA + Directional Hysteresis + Automatic Rollback Circuit Breaker.
- Strict BlackSpace Engine XML validation (discrete scale enums {"AA", "67%", "59%"} and 3 graphics tags only).
- Cross-diagnoses Live Editor health (editor_evidence.json + live_editor_health.json + CheatConfig.ini + DWM Flip Mode).
"""

import os
import re
import sys
import csv
import glob
import json
import math
import time
import shutil
import ctypes
import tempfile
import statistics
import subprocess
import xml.etree.ElementTree as ET
from ctypes import wintypes
from datetime import datetime

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
RESEARCH_DIR = os.path.join(BASE_DIR, "research")
SESSIONS_DIR = os.path.join(RESEARCH_DIR, "sessions")
AUTOPLAY_DIR = os.path.join(RESEARCH_DIR, "autoplay_sessions")
RSI_DIR = os.path.join(RESEARCH_DIR, "rsi_telemetry")
SCALE_RANK = {"59%": 0, "67%": 1, "AA": 2}


def _csv_track_key(fpath):
    norm = fpath.replace("\\", "/")
    if "/autoplay_sessions/" in norm or "autoplay_sessions/" in norm:
        return f"{os.path.basename(os.path.dirname(fpath))}/{os.path.basename(fpath)}"
    return os.path.basename(fpath)
HISTORY_DIR = os.path.join(BASE_DIR, "backups", "rsi_history")
PROFILES_DIR = os.path.join(BASE_DIR, "profiles")
MASTER_PROFILE = os.path.join(PROFILES_DIR, "00-장시간무병목-고화질마스터-GTX1070최적.xml")
LIVE_CONFIG = os.path.expandvars(r"%LOCALAPPDATA%\Pearl Abyss\CD\save\user_engine_option_save.xml")
STATE_FILE = os.path.join(RSI_DIR, "rsi_state.json")
ARCHIVE_FILE = os.path.join(RSI_DIR, "rsi_history_archive.jsonl")
REPORT_MD = os.path.join(RSI_DIR, "LATEST_RSI_REPORT.md")
EDITOR_EVIDENCE_JSON = os.path.join(RSI_DIR, "editor_evidence.json")
LIVE_EDITOR_HEALTH_JSON = os.path.join(RSI_DIR, "live_editor_health.json")
CHEAT_INI_PATH = os.path.join(BASE_DIR, "CrimsonDesert_CheatConfig.ini")
CRASH_EVENTS_JSONL = os.path.join(RSI_DIR, "crash_events.jsonl")
UNIFIED_SUMMARY_JSON = os.path.join(RSI_DIR, "unified_game_rsi_summary.json")
LAST_CRASH_FILE = os.path.expandvars(r"%LOCALAPPDATA%\Pearl Abyss\DumpCache\last_crash")

os.makedirs(RSI_DIR, exist_ok=True)
os.makedirs(HISTORY_DIR, exist_ok=True)

VALID_FSR_SCALES = ("AA", "67%", "59%")
VALID_QUALITY_LEVELS = ("Low", "Medium", "High")

# Verified discrete BlackSpace Engine tuning arms (safe GTX 1070 8GB Pareto frontier)
ARMS = {
    "AA_UltraSharp": {
        "arm_id": "AA_UltraSharp",
        "label": "네이티브 1080p AA 극선명 (High 모델·물 / Med 텍스처)",
        "scale_display": "AA",
        "texture": "Medium",
        "model": "High",
        "water": "High",
        "clarity_bonus": 12.0,
        "vram_est_mb": 4200,
    },
    "AA_BalancedWater": {
        "arm_id": "AA_BalancedWater",
        "label": "네이티브 1080p AA 경량 물품질 (High 모델 / Med 텍스처·물)",
        "scale_display": "AA",
        "texture": "Medium",
        "model": "High",
        "water": "Medium",
        "clarity_bonus": 10.0,
        "vram_est_mb": 4100,
    },
    "FSR67_QualityHigh": {
        "arm_id": "FSR67_QualityHigh",
        "label": "FSR 3.1 Quality(67%) + High 풀옵션 (고선명 텍스처·대폭 프레임 향상)",
        "scale_display": "67%",
        "texture": "High",
        "model": "High",
        "water": "High",
        "clarity_bonus": 10.5,
        "vram_est_mb": 4900,
    },
    "FSR67_CombatLock": {
        "arm_id": "FSR67_CombatLock",
        "label": "FSR 3.1 Quality(67%) GTX1070 고화질·무병목 마스터 (High 텍스처 / Med 모델·물)",
        "scale_display": "67%",
        "texture": "High",
        "model": "Medium",
        "water": "Medium",
        "clarity_bonus": 10.2,
        "vram_est_mb": 4250,
    },
    "FSR59_UltraResponsive": {
        "arm_id": "FSR59_UltraResponsive",
        "label": "FSR 3.1 Balanced(59%) 초저지연 반응성 마스터 (Low 모델·물 / High 텍스처)",
        "scale_display": "59%",
        "texture": "High",
        "model": "Low",
        "water": "Low",
        "clarity_bonus": 9.0,
        "vram_est_mb": 4300,
    },
}

DEFAULT_ARM_ID = "AA_UltraSharp"
HYSTERESIS_MIN_GAIN = 3.5
UCB_EXPLORATION_C = 4.0
EWMA_ALPHA = 0.65
ROLLBACK_DROP_THRESHOLD = 12.0


def clamp(val, lo, hi):
    return max(lo, min(hi, val))


def is_game_running(exe_name="CrimsonDesert.exe"):
    """Zero-subprocess Win32 snapshot check for CrimsonDesert.exe."""
    try:
        TH32CS_SNAPPROCESS = 0x00000002

        class PROCESSENTRY32(ctypes.Structure):
            _fields_ = [
                ("dwSize", wintypes.DWORD),
                ("cntUsage", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)),
                ("th32ModuleID", wintypes.DWORD),
                ("cntThreads", wintypes.DWORD),
                ("th32ParentProcessID", wintypes.DWORD),
                ("pcPriClassBase", ctypes.c_long),
                ("dwFlags", wintypes.DWORD),
                ("szExeFile", ctypes.c_char * 260),
            ]

        k32 = ctypes.windll.kernel32
        snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if snap == -1:
            return False
        try:
            pe = PROCESSENTRY32()
            pe.dwSize = ctypes.sizeof(PROCESSENTRY32)
            ok = k32.Process32First(snap, ctypes.byref(pe))
            target = exe_name.lower()
            while ok:
                name = pe.szExeFile.decode("ascii", errors="ignore").lower()
                if name == target:
                    return True
                ok = k32.Process32Next(snap, ctypes.byref(pe))
        finally:
            k32.CloseHandle(snap)
    except Exception:
        pass
    return False


def build_arm_params(arm_id, prev_params=None):
    arm = ARMS.get(arm_id, ARMS[DEFAULT_ARM_ID])
    prev = prev_params or {}
    return {
        "arm_id": arm["arm_id"],
        "scale_display": arm["scale_display"],
        "texture": arm["texture"],
        "model": arm["model"],
        "water": arm["water"],
        "lighting": prev.get("lighting", "Medium"),
        "reflection": prev.get("reflection", "Medium"),
        "foliage": prev.get("foliage", "Medium"),
        "effect": prev.get("effect", "Medium"),
        "shadow": prev.get("shadow", "Low"),
        "fog": prev.get("fog", "Low"),
        "simulation": prev.get("simulation", "Low"),
        "fov": prev.get("fov", "1.250000"),
        "zoom": prev.get("zoom", "1.180000"),
    }


def detect_arm_from_params(params):
    if not isinstance(params, dict):
        return DEFAULT_ARM_ID
    aid = params.get("arm_id")
    if aid in ARMS:
        return aid
    sc = params.get("scale_display", "AA")
    tex = params.get("texture", "Medium")
    wat = params.get("water", "High")
    if sc == "59%":
        return "FSR59_UltraResponsive"
    if sc == "AA":
        return "AA_BalancedWater" if wat == "Medium" else "AA_UltraSharp"
    return "FSR67_QualityHigh" if tex == "High" else "FSR67_CombatLock"


def init_arm_stats():
    return {
        aid: {
            "pulls": 0,
            "ewma_reward": None,
            "last_fps": 0.0,
            "last_p99_ms": 0.0,
            "last_reward": 0.0,
            "crashes": 0,
        }
        for aid in ARMS
    }


def compact_history_archive(history, archive_path=ARCHIVE_FILE, keep_last=25):
    if len(history) <= keep_last:
        return history
    overflow = history[:-keep_last]
    kept = history[-keep_last:]
    try:
        with open(archive_path, "a", encoding="utf-8") as af:
            for item in overflow:
                af.write(json.dumps(item, ensure_ascii=False) + "\n")
    except Exception:
        pass
    return kept


def load_state(state_file=STATE_FILE, archive_file=ARCHIVE_FILE):
    if os.path.exists(state_file):
        try:
            with open(state_file, "r", encoding="utf-8-sig") as f:
                data = json.load(f)
            if isinstance(data, dict):
                data.setdefault("processed_csvs", [])
                data.setdefault("history", [])
                data.setdefault("active_parameters", build_arm_params(DEFAULT_ARM_ID))

                # One-time migration from legacy v3 state -> v4 closed-loop state
                if data.get("engine_version", 3) < 4:
                    data["history"] = compact_history_archive(data.get("history", []), archive_file, keep_last=20)
                    # Re-queue Session #1 live baseline telemetry if v3 swallowed it without v4 PresentMon metrics
                    requeue = {
                        "20261114-124216-baseline_phase2-frames.csv",
                        "20261114-124216-baseline_phase2-gpu.csv",
                        "session_20261114_124222.csv",
                    }
                    data["processed_csvs"] = [x for x in data.get("processed_csvs", []) if x not in requeue][-60:]
                    data["engine_version"] = 4
                    data["active_arm"] = detect_arm_from_params(data.get("active_parameters"))
                    data["active_parameters"] = build_arm_params(data["active_arm"], data.get("active_parameters"))
                    data["arm_stats"] = init_arm_stats()
                    data["pending_apply_on_exit"] = False
                else:
                    if "arm_stats" not in data or not isinstance(data["arm_stats"], dict):
                        data["arm_stats"] = init_arm_stats()
                    for aid in ARMS:
                        data["arm_stats"].setdefault(aid, init_arm_stats()[aid])
                    if data.get("active_arm") not in ARMS:
                        data["active_arm"] = detect_arm_from_params(data.get("active_parameters"))
                return data
        except Exception:
            pass

    return {
        "engine_version": 4,
        "current_generation": 1,
        "last_updated": datetime.now().isoformat(timespec="seconds"),
        "processed_csvs": [],
        "history": [],
        "convergence_score": 0.0,
        "total_analyzed_sessions": 0,
        "active_arm": DEFAULT_ARM_ID,
        "active_parameters": build_arm_params(DEFAULT_ARM_ID),
        "arm_stats": init_arm_stats(),
        "pending_apply_on_exit": False,
        "last_bottleneck": "UNMEASURED",
        "last_reward_score": 0.0,
        "last_measured_fps": 0.0,
    }


class _RsiMutex:
    """Cross-process Windows named mutex serializing RSI state reads/writes."""

    def __init__(self, name="Local\\CrimsonDesert_RsiEngine_v4_Mutex", timeout_ms=20000):
        self.name = name
        self.timeout_ms = timeout_ms
        self.handle = None
        self.acquired = False

    def __enter__(self):
        k32 = ctypes.windll.kernel32
        k32.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
        k32.CreateMutexW.restype = wintypes.HANDLE
        k32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        k32.WaitForSingleObject.restype = wintypes.DWORD
        self.handle = k32.CreateMutexW(None, False, self.name)
        if not self.handle:
            raise OSError("CreateMutexW failed")
        rc = k32.WaitForSingleObject(self.handle, self.timeout_ms)
        if rc in (0, 0x80):  # WAIT_OBJECT_0 or WAIT_ABANDONED
            self.acquired = True
            return self
        k32.CloseHandle(self.handle)
        self.handle = None
        raise TimeoutError(f"Timeout acquiring RSI mutex {self.name} (rc={rc})")

    def __exit__(self, exc_type, exc_val, exc_tb):
        k32 = ctypes.windll.kernel32
        if self.handle:
            if self.acquired:
                try:
                    k32.ReleaseMutex(self.handle)
                except Exception:
                    pass
            try:
                k32.CloseHandle(self.handle)
            except Exception:
                pass
            self.handle = None
        self.acquired = False


def save_state(state, state_file=STATE_FILE, archive_file=ARCHIVE_FILE):
    state["engine_version"] = 4
    state["last_updated"] = datetime.now().isoformat(timespec="seconds")
    state["history"] = compact_history_archive(state.get("history", []), archive_file, keep_last=25)
    if len(state.get("processed_csvs", [])) > 120:
        state["processed_csvs"] = state["processed_csvs"][-120:]
    tmp_path = f"{state_file}.tmp.{os.getpid()}"
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2, ensure_ascii=False)
    for attempt in range(5):
        try:
            os.replace(tmp_path, state_file)
            return
        except OSError:
            if attempt == 4:
                raise
            time.sleep(0.05)


def _read_presentmon_csv(fpath):
    intervals = []
    gpu_busy = []
    cpu_busy = []
    render_lat = []
    display_lat = []
    present_modes = {}
    indep_flip_count = 0
    total_mode_rows = 0

    with open(fpath, "r", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for r in reader:
            raw_int = r.get("MsBetweenPresents", "")
            if not raw_int or raw_int == "NA":
                continue
            try:
                ms = float(raw_int)
                if 1.0 <= ms <= 500.0:
                    intervals.append(ms)
                else:
                    continue
            except ValueError:
                continue

            for col, bucket in (
                ("MsGPUBusy", gpu_busy),
                ("MsCPUBusy", cpu_busy),
                ("MsRenderPresentLatency", render_lat),
                ("MsBetweenDisplayChange", display_lat),
            ):
                val = r.get(col, "")
                if val and val != "NA":
                    try:
                        fv = float(val)
                        if 0.0 <= fv <= 500.0:
                            bucket.append(fv)
                    except ValueError:
                        pass

            pm = (r.get("PresentMode") or "").strip()
            if pm and pm != "NA":
                total_mode_rows += 1
                present_modes[pm] = present_modes.get(pm, 0) + 1
                if "independent flip" in pm.lower():
                    indep_flip_count += 1

    return {
        "intervals": intervals,
        "gpu_busy": gpu_busy,
        "cpu_busy": cpu_busy,
        "render_lat": render_lat,
        "display_lat": display_lat,
        "present_modes": present_modes,
        "indep_flip_count": indep_flip_count,
        "total_mode_rows": total_mode_rows,
    }


def _read_gpu_csv(fpath):
    vram_used = []
    gpu_util = []
    ram_used = []
    gpu_temps = []
    gpu_clocks = []
    fg_flags = []

    with open(fpath, "r", encoding="utf-8", errors="replace") as f:
        reader = csv.DictReader(f)
        for r in reader:
            raw_vram = r.get("VramUsedMB") or r.get("vram_mb")
            if raw_vram:
                try:
                    vram_used.append(float(raw_vram))
                except ValueError:
                    pass
            raw_util = r.get("GpuUtilPct") or r.get("gpu_util")
            if raw_util:
                try:
                    gpu_util.append(float(raw_util))
                except ValueError:
                    pass
            raw_ws = r.get("WorkingSetMB") or r.get("ram_mb")
            if raw_ws:
                try:
                    ws = float(raw_ws)
                    if ws > 0:
                        ram_used.append(ws)
                except ValueError:
                    pass
            raw_temp = r.get("GpuTempC") or r.get("gpu_temp_c")
            if raw_temp:
                try:
                    gpu_temps.append(float(raw_temp))
                except ValueError:
                    pass
            raw_clk = r.get("GpuClockMHz") or r.get("gpu_clock")
            if raw_clk:
                try:
                    gpu_clocks.append(float(raw_clk))
                except ValueError:
                    pass
            raw_fg = r.get("Foreground") if r.get("Foreground") in ("0", "1") else (r.get("foreground") if r.get("foreground") in ("0", "1") else None)
            if raw_fg is not None:
                fg_flags.append(int(raw_fg))

    return {
        "vram_used": vram_used,
        "gpu_util": gpu_util,
        "ram_used": ram_used,
        "gpu_temps": gpu_temps,
        "gpu_clocks": gpu_clocks,
        "fg_flags": fg_flags,
    }


def _detect_recent_crash(frame_mtimes):
    """Checks if a crash event or Pearl Abyss last_crash occurred near the analyzed telemetry files."""
    if not frame_mtimes:
        return False, ""
    min_t = min(frame_mtimes) - 30.0
    max_t = max(frame_mtimes) + 45.0

    try:
        if os.path.exists(LAST_CRASH_FILE):
            mt = os.path.getmtime(LAST_CRASH_FILE)
            if min_t <= mt <= max_t:
                return True, f"DumpCache last_crash @ {datetime.fromtimestamp(mt).strftime('%H:%M:%S')}"
    except Exception:
        pass

    try:
        if os.path.exists(CRASH_EVENTS_JSONL):
            with open(CRASH_EVENTS_JSONL, "r", encoding="utf-8") as cf:
                for line in cf:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        ev = json.loads(line)
                        ts_ep = float(ev.get("epoch", 0))
                        if min_t <= ts_ep <= max_t:
                            return True, ev.get("summary", "Windows crash event")
                    except Exception:
                        pass
    except Exception:
        pass
    return False, ""


def analyze_unprocessed_sessions(state, sessions_dir=SESSIONS_DIR, rsi_dir=RSI_DIR, limit_frames=1):
    processed = set(state.get("processed_csvs", []))
    all_frame_csvs = sorted(glob.glob(os.path.join(sessions_dir, "*-frames.csv")))
    all_gpu_csvs = (
        sorted(glob.glob(os.path.join(sessions_dir, "*-gpu.csv")))
        + sorted(glob.glob(os.path.join(sessions_dir, "*-watcher.csv")))
        + sorted(glob.glob(os.path.join(AUTOPLAY_DIR, "*", "gpu_samples.csv")))
        + sorted(glob.glob(os.path.join(rsi_dir, "session_*.csv")))
    )

    unproc_frames_raw = [f for f in all_frame_csvs if _csv_track_key(f) not in processed]
    unproc_gpu_raw = [f for f in all_gpu_csvs if _csv_track_key(f) not in processed]

    marked_names = []
    watermark_mtime = float(state.get("watermark_mtime", 0.0) or 0.0)
    if watermark_mtime > 0.0:
        def _filter_since_watermark(paths):
            kept = []
            for p in paths:
                try:
                    if os.path.getmtime(p) <= watermark_mtime:
                        k = _csv_track_key(p)
                        if k not in marked_names:
                            marked_names.append(k)
                        continue
                except Exception:
                    pass
                kept.append(p)
            return kept
        unproc_frames = _filter_since_watermark(unproc_frames_raw)
        unproc_gpu = _filter_since_watermark(unproc_gpu_raw)
    else:
        unproc_frames = unproc_frames_raw
        unproc_gpu = unproc_gpu_raw

    valid_pm_files = []
    intervals = []
    gpu_busy = []
    cpu_busy = []
    render_lat = []
    present_modes = {}
    indep_flip_count = 0
    total_mode_rows = 0

    # Evaluate the latest unprocessed PresentMon CSVs cleanly (never diluting with fake nvidia-smi FPS!)
    if unproc_frames:
        # Skip tiny invalid/empty PresentMon CSVs, mark them processed so they don't clog queue
        usable_frames = []
        for fpath in unproc_frames:
            pm_data = _read_presentmon_csv(fpath)
            if len(pm_data["intervals"]) >= 25:
                usable_frames.append((fpath, pm_data))
            else:
                marked_names.append(_csv_track_key(fpath))

        if usable_frames:
            if limit_frames == 1:
                latest_mt = os.path.getmtime(usable_frames[-1][0])
                recent_group = [
                    (fp, pd)
                    for fp, pd in usable_frames
                    if abs(latest_mt - os.path.getmtime(fp)) <= 600.0
                ]
                for fp, _ in recent_group:
                    k = _csv_track_key(fp)
                    if k not in marked_names:
                        marked_names.append(k)
                major = [u for u in recent_group if len(u[1]["intervals"]) >= 250]
                chosen = [major[-1]] if major else [recent_group[-1]]
            else:
                chosen = usable_frames[-limit_frames:]
            for fpath, pm_data in chosen:
                valid_pm_files.append(fpath)
                k = _csv_track_key(fpath)
                if k not in marked_names:
                    marked_names.append(k)
                intervals.extend(pm_data["intervals"])
                gpu_busy.extend(pm_data["gpu_busy"])
                cpu_busy.extend(pm_data["cpu_busy"])
                render_lat.extend(pm_data["render_lat"])
                indep_flip_count += pm_data["indep_flip_count"]
                total_mode_rows += pm_data["total_mode_rows"]
                for k, v in pm_data["present_modes"].items():
                    present_modes[k] = present_modes.get(k, 0) + v

    # Gather companion GPU / RAM CSVs (prefer matching timestamp prefix or unprocessed gpu CSVs, fallback latest gpu CSV)
    companion_gpu = []
    if valid_pm_files:
        target_mtime = os.path.getmtime(valid_pm_files[-1])
        for gf in unproc_gpu:
            try:
                if abs(os.path.getmtime(gf) - target_mtime) <= 900.0:
                    companion_gpu.append(gf)
                    marked_names.append(_csv_track_key(gf))
            except Exception:
                pass
        if not companion_gpu and all_gpu_csvs:
            # Find nearest timestamp GPU CSV even if watcher processed it first
            nearest = min(all_gpu_csvs, key=lambda p: abs(os.path.getmtime(p) - target_mtime))
            if abs(os.path.getmtime(nearest) - target_mtime) <= 900.0:
                companion_gpu.append(nearest)
    elif unproc_gpu:
        companion_gpu = unproc_gpu[-2:]
        for gf in companion_gpu:
            marked_names.append(_csv_track_key(gf))

    vram_used, gpu_util, ram_used, gpu_temps, gpu_clocks = [], [], [], [], []
    for gf in companion_gpu:
        gdata = _read_gpu_csv(gf)
        vram_used.extend(gdata["vram_used"])
        gpu_util.extend(gdata["gpu_util"])
        ram_used.extend(gdata["ram_used"])
        gpu_temps.extend(gdata["gpu_temps"])
        gpu_clocks.extend(gdata["gpu_clocks"])

    if not vram_used:
        ap_report = os.path.join(RSI_DIR, "auto_play_final_report.json")
        if os.path.exists(ap_report):
            try:
                ref_files = valid_pm_files or companion_gpu
                ref_mtime = max((os.path.getmtime(p) for p in ref_files if os.path.exists(p)), default=time.time())
                if abs(os.path.getmtime(ap_report) - ref_mtime) <= 900.0:
                    with open(ap_report, "r", encoding="utf-8", errors="replace") as af:
                        ap = json.load(af)
                    if float(ap.get("peak_vram_mb", 0.0) or 0.0) > 0.0:
                        vram_used.append(float(ap["peak_vram_mb"]))
                    if not ram_used and float(ap.get("peak_ram_mb", 0.0) or 0.0) > 0.0:
                        ram_used.append(float(ap["peak_ram_mb"]))
                    if not gpu_temps and float(ap.get("peak_gpu_temp_c", 0.0) or 0.0) > 0.0:
                        gpu_temps.append(float(ap["peak_gpu_temp_c"]))
            except Exception:
                pass

    telemetry_source = "presentmon_v2"
    if not intervals:
        # Strict fallback only when NO real PresentMon frame file was produced
        if not gpu_util:
            return None, marked_names
        telemetry_source = "nvidia_smi_fallback"
        for gu in gpu_util:
            est_fps = 60.0 if gu < 94.0 else max(30.0, 60.0 - (gu - 94.0) * 3.5)
            intervals.append(1000.0 / est_fps)

    sorted_iv = sorted(intervals)
    n_iv = len(sorted_iv)
    mean_iv = statistics.mean(sorted_iv)
    mean_fps = 1000.0 / mean_iv if mean_iv > 0 else 0.0
    p95_ms = sorted_iv[min(n_iv - 1, int(n_iv * 0.95))]
    p99_ms = sorted_iv[min(n_iv - 1, int(n_iv * 0.99))]
    top1_slice = sorted_iv[int(n_iv * 0.99):] or [p99_ms]
    slowest_1pct_fps = 1000.0 / statistics.mean(top1_slice)
    stutter_ratio = sum(1 for ms in sorted_iv if ms > 33.3) / max(1, n_iv)
    severe_stutter_ratio = sum(1 for ms in sorted_iv if ms > 50.0) / max(1, n_iv)
    indep_flip_pct = round(100.0 * indep_flip_count / total_mode_rows, 2) if total_mode_rows > 0 else 0.0

    mtimes = [os.path.getmtime(p) for p in (valid_pm_files or companion_gpu) if os.path.exists(p)]
    crash_detected, crash_note = _detect_recent_crash(mtimes)

    metrics = {
        "telemetry_source": telemetry_source,
        "analyzed_sessions": len(valid_pm_files) if valid_pm_files else len(companion_gpu),
        "primary_file": os.path.basename(valid_pm_files[-1]) if valid_pm_files else (os.path.basename(companion_gpu[-1]) if companion_gpu else ""),
        "total_frames": n_iv,
        "mean_fps": round(mean_fps, 2),
        "mean_present_ms": round(mean_iv, 2),
        "p95_interval_ms": round(p95_ms, 2),
        "p99_interval_ms": round(p99_ms, 2),
        "slowest_1pct_fps": round(slowest_1pct_fps, 2),
        "stutter_ratio": round(stutter_ratio, 4),
        "severe_stutter_ratio": round(severe_stutter_ratio, 4),
        "mean_gpu_busy_ms": round(statistics.mean(gpu_busy), 2) if gpu_busy else 0.0,
        "mean_cpu_busy_ms": round(statistics.mean(cpu_busy), 2) if cpu_busy else 0.0,
        "mean_render_present_latency_ms": round(statistics.mean(render_lat), 2) if render_lat else 0.0,
        "independent_flip_pct": indep_flip_pct,
        "present_modes": present_modes,
        "mean_vram_mb": round(statistics.mean(vram_used), 1) if vram_used else 0.0,
        "peak_vram_mb": round(max(vram_used), 1) if vram_used else 0.0,
        "mean_ram_mb": round(statistics.mean(ram_used), 1) if ram_used else 0.0,
        "peak_ram_mb": round(max(ram_used), 1) if ram_used else 0.0,
        "mean_gpu_util_pct": round(statistics.mean(gpu_util), 1) if gpu_util else 0.0,
        "max_gpu_temp_c": round(max(gpu_temps), 1) if gpu_temps else 0.0,
        "mean_gpu_clock_mhz": round(statistics.mean(gpu_clocks), 1) if gpu_clocks else 0.0,
        "crash_detected": crash_detected,
        "crash_note": crash_note,
        "latest_csv_mtime": round(max(mtimes), 3) if mtimes else 0.0,
    }
    return metrics, marked_names


def compute_reward_and_bottleneck(metrics, arm_id):
    arm = ARMS.get(arm_id, ARMS[DEFAULT_ARM_ID])
    mean_fps = float(metrics.get("mean_fps", 0.0))
    p99_ms = float(metrics.get("p99_interval_ms", 50.0))
    stutter = float(metrics.get("stutter_ratio", 0.0))
    lat_ms = float(metrics.get("mean_render_present_latency_ms", 25.0)) or 25.0
    indep_pct = float(metrics.get("independent_flip_pct", 0.0))
    gpu_busy = float(metrics.get("mean_gpu_busy_ms", 0.0))
    cpu_busy = float(metrics.get("mean_cpu_busy_ms", 0.0))
    gpu_util = float(metrics.get("mean_gpu_util_pct", 0.0))
    peak_vram = float(metrics.get("peak_vram_mb", 0.0))
    max_temp = float(metrics.get("max_gpu_temp_c", 0.0))
    crashed = bool(metrics.get("crash_detected", False))

    fps_score = 25.0 * clamp((mean_fps - 20.0) / 40.0, 0.0, 1.0)
    p99_score = 20.0 * clamp((55.0 - p99_ms) / 35.0, 0.0, 1.0)
    consistency_score = 20.0 * clamp(1.0 - stutter * 1.4, 0.0, 1.0)
    latency_score = 10.0 * clamp((50.0 - lat_ms) / 35.0, 0.0, 1.0) + 5.0 * clamp(indep_pct / 100.0, 0.0, 1.0)
    clarity_score = float(arm.get("clarity_bonus", 10.0))
    headroom_score = 8.0
    if peak_vram >= 7200.0:
        headroom_score -= 10.0
    elif peak_vram >= 6600.0:
        headroom_score -= 4.0
    if max_temp >= 83.0:
        headroom_score -= 6.0
    if crashed:
        headroom_score -= 20.0

    reward = round(clamp(fps_score + p99_score + consistency_score + latency_score + clarity_score + headroom_score, 0.0, 100.0), 2)

    # Classify primary runtime bottleneck
    if crashed and metrics.get("last_xml_changed_recently", False):
        bottleneck = "CRASH_RECOVERY"
    elif peak_vram >= 7100.0:
        bottleneck = "VRAM_PRESSURE"
    elif ((gpu_busy > cpu_busy * 1.35 and gpu_busy > 18.0) or gpu_util >= 95.0) and (mean_fps < 48.0 or p99_ms > 34.0):
        bottleneck = "GPU_SHADER_BOUND"
    elif indep_pct < 30.0 and lat_ms > 30.0 and metrics.get("telemetry_source") == "presentmon_v2":
        bottleneck = "DWM_COMPOSED_LATENCY"
    elif cpu_busy > gpu_busy and cpu_busy > 16.0 and p99_ms > 30.0:
        bottleneck = "CPU_GAME_THREAD_BOUND"
    elif mean_fps >= 46.0 and p99_ms <= 33.3 and not crashed:
        bottleneck = "OPTIMAL_EQUILIBRIUM"
    elif gpu_util >= 94.0 or gpu_busy > 21.0:
        bottleneck = "GPU_SHADER_BOUND"
    else:
        bottleneck = "MONITORING_CONVERGENCE"

    return reward, bottleneck


def evolve_parameters(state, metrics):
    curr_arm = state.get("active_arm", DEFAULT_ARM_ID)
    if curr_arm not in ARMS:
        curr_arm = detect_arm_from_params(state.get("active_parameters"))

    arm_stats = state.setdefault("arm_stats", init_arm_stats())
    for aid in ARMS:
        arm_stats.setdefault(aid, init_arm_stats()[aid])

    reward, bottleneck = compute_reward_and_bottleneck(metrics, curr_arm)

    # Update current arm EWMA statistics
    st_cur = arm_stats[curr_arm]
    st_cur["pulls"] = int(st_cur.get("pulls", 0)) + 1
    prev_ewma = st_cur.get("ewma_reward")
    new_ewma = reward if prev_ewma is None else round(EWMA_ALPHA * reward + (1.0 - EWMA_ALPHA) * float(prev_ewma), 2)
    st_cur["ewma_reward"] = new_ewma
    st_cur["last_reward"] = reward
    st_cur["last_fps"] = metrics.get("mean_fps", 0.0)
    st_cur["last_p99_ms"] = metrics.get("p99_interval_ms", 0.0)
    if metrics.get("crash_detected"):
        st_cur["crashes"] = int(st_cur.get("crashes", 0)) + 1

    actions = []
    peak_vram = float(metrics.get("peak_vram_mb", 0.0))
    mean_fps = float(metrics.get("mean_fps", 0.0))
    p99_ms = float(metrics.get("p99_interval_ms", 0.0))
    gpu_busy = float(metrics.get("mean_gpu_busy_ms", 0.0))
    cpu_busy = float(metrics.get("mean_cpu_busy_ms", 0.0))
    gpu_util = float(metrics.get("mean_gpu_util_pct", 0.0))
    indep_pct = float(metrics.get("independent_flip_pct", 0.0))
    prev_arm = state.get("prev_arm")
    prev_reward = float(state.get("last_reward_score", 0.0) or 0.0)
    total_pulls = sum(int(v.get("pulls", 0)) for v in arm_stats.values()) + 1

    # 1. Safety Circuit Breaker: automatic rollback if last transition dropped reward >= 12 pts or caused crash
    next_arm = curr_arm
    decision_mode = "HOLD"

    safe_proven = [
        (aid, v)
        for aid, v in arm_stats.items()
        if aid != curr_arm and int(v.get("pulls", 0)) > 0 and int(v.get("crashes", 0)) == 0 and v.get("ewma_reward") is not None
    ]
    if prev_arm and prev_arm != curr_arm and prev_reward > 0.0 and (
        reward < prev_reward - ROLLBACK_DROP_THRESHOLD or (metrics.get("crash_detected") and bottleneck == "CRASH_RECOVERY")
    ):
        if safe_proven:
            best_safe_aid = max(safe_proven, key=lambda kv: float(kv[1]["ewma_reward"]))[0]
        else:
            best_safe_aid = prev_arm
        next_arm = best_safe_aid
        decision_mode = "CIRCUIT_BREAKER_ROLLBACK"
        actions.append(
            f"[서킷브레이커 롤백] '{curr_arm}' 보상 하락 ({reward} vs 직전 {prev_reward}) 또는 크래시 감지 → 검증된 '{next_arm}' 자동 복원"
        )
    elif bottleneck == "OPTIMAL_EQUILIBRIUM" and not metrics.get("crash_detected"):
        next_arm = curr_arm
        decision_mode = "EQUILIBRIUM_HOLD"
        actions.append(
            f"[최적 평형 유지] '{curr_arm}' 평균 {mean_fps} FPS / P99 {p99_ms}ms / 보상 {reward}점 (불필요한 변경 억제)"
        )
    else:
        # 2. Contextual Safe UCB1 Evaluation across valid discrete XML arms
        curr_rank = SCALE_RANK.get(ARMS[curr_arm]["scale_display"], 1)
        scores = {}
        for aid, spec in ARMS.items():
            # Hard VRAM safety constraint: disallow High Texture when VRAM nears GTX 1070 8GB ceiling
            if peak_vram >= 6800.0 and spec["texture"] == "High":
                continue

            cand_rank = SCALE_RANK.get(spec["scale_display"], 1)
            # Physical monotonicity guard: never increase pixel count (67% -> AA) while GPU-shader-bound below 48 FPS
            if bottleneck == "GPU_SHADER_BOUND" and cand_rank > curr_rank and mean_fps < 48.0:
                continue

            ast = arm_stats[aid]
            pulls = int(ast.get("pulls", 0))
            base_val = float(ast["ewma_reward"]) if ast.get("ewma_reward") is not None else float(new_ewma)
            ucb_bonus = UCB_EXPLORATION_C * math.sqrt(math.log(total_pulls + 1.0) / (pulls + 1.0))

            # Evidence-grounded contextual physics prior
            ctx_bonus = 0.0
            if bottleneck == "GPU_SHADER_BOUND":
                if cand_rank == 0:
                    if curr_rank == 2:
                        ctx_bonus += 11.0
                    elif curr_arm == "FSR67_CombatLock" or (curr_rank == 1 and mean_fps < 31.5):
                        ctx_bonus += 16.5
                elif cand_rank < curr_rank:
                    # Downscaling resolution cuts GPU fragment shader load by ~55%
                    if spec["texture"] == "High" and peak_vram < 5800.0:
                        ctx_bonus += 16.0  # Upgrade Texture to High using 4GB+ free VRAM headroom!
                    else:
                        ctx_bonus += 13.0
                elif cand_rank == curr_rank:
                    if ARMS[curr_arm]["water"] == "High" and spec["water"] == "Medium":
                        ctx_bonus += 5.0
                    elif ARMS[curr_arm]["water"] == "Medium" and spec["water"] == "High" and mean_fps < 42.0:
                        ctx_bonus -= 2.5
            elif bottleneck == "VRAM_PRESSURE":
                if spec["texture"] == "Medium":
                    ctx_bonus += 18.0
            elif bottleneck == "DWM_COMPOSED_LATENCY":
                if spec["arm_id"] == curr_arm:
                    ctx_bonus += 2.0

            scores[aid] = round(base_val + (ucb_bonus if aid != curr_arm else 0.0) + ctx_bonus, 2)

        cur_score = scores.get(curr_arm, new_ewma)
        if scores:
            best_aid = max(scores.keys(), key=lambda k: scores[k])
            gain = scores[best_aid] - cur_score
            if best_aid != curr_arm and gain >= HYSTERESIS_MIN_GAIN:
                next_arm = best_aid
                decision_mode = "UCB_EVOLVE"
                actions.append(
                    f"[자율 진화 전환] 병목 '{bottleneck}' (GPU Busy {gpu_busy}ms vs CPU {cpu_busy}ms, GPU {gpu_util}%, VRAM {peak_vram:.0f}MB) 해소 → '{curr_arm}' -> '{next_arm}' (예상 이득 +{gain:.1f}점)"
                )
            else:
                actions.append(
                    f"[히스테리시스 유지] '{curr_arm}' 보상 {reward}점 (전환 최소 임계치 +{HYSTERESIS_MIN_GAIN} 미달, 현재 조합 유지)"
                )

    if indep_pct < 50.0 and metrics.get("telemetry_source") == "presentmon_v2":
        actions.append(
            f"[DWM 플립 진단] 게임 보더리스 SyncInterval=1 기준 Composed:Flip 관측 (Independent Flip {indep_pct:.1f}%) — HUD 비활성 구간 ShowWindow(SW_HIDE) 자동 언맵 가동 중"
        )
    if metrics.get("crash_detected"):
        actions.append(f"[크래시 원격 기록] 세션 중 엔진 오류 감지됨 ({metrics.get('crash_note', 'DumpCache')}) — 에디터 훅/메모리 안전 가드 우선 점검")

    new_params = build_arm_params(next_arm, state.get("active_parameters"))
    summary = f"Gen RSI [{decision_mode}] {next_arm} (FSR {new_params['scale_display']} / Tex {new_params['texture']}): 실측 {mean_fps} FPS · P99 {p99_ms}ms · 보상 {reward}점 · 병목 {bottleneck}"

    return {
        "next_arm": next_arm,
        "prev_arm": curr_arm,
        "switched": next_arm != curr_arm,
        "decision_mode": decision_mode,
        "reward": reward,
        "bottleneck": bottleneck,
        "new_params": new_params,
        "actions": actions,
        "summary": summary,
    }


def validate_xml_strict(xml_path):
    """Strictly validates BlackSpace Engine XML constraints before accepting any write."""
    if not os.path.exists(xml_path):
        raise ValueError(f"XML not found: {xml_path}")
    tree = ET.parse(xml_path)
    root = tree.getroot()
    if root.tag != "EngineOptionSave":
        raise ValueError(f"Invalid XML root: {root.tag}")

    scale_el = root.find(".//EnumSelectResolutionScale[@Name='_upscaleResolution']")
    if scale_el is None:
        raise ValueError("Missing _upscaleResolution node")
    scale_val = scale_el.attrib.get("_select", "")
    if scale_val not in VALID_FSR_SCALES:
        raise ValueError(f"Forbidden BlackSpace scale enum '{scale_val}' (must be AA, 67%, or 59%)")

    gfx_el = root.find(".//EngineOptionGraphics")
    if gfx_el is None:
        raise ValueError("Missing EngineOptionGraphics")
    tpl_val = gfx_el.attrib.get("Template", "")
    if tpl_val != "/OS_M_Windows::Medium.engineoptiongraphics":
        raise ValueError(f"Graphics template must be Medium.engineoptiongraphics for high-quality lighting/AO, got '{tpl_val}'")
    children = list(gfx_el)
    if len(children) != 3:
        raise ValueError(f"BlackSpace CTD risk: EngineOptionGraphics must have exactly 3 child nodes, got {len(children)}")
    expected_names = {"_modelQualityLevelSelect", "_textureQualityLevelSelect", "_waterQualityLevelSelect"}
    got_names = {c.attrib.get("Name", "") for c in children}
    if got_names != expected_names:
        raise ValueError(f"Unexpected graphics child tags: {got_names}")
    for c in children:
        val = c.attrib.get("_select", "")
        if val not in VALID_QUALITY_LEVELS:
            raise ValueError(f"Invalid quality level '{val}' on {c.attrib.get('Name')}")
    return True


def update_xml_preset(xml_path, params):
    """Applies verified parameters strictly preserving BlackSpace 3-tag XML structure & atomic validation."""
    if not os.path.exists(xml_path):
        return False
    scale = params.get("scale_display", "AA")
    if scale not in VALID_FSR_SCALES:
        raise ValueError(f"Refusing forbidden _upscaleResolution='{scale}'")
    for k in ("model", "texture", "water"):
        if params.get(k, "High") not in VALID_QUALITY_LEVELS:
            raise ValueError(f"Refusing invalid {k}='{params.get(k)}'")

    with open(xml_path, "r", encoding="utf-8") as f:
        content = f.read()

    content = re.sub(
        r'<EnumSelectResolutionScale Name="_upscaleResolution" _select="[^"]*"\s*/>',
        f'<EnumSelectResolutionScale Name="_upscaleResolution" _select="{scale}" />',
        content,
    )
    target_tpl = "Medium"
    content = re.sub(
        r'Template="/OS_M_Windows::(?:Low|Medium|High)\.engineoptiongraphics"',
        f'Template="/OS_M_Windows::{target_tpl}.engineoptiongraphics"',
        content,
    )
    tag_map = {
        "_modelQualityLevelSelect": params.get("model", "High"),
        "_textureQualityLevelSelect": params.get("texture", "High"),
        "_waterQualityLevelSelect": params.get("water", "Medium"),
    }
    for tag_name, val in tag_map.items():
        pattern = rf'<EnumSelectQualityLevel Name="{tag_name}" _select="[^"]*"\s*/>'
        replacement = f'<EnumSelectQualityLevel Name="{tag_name}" _select="{val}" />'
        if re.search(pattern, content):
            content = re.sub(pattern, replacement, content)

    tmp_xml = xml_path + ".rsi.tmp"
    with open(tmp_xml, "w", encoding="utf-8") as f:
        f.write(content)
    validate_xml_strict(tmp_xml)
    os.replace(tmp_xml, xml_path)
    return True


def gather_cross_diagnosis(metrics):
    """Cross-analyzes EditorEvidence + LiveEditorHealth + CheatConfig.ini against PresentMon telemetry."""
    diag = {
        "editor_sessions": 0,
        "verified_keys": [],
        "ineffective_keys": [],
        "hooked_sites_count": 0,
        "player_ptr_valid": False,
        "live_silver": 0,
        "non_vanilla_ini_keys": [],
        "notes": [],
    }

    try:
        import EditorEvidence
        ev = EditorEvidence.summarize()
        diag["editor_sessions"] = len(ev.get("sessions", []))
        diag["verified_keys"] = ev.get("verified_keys", [])
        diag["ineffective_keys"] = ev.get("ineffective_keys", [])
        diag["hooked_sites_count"] = len(ev.get("hooked_sites_ever", []))
        cph = ev.get("cave_pointer_health", {})
        diag["player_ptr_valid"] = bool(cph.get("player_ptr_valid", False))
    except Exception:
        if os.path.exists(EDITOR_EVIDENCE_JSON):
            try:
                ev = json.load(open(EDITOR_EVIDENCE_JSON, "r", encoding="utf-8"))
                diag["verified_keys"] = ev.get("verified_keys", [])
                diag["ineffective_keys"] = ev.get("ineffective_keys", [])
                diag["hooked_sites_count"] = len(ev.get("hooked_sites_ever", []))
                diag["player_ptr_valid"] = bool(ev.get("cave_pointer_health", {}).get("player_ptr_valid", False))
            except Exception:
                pass

    if os.path.exists(LIVE_EDITOR_HEALTH_JSON):
        try:
            lh = json.load(open(LIVE_EDITOR_HEALTH_JSON, "r", encoding="utf-8"))
            diag["live_silver"] = int(lh.get("live_silver", 0) or 0)
            if lh.get("player_ptr_valid"):
                diag["player_ptr_valid"] = True
        except Exception:
            pass

    if os.path.exists(CHEAT_INI_PATH):
        defaults = {
            "DamageTakenMode": "0", "DamageDealtMultiplier": "1", "PlayerStaminaMode": "0",
            "CooldownMode": "0", "ExpMultiplier": "1", "SpeedMultiplier": "1", "JumpMultiplier": "1",
            "RomanceMultiplier": "1", "TamingMultiplier": "1", "MountSpeedMultiplier": "1",
            "MountStaminaMode": "0", "UnderwaterOxygenMultiplier": "1", "UnderwaterSpeed": "1",
            "CraftCostMode": "0", "CraftSuccessMultiplier": "1", "DurabilityMode": "0",
            "GearHiderMode": "0", "WeaponImbueMode": "0", "ThiefStealthMode": "0",
            "ThiefDetectionMode": "0", "PickpocketSuccessMode": "0", "BountyImmunityMode": "0",
            "ThiefGloveFenceMode": "0", "BagExpandSlots": "0", "SpecialConvenienceMode": "0",
            "MaxHealthVal": "0", "MaxStaminaVal": "0", "MaxSpiritVal": "0", "FreezeDaytime": "0",
        }
        try:
            with open(CHEAT_INI_PATH, "r", encoding="utf-8", errors="replace") as f:
                for line in f:
                    s = line.strip()
                    if not s or s.startswith(("[", ";", "#")) or "=" not in s:
                        continue
                    k, v = [x.strip() for x in s.split("=", 1)]
                    if k in defaults and v != defaults[k]:
                        diag["non_vanilla_ini_keys"].append(f"{k}={v}")
        except Exception:
            pass

    if diag["ineffective_keys"]:
        diag["notes"].append(f"무반응 토글 키 관측: {', '.join(diag['ineffective_keys'])}")
    else:
        diag["notes"].append("테스트된 모든 에디터 토글 키가 훅/케이브 메모리에 정상 반영됨")
    if metrics.get("independent_flip_pct", 0.0) < 50.0 and metrics.get("telemetry_source") == "presentmon_v2":
        diag["notes"].append(f"보더리스 DXGI SyncInterval=1 Composed:Flip 관측 ({metrics['independent_flip_pct']:.1f}% Independent Flip) → HUD 자동 언맵(ShowWindow SW_HIDE)으로 오버레이 지연 완화 가동")
    elif metrics.get("independent_flip_pct", 0.0) >= 80.0:
        diag["notes"].append(f"DXGI Independent Flip 달성 ({metrics['independent_flip_pct']:.1f}%) → 오버레이 DWM 병목 제거 확인")
    return diag


def _sanitize_history_entry(entry):
    if not isinstance(entry, dict):
        return {}
    metrics = dict(entry.get("metrics") or {})
    if "primary_file" in metrics and metrics["primary_file"]:
        metrics["primary_file"] = os.path.basename(str(metrics["primary_file"]))
    return {
        "generation": entry.get("generation"),
        "timestamp": entry.get("timestamp"),
        "processed_files": [os.path.basename(str(p)) for p in (entry.get("processed_files") or [])],
        "arm_before": entry.get("arm_before"),
        "arm_after": entry.get("arm_after"),
        "decision_mode": entry.get("decision_mode"),
        "reward_score": entry.get("reward_score"),
        "bottleneck": entry.get("bottleneck"),
        "metrics": metrics,
        "parameters": entry.get("parameters"),
        "snapshot": os.path.basename(str(entry.get("snapshot") or "")) if entry.get("snapshot") else None,
    }


def export_unified_game_rsi_snapshot(state, latest_eval=None):
    """Export standardized cross-project Game RSI v4.1 schema summary without leaking personal local paths."""
    ev_summary = dict(state.get("editor_evidence") or {})
    if os.path.exists(EDITOR_EVIDENCE_JSON):
        try:
            with open(EDITOR_EVIDENCE_JSON, "r", encoding="utf-8") as f:
                ev_raw = json.load(f)
            if isinstance(ev_raw, dict):
                ev_summary = {
                    "hooked_sites_count": len(ev_raw.get("hooked_sites_ever", [])),
                    "hooked_sites": list(ev_raw.get("hooked_sites_ever", [])),
                    "verified_keys_count": len(ev_raw.get("verified_keys", [])),
                    "ineffective_keys": list(ev_raw.get("ineffective_keys", [])),
                    "sessions": len(ev_raw.get("sessions", [])),
                    "hit_totals": ev_raw.get("hit_totals", {}),
                }
        except Exception:
            pass

    history_tail = [_sanitize_history_entry(h) for h in (state.get("history") or [])[-5:]]
    if latest_eval and (not history_tail or history_tail[-1].get("generation") != latest_eval.get("generation")):
        history_tail = (history_tail + [_sanitize_history_entry(latest_eval)])[-5:]

    payload = {
        "schema_version": "1.0.0",
        "domain": "game",
        "project_id": "crimson-desert",
        "engine_version": "4.1",
        "updated_at": datetime.now().isoformat(timespec="seconds"),
        "closed_loop_stages": [
            "observe_telemetry",
            "cross_analyze_game_vs_editor",
            "ucb_bandit_decide",
            "guardrail_validate",
            "atomic_apply_or_rollback",
        ],
        "current_generation": state.get("current_generation"),
        "active_arm": state.get("active_arm"),
        "active_parameters": state.get("active_parameters"),
        "arm_stats": state.get("arm_stats", {}),
        "recent_generations": history_tail,
        "editor_verification": ev_summary,
        "guardrails": {
            "monotonic_scale_rank": True,
            "min_major_frames": 250,
            "named_mutex": "Local\\CrimsonDesert_RsiEngine_v4_Mutex",
            "xml_3tag_contract": True,
        },
    }

    tmp_path = f"{UNIFIED_SUMMARY_JSON}.tmp.{os.getpid()}"
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    os.replace(tmp_path, UNIFIED_SUMMARY_JSON)
    return UNIFIED_SUMMARY_JSON


def _apply_pending_if_idle_unlocked(state):
    if state.get("pending_apply_on_exit") and not is_game_running():
        params = state.get("active_parameters", build_arm_params(state.get("active_arm", DEFAULT_ARM_ID)))
        update_xml_preset(MASTER_PROFILE, params)
        if os.path.exists(LIVE_CONFIG):
            update_xml_preset(LIVE_CONFIG, params)
        state["pending_apply_on_exit"] = False
        save_state(state)
        print(f"[RSI] 게임 종료 후 대기 중이던 XML 최적화 설정을 실게임 설정에 안전 적용했습니다 ({params.get('arm_id')}).")
        return True
    return False


def apply_pending_if_idle(state=None):
    with _RsiMutex():
        st = state if state is not None else load_state()
        return _apply_pending_if_idle_unlocked(st)


def run_rsi_cycle(preserve_live_xml=False):
    with _RsiMutex():
        return _run_rsi_cycle_unlocked(preserve_live_xml=preserve_live_xml)


def _run_rsi_cycle_unlocked(preserve_live_xml=False):
    state = load_state()
    if not preserve_live_xml:
        _apply_pending_if_idle_unlocked(state)

    metrics, marked_names = analyze_unprocessed_sessions(state, limit_frames=1)
    if not metrics:
        if marked_names:
            state.setdefault("processed_csvs", []).extend(marked_names)
            save_state(state)
        params = state.get("active_parameters", build_arm_params(DEFAULT_ARM_ID))
        print(f"[RSI v4] 신규 실측 세션이 없어 현재 최적 상태(Gen #{state.get('current_generation', 1)}, {state.get('active_arm')})를 유지합니다.")
        print(f"  • 활성 파라미터: FSR={params.get('scale_display')}, Texture={params.get('texture')}, Model={params.get('model')}, Water={params.get('water')}")
        return {"status": "NO_NEW_SESSION"}

    cross_diag = gather_cross_diagnosis(metrics)
    metrics["last_xml_changed_recently"] = bool(state.get("last_xml_changed", False))

    gen = int(state.get("current_generation", 1)) + 1
    decision = evolve_parameters(state, metrics)
    if preserve_live_xml:
        cur_arm = state.get("active_arm", DEFAULT_ARM_ID)
        new_params = state.get("active_parameters", build_arm_params(cur_arm))
        decision["next_arm"] = cur_arm
        decision["new_params"] = new_params
        decision["switched"] = False
        decision["decision_mode"] = "PRESERVE_LIVE_XML"
        decision["actions"] = [
            (
                f"[자가테스트 XML 보존] '{cur_arm}' 유지 (HUD 전수 실험 오버레이 부하 세션 — 사용자 고화질 XML 변경 생략)"
                if isinstance(a, str) and a.startswith("[자율 진화 전환]")
                else a
            )
            for a in decision.get("actions", [])
        ]
    else:
        new_params = decision["new_params"]

    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    snapshot_name = f"Gen_{gen - 1:04d}_{ts}.xml"
    snapshot_path = os.path.join(HISTORY_DIR, snapshot_name)
    if os.path.exists(MASTER_PROFILE):
        shutil.copyfile(MASTER_PROFILE, snapshot_path)

    game_running = is_game_running()
    if preserve_live_xml:
        state["pending_apply_on_exit"] = False
    else:
        update_xml_preset(MASTER_PROFILE, new_params)
        if game_running:
            state["pending_apply_on_exit"] = decision["switched"] or state.get("pending_apply_on_exit", False)
        elif os.path.exists(LIVE_CONFIG):
            update_xml_preset(LIVE_CONFIG, new_params)
            state["pending_apply_on_exit"] = False

    gen_record = {
        "generation": gen,
        "timestamp": datetime.now().isoformat(timespec="seconds"),
        "processed_files": marked_names,
        "arm_before": decision["prev_arm"],
        "arm_after": decision["next_arm"],
        "decision_mode": decision["decision_mode"],
        "reward_score": decision["reward"],
        "bottleneck": decision["bottleneck"],
        "metrics": metrics,
        "parameters": new_params,
        "actions": decision["actions"],
        "summary": decision["summary"],
        "snapshot": snapshot_name,
    }

    state["current_generation"] = gen
    state["prev_arm"] = decision["prev_arm"]
    state["active_arm"] = decision["next_arm"]
    state["active_parameters"] = new_params
    state["last_xml_changed"] = bool(decision["switched"])
    state["last_reward_score"] = decision["reward"]
    state["last_bottleneck"] = decision["bottleneck"]
    state["last_measured_fps"] = metrics["mean_fps"]
    state["convergence_score"] = decision["reward"]
    state["total_analyzed_sessions"] = int(state.get("total_analyzed_sessions", 0)) + max(1, int(metrics.get("analyzed_sessions", 1)))
    if metrics.get("latest_csv_mtime"):
        state["watermark_mtime"] = max(float(state.get("watermark_mtime", 0.0) or 0.0), float(metrics["latest_csv_mtime"]))
    state["editor_evidence"] = {
        "ineffective_keys": cross_diag["ineffective_keys"],
        "verified_keys_count": len(cross_diag["verified_keys"]),
        "hooked_sites_count": cross_diag["hooked_sites_count"],
        "player_ptr_valid": cross_diag["player_ptr_valid"],
        "non_vanilla_ini_keys": cross_diag["non_vanilla_ini_keys"],
    }
    state.setdefault("processed_csvs", []).extend(marked_names)
    state["history"].append(gen_record)
    save_state(state)
    export_unified_game_rsi_snapshot(state, gen_record)

    live_apply_note = (
        "자가테스트 HUD 시연 보호 모드 — 사용자 고화질 XML(FSR67_QualityHigh) 보존"
        if preserve_live_xml
        else ("즉시 실게임 XML 적용 완료" if not game_running else "게임 실행 중 — 종료 시 자동 실게임 XML 반영 예약됨")
    )
    report_content = f"""# Crimson Desert RSI v4 자율 최적화 리포트 (Generation #{gen})

- **생성 시각**: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}
- **텔레메트리 소스**: `{metrics['telemetry_source']}` (`{metrics.get('primary_file', '')}`, {metrics['total_frames']} frames)
- **판정 모드**: `{decision['decision_mode']}` | **선택 암**: `{decision['next_arm']}` (이전: `{decision['prev_arm']}`)
- **다목적 보상 점수**: **{decision['reward']} / 100.0** | **핵심 병목**: `{decision['bottleneck']}`
- **실게임 설정 반영**: {live_apply_note}

## 1. 실측 프레임·GPU·DWM 지표
| 지표 | 측정값 | 진단 |
| :--- | :--- | :--- |
| **평균 프레임 (Mean FPS)** | **{metrics['mean_fps']} FPS** ({metrics['mean_present_ms']} ms) | {'목표 도달' if metrics['mean_fps'] >= 45 else 'GPU/환경 최적화 필요'} |
| **하위 1% / P99 지연** | **{metrics['slowest_1pct_fps']} FPS** / **P99 {metrics['p99_interval_ms']} ms** (P95 {metrics['p95_interval_ms']} ms) | {'균일함' if metrics['p99_interval_ms'] <= 33.3 else '프레임 스파이크 존재'} |
| **GPU Busy / CPU Busy** | **{metrics['mean_gpu_busy_ms']} ms** / **{metrics['mean_cpu_busy_ms']} ms** (GPU {metrics['mean_gpu_util_pct']}%) | {'GPU 셰이더 병목' if metrics['mean_gpu_busy_ms'] > metrics['mean_cpu_busy_ms'] * 1.35 else '균형'} |
| **DWM 플립 & 레이턴시** | **Independent Flip {metrics['independent_flip_pct']}%** / Render-Present **{metrics['mean_render_present_latency_ms']} ms** | {'DWM 제로오버헤드' if metrics['independent_flip_pct'] >= 80 else 'Composed Flip 지연 감지'} |
| **VRAM / RAM / 온도** | **피크 {metrics['peak_vram_mb']} MB** / RAM **{metrics['peak_ram_mb']} MB** / **{metrics['max_gpu_temp_c']} C** | {'여유 충분' if metrics['peak_vram_mb'] < 6500 else '임계 주의'} |

## 2. 자율 판단 및 대조 분석 (게임 ↔ 실시간 에디터)
{chr(10).join([f'- {a}' for a in decision['actions']])}
{chr(10).join([f'- [대조분석] {n}' for n in cross_diag['notes']])}

## 3. 활성 그래픽 파라미터 (3-Tag XML 검증 통과)
- `_upscaleResolution`: `{new_params['scale_display']}` | `_textureQualityLevelSelect`: `{new_params['texture']}` | `_modelQualityLevelSelect`: `{new_params['model']}` | `_waterQualityLevelSelect`: `{new_params['water']}`
- 스냅샷: `{snapshot_name}`
"""
    try:
        with open(REPORT_MD, "w", encoding="utf-8") as rf:
            rf.write(report_content)
    except Exception:
        pass

    print(f"[RSI v4] 세대 진화 완료 (Generation #{gen}) | {decision['decision_mode']}")
    print(f"  • 실측({metrics['telemetry_source']}): {metrics['mean_fps']} FPS | P99 {metrics['p99_interval_ms']}ms | Slowest1% {metrics['slowest_1pct_fps']} FPS | IndepFlip {metrics['independent_flip_pct']}%")
    print(f"  • 보상/병목: {decision['reward']}/100 ({decision['bottleneck']})")
    print(f"  • 활성 암: {decision['prev_arm']} -> {decision['next_arm']} (FSR={new_params['scale_display']}, Tex={new_params['texture']}, Model={new_params['model']}, Water={new_params['water']})")
    return {"status": "SUCCESS", "generation": gen, "decision": decision}


def reset_to_high_quality():
    all_csvs = [os.path.basename(f) for f in sorted(glob.glob(os.path.join(SESSIONS_DIR, "*-frames.csv")))]
    params = build_arm_params(DEFAULT_ARM_ID)
    state = {
        "engine_version": 4,
        "current_generation": 1,
        "last_updated": datetime.now().isoformat(timespec="seconds"),
        "processed_csvs": all_csvs[-120:],
        "history": [{
            "generation": 1,
            "timestamp": datetime.now().isoformat(timespec="seconds"),
            "processed_files": [],
            "arm_before": DEFAULT_ARM_ID,
            "arm_after": DEFAULT_ARM_ID,
            "decision_mode": "RESET_BASELINE",
            "reward_score": 0.0,
            "bottleneck": "UNMEASURED",
            "parameters": params,
            "actions": ["기본 베이스라인 복원 완료"],
            "summary": "AA_UltraSharp 베이스라인 재설정",
            "snapshot": "Baseline_HighQuality.xml",
        }],
        "convergence_score": 0.0,
        "total_analyzed_sessions": 0,
        "active_arm": DEFAULT_ARM_ID,
        "active_parameters": params,
        "arm_stats": init_arm_stats(),
        "pending_apply_on_exit": False,
        "last_bottleneck": "UNMEASURED",
        "last_reward_score": 0.0,
        "last_measured_fps": 0.0,
    }
    save_state(state)
    update_xml_preset(MASTER_PROFILE, params)
    if os.path.exists(LIVE_CONFIG) and not is_game_running():
        update_xml_preset(LIVE_CONFIG, params)
    print("[RSI v4] 베이스라인 파라미터(AA_UltraSharp) 복원 완료.")
    return True


def lock_master_preset():
    """Locks 00-장시간무병목-고화질마스터-GTX1070최적.xml (FSR 67% Quality + High Tex + Med Model/Water) into MASTER_PROFILE, LIVE_CONFIG, and rsi_state.json."""
    with _RsiMutex():
        state = load_state()
        params = build_arm_params("FSR67_CombatLock", state.get("active_parameters"))
        params["scale_display"] = "67%"
        params["texture"] = "High"
        params["model"] = "Medium"
        params["water"] = "Medium"
        params["fov"] = "1.250000"
        params["zoom"] = "1.180000"
        update_xml_preset(MASTER_PROFILE, params)
        if os.path.exists(LIVE_CONFIG) and not is_game_running():
            update_xml_preset(LIVE_CONFIG, params)
        state["active_arm"] = "FSR67_CombatLock"
        state["active_parameters"] = params
        state["pending_apply_on_exit"] = False
        save_state(state)
        export_unified_game_rsi_snapshot(state)
        print("[RSI v4.1] 메인 마스터 프리셋(00-장시간무병목-고화질마스터-GTX1070최적.xml: FSR 67% Quality + High Tex + Med Model/Water) 고정 완료.")
        return True


def rollback_generation():
    with _RsiMutex():
        state = load_state()
        history = state.get("history", [])
        if len(history) <= 1:
            return reset_to_high_quality()

        last_gen = history.pop()
        prev_snapshot = last_gen.get("snapshot")
        if prev_snapshot:
            prev_path = os.path.join(HISTORY_DIR, prev_snapshot)
            if os.path.exists(prev_path):
                validate_xml_strict(prev_path)
                shutil.copyfile(prev_path, MASTER_PROFILE)
                if os.path.exists(LIVE_CONFIG) and not is_game_running():
                    shutil.copyfile(prev_path, LIVE_CONFIG)
                print(f"[RSI v4] Gen-{last_gen['generation']} -> Gen-{last_gen['generation'] - 1} XML 롤백 완료 ({prev_snapshot})")

        state["current_generation"] = max(1, int(state.get("current_generation", 1)) - 1)
        if history:
            state["active_parameters"] = history[-1].get("parameters", build_arm_params(DEFAULT_ARM_ID))
            state["active_arm"] = detect_arm_from_params(state["active_parameters"])

        popped_files = last_gen.get("processed_files") or []
        still_used = {f for h in history for f in (h.get("processed_files") or [])}
        removable = {f for f in popped_files if f not in still_used}
        if removable and isinstance(state.get("processed_csvs"), list):
            state["processed_csvs"] = [f for f in state["processed_csvs"] if f not in removable]

        primary_file = (last_gen.get("metrics") or {}).get("primary_file")
        check_files = [primary_file] if primary_file else popped_files
        popped_mtimes = []
        for fname in check_files:
            if not fname:
                continue
            sp = os.path.join(SESSIONS_DIR, os.path.basename(str(fname)))
            if os.path.exists(sp):
                try:
                    popped_mtimes.append(float(os.path.getmtime(sp)))
                except Exception:
                    pass
        if popped_mtimes:
            target_mt = max(popped_mtimes)
            wm = float(state.get("watermark_mtime", 0.0) or 0.0)
            if wm <= 0.0 or target_mt <= wm:
                state["watermark_mtime"] = target_mt - 1.0

        state["total_analyzed_sessions"] = max(0, int(state.get("total_analyzed_sessions", 1)) - 1)
        arm_before = last_gen.get("arm_before")
        arm_stats = state.get("arm_stats")
        if arm_before and isinstance(arm_stats, dict) and isinstance(arm_stats.get(arm_before), dict):
            arm_stats[arm_before]["pulls"] = max(0, int(arm_stats[arm_before].get("pulls", 0)) - 1)

        save_state(state)
        return True


def print_status():
    state = load_state()
    params = state.get("active_parameters", build_arm_params(DEFAULT_ARM_ID))
    print("=" * 68)
    print("CRIMSON DESERT · AUTONOMOUS RSI v4 (SAFE CONTEXTUAL BANDIT)")
    print("=" * 68)
    print(f"• 현재 진화 세대 : Generation #{state.get('current_generation', 1)}")
    print(f"• 활성 암(Arm)   : {state.get('active_arm')} (FSR={params.get('scale_display')} | Tex={params.get('texture')} | Model={params.get('model')} | Water={params.get('water')})")
    print(f"• 최근 실측/보상 : {state.get('last_measured_fps', 0)} FPS | 보상 {state.get('last_reward_score', 0)}/100 | 병목={state.get('last_bottleneck', 'N/A')}")
    print(f"• 누적 분석 세션 : {state.get('total_analyzed_sessions', 0)} 회")
    print("=" * 68)


def run_self_test():
    """Isolated verification of RSI v4 bandit transitions, hysteresis, circuit-breaker, and 3-tag XML checks."""
    # Test 1: Session #1 GPU-shader-bound metrics at AA_UltraSharp -> must select FSR67_QualityHigh
    st1 = {
        "engine_version": 4,
        "current_generation": 1,
        "active_arm": "AA_UltraSharp",
        "active_parameters": build_arm_params("AA_UltraSharp"),
        "arm_stats": init_arm_stats(),
        "last_reward_score": 0.0,
    }
    m1 = {
        "telemetry_source": "presentmon_v2",
        "mean_fps": 29.98,
        "p99_interval_ms": 48.41,
        "stutter_ratio": 0.5427,
        "mean_gpu_busy_ms": 33.12,
        "mean_cpu_busy_ms": 10.43,
        "mean_render_present_latency_ms": 38.31,
        "independent_flip_pct": 0.0,
        "peak_vram_mb": 3921.0,
        "mean_gpu_util_pct": 99.1,
        "max_gpu_temp_c": 57.0,
        "crash_detected": False,
    }
    d1 = evolve_parameters(st1, m1)
    assert d1["bottleneck"] == "GPU_SHADER_BOUND", f"Expected GPU_SHADER_BOUND, got {d1['bottleneck']}"
    assert d1["next_arm"] == "FSR67_QualityHigh", f"Expected FSR67_QualityHigh, got {d1['next_arm']}"
    assert d1["switched"] is True

    # Test 2: Equilibrium hold with FSR67_QualityHigh -> hysteresis prevents unnecessary switching
    st2 = {
        "engine_version": 4,
        "current_generation": 2,
        "active_arm": "FSR67_QualityHigh",
        "prev_arm": "AA_UltraSharp",
        "active_parameters": build_arm_params("FSR67_QualityHigh"),
        "arm_stats": st1["arm_stats"],
        "last_reward_score": d1["reward"],
    }
    m2 = {
        "telemetry_source": "presentmon_v2",
        "mean_fps": 56.5,
        "p99_interval_ms": 22.4,
        "stutter_ratio": 0.015,
        "mean_gpu_busy_ms": 16.2,
        "mean_cpu_busy_ms": 10.1,
        "mean_render_present_latency_ms": 18.0,
        "independent_flip_pct": 98.0,
        "peak_vram_mb": 4750.0,
        "mean_gpu_util_pct": 89.0,
        "max_gpu_temp_c": 61.0,
        "crash_detected": False,
    }
    d2 = evolve_parameters(st2, m2)
    assert d2["bottleneck"] == "OPTIMAL_EQUILIBRIUM", f"Expected OPTIMAL_EQUILIBRIUM, got {d2['bottleneck']}"
    assert d2["next_arm"] == "FSR67_QualityHigh" and d2["switched"] is False

    # Test 3: Severe regression after switching away from safe proven arm triggers circuit breaker rollback
    st3 = {
        "engine_version": 4,
        "current_generation": 3,
        "active_arm": "AA_BalancedWater",
        "prev_arm": "FSR67_QualityHigh",
        "active_parameters": build_arm_params("AA_BalancedWater"),
        "arm_stats": st2["arm_stats"],
        "last_reward_score": d2["reward"],
    }
    m3 = {
        "telemetry_source": "presentmon_v2",
        "mean_fps": 22.0,
        "p99_interval_ms": 58.0,
        "stutter_ratio": 0.72,
        "mean_gpu_busy_ms": 42.0,
        "mean_cpu_busy_ms": 10.0,
        "mean_render_present_latency_ms": 45.0,
        "independent_flip_pct": 95.0,
        "peak_vram_mb": 4100.0,
        "mean_gpu_util_pct": 99.0,
        "max_gpu_temp_c": 62.0,
        "crash_detected": False,
    }
    d3 = evolve_parameters(st3, m3)
    assert d3["decision_mode"] == "CIRCUIT_BREAKER_ROLLBACK", f"Expected CIRCUIT_BREAKER_ROLLBACK, got {d3}"
    assert d3["next_arm"] == "FSR67_QualityHigh", f"Expected rollback to FSR67_QualityHigh, got {d3['next_arm']}"

    # Test 4: Strict 3-tag XML validator rejects forbidden continuous percentage '63%' and passes valid '67%'
    with tempfile.TemporaryDirectory() as tmpd:
        valid_xml = os.path.join(tmpd, "valid.xml")
        shutil.copyfile(MASTER_PROFILE, valid_xml)
        update_xml_preset(valid_xml, build_arm_params("FSR67_QualityHigh"))
        assert validate_xml_strict(valid_xml) is True
        update_xml_preset(valid_xml, build_arm_params("FSR59_UltraResponsive"))
        assert validate_xml_strict(valid_xml) is True

        rejected = False
        try:
            update_xml_preset(valid_xml, {"scale_display": "63%", "model": "High", "texture": "High", "water": "High"})
        except ValueError:
            rejected = True
        assert rejected is True, "Validator failed to reject forbidden 63% scale"

    # Test 5: GPU_SHADER_BOUND monotonicity guard on FSR67_QualityHigh must pick FSR67_CombatLock, never AA_*
    st5 = {
        "engine_version": 4,
        "current_generation": 2,
        "active_arm": "FSR67_QualityHigh",
        "prev_arm": "AA_UltraSharp",
        "active_parameters": build_arm_params("FSR67_QualityHigh"),
        "arm_stats": st1["arm_stats"],
        "last_reward_score": 18.09,
    }
    m5 = {
        "telemetry_source": "presentmon_v2",
        "mean_fps": 32.35,
        "p99_interval_ms": 41.32,
        "stutter_ratio": 0.2223,
        "mean_gpu_busy_ms": 30.78,
        "mean_cpu_busy_ms": 12.32,
        "mean_render_present_latency_ms": 33.47,
        "independent_flip_pct": 0.0,
        "peak_vram_mb": 3995.0,
        "mean_gpu_util_pct": 99.9,
        "max_gpu_temp_c": 58.0,
        "crash_detected": False,
    }
    d5 = evolve_parameters(st5, m5)
    assert d5["bottleneck"] == "GPU_SHADER_BOUND", f"Expected GPU_SHADER_BOUND, got {d5['bottleneck']}"
    assert d5["next_arm"] == "FSR67_CombatLock", f"Expected FSR67_CombatLock, got {d5['next_arm']}"
    for pxml in sorted(glob.glob(os.path.join(PROFILES_DIR, "*.xml"))):
        assert validate_xml_strict(pxml) is True, f"Profile XML failed strict validation: {pxml}"
    run_self_test_tail_and_parallel()

    print("[RSI-SELF-TEST PASS]")
    return 0


def _run_mutex_worker(json_path):
    with _RsiMutex("Local\\CrimsonDesert_RsiEngine_SelfTest_Mutex", timeout_ms=20000):
        cur = 0
        if os.path.exists(json_path):
            with open(json_path, "r", encoding="utf-8") as f:
                cur = int(json.load(f).get("count", 0))
        time.sleep(0.03)
        tmp = f"{json_path}.tmp.{os.getpid()}"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"count": cur + 1}, f)
        os.replace(tmp, json_path)


def _write_synthetic_presentmon_csv(path, n_rows, interval_ms):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["MsBetweenPresents", "MsGPUBusy", "MsCPUBusy", "MsRenderPresentLatency", "MsBetweenDisplayChange", "PresentMode"])
        for _ in range(n_rows):
            w.writerow([f"{interval_ms:.2f}", f"{interval_ms * 0.92:.2f}", "12.00", f"{interval_ms + 2.0:.2f}", f"{interval_ms:.2f}", "Composed: Flip"])


def run_self_test_tail_and_parallel():
    # Test 6: Tail-bias guard — 47-frame exit tail must NOT override 500-frame full session CSV
    with tempfile.TemporaryDirectory() as tmpd6:
        tmp_sess = os.path.join(tmpd6, "sessions")
        tmp_rsi = os.path.join(tmpd6, "rsi")
        os.makedirs(tmp_sess, exist_ok=True)
        os.makedirs(tmp_rsi, exist_ok=True)
        major_csv = os.path.join(tmp_sess, "20261114-100000-major-frames.csv")
        tail_csv = os.path.join(tmp_sess, "20261114-100200-tail-rsi-frames.csv")
        _write_synthetic_presentmon_csv(major_csv, 500, 30.0)
        _write_synthetic_presentmon_csv(tail_csv, 47, 18.0)
        base_t = time.time() - 30.0
        os.utime(major_csv, (base_t, base_t))
        os.utime(tail_csv, (base_t + 10.0, base_t + 10.0))

        m6, marked6 = analyze_unprocessed_sessions({"processed_csvs": []}, sessions_dir=tmp_sess, rsi_dir=tmp_rsi, limit_frames=1)
        assert m6 is not None and m6["total_frames"] == 500, f"Expected 500 frames, got {m6}"
        assert round(m6["mean_fps"], 1) == 33.3, f"Expected ~33.3 FPS, got {m6['mean_fps']}"
        assert "20261114-100000-major-frames.csv" in marked6 and "20261114-100200-tail-rsi-frames.csv" in marked6

    # Test 7: Real parallel concurrency & atomicity proof with 4 concurrent worker processes
    with tempfile.TemporaryDirectory() as tmpd7:
        cnt_path = os.path.join(tmpd7, "counter.json")
        with open(cnt_path, "w", encoding="utf-8") as f:
            json.dump({"count": 0}, f)
        procs = [
            subprocess.Popen(
                [sys.executable, os.path.abspath(__file__), "--self-test-mutex-worker", cnt_path],
                creationflags=0x08000000,
            )
            for _ in range(4)
        ]
        rcs = [p.wait(timeout=20) for p in procs]
        assert all(rc == 0 for rc in rcs), f"Mutex worker exit codes={rcs}"
        with open(cnt_path, "r", encoding="utf-8") as f:
            final_count = int(json.load(f).get("count", -1))
        assert final_count == 4, f"Expected final_count=4 under real parallelism, got {final_count}"

    # Test 8: Monotonic watermark_mtime guard — historical CSVs at or before watermark_mtime never trigger generations
    with tempfile.TemporaryDirectory() as tmpd8:
        tmp_sess8 = os.path.join(tmpd8, "sessions")
        tmp_rsi8 = os.path.join(tmpd8, "rsi")
        os.makedirs(tmp_sess8, exist_ok=True)
        os.makedirs(tmp_rsi8, exist_ok=True)
        old_csv = os.path.join(tmp_sess8, "20261107-120000-old-frames.csv")
        _write_synthetic_presentmon_csv(old_csv, 400, 32.0)
        t_mark = time.time() - 100.0
        os.utime(old_csv, (t_mark - 50.0, t_mark - 50.0))
        st8 = {"processed_csvs": [], "watermark_mtime": t_mark}
        m8, marked8 = analyze_unprocessed_sessions(st8, sessions_dir=tmp_sess8, rsi_dir=tmp_rsi8, limit_frames=1)
        assert m8 is None, f"Expected None when only historical CSVs exist, got {m8}"
        assert "20261107-120000-old-frames.csv" in marked8, f"Expected old CSV marked skipped, got {marked8}"


if __name__ == "__main__":
    cmd = sys.argv[1].lower() if len(sys.argv) > 1 else ""
    if cmd in ("--evolve", "-e"):
        run_rsi_cycle(preserve_live_xml=("--preserve-live-xml" in [a.lower() for a in sys.argv[2:]]))
    elif cmd in ("--apply-pending",):
        apply_pending_if_idle(load_state())
    elif cmd == "--export-unified":
        out = export_unified_game_rsi_snapshot(load_state())
        print(f"[RSI v4.1] Exported unified Game RSI schema -> {out}")
    elif cmd in ("--rollback", "-r"):
        rollback_generation()
    elif cmd in ("--reset-hq", "--reset"):
        reset_to_high_quality()
    elif cmd in ("--lock-master",):
        lock_master_preset()
    elif cmd == "--self-test":
        sys.exit(run_self_test())
    elif cmd == "--self-test-mutex-worker" and len(sys.argv) >= 3:
        _run_mutex_worker(sys.argv[2])
    else:
        print_status()
