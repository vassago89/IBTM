# 직접 개발할 때 보는 안내

기준: 2026-09-14 소스. 목적은 **수정 → 현장 확인 → 원인 확인** 사이클을 짧게 하는 것이다.
새 계층보다 실제 호출과 조건이 한눈에 보이는 코드를 우선한다. 상세 작업 규칙은 루트 `AGENTS.md`.

## 수정할 파일

경로는 저장소 루트 기준이다.

| 바꾸려는 것 | 먼저 열 파일 |
| --- | --- |
| 장치 생성·연결 | `IBTM/App.xaml.cs`, `IBTM/DependencyInjection.cs` |
| Start 준비 조건 / 자동 유닛 시작·취소·예외 | `IBTM/MachineController.Automatic.cs` |
| 공통 연결 / Stop·Shutdown / 안전 입력·이동 인터록 | `IBTM/MachineController.cs` |
| 장치 초기화 / Reset 허용 조건·복구 순서 | `IBTM/MachineController.Recovery.cs` |
| 전체·유닛·축 Home / 실린더 상승 / Home 차단 이유 | `IBTM/MachineController.Home.cs` |
| 수동 축 이동 / 티칭 저장 / 서보·ADC·볼트 테스트 | `IBTM/MachineController.Manual.cs` |
| Repeat 왕복 경로와 마지막 유닛 | `IBTM/MachineController.Repeat.cs` |
| 수동 DO 조작 | `IBTM/MachineController.Outputs.cs`, `IBTM/UI/OutputWindowRow.cs` |
| 화면 표시 상태 | `IBTM/MachineController.Display.cs`, `IBTM/UI/OperationViewModel.Display.cs` |
| 메인 컨베이어 이송·감지 후 밀착 시간 | `Stations/IBTM.Conveyor/MainConveyor.cs`, `ConveyorSettings.cs` |
| 백업 플레이트·스토퍼 | `Shared/IBTM.Device/ConveyorStation.cs` |
| Station 3 작업/NG 대기 | `Stations/IBTM.Inspection/InspectionStation.cs`, `InspectionWork.cs` |
| NG 픽업·복귀 이동 순서 | `Stations/IBTM.Inspection/NgCarrierMove.cs` |
| NG 실린더·그리퍼 | `Stations/IBTM.Inspection/NgCarrierTransfer.cs` |
| 셔틀·NG 벨트 | `Stations/IBTM.NgConveyor/NgShuttle.cs`, `NgCarrierConveyor.cs` |
| 티칭 화면 배치 | `IBTM/UI/TeachingView.xaml` |
| 공통 티칭 I/O 행·그룹 템플릿 | `IBTM/UI/IoWindowStyles.xaml` |
| 티칭 포인트·선택 | `IBTM/UI/TeachingViewModel.cs` |
| 티칭 Home / 조그 / Move To | `IBTM/UI/TeachingViewModel.Motion.cs`, `TeachingMotionViewModel.cs` |
| Live / FOV 추가 / ROI 저장 / Data Matrix | `IBTM/UI/TeachingViewModel.Camera.cs` |
| 이미지 위 ROI·십자선 그리기 | `IBTM/UI/ImageTeachingView.cs` |
| 실제 검사 이동·촬영·판정 | `Stations/IBTM.Inspection/BoltInspector.cs` |
| 카메라 연결·수신 | `Hardware/IBTM.Hik/HikCamera.cs` |
| ADC 시리얼·파서 | `Hardware/IBTM.Hantas/` |
| AJIN 단위·Home·축 이동 | `Hardware/IBTM.Ajin/AjinMotionService.cs` |
| DI·DO·축 피드백 수집·감시 수명 | `IBTM/MachineFeedbackMonitor.cs` |
| 축 진단값·화면 바인딩 데이터 | `Shared/IBTM.Device/MotionStatus.cs` |
| DI/DO 화면용 상태 | `Shared/IBTM.Device/IoStatus.cs`, `IoSignals.cs` |
| IO 번호·축 번호 기본값 | 각 유닛의 `*HardwareSettings.cs` |
| 설정 구성·편집 화면 | `IBTM/MachineSettings.cs`, `IBTM/UI/SettingsViewModel.cs` |
| DB JSON 저장 | `Shared/IBTM.Storage/MachineStore.cs` |
| 레시피 이미지 저장·교체 | `IBTM/RecipeStore.cs`, `IBTM/UI/RecipeEditor.cs` |

## 막힌 동작을 따라가는 순서

티칭 메뉴는 `Teaching` 하나다. 유닛 목록에서 Supply, Placement, Fastening,
Inspection, NG Transfer를 선택한다. 인계 위치·이탈 높이·경계는 각각 Supply와 Placement의
티칭 목록에 포함되며, 축 피드백과 I/O는 선택 유닛을 따른다. 인계값은 Teach로 임시 보관하고
두 유닛에서 보이는 `Apply & Save Handoff`로 양쪽 값을 묶어서 적용·저장한다. 유닛을 전환해도 임시값은 유지되며,
Teaching 메뉴를 나갔다 다시 열면 저장·적용된 설정에서 다시 읽는다.

