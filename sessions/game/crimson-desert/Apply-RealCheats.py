# -*- coding: utf-8 -*-
"""
Crimson Desert — Safe SWISS Knife In-Place Save Editor Engine
=============================================================
Applies 100% fixed-size in-place binary patches across active save slots
(slot0, slot1, slot2, ...) with ChaCha20 + HMAC-SHA256 + LZ4 block encryption.
Strictly verifies (end_offset - start_offset) == expected_byte_width on every
PARC field so 0-byte default fields and adjacent PARC headers are never touched.
Never overwrites empty (_itemKey == 0) inventory slots.
"""
import os
import sys
import shutil
import struct
import argparse

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
COMMUNITY_SRC = os.path.join(SCRIPT_DIR, "research", "community-source", "CrimsonMods")
SAVE_BASE_DIR = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Pearl Abyss", "CD", "save")
SILVER_CACHE_PATH = os.path.join(SCRIPT_DIR, "editor", "silver_cache.txt")

sys.path.insert(0, COMMUNITY_SRC)
import save_crypto
import save_parser

SKIP_DIR_TOKENS = ("백업", "backup", "테스트", "torture", "tmp", "scratch")
MAX_SILVER_CENTI = 99999999999


def has_exact_bytes(field_obj, expected_len):
    return (
        field_obj is not None
        and getattr(field_obj, "start_offset", 0) > 0
        and (getattr(field_obj, "end_offset", 0) - getattr(field_obj, "start_offset", 0)) == expected_len
    )


def find_all_save_files(target_file=None):
    if target_file:
        abs_target = os.path.abspath(target_file)
        return [abs_target] if os.path.isfile(abs_target) else []
    if not os.path.isdir(SAVE_BASE_DIR):
        return []
    found = []
    for root, dirs, files in os.walk(SAVE_BASE_DIR):
        rel = os.path.relpath(root, SAVE_BASE_DIR).lower()
        if any(tok in rel for tok in SKIP_DIR_TOKENS):
            dirs[:] = []
            continue
        dirs[:] = [d for d in dirs if not any(tok in d.lower() for tok in SKIP_DIR_TOKENS)]
        for f in files:
            if f.lower() == "save.save":
                found.append(os.path.join(root, f))
    found.sort(key=lambda p: os.path.getmtime(p), reverse=True)
    return found


def ensure_original_backup(save_path):
    bak = save_path + ".original.bak"
    if not os.path.isfile(bak):
        shutil.copy2(save_path, bak)
    return bak


def write_silver_cache(silver_centi):
    if silver_centi is None or silver_centi < 0:
        return
    try:
        os.makedirs(os.path.dirname(SILVER_CACHE_PATH), exist_ok=True)
        tmp = SILVER_CACHE_PATH + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(str(int(silver_centi)))
        os.replace(tmp, SILVER_CACHE_PATH)
    except Exception:
        pass


