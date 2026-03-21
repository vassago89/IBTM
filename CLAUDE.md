# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

IBTM is a WPF desktop application (.NET 10.0) for an SMT (Surface Mount Technology) Inline Bolt Tightening Control System. It controls multi-axis motion, vision-based fiducial detection, and bolt tightening on a production line. **Comments and XML doc comments are written in Korean. Do not translate comments to English.**

## Build & Run Commands

```bash
dotnet build              # Build entire solution
dotnet run --project IBTM # Run the WPF application
dotnet clean              # Clean build artifacts
```

Solution file: `IBTM.slnx` (modern format). No test projects or linting tools are configured.

## Verification Requirements

**코드 변경 후 반드시 아래 단계를 수행할 것:**

1. **.NET SDK 설치 확인** — `dotnet --version`으로 .NET 10.0 SDK가 설치되어 있는지 확인. 없으면 설치.
2. **빌드 확인** — `dotnet build`로 컴파일 에러 없이 빌드되는지 확인.
3. **실제 뷰 확인** — `dotnet run --project IBTM`으로 WPF 앱을 실행하여 UI가 정상 렌더링되는지 확인. XAML 바인딩 오류, 런타임 예외 없는지 점검.

SDK가 설치되지 않은 환경에서는 빌드/실행 검증 없이 코드를 커밋하지 말 것.

## Solution Structure