각 유닛 목록은 작업 위치, 설비 기준값, 계산 위치, 인계 간섭 영역으로 구분한다.
Supply의 `PCB Give Position`은 XY만 티칭하며 `Transport / Rotation Z`에서 그대로 전달한다.
Placement는 `PCB Receive Position` → Heat Sink 1/2 안착 순서다. `Post-release Clearance Z`는
Supply가 해제 후 X로 빠지기 위한 높이다. 체결의 B1/B2는 헤드별 계산 위치이며 Move To로 확인한다.
검사 볼트는 Add Bolt로 생성하고 FOV/ROI를 연결한다. 좌표 없는 안내 항목은 목록에 넣지 않는다.

1. XAML의 `Command` / `IsEnabled` 바인딩 이름을 찾는다.
2. ViewModel의 `[RelayCommand(CanExecute = nameof(...))]`가 가리키는 조건을 본다.
3. 실제 실행 메서드에서 `MachineController` 또는 유닛 호출을 따라간다.
4. 유닛의 현재 상태 분기에서 읽는 **DI/축 피드백을 실장비와 대조**한다.
5. 명령이 나갔다면 TX/DO 로그와 실제 RX/DI를 나눠 확인한다. DO ON 로그만으로 구동 성공을 단정하지 않는다.

생성된 `*Command` 코드를 수정하지 말고 해당 ViewModel의 이름 있는 메서드를 수정한다.
알람 전체 조건과 개별 동작의 간섭 조건을 섞지 않는다. 임의의 지연·재시도·catch로 원인을 감추지 않는다.

티칭 DO는 `ToggleOutputCommand` → `MachineController.ToggleTeachingOutputAsync`에서
현재 DO를 읽어 반전한다. XAML은 ON/OFF에 따라 명령을 교체하지 않는다.
저장 FOV의 화면 객체는 `Metadata`로 레시피의 `CarrierImageTile`을 직접 참조한다.
ROI·대상·촬영 좌표의 화면용 복사본을 추가하지 않는다.

## 바로 걸어 둘 중단점

설비 동작 경계의 우선 확인 지점:

- `OperationCancellation.TryBegin`: 최상위 운전의 실행권을 확보한다. 다른 운전이나 STOP 정리가
  남으면 시작하지 않는다. 내부 축·실린더 작업은 기존 `Link`로 같은 취소 수명에 참여한다.
  START/HOME/수동 이동·컨베이어/ADC/초기화·복구와 설정 저장·조명 테스트가 이 경계를 사용한다.
- `MachineController.StartAsync.StopWhenOperationBecomesUnavailable`: 정지를 요구하는 DI를 먼저
  확인한 뒤 정상 상태에서만 현재 SDK 준비 상태를 읽는다. SDK 대신 캐시로 운전을 허용하지 않는다.
- `MainConveyor.PrepareEmptyStationsAsync`: 최초 START와 STOP 후 재개 모두 빈 스테이션만 내린다.
  캐리어 또는 NG 픽업의 지지 상태는 유지하고, 실제 이송의 Release 단계가 하강을 소유한다.
- `StationWork.CurrentJob` / `Complete(job)`: 결과와 완료의 작업 주인이다. 캐리어 교체 후 이전 작업의
  완료는 거부한다. `MainConveyor._transferJob`은 출발 시 결과를 잡아 새 출발 캐리어와 섞이지 않게 한다.
- `AutoUnit.TraceStep`: 이미 선택한 실행 단계를 로그에 남긴다. 같은 단계·대상·작업·대기 이유가
  유지되는 동안 로그를 반복하지 않는다. 실제 위치·완료 판단에 이 기록을 사용하지 않는다.

집중 검사는 `ConcurrentManualAdmissionOnlyStartsOneDeviceCommand`,
`TopLevelAdmissionWaitsForCancelledOwnerAndChildCleanup`,
`EmergencyInputStopsConveyorBeforeReadingUnrelatedMotionFeedback`,
`StartupPreparationPreservesOccupiedSupportsAndNgPickupSupport`,
`DepartedWorkCannotCompleteNewCarrierAndTransferKeepsOriginalLoad`,
`StepTraceKeepsWaitReasonAndTargetWithoutRepeatingUnchangedFeedback`다.

자동운전 호출은 `OperationViewModel.StartAsync` → `MachineController.StartAsync` →
`RunAutomaticUnitsAsync` → 각 유닛의 `RunAsync` 순서다.
Start 허용 조건과 실행·유닛 시작 목록은 `MachineController.Automatic.cs`에 모여 있다.
Home의 허용 조건·차단 이유·실행은 `MachineController.Home.cs`,
장치 초기화·Reset은 `MachineController.Recovery.cs`, 수동 작업 수명은
`MachineController.Manual.cs`에서 따라간다. 모두 같은 `MachineController`의 partial 파일이며
의존성과 공통 Stop·안전 인터록은 `MachineController.cs`가 소유한다.
`RunAutomaticUnitAsync`는 동기 시작 오류까지 잡고, 한 유닛이 종료되면 나머지 유닛에
취소를 요청한다. `Task.WhenAll(runningUnits)`가 끝나야 정지 또는 Repeat 역방향으로 넘어간다.
시작 준비의 `InitializeHardwareAsync`도 각 모션 초기화 호출이 반환되면 취소를 확인한다.
Stop 전에 이미 들어간 동기 SDK 호출은 반환을 기다리지만, 다음 유닛의 초기화로 넘어가지 않는다.
이 취소는 초기화 실패 알람으로 바꾸지 않으며 `StopDuringMotionInitializationSkipsLaterUnitsAndCanRetry`로 검증한다.

