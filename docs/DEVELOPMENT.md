# 직접 개발할 때 보는 안내

기준: 2026-09-11 소스. 목적은 **수정 → 현장 확인 → 원인 확인** 사이클을 짧게 하는 것이다.
새 계층보다 실제 호출과 조건이 한눈에 보이는 코드를 우선한다. 상세 작업 규칙은 루트 `AGENTS.md`.

## 수정할 파일

경로는 저장소 루트 기준이다.

| 바꾸려는 것 | 먼저 열 파일 |
| --- | --- |
| 장치 생성·연결 | `IBTM/App.xaml.cs`, `IBTM/DependencyInjection.cs` |
| Start / Stop / Reset / 전체 Home / 준비 조건 | `IBTM/MachineController.cs` |
| Repeat 왕복 경로와 마지막 유닛 | `IBTM/MachineController.Repeat.cs` |
| 수동 DO 조작 | `IBTM/MachineController.Outputs.cs`, `IBTM/UI/OutputWindowRow.cs` |
| 화면 표시 상태 | `IBTM/MachineController.Display.cs`, `IBTM/UI/OperationViewModel.Display.cs` |
| 메인 컨베이어 이송·감지 후 밀착 시간 | `Stations/IBTM.Conveyor/MainConveyor.cs`, `ConveyorSettings.cs` |
| 백업 플레이트·스토퍼 | `Shared/IBTM.Device/ConveyorStation.cs` |
| Station 3 작업/NG 대기 | `Stations/IBTM.Inspection/InspectionStation.cs`, `InspectionWork.cs` |
| NG 픽업·복귀 이동 순서 | `Stations/IBTM.Inspection/NgCarrierMove.cs` |
| NG 실린더·그리퍼 | `Stations/IBTM.Inspection/NgCarrierTransfer.cs` |
| 셔틀·NG 벨트 | `Stations/IBTM.NgConveyor/NgShuttle.cs`, `NgConveyor.cs` |
| 티칭 화면 배치 | `IBTM/UI/StationTeachingView.xaml` |
| 공통 티칭 I/O 행·그룹 템플릿 | `IBTM/UI/IoWindowStyles.xaml` |
| 티칭 포인트·선택 | `IBTM/UI/StationTeachingViewModel.cs` |
| 티칭 Home / 조그 / Move To | `IBTM/UI/StationTeachingViewModel.Motion.cs`, `TeachingMotionViewModel.cs` |
| Live / FOV 추가 / ROI 저장 / Data Matrix | `IBTM/UI/StationTeachingViewModel.Camera.cs` |
| 이미지 위 ROI·십자선 그리기 | `IBTM/UI/ImageTeachingView.cs` |
| 실제 검사 이동·촬영·판정 | `Stations/IBTM.Inspection/BoltInspector.cs` |
| 카메라 연결·수신 | `Hardware/IBTM.Hik/HikCamera.cs` |
| ADC 시리얼·파서 | `Hardware/IBTM.Hantas/` |
| AJIN 단위·Home·축 이동 | `Hardware/IBTM.Ajin/AjinMotionService.cs` |
| 축 피드백 감시 | `Shared/IBTM.Device/MotionStatus.cs` |
| DI/DO 화면용 상태 | `Shared/IBTM.Device/IoStatus.cs`, `IoSignals.cs` |
| IO 번호·축 번호 기본값 | 각 유닛의 `*HardwareSettings.cs` |
| 설정 구성·편집 화면 | `IBTM/MachineSettings.cs`, `IBTM/UI/SettingsViewModel.cs` |
| DB JSON 저장 | `Shared/IBTM.Storage/MachineStore.cs` |
| 레시피 이미지 저장·교체 | `IBTM/RecipeStore.cs`, `IBTM/UI/RecipeEditor.cs` |

## 막힌 동작을 따라가는 순서

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
