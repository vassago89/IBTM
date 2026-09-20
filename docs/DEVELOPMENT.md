# 직접 개발할 때 보는 안내

기준: 2026-09-19 소스. 목적은 **수정 → 현장 확인 → 원인 확인** 사이클을 짧게 하는 것이다.
새 계층보다 실제 호출과 조건이 한눈에 보이는 코드를 우선한다. 상세 작업 규칙은 루트 `AGENTS.md`.

## 코드 표기와 디버깅 진입점

기존 `C:\git\MWD100`의 일반 생성자와 명시적인 순차 호출을 참고하고, 오늘 정한 C# 표기 규칙은 `AGENTS.md`를 따른다.
제어·드라이버·공통 장치·저장소·UI·테스트에 같은 표기를 적용한다. 제조사 SDK와 자동 생성 코드는 제외한다.
클래스는 필드 → 생성자 → 이벤트·프로퍼티 → 조회·실행 함수 → 내부 보조 타입 순서로 배치한다.
필드와 자동 프로퍼티의 초기화 순서는 실행 결과에 영향을 줄 수 있으므로 선언을 옮길 때 보존한다.
일반 클래스의 생성자는 명시하고, 값만 묶는 `record`는 간결한 선언을 유지한다.
프로퍼티를 함수 사이에 끼워 넣지 않고, 단계 실행을 큰 함수 안의 지역 함수로 숨기지 않는다.
취소·피드백 구독처럼 현재 호출의 변수를 함께 써야 하는 짧은 지역 함수는 해당 호출 안에 둔다.
중첩 삼항식으로 여러 결과를 반환하는 부분은 `if`와 `return`으로 풀어 분기별 중단점을 잡을 수 있게 한다.
상태·동작·테스트 시나리오는 enum과 실제 객체로 구분한다. 표시 문구나 예외 메시지를 비교해 판단하지 않는다.
I/O 이름을 조합해 파싱하거나 프로퍼티 경로 문자열을 만들어 대상을 찾지 않는다. 선언된 신호와 멤버를 직접 참조한다.
설정 가능한 운전값은 해당 설정에서 읽는다. 문자열은 표시·로그·사용자 입력·기존 저장 형식·통신 형식의 경계에서 사용한다.
비동기 작업 호출은 `await`로 이어간다. 동기 SDK 호출로 인한 백그라운드 실행은 작업을 제공하는 쪽에서 맡는다.
START·HOME·실린더 상승·RESET은 `MachineController`가 동기 SDK 조회와 시작을 맡고, 화면 명령은 `await`한다.
이미 비동기인 작업을 화면에서 다시 `Task.Run`으로 감싸거나 `Wait`·`Result`·`GetAwaiter().GetResult()`로 기다리지 않는다.
`Task.Run`은 동기 SDK·DB·이미지 계산과 장치 감시 작업에 한정한다. 취소 후에도 장치 정리 완료를 기다린다.
카메라 촬영은 `ICamera.CaptureAsync`로 연결하며 라이브 프레임 이벤트는 취소 가능한 비동기 대기로 받는다.
앱 종료는 명령 취소·장치 정리 뒤 DI의 `DisposeAsync`와 로거 팩터리의 파일 기록 완료까지 기다리고 창을 닫는다.
실행 중인 피드백·화면 감시의 소유자는 `await using` 또는 `DisposeAsync`를 사용한다.

조명은 `C:\git\AnyWave\AnyWave.Device\Lights\MOVSService.cs` 원본을
`Shared/IBTM.Device/MOVSService.cs`에 그대로 포함한다. 컴파일에 필요한 using과 nullable 지시문만 덧붙였다.
`MovsLightController`는 기존 `ILightController` 호출을 원본의 Connect/Set/On/Off/Disconnect에 연결한다.
촬영·Live 점등 직전에 원본과 같이 Connect → Set → On 순서로 호출한다.
검사 유닛 Disabled로 시작해 초기화에서 조명 연결을 생략했어도 수동 티칭 점등 시 연결한다.
19200 통신, 문자 버퍼, 전송 뒤 50ms 대기, 빈 COM/미연결 처리도 원본을 따른다.
추가했던 시리얼 세부 설정·쓰기 잠금·드라이버 예외 재포장은 제거했다. 설정에는 COM과 검사 채널만 남긴다.
원본의 Find도 보존하지만, 장비 초기화나 검증에서 자동 포트 검색을 호출하지 않는다.

| 형태 | 의미 | 예 |
| --- | --- | --- |
| 명사 프로퍼티 | 현재 값·상태 노출 | `CarrierPresent`, `BackupPlate`, `CurrentJob`, `State` |
| `Get...()` | 인자와 현재 상태로 값·작업 단계 조회 | `GetState()`, `GetActiveBolt()`, `GetAssembly()` |
| `Is...()` / `Has...()` | 인자를 받는 조건 확인 | `IsHeatSinkPresent(slot)`, `IsSupportReady(destination)` |
| `Create...()` | 객체 생성 | `ConveyorStation.CreateInspection(io)` |
| 동사 + `Async()` | 장치 동작 또는 비동기 대기 | `RunAsync()`, `MoveToCarrierAsync()`, `WaitForChangeAsync()` |

메인 컨베이어는 `MainConveyor.Sequence.cs`의 `GetState()`에서 상태를 선택하고,
`RunAsync()` 루프에서 직접 실행한다. 반입·S1→S2·S2→S3는 `MainConveyor.Transfer.cs`의 `TransferAsync()`에서 따라간다.
체결·검사·안착도 `GetState()`로 판단을 확인하고 `RunAsync()`와 `ExecuteAsync()`에서 실행을 따라간다.
PCB 공급의 `ExecuteAsync(recipe, cancellationToken)`도 독립 메서드로 두어 F12와 함수 중단점으로 찾을 수 있다.

## 스테이션 상태의 범위

상태는 한 번 선택한 뒤 끝까지 `await`하는 작업 단위다. 실린더 하나, 축 하나,
결과 기록 한 번마다 루프로 돌아가지 않는다. 작업 내부의 순서는 각 `switch` 분기에 직접 쓴다.
다음 상태는 그 작업이 끝난 뒤 현재 피드백으로 판단한다.

| 유닛 | 상태 수 변경 | 묶은 동작 |
| --- | --- | --- |
| PCB Supply | 13 → 8 | 픽업 복귀·회전, PCB 고정·회전·인계 이동 |
| PCB Placement | 21 → 9 | 인계 준비, 수취, 안착·압착·기록·복귀, Repeat 픽업 |
| Bolt Fastening | 23 → 7 | 볼트 하나의 공급·이동·체결·복귀 |
| Inspection | 21 → 11 | 포인트 이동·촬영, 검사 완료 복귀, NG 표시 상태 통합 |
| NG Transfer | 16 → 13 | 접근·하강·집기·상승, 목적지 이동·하강, 닫기·집힘 확인 |
| Main Conveyor | 18 → 16 | S1/S2 동시 착좌, S3 상승 중복 제거 |
| NG Conveyor | 14 → 13 | 배출 확인 입력 처리를 확인 대기에 포함 |