- **IBTM/** — Main WPF application (MVVM pattern using CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection)
- **IBTM.Device/** — Hardware abstraction library for motion controllers, IO, and cameras

## Architecture

### 3-Zone Pipeline

The equipment has 3 zones operating in parallel on separate shuttles via a shared conveyor with lift-and-locate stations:

| Zone | Motion Axes | Function |
|------|-------------|----------|
| **Zone 1 (Pick)** | `zone1` XYZ (axes 0,1,2) | Pick 2 PCBs from previous equipment's shuttle → place on our shuttle |
| **Zone 2 (Bolt)** | `zone2` XYZ (axes 3,4,5) | Fiducial detection → bolt tightening (N points per recipe) |
| **Zone 3 (Inspect)** | `zone3` XYZ (axes 6,7,8) | Camera inspection → NG stack (max 3) or SMEMA discharge |

Each zone follows: **Sensor detect → Stopper → Align → Lift up → Work → Lift down → Stopper release**

Shuttles are lifted above the shared conveyor during work, so zones don't interfere with each other.

### Key Files

- **`App.xaml.cs`** — DI configuration. 3 keyed `IMotionService` singletons (`zone1`, `zone2`, `zone3`).
- **`ProcessOrchestrator.cs`** — Central business logic. Runs 3 independent zone loops via `Task.WhenAll`. Each loop waits for shuttle arrival, performs zone-specific work, then releases. `FireZonePos(zone)` fires position events after each motion step for real-time head visualization. `GripperChanged` event fires on gripper ON/OFF (Zone 1 pick/place, Zone 3 NG transfer). Zone 3 has conditional branching: NG path (NgTransfer → Release) vs Good path (SmemaWait → Release → Discharge).
- **`Models/IoMap.cs`** — IO index constants for sensors, stoppers, align, lift, grippers, SMEMA per zone.
- **`Models/Recipe.cs`** — Zone-specific positions (pick/place for Zone 1, fiducial/bolt points for Zone 2, inspect/NG stack for Zone 3).
- **`Models/Enums.cs`** — `ProcessStage` enum uses 100/200/300-series numbering for Zone 1/2/3 stages. `StageStatus` includes `Skipped` for conditional branching visualization.

### Hardware Abstraction

Interface-based design (`IMotionService`, `IIOService`, `IMotionConverter`, `IBoltService`, `IFiducialService`) with:
- Real implementations: `AjinService`/`AjinIOService` (motion/IO), `BaslerService` (cameras)
- Virtual implementations: `VirtualMotionService`, `VirtualOService` for simulation/development
- Ajin vendor SDK bindings in `IBTM.Device/Ajin/`
- Basler Pylon DLL in `IBTM.Device/Basler/`

### MVVM & UI

- **`ProcessViewModel`** — Main VM with `ZoneVisualState` objects for 2D equipment layout visualization. Tracks shuttle transit between zones (`ShuttleTransit12/23/Out`), zone timing (`Zone1/2/3Timing`), fiducial results, and route branching (`IsNgPath/IsGoodPath`). `BoltMarkers` (`ObservableCollection<BoltMarkerViewModel>`) — 레시피 `BoltPoints`에서 동적 생성, 볼트 개수는 생산 모델에 따라 다름. `NgStackSlots` (`ObservableCollection<NgSlotViewModel>`) — 레시피 `NgStackMaxCount`에서 동적 생성.
- **`ZoneVisualState`** — Per-zone visual state with local Canvas coordinates (240×180). Constructor takes `(canvasW, canvasH)`. `UpdatePosition(xMm, yMm, zMm)` maps equipment coordinates to canvas coordinates (MaxCoord=200, MaxZ=60). Properties:
  - Head: `HeadLeft/Top/Size`, `HeadCenterLeft/Top` (중심점 좌표), `BridgeTop`
  - Crosshair: `CrosshairLeft/Top` (중심), `CrosshairH1/H2` (수평선 X범위), `CrosshairV1/V2` (수직선 Y범위) — 헤드 위치 추적 보조선 (±30px, 캔버스 범위 클램프)
  - Shuttle: `ShuttlePresent`, `IsLifted`, `LiftIndicatorVisibility`, `ShuttleStrokeThickness`
  - PCB: `PcbCount`, `Pcb1Visibility`, `Pcb2Visibility` — Zone 1에서 배치 시 증가, Zone 3에서 NG 이송 시 감소
  - Gripper: `GripperActive`, `GripperVisibility` — 그리퍼 ON/OFF 시 GRIP 뱃지 표시
  - Fiducial (Zone 2 only): `FiducialOffsetLeft/Top`, `FiducialCrossH1/H2`, `FiducialCrossV1/V2`, `FiducialOffsetVisibility` — 보정 위치 시안색 십자선 마커
  - Z gauge: `ZGaugeHeight`, `ZToolTop`, `ZRatio`, `CoordText`
- **`StageCardViewModel`** — `StageStatus.Skipped` 지원. Zone 3 Good/NG 분기 시 안 쓰는 경로의 카드가 Skipped(흐린 회색)으로 표시.
- **`ProcessView.xaml`** — 4-row layout: Stats bar → Stage cards (3 columns) → 2D equipment layout → Control buttons.
  - 2D layout uses **Grid** (5 columns × 6 rows) instead of absolute Canvas positioning. Zone columns use `Width="*"` for even distribution.
  - Each zone's gantry area: `Viewbox Stretch="Uniform" > Canvas(240×180)` with local coordinates for head movement. Uniform stretch preserves aspect ratio (no distortion).
  - Station elements (STP/ALN/Shuttle) are inside the Canvas at fixed coordinates — scaled uniformly with the canvas.
  - 컨베이어 연결 레일: Zone 전체 관통하는 수평 `Rectangle` 2줄 (`Grid.ColumnSpan="3"`, VerticalAlignment Center ±27px). Zone 간 시각적 연속성 제공.
  - 헤드 십자선: Zone별 색상 dashed Line 2개 (수평/수직), `CrosshairH1/H2/V1/V2` 바인딩. 헤드 이동 시 실시간 추적.
  - Zone 2 볼트 마커: `ItemsControl` + `BoltMarkers` 바인딩으로 레시피 기반 동적 렌더링. 각 마커는 `BoltMarkerViewModel`이 상태별 색상(`MarkerBrush`) 직접 제공.
  - Zone 2 볼트드라이버 헤드에 소켓 중심점, Zone 3 카메라 헤드에 렌즈 내부 원 표시.
  - Zone 2 피듀셜 보정 십자선 (가로/세로 Line 2개, 시안색 점선).
  - Zone 사이 셔틀 트랜짓 뱃지 (Transit12/23/Out) — 36×20 펄스 애니메이션 (`► ► ►`, 0.5초 주기 Opacity 0.4↔1.0). Zone별 색상. Release 완료 → 다음 Zone 도착 전 표시.
  - NG 스택 슬롯: `ItemsControl` + `NgStackSlots` 바인딩으로 레시피 max 기반 동적 렌더링. 카운트 표시도 `NgStackMaxCount` 바인딩.
  - Zone 3 캔버스에 검사 영역 + NG 스택 적재 영역 dashed 마커.
  - 각 Zone 좌표 바에 마지막 작업 소요시간 표시 (병목 구간 파악).
  - 우측 패널: 결과 배지(GOOD/NG), NG 스택 슬롯, SMEMA 상태, 컨베이어 흐름 순차 페이드 애니메이션.
  - To adjust layout proportions, modify Grid column `Width` / row `Height` values — no pixel coordinates to update.
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm.
- **`Converters/AppConverters.cs`** — Value converters including `ZGaugeTopConverter` for Z-axis gauge visualization. `StageStatus.Skipped` → 흐린 회색 border + 어두운 배경.

### Localization (i18n)

- **`Localization/Loc.cs`** — Dictionary-based singleton for runtime EN/KO language switching. Default language is English.
  - XAML: `{Binding [Key], Source={x:Static loc:Loc.Instance}}` (indexer binding)
  - C#: `Loc.S("Key")` or `Loc.S("Key", args)` for formatted strings
  - `ToggleLanguage()` switches between EN↔KO at runtime without app restart
  - `OnPropertyChanged("Item[]")` refreshes all XAML indexer bindings
  - Key naming: `Btn_*`, `Stage_*`, `Sub_*`, `Act_*`, `Stat_*`, `Card_*`, `NgAlarm_*`, `Fiducial_*`, `Bolt_*`, `Torque_*`
- **UI strings** (buttons, stage labels, status messages) go through `Loc` for localization
- **Log messages** (`AddLog` in `ProcessOrchestrator`) are hardcoded in English (developer-facing, not localized)
- **Code comments** stay in Korean — do not localize

### Vision

OpenCvSharp4 used in `FiducialService` for fiducial mark detection with template matching.

## Key Dependencies

- CommunityToolkit.Mvvm 8.4.0
- Microsoft.Extensions.DependencyInjection 10.0.0
- OpenCvSharp4 4.10.0
- System.IO.Ports 10.0.3 (serial comms in Device project)
