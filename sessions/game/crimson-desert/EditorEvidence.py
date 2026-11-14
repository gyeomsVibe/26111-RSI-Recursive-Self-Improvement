# -*- coding: utf-8 -*-
"""
EditorEvidence v2: Read-only evaluator merging CdRecorder (18-hook + 0x4000 cave) logs
into the closed-loop RSI system.
Evaluator-only: NEVER writes to game memory, game XMLs, or editor binaries.
"""
import os
import sys
import json
import subprocess
import tempfile
from datetime import datetime

if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

BASE = os.path.dirname(os.path.abspath(__file__))
ED = os.path.join(BASE, "editor")
EV = os.path.join(ED, "evidence")
OUT = os.path.join(BASE, "research", "rsi_telemetry", "editor_evidence.json")
REACT_WINDOW = 10.0

# Map each INI key in the 25-row v2 schema to expected hook sites and CAVE fields.
KEY_SPECS = {
    "DefensePreset":               {"sites": ["PLAYER", "DEFENSE", "STATS", "COMMIT"], "cave_fields": ["def", "god"]},
    "DamagePreset":                {"sites": ["PLAYER", "DMG"],                        "cave_fields": ["dmg"]},
    "PlayerStaminaMode":           {"sites": ["PLAYER", "STATS", "COMMIT"],            "cave_fields": ["sta"]},
    "SkillCooldownMode":           {"sites": ["PLAYER", "STATS", "COMMIT"],            "cave_fields": ["cd"]},
    "ExpMultiplier":               {"sites": ["EXP"],                                  "cave_fields": ["exp"]},
    "MovementSpeed":               {"sites": ["KISS"],                                 "cave_fields": ["kissMult"]},
    "JumpMultiplier":              {"sites": ["KISS"],                                 "cave_fields": ["jumpSc"]},
    "RomanceAffectionMultiplier":  {"sites": ["ROMANCE_TRUST"],                        "cave_fields": ["rom"]},
    "TamingMultiplier":            {"sites": ["HORSE_TAMING"],                         "cave_fields": ["tame"]},
    "MountSpeedMultiplier":        {"sites": ["PLAYER", "STATS", "KISS"],              "cave_fields": ["mspd", "kissMult"]},
    "MountStaminaMode":            {"sites": ["PLAYER", "STATS", "COMMIT"],            "cave_fields": ["msta"]},
    "UnderwaterOxygenMultiplier":  {"sites": ["BREATH_VITALS"],                        "cave_fields": ["breath"]},
    "UnderwaterSpeed":             {"sites": ["KISS", "BREATH_VITALS"],                "cave_fields": ["swim"]},
    "CraftCostMode":               {"sites": ["SAVE_ITEM", "COUNT_ITEMS"],             "cave_fields": ["craftCost"]},
    "CraftSuccessMultiplier":      {"sites": [],                                       "cave_fields": ["craftSucc"]},
    "DurabilityMode":              {"sites": ["DURABILITY"],                           "cave_fields": ["dur"]},
    "GearHiderMode":               {"sites": [],                                       "cave_fields": ["gear"]},
    "WeaponImbueMode":             {"sites": ["PLAYER", "DMG", "STATS"],               "cave_fields": ["imbue"]},
    "PendingAddSilver":            {"sites": ["SAVE_ITEM", "SELECTED_ITEM"],           "cave_fields": []},
    "ThiefDetectionMode":          {"sites": ["CRIME_EVAL"],                           "cave_fields": ["thiefDet"]},
    "PickpocketSuccessMode":       {"sites": ["CRIME_EVAL"],                           "cave_fields": ["pick"]},
    "BountyImmunityMode":          {"sites": ["CRIME_EVAL", "WANTED_EVAL"],            "cave_fields": ["bounty"]},
    "ThiefGloveFenceMode":         {"sites": ["CRIME_EVAL", "WANTED_EVAL"],            "cave_fields": ["glove"]},
    "BagExpandSlots":              {"sites": ["SLOT_EXPAND"],                          "cave_fields": ["bag"]},
    "SpecialConvenienceMode":      {"sites": ["KISS"],                                 "cave_fields": ["spec"]},
    "FreezeDaytime":               {"sites": ["DAYTIME"],                              "cave_fields": ["freeze"]},
}


def ensure_recorder(session_id=""):
    exe = os.path.join(ED, "CdRecorder.exe")
    new = os.path.join(ED, "CdRecorder.new.exe")
    try:
        if os.path.exists(new):
            os.replace(new, exe)
    except Exception:
        if os.path.exists(new):
            exe = new
    if not os.path.exists(exe):
        return False
    args = [exe, "--exit-with-game"] + (["--session", session_id] if session_id else [])
    try:
        subprocess.Popen(args, cwd=ED, creationflags=0x08000000, close_fds=True)
        return True
    except Exception:
        return False


def _ts(s):
    h, m, r = s.split(":")
    return int(h) * 3600 + int(m) * 60 + float(r)


def _parse_kv(msg):
    out = {}
    for part in msg.split():
        if "=" in part:
            k, v = part.split("=", 1)
            out[k] = v
    return out


