# Game Domain RSI v4.1 (게임 전용 자가개선 시스템)

게임 엔진·드라이버 프로파일·실시간 메모리 에디터의 상호작용을 단일 계약 아래에서 측정, 대조, 개선, 롤백하는 게임 전용 RSI 표준입니다.

## 표준 폐쇄 루프 (5-Stage Closed Loop)

1. `observe_telemetry` — PresentMon v2 프레임 간격·렌더 지연·GPU 점유율·크래시 덤프·에디터 훅/키 토글 로그를 동시 수집합니다.
2. `cross_analyze_game_vs_editor` — [`EditorEvidence.py`](./crimson-desert/EditorEvidence.py)가 세션 로그와 메모리 케이브/트램폴린 히트 카운터를 교차 분석해 미작동 키(`ineffective_keys`)와 포인터 상태를 산출합니다.
3. `ucb_bandit_decide` — [`RsiEngine.py`](./crimson-desert/RsiEngine.py)가 UCB1 + EWMA 보상으로 그래픽/안정성 프리셋(`arm`)을 선택하고, 병목 유형(`GPU_SHADER_BOUND`, `DWM_COMPOSED_LATENCY`, `MONITORING_CONVERGENCE`)을 분류합니다.
4. `guardrail_validate` — 명명된 뮤텍스(`Local\CrimsonDesert_RsiEngine_v4_Mutex`), 최소 유효 프레임(`250`), XML 3-태그 계약, `watermark_mtime` 단조 필터로 오염/중복 CSV 분석을 차단합니다.
5. `atomic_apply_or_rollback` — 크래시나 서킷 브레이커 조건 감지 시 이전 안정 프리셋으로 즉시 롤백하며, [`RsiWatcher.py`](./crimson-desert/RsiWatcher.py)는 게임 실행 감지(`GAME_SESSION_START`) 시 로컬 Ollama 모델 메모리를 즉시 비워 VRAM/시스템 경합을 방지합니다.

## 공통 스키마 계약

- 스키마: [`schema/unified_game_rsi.schema.json`](./schema/unified_game_rsi.schema.json)
- 필수 루트 필드: `schema_version`, `domain`, `project_id`, `engine_version`, `updated_at`, `closed_loop_stages`, `current_generation`, `active_arm`, `active_parameters`, `arm_stats`, `recent_generations`, `editor_verification`, `guardrails`

## 독립 스탠드얼론 검증

```bash
python sessions/game/crimson-desert/EditorEvidence.py --self-test
python sessions/game/crimson-desert/CrimsonAutoPlayEngine.py --self-test
python sessions/game/crimson-desert/RsiEngine.py --self-test
python -m py_compile sessions/game/crimson-desert/CrimsonAutoPlayEngine.py sessions/game/crimson-desert/RsiEngine.py sessions/game/crimson-desert/RsiWatcher.py sessions/game/crimson-desert/EditorEvidence.py
```

## Crimson Desert 실측 요약 (2026-11-14)

- 분석 에디터 로그: 5개 세션 (`hooked_sites_count: 17`, `verified_keys_count: 25`, `ineffective_keys: []`)
- 채택 기본 프리셋: `FSR59_UltraResponsive` (Gen `#143` `PRESERVE_LIVE_XML`, `59%`/Texture `High`·Model/Water `Low`, `crashes: 0`; 순수 3D 벤치마크 실측 `38.88 FPS`, P99 `38.68ms`, 렌더-표시 지연 `29.23ms`, 스터터 `5.1%`, 보상 `62.6/100`)
- 인게임 HUD 전수 실험(U07/U08 및 2026-11-15 실게임 재검증): 상단 바 `7/7`, 탭 헤더 `4/4`, 행 버튼 `129/129` (`140/140`, `failed_buttons: []`, `38.39 FPS`), `"자가 테스트 실시"` 엄격 게이트 + `WH_KEYBOARD_LL` 물리 입력 즉시 중단 + `ClipCursor(None)` 원자적 `SendInput` + 세이브 6슬롯 SHA-256/XML 무결성 유지
- 케이브 오버플로우·누수 및 FSR 라벨 수정(2026-11-15): `SessionBoost.cs` `SAVE_ITEM`/`SELECTED_ITEM` 본문을 `0x2680`/`0x2A80`으로 재배치 + `WriteCaveSlot()`/`FreeCaveOnInitFail()` 적용(`InitTrampolines READY @ 0x13FF00000`, `21 hooks`, `engine.log` 오류 `0`), HUD 상단 바 실스케일(`"▣ 화질:FSR59%"`) 실시간 동기화
- 화살 과다 적재 및 실시간 은화 주입 수정(2026-11-14): `GlobalItemTableManager`(`+0x06331248`) 실 `_itemKey` 역참조로 화살(`10,000,100,000 -> 501`) 오주입 근절 및 플레이어 가방(`invKey=2`, `_itemKey=1`) 실시간 은화(`187,706` = `1,877.06 G`; 2026-11-15 실게임 `6/6` 주입 `133,487,706 -> 199,987,706`) 읽기/주입 복구 ([`crimson-desert/evidence/arrow_silver_remediation_20261114.json`](./crimson-desert/evidence/arrow_silver_remediation_20261114.json))
- NPC 호감도·일일 제한 훅 복구 및 4계층 프레임·입력 지연 최적화 실게임 커널 검증(2026-11-15): `ABOVE_NORMAL`(`0x8000`) + 타이머 `0.496ms`, `ReadProcessMemory`로 4대 호감도 훅(`ROMANCE_TRUST` `0x01BDB420`·`FRIENDLY_DAILY` `0x01BDC930`·`AFFINITY_ADD` `0x01BDCC50`·`FRIENDLY_ADD` `0x0239B016`) `2.0(케이브) ↔ 1.0(바닐라) ↔ 2.0(재설치)` 왕복 바이트 증명 및 호감도 배율 `6/6` 실증, 순수 3D 45초(`1745` frames, `hud_visible_during_3d=false`, `breath=11026`·`crime=2793`·`tame=1942`) GPU `99.1%`/`53°C`/`0` 크래시 검증 ([`crimson-desert/evidence/romance_latency_optimization_20261114.json`](./crimson-desert/evidence/romance_latency_optimization_20261114.json))
- 상세 감사 리포트: [`crimson-desert/evidence/user_live_tab_audit_20261114.json`](./crimson-desert/evidence/user_live_tab_audit_20261114.json), [`crimson-desert/evidence/all_tabs_live_experiment_20261114.json`](./crimson-desert/evidence/all_tabs_live_experiment_20261114.json)
