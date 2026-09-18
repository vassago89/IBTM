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
| 장치 초기화 / Reset 허용 조건·복구 순서 | `IBTM/MachineController.Reset.cs` |
| 전체·유닛·축 Home / 실린더 상승 / Home 차단 이유 | `IBTM/MachineController.Home.cs` |
| 수동 축 이동 / 티칭 저장 / 서보·ADC·볼트 테스트 | `IBTM/MachineController.Manual.cs` |
| Repeat 왕복 경로와 마지막 유닛 | `IBTM/MachineController.Repeat.cs` |
| 수동 DO 조작 | `IBTM/MachineController.Outputs.cs`, `IBTM/UI/OutputWindowRow.cs` |
| 화면 표시 상태 | `IBTM/MachineController.Display.cs`, `IBTM/UI/OperationViewModel.Display.cs` |
| 메인 창 명령·레시피 파일 선택·종료 대기 | `IBTM/UI/MainViewModel.cs` |
| 진단 창 생성·재활성화·Owner 관리 | `IBTM/UI/DiagnosticWindows.cs` |
| ADC 진단 명령·입력값·취소·정지 확인 | `IBTM/UI/AdcProtocolViewModel.cs` |
| 입력·출력 목록과 새로고침 | `IBTM/UI/InputWindowViewModel.cs`, `OutputWindowViewModel.cs` |
| 모션 진단 구독·축 명령·창 종료 대기 | `IBTM/UI/MotionWindowViewModel.cs` |
| 로그 표시·복사·일시정지 | `IBTM/UI/LogWindowViewModel.cs`, `LogTextBox.cs` |
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

## 화면과 ViewModel 경계

모든 화면의 명령과 편집값은 XAML에서 해당 ViewModel에 바인딩한다. ADC 진단도 창 객체가 아니라
`AdcProtocolViewModel`이 포트·슬레이브·레지스터·프리셋 입력과 통신 작업을 소유한다.
명령의 장치 진입은 기존 `MachineController.RunAdcProtocolAsync`와 `RunBoltTestAsync`를 거친다.
ADC는 별도 busy 플래그 없이 현재 작업의 취소 소스로 실행 중 여부를 판단한다.
STOP과 창 닫기는 현재 작업을 취소하고 완료를 기다리며, 모터 정지는 실제 RUN OFF로 확인한다.
화면의 실행 가능 조건은 `CanExecute`·`IsEnabled`에서 처리하고, 명령 본문에서 같은 조건을 반복 검사하지 않는다.
장치의 실행권·현재 피드백·인터록과 비동기 대기 후 상태 변화 확인, 입력값 검증은 유지한다.
모션 진단의 상태 구독은 `Activate`/`Deactivate`, 종료 시 작업 취소·대기는 `ShutdownAsync`에서 찾는다.

`.xaml.cs`에는 `InitializeComponent`, 루트 DataContext 연결, WPF Closing/Closed 이벤트와
종료 오류 대화상자 표시만 둔다. 비동기 종료 허용 여부와 오류 정보는 ViewModel의 `TryCloseAsync`에서 결정한다.
메인의 진단 창 열기 명령은 `DiagnosticWindows`로 이어지며, 이 클래스는 창 생성·재활성화·Owner를 관리한다.
운전 화면의 RESUME WORK와 완료 항목을 지정하는 복구창은 제거했다.

텍스트 선택과 마우스 캡처, 컨트롤 템플릿 동작은 WPF 컨트롤 책임이다.
`LogTextBox`는 선택 문자열·일시정지 값을 바인딩으로 전달하고, `ComboBoxDropDownButton`은 드롭다운만 연다.
`App.xaml.cs`는 DI 구성·앱 시작과 종료를 담당하는 진입점으로 유지한다.

## 막힌 동작을 따라가는 순서

