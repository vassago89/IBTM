# 테스트 실행

장비를 실행하지 않고 `Virtual` 구성 또는 SDK 대역으로 검증한다. 변경한 동작의 테스트만 선택하고, 같은 테스트를 Debug/Release로 반복 실행하지 않는다. `dotnet test`가 의존 프로젝트도 빌드하므로 별도 전체 빌드는 필요 없다.

테스트는 검증 대상별 클래스에 모으며, 별도의 반복 시험만 `.Repeat.cs`에 둔다.
위치를 옮겨도 안전·취소·오류 복구 검증과 `Category=MachineFlow` 구분은 유지한다.

## 설비·화면 테스트 구성

| 파일 | 검증 대상 |
| --- | --- |
| `IBTM.Virtual.Tests/MachineLifecycleTests.cs` | START·STOP·RESET·종료, 유닛 간 인계와 결과 소유권 |
| `IBTM.Virtual.Tests/MachineHomeTests.cs` | 전체·개별 HOME, 실린더 준비, 취소와 홈 실패 |
| `IBTM.Virtual.Tests/TeachingTests.cs` | 티칭 위치·순서·저장, Jog·Step·화면 전환 중 취소 |
| `IBTM.Virtual.Tests/InspectionTeachingTests.cs` | 검사 이미지·ROI·레시피 편집·저장 결과 불러오기 |
| `IBTM.Virtual.Tests/UiBindingTests.cs` | WPF 바인딩의 스레드 갱신, 창 열기 실패와 재열기 |
| `IBTM.Virtual.Tests/HeatSinkAssemblyTests.cs` | 저장소 없는 결과 판정, 최소 회전수, 결과·이미지 GUID 유효성 |
| `IBTM.Virtual.Tests/PcbHistoryTests.cs` | 결과·이미지 DB 저장, 저장 실패 후 재시도, 번호 유지·조회 |

`MachineTest`는 위 설비 테스트들이 공유하는 가상 설정·DI 준비와 모션 오류 대역을 소유한다. 다른 테스트 클래스의 내부 대역을 참조하지 않는다. `VirtualTest`에는 공통 대기와 간단한 STA 바인딩 실행을 둔다.

분리한 설비 테스트는 `Machine integration` 컬렉션에서 기존처럼 직렬 실행한다. `UiBindingTests`는 한 프로세스에서 WPF `Application`을 한 번만 만들기 위해 하나의 테스트 진입점을 유지하며, 실제 `IBTM.App`은 실행하지 않는다.

## 볼트 테스트 구성

| 파일 | 검증 대상 |
| --- | --- |
| `IBTM.Virtual.Tests/AdcProtocolTests.cs` | 시리얼 설정, 포트 분리, 분할 응답·CRC·요청 소유권, 읽기 취소 |
| `IBTM.Virtual.Tests/AdcBoltHeadTests.cs` | ADC START/STOP, 실제 RUN 피드백 확인, 결과 소유권, 드라이런·취소·통신 오류 |
| `IBTM.Virtual.Tests/BoltControllerWiringTests.cs` | I/O START 실패·출력 매핑, 드라이버 선택, 구형 설정 호환 |
| `IBTM.Virtual.Tests/BoltFasteningTests.cs` | 스테이션 이동 순서, 공급, 실린더 인터록, 재시작 |
| `IBTM.Virtual.Tests/MachineLifecycleTests.cs` | 피더 사용 설정과 설비 운전 연결, 상승 피드백 인터록 |

`AdcControllerStub`은 ADC 헤드 테스트에서 응답과 RUN 피드백을 직접 지정하는 공통 대역이다. STOP 전송 횟수와 현재 운전 상태는 별개이며, 새 START 뒤에는 RUN이 다시 켜져야 한다. 정상 프레임과 가상 장비 전체 동작은 기존 `VirtualAdcBus`로 검증한다.

첫 볼트 START·헤드 하강 순서는 `FasteningStartsAdcBeforeHeadDescentAndStopsItAfterCompletionOrInterruption`, 다음 슈팅 공급의 이동 병행·실패 정리는 `NextShootingSupplyOverlapsRetractionAndTravelWithoutDuplicateShot`에서 검증한다. 시작 대기 이동은 첫 슈팅 위치까지 먼저 완료되므로 첫 공급에 XY 병행을 요구하지 않는다.

## 선택 실행 예

저장소 루트에서 실행한다. 의존성을 아직 복원하지 않았다면 처음에는 `--no-restore`를 생략한다.

ADC 프레임 조립 변경의 선택 실행 예:

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~RequestReadLogsEachChunkBeforeFrameValidation|FullyQualifiedName~StatusAndResultRepliesAreReadSeparatelyAndLengthMismatchIsRejected" --verbosity minimal
```

체결기 I/O 연결 변경:

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~BoltControllerWiringTests" --verbosity minimal
```

스테이션 변경은 영향받는 메서드 이름으로 좁히거나 다음 범위를 사용한다:

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~BoltFasteningTests&Category!=MachineFlow" --verbosity minimal
```

긴 전체 경로 시뮬레이션은 `Category=MachineFlow`로 구분한다. 필터를 생략하면 프로젝트의 기본 필터가 이 범주를 제외한다. 직접 지정한 필터는 기본 필터를 대체하므로, 일상적인 수정 확인에는 영향받은 테스트 이름을 지정하고 전체 경로 검증은 명시적으로 요청받았을 때만 실행한다.
