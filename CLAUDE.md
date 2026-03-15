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
- **`ProcessOrchestrator.cs`** — Central business logic. Runs 3 independent zone loops via `Task.WhenAll`. Each loop waits for shuttle arrival, performs zone-specific work, then releases. `FireZonePos(zone)` fires position events after each motion step for real-time head visualization.
- **`Models/IoMap.cs`** — IO index constants for sensors, stoppers, align, lift, grippers, SMEMA per zone.
- **`Models/Recipe.cs`** — Zone-specific positions (pick/place for Zone 1, fiducial/bolt points for Zone 2, inspect/NG stack for Zone 3).
- **`Models/Enums.cs`** — `ProcessStage` enum uses 100/200/300-series numbering for Zone 1/2/3 stages.

### Hardware Abstraction

Interface-based design (`IMotionService`, `IIOService`, `IMotionConverter`, `IBoltService`, `IFiducialService`) with:
- Real implementations: `AjinService`/`AjinIOService` (motion/IO), `BaslerService` (cameras)
- Virtual implementations: `VirtualMotionService`, `VirtualOService` for simulation/development
- Ajin vendor SDK bindings in `IBTM.Device/Ajin/`
- Basler Pylon DLL in `IBTM.Device/Basler/`

### MVVM & UI

- **`ProcessViewModel`** — Main VM with `ZoneVisualState` objects for 2D equipment layout visualization (head XY position, Z depth as head size, shuttle/lift state, Z gauge).
- **`ZoneVisualState`** — Per-zone visual state with local Canvas coordinates (240×100). Constructor takes `(canvasW, canvasH)`. `UpdatePosition(xMm, yMm, zMm)` maps equipment coordinates to canvas coordinates. Properties: `HeadLeft/Top/Size`, `ShuttlePresent`, `IsLifted`, `LiftIndicatorVisibility`, `ShuttleStrokeThickness`, `ZGaugeHeight`, `CoordText`.
- **`ProcessView.xaml`** — 4-row layout: Stats bar → Stage cards (3 columns) → 2D equipment layout → Control buttons.
  - 2D layout uses **Grid** (5 columns × 6 rows) instead of absolute Canvas positioning. Zone columns use `Width="*"` for even distribution.
  - Each zone's gantry area: `Viewbox > Canvas(240×100)` with local coordinates for head movement.
  - Station area (conveyor level): inner Grid with columns for STP/ALN/Lift/Shuttle — all relative positioning.
  - Conveyor rails span all columns via `Grid.ColumnSpan`.
  - To adjust layout proportions, modify Grid column `Width` / row `Height` values — no pixel coordinates to update.
- ViewModels use `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm.
- **`Converters/AppConverters.cs`** — Value converters including `ZGaugeTopConverter` for Z-axis gauge visualization.

### Vision

OpenCvSharp4 used in `FiducialService` for fiducial mark detection with template matching.

## Key Dependencies

- CommunityToolkit.Mvvm 8.4.0
- Microsoft.Extensions.DependencyInjection 10.0.0
- OpenCvSharp4 4.10.0
- System.IO.Ports 10.0.3 (serial comms in Device project)