티칭 메뉴는 `Teaching` 하나다. 유닛 목록에서 Supply, Placement, Fastening,
Inspection, NG Transfer를 선택한다. 인계 위치·공통 이동 높이·경계는 각각 Supply와 Placement의
티칭 목록에 포함되며, 축 피드백과 I/O는 선택 유닛을 따른다. 인계값은 Teach로 임시 보관하고
두 유닛에서 보이는 `Apply & Save Handoff`로 양쪽 값을 묶어서 적용·저장한다. 유닛을 전환해도 임시값은 유지되며,
Teaching 메뉴를 나갔다 다시 열면 저장·적용된 설정에서 다시 읽는다.

각 유닛 목록은 작업 위치, 설비 기준값, 계산 위치, 인계 간섭 영역으로 구분한다.
Supply의 `PCB Give Position`은 XY만 티칭하며 `Transport / Rotation Z`에서 그대로 전달한다.
Placement는 `PCB Receive Position` → Heat Sink 1/2 안착 순서다.
체결의 `Safe Z (Travel)`은 공통 이동 높이다. `Shooting Head Fastening Z`는 PCB 체결 높이,
`Pickup Head Fastening Z`는 IPM 안착·최종 체결 높이다.
자동 동작은 양쪽 헤드 상승 → Safe Z에서 XY 이동 → 선택 헤드의 체결 Z 이동 → 체결 START → 즉시 해당 헤드 하강 순서다.
체결기는 회전을, 실린더는 볼트 전진을 담당한다. START 전송 성공 후 하강하며, 하강 중 오류·정지 시 체결기도 정지한다.
`Bolt Pickup`의 Z는 별도 픽업 높이로 유지한다. 체결의 B1/B2는 헤드별 계산 XY 위치이며
Move To는 Safe Z에서 위치만 확인한다. 각 Z 티칭값은 설비 설정에 독립적으로 자동 저장된다.
기존 공통 체결 Z는 두 헤드 체결 Z의 초기값으로 옮기며, 이후에는 서로 영향을 주지 않는다.
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
- `MainConveyor.PrepareEmptyStationsAsync`: 중단된 이송이 있으면 START 준비를 거부한다. 정상 시작에서는 빈 스테이션만 내린다.
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
장치 초기화·Reset은 `MachineController.Reset.cs`, 수동 작업 수명은
`MachineController.Manual.cs`에서 따라간다. 모두 같은 `MachineController`의 partial 파일이며
의존성과 공통 Stop·안전 인터록은 `MachineController.cs`가 소유한다.
START/HOME/실린더 상승의 실행 전 조건 읽기도 명령의 오류 처리 범위에 포함한다.
장치 읽기 실패는 동작을 시작하지 않고 알람·원인으로 남긴다. HOME/상승 중 상태 변경 통지에서
조건을 다시 읽다 실패하면 해당 작업을 취소한다. 조건 getter의 읽기 오류를 false나 캐시값으로 숨기지 않는다.
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
| 메인 컨베이어가 이송하지 않거나 센서 사이에서 멈춤 | `MainConveyor.ExecuteAsync`, `ReadState` | `state`, `RequiresManualClear`, 현재 도착·착좌 센서; 중간 정지는 수동으로 비운 뒤 RESET |
| PCB 공급이 대기하거나 예상과 다른 동작 | `PcbSupplier.RunAsync` 안 `ExecuteAsync`의 `switch (state)`, `PickPcbAsync` | `state`, `_pickStep`; 픽업 중에는 `pickPosition`, `carrierChanged` |
| PCB 안착이 멈춤 | `PcbPlacer.ExecuteAsync`, `PlaceStepAsync`의 `switch (state)` | `heatSink`, `state`, `action`; `action == null`이면 피드백 대기 |
| 공급 진입 또는 안착 인수 실린더가 대기함 | `BufferStage.CanEnterSupply`, `CanEnterPlacement`, `HasConflict` | 도착 순서는 무관; Placement Handler Up/Down 입력, 양쪽 현재 위치·Home·정지 피드백, Supply `PcbSecured`, 인계 좌표 |
| 인수 후 실린더 상승 또는 Supply 복귀가 대기함 | `BufferStage.CanRaisePlacement`, `CanExitSupply`, `PcbSupplier.State` | Supply `PcbReleased`는 그리퍼·IPM 고정 실린더 모두 후퇴 확인; Placement 상승 확인 후 `MovingFromHandoff`에서 다음 PCB 픽업 XY로 복귀 |
| 픽업 또는 슈팅 볼트 피더가 대기/타임아웃 | 두 피더가 공유하는 `BoltFeeder.ExecuteAsync` | `waitingForBolt`, `_boltDetected`, `TimeoutMilliseconds`; 슈팅 출력은 `ShootingBoltFeeder.SetFeeding` |
| 볼트 체결이 멈춤 | `BoltFasteningStation.RunCarrierAsync`, `ExecuteAsync`, `FastenAsync` | `state`, `head`, `pass`, `_pendingFastening`의 볼트·캐리어·패스 |
| Station 3 검사/NG 이송이 대기 | `InspectionStation.ExecuteAsync`, `ExecuteInspectionAsync` | `transferState`, `inspectionState`, `bolt`; `transfer == null`이면 이송 명령 없이 피드백을 기다림 |
| NG 이송의 정방향·복귀 순서가 예상과 다름 | `NgCarrierMove.State`, `ExecuteAsync`, `MoveToCarrierAsync` | `destination`, `state`, 현재 픽업 상승·그립·캐리어 감지, Safe X |
| NG 셔틀이 대기하거나 Repeat 상승하지 않음 | `NgShuttle.ExecuteAsync`, `CycleAsync` | `state`, 실제 Up/Down·캐리어·픽업 상승 피드백 |
| NG 컨베이어 적재·배출이 막힘 | `NgCarrierConveyor.ExecuteAsync`, `MoveCarrierAsync`, `ReadState` | `state`, `destination` 입력, `_movement`, `_ejectionPhase`, 현재 위치 센서 |
| 실린더 타임아웃 | `IIoService.SetOutputAndWaitAsync`, `WaitForInputAsync` | 출력 `output`/`value`, 기다리는 입력 `input`/`value`, 제한시간 |