def patch_save_slot(save_path, args, is_latest_main=False):
    slot_name = os.path.basename(os.path.dirname(save_path))
    if any(tok in slot_name.lower() for tok in SKIP_DIR_TOKENS) and not args.restore and not args.file:
        return True

    if not args.query:
        ensure_original_backup(save_path)

    sd = save_crypto.load_save_file(save_path)
    raw = bytearray(sd.decompressed_blob)
    orig_len = len(raw)

    schema = save_parser.parse_schema(bytes(raw))
    type_names = [t.name for t in schema["types"]]
    toc = save_parser.parse_toc(bytes(raw), schema["schema_end"], type_names)

    def _patch_level_and_daily(raw_buf, fields, set_level_10, set_exp_max, reset_daily):
        rom_cnt = 0
        daily_cnt = 0
        for cf in fields or ():
            if cf.name == "_levelData" and cf.child_fields:
                r2, d2 = _patch_level_and_daily(
                    raw_buf, cf.child_fields, set_level_10, set_exp_max, reset_daily
                )
                rom_cnt += r2
                daily_cnt += d2
            elif cf.name == "_level" and set_level_10 and has_exact_bytes(cf, 4):
                struct.pack_into("<I", raw_buf, cf.start_offset, 10)
                rom_cnt += 1
            elif cf.name == "_exp" and set_exp_max and has_exact_bytes(cf, 8):
                struct.pack_into("<Q", raw_buf, cf.start_offset, 9999)
                rom_cnt += 1
            elif reset_daily and cf.name in (
                "_gimmickEventDailyCountData",
                "_actionFrameEventDailyCountData",
                "_talkEventDailyCountData",
            ) and cf.child_fields:
                for df in cf.child_fields:
                    if df.name == "_dailyCount" and has_exact_bytes(df, 4):
                        if struct.unpack_from("<I", raw_buf, df.start_offset)[0] != 0:
                            struct.pack_into("<I", raw_buf, df.start_offset, 0)
                            daily_cnt += 1
                    elif df.name == "_lastUpdateTime" and has_exact_bytes(df, 8):
                        if struct.unpack_from("<Q", raw_buf, df.start_offset)[0] != 0:
                            struct.pack_into("<Q", raw_buf, df.start_offset, 0)
                            daily_cnt += 1
        return rom_cnt, daily_cnt

    wanted = {"CharacterStatusSaveData", "InventorySaveData"}
    if args.gear_max or args.all_max:
        wanted.add("EquipmentSaveData")
    if args.romance_max or args.all_max or getattr(args, "reset_friendly_daily", False):
        wanted.add("FieldNPCSaveData")
        wanted.add("FriendlySaveData")

    entries = [e for e in toc["entries"] if e.class_name in wanted]
    objects = save_parser.decode_object_blocks(bytes(raw), entries, schema["types"])

    changes = []
    latest_bag_silver = None

    for obj in objects:
        # 1. CharacterStatusSaveData (HP, MP, Level - read-only)
        if obj.class_name == "CharacterStatusSaveData":
            if args.query:
                lvl_f = hp_f = mp_f = None
                for f in obj.fields:
                    if f.name == "_level" and has_exact_bytes(f, 4):
                        lvl_f = f
                    elif f.name == "_currentHp" and has_exact_bytes(f, 8):
                        hp_f = f
                    elif f.name == "_currentMp" and has_exact_bytes(f, 8):
                        mp_f = f
                cur_lvl = struct.unpack_from("<I", raw, lvl_f.start_offset)[0] if lvl_f else 1
                cur_hp = struct.unpack_from("<Q", raw, hp_f.start_offset)[0] if hp_f else 0
                cur_mp = struct.unpack_from("<Q", raw, mp_f.start_offset)[0] if mp_f else 0
                print(f"[{slot_name}] Level={cur_lvl}, HP={cur_hp:,}, MP={cur_mp:,}", end="")

        # 2. EquipmentSaveData (Equipped gear endurance ONLY)
        elif obj.class_name == "EquipmentSaveData" and (args.gear_max or args.all_max):
            eq_cnt = 0
            for f in obj.fields:
                if f.name == "_list" and f.list_elements:
                    for el in f.list_elements:
                        for cf in (el.child_fields or []):
                            if cf.name == "_item" and cf.child_fields:
                                for scf in cf.child_fields:
                                    if scf.name == "_endurance" and has_exact_bytes(scf, 2):
                                        cur_end = struct.unpack_from("<H", raw, scf.start_offset)[0]
                                        if 0 < cur_end < 32767:
                                            struct.pack_into("<H", raw, scf.start_offset, 32767)
                                            eq_cnt += 1
            if eq_cnt > 0:
                changes.append(f"장착장비 {eq_cnt}개 내구도 MAX(32,767)")

        # 3. InventorySaveData (Player Bag Silver invKey==2, Arrow Overflow Fix, Bag Expansion, Stacks)
        elif obj.class_name == "InventorySaveData":
            for f in obj.fields:
                if f.name in ("_inventorylist", "_inventoryList") and f.list_elements:
                    for el in f.list_elements:
                        inv_key = 0
                        expand_f = None
                        item_list_f = None
                        for cf in (el.child_fields or []):
                            if cf.name == "_inventoryKey" and has_exact_bytes(cf, 2):
                                inv_key = struct.unpack_from("<H", raw, cf.start_offset)[0]
                            elif cf.name == "_varyExpandSlotCount" and has_exact_bytes(cf, 2):
                                expand_f = cf
                            elif cf.name in ("_itemList", "_itemSaveList") and cf.list_elements:
                                item_list_f = cf

                        if inv_key == 2 and expand_f and (args.bag_expand > 0 or args.all_max):
                            target_bag = 252
                            struct.pack_into("<H", raw, expand_f.start_offset, target_bag)
                            changes.append(f"가방 확장 슬롯 -> +{target_bag}칸 (총 700칸)")
                        elif inv_key == 2 and expand_f and args.query:
                            cur_bag = struct.unpack_from("<H", raw, expand_f.start_offset)[0]
                            print(f", BagExpand=+{cur_bag}", end="")

                        if item_list_f:
                            stack_boosted = 0
                            for it_el in item_list_f.list_elements:
                                item_key = 0
                                stack_f = None
                                endur_f = None
                                for it_f in (it_el.child_fields or []):
                                    if it_f.name == "_itemKey" and has_exact_bytes(it_f, 4):
                                        item_key = struct.unpack_from("<I", raw, it_f.start_offset)[0]
                                    elif it_f.name == "_stackCount" and has_exact_bytes(it_f, 8):
                                        stack_f = it_f
                                    elif it_f.name == "_endurance" and has_exact_bytes(it_f, 2):
                                        endur_f = it_f

                                # Silver strictly lives in Player Bag (invKey == 2) with _itemKey == 1
                                if inv_key == 2 and item_key == 1 and stack_f:
                                    cur_gold = struct.unpack_from("<Q", raw, stack_f.start_offset)[0]
                                    latest_bag_silver = cur_gold
                                    if args.query:
                                        print(f", Silver={cur_gold / 100.0:.2f}G ({cur_gold})", end="")
                                    if args.gold_set is not None:
                                        new_gold = max(0, min(MAX_SILVER_CENTI, args.gold_set))
                                        struct.pack_into("<Q", raw, stack_f.start_offset, new_gold)
                                        latest_bag_silver = new_gold
                                        changes.append(f"은화(Silver): {cur_gold / 100.0:,.2f}G -> {new_gold / 100.0:,.2f}G")
                                    elif args.gold_add != 0:
                                        new_gold = max(0, min(MAX_SILVER_CENTI, cur_gold + args.gold_add))
                                        struct.pack_into("<Q", raw, stack_f.start_offset, new_gold)
                                        latest_bag_silver = new_gold
                                        changes.append(f"은화(Silver): {cur_gold / 100.0:,.2f}G -> {new_gold / 100.0:,.2f}G (+{args.gold_add / 100.0:,.2f}G)")
                                    elif args.all_max and cur_gold < 999999900:
                                        new_gold = 999999900
                                        struct.pack_into("<Q", raw, stack_f.start_offset, new_gold)
                                        latest_bag_silver = new_gold
                                        changes.append(f"은화(Silver): {cur_gold / 100.0:,.2f}G -> {new_gold / 100.0:,.2f}G")

                                # Regular Arrows (_itemKey == 50001) overflow remediation
                                elif item_key == 50001 and stack_f:
                                    cur_arrows = struct.unpack_from("<Q", raw, stack_f.start_offset)[0]
                                    if args.query and cur_arrows > 9999:
                                        print(f", ArrowsOverflow(invKey={inv_key})={cur_arrows:,}", end="")
                                    if args.fix_arrows and cur_arrows > 9999:
                                        new_arrows = 501 if inv_key == 2 else 1
                                        struct.pack_into("<Q", raw, stack_f.start_offset, new_arrows)
                                        changes.append(f"화살(invKey={inv_key}) 복구: {cur_arrows:,} -> {new_arrows:,}")

                                elif item_key > 1 and item_key != 50001 and stack_f and (args.gear_max or args.all_max):
                                    cur_stack = struct.unpack_from("<Q", raw, stack_f.start_offset)[0]
                                    if 2 <= cur_stack < 999:
                                        struct.pack_into("<Q", raw, stack_f.start_offset, 999)
                                        stack_boosted += 1

                                if item_key > 1 and endur_f and (args.gear_max or args.all_max):
                                    cur_end = struct.unpack_from("<H", raw, endur_f.start_offset)[0]
                                    if 0 < cur_end < 32767:
                                        struct.pack_into("<H", raw, endur_f.start_offset, 32767)

                            if stack_boosted > 0:
                                changes.append(f"소비/재료 아이템 {stack_boosted}종 수량 -> 999개")

        # 4. FriendlySaveData & FieldNPCSaveData (NPC Friendly, Romance Affinity & Daily Caps)
        elif obj.class_name == "FriendlySaveData" and (args.romance_max or args.all_max or getattr(args, "reset_friendly_daily", False)):
            want_romance = bool(args.romance_max or args.all_max)
            want_daily = bool(want_romance or getattr(args, "reset_friendly_daily", False))
            rom_cnt = 0
            daily_cnt = 0
            for f in obj.fields:
                if f.name in ("_friendlyDataList", "_affinityDataList") and f.list_elements:
                    is_affinity = (f.name == "_affinityDataList")
                    for elem in f.list_elements:
                        if elem.child_fields:
                            r2, d2 = _patch_level_and_daily(
                                raw, elem.child_fields, is_affinity and want_romance, want_romance, want_daily
                            )
                            rom_cnt += r2
                            daily_cnt += d2
            if rom_cnt > 0:
                changes.append(f"NPC 호감도·연애 EXP 9,999 ({rom_cnt}필드)")
            if daily_cnt > 0:
                changes.append(f"NPC 일일 호감 제한 초기화 ({daily_cnt}필드)")
        elif obj.class_name == "FieldNPCSaveData" and (args.romance_max or args.all_max or getattr(args, "reset_friendly_daily", False)):
            want_romance = bool(args.romance_max or args.all_max)
            want_daily = bool(want_romance or getattr(args, "reset_friendly_daily", False))
            rom_cnt = 0
            daily_cnt = 0
            for f in obj.fields:
                if f.name in ("_friendly", "_affinity") and f.child_fields:
                    is_affinity = (f.name == "_affinity")
                    r2, d2 = _patch_level_and_daily(
                        raw, f.child_fields, is_affinity and want_romance, want_romance, want_daily
                    )
                    rom_cnt += r2
                    daily_cnt += d2
            if rom_cnt > 0:
                changes.append("NPC 호감도/연애 Lv.10 & EXP 9,999")
            if daily_cnt > 0:
                changes.append(f"NPC 필드 일일 제한 초기화 ({daily_cnt}필드)")

    if is_latest_main and latest_bag_silver is not None:
        write_silver_cache(latest_bag_silver)

    if args.query:
        print()
        return True

    if not changes:
        print(f"  [{slot_name}] 변경할 대상 필드가 없거나 이미 최신 상태입니다.")
        return True

    assert len(raw) == orig_len, "Safety violation: uncompressed blob size must not change!"
    save_crypto.write_save_file(save_path, bytes(raw), sd.raw_header)
    print(f"  [완료] [{slot_name}] SWISS Knife 세이브 패치 ({len(raw):,}B 무결성 100% 유지): {', '.join(changes)}")
    return True