def summarize_directory(ev_dir=EV, out_path=OUT, latest_only=False):
    sessions = []
    sites_seen = set()
    keys = {}
    cave_health = {
        "cave_discovered": False,
        "player_ptr_valid": False,
        "sta_ptr_valid": False,
        "spi_ptr_valid": False,
        "silver_seen": False,
    }
    hit_totals = {}

    files = sorted(fn for fn in os.listdir(ev_dir) if fn.endswith(".log")) if os.path.isdir(ev_dir) else []
    if latest_only and files:
        files = [files[-1]]

    for fn in files:
        fpath = os.path.join(ev_dir, fn)
        ev = []
        with open(fpath, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                p = line.rstrip("\r\n").split("\t", 2)
                if len(p) == 3:
                    try:
                        ev.append((_ts(p[0]), p[1], p[2]))
                    except Exception:
                        pass

        is_v2 = any(k == "CAVE" or k == "HITS" for _, k, _ in ev)
        # Skip legacy v1 logs once we have v2 logs (unless inspecting explicitly)
        if not is_v2 and any("recorder_v2" in m for _, k, m in ev if k == "START") is False:
            # Allow v1 counting only if zero v2 logs exist
            pass

        s_first_hits = None
        s_last_hits = None
        for _, k, m in ev:
            if k == "SITES":
                for site, state in _parse_kv(m).items():
                    if state == "HOOKED":
                        sites_seen.add(site)
            elif k == "CAVE":
                kv = _parse_kv(m)
                if kv.get("cf", "0x0") not in ("0x0", "0"):
                    cave_health["cave_discovered"] = True
                if kv.get("player", "0x0") not in ("0x0", "0"):
                    cave_health["player_ptr_valid"] = True
                if kv.get("staPtr", "0x0") not in ("0x0", "0"):
                    cave_health["sta_ptr_valid"] = True
                if kv.get("spiPtr", "0x0") not in ("0x0", "0"):
                    cave_health["spi_ptr_valid"] = True
                try:
                    if int(kv.get("silver", "-1")) >= 0:
                        cave_health["silver_seen"] = True
                except ValueError:
                    pass
            elif k == "HITS":
                kv = _parse_kv(m)
                parsed_hits = {}
                for hk, hv in kv.items():
                    try:
                        parsed_hits[hk] = int(hv)
                    except ValueError:
                        pass
                if s_first_hits is None:
                    s_first_hits = parsed_hits
                s_last_hits = parsed_hits

        if s_first_hits and s_last_hits:
            for hk, lv in s_last_hits.items():
                delta = max(0, lv - s_first_hits.get(hk, 0))
                hit_totals[hk] = max(hit_totals.get(hk, 0), lv)
                hit_totals[f"{hk}_max_delta"] = max(hit_totals.get(f"{hk}_max_delta", 0), delta)

        sessions.append({
            "file": fn,
            "v2": is_v2,
            "events": len(ev),
            "toggles": sum(1 for e in ev if e[1] == "TOGGLE"),
        })

        # Track state timeline for toggle reactions (only v2 or fallback)
        for i, (t, k, m) in enumerate(ev):
            if k != "TOGGLE":
                continue
            key = m.split(":")[0].strip()
            spec = KEY_SPECS.get(key)
            val_part = m.split("->")[-1].strip() if "->" in m else ""
            d = keys.setdefault(key, {
                "toggles": 0,
                "reacted": 0,
                "site_reacted": 0,
                "cave_reacted": 0,
                "non_default_toggles": 0,
                "world_attached_toggles": 0,
            })
            d["toggles"] += 1

            is_default = val_part in ("0", "1", "1.0", "1.00", "OFF", "")
            if not is_default:
                d["non_default_toggles"] += 1

            prev_sites = {}
            prev_cave = {}
            prev_ctrl = ""
            player_attached = False
            for _, kk, mm in ev[:i]:
                if kk == "SITES":
                    prev_sites = _parse_kv(mm)
                elif kk == "CAVE":
                    prev_cave = _parse_kv(mm)
                    if prev_cave.get("player", "0x0") not in ("0x0", "0"):
                        player_attached = True
                elif kk == "CTRL":
                    prev_ctrl = mm

            # Check if CAVE or SITES already settled on game state
            reacted_here = False
            site_hit = False
            cave_hit = False
            for tt, kk, mm in ev[i + 1:]:
                dt = tt - t
                if dt < 0:
                    dt += 86400.0
                if dt > REACT_WINDOW:
                    break
                if kk == "SITES":
                    cur_sites = _parse_kv(mm)
                    if spec and spec["sites"]:
                        if any(cur_sites.get(s) != prev_sites.get(s) or (not is_default and cur_sites.get(s) == "HOOKED") for s in spec["sites"]):
                            site_hit = True
                            reacted_here = True
                    elif cur_sites != prev_sites:
                        site_hit = True
                        reacted_here = True
                elif kk == "CAVE":
                    cur_cave = _parse_kv(mm)
                    if cur_cave.get("player", "0x0") not in ("0x0", "0"):
                        player_attached = True
                    if spec and spec["cave_fields"]:
                        if any(cur_cave.get(cfld) != prev_cave.get(cfld) for cfld in spec["cave_fields"]):
                            cave_hit = True
                            reacted_here = True
                    elif cur_cave != prev_cave:
                        cave_hit = True
                        reacted_here = True
                elif kk == "CTRL" and mm != prev_ctrl:
                    reacted_here = True

            if player_attached:
                d["world_attached_toggles"] += 1
            if site_hit:
                d["site_reacted"] += 1
            if cave_hit:
                d["cave_reacted"] += 1
            if reacted_here:
                d["reacted"] += 1

    verified_keys = []
    ineffective_keys = []
    untriggered_in_scene_keys = []
    for k, d in sorted(keys.items()):
        d["rate"] = round(d["reacted"] / max(1, d["toggles"]), 2)
        if d["reacted"] > 0:
            verified_keys.append(k)
        elif d["non_default_toggles"] > 0 and d["world_attached_toggles"] > 0:
            if KEY_SPECS.get(k, {}).get("cave_fields") or KEY_SPECS.get(k, {}).get("sites"):
                ineffective_keys.append(k)
            else:
                untriggered_in_scene_keys.append(k)
        else:
            untriggered_in_scene_keys.append(k)

    res = {
        "updated": datetime.now().isoformat(timespec="seconds"),
        "sessions": sessions[-10:],
        "total_session_logs": len(sessions),
        "hooked_sites_ever": sorted(sites_seen),
        "cave_pointer_health": cave_health,
        "hit_totals": hit_totals,
        "keys": keys,
        "verified_keys": verified_keys,
        "untriggered_in_scene_keys": untriggered_in_scene_keys,
        "ineffective_keys": ineffective_keys,
    }
    if out_path:
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
    return res


def summarize():
    # Summarize v2 logs primarily when available so old broken v1 logs don't pollute live RSI diagnosis
    if os.path.isdir(EV):
        all_logs = sorted(fn for fn in os.listdir(EV) if fn.endswith(".log"))
        v2_logs = []
        for fn in all_logs:
            try:
                with open(os.path.join(EV, fn), "r", encoding="utf-8", errors="replace") as f:
                    head = f.read(400)
                    if "recorder_v2" in head:
                        v2_logs.append(fn)
            except Exception:
                pass
        if v2_logs:
            with tempfile.TemporaryDirectory() as tmpd:
                for fn in v2_logs[-5:]:
                    src = os.path.join(EV, fn)
                    dst = os.path.join(tmpd, fn)
                    with open(src, "r", encoding="utf-8", errors="replace") as s, open(dst, "w", encoding="utf-8") as d:
                        d.write(s.read())
                return summarize_directory(ev_dir=tmpd, out_path=OUT)
    return summarize_directory(ev_dir=EV, out_path=OUT)


def run_self_test():
    with tempfile.TemporaryDirectory() as td:
        sample = os.path.join(td, "session_selftest.log")
        lines = [
            "12:00:00.000\tSTART\trecorder_v2 pid=1234 ini=test.ini",
            "12:00:01.000\tSITES\tPLAYER=VANILLA EXP=VANILLA DURABILITY=VANILLA",
            "12:00:02.000\tCAVE\tcf=0x211D0000 player=0x11223344 silver=50000 staPtr=0x2233 spiPtr=0x3344 exp=1 dur=0",
            "12:00:03.000\tTOGGLE\tExpMultiplier: 1 -> 5",
            "12:00:04.500\tSITES\tPLAYER=VANILLA EXP=HOOKED DURABILITY=VANILLA",
            "12:00:05.000\tCAVE\tcf=0x211D0000 player=0x11223344 silver=50000 staPtr=0x2233 spiPtr=0x3344 exp=5 dur=0",
            "12:00:06.000\tHITS\tcommit=10 dur=0",
            "12:00:07.000\tTOGGLE\tDurabilityMode: 0 -> 4",
            # No reaction for DurabilityMode -> should appear in ineffective_keys
            "12:00:19.000\tEND\tdone",
        ]
        with open(sample, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        res = summarize_directory(ev_dir=td, out_path=None)
        assert "EXP" in res["hooked_sites_ever"], "EXP site missing"
        assert res["cave_pointer_health"]["player_ptr_valid"] is True, "player ptr check failed"
        assert "ExpMultiplier" in res["verified_keys"], "ExpMultiplier should be verified"
        assert "DurabilityMode" in res["ineffective_keys"], "DurabilityMode should be marked ineffective"
    print("[EDITOR-EVIDENCE-SELF-TEST PASS]")
    return 0


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        sys.exit(run_self_test())
    r = summarize()
    print(json.dumps({
        "hooked_sites_ever": r["hooked_sites_ever"],
        "verified_keys": r["verified_keys"],
        "ineffective_keys": r["ineffective_keys"],
        "sessions": r["total_session_logs"],
    }, ensure_ascii=False))