다른 유닛·설비·작업자를 기다리는 경계는 남긴다. 메인 컨베이어의 이송 상태는
출발지 하강부터 도착까지 이미 한 동작이므로 목적지와 우선순위를 합치지 않는다.
NG 셔틀의 6개 상태는 픽업 상승·컨베이어 종료·위치 불명 등 실제 다른 대기 조건이다.
볼트 피더의 2개 상태와 실린더의 Up/Down/Between 같은 물리 피드백은 유지한다.

작업 결과 소유자(`CurrentJob`, 미수집 체결 결과), 반복 운전의 현재 목적지,
압착 전후처럼 센서만으로 구분되지 않는 실행 이력도 유지한다. 연속 동작 중 정지·캐리어
교체·착좌 이탈을 확인하며, 완료 피드백 없이 다음 명령이나 완료 기록으로 넘어가지 않는다.
`PlaceAsync`와 NG `ExecuteAsync`의 `false`는 외부 조건 대기를 뜻한다.

## 참조 방향과 상태의 소유자

- `IBTM`은 화면·운전 시작/정지·DI 구성을 맡고, 각 Station은 자기 작업 순서를 맡는다.
  유닛 사용 여부는 `Shared/IBTM.Device/UnitSettings.cs`의 같은 설정 객체를 주입해 직접 읽는다.
  설정 판단을 DI의 `Func<bool>`로 나누거나 각 유닛에 복사하지 않는다.
- 현재 레시피의 로드·저장은 `RecipeManager`가 맡는다. 소비자는 주입받은 관리자의 `Current`를 읽는다.
- `MainConveyor`는 이송 우선순위를 결정하고, 하나의 이송 메서드가 출발지 하강부터 목적지 도착까지 맡는다.
  NG 배출 여부도 컨베이어가 검사 결과와 유닛 설정으로 판단한다. DI에는 객체 연결만 둔다.
- `StationWork`는 현재 캐리어의 작업·결과 소유권을 관리한다. 위치와 착좌 여부는 `ConveyorStation`의 현재 I/O로 판단한다.
  사용 설정, 실행 중 명령, 결과 소유권을 물리 위치나 완료 피드백으로 대신하지 않는다.
- 장비의 동기 SDK 조회·정지는 장비 진입부에서 UI 스레드와 분리한다. 화면에서는 비동기 명령을 그대로 `await`한다.
  화면 값은 기존 observable 객체에 직접 바인딩하고, 표시를 위한 복사 속성과 알림 중계를 만들지 않는다.

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
| 화면 표시 상태 | `IBTM/MachineState.cs`, `IBTM/UI/OperationViewModel.Display.cs` |
| 메인 창 명령·레시피 파일 선택·종료 대기 | `IBTM/UI/MainViewModel.cs` |
| 진단 창 생성·재활성화·Owner 관리 | `IBTM/UI/DiagnosticWindows.cs` |
| ADC 진단 명령·입력값·취소·정지 확인 | `IBTM/UI/AdcProtocolViewModel.cs` |
| 입력·출력 목록과 새로고침 | `IBTM/UI/InputWindowViewModel.cs`, `OutputWindowViewModel.cs` |
| 모션 진단 구독·축 명령·창 종료 대기 | `IBTM/UI/MotionWindowViewModel.cs` |
| 로그 표시·복사·일시정지 | `IBTM/UI/LogWindowViewModel.cs`, `LogTextBox.cs` |
| 표준 로거 연결·파일 저장·최근 로그 수신 | `Shared/IBTM.Core/ApplicationLog.cs`, `IBTM/ApplicationTraceListener.cs` |
| 메인 컨베이어 이송·감지 후 밀착 시간 | `Stations/IBTM.Conveyor/MainConveyor.cs`, `ConveyorSettings.cs` |
| 백업 플레이트·스토퍼 | `Shared/IBTM.Device/ConveyorStation.cs` |
| Station 3 작업/NG 대기 | `Stations/IBTM.Inspection/InspectionStation.cs`, `InspectionWork.cs` |
| NG 픽업·복귀 이동 순서 | `Stations/IBTM.Inspection/NgCarrierMove.cs` |
| NG 실린더·그리퍼 | `Stations/IBTM.Inspection/NgCarrierTransfer.cs` |
| 셔틀·NG 벨트 | `Stations/IBTM.NgConveyor/NgShuttle.cs`, `NgCarrierConveyor.cs` |
| 티칭 화면 배치 | `IBTM/UI/TeachingView.xaml` |
| 공통 티칭 I/O 행·그룹 템플릿 | `IBTM/UI/IoWindowStyles.xaml` |
| 티칭 포인트·선택 | `IBTM/UI/TeachingViewModel.cs` |
| 티칭 Home / 조그 / Move To | `IBTM/UI/TeachingViewModel.Motion.cs`, `TeachingViewModel.Commands.cs` |
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
| 현재 레시피·저장·이미지 교체 | `Shared/IBTM.Storage/RecipeManager.cs`, `IBTM/UI/RecipeEditor.cs` |

## 화면과 ViewModel 경계

모든 화면의 명령과 편집값은 XAML에서 해당 ViewModel에 바인딩한다. ADC 진단도 창 객체가 아니라
`AdcProtocolViewModel`이 포트·슬레이브·레지스터 입력과 통신 작업을 소유한다. 프리셋 선택은 1번 고정이다.
`BeginAdcProtocol`에서 실행권을 얻은 뒤 ViewModel의 명령 본문이 통신과 헤드 동작을 직접 호출한다.
체결 테스트의 시작 조건은 `EnsureBoltTestAvailable`에서 확인하고, 실행과 결과 처리는 명령 본문에 둔다.
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
종료 시 카메라는 DI 전체 정리 전에 명시적으로 Dispose하고 완료를 기다린다.
`OnExit`와 치명적 예외 종료에도 같은 해제를 호출한다. MVS는 수신 종료·버퍼 반환 →
StopGrabbing → 장치 Close → Dispose → SDK Finalize 순서이며, 연결 상태가 OFF여도
남은 장치 핸들의 Close를 시도한다. Dispose 이후에는 Initialize·Live·Grab으로 다시 연결하지 않는다.

## 막힌 동작을 따라가는 순서

티칭 메뉴는 `Teaching` 하나다. 유닛 목록에서 Supply, Placement, Fastening,
Inspection, NG Transfer를 선택한다. 인계 위치·회전 및 이동 높이는 각각 Supply와 Placement의
티칭 목록에 포함되며, 축 피드백과 I/O는 선택 유닛을 따른다. 인계값은 Teach로 임시 보관하고
상단의 `Save`로 양쪽 인계값을 적용·저장하고 현재 레시피도 저장한다. 유닛을 전환해도 임시값은 유지되며,
Teaching 메뉴를 나갔다 다시 열면 저장·적용된 설정에서 다시 읽는다.