def main():
    parser = argparse.ArgumentParser(description="Crimson Desert Safe SWISS Knife Save Patcher")
    parser.add_argument("--file", default=None, help="Operate strictly on a single save.save path")
    parser.add_argument("--latest", action="store_true", help="Operate only on the newest active save slot")
    parser.add_argument("--gold-add", type=int, default=0)
    parser.add_argument("--gold-set", type=int, default=None)
    parser.add_argument("--fix-arrows", action="store_true", help="Reset overflowed _itemKey==50001 arrow stacks (>9999)")
    parser.add_argument("--level-set", type=int, default=None)
    parser.add_argument("--status-boost", action="store_true")
    parser.add_argument("--gear-max", action="store_true")
    parser.add_argument("--equip-thief-gloves", action="store_true")
    parser.add_argument("--bag-expand", type=int, default=0)
    parser.add_argument("--romance-max", action="store_true")
    parser.add_argument("--reset-friendly-daily", action="store_true")
    parser.add_argument("--all-max", action="store_true")
    parser.add_argument("--restore", action="store_true")
    parser.add_argument("--query", action="store_true")
    args = parser.parse_args()

    saves = find_all_save_files(args.file)
    if not saves:
        print("[오류] 세이브 파일(slot0~slot2)을 찾을 수 없습니다.")
        sys.exit(1)

    if args.restore:
        for sp in saves:
            bak = sp + ".original.bak"
            slot_name = os.path.basename(os.path.dirname(sp))
            if os.path.isfile(bak):
                shutil.copy2(bak, sp)
                print(f"  [복원] [{slot_name}] 원본 세이브(.original.bak) 복원 완료")
        sys.exit(0)

    if args.latest:
        saves = saves[:1]

    # Identify newest real slot (non --file) so silver_cache.txt always mirrors active save
    latest_main = None
    if not args.file:
        latest_main = os.path.normcase(os.path.abspath(saves[0]))

    for sp in saves:
        is_latest = args.file is None and os.path.normcase(os.path.abspath(sp)) == latest_main
        patch_save_slot(sp, args, is_latest_main=is_latest)


if __name__ == "__main__":
    main()