메인 컨베이어의 정지 출력은 `MainConveyor.StopOutputs`에서 확인한다. 호출부에 OFF 대상이
명시돼 있다. Stop·수동/자동 운전 종료는 모터와 전후단 SMEMA, 이송 중 핸드셰이크 초기화는
SMEMA만 대상으로 한다. 입고·배출 단계는 모터와 해당 핸드셰이크를 한 종료 블록에서 정리한다.
한 출력 쓰기가 실패해도 뒤의 출력을 계속 처리하며, 기존 동작 오류가 있으면 함께 전달한다.
`StopAttemptsEveryDeviceAndPreservesWriteFailures`와
`ConveyorStopsMotorAndPreservesRunFailureWhenHandshakeCleanupFails`가 출력 누락과 오류 보존을 확인한다.
NG 컨베이어도 `NgCarrierConveyor.Stop`에서 모터·배출 안내·완료 램프를 각각 OFF 시도한다.
메인·NG 컨베이어는 각 장치의 `RunControlledAsync`가 수동·자동 운전의 취소 등록과 종료를 맡는다.
메인의 `_runCancellation`은 현재 실행의 취소 대상이며, 캐리어 위치나 이송 이력은 아니다.
종료 출력 실패가 앞선 운전 오류를 덮지 않도록 함께 전달한다.
`ConveyorRunFailureSurvivesOutputCleanupFailure`에서 원본 운전·정지 오류를 확인한다.
단계 내부의 종료도 같은 원칙이다. `TransferStepPreservesOperationAndCleanupFailures`는
메인 입고·이송·배출·복귀, NG 이송, 슈팅, PCB 공급, 슈팅 피더에서 동작과 OFF가 함께
실패해도 원본 예외가 남는지 확인한다. `MainConveyorStopPreservesCancellationAndOutputFailures`는
직접 Stop의 취소 콜백 오류를, `FasteningRunPreservesFailureWhenShootingCleanupFails`는
체결 유닛 전체 종료의 원본 오류를 확인한다. `AggregateException`이면 `Flatten().InnerExceptions`로
모든 원인을 확인한다. 마지막 OFF 오류 하나만 보고 최초 동작 오류로 판단하지 않는다.
`MachineController.Stop`과 `ShutdownAsync`는 취소·출력·종료 감시 오류를 함께 보존한다.
종료 오류가 있어도 작업 정리를 기다린 다음 피드백 감시를 종료하며,
`StopAndShutdownPreserveCancellationAndOutputFailures`로 이 순서를 확인한다.

수동·자동·Repeat의 모션 알람 분류는 `MachineController.IsMotionFailure`에서 중첩된
모션 오류까지 확인한다. 알람 상세에는 분류 전 원본 예외 전체를 남긴다.
`RunManualAsync`는 취소와 장치 정리 실패가 함께 발생해도 장치 알람·Stop을 처리하며,
장치 오류가 없는 프로그래밍 예외는 호출부로 전달한다.
실린더 상승의 `RaiseAsync`도 STOP 이후 장치 오류를 누락하지 않는다. 먼저 발생한 안전 알람이
있으면 유지하고, 추가 오류는 `Cylinder raise ... failed while stopping` 로그로 확인한다.
일괄 실린더 상승은 `IsCylinderRaiseClear`에서 캐리어 재실과 플레이스먼트 PCB 감지를 확인한다.
PCB를 들고 있을 때는 IPM을 내린 상태를 유지하며, 버튼 표시·실행·진행 중 취소가 같은 조건을 사용한다.

운전·티칭·설정·수동 컨베이어·모션 화면 종료는 `UI/CommandShutdown.StopAsync`에서
실행 중인 각 명령의 취소를 시도한 뒤 모두 기다린다. 한 취소가 실패해도 나머지 명령을 취소하며,
화면 비활성화·취소 콜백·명령 정리 오류를 함께 전달한다. `CommandShutdownTests`가
대기 순서, 복수 오류, 정상 취소와 첫 취소 실패 뒤 나머지 명령의 정리를 확인한다.
티칭 화면은 명령 종료 후 마지막 이미지 작업과 카메라 종료를 기다리며 앞선 명령 오류도 보존한다.
`OperationCancellation.Link`와 `Operation.Cancel`도 시작·취소 오류 뒤 종료 알림이 실패하면
두 예외를 함께 남긴다. `OperationCancellationTests`에서 오류 보존과 작업 수명 해제를 확인한다.