각 유닛 목록은 작업 위치, 설비 기준값, 계산 위치로 구분한다. 인계 영역 경계값은 사용하지 않는다.
Supply의 `PCB Handoff`은 XYZ를 티칭한다. 픽업은 Rotated, 인계는 Unrotated 상태다.
`Rotation Z`에서 Unrotated로 전환한 뒤 인계 Z → 인계 XY 순서로 이동한다.
대기는 PCB 1 Pickup의 X/Y와 PCB Rotation Z에서 Rotated 상태다.
Placement는 핸들러 상승 → 대기 Z → 인계 XY에서 대기하고, 실린더 Up 상태로 `ReceiveZ`까지 이동해 받는다.
Supply 해제 후 대기 Z로 복귀하고 Heat Sink 1/2에 차례로 안착한다.
Placement Handler Rotate 출력은 항상 OFF로 고정하며, 자동·반복 동작에서 회전하거나 회전 피드백을 기다리지 않는다. 티칭·OUTPUTS에서도 ON으로 전환할 수 없다.
체결의 `Travel Z`은 공통 이동 높이다. `Shooting Head Fastening Z`는 PCB 체결 높이,
`Pickup Head Fastening Z`는 픽업 볼트의 체결 높이다. 볼트마다 한 번만 체결한다.
자동 동작은 양쪽 헤드 상승 → Safe Z에서 XY 이동 → 선택 헤드의 체결 Z 이동 → 체결 START → 즉시 해당 헤드 하강 순서다.
체결기는 회전을, 실린더는 볼트 전진을 담당한다. START 전송 성공 후 하강하며, 하강 중 오류·정지 시 체결기도 정지한다.
대기는 Heat Sink 1의 첫 슈팅 볼트 XY·Safe Z다. 백업 플레이트가 상승해 캐리어가 착좌되면 체결 Z로 내려간다.
모든 Heat Sink의 슈팅 체결이 끝날 때까지 픽업 테이블을 상승 상태로 유지한다.
슈팅 완료 후 헤드 상승 → Safe Z → 픽업 테이블 하강 확인 → Pickup XY → Pickup Z → 볼트 취득 →
Safe Z → 헤드 상승 → 볼트 XY → Pickup Head Fastening Z → 1회 체결을 반복한다.
별도의 가체결·본체결 패스는 없다. 슈팅·픽업 모두 프리셋 1번으로 고정하며, 레시피에는 프리셋 속성이 없다.

| 체결 상태 | 동작 / 완료 기준 |
| --- | --- |
| `MovingToStandby` → `Waiting` | 헤드 상승 → 첫 슈팅 볼트 XY·이동 Z → 픽업 테이블 상승 후 착좌 대기 |
| `FasteningPcb` | 테이블 상승 확인 → 볼트 위치 → 공급·튜브 통과 확인 → 체결 → 헤드·이동 Z 복귀 |
| `FasteningPickup` | 이동 Z → 테이블 하강 → Pickup XY/Z → 볼트 취득 → 이동 Z·헤드 상승 → 체결 위치 → 체결·복귀 |
| `WaitingForShootingFeeder` / `WaitingForPickupFeeder` | 해당 공급기의 실제 볼트 준비 또는 헤드 감지 변경 대기 |
| `CompletingCarrier` | 모든 결과와 헤드·Z 복귀 확인 후 작업 완료 |


`Bolt Pickup`의 Z는 별도 픽업 높이로 유지한다. 체결의 B1/B2는 헤드별 계산 XY 위치이며
Move To는 Safe Z에서 위치만 확인한다. 각 Z 티칭값은 설비 설정에 독립적으로 자동 저장된다.
기존 공통 체결 Z는 두 헤드 체결 Z의 초기값으로 옮기며, 이후에는 서로 영향을 주지 않는다.
검사 볼트는 Add Bolt로 생성하고 FOV/ROI를 연결한다. 좌표 없는 안내 항목은 목록에 넣지 않는다.

1. XAML의 `Command` / `IsEnabled` 바인딩 이름을 찾는다.
2. ViewModel 생성자의 `new RelayCommand(...)` / `new AsyncRelayCommand(...)`에서 실행 메서드와 조건을 본다. 커맨드는 자동 생성 특성 없이 읽기 전용 속성으로 직접 선언하고, 취소 커맨드와 동시 실행 옵션도 생성자에서 연결한다.
3. 실제 실행 메서드에서 `MachineController` 또는 유닛 호출을 따라간다.
4. 유닛의 현재 상태 분기에서 읽는 **DI/축 피드백을 실장비와 대조**한다.
5. 명령이 나갔다면 TX/DO 로그와 실제 RX/DI를 나눠 확인한다. DO ON 로그만으로 구동 성공을 단정하지 않는다.

커맨드 속성 바로 아래의 이름 있는 실행 메서드에서 동작을 수정한다.
알람 전체 조건과 개별 동작의 간섭 조건을 섞지 않는다. 임의의 지연·재시도·catch로 원인을 감추지 않는다.

티칭 조그의 순서는 `TeachingViewModel.JogAsync` 한 곳에서 관리한다.
동작 중 여부 확인 → `BeginManualOperation`으로 실행권 확보와 현재 피드백 확인 →
유닛의 `JogAsync` 실행 → 실행권 반환 순서다. 시작 직전 같은 피드백을 두 번 확인하지 않는다.
유닛은 해당 장치의 간섭 확인과 장치 제어를 맡는다. 유닛을 건너뛰어 장치를 노출하지 않는다.
드라이버도 공통 `ValidateJog` 호출이 돌아온 뒤 SDK 시작 → 완료/취소 대기 → 종료를 직접 수행한다.
오류가 발생하면 티칭 함수에서 `MachineController.ReportManualFailure`를 호출한다.
전체 STOP·실린더 간섭 감시는 기존 장비 감시에서 유지한다.

버튼의 조그 조건은 티칭 ViewModel에 모으고 유닛별 `CanJog` 전달 함수는 두지 않는다.
HOME 버튼은 표시 갱신에서 계산한 `HomeableAxes`를 쓰며, 실제 HOME은 실행권 확보 후 현재 조건을 확인한다.
공통 HOME 조건과 수평축이 공유하는 설정 검증은 축마다 반복하지 않는다.
테스트는 실제 진입점과 SDK 대역을 사용하고, private 변환 함수의 리플렉션 검사나 같은 분기의 숫자 조합은 반복하지 않는다.

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
- `MainConveyor.PrepareEmptyStationsAsync`: START 준비에서 빈 스테이션만 내린다. 루프마다 반복하지 않는다.
  캐리어 또는 NG 픽업의 지지 상태는 유지하고, 실제 이송의 Release 단계가 하강을 소유한다.
- `StationWork.CurrentJob` / `Complete(job)`: 결과와 완료의 작업 주인이다. 캐리어 교체 후 이전 작업의
  완료는 거부한다. `TransferAsync`의 지역 변수 `departingJob`이 출발 결과를 잡아 새 캐리어와 섞이지 않게 한다.
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

HOME·START 선상승과 HOME 순서(2026-09-19):

- `HOME ALL`은 운전 화면에 항상 표시한다. 별도 `RAISE CYLINDERS`와 `STOP HOME` 버튼은 제거했다.
  모든 HOME은 해당 실린더의 상승·완료 피드백 확인부터 시작하며, 이 준비부터 종료까지 장비 상태는 `Homing`이다.
  공통 STOP으로 준비·원점복귀를 취소할 수 있고, 취소 후 다음 단계로 넘어가지 않는다.