예를 들어 공급의 `switch (state)`에 조건부 중단점
`state == PcbSupplyState.WaitingForHandoff`를 걸면 해당 대기로 들어가는 판단을 볼 수 있다.
이벤트 대기 중에는 새 피드백이 와야 다음 판단으로 들어간다. `state`, `transferState`,
`inspectionState`는 그 회차에 선택한 분기이며, 장비 위치를 저장하는 별도 상태가 아니다.

PCB 공급의 그립·해제 출력 순서는 `ExecuteAsync`의 해당 `case`에서 바로 확인한다.
`_pickStep`은 전단 캐리어에서 확인한 PCB 슬롯 이력이며 Stop·재시작 사이에도 유지한다.
`OnHandlerChanged`가 정지 중에도 전단 캐리어 이탈을 받아 다음 캐리어의 PCB1을 선택한다.
현재 위치나 PCB 센서값으로 이 슬롯 이력을 대신하지 않는다.
Front 1 Ready는 캐리어 도착 후에도 유지하고, PCB2까지 확인/확보하여 운반 높이로 복귀한 뒤 OFF한다.
Available OFF가 들어오면 다음 캐리어의 Ready를 ON한다. `PcbSupplyHandler.StopUpstream`은
현재 Available이 ON이면 Ready를 그대로 두며, OFF일 때만 대기 중 Ready를 끈다.
입력 읽기 실패도 캐리어 없음으로 취급하지 않는다. 전체 STOP과 공급 루프 종료가 같은 메서드를 사용한다.
시운전 입력은 `PcbSupplyHandler.TestUpstreamCarrierAvailable`,
`MainConveyor.TestUpstreamCarrierAvailable`, `MainConveyor.TestDownstreamReady`의 메모리 bool이다.
티칭에서는 메모리 값만, 자동에서는 실제 SMEMA DI만 판단하며 기존 `Changed`로 변경을 통지한다.
Operation 화면의 SMEMA 카드에 직접 바인딩하며 저장·새 I/O 주소·별도 감시 루프는 없다.
테스트 값 설정과 실제 사용 모두 현재 티칭 접점 `InputIo.AutoMode=ON`을 확인한다.
UI는 같은 입력의 관측값으로 활성화한다. 티칭 OFF 입력은 세 값을 지워 재진입 시 복원하지 않는다.
STOP은 값을 유지하고 프로그램을 다시 실행하면 OFF다. Front 1은 선택된 Available 소스가
OFF→ON되어야 다음 캐리어로 처리한다. 선택기 피드백 오류나 일반 I/O 통신 오류는 여전히 오류다.
시퀀스의 SMEMA 출력은 `IIoService.SetAutomaticSmemaOutput`을 사용하며 티칭에서는 쓰지 않는다.
초기화·티칭 진입 시 기존 STOP 경로로 외부 SMEMA를 OFF한다. OUTPUTS 창은 원래 `SetOutput`을
직접 사용하므로 티칭에서도 수동 ON/OFF가 가능하다. 이 창의 기존 조작 조건에 새 제한을 넣지 않았다.
`SupplySlotProgressDoesNotSurviveTheRun`이 STOP 후 슬롯 진행을 유지하지 않는지 확인한다.
`PickPcbAsync`는 픽업 중 전단 캐리어 이탈을 받으면 그 픽업을 취소한다. 늦게 끝난 이전 픽업은
새 캐리어의 슬롯 이력을 넘기지 않으며 `SupplyDoesNotAdvanceTheNewCarrierWhenAnOldPickupFinishes`로 확인한다.
수동 한 축 이동은 `TeachingViewModel.StepAsync` → `PcbSupplyHandler.MoveAxisAsync` →
`MotionService.MoveAxisAsync` 순서다. 공급 핸들러에서 인계 구역 내부 Z 이동을 막고,
모션 계층에서 축 속도·범위·안전 Z·취소를 처리한다. 자동/수동 인계 진입은
`MoveToHandoffAsync`에서 Rotation Z 확보 → XY 동시 이동 순서로 진행한다. 픽업과 티칭도
같은 `IXyMotion.MoveToXYAsync`를 사용하며, 픽업은 XY 도착 후 해당 PCB Z로 내려간다.
인계 구역 안에서도 Rotation Z에서 X/Y 조작을 허용하며 Placement 간섭 조건은 유지한다. 별도 인계 하강은 없으며,
해제 후에는 두 Supply 실린더의 후퇴 완료 → Placement Handler Up 확인 → `MoveFromHandoffAsync`의
XY 동시 복귀 순서다. Rotation Z를 유지하며, PCB1 후에는 PCB2 X와 Carrier Y, PCB2 후에는
다음 캐리어의 PCB1 X와 Carrier Y로 돌아간다. 별도 Clear Z나 복귀 좌표는 없다.
Placement는 실린더만 올리고 XYZ를 유지하다가 Supply가 영역 밖으로 나간 뒤 축을 이동한다.
공유 영역에서 Supply 수평 이동 중 Placement 상승 확인을 잃으면 기존 `HasConflict` 감시가 정지·알람을 처리한다.

