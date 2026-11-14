# 261114-RSI: 통합 자가개선(Recursive Self-Improvement) 시스템 모노레포

모든 도메인 프로젝트의 실증 기반 자가개선(Evidence-Gated RSI) 아키텍처, 스키마, 엔진, 검증 하네스 및 실사용 감사 리포트를 통합 관리하는 저장소입니다.

## 저장소 구조

```text
.
├── LICENSE                          # MIT License (Copyright (c) 2026 gyeomsVibe)
├── README.md                        # 전체 RSI 아키텍처 및 현재 현황 개요
└── sessions/
    └── game/                        # 게임 전용 일관화 Game RSI v4.1 도메인
        ├── README.md                # 5단계 폐쇄 루프(Closed-Loop) 설계 및 검증 기준
        ├── schema/
        │   └── unified_game_rsi.schema.json
        └── crimson-desert/
            ├── RsiEngine.py         # UCB1 밴딧 + PresentMon v2 + watermark_mtime 가드
            ├── RsiWatcher.py        # 5초 데몬 감시 + 게임 시작 시 Ollama 자원 즉시 해제
            ├── EditorEvidence.py    # 18개 훅 사이트·26개 실시간 에디터 키 교차검증 (--self-test)
            ├── CdRecorder.cs        # 실시간 에디터 메모리 훅·트램폴린·FLiNG 양보(12초 유예)
            ├── DriverPolicy.ps1     # GTX 1070 드라이버 정책 감사·적용 스크립트
            ├── Apply-NvidiaProfile.ps1 # 17개 샤프/저지연 NVIDIA 드라이버 프로파일 적용·검증
            └── evidence/
                ├── unified_game_rsi_summary.json
                ├── editor_evidence.json
                └── user_live_tab_audit_20261114.json
```

## 현재 진행 현황 (2026-11-14 기준)

### 1. 게임 전용 Game RSI v4.1 아키텍처 통일
모든 게임 세션은 동일한 5단계 폐쇄 루프 및 JSON 계약(`sessions/game/schema/unified_game_rsi.schema.json`)을 따릅니다.

1. `observe_telemetry`: PresentMon v2 프레임 타임, GPU Busy/VRAM, 1% Low, 에디터 훅/포인터 로그 수집
2. `cross_analyze_game_vs_editor`: 게임 물리/그래픽 지표와 실시간 에디터 26개 제어 키의 실효성 교차 대조
3. `ucb_bandit_decide`: UCB1 + EWMA 보상 함수 기반 프리셋 탐색·활용(`AA_UltraSharp`, `AA_BalancedWater`, `FSR67_QualityHigh`, `FSR67_CombatLock`)
4. `guardrail_validate`: 단조 해상도 랭크, 최소 프레임 수(`min_major_frames: 250`), `watermark_mtime` 단조 증가 워터마크, XML 3-태그 계약 검증
5. `atomic_apply_or_rollback`: 서킷 브레이커 자동 롤백 및 원자적 설정 반영, 게임 시작 시 로컬 LLM 메모리 즉시 해제(`unload_ollama_vram_for_game`)

### 2. Crimson Desert 실플레이(14:21~14:39) 4탭 전수 감사 및 v4.1 개선 내역
- **실시간 에디터 검증**: 18개 훅 사이트(`PLAYER`, `DEFENSE`, `EXP`, `HORSE_TAMING`, `BREATH_VITALS`, `SAVE_ITEM`, `DURABILITY`, `SELECTED_ITEM`, `WANTED_EVAL`, `SLOT_EXPAND`, `COMMIT` 등) 및 26개 에디터 키 전수 검증 완료 (`ineffective_keys: []`).
- **조작권 탈취(Focus Hijack) 해결**: Win32 오버레이 HUD에 `WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE -> MA_NOACTIVATE`, `SW_SHOWNOACTIVATE`, 게임 포커스 즉시 복원 로직을 적용하고 `--self-test` Gate 5로 검증했습니다.
- **한 박자 렉(1-Beat Lag) 제거**: 핫 경로의 외부 Python 서브프로세스 반복 호출을 제거하고 네이티브 직접 읽기로 전환하여 `Apply-RealCheats` 백그라운드 프로세스 `0`개를 유지합니다.
- **GTX 1070 고화질 선명도/안정성 최적화**: `FSR67_QualityHigh` 프리셋(67% 스케일 / Texture·Model·Water High, 실측 `39.27 FPS`, `P99 30.96ms`, `EWMA 보상 68.42점`, `crashes: 0`) 및 NVIDIA 17개 샤프/저지연 설정(`17/17 Matches=True`)을 기본 채택했습니다.
- **중복 진화(Replay) 방지**: `RsiEngine.py`에 `watermark_mtime` 단조 증가 가드를 추가하여 과거 CSV 재평가(`Gen #137~#139`) 루프를 차단했습니다.

## 라이선스
본 저장소는 [MIT License](./LICENSE)를 따릅니다.