- 전체 HOME은 `HomeAllAxesAsync`에서 실린더 상승 → Z축 HOME 병렬 완료 → 수평축 HOME 병렬 완료를 순서대로 실행한다.
  공급·안착·체결은 Z HOME 완료 뒤 X/Y 동시 HOME을 수행한다. 티칭 유닛 HOME도 같은 순서다.
  공급기 전용 회전·Z 상한 이동·X→Y→Z 순서와 해당 전용 모션 API를 제거했다.
  티칭·모션 창의 개별 HOME도 해당 유닛의 실린더를 먼저 올린다. 축별 HOME은 지정 축만 실행한다.
- START도 같은 실행권·취소 토큰 안에서 활성 유닛의 실린더 상승을 완료한 뒤 자동 시퀀스를 시작한다.
  START가 HOME을 대신 수행하지는 않으며, 원점 완료 조건은 유지한다.
- HOME 중 안착의 Standby Z, 체결의 Travel Z로 이동하지 않는다.
  전체 HOME 종료 뒤 공급기의 Rotation Z로 이동하던 단계도 제거했다.
- 단일 축 HOME은 지정한 축의 서보·알람 피드백을 확인하고 해당 축만 실행한다.
  X/Y HOME에 Z의 원점 완료·서보 ON·높이 일치를 요구하지 않는다.
  Z HOME이나 높이 이동을 공통 드라이버가 대신 실행하지 않는다.
- AJIN의 동시 X/Y HOME은 두 축을 시작한 뒤 한 루프에서 결과를 확인한다.
  취소·실패 시 두 축의 정지 확인과 오류 수거를 끝내야 HOME이 반환한다.
  상위 단계도 한 유닛의 시작 오류 때문에 이미 시작한 다른 유닛을 남겨 두지 않는다.
- AJIN 디바이스 동작은 `C:\git\AnyWave\AnyWave.Device\Motions\Ajin\AjinService.cs`가 기준이다.
  원본의 HOME 결과 초기화·Z HOME 방식·Task.Run과 시작/대기 순서를 유지한다.
  HOME 시작/결과 실패는 원본처럼 false로 반환하고, 상위에서 HomeFailed를 처리한다.
  이동 가감속은 속도 크기 / 설정 시간(초)으로 SDK 단위에 맞춘다. 기본값 0.5초는 원본의 속도 2배와 같다.
  XY 속도 배분은 원본의 각 축 이동거리 / 두 축 이동거리 합을 유지한다.
  HOME 방향은 축별 `AxisHardware.HomeDirection`, 검색 이후 속도와 가속 시간은 `HomeSettings`를 적용한다.
  XY의 센서·Z상·클리어 시간·오프셋은 SDK 설정을 보존하고, Z HOME 방식은 원본을 유지한다.
  설정은 기존 JSON 이름으로 복원했으며 Settings의 Motion 화면에서 수정한다. 범용 이동 실행기는 다시 넣지 않는다.
  IBTM 연결에 필요한 취소, SDK 오류, 실제 이동·정지 피드백 처리는 유지한다.

`MotionService.MoveAxisAsync` / `MoveToXYAsync`는 지정한 축만 움직인다.
조그도 현재 높이에서 지정 축을 움직이며, Z 높이 제한이나 우회 플래그를 두지 않는다.
공급기의 Rotation Z·인계 Z, 배치기의 인계 Z, 체결기의 이동 Z는 각 유닛에서
Z 이동 → XY 이동 순서를 명시한다. 공통 드라이버가 숨은 선행 이동을 넣지 않는다.
새 명령을 시작할 때 기존 명령과 실제 이동 여부를 확인하되, 모든 축의 `InPosition`을
요구하지 않는다. 실행한 이동의 완료·정지 피드백 확인은 유지한다.

실린더 상승 피드백·서보·알람·정지 완료 검사는 추가 이동과 별개다.
위 단계에서 임의로 실린더를 올리거나 장치 준비 상태를 추정하지 않는다.

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
메인·NG 컨베이어의 수동·자동 운전은 각각의 시작 함수에 직접 작성한다.
`ConveyorRun`은 취소 시 모터 OFF와 종료 출력·오류 수집만 맡고 운전 코드를 호출하지 않는다.
메인의 `_runCancellation`은 현재 실행의 취소 대상이며, 캐리어 위치나 이송 이력은 아니다.
종료 출력 실패가 앞선 운전 오류를 덮지 않도록 함께 전달한다.
`ConveyorRunFailureSurvivesOutputCleanupFailure`에서 원본 운전·정지 오류를 확인한다.
단계 내부의 종료도 같은 원칙이다. `TransferStepPreservesOperationAndCleanupFailures`는
메인 입고·이송·배출·복귀, NG 이송, 슈팅, PCB 공급, 슈팅 피더에서 동작과 OFF가 함께
실패해도 원본 예외가 남는지 확인한다. `MainConveyorStopPreservesCancellationAndOutputFailures`는
취소 시 모터 OFF 오류와 종료 오류를, `FasteningRunPreservesFailureWhenShootingCleanupFails`는
체결 유닛 전체 종료의 원본 오류를 확인한다. `AggregateException`이면 `Flatten().InnerExceptions`로
모든 원인을 확인한다. 마지막 OFF 오류 하나만 보고 최초 동작 오류로 판단하지 않는다.
`MachineController.Stop`과 `ShutdownAsync`는 취소·출력·종료 감시 오류를 함께 보존한다.
종료 오류가 있어도 작업 정리를 기다린 다음 피드백 감시를 종료하며,
`StopAndShutdownPreserveCancellationAndOutputFailures`로 이 순서를 확인한다.

수동·자동·Repeat의 모션 알람 분류는 `MachineController.IsMotionFailure`에서 중첩된
모션 오류까지 확인한다. 알람 상세에는 분류 전 원본 예외 전체를 남긴다.
수동 명령은 유닛 호출 뒤 `ReportManualFailure`로 취소와 장치 정리 실패를 보고한다.
`BeginManualOperation`은 실행권·상태 감시만 관리하며 실행 콜백을 받지 않는다.
장치 오류가 없는 프로그래밍 예외는 호출부로 전달한다.
실린더 상승의 `ObserveRaiseAsync`도 STOP 이후 장치 오류를 누락하지 않는다. 먼저 발생한 안전 알람이
있으면 유지하고, 추가 오류는 `Cylinder raise ... failed while stopping` 로그로 확인한다.
START 준비 중 Placement가 PCB를 들고 있으면 핸들러만 올리고 IPM 지지는 유지한다.
HOME은 IPM 상승이 필요하므로 PCB를 잡고 IPM이 내려간 경우 `PlacementHoldingPcb`로 표시한다.
단순 실린더 하강은 HOME 진입을 막지 않으며, 축 HOME 시작 후에는 상승 피드백을 계속 감시한다.

운전·티칭·설정·수동 컨베이어·모션 화면은 정지 명령을 직접 시작하고,
`UI/CommandShutdown.CancelAndWaitAsync`에서
실행 중인 각 명령의 취소를 시도한 뒤 모두 기다린다. 한 취소가 실패해도 나머지 명령을 취소하며,
화면 비활성화·취소 콜백·명령 정리 오류를 함께 전달한다. `CommandShutdownTests`가
대기 순서, 복수 오류, 정상 취소와 첫 취소 실패 뒤 나머지 명령의 정리를 확인한다.
티칭 화면은 명령 종료 후 마지막 이미지 작업과 카메라 종료를 기다리며 앞선 명령 오류도 보존한다.
`OperationCancellation.Link`와 `Operation.Cancel`도 시작·취소 오류 뒤 종료 알림이 실패하면
두 예외를 함께 남긴다. `OperationCancellationTests`에서 오류 보존과 작업 수명 해제를 확인한다.