PCB 안착의 XY 이동은 `PcbPlacementHandler.MoveToXYAsync`, Z 이동은 `MoveAxisAsync`에서
장치 호출로 이어진다. 같은 Down 센서값으로 압입 전후를 구별할 수 없으므로
`_pressingHeatSink`는 현재 실행 안에서만 압입 대상을 보관한다. `RunAsync` 종료 시 압입 대상,
Repeat PCB 왕복 단계와 실행 대상을 버린다. 현재 캐리어·IPM·그리퍼 피드백 확인은 유지한다.

장비 자동 운전이 종료되면 `MachineController.RequiresManualClear`가 전체 START를 차단한다.
PCB 공급·안착·체결·검사·NG 이송·셔틀 및 Repeat에 같은 정책을 적용하며,
Main Conveyor나 해당 유닛을 비활성화해도 차단을 우회하지 못한다.
캐리어와 핸들러의 보유 부품, 센서 사이의 소재까지 수동 제거하고 RESET한다.
RESET은 현재 재실·보유 부품과 컨베이어 정지를 확인하고 중단 작업을 폐기한다.
센서 OFF만으로 차단을 해제하지 않으며, RESET은 자동 운전을 시작하거나 작업을 완료 처리하지 않는다.
복구창, `StartPreparation`, `PrepareRecovery`, 수동 완료 결과 생성은 제거했다.

