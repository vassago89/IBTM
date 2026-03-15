# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

IBTM is a WPF desktop application (.NET 10.0) for an SMT (Surface Mount Technology) Inline Bolt Tightening Control System. It controls multi-axis motion, vision-based fiducial detection, and bolt tightening on a production line. Comments are written in Korean.

## Build & Run Commands

```bash
dotnet build              # Build entire solution
dotnet run --project IBTM # Run the WPF application
dotnet clean              # Clean build artifacts
```

Solution file: `IBTM.slnx` (modern format). No test projects or linting tools are configured.

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

- **`ProcessViewModel`** — Main VM with `ZoneVisualState` objects for 2D equipment layout visualization. Tracks shuttle transit between zones (`ShuttleTransit12/23/Out`), zone timing (`Zone1/2/3Timing`), fiducial results, and route branching (`IsNgPath/IsGoodPath`).
- **`ZoneVisualState`** — Per-zone visual state with local Canvas coordinates (240×180). Constructor takes `(canvasW, canvasH)`. `UpdatePosition(xMm, yMm, zMm)` maps equipment coordinates to canvas coordinates (MaxCoord=200, MaxZ=60). Properties:
  - Head: `HeadLeft/Top/Size`, `BridgeTop`
  - Shuttle: `ShuttlePresent`, `IsLifted`, `LiftIndicatorVisibility`, `ShuttleStrokeThickness`
  - PCB: `PcbCount`, `Pcb1Visibility`, `Pcb2Visibility` — Zone 1에서 배치 시 증가, Zone 3에서 NG 이송 시 감소
  - Gripper: `GripperActive`, `GripperVisibility` — 그리퍼 ON/OFF 시 GRIP 뱃지 표시
  - Fiducial (Zone 2 only): `FiducialOffsetLeft/Top`, `FiducialOffsetVisibility` — 보정 위치 시안색 마커
  - Z gauge: `ZGaugeHeight`, `ZToolTop`, `ZRatio`, `CoordText`
- **`StageCardViewModel`** — `StageStatus.Skipped` 지원. Zone 3 Good/NG 분기 시 안 쓰는 경로의 카드가 Skipped(흐린 회색)으로 표시.
- **`ProcessView.xaml`** — 4-row layout: Stats bar → Stage cards (3 columns) → 2D equipment layout → Control buttons.
  - 2D layout uses **Grid** (5 columns × 6 rows) instead of absolute Canvas positioning. Zone columns use `Width="*"` for even distribution.
  - Each zone's gantry area: `Viewbox > Canvas(240×180)` with local coordinates for head movement.
  - Station area (conveyor level): inner Grid with columns for STP/ALN/Lift/Shuttle — all relative positioning.
  - Conveyor rails span all columns via `Grid.ColumnSpan`. 가동 중 Storyboard 순차 페이드 애니메이션.
  - Zone 사이 셔틀 이동 뱃지 (Transit12/23/Out) — Release 완료 → 다음 Zone 도착 전 표시.
  - Zone 3 캔버스에 NG 스택 적재 영역 마커.
  - 각 Zone 좌표 바에 마지막 작업 소요시간 표시 (병목 구간 파악).
  - To adjust layout proportions, modify Grid column `Width` / row `Height` values — no pixel coordinates to update.
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm.
- **`Converters/AppConverters.cs`** — Value converters including `ZGaugeTopConverter` for Z-axis gauge visualization. `StageStatus.Skipped` → 흐린 회색 border + 어두운 배경.

### Vision

OpenCvSharp4 used in `FiducialService` for fiducial mark detection with template matching.

## Key Dependencies

- CommunityToolkit.Mvvm 8.4.0
- Microsoft.Extensions.DependencyInjection 10.0.0
- OpenCvSharp4 4.10.0
- System.IO.Ports 10.0.3 (serial comms in Device project)