설정 저장·백업·복원·가상 이미지 변경은 버튼의 `CanExecute`뿐 아니라 실행 메서드에서도
현재 편집 허용 상태를 확인한다. `SettingsStayLockedWhileBusyOrClosingEvenWithAnAlarm`은
작업 중·종료 중 직접 호출해도 파일 대화상자를 열거나 검사 입력 이미지를 바꾸지 않는지 검증한다.

아래 표는 자동 유닛과 공급·안착 직접 인계를 포함한다. 각 유닛의 `RunAsync`에 있는 루프가
현재 피드백으로 다음 동작을 선택하고, 할 일이 없으면 `WaitForChangeAsync`로 기다린다.
`AutoUnit`은 변경 알림과 추적만 관리한다. 자동운전 시작 함수가 각 유닛을 직접 시작하고,
`ObserveAutomaticUnitAsync`는 이미 시작한 작업의 종료·오류를 확인한다.
검사와 NG 이송은 갠트리를 공유하므로 `InspectionStation.RunAsync` 한 경로에서 실행한다.
공급·안착 유닛은 자기 피드백만으로 상태를 계산하며 전체 시퀀스 enum은 각 프로젝트에 둔다. 두 루프는 Core의 `IPcbSupplyHandoff` / `IPcbPlacementHandoff`를 통해 `Handoff`와 변경 알림만 공유한다. 공급은 `Holding`/`Released`, 안착은 `Holding`/`Clear`를 내보내며 그 외에는 `Unavailable`이다. 상대 내부 작업 단계나 핸들러를 참조하지 않는다. Placement는 Supply 인계를 DI로 받고, MachineController는 Supply 실행 시 Placement 인계를 전달한다. 상대 변경은 대기를 깨우기만 하고 다시 전달하지 않는다.
안착 상태 판단과 실행 좌표는 모두 `RecipeManager.Current.PcbPlacement`에서 읽으며 호출자가 별도 레시피를 넘기지 않는다.

| 증상 | 중단점 위치 | 먼저 볼 값 |
| --- | --- | --- |
| Start가 실행돼도 돌아오거나 버튼이 비활성 | `MachineController.StartAsync`의 `IsStartAllowed` 조건 / `MachineController.GetStartBlock` | 실행 시 `startBlock`, 버튼은 `Machine.StartBlock` |
| 특정 유닛이 시작하지 않음 | `RunAutomaticUnitsAsync`의 해당 유닛 `if`, `RunAutomaticUnitAsync`의 취소 조건 | `_units`, `repeat`, `alarm`, `cycle.IsCancellationRequested` |
| 자동운전 중 알람 발생 | `RunAutomaticUnitAsync`의 `catch (Exception exception)` | `alarm`은 발생 유닛, `exception`은 원본 오류, `_state.Alarm`은 먼저 발생한 알람 |
| Repeat 메인 복귀가 취소됨 | `MachineController.Repeat.cs`의 `GetMainConveyorReturnBlock`, `ReturnMainCarrierAsync`의 `CheckPath` | 핸들러 상승·안전 Z, NG 픽업 상승·캐리어 센서; 현재 피드백으로 차단 이유를 반환 |
| 메인 컨베이어가 이송하지 않거나 센서 사이에서 멈춤 | `MainConveyor.RunAsync`, `GetState`, `TransferAsync` | 현재 도착·착좌 센서, 작업 완료와 목적지 점유; START는 현재 피드백으로 동작 선택 |
| PCB 공급이 대기하거나 예상과 다른 동작 | `PcbSupplier.RunAsync` 안 `ExecuteAsync`의 `switch (state)` | `state`, `_pickStep`; 픽업 중에는 `pickPosition`, `carrierChanged` |
| PCB 안착이 멈춤 | `PcbPlacer.ExecuteAsync`, `PlaceAsync`의 `switch (state)` | `heatSink`, `state`; 반환값 `false`이면 피드백 대기 |
| 공급 진입 또는 안착 인수 Z 이동이 대기함 | `PcbPlacer.PlaceAsync`, `PcbSupplier.ExecuteAsync` | Supply `WaitingForPlacement`이면 수취 Z로 이동, Placement `WaitingForSupplyRelease`이면 해제; 위치·잡힘 확인은 해당 유닛 내부에서 수행 |
| 인수 후 Z 복귀 또는 Supply 복귀가 대기함 | `PcbPlacer.PlaceAsync`, `PcbSupplier.ExecuteAsync` | Supply `WaitingForPlacementZ`이면 Placement가 대기 Z로 복귀; Placement의 `Clear` 확인 후 Supply 복귀 |
| 픽업 또는 슈팅 볼트 피더가 대기/타임아웃 | 두 피더가 공유하는 `BoltFeeder.RunAsync` | `state`, `_boltDetected`, `TimeoutMilliseconds`; 슈팅 출력은 `ShootingBoltFeeder.SetFeeding` |
| 볼트 체결이 멈춤 | `BoltFasteningStation.RunCarrierAsync`, `ExecuteAsync`, `FastenAsync` | `state`, `head`, `_pendingFastening`의 볼트·캐리어 |
| Station 3 검사/NG 이송이 대기 | `InspectionStation.ExecuteAsync`, `ExecuteInspectionAsync` | `transferState`, `inspectionState`, `bolt`; `ExecuteAsync`가 `false`를 반환하면 피드백 대기 |
| NG 이송의 정방향·복귀 순서가 예상과 다름 | `NgCarrierMove.GetState`, `ExecuteAsync`, `MoveToCarrierAsync` | `destination`, `state`, 현재 픽업 상승·그립·캐리어 감지, Safe X |
| NG 셔틀이 대기하거나 Repeat 상승하지 않음 | `NgShuttle.ExecuteAsync`, `CycleAsync` | `state`, 실제 Up/Down·캐리어·픽업 상승 피드백 |
| NG 컨베이어 적재·배출이 막힘 | `NgCarrierConveyor.ExecuteAsync`, `MoveCarrierAsync`, `GetState` | `state`, `destination` 입력, `_movement`, `_ejectionPhase`, 현재 위치 센서 |
| 실린더 타임아웃 | `IIoService.SetOutputAndWaitAsync`, `WaitForInputAsync` | 출력 `output`/`value`, 기다리는 입력 `input`/`value`, 제한시간 |

