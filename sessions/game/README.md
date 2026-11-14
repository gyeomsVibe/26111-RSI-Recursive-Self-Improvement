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

- 분석 에디터 로그: 5개 세션 (`hooked_sites_count: 18`, `verified_keys_count: 27`, `ineffective_keys: []`)
- 채택 기본 프리셋: `FSR67_QualityHigh` (Gen `#141` `PRESERVE_LIVE_XML`, `67%`/Texture·Model·Water `High`, `crashes: 0`; U08 HUD 전수 실험 세션 실측 `30.75 FPS`, P99 `46.38ms`, `1921` frames)
- 인게임 HUD 전수 실험(U07/U08): 상단 바 `7/7`, 탭 헤더 `4/4`, 행 버튼 `129/129` (`140/140`, `failed_buttons: []`), `"자가 테스트 실시"` 엄격 게이트 + `WH_KEYBOARD_LL` 물리 입력 즉시 중단 + `ClipCursor(None)` 원자적 `SendInput` + 세이브/XML `20/20` 무결성 유지
- 화살 과다 적재 및 실시간 은화 주입 수정(2026-11-14): `GlobalItemTableManager`(`+0x06331248`) 실 `_itemKey` 역참조로 화살(`10,000,100,000 -> 501`) 오주입 근절 및 플레이어 가방(`invKey=2`, `_itemKey=1`) 실시간 은화(`187,706` = `1,877.06 G`) 읽기/주입 복구 (`RVA_VERIFY_OK=20/20`, `Gates 1..7 PASS`, `OVERLAP_COUNT=0`, [`crimson-desert/evidence/arrow_silver_remediation_20261114.json`](./crimson-desert/evidence/arrow_silver_remediation_20261114.json))
- NPC 호감도·일일 제한 훅 복구 및 4계층 프레임·입력 지연 최적화(2026-11-14): `RVA_FRIENDLY_DAILY`(`0x01BDC930`)·`RVA_AFFINITY_ADD`(`0x01BDCC50`)·`RVA_FRIENDLY_ADD`(`0x0239B016`) 실함수 훅 및 `FriendlySaveData` PARC 일일 제한 초기화(`slot0`~`slot102` 6슬롯 100% 바이트 무결성), `SessionBoost`/`Launcher` 핫루프 스로틀 및 `WriteCaveParam` 섀도우 캐시, DX12 `SwapEffectUpgradeEnable=1;GpuPreference=2;`, RSI Gen `#142` `FSR59_UltraResponsive`(`59%`/Low 모델·물/High 텍스처) 적용 ([`crimson-desert/evidence/romance_latency_optimization_20261114.json`](./crimson-desert/evidence/romance_latency_optimization_20261114.json))
- 상세 감사 리포트: [`crimson-desert/evidence/user_live_tab_audit_20261114.json`](./crimson-desert/evidence/user_live_tab_audit_20261114.json), [`crimson-desert/evidence/all_tabs_live_experiment_20261114.json`](./crimson-desert/evidence/all_tabs_live_experiment_20261114.json)