검사 결과는 해당 캐리어 객체에만 기록한다. STOP 후 새 START에서 미완료 검사를 초기화해
자동 재검사하던 경로는 없다. 새 캐리어 입력이 새 작업을 만든다.
체결의 `_pendingFastening`은 결과가 귀속될 원래 캐리어·볼트·패스만 보관한다.
`ReadPendingResultAsync`는 모터 재기동 없이 확인된 결과만 회수한다.
ADC와 I/O 모두 미완료 체결에 다시 START하지 않는다. 실린더 하강 미확인 결과도 OK로 기록하지 않는다.
수동 정리·RESET까지 미수거 결과의 귀속을 유지하며, 확인된 결과를 다른 캐리어로 옮기지 않는다.
수동 체결 테스트도 미완료 작업이 있으면 차단한다. 통신 상태·결과 읽기는 가능하다.

NG 컨베이어의 목적지와 배출 버튼 확인 단계는 현재 실행에만 속한다.
이송·배출 중 중단되면 `ManualClearRequired`로 차단하고 실행 단계를 버린다.
정상 이송의 도착 센서 확인, 셔틀 지지, 배출 확인 버튼 및 모터 OFF 정리는 유지한다.
메인 컨베이어는 중단 단계의 자동 재개를 하지 않는다. `ConveyorTransfer`와 장기 보관 도착 이력은 없다.
정상 이송은 출발 시 잡은 `StationWork.Job`을 `MoveCarrierAsync`의 도착 이벤트에서 전달하고,
이송 종료 시 이벤트를 해제한다. 중단 뒤 들어온 신호로 이전 작업 결과를 다른 캐리어에 붙이지 않는다.
작업 추적 번호는 유지하며, 도착 스테이션의 완료 주인은 새 객체다. 출발지의 다음 작업과 섞지 않는다.
`RunToStationAsync`는 현재 구동의 입구·Heat Sink 2 감지와 추가 밀착 시간을 처리한다.
모터 정지 후 플레이트를 올리는 것까지 한 실행에 포함하며, 중간 취소/오류는 `RequiresManualClear`를 남긴다.
이는 물리 위치나 재개 단계가 아니라 작업자 확인이 필요한 미완료 기록이다.
START는 차단하고 기존 지지 출력은 유지한다. 센서 사이까지 수동으로 비운 뒤 RESET에서
`ConfirmManualClear`를 호출한다. 정지 출력과 모든 재실 입력·Station 3 인수 가능 상태를 확인하며,
센서 OFF만으로는 중단 기록을 해제하지 않는다. 미전달 작업 참조는 이 명시적인 제거 확인 때 해제한다.
메인 컨베이어 자체의 대기 STOP은 이송 중단 기록을 만들지 않지만, 장비 자동 운전 종료는 전체 정리 확인을 요구한다.
정상 운전에서는 벨트가 정지한 상태에서 감지된 캐리어를 현재 스테이션에서 올린다.
여러 스테이션에 캐리어가 있으면 동시에 착좌를 시작하고, 각 작업 유닛은 자기 스테이션의
상승·스토퍼 하강 피드백이 확인되는 즉시 작업한다. 완료된 캐리어의 이송은
S3 배출 → S2에서 S3 → S1에서 S2 → 신규 반입 순서이며, 목적지가 비어 있어야 한다.
정상 착좌가 중단되어도 수동 정리·RESET 차단이 걸리며, 중단된 동작을 자동 재개하지 않는다.
집중 검사는 `InterruptedSeatingDoesNotResumeAfterSensorChanges`, `InterruptedPlateRaiseDoesNotResumeOrLowerSupport`,
`InterruptedTransferKeepsPendingResultsWithoutMovingThemOnLaterInput`, `ActiveTransferKeepsOriginalResultsWhenSourceGetsAnotherCarrier`,
`ResetAcknowledgesInterruptedConveyorOnlyAfterManualClear`다.
셔틀의 `_cycleReturnPending`은 제거했다. `CycleAsync`는 하강 완료 후 현재 캐리어와
픽업 상승을 확인하고 상승한다. 중단된 상승을 별도로 기억해 이어가지 않는다.
전체 Repeat도 저장 단계 분기 없이 정방향 → NG 반환/셔틀 왕복 → Station 3 → 입구 순서로 실행한다.
`_repeatDisplayPhase`는 표시와 오류 위치 설명에만 쓰며, 운전 종료 시 초기 표시로 돌아간다.
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