예를 들어 공급의 `switch (state)`에 조건부 중단점
`state == PcbSupplyState.WaitingForPlacement`를 걸면 해당 대기로 들어가는 판단을 볼 수 있다.
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
`PickingPcb` 분기는 픽업 중 전단 캐리어 이탈을 받으면 그 픽업을 취소한다. 늦게 끝난 이전 픽업은
새 캐리어의 슬롯 이력을 넘기지 않으며 `SupplyDoesNotAdvanceTheNewCarrierWhenAnOldPickupFinishes`로 확인한다.
수동 스텝 이동은 `TeachingViewModel.StepAsync` → 각 핸들러의 `AdjustAxisAsync` →
`MotionService.AdjustAxisAsync` 순서다. 공급기·배치기 조그/스텝은 현재 Z에서 선택 축만 움직인다.
Rotation Z/인계 Z와의 일치 조건 및 선행 Z 이동은 없다. 배치기 Z 조그/스텝은 핸들러가 내려와 있어도
조정할 수 있으며, X/Y 조그/스텝과 Move To에는 핸들러 상승 확인을 유지한다.
모션 계층에서 축 속도·범위·취소를 처리한다. 자동/수동 인계 진입은
`MoveToHandoffAsync`에서 `MoveToHorizontalZAsync`에 인계 Z를 전달한 뒤 XY를 이동한다.
XY 이동 전에 Rotation Z로 되돌아가지 않는다.
픽업은 Rotation Z에서 XY 도착 후 해당 PCB 픽업 Z로 내려간다.
회전 IO는 Rotation Z에서만 조작한다. 자동 이송·티칭 포인트 이동은 Rotated일 때 Rotation Z,
Unrotated일 때 인계 Z를 사용한다.
해제 후에는 두 Supply 실린더의 후퇴 완료 → Placement의 대기 Z 복귀·Handler Up 확인 → `MoveFromHandoffAsync`의
XY 동시 복귀 순서다. 인계 Z를 유지하며, PCB1 후에는 PCB2 X와 Carrier Y, PCB2 후에는
다음 캐리어의 PCB1 X와 Carrier Y로 돌아간다. 픽업 XY에 도착한 뒤 Rotation Z로 이동하고 Rotated로 전환한다.
별도 Clear Z나 복귀 좌표는 없다. 시작 시 SMEMA가 없으면 PCB 1 XY·Rotation Z까지 이동해 대기한다.
기존 설정은 인계 Z를 저장하지 않았으므로 `PCB Handoff`의 XYZ를 확인하고 `Save`로 저장한다.
Placement는 `PCB Receive Standby`에서 기다리다가 실린더 Up 상태로 `PCB Receive Z`까지 내려가
PCB 감지·진공·그리퍼를 확인한다. Supply 해제 후 대기 Z로 복귀하고 선택한 히트싱크 XY로 이동한다.
`PCB Receive Z`는 티칭 시 자동 저장되며 기존 대기 XYZ와 별개다. 미티칭이면 수취 Z 이동을 시작하지 않는다.
상대 위치에 따른 진입·이탈·간섭 대기와 경계 티칭은 제거했다. 수동 Z 조그/스텝을 제외한 Placement 축 이동은
핸들러 상승을 요구하며, 이동 중 상승 피드백을 잃으면 정지한다. 자동 Z 이동과 HOME도 이 조건을 유지한다.
인계 도착·잡힘·해제 확인은 유지한다.
상세 순서는 [Placement 동작](../Stations/IBTM.PcbPlacement/DESIGN.md)을 따른다.

PCB 안착의 XY 이동은 `PcbPlacementHandler.MoveToXYAsync`, Z 이동은 `MoveAxisAsync`에서
장치 호출로 이어진다. 같은 Down 센서값으로 압입 전후를 구별할 수 없으므로
`_pressingHeatSink`는 현재 실행 안에서만 압입 대상을 보관한다. `RunAsync` 종료 시 압입 대상,
Repeat PCB 왕복 단계와 실행 대상을 버린다. 현재 캐리어·IPM·그리퍼 피드백 확인은 유지한다.

STOP 자체는 새 START를 차단하거나 전체 장비 비움을 요구하지 않는다.
정상 착좌된 캐리어는 그대로 두고 현재 I/O·작업 완료·인터록으로 다음 동작을 판단한다.
RESET은 캐리어가 착좌되어 있거나 부품이 감지되어도 장치 오류를 해제한다.
재실·보유 부품 감지를 RESET 실패나 새 알람으로 처리하지 않으며, 지지 출력과 작업 결과를 유지한다.
메인·NG 컨베이어와 PCB 안착에는 중단 이력에 따른 차단 플래그나 수동 확인 단계가 없다.
캐리어·PCB 재실, NG 픽업 보유 입력, 중단 이력은 START 차단 조건이 아니다.
STOP 뒤 새 START는 현재 센서·축 피드백과 해당 캐리어의 작업 결과로 동작을 선택한다.
RESET은 장치 알람을 해제하며 운전이나 작업 완료 처리를 하지 않는다.
NG 픽업은 현재 보유·지지·목적지 피드백으로 다음 동작을 판단한다.
복구창, `StartPreparation`, `PrepareRecovery`, 수동 완료 결과 생성은 제거했다.

검사 결과는 해당 캐리어 객체에만 기록한다. STOP 후 새 START에서 미완료 검사를 초기화해
자동 재검사하던 경로는 없다. 새 캐리어 입력이 새 작업을 만든다.
체결의 `_pendingFastening`은 결과가 귀속될 원래 캐리어·볼트·패스만 보관한다.
`ReadPendingResultAsync`는 모터 재기동 없이 확인된 결과만 회수한다.
새 START에서 확인된 미수집 결과를 먼저 수집하고, 결과가 없으면 같은 볼트·패스를 다시 체결한다.
재체결에는 현재 위치, 컨트롤러 정지·준비와 프리셋, 새 하강 피드백과 새 체결 결과가 필요하다.
실린더 하강이 미확인된 이전 결과나 STOP으로 떨어진 FASTEN 신호를 OK로 기록하지 않는다.
중단된 볼트에 새 볼트를 다시 공급하지 않으며, 결과를 다른 캐리어로 옮기지 않는다.
수동 체결 테스트도 미완료 작업이 있으면 차단한다. 통신 상태·결과 읽기는 가능하다.