설정 저장·백업·복원·가상 이미지 변경은 버튼의 `CanExecute`뿐 아니라 실행 메서드에서도
현재 편집 허용 상태를 확인한다. `SettingsStayLockedWhileBusyOrClosingEvenWithAnAlarm`은
작업 중·종료 중 직접 호출해도 파일 대화상자를 열거나 검사 입력 이미지를 바꾸지 않는지 검증한다.

아래 표는 자동 유닛 10개와 공용 인계 영역를 포함한다. 각 유닛은 `AutoUnit.RunLoopAsync`에서
현재 피드백으로 다음 동작을 선택하고, 할 일이 없으면 `WaitForChangeAsync`로 기다린다.
검사와 NG 이송은 갠트리를 공유하므로 `InspectionStation.RunAsync` 한 경로에서 실행한다.
`BufferStage`는 공급·안착이 함께 읽는 진입 조건이며 별도 실행 루프를 만들지 않는다.

| 증상 | 중단점 위치 | 먼저 볼 값 |
| --- | --- | --- |
| Start가 실행돼도 돌아오거나 버튼이 비활성 | `MachineController.StartAsync`의 `IsStartAllowed` 조건 / `MachineController.GetStartBlock` | 실행 시 `startBlock`, 버튼은 `State.Display.StartBlock` |
| 특정 유닛이 시작하지 않음 | `RunAutomaticUnitsAsync`의 해당 유닛 `if`, `RunAutomaticUnitAsync`의 취소 조건 | `_units`, `repeat`, `alarm`, `cycle.IsCancellationRequested` |
| 자동운전 중 알람 발생 | `RunAutomaticUnitAsync`의 `catch (Exception exception)` | `alarm`은 발생 유닛, `exception`은 원본 오류, `_state.Alarm`은 먼저 발생한 알람 |
| Repeat 메인 복귀가 취소됨 | `MachineController.Repeat.cs`의 `GetMainConveyorReturnBlock`, `ReturnMainCarrierAsync`의 `CheckPath` | 핸들러 상승·안전 Z, NG 픽업 상승·캐리어 센서; 현재 피드백으로 차단 이유를 반환 |
| 메인 컨베이어가 이송하지 않거나 센서 사이에서 멈춤 | `MainConveyor.ExecuteAsync`, `ReadState` | `state`, `_transfer`의 출발·도착·수신/배출 단계, 현재 도착 센서 |
| PCB 공급이 대기하거나 예상과 다른 동작 | `PcbSupplier.RunAsync` 안 `ExecuteAsync`의 `switch (state)`, `PickPcbAsync` | `state`, `_pickStep`; 픽업 중에는 `pickPosition`, `carrierChanged` |
| PCB 안착이 멈춤 | `PcbPlacer.ExecuteAsync`, `PlaceStepAsync`의 `switch (state)` | `heatSink`, `state`, `action`; `action == null`이면 피드백 대기 |
| 공급·안착이 버퍼에 진입하지 못함 | `BufferStage.CanEnterSupply`, `CanEnterPlacement`, `HasConflict` | 양쪽 현재 위치·Home·이동 피드백, 각 핸들러의 `PcbSecured`, 인계 좌표 |
| 픽업 또는 슈팅 볼트 피더가 대기/타임아웃 | 두 피더가 공유하는 `BoltFeeder.ExecuteAsync` | `waitingForBolt`, `_boltDetected`, `TimeoutMilliseconds`; 슈팅 출력은 `ShootingBoltFeeder.SetFeeding` |
| 볼트 체결이 멈춤 | `BoltFasteningStation.RunCarrierAsync`, `ExecuteAsync`, `FastenAsync` | `state`, `head`, `pass`, `_pendingFastening`의 볼트·캐리어·패스 |
| Station 3 검사/NG 이송이 대기 | `InspectionStation.ExecuteAsync`, `ExecuteInspectionAsync` | `transferState`, `inspectionState`, `bolt`; `transfer == null`이면 이송 명령 없이 피드백을 기다림 |
| NG 이송의 정방향·복귀 순서가 예상과 다름 | `NgCarrierMove.State`, `ExecuteAsync`, `MoveToCarrierAsync` | `destination`, `state`, 현재 픽업 상승·그립·캐리어 감지, Safe X |
| NG 셔틀이 대기하거나 Repeat 상승을 재개하지 않음 | `NgShuttle.ExecuteAsync`, `CycleAsync` | `state`, `_cycleReturnPending`, 실제 Up/Down·캐리어·픽업 상승 피드백 |
| NG 컨베이어 적재·배출·재개가 막힘 | `NgCarrierConveyor.ExecuteAsync`, `MoveCarrierAsync`, `ReadState` | `state`, `destination` 입력, `_movement`, `_ejectionPhase`, 현재 위치 센서 |
| 실린더 타임아웃 | `IIoService.SetOutputAndWaitAsync`, `WaitForInputAsync` | 출력 `output`/`value`, 기다리는 입력 `input`/`value`, 제한시간 |

