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
python -m py_compile sessions/game/crimson-desert/RsiEngine.py sessions/game/crimson-desert/RsiWatcher.py sessions/game/crimson-desert/EditorEvidence.py
```

## Crimson Desert 실측 요약 (2026-11-14)

- 분석 에디터 로그: 4개 세션 (`hooked_sites_count: 18`, `verified_keys_count: 26`, `ineffective_keys: []`)
- 채택 기본 프리셋: `FSR67_QualityHigh` (3회 평가, EWMA 보상 `68.42`, 최근 `39.27 FPS`, `P99 30.96ms`, `crashes: 0`)
- 상세 감사 리포트: [`crimson-desert/evidence/user_live_tab_audit_20261114.json`](./crimson-desert/evidence/user_live_tab_audit_20261114.json)