NG 컨베이어의 목적지와 배출 버튼 확인 단계는 현재 실행에만 속한다.
이송·배출 중 중단되면 실행 단계를 버린다. 새 START는 현재 센서로 동작을 선택한다.
정상 이송의 도착 센서 확인, 셔틀 지지, 배출 확인 버튼 및 모터 OFF 정리는 유지한다.
메인 컨베이어는 중단 단계의 자동 재개를 하지 않는다. `ConveyorTransfer`와 장기 보관 도착 이력은 없다.
`RunAsync` 루프는 `GetState`가 선택한 동작을 끝까지 기다린 뒤 현재 피드백을 다시 읽는다.
반입·S1→S2·S2→S3는 `TransferAsync` 하나에서 목적지 준비 → 출발지 하강 → 구동 →
HS2 감지·추가 밀착 → 결과 전달 → 정지 → S1/S2 착좌 순서로 실행한다. 반입의 출발지는 `null`이다.
지역 변수 `departingJob`의 결과는 모터 OFF 전에 도착지에 전달한다. 취소 시 이벤트를 해제하므로
늦게 들어온 도착 신호가 이전 결과를 다른 캐리어에 붙이지 않는다. 지지 출력은 유지한다.
`_executingTransfer`는 실행 중인 명령만 표시하고 종료·취소·오류 시 지운다. 재개 이력이 아니다.
`GetNextTransfer`의 우선순위는 출구 잔류 → S3 배출 → S2→S3 → S1→S2 → 신규 반입이다.
S1 작업 완료 대기는 `WaitingForPcbPlacement`, S2 작업 완료 대기는 `WaitingForBoltFastening`으로 표시한다.
입구 감지·목적지 HS2 도착·출구 감지/해제·Repeat 역방향 입구 도착에는 `ConveyorSettings.TransferTimeoutSeconds`(기본 5초)를 적용한다.
각 센서 대기 단계에서 시간을 재며, 초과 시 모터와 해당 SMEMA 출력을 끄고 기다리던 입력을 타임아웃으로 보고한다.
스테이션 작업 완료·후단 준비 대기는 제한하지 않는다. 실린더 피드백은 기존 공통 I/O 타임아웃을 사용한다.
HS2 감지 후 추가 밀착 시간과 출구 구멍 통과 여유 시간은 별도이며, Settings → Operation & Timing → Main Conveyor에 모았다.
S1/S2는 동시에 착좌하고 각 유닛이 작업한다. S3는 검사 전에 다른 이송이 가능하면 올려서
기다린다. 가능한 이송이 끝나면 내리고 검사하며, 검사 요청 후에는 벨트를 정지한다.
S3 캐리어가 벨트에 놓여 있으면 전단 Ready도 OFF로 유지한다. S3를 올려 다른 물류를
이송할 수 있거나 S3가 비었을 때 기존 반입 조건에 따라 전단 Ready를 켠다.
검사 완료 후 NG 픽업 위치 복귀를 확인하고, OK·후단 준비 시 바로 배출하거나 올려서 기다린다.
착좌 중 STOP 뒤에도 RESET 없이 현재 상승·하강 피드백으로 새 START를 실행한다.
집중 검사는 `InterruptedSeatingRestartsFromCurrentPresenceWithoutReset`, `InterruptedPlateRaiseUsesFeedbackOnRestartWithoutLoweringSupport`,
`InterruptedTransferKeepsPendingResultsWithoutMovingThemOnLaterInput`, `ActiveTransferKeepsOriginalResultsWhenSourceGetsAnotherCarrier`,
`InterruptedConveyorStartsWithCarrierStillPresentWithoutReset`, `ResetPreservesSeatedCarrierAndAllowsStartingItsTransfer`다.
셔틀의 `_cycleReturnPending`은 제거했다. `CycleAsync`는 하강 완료 후 현재 캐리어와
픽업 상승을 확인하고 상승한다. 중단된 상승을 별도로 기억해 이어가지 않는다.
전체 Repeat도 저장 단계 분기 없이 정방향 → NG 반환/셔틀 왕복 → Station 3 → 입구 순서로 실행한다.
`_repeatDisplayPhase`는 표시와 오류 위치 설명에만 쓰며, 운전 종료 시 초기 표시로 돌아간다.
NG 픽업의 상승·하강은 정방향·Repeat·수동 모두 `SetLiftUpAsync`에서 같은 피드백 대기를 거친다.

Watch에서 `GetPosition()`, `GetAxisState()` 같은 장치 읽기를 계속 평가하기보다 먼저
현재 프레임의 지역변수와 `State`, `Motion.Axes`의 수집된 값을 확인한다.
수집된 표시값은 마지막 스캔 값이다. 실제 분기 판단은 유닛의 현재 I/O·SDK 읽기에서 확인한다.

오류는 실행 폴더의 `Logs/IBTM-*.log`에도 남는다. `Automatic unit ... failed`로 유닛을 찾고,
뒤의 `Machine alarm` 항목에서 원본 예외·내부 예외·스택을 확인한다.
모션 오류가 `MotionUnavailable`로 분류돼도 앞 항목에 발생 유닛 이름이 남는다.
`failed while stopping`은 정리 중 추가 오류이므로 최초 알람과 함께 확인한다.

`AXL.dll` 내부의 `0xC0000005` 종료는 C# HOME 예외 처리만으로 복구할 수 없다.
Visual Studio에서 `IBTM Native` 실행 프로필을 선택하고, 예외 설정의
`Win32 Exceptions > 0xC0000005 Access violation`을 발생 시 중단하도록 설정한다.
실장비 재현은 작업자가 수행하며, 중단 시 호출 스택을 외부 코드까지 표시해
`AXL.dll` 프레임과 최초 C# 호출 위치, 해당 축 번호를 수집한다.
이벤트 로그의 DLL 버전·타임스탬프·오류 오프셋도 함께 보관한다.
오프셋만으로 특정 HOME 함수나 스레드 경합을 원인으로 확정하지 않는다.

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
화면 갱신 루프와 전체 `MachineDisplay` 스냅샷은 없다.
`IoSignals`·`MotionStatus`가 실제 값의 변경을 알리고, `MachineState`는 수집된 준비 상태와
운전·알람 속성의 변경을 알린다. 화면은 이 객체에 직접 바인딩한다.
스테이션별 표시 계산은 `OperationViewModel`에서 해당 유닛의 변경을 받아 수행하며,
같은 상태 판단 메서드에 `live: false`를 전달한다. 수집값이 없으면 null/Unavailable로 표시하고
SDK 재조회로 대체하지 않는다. START·HOME 버튼의 표시도 수집값만 사용한다.
실제 명령 진입에서는 `MotionReadiness`와 현재 출력·센서 피드백을 다시 확인한다.
RESET도 표시용 정지 값만으로 초기화하지 않고, 실행 직전에 Main/NG 운전 출력을 다시 읽는다.
읽기 실패는 정지·통신 알람으로 처리하며, 이미 연결이 끊긴 I/O의 명시적 초기화는 허용한다.
수집기는 준비 상태와 읽기 오류를 따로 알린다. 축 변경마다 변경되지 않은 `ReadError`를
재통지하여 전체 스테이션을 갱신하지 않는다. 티칭은 선택 유닛의 위치·축 상태 변경을 직접
구독해 조그/스텝 버튼을 갱신하며, 유닛 변경·화면 종료 시 해당 구독을 해제한다.
타워램프·부저 출력은 컨트롤러가 알람·자동운전·NG 상태 변경 시 처리한다.
`MachineState`는 스테이션 객체를 참조하거나 출력 명령을 내리지 않는다.
창을 닫거나 운전을 정지해도 DI·DO·모션 수집은 계속 동작한다.

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
중단점을 걸면 된다. 화면 속성으로 정지 여부를 결정하지 않는다.
Start 시점보다 먼저 읽기 시작한 샘플은 새 운전에 적용하지 않는다.
Start/Home/수동 명령의 진입 조건은 계속 현재 장치 피드백을 직접 확인한다.

수집값이 그대로이면 화면에 다시 알리지 않는다. `Sampled`는 값이 같아도 매 수집마다 유지한다.
원시 모션·출력 이벤트는 제어에 즉시 전달하지만 화면은 수집된 속성의 변경을 반영한다.
DI는 입력 변경을, 운전·알람 등 소프트웨어 상태는 해당 속성 변경을 직접 알린다.
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