예를 들어 공급의 `switch (state)`에 조건부 중단점
`state == PcbSupplyState.WaitingForHandoff`를 걸면 해당 대기로 들어가는 판단을 볼 수 있다.
이벤트 대기 중에는 새 피드백이 와야 다음 판단으로 들어간다. `state`, `transferState`,
`inspectionState`는 그 회차에 선택한 분기이며, 장비 위치를 저장하는 별도 상태가 아니다.

PCB 공급의 그립·해제 출력 순서는 `ExecuteAsync`의 해당 `case`에서 바로 확인한다.
`_pickStep`은 전단 캐리어에서 확인한 PCB 슬롯 이력이며 Stop·재시작 사이에도 유지한다.
`OnHandlerChanged`가 정지 중에도 전단 캐리어 이탈을 받아 다음 캐리어의 PCB1을 선택한다.
현재 위치나 PCB 센서값으로 이 슬롯 이력을 대신하지 않는다.
`SupplyKeepsCheckedSlotsUntilUpstreamCarrierChanges`가 같은 캐리어 재개, 정지 중 교체,
픽업 감지 순간의 Stop과 전단 배출 허용을 확인한다.
`PickPcbAsync`는 픽업 중 전단 캐리어 이탈을 받으면 그 픽업을 취소한다. 늦게 끝난 이전 픽업은
새 캐리어의 슬롯 이력을 넘기지 않으며 `SupplyDoesNotAdvanceTheNewCarrierWhenAnOldPickupFinishes`로 확인한다.
수동 한 축 이동은 `TeachingViewModel.StepAsync` → `PcbSupplyHandler.MoveAxisAsync` →
`MotionService.MoveAxisAsync` 순서다. 공급 핸들러에서 버퍼 내부 Y/Z 이동을 막고,
모션 계층에서 축 속도·범위·안전 Z·취소를 처리한다. 자동/수동 인계 진입은
`MoveToHandoffAsync`에서 Rotation Z 유지 → Y → X 순서로 진행한다. 별도 인계 하강은 없으며,
해제 후 이탈만 `MoveClearAsync`에서 Clear Z → X 원점 순서로 진행한다.

PCB 안착의 XY 이동은 `PcbPlacementHandler.MoveToXYAsync`, Z 이동은 `MoveAxisAsync`에서
장치 호출로 이어진다. `PressPcbAsync`의 `_pressingHeatSink`는 중간 정지 후 눌러 붙이기를
마칠 대상이다. 같은 Down 센서값만으로 작업 전후를 구별할 수 없으므로 이 이력을 유지하고,
현재 IPM·그리퍼 피드백이 완료 조건을 만족해야 `RecordingPlacement`로 넘어간다.
캐리어 교체 시에는 `OnCarrierChanged`가 이 이력을 지워 새 캐리어를 완료 처리하지 않는다.
`PlacementResumesPressOnlyForTheSameCarrier`가 같은 캐리어의 재개와 교체를 함께 확인한다.
그리퍼를 닫은 뒤 누름 출력 직전에도 같은 캐리어·현재 안착 피드백을 확인한다.
`PlacementDoesNotPressAfterCarrierChangesDuringGripperClose`가 이 구간의 교체·지지 상실을 검증한다.
취소된 `PlaceStepAsync` 호출은 완료 이력을 기록하지 않는다.

안착·체결 복구창은 `StartPreparation.Open`에서 확인을 시작한 변경 번호를 잡고,
`CanApply`에서 같은 상태인지 검사한다. `ConveyorStation.Changed`는 실제 스테이션 입력 변경이며,
결과를 저장할 때 발생하는 `StationWork.Changed`와 구분한다. 창을 연 뒤 센서가 바뀌거나 I/O가
끊기면 선택을 적용하지 않고, 적용 도중 캐리어가 바뀌어도 확인 완료로 남기지 않는다.
이 경우 현재 캐리어를 보고 복구창을 다시 확인한다. `OperationViewModel.StartAsync`는 두 창을
모두 확인한 뒤에도 각 `Prepared`를 재검사한다. WPF 회귀 검증의 `VerifyRecoveryConfirmations`가
두 창의 캐리어 교체·센서 변화·통신 단절과 다른 창을 보는 동안의 확인 무효화를 검증한다.

검사 상태 조회와 바코드 대상 선택은 결과 객체를 만들지 않는다. 실제 바코드·볼트 검사 전에
결과가 귀속될 캐리어 객체를 선택하고, 응답 후에도 그 객체에 기록한다. 이미 취소된 검사 시작은
기존 결과를 초기화하지 않으며 `InspectionUsesCurrentCarrierSensorsAndRestartsIncompleteWork`로 확인한다.