앱 종료는 명령 취소와 모터 OFF, 진행 중인 작업의 정리를 기다린 다음 모든 구성 축의
현재 `InMotion`을 한 번 더 읽는다. 비활성 유닛도 포함하며, 이동 중이거나 읽기가 실패하면
정지 미확정으로 기존 종료 실패 처리에 전달한다. 이 확인까지 끝난 뒤 수집 루프를 종료한다.
작업 수가 0이라는 사실만으로 실제 축의 정지를 판단하지 않는다.

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

## Repeat 운전 범위

Repeat는 PCB가 이미 안착된 캐리어 하나를 메인 입구(첫 번째) 센서에 놓고 시작한다.
첫 Station 1 도착부터 기존 PCB를 집어 왕복한다. 역방향은 메인 입구 전용 센서까지 복귀하고,
전진은 Station 1의 HS2 감지 후 설정된 추가 이송 시간과 캐리어 상승을 그대로 거친다.
Placement는 기존 PCB를 집어 기존 인계 좌표까지 왕복한 뒤 원래 자리에 재안착·압착한다.
Supply에서 새 PCB를 받지 않으며, Placement가 켜져 있으면 왕복 완료 후 다음 공정으로 보낸다.
일반 운전과 Repeat 모두 Pickup Feeder OFF에서도 피더 XY 이동·실린더 하강·픽업 Z 이동·진공 ON·Safe Z 복귀·실린더 상승을 수행한다.
피더의 볼트 감지와 픽업 진공 ON 확인만 생략한다. 축 위치와 실린더 피드백, 진공 해제 확인은 유지한다.
집힘 확인을 생략한 픽업 실행 이력은 현재 캐리어와 볼트에만 적용하며, 실제 볼트 보유 상태로 표시하지 않는다.
Shooting Bolt Feeder OFF는 공급 대기·이스케이프·볼트 발사와 공급 관련 감지 대기를 생략한다.
피더 ON/OFF와 관계없이 모든 볼트의 XY·헤드별 체결 Z로 이동하고, 프리셋 선택 → START → 실린더 하강 → 체결 결과 수거를 수행한다.
IO형은 기존 FASTEN ON → OFF를 확인한 뒤 START를 끄고 IO · Assumed OK로 기록한다. 통신형은 체결기의 실제 OK/NG 결과를 기록한다.
픽업 헤드의 가체결·본체결 위치도 모두 방문한다. 체결기 준비 확인과 검사도 유지한다.
미수거 결과와 중단된 체결의 복구 조건, XY 이동에 필요한 양쪽 헤드 상승과 Safe Z도 유지한다.
통신형도 START 후 실린더 하강이 확인되지 않으면 늦게 온 결과를 자동 반영하지 않으며 수동 정리·RESET을 요구한다.
하강이 확인된 뒤 결과 수신만 실패한 경우에는 기존 결과를 수거하며 다시 START하지 않는다.
관련 코드는 `PcbPlacer.Repeat.cs`, `MainConveyor.ReturnToStartAsync`, `MachineController.Repeat.cs`다.

NG Transfer까지만 켠 Repeat와 실제 셔틀에 놓는 동작은 다르다.
전자는 셔틀 위치에서 내려도 그리퍼를 풀지 않고 돌아온다.
전체 순서와 실장비 확인 항목은 [Station 3 안내](STATION3_COMMISSIONING.md)에 있다.