이 경계의 집중 검사는 `HomeAndAutomaticStartIgnorePreStartSample`와
`AutomaticFeedbackStopsOnSilentMotionFaultWithoutAView`,
`InputAndOutputMonitorsWaitForOperationCleanup`,
`ReadyOutputReadFailureStillFailsClosed`다.
`UnchangedFeedbackDoesNotRefreshTheViewAndInputChangesNotifyImmediately`는 무변경 수집 중 화면 알림이 없고 센서 변경은 즉시 반영되는지 확인한다.
`DisplayUsesAcquiredFeedbackForAllStationsAndNeverFallsBackToHardware`는 각 스테이션이
활성인 화면에서 SDK 재조회가 없고, 명령의 직접 읽기와 수집 실패 표시가 유지되는지 확인한다.
실제 DI 스캔은 SDK 대역의
`PhysicalInputScanPublishesBothProvidersAndRequiresExplicitRecovery`에서 확인한다.
`RecoveredCarrierArrivalDoesNotReuseCompletedStationWork`는 통신 단절 전 비어 있던
스테이션에 복구 시 캐리어가 확인되면 이전 완료 상태로 배출하지 않는지 확인한다.

## 로그 라이브러리와 오프라인 패키지

로그를 남기는 클래스는 `Microsoft.Extensions.Logging.ILogger<T>`를 주입받아 `LogInformation`·`LogError`를 직접 호출한다.
파일은 Serilog의 File/Async sink가 기록하고, `ApplicationLog`는 화면에 표시할 최근 2,000건과 파일 오류만 보관한다.
별도의 파일 쓰기 큐나 로그 호출 래퍼를 만들지 않는다. 기존 `Trace` 메시지는 `ApplicationTraceListener`에서 표준 로거로 연결한다.
파일 저장 실패는 화면 로그와 `FileError`에 남기며, 파일 저장 실패가 장비 호출로 전파되지 않는다.
종료 시 팩터리의 동기 `Dispose`가 남은 파일 기록을 기다리므로 UI에서는 `Task.Run`으로 실행하고 완료를 기다린다.

오프라인 장비에는 `artifacts/IBTM-offline-packages.zip`을 저장소 루트에 푼다.
ZIP에는 현재 장비·테스트 프로젝트의 전체 NuGet 의존 패키지와 `Directory.Build.props`가 들어 있다.
예를 들어 `C:\git\IBTM\packages-offline\`에 `.nupkg` 파일들이 놓여야 한다.
`Directory.Build.props`는 이 폴더가 있을 때 복원 원본을 해당 폴더로 제한하고,
인터넷이 필요한 취약성 검사를 끈다. Visual Studio의 자동 복원에도 같은 설정이 적용된다.
폴더가 없으면 기존 NuGet 원본과 취약성 검사 설정을 그대로 사용한다.

장비에서 Visual Studio로 빌드하거나 다음 명령을 실행한다.

```powershell
dotnet restore IBTM.slnx
dotnet build IBTM/IBTM.csproj -c Debug --no-restore
```

기존 `IBTM-logging-packages.zip`은 로그 관련 패키지만 담았으므로 위 전체 패키지 ZIP을 사용한다.
패키지 참조나 버전을 변경하면 로컬 패키지도 함께 갱신해야 한다.
ZIP은 .NET SDK·Visual Studio·장비 SDK 설치 파일을 포함하지 않는다.
NuGet의 원본 지정과 취약성 검사 옵션은 [공식 복원 문서](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore)를 참고한다.

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
`OutputWindowThreadingTests.BoundConveyorButtonsUpdateAcrossOffCloseAndReopen`에
기존 티칭·FOV 검사도 묶여 있다. WPF Application을 여러 개 만드는 새 테스트 호스트를 추가하지 않는다.
AJIN/AlphaMotion 래퍼는 각 SDK 대역 테스트 프로젝트에서 해당 테스트만 선택한다.
장비 코드 수정은 `IBTM.slnx`, 테스트 프로젝트를 IDE에서 열 때는 `Tests/IBTM.Tests.slnx`를 사용한다.
SDK 대역 프로젝트는 실제 드라이버 파일을 링크해 동명 SDK 스텁과 다시 컴파일한다.
같은 파일의 참조 문맥이 섞이지 않도록 장비용 솔루션에는 테스트 프로젝트를 넣지 않는다.
처음 체크아웃해서 assets가 없으면 장비는 `dotnet restore IBTM.slnx`,
테스트는 `dotnet restore Tests/IBTM.Tests.slnx`를 실행한다.

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
Repeat에서는 Enabled 설정값을 바꾸지 않고 Pickup/Shooting 피더를 모두 OFF로 취급하며 피더 자체를 실행하지 않는다.
일반 운전은 각 피더 Enabled 설정을 따른다. Pickup Feeder OFF 또는 Repeat에서도 피더 XY 이동·실린더 하강·픽업 Z 이동·진공 ON·Safe Z 복귀·실린더 상승을 수행한다.
피더의 볼트 감지와 픽업 진공 ON 확인만 생략한다. 축 위치와 실린더 피드백, 진공 해제 확인은 유지한다.
집힘 확인을 생략한 픽업 실행 이력은 현재 캐리어와 볼트에만 적용하며, 실제 볼트 보유 상태로 표시하지 않는다.
Shooting Bolt Feeder OFF 또는 Repeat는 공급 대기·이스케이프·볼트 발사와 공급 관련 감지 대기를 생략한다.
슈팅 튜브 ON 감지 후 `BoltFasteningSettings.ShootingArrivalDelaySeconds`만큼 기다린 뒤 발사 출력을 끈다.
헤드 진공 ON은 도착 완료 조건으로 기다리지 않는다. 도착 대기 기본값은 3초이며,
Settings → Operation & Timing → Bolt Shooting · Arrival Timing → Head Arrival Delay (s)에서 수정한다.
슈팅 튜브 ON·OFF 감지에는 기존 `ShootingDetectionTimeoutMilliseconds`를 각각 적용한다(기본 3,000ms).
STOP은 도착 시간 대기를 취소하고 발사 출력을 끈다. 공통 피드백·정지 및 피더 공급 타임아웃과 별개다.
피더 ON/OFF와 관계없이 모든 볼트의 XY·헤드별 체결 Z로 이동하고, 프리셋 선택 → START → 실린더 하강 → 체결 결과 수거를 수행한다.
IO형은 기존 FASTEN ON → OFF를 확인한 뒤 START를 끄고 IO · Assumed OK로 기록한다. 통신형은 체결기의 실제 OK/NG 결과를 기록한다.
픽업 볼트는 각 위치에서 한 번만 체결한다. 체결기 준비 확인과 검사도 유지한다.
미수거 결과와 중단된 체결의 복구 조건, XY 이동에 필요한 양쪽 헤드 상승과 Safe Z도 유지한다.
통신형도 START 후 실린더 하강이 확인되지 않으면 늦게 온 결과를 자동 반영하지 않는다.
새 START로 재체결할 때 새 하강 피드백과 새 결과를 확인한다.
하강이 확인된 뒤 결과 수신만 실패한 경우에는 기존 결과를 수거하며 다시 START하지 않는다.
관련 코드는 `PcbPlacer.Repeat.cs`, `MainConveyor.ReturnToStartAsync`, `MachineController.Repeat.cs`다.

NG Transfer까지만 켠 Repeat와 실제 셔틀에 놓는 동작은 다르다.
전자는 셔틀 위치에서 내려도 그리퍼를 풀지 않고 돌아온다.
전체 순서와 실장비 확인 항목은 [Station 3 안내](STATION3_COMMISSIONING.md)에 있다.