볼트 체결은 `FastenAsync`에서 헤드를 선택해 `IBoltHead.SelectPresetAsync`와 `TightenAsync`로
바로 이어진다. 정지 후 미회수 결과는 `RunCarrierAsync`의 `ReadPendingResultAsync`에서 먼저
확인한다. `_pendingFastening`의 볼트·캐리어 객체·체결 단계가 결과의 소유자이므로,
재개 시 새로 선택한 볼트에 결과를 기록하지 않는다. 슈팅 이스케이프의 전진·후진은
`SetShootingEscapeForwardAsync`의 `forward`와 양쪽 센서 대기를 확인한다. 두 헤드의 상승·하강은
`SetHeadDownAsync`에서 출력 선택과 `!down` 극성, 양쪽 센서의 완료 대기를 확인한다.
`CompleteAsync`는 마지막 안전 Z 정리가 끝난 뒤에도 취소를 확인하고 캐리어 완료를 기록한다.
`FasteningCompletionCannotCompleteAReplacementCarrier`가 이 순간의 캐리어 교체를 확인한다.

ADC 수동 정회전·역회전은 현재 캐리어의 미회수 체결 결과가 있으면 시작하지 않는다.
별도로 만든 진단용 헤드가 같은 드라이버를 돌려 생산 결과를 덮어쓰지 않도록
`BoltFasteningStation.HasPendingResult`를 버튼과 명령 진입부에서 확인한다.
상태/결과 읽기는 계속 가능하며, 기존 자동 재개로 결과를 회수하거나 Recovery에서 해당 작업을
완료 처리한 뒤 테스트할 수 있다. 캐리어가 교체되어 결과 소유권이 사라진 경우에는 이 조건으로 막지 않는다.

NG 컨베이어의 `MoveCarrierAsync`는 `_movement`에 기록할 목적지 하나로 도착 센서를 정한다.
`RunUntilAsync`가 실제 센서 도착을 기다리고 `finally`에서 모터를 정지한다.
메인·NG 컨베이어의 이송 목적지와 배출 단계는 센서 사이에서 정지한 작업을 구별하는 이력이다.
존재 센서가 없는 상태는 `CarrierPositionUnknown`으로 남기며 이력만으로 이동을 재개하지 않는다.
메인 컨베이어는 중단한 이송 목적지를 새 앞단 입고보다 먼저 처리한다. Station 2→3 재개 시
앞단에 다른 캐리어가 있으면 Station 1 스토퍼·플레이트를 입고 상태로 준비한 뒤 벨트를 구동한다.
Station 3 도착 때는 출발 시 잡은 `StationWork.Job`의 체결 결과를 넘긴다.
도착 신호는 `OnBoltFasteningCarrierChanged`, `OnInspectionCarrierChanged`에서 각각
해당 스테이션의 결과 전달로 바로 이어진다.
작업 추적 번호는 이송 뒤에도 같지만, 도착 스테이션의 완료 주인은 새 객체다.
출발 스테이션에 다음 캐리어가 들어왔어도 그 작업 결과를 지우거나 대신 넘기지 않는다.
`StoppedTransferKeepsResultsWhenAnotherCarrierReachesEntry`가 이 재개 경로와 NG 결과 전달을 확인한다.
`MainConveyor.CompleteSeatingPushAsync`는 전단 반입과 공정 간 이송의 감지 후 밀착을 처리한다.
밀착과 모터 정지를 마치면 `_transfer`를 끝내고 플레이트를 올린다. 밀착 중 STOP은 이송 단계에
남아 밀착을 다시 수행하고, 상승 중 STOP은 기존 착좌 상태 판단으로 재개한다.
별도 밀착 완료 이력은 두지 않는다. 재구동 전 실제 플레이트 하강·스토퍼 상승을 확인하고,
밀착 중 감지 소실은 오류로 종료한다. 로그의 `target=seating push`로 이 구간을 구분한다.
집중 검사는 `RestartAfterArrivalFinishesSeatingPushBeforeRaisingPlate`,
`StopDuringPlateRaiseResumesWithoutAnotherPushOrLoweringSupport`,
`LostCarrierStopsSeatingPushAndRestartPreservesRaisedSupport`다.
셔틀의 `_cycleReturnPending`도 정지했던 상승 동작을 마치기 위한 이력이다.
`NgShuttle.CycleAsync`는 하강 완료 뒤 상승하기 전에도 현재 캐리어 감지와 픽업 상승을 확인한다.
이 조건을 잃으면 상승을 막고, 복구 후에는 완료한 하강을 반복하지 않는다.
`ShuttleCycleRechecksCarrierAndPickupBeforeAscent`가 이 경계와 재개를 검증한다.
NG 픽업의 상승·하강은 정방향·Repeat·수동 모두 `SetLiftUpAsync`에서 같은 피드백 대기를 거친다.

Watch에서 `GetPosition()`, `GetAxisState()` 같은 장치 읽기를 계속 평가하기보다 먼저
현재 프레임의 지역변수와 `State.Display`, `Motion.Axes`의 수집된 값을 확인한다.
수집된 표시값은 마지막 스캔 값이다. 실제 분기 판단은 유닛의 현재 I/O·SDK 읽기에서 확인한다.

오류는 실행 폴더의 `Logs/IBTM-*.log`에도 남는다. `Automatic unit ... failed`로 유닛을 찾고,
뒤의 `Machine alarm` 항목에서 원본 예외·내부 예외·스택을 확인한다.
모션 오류가 `MotionUnavailable`로 분류돼도 앞 항목에 발생 유닛 이름이 남는다.
`failed while stopping`은 정리 중 추가 오류이므로 최초 알람과 함께 확인한다.

## 피드백 감시와 화면 갱신의 경계

DI·DO·모션의 상시 감시는 모두 `MachineFeedbackMonitor.StartAsync`에서 시작하고
`StopAsync`에서 취소·합류한다. 공통 `MonitorAsync`가 **읽기 → 상태 반영·알림 → 다음 주기 대기**,
첫 샘플 완료, 취소와 예외 처리를 맡는다. 장치별 읽기는 아래 메서드에서 직접 확인한다.
특정 장치 읽기가 지연돼도 DI 감시를 막지 않도록 별도 Task로 실행한다.

| 감시 | 루프 중단점 | 실제 읽기 | 주기·즉시 갱신 |
| --- | --- | --- | --- |
| DI | `ReadInputs` | `PhysicalIoService.RefreshInputs` → AlphaMotion/AJIN 전체 스캔 | 10ms |
| DO | `ReadOutputs` | `IoSignals.RefreshOutputs` → `IIoService.GetOutput` | 250ms + `OutputChanged` |
| 모션 | `ReadMotion` | `MotionStatus.RefreshMonitorFeedback` → 축 진단 읽기 | 250ms + `StateChanged` |

`PhysicalIoService`에는 백그라운드 루프가 없다. 초기화와 스캔은 같은 잠금으로 보호하고,
두 공급자의 스캔을 모두 읽은 뒤 입력 캐시를 반영하고 변경 이벤트를 발행한다.
복구 초기화도 `PublishInputScan`에서 마지막 정상 입력과 비교해 바뀐 DI를 알린다.
읽기가 중간에 실패하면 마지막 정상 캐시를 보존하므로 다음 복구에서 변화가 누락되지 않는다.
최초 실행의 입력 상태는 새 버튼 입력으로 알리지 않는다. 복구 중 새로 확인된 캐리어 입고는
`ConveyorStation.CarrierChanged`를 통해 이전 작업 완료·결과를 초기화한다.
Virtual 입력은 시뮬레이터의 상태 변경 시 이미 반영되므로 `RefreshInputs`에서 SDK를 읽지 않는다.
`MachineController.ReadDisplay`는 `IoSignals`와 `MotionStatus`의 수집값으로 계산한다.
버퍼·안착·체결·검사 상태도 같은 판단 메서드에 `live: false`를 전달하며 SDK를 재조회하지 않는다.
수집값이 없으면 읽기 오류로 표시하고 장치 직접 읽기로 대체하지 않는다.
명령에서는 기본값인 `live: true`로 같은 조건을 현재 피드백에 적용한다.
화면 갱신을 멈추거나 창을 닫아도 DI·DO·모션 수집은 계속 동작한다.

출력 명령 이벤트는 다음 읽기를 요청할 뿐, 명령값을 DO 피드백으로 저장하지 않는다.
DO 읽기 실패도 `IoFaulted`를 통해 제어 쪽으로 전달돼 화면 없이 정지·알람 처리한다.
DI 통신 실패는 캐시를 unknown으로 만들고 명시적인 초기화 전까지 SDK 읽기를 중단한다.
DO·모션 진단값은 후속 읽기로 회복할 수 있지만 알람을 자동 해제하거나 운전을 재시작하지 않는다.
예상하지 못한 코드 오류로 루프가 종료되면 `Failure`에 남기고 RESET을 차단한다.
이 경우 로그의 원인을 수정하고 앱을 재시작해야 한다.

비활성 축도 지원되는 원시 진단값은 읽지만 자동운전 정지 판단에서는 제외한다.
별도 진단 API가 없는 장치는 활성 축의 현재 피드백을 읽는다.

한 회차의 읽기가 끝나면 `Sampled(group, sample)`을 먼저 전달한다.
`MachineController.StartAsync` 안의 `StopWhenMotionFeedbackBecomesUnavailable`이 이 샘플로
운전 지속 여부를 판단한다. 여기서는 `group`, `sample.Readiness`, `sample.ReadError`에
중단점을 걸면 된다. 표시용 `MachineDisplay`를 다시 읽어 정지 여부를 결정하지 않는다.
Start 시점보다 먼저 읽기 시작한 샘플은 새 운전에 적용하지 않는다.
Start/Home/수동 명령의 진입 조건은 계속 현재 장치 피드백을 직접 확인한다.

화면은 그 뒤의 `Changed` 알림을 받아 마지막 수집값을 반영한다.
원시 모션·출력 이벤트는 제어에 즉시 전달하지만 화면은 수집 완료 후 갱신한다.
DI는 입력 변경 시에만 화면을 깨우며, 운전·알람 등 소프트웨어 상태 변경은 직접 갱신을 요청한다.
`MachineState.FeedbackReadiness`는 수집된 준비 상태이고, `MotionReadiness`는 현재 장치 읽기다.
`MotionStatus`는 감시 루프의 수명을 소유하지 않는다.
제어 I/O가 끊기면 제어용 축 표시를 즉시 unknown으로 만들고 원시 모션 진단은 계속한다.
전체 종료는 먼저 명령 취소·장치 정리를 기다린 뒤 피드백 수집을 종료한다.
실린더 정리 도중에도 센서 응답을 받아야 하므로 감시를 먼저 끊지 않는다.

카메라 라이브 촬영, 이동/체결 완료 대기, 통신 응답 대기는 특정 작업의 일부다.
항상 실행되는 상태 감시와 달리 해당 작업의 시작·취소·결과 회수 수명을 유지한다.
여러 유닛의 조건에 영향을 주는 피드백은 각 유닛의 `Changed`를 깨울 수 있고,
NG 셔틀·검사 작업처럼 연결된 객체를 통해 같은 변경 알림이 겹쳐 도착하는 경로도 있다.
`AutoUnit`의 `AsyncAutoResetEvent`는 대기 중인 알림을 합치고 동작을 순서대로 실행한다.
이벤트 구독 자체에 SDK 폴링 루프가 추가되는 것은 아니다.

이 경계의 집중 검사는 `HomeAndAutomaticStartIgnorePreStartSampleAndStoppedDisplay`와
`AutomaticFeedbackStopsOnSilentMotionFaultAfterDisplayStops`,
`InputAndOutputMonitorsOutliveDisplayAndWaitForOperationCleanup`,
`ReadyOutputReadFailureStillFailsClosed`다.
`DisplayUsesAcquiredFeedbackForAllStationsAndNeverFallsBackToHardware`는 각 스테이션이
활성인 화면에서 SDK 재조회가 없고, 명령의 직접 읽기와 수집 실패 표시가 유지되는지 확인한다.
실제 DI 스캔은 SDK 대역의
`PhysicalInputScanPublishesBothProvidersAndRequiresExplicitRecovery`에서 확인한다.
`RecoveredCarrierArrivalDoesNotReuseCompletedStationWork`는 통신 단절 전 비어 있던
스테이션에 복구 시 캐리어가 확인되면 이전 완료 상태로 배출하지 않는지 확인한다.

## 짧게 검증하기

UI 텍스트/배치만 바꿨으면 테스트를 늘리지 않는다. 컴파일 확인이 필요하면:

```powershell
dotnet build IBTM/IBTM.csproj -c Virtual --no-restore
```

동작을 바꿨으면 **관련 테스트 하나를 선택**한다. 아래 명령을 전부 실행할 필요는 없다.

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~InspectionReceivingIgnoresPickupHeightButWaitsForHeldCarrier"
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~ForwardTransferWaitsForArrivalThenPushesAgainstStopperForConfiguredDelay"
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~FovCaptureUsesCurrentPositionWithoutMoving"
```

WPF 화면 바인딩/명령 수명 회귀는
`OutputWindowThreadingTests.BoundConveyorButtonsKeepDisplayAliveAcrossOffCloseAndReopen`에
기존 티칭·FOV 검사도 묶여 있다. WPF Application을 여러 개 만드는 새 테스트 호스트를 추가하지 않는다.
AJIN/AlphaMotion 래퍼는 각 SDK 대역 테스트 프로젝트에서 해당 테스트만 선택한다.
처음 체크아웃해서 assets가 없으면 먼저 `dotnet restore IBTM.slnx`를 실행한다.

## 바꾸지 말아야 할 경계

- View를 닫는 것과 설비 Stop은 별개다. 창 닫기에 임의의 DO 조작을 추가하지 않는다.
- IO·모션 읽기는 장치 루프가 담당한다. 화면 갱신용 타이머를 추가하지 않는다.
- BitmapSource를 다른 스레드로 넘길 때는 Freeze된 이미지를 사용한다.
- Missing feedback은 unknown이다. false/0/완료로 바꾸지 않는다.
- 이동 속도·가감속 시간·원점 검색 수치는 `MotionService` 진입부에서 확인한다.
  잘못된 XY 명령 때문에 Z 준비 이동이 먼저 나가지 않도록 준비 동작 전에 확인하며, 조그 방향의 부호는 유지한다.
- Stop은 취소 요청만이 아니라 진행 중 명령의 정리 완료까지 고려한다.
- IO 기본값을 코드에서 바꿔도 이미 저장된 DB 값이 자동으로 바뀌지는 않는다.
- 실장비 데이터와 설정은 이번 코드 정리에서 변경하지 않았다. 백업 후 현장에서 확인한다.

## 현장에 남은 임시 전제

Repeat는 캐리어 하나 기준이다. 메인 앞단 센서 설치 전에는 역방향 반환을 Station 1 센서에서
멈추고 다음 전진은 Station 2부터 처리한다. 전용 앞단 센서가 설치되면
`MainConveyor.ReturnToStartAsync`와 `MachineController.Repeat.cs`의 TEMP 경로를 같이 확인한다.

NG Transfer까지만 켠 Repeat와 실제 셔틀에 놓는 동작은 다르다.
전자는 셔틀 위치에서 내려도 그리퍼를 풀지 않고 돌아온다.
전체 순서와 실장비 확인 항목은 [Station 3 안내](STATION3_COMMISSIONING.md)에 있다.
