# 2026-09-14 아침까지 소스 검토

마감: 2026-09-14 08:00 KST. 현재 대화의 예약 `ibtm`이 매시 00분·30분에 이어서 검토한다.
마감에는 새 수정을 시작하지 않고 결과와 남은 현장 확인 사항을 정리한다.

## 유지할 결정

- Supply → Placement는 잡은 상태로 직접 인계한다. Placement의 PCB 감지·진공·그리퍼 닫힘을 모두 확인한 후 Supply를 해제한다.
- 검사는 이진화한 ROI의 밝은 픽셀 비율이다. 학습·모델 관리 프로젝트 제거 변경을 보존한다.
- 메인 스테이션 1·2·3의 재실은 HS1 OR HS2다. 전진 안착은 HS1 감지 후 설정된 추가 구동 시간을 유지한다.
- 입구·출구 센서 제거 여부는 미확정이다. 사용자 답변 전까지 유지한다.
- 이전 턴부터 많은 미커밋 변경이 존재한다. 관련 없는 변경을 되돌리거나 커밋하지 않는다.
- AGENTS.md에 따라 작은 수정과 해당 경로의 Virtual/SDK 대역 테스트만 수행한다. 실장비·네이티브 SDK 실행, 전체 경로/수명주기 테스트, 추측성 추상화, 불필요한 재검증은 하지 않는다.

## 시작 시점의 검증

직전 턴에서 메인 센서 변경 관련 Virtual 테스트 22건과 SDK 대역 테스트 3건이 통과했다.
핵심 범위는 OR 재실 변화, HS2 선행 감지 시 타이머 미시작, HS1 통과 후 STOP/START,
밀착 중 소재 소실, 플레이트 상승 중 STOP, 역방향 복귀, 결과 인계,
검사/안착 작업 재선택, 기존 설정의 폐기 센서 제거, 입력 스캔 실패/복구다.
새로운 근거 없이 이 전체 묶음을 다시 실행할 필요는 없다.

## 23:24–23:28 첫 검토

확인 범위:

- `MainConveyor.ReadState`, `StationWork`의 이송 목적지/작업 결과 소유권.
- `NgCarrierMove.State`와 복귀·하강·그리퍼 해제 판단.
- `BufferStage`의 직접 인계 구역 및 퇴장 대기, `PcbPlacer`의 Supply 퇴장 대기 연결.
- `PcbSupplier.State`의 인계 위치 판단.
- `MachineFeedbackMonitor`, `OperationCancellation`은 다음 검토를 위한 구조 파악만 시작했으며 전체 검토 완료가 아니다.

발견 및 수정:

- `WaitForSupplyOutsideAsync`는 위치를 읽어 퇴장 여부를 판단하면서 `StateChanged`만 기다렸다.
  이동 중 공유 구역을 벗어나도 위치 변화만 발생하면 대기가 풀리지 않았다.
- 기존 `PositionChanged`에도 대기를 연결하고 `finally`에서 구독을 해제했다. 별도 감시 루프나 상태는 추가하지 않았다.
- 기존 `BufferAllowsOnlyTheTaughtHandoffOverlap` 테스트에 공급축이 계속 조그하는 동안 구역을 벗어나는 경우를 추가했다.
  실제 경계 밖 위치가 확인된 뒤에도 대기가 시간 초과하는 것을 먼저 재현했고, 수정 후 이동이 끝나기 전에 대기가 풀리는 것을 확인했다.

검증: Virtual 구성에서 아래 3건 통과. 전체 솔루션 추가 빌드나 실장비 실행 없음.

- `BufferAllowsOnlyTheTaughtHandoffOverlap`
- `DirectHandoffWaitsForAllThreePlacementSignalsAndSupplyExit`
- `SupplyKeepsGripperClosedWhenPlacementLosesVacuumDuringRelease`

## 다음 검토 범위

1. `MachineFeedbackMonitor`의 나머지 읽기 실패/정지/종료와 `MachineController`의 고장 연결. 표시 갱신과 실제 장치 취득의 중복 여부.
2. `PcbSupplier.RunAsync`의 `ReleasingPcb` 분기와 인계 도중 STOP/재시작 시 PCB 지지 유지. 현재 수정의 동작을 근거 없이 다시 바꾸지 않는다.
3. 체결 중 STOP/통신 실패 뒤 미회수 결과가 같은 소재·볼트에 돌아가는지 확인한다.
4. 이진화 검사 ROI·레시피 저장과 검사 결과 소유권, NG 인계·Repeat 반환을 확인한다.
5. 센서 제거/버퍼 제거 이후 남은 문서·테스트의 전제 불일치를 찾는다. 이미 확인한 경로의 반복 청소는 하지 않는다.

의심만 있는 내용은 근거와 함께 미확정으로 기록하고, 재현 가능한 결함부터 작게 수정한다.

## 23:31 이후 두 번째 검토

확인 범위와 판단:

- `MachineFeedbackMonitor` 전체의 입력·출력·축 감시, 읽기 실패 및 종료 경로를 확인했다.
  실제 입력 스캔은 `ReadInputs`, 출력 취득은 `ReadOutputs`, 축 샘플은 `ReadMotion`에 모여 있다.
  `MotionStatus`의 제어 표시가 진단 샘플을 재사용하며, 실제 명령 전 현재 피드백을 읽는 경로와 구분된다.
- `MachineState`의 화면 갱신은 장치 취득을 직접 반복하지 않고 알림을 소비한다.
  `MachineController.ShutdownAsync`는 취소 후 장치 정리와 Live View 종료를 기다린 뒤 감시를 종료한다.
- I/O 실패 시 최초 통신 오류를 알람 원인으로 남기고 STOP 실패는 따로 기록한다.
  축 감시의 실패 샘플은 자동운전에 전달하며, 시작 이전에 출발한 샘플로 새 운전을 취소하지 않는 구분이 있다.
- `OperationCancellation`의 상위 명령 소유권, 취소 중 자식 정리 대기, 종료 중 새 작업 차단을 확인했다.
  이 범위에서 새로운 결함이나 제거할 중복 감시를 확인하지 못했다.
- 직접 인계의 실제 해제 코드는 `PcbSupplyHandler.ReleaseAtBufferAsync`라는 메서드가 아니라
  `PcbSupplier.RunAsync`의 `ReleasingPcb` 분기다. 앞선 다음 범위의 잘못된 메서드명을 정정했다.
  고정구 해제와 그리퍼 개방 직전에 각각 Placement의 3조건을 재확인한다.
  STOP 후 이미 해제된 액추에이터는 현재 피드백으로 건너뛰며, 인계 구역 안에서 둘 다 열린 경우 퇴장 동작을 이어간다.
- `BoltFasteningStation`의 `PendingResult`, `RunCarrierAsync`, `FastenAsync`, `RecordResult`와
  `AdcBoltHead`의 전체 결과 회수 경로를 읽었다. 소재의 Job·볼트·체결 단계를 묶어 보관하고,
  재시작 시 미회수 결과를 먼저 읽으며, 교체된 소재에는 이전 결과를 기록하지 않도록 검사한다.
  `AdcBoltHead`는 STOP을 보낸 뒤 늦은 결과를 회수하고, 하드웨어 알람 리셋만으로 미회수 결과를 지우지 않는다.

검증 방식: 관련 기존 회귀 테스트의 기대값과 코드 연결을 대조했다.
이번에는 제어 코드를 변경하지 않았으므로 테스트를 다시 실행하지 않았다.
새로운 현장 확인 사항도 추가하지 않았다.

다음 실행은 아래의 미검토 범위부터 이어간다:

1. `AdcBus`의 통신 취소·포트 종료·타임아웃과 `BoltFasteningStation`의 나머지 헤드/피더 동작.
2. `BinaryChecker`, `BoltInspector`, `InspectionStation`, 레시피 저장의 경계와 결과 귀속.
3. NG 인계 및 Repeat의 전체 반환 연결. 이미 읽은 `NgCarrierMove.State`만 다시 반복하지 않는다.
4. 최근 센서/버퍼/학습 프로젝트 제거 이후 남은 문서·테스트 전제 불일치.

## 00:00 이후 세 번째 검토

체결·통신 확인:

- `AdcBus` 전체를 읽었다. 교환 세마포어 안에서 송수신하며, 취소 시 네이티브 I/O 중단을 요청하고
  실제 요청 종료를 기다린 다음 다음 통신에 버스를 넘긴다. 중단 요청 자체가 실패해도 진행 중 I/O 대기는 유지한다.
- 원시 수신 바이트를 파싱 전에 남기고, 부분 응답·CRC·슬레이브·기능 코드 오류를 구분한다.
  이 경로의 기존 `SerialCancellationDrainsTheNativeOperationEvenWhenAbortFails` 등의 테스트와 대조했다.
- 체결 스테이션의 나머지 PCB/IPM 단계, 헤드 상승 및 안전 Z 이동, 슈팅 튜브 통과·진공 대기와
  `BoltFeeder` 공통 동작을 확인했다. 슈팅 종료 실패와 원래 작업 실패를 보존하며 통과 감시 대기도 회수한다.
  새로 확인된 체결·통신 결함은 없고, 이 경로의 제어 코드는 변경하지 않았다.

검사 확인 및 수정:

- `BinaryChecker`, `PixelRegion`, 검사 레시피 값 검증, `InspectionStation`의 결과 기록·취소 경로를 읽었다.
  밝기 임계값은 포함 비교이며 ROI 면적을 분모로 사용한다. 원본 stride와 영상 경계를 검사한다.
  검사 응답 후 취소 및 Job 소유권을 재확인한 뒤 결과를 기록한다.
- `BoltInspector.HasPosition`은 ROI의 존재만 확인하여, 영상 밖 ROI도 준비 완료로 판단했다.
  바코드 ROI는 이미 영상 범위를 확인하고 있어 두 경로의 조건이 달랐다.
- `HasPosition`과 `GetFov`에서 현재 카메라 영상 크기 안의 유효 ROI를 요구하도록 수정했다.
  잘못 저장된 볼트 ROI는 이제 자동운전 준비 판단에서 제외되고, 촬영/이동 요청도 시작 전에 티칭 오류를 반환한다.
- 기존 `FovCaptureUsesCurrentPositionWithoutMoving`에 영상 밖 볼트 ROI를 추가해 수정 전 실패를 재현했다.
  수정 후 촬영/Live View 양쪽과 검사 취소·재시작 검증이 통과했다.

실행 검증: Virtual 구성 3건 통과.

- `FovCaptureUsesCurrentPositionWithoutMoving` 2건
- `InspectionUsesCurrentCarrierSensorsAndRestartsIncompleteWork` 1건

다음 미검토 범위:

1. `InspectionPreview`, 레시피/티칭 이미지 저장과 로드. 이진화 수치가 UI와 실제 판정에서 같은 의미인지 확인.
2. `BoltInspector`의 나머지 Live View/조명 오류 정리와 카메라 종료 연결.
3. NG 인계 및 Repeat의 반환 전체 연결.
4. 센서/버퍼/학습 프로젝트 제거 이후 문서·테스트 전제 불일치.

## 00:31 이후 네 번째 검토

- `InspectionPreview`의 임계값 변경, 기존 프레임 재판정, 이진화 표시 및 최소 밝은 비율을 확인했다.
  화면의 0~100%와 레시피의 0~1 사이 변환은 일치한다. 실제 검사와 미리보기가 같은 `BinaryChecker`를 사용한다.
- 티칭 화면은 `CanEditInspectionRecipe`로 값 편집을 제한한다. 선택 변경 때 진행 중 촬영/재판정을 취소하고
  미리보기를 비우며, 비동기 결과를 반영하기 전에 취소를 재확인한다.
- `RecipeEditor`, `RecipeStore`, `MachineStore.SaveRecipe`, `MachineDb`의 저장·로드를 확인했다.
  레시피·이미지·현재 선택은 트랜잭션으로 저장하며, 이미지 인코딩 실패/취소는 부분 저장을 남기지 않는다.
  다른 이름으로 저장할 때 이미지 복사를 포함하고, 이름 비교는 데이터베이스의 NOCASE 설정과 연결돼 있다.
  `RecipeImagesAndSaveAsAreAtomic`, `RecipeSaveAndLoadKeepTheirOperationActive`의 기존 기대값과 대조했다.
- `BoltInspector`의 Live View/조명 정리와 `HikCamera`의 수신·프레임 반환·종료·Dispose 경로를 확인했다.
  취득을 종료한 뒤 오류를 전달하고, 켰던 조명 채널을 기준으로 OFF하며, 원래 실패와 정리 실패를 보존한다.
  카메라 호출은 이번 검토에서 실행하지 않았다. 관련 SDK 대역/조명 테스트의 기존 커버리지를 읽어 확인했다.

새 결함이나 추가 현장 확인 사항을 찾지 못해 제어 코드 수정 및 테스트 재실행은 하지 않았다.

다음 실행은 NG 컨베이어·셔틀·픽업과 `MachineController.Repeat.cs`의 반환 전체 연결을 우선 확인한다.
그다음 최근 제거된 센서·버퍼·학습 프로젝트와 맞지 않는 잔여 문서/테스트 전제를 확인한다.

## 01:00 이후 다섯 번째 검토

Repeat 시작 조건 수정:

- `CarrierInputs`는 입력 변경 감지를 위해 각 히트싱크 신호를 모두 포함한다. 그런데 Repeat 시작 조건이
  이 배열의 ON 신호 개수를 캐리어 수로 사용하여, 같은 스테이션의 HS1·HS2가 모두 ON이면 두 개로 잘못 셌다.
- `MainConveyor.CarrierCount`에서 입구·출구와 각 스테이션을 위치별로 센다. 스테이션은 기존
  `ConveyorStation.CarrierPresent`의 HS1 OR HS2 판정을 그대로 사용한다.
  Repeat 시작과 메인 컨베이어 반환이 이 계산을 함께 사용한다. 출구 감지 시 Repeat 차단은 유지한다.
- NG 셔틀/컨베이어의 비활성 유닛 제외와 픽업 감지 중복 제외 조건은 유지했다.
- 기존 모드 선택 테스트에 HS1·HS2 동시 ON을 추가하여 수정 전 Repeat 시작 실패를 재현했다.
  다른 두 스테이션에 HS2가 각각 ON이면 차단하고, 하나를 제거한 뒤에는 HS2만으로 시작되는 회귀 검증도 추가했다.

실행 검증: Virtual 구성 5건 통과.

- `ProductionAllowsBothModesWhileRepeatRequiresManualAndSelectorChangeStops` 3건
- `RepeatRejectsTwoOccupiedStationsAndAcceptsOneWithOnlyHeatSink2` 1건
- `TemporaryRepeatReturnStopsAtStation1WithItsPlateDown` 1건

NG/반환 연결 확인:

- `NgCarrierConveyor`의 수납·압축·배출·역송 전체와 `NgShuttle`의 하강/상승 및 Repeat 재개를 읽었다.
  선택된 이동 목적지는 STOP 후 유지하지만, 출발/도착 감지가 모두 없을 때 위치 불명으로 대기한다.
  모터 방향 설정 후 취소를 다시 확인하고 RUN을 켜며, 역송은 캐리어 하나와 셔틀 지지 조건을 확인한다.
- `NgCarrierMove`의 지지 확인·내림·그리퍼 해제·안착 대기와 Station 3 반환/검사 FOV 이탈을 읽었다.
  셔틀 비활성 Repeat에서는 캐리어를 계속 잡고, 활성 셔틀에서는 지지 및 소재 감지를 기준으로 인계한다.
- `MachineController.Repeat.cs` 전체를 확인했다. 정방향 작업 취소/종료를 회수한 뒤 역송을 시작하며,
  메인 반환은 활성 핸들러의 안전 높이와 NG 픽업 상승/소재 없음 조건을 감시한다.
  시작 개수 판정 외에 이번에 새로 입증한 제어 결함은 없다. NG 제어 코드는 변경하지 않았다.

다음 미검토 범위:

1. 최근 센서·버퍼·학습 프로젝트 제거 이후 남은 문서·설정·테스트 전제 불일치.
2. 검사 결과/생산 이력 저장 및 화면의 결과 표시가 새 이진화 검사와 연결되는 나머지 경로.
3. 원점 복귀·수동 출력 조작의 현재 피드백 조건과 최근 센서 변경의 연결.

## 01:30 이후 여섯 번째 검토

제거 기능의 잔여 경로 정리:

- 제거된 입력 4개의 전체 참조를 검색했다. enum ID 보존, 과거 매핑 제거, 사용 불가를 확인하는 테스트에만
  남아 있고 실행 시 해당 입력을 읽는 경로는 추가로 찾지 못했다.
- 학습 이미지 수집용 `BoltInspector.Inspected` 이벤트와 `BoltInspectionImage` 자료형이 남아 있었다.
  제품 코드의 구독자는 모두 제거된 상태이고 테스트만 사용했다. 이벤트·자료형과 전용 기대값을 삭제했다.
- 촬영 후 취소 테스트는 실제 `HeatSinkAssembly`를 보관하여 취소된 촬영의 바코드/볼트 결과가
  이전 작업에 기록되지 않으며, 교체된 캐리어 작업에도 결과가 남지 않는지 확인하도록 바꿨다.
  이진화 전후 취소 확인과 검사 스테이션의 Job 소유권 확인은 유지했다.
- Supply 설계와 배치 표의 버퍼 적치 표현을 직접 인계로 수정했다. IO 안내에는 스테이션별 OR 재실과
  HS1 이후 추가 구동을 반영했다. 검사 티칭에는 밝기/비율의 범위와 경계 비교를 적었다.
- 저장 문서의 '과거 형식 변환 없음'은 현재 `MachineStore`의 지정된 IO 보정과 맞지 않아 바로잡았다.
  문서 수정으로 실제 DB나 장비 설정을 변경하지 않았다.

결과 흐름 확인:

- `HeatSinkAssembly`, `InspectionWork`, `InspectionStation`의 기록/완료 경로와
  `OperationViewModel`의 결과·바코드·볼트 표시, `InspectionView`/`ConveyorView`의 표시 조건을 대조했다.
  이진화 결과는 볼트 유무 결과를 통해 기존 OK/NG 및 NG 이송 판단으로 연결된다.
  현재 재실/히트싱크 입력으로 표시 대상을 제한한다.
- `MachineDb`의 저장 대상은 설정·레시피·티칭 이미지다. 자동 검사 결과는 작업 객체에 보관하며,
  검사 이미지/밝은 비율의 생산 이력 DB 저장 경로는 현재 없다. 이번 정리에서 새 이력 저장 기능은 추가하지 않았다.

실행 검증: Virtual 구성 3건 통과.

- `FovCaptureUsesCurrentPositionWithoutMoving` 2건
- `InspectionUsesCurrentCarrierSensorsAndRestartsIncompleteWork` 1건

문서 변경에는 별도 빌드/테스트를 추가하지 않았다. `git diff --check` 통과.

다음 미검토 범위:

1. `MachineController.Home.cs`의 원점 복귀·실린더 준비와 최근 센서 변경의 연결.
2. 수동 출력 조작 및 출력 창의 실제 I/O 피드백·인터록 연결.
3. 설정 백업/복원 및 종료 시점의 준비 파일 적용 경로.

## 02:01 이후 일곱 번째 검토

원점 복귀·실린더 준비 확인 및 수정:

- `MachineController.Home.cs` 전체를 읽었다. 원점 복귀는 현재 캐리어 입력과 실린더 상승 피드백을 확인하며,
  HS1·HS2 입력 변경 모두 `CarrierInputs`를 통해 재평가된다. Z 원점·안전 높이·수평 원점 순서를 유지한다.
- 일괄 실린더 상승은 플레이스먼트 핸들러와 IPM을 함께 올리지만, 기존 허용 조건은 컨베이어/NG 캐리어만 확인했다.
  다음 PCB를 미리 잡은 상태에서는 컨베이어가 비어 있어도 IPM을 내리고 운반해야 하므로 이 조건이 빠져 있었다.
- `IsCylinderRaiseClear`에서 기존 캐리어 조건과 활성 버퍼 핸들러의 Placement PCB 감지를 함께 확인하도록 했다.
  버튼 표시, 실행 전 확인, 진행 중 취소에 같은 조건을 사용한다. `PcbPlacementPcbDetected` 변경도 즉시 상태를 갱신한다.
- 수정 전 PCB 감지 상태에서 일괄 상승이 허용되고, 핸들러 상승 출력 직후 PCB 감지가 들어와도
  다음 IPM 상승 출력이 나가는 두 실패를 재현했다. 수정 후에는 PCB 감지 시 시작을 막고,
  진행 중 감지 시 이후 명령을 취소한다. 이미 보낸 공압 출력을 역전시키지는 않는다.

수동 출력 경로 확인:

- `MachineController.Outputs.cs`, `OutputWindowRow`, `OutputWindow`의 명령 및 표시를 읽었다.
  OUTPUTS는 화면에 명시된 직접 IO 조작이며, 현재 최소 조건은 수동 모드·E-Stop·IO 준비·종료 여부다.
  이 경로를 임의로 자동 시퀀스나 위치 대기로 바꾸지 않았다.
- 수동 컨베이어는 작업 소유권을 유지하고 OFF·모드 전환·E-Stop 시 취소하며 장치 정리를 기다린다.
  티칭 출력은 구체적인 핸들러 메서드와 대응 DI 대기를 사용한다. 양쪽 DI가 ON인 상태를 완료로 보지 않고,
  화면/선택 변경 취소 시 공압 출력을 자동으로 반전하지 않는 기존 회귀 기대값과 대조했다.

실행 검증: Virtual 구성 3건 통과.

- `RaiseCylindersPreparesHomeWithoutMovingAxesOrOtherActuators` 1건
- `CylinderRaiseStopsBeforeLiftingIpmWhenPlacementDetectsPcb` 1건
- `RaiseCylindersUsesEnabledUnitsAndKeepsOutputsOnStopOrTimeout` 1건

`git diff --check` 통과. 전체 원점/티칭/경로 테스트는 실행하지 않았다.

다음 미검토 범위:

1. 설정 백업/복원 및 종료 시점의 준비 파일 적용 경로.
2. 설정 편집의 IO/축 매핑 검증과 저장 후 재시작 경계.
3. 비동기 화면 작업의 오류 표시·종료 경로 중 아직 읽지 않은 부분.

## 02:31 이후 여덟 번째 검토

설정 백업·복원 확인 및 수정:

- `MachineStore.Backup`, `PrepareRestore`, `RestorePending`, `CopyDatabase`와 설정 화면의 명령을 읽었다.
  백업은 SQLite 백업 API를 사용하며, 복원은 `.restore` 준비 후 다음 시작에서 적용한다.
  `App.OnStartup`은 장치 구성/초기화보다 먼저 복원을 수행하고 DB 로드 오류 시 시작을 중단한다.
- 복원 대기 파일은 존재 여부만 확인하고 바로 적용했다. 복사 도중 오류로 생성된 빈 대기 파일이 남으면
  시작 때 현재 DB에 적용될 수 있었다. 기존 복원 회귀 테스트에 빈 `.restore` 파일을 추가해 거부되지 않는 실패를 재현했다.
- 기존 테이블/무결성 확인을 `CheckDatabase`로 옮겨 복원 준비와 실제 적용에서 함께 사용했다.
  이제 대기 파일을 먼저 검사하고, 통과한 경우에만 현재 DB 백업과 복사를 시작한다.
  잘못된 파일을 임의 삭제하거나 새 설정으로 시작하지 않는다.
- 수정 후 빈 대기 파일 거부, 현재 설정 보존, 정상 복원, 이전 DB 보관과 티칭 이미지 유지가 모두 통과했다.

설정·종료 경계 확인:

- `MachineSettings`의 설정 섹션 저장/로드, `SettingsViewModel.SaveSettingsAsync`, `HardwareMappingRow`,
  설정 XAML의 편집 허용 조건과 기존 `AlarmSettingsRequireManualModeAndSavingDoesNotClearTheAlarm`을 대조했다.
  운전/종료 중 편집을 차단하고, 수동 정지 상태에서는 알람을 유지한 채 설정을 저장할 수 있다.
- 실제 입력 매핑은 값 복사, 출력/피드백 매핑은 새 객체로 복사되며, `AjinMotionService`는 생성 시 축 번호와
  펄스 변환 값을 보관한다. `IoSignals`의 표시 주소도 생성 시 값이다. 편집한 하드웨어 주소가 즉시 장치 호출 주소로 섞이지 않는다.
- `SettingsViewModel`의 가상 이미지 로드는 취소/편집 가능 상태를 재확인한 뒤 적용하고,
  종료 시 저장·백업·복원·이미지/조명 작업을 기다린다. `MainWindow.OnClosing`은 장치/화면 종료 실패를 알린다.
  이 경로에서 추가로 입증한 결함은 없어 수정하지 않았다.

실행 검증: Virtual 구성 `SettingsBatchAndDatabaseBackupRestorePreserveValues` 1건 통과.
`git diff --check` 통과. 실제 장비 DB·백업 파일은 건드리지 않았고 테스트의 임시 DB만 사용했다.

다음 미검토 범위:

1. `RecipeEditor` 외 나머지 UI 비동기 명령과 오류/취소 전달: 수동 모션 창, ADC 프로토콜 창, 로그 창.
2. 설정 수치 검증이 실제 모션 명령 경계에 도달하는 경로와 한계/속도 단위 처리.
3. 최근 수정 외의 가상 하드웨어와 실제 하드웨어 간 동작 전제 차이 중 미검토 부분.

## 03:01 이후 아홉 번째 검토

- `MotionWindowViewModel`과 `MotionWindow` 전체를 읽었다. 수동 축 원점은 기존 컨트롤러의 조건/취소를 사용하고,
  창 닫기는 원점 작업 종료를 기다린다. 닫힌 창은 표시 이벤트 구독을 해제하며 이미 예약된 갱신도 `_closing`으로 제외한다.
  표시용 축 번호는 창이 아닌 ViewModel 생성 시 보관하여 미저장 매핑 편집과 섞이지 않는다.
- `LogWindowViewModel`과 `LogWindow` 전체를 읽었다. 컬렉션 동기화, 선택 중 일시 정지, 복사 오류 표시,
  화면 Clear와 원본 로그 구분, 닫기 시 소스 컬렉션/오류 이벤트 해제를 확인했다.
- `AdcProtocolWindow` 전체와 `RunAdcProtocolAsync`, `RunBoltTestAsync` 연결을 읽었다.
  단일 진단 작업이 작업 소유권을 잡고, 닫기/Stop은 진행 중 작업을 취소하고 기다린다.
  원시 Start 쓰기는 제한하며 정회전·역회전은 `AdcBoltHead`의 정리 경로를 사용한다.
  현재 상태/마지막 결과를 구분해 표시하고 원시 수신 로그는 파싱 전에 전달된다.
- ADC 취소 요청 자체의 예외 가능성을 추가로 살폈다. 현재 통신 중단은 취소 콜백에서 직접 장치를 호출하는 구조가 아니라
  `AdcBus.AwaitSerialIoAsync`의 요청 태스크 안에서 중단·종료 대기를 수행한다.
  앞서 확인한 통신 종료 실패 전달 경로와 대조했으며 이번 범위에서 새로 재현하거나 입증한 결함은 없었다.
- 모션/로그/ADC 창의 관련 기존 WPF 검증 소스를 대조했다. 명확한 변경 근거가 없어 코드 수정과 테스트 재실행은 하지 않았다.

다음 미검토 범위:

1. `MotionService`의 수치 검증·한계·속도 처리와 `AjinMotionService`의 단위 변환/축별 완료 판단.
2. `VirtualMotionService`의 같은 명령/취소 동작과 실제 SDK 경계의 차이.
3. 자동운전 밖 수동 볼트 테스트와 피더 테스트의 소유권/미수집 결과 처리.

## 03:30 이후 열 번째 검토

- `MotionService` 전체와 `AjinMotionService`의 이동·원점·조그·정지 완료 및 단위 변환 경로를 읽었다.
  실제 SDK 피드백으로 이동/인포지션/알람을 확인하며 명령 이력을 실제 축 상태로 대체하지 않는다.
  취소 후 정지는 취소되지 않은 완료 대기를 사용하고 여러 축의 정지를 각각 시도한다.
- 이동 속도와 가감속 시간 검증이 없어 잘못된 XY 속도에도 Z 준비 이동이 먼저 실행됐다.
  Virtual에서 속도 0/NaN 요청의 선행 Z 이동을 재현했고, SDK 대역에서는 가감속 시간 0으로
  이동/원점 설정 명령이 호출되는 문제를 재현했다. 변경 전 관련 테스트 4건이 실패했다.
- 공통 모션 진입부에서 속도·가감속 시간과 원점 검색 속도/가속 시간을 양수·유한 값으로 확인한다.
  XY 이동과 수평축 원점의 준비 Z 동작 전에 필요한 값을 확인한다.
  조그의 음수 방향과 기존 리미트/원점 API의 속도 절댓값 처리는 유지했다.
- SDK 단위당 mm 설정도 0/NaN을 거부한다. 기존 JSON 키 `MillimetersPerPulse`와 기본값은 유지하며
  설정 입력의 예외는 WPF 바인딩 검증으로 표시한다. 기존 축별 이동 단위 테스트에 거부 검증을 추가했다.
- `VirtualMotionService`의 이동 시간 계산에서는 속도 0이 종료되지 않는 이동, NaN은 즉시 목표 위치 반영으로
  이어질 수 있었으며 공통 진입부 수정이 가상/실제 구현에 함께 적용된다.

실행 검증: 서로 다른 Virtual 테스트 사례 9건과 SDK 대역 2건 통과.

- Virtual 첫 검증 8건: `InvalidMoveSpeedDoesNotRetractZ` 2건,
  `MotionRetractsZBeforeXyMovement` 3건, `HomePublishesTheCompletedState` 2건,
  `SlowJogAccumulatesSubPulseDistanceAndStopsOnCancellation` 1건.
- 단위 설정 및 준비 Z 원점 검증 보완 후 Virtual 4건: `VirtualMotionUsesEachAxisPulseLength` 1건과
  `MotionRetractsZBeforeXyMovement` 3건. 후자의 3건은 첫 검증과 중복이다.
- SDK 대역 `InvalidAccelerationDoesNotIssueMoveOrHomeCommands` 2건.

`git diff --check` 통과. 전체 솔루션 빌드·경로 테스트·실제 SDK 호출은 하지 않았다.

다음 미검토 범위:

1. 자동운전 밖 수동 볼트 테스트와 피더 테스트의 소유권/미수집 결과 처리.
2. `VirtualMotionService`의 남은 보조 메서드와 가상 설정/준비 상태 경계.
3. 최근 검토에서 아직 읽지 않은 유닛별 수동 명령의 취소 후 결과 처리.

## 마감 처리 — 08:37 KST 현재 시각 확인 후

- 예약 메시지에 포함된 시각은 04:01 KST였지만, 이번 실행에서 확인한 실제 현재 시각은
  08:37 KST였다. 마감이 지나 새 코드 수정/테스트를 시작하지 않고 기록과 최종 결과 정리만 수행했다.
- 완료된 상세 검토는 위의 10회차이며 마지막 코드 검토는 03:30 회차다.
  04시 이후 별도의 검토를 완료했다고 볼 기록은 없다. 수동 볼트·피더 경로는 검색만 했으며
  다음 범위에 적힌 추가 검토를 완료한 것으로 처리하지 않는다.
- 예약 `ibtm`은 앱의 자동화 도구로 `PAUSED` 변경을 확인했다.

최종 변경 요약:

1. Supply가 인계 구역을 벗어날 때 위치 변화도 대기를 깨우도록 연결했다.
2. 현재 영상 밖의 볼트 검사 ROI를 준비 완료로 판단하거나 촬영/이동하지 않도록 했다.
3. Repeat 시작 시 HS1·HS2가 모두 ON인 한 스테이션을 캐리어 한 개로 센다.
4. 학습 이미지 수집의 남은 이벤트·자료형을 삭제하고 관련 문서를 현재 설비 전제로 정리했다.
5. Placement가 PCB를 감지하는 동안 일괄 실린더 상승을 차단하고, 진행 중 감지도 이후 명령을 취소한다.
6. 복원 대기 DB를 실제 적용 직전에 검사하여 빈 파일 등으로 현재 DB를 덮어쓰지 않도록 했다.
7. 잘못된 모션 속도·가감속·원점 검색 값을 준비 이동 전에 거부하며 SDK 단위당 mm 설정도 검증한다.

야간 검토에서 수정 후 통과를 확인한 서로 다른 테스트 사례는 Virtual 24건, SDK 대역 2건이다.
검사 경로 재검증 및 모션 보완 후 중복 실행은 이 수에서 제외했다.
시작 전에 통과했던 센서 변경 25건도 야간 검토 수에 합산하지 않았다.
마감 시 `git diff --check`도 통과했다. 실장비/네이티브 SDK 실행과 전체 경로 테스트는 하지 않았다.
기존 미커밋 변경을 보존했으며 커밋·푸시·배포하지 않았다.

현장 확인과 남은 범위:

- 직접 인계에서 Placement PCB 감지·진공·그리퍼 확인 후 Supply 해제, STOP/재시작 시 지지 유지와
  Supply 퇴장 판정을 실장비에서 확인해야 한다.
- 메인 스테이션은 HS1 OR HS2 재실을 유지한다. HS1 감지 후 추가 구동 시간은 물리적 거리 기준으로
  현장에서 확인해야 한다. HS2만으로 이 추가 구동 타이머를 시작하지 않는다.
- 이진화 임계값/밝은 비율/ROI는 실제 정상·불량 영상으로 확인해야 한다.
- 입구·출구 경계 센서 제거 여부는 사용자 확인 전까지 변경하지 않았다.
- 수동 볼트·피더 테스트의 추가 결과 소유권 검토, `VirtualMotionService`의 남은 보조 메서드,
  아직 읽지 않은 유닛별 수동 명령의 취소 후 결과 처리는 미완료 범위로 남긴다.

## 2026-09-14 사용자 요청으로 검토 재개

수동 볼트 결과 소유권 수정:

- `MachineController.Manual.cs`, ADC 창의 `CreateHead`, 정회전·역회전 명령과 원시 레지스터 쓰기,
  `AdcBoltHead`, `BoltFasteningStation.PendingResult` 및 복구 화면 연결을 대조했다.
- ADC 창은 진단마다 새 `AdcBoltHead`를 만들지만 생산 헤드와 같은 드라이버에 연결된다.
  중단한 생산 체결의 결과가 미회수인 상태에서 수동 정회전을 실행하면 드라이버의 이벤트/결과가 바뀌어
  이후 자동 재개가 수동 테스트 결과를 생산 결과로 읽을 수 있었다.
  픽업·슈팅 양쪽에서 미회수 상태에도 수동 테스트가 끝까지 실행되는 실패를 먼저 재현했다.
- 기존 `PendingResult`의 캐리어·Job·Assembly 귀속 판단을 공개 읽기 속성으로 노출하고,
  `CanTestBoltHead`와 `RunBoltTestAsync`에서 같은 조건을 확인한다. 별도 결과 상태나 감시를 추가하지 않았다.
  수동 정회전과 역회전 모두 이 명령 경계를 사용한다. 미회수 결과를 테스트 진입 시 임의로 지우지 않는다.
- 차단은 현재 캐리어에 귀속된 결과만 대상으로 한다. 캐리어 교체 후 남은 헤드 이력만으로 막지 않는다.
  기존 Recovery에서 해당 단계를 완료 처리하거나 자동 재개에서 결과를 회수하면 테스트가 가능하다.
  상태/결과 읽기와 장치 정지는 기존처럼 가능하며, 차단 이유를 예외 메시지와 버튼 툴팁에 적었다.

추가 확인:

- `BoltFeeder`, `PickupBoltFeeder`, `ShootingBoltFeeder` 전체와 `AutoUnit.RunLoopAsync`,
  `MachineController.StopRunOutputs`를 연결해 확인했다. 현재 별도 피더 테스트 명령은 없으며,
  수동 RUN 출력은 기존 OUTPUTS 경로다. 자동 피더는 현재 볼트 센서로 공급을 결정하고 종료 시 RUN을 끈다.
  작업 실패와 OFF 실패를 함께 보존하며, 한 장치 STOP 실패로 다른 장치 STOP이 생략되지 않는다.
  해당 기존 회귀 테스트 소스를 확인했고 새 결함이 없어 이 경로를 수정/재실행하지 않았다.
- `VirtualMotionService`의 남은 원점·조그·정지·위치 양자화와 보조 메서드를 확인했다.
  취소 후 목표 위치로 건너뛰지 않고 마지막 위치를 유지하며, 원점 완료 알림은 홈 완료 상태 반영 후 발생한다.
  실제 장치의 외부 이동/리미트를 검증하는 구현은 아니므로 이 검토를 실장비 검증으로 간주하지 않는다.
  이번 범위에서 새 결함을 입증하지 못해 가상 모션 코드는 변경하지 않았다.

실행 검증: Virtual 구성의 관련 테스트 6건 통과.

- `ManualBoltTestPreservesInterruptedProductionResult` 2건: 실제 체결 유닛 시작 직후 취소하여
  생산 결과 귀속을 만든 뒤 수동 테스트 차단, 이벤트 불변, 결과 보존, Recovery 후 테스트 허용을 확인.
- `AdcOperationKeepsMachineLockedUntilStopFinishes` 1건.
- `FasteningResumeKeepsTheResultWithItsCarrierBoltAndPass` 3건:
  기존 재개/캐리어 교체/수동 완료 처리 검증에 공개 결과 귀속 조건을 함께 확인.

조건 보완 전후 같은 6건을 확인했으며 중복 실행을 별도 사례 수로 세지 않는다.
툴팁/문서만 바꾼 뒤 전체 빌드나 테스트를 추가하지 않았다. `git diff --check` 통과.
중지된 야간 예약은 재개하지 않았으며 현재 사용자 요청에 따라 이 대화에서 작업했다.

다음 검토 후보는 남은 개별 유닛 수동 명령과 티칭 선택 변경 중 취소 전달이다.

## 추가 검토 — 티칭 선택 변경과 늦은 이미지 처리

- `TeachingMotionViewModel`, Supply/Station 티칭의 이동·출력 명령과 Deactivate/Shutdown,
  `CommandShutdown`을 읽고 기존 선택 변경·조그 정지·출력 대기 테스트와 대조했다.
  선택 변경은 이전 `ViewCancellation`을 취소하며, 종료는 시작된 명령과 뒤늦게 예약된 이미지 작업을 기다린다.
  이미 보낸 공압 출력을 취소만으로 반전시키지 않는 기존 동작을 유지했다.
- 수동 Step 속도 차이는 화면 및 기존 테스트에 명시된 동작이었다.
  볼트 미세 조정은 지정한 수동 속도, 다른 유닛 Step은 설정된 축 속도를 사용하며,
  해당 화면에서는 수동 속도 입력을 숨기고 설정 속도 사용을 표시한다. 이 의도를 결함으로 바꾸지 않았다.
- FOV/티칭 포인트 변경, 카메라 촬영, 저장 이미지 재검사, 바코드 읽기 및 `InspectionPreview`의
  결과 적용을 읽었다. 촬영과 검사 결과의 최종 적용에는 취소 확인이 있었다.
- `ReinspectImageAsync`는 저장 영상 변환을 기다린 직후 취소 확인 없이 `Preview.Clear`를 호출했다.
  변환 중 선택이 바뀌면 최종 영상 적용은 거부되더라도 옛 FOV의 바코드/볼트 구분을 다시 적용할 수 있었다.
  화면을 변경하기 전에 기존 토큰의 취소를 확인하는 한 줄을 추가했다. 새 토큰·상태·추상화는 없다.

검증: Virtual 앱 빌드 성공, 경고 0·오류 0. UI 결과 반영의 작은 수정이므로 테스트를 추가하거나
전체 WPF/경로 검증을 재실행하지 않았다. 타이밍 경쟁을 별도 실행 재현한 것은 아니며 코드 경로로 확인했다.
`git diff --check` 통과. 이 범위에서 추가로 입증한 큰 제어 결함은 없다.

다음 우선순위는 실장비에서 직접 인계의 신호/해제 순서와 STOP 재개,
HS1 감지 후 실제 안착 시간, 정상·불량 영상의 밝기/비율 기준을 확인하는 것이다.

## 추가 검토 — 저장 중 STOP과 편집값/저장값의 구분

- `RecipeEditor`, `RecipeStore`, `MachineStore.SaveRecipe/SaveSettings`, `MachineSettings.SaveAsync`를
  저장 전 취소와 커밋 후 취소 관점에서 대조했다. 레시피·이미지·선택은 한 트랜잭션이며,
  커밋이 끝난 작업은 늦은 취소 때문에 화면 반영을 건너뛰지 않는다. 이 경로는 수정하지 않았다.
- `TeachingPoint.Apply`, `TeachCurrentPositionAsync`, `SaveBufferSetupAsync`는 적용한 편집값을
  메모리에 둔 뒤 저장한다. DB에 쓰기 전에 STOP이 들어오면 기존 DB는 보존되지만,
  공통 `SaveSettingsAsync`가 취소를 다시 던지기만 해 미저장 안내가 없었다.
- 기존 인계 설정 테스트에서 메모리 경계값 80 / DB 경계값 70인 상태와 비어 있는 `SaveError`를 재현했다.
  공통 티칭 저장의 취소 처리에 미저장 메시지 한 줄을 추가했다. 편집값을 임의로 되돌리거나
  자동 재저장하지 않고, 사용자가 다시 저장하면 안내가 지워지고 DB도 현재 값으로 반영된다.
- 실행 전에 이미 취소된 작업은 편집값을 적용하지 않으며 기존 동작을 유지한다.
- 티칭 항목의 `Supply Handoff Handoff`, `Placement Handoff Handoff` 중복 표기도 정리했다.

검증: Virtual 구성 `BufferSetupRequiresIdleManualControl` 1건과
`BufferSetupCancelledBeforeExecutionDoesNotApply` 2건, 총 3건 통과.
첫 테스트는 기존 DB 보존, 미저장 안내, 재저장 후 DB 갱신/안내 해제를 함께 확인한다.
수정 전에는 미저장 안내 검증이 실패했고 수정 후 통과했다.
`git diff --check` 통과. 레시피 전체 검증 및 전체 솔루션 빌드는 반복하지 않았다.

## 추가 검토 — Virtual UI 직접 실행 (13:31–13:40)

- 사용자 요청에 따라 Windows UI를 직접 띄우고 버튼·탭·ROI 드래그를 조작했다.
  실행 파일은 `artifacts/ui-check-20260914/IBTM.exe`이며, 기존 실행 폴더의 Data/Logs를
  복사하지 않고 별도 DB에 Virtual Development 레시피를 생성했다.
  최초 실행과 재실행 로그 모두 Control/Camera/Light/Bolt=Virtual을 확인했다.
- HOME ALL: Home Required/NOT HOMED에서 Homing을 거쳐 Ready/HOMED로 전환됐다.
  Supply Teaching에서 Step 모드 X+를 한 번 눌러 현재 X가 0.000 → 0.100 mm로 바뀌고,
  Motion Monitor에서도 동일한 위치와 원점 센서 OFF가 표시되는 것을 확인했다.
  직접 인계 항목의 중복 없는 명칭과 Apply & Save Handoff의 오류 없는 완료도 확인했다.
- 검사: Heat Sink 1/B1을 선택해 Add Current Image로 가상 카메라 FOV를 저장했다.
  밝은 원을 감싸는 ROI를 드래그한 뒤 Reinspect에서 이진화 영상과
  `OK · Bright 31.223%`가 표시됐다. 어두운 영역으로 ROI를 바꾼 뒤에는
  `NG · Bright 0%`로 바뀌었다. Save Recipe 후 앱을 종료·재실행해 FOV/ROI 유지와
  동일한 NG 재검사 결과를 확인했다. 원점 복귀 전에는 이동/신규 FOV 촬영이 비활성화되고
  저장 영상 재검사는 가능한 상태였다.
- Digital Outputs: Machine Light를 ON/OFF해 현재 출력 색상·텍스트 및 반대 동작 버튼이
  함께 갱신되는 것을 확인했다. 출력 창과 모션 창을 닫은 후 메인 화면도 계속 응답했다.
- ADC TEST: Virtual 연결에서 Start Fastening 실행 중 중복 실행 버튼이 비활성화되고,
  TX/RX 로그와 `OK Torque 1.00` 결과가 표시된 뒤 조작 가능 상태로 복귀했다.
- Manual Control: NG Conveyor Run을 눌러 ON/Running을 확인하고 전체 STOP을 눌러
  OFF/Ready 및 Run 재활성화를 확인했다. 로그에도 수동 운전 ON, STOP 요청, OFF가 남았다.
- 표시 수정: Motion Monitor 기본 크기에서 `Servo OFF`의 마지막 F가 잘렸다.
  96 폭 버튼에 공통 좌우 Padding 20이 적용되고 있어 이 버튼만 Padding 8로 줄였다.
  Virtual 앱 빌드(경고 0·오류 0) 후 재실행한 동일 크기 창에서 전체 문구를 확인했다.
  제어 로직 변경이나 UI 스냅샷 테스트 추가는 없다.

검증 범위와 제한: 관찰한 조작에서 앱 충돌·멈춤·저장 오류는 없었고, 실행 로그의
ERROR/WARN/Exception/Binding 검색 결과도 없었다. 정상 종료 시 Application stopped를 확인했다.
숫자 임계값/합격 비율 편집은 자동화 도구의 UIA 값 설정 오류 및 입력 충돌 감지로
완료하지 못했다(화면의 기존 밝기 128, 최소 비율 1% 유지). 저장 중 STOP 취소 경쟁은
이번 UI 조작으로 재현하지 않았으며 바로 앞 절의 회귀 테스트 결과와 구분한다.
전체 생산 경로·실장비 동작·모든 설정 조합을 검사한 것은 아니다.
최종 Virtual 검사 화면은 정지 상태로 열어 두었다.

## 추가 검토 — 검사 기준 입력과 저장 (13:45–13:59)

- 앞서 도구 입력 충돌로 남겨 둔 숫자 편집을 같은 격리 Virtual UI에서 완료했다.
  저장된 어두운 ROI에서 밝기 기준 128 → 0 변경만으로 재촬영·재검사 버튼 없이
  `NG · Bright 0%` → `OK · Bright 100%`가 됐다.
  밝기를 128로 돌리고 최소 비율을 0%로 바꾸면 `OK · Bright 0%`로 갱신됐다.
- 밝기 256과 비율 101%는 입력란에 오류가 표시되고 레시피 속성에 반영되지 않았다.
  다만 오류 상태에서도 Save Recipe가 실행되어 이전 유효값이 저장되는 문제를 확인했다.
  실제로 101%가 표시된 상태의 저장 후 격리 DB에는 이전 비율 0이 남아 있었다.
- XAML의 명명된 BindingGroup에 두 검사 입력을 연결하고, 오류가 있으면 Save Recipe를
  비활성화하고 수정 안내를 표시하도록 했다. 별도 검증 클래스·타이머·VM 상태는 추가하지 않았다.
  레시피 이름과 REPEAT 체크는 그룹에서 제외해 기존 즉시 반영 방식을 유지한다.
- 수정본을 빌드·재실행해 비율 오류만 있을 때, 밝기와 비율 모두 오류일 때,
  비율만 고쳐 밝기 오류가 남았을 때 저장 비활성화를 확인했다.
  둘 다 정상값으로 고치면 저장이 다시 활성화됐다. 저장 후 SQLite를 읽어
  밝기 128, 최소 비율 0.01(1%) 복원을 확인했다.

검증: Virtual 앱 빌드 경고 0·오류 0, UI 직접 조작 및 격리 DB 조회.
입력 표시·저장 버튼 변경이므로 테스트 코드를 추가하거나 전체 경로 시험을 반복하지 않았다.
재실행 로그의 Control/Camera/Light/Bolt=Virtual을 확인했으며 실장비는 구동하지 않았다.
남은 현장 우선순위는 직접 PCB 인계의 세 조건/해제 순서, HS1 감지 후 추가 안착 시간,
실제 양품·불량 영상의 밝기/비율 기준 조정이다.

## 추가 검토 — 정지·재개와 촬영 설정 입력 (14:00–14:14)

- PCB 직접 인계의 세 조건, 고정기 해제 후 그리퍼 해제 전 재확인, 인계 진입 중 STOP 복구를
  코드와 기존 회귀 테스트로 대조했다. 메인 컨베이어의 HS1 감지 이력과 추가 밀기 시간,
  밀기 중 캐리어 상실, 플레이트 상승 중 STOP 복구도 확인했다. 해당 8개 Virtual 사례가 통과했다.
  이 경로의 제어 순서는 변경하지 않았다.
- 검사 입력 오류 상태에서 Camera Settings → Teaching / Inspect로 전환하면 저장 잠금이 유지된다.
  Supply Teaching으로 나갔다 돌아오면 마지막 유효값이 표시되고 저장도 다시 가능했다.
  화면 전환 후 저장 버튼이 잠긴 채 남는 현상은 없었다.
- 조명값 256이 오류 없이 입력·저장되는 문제를 격리 Virtual UI와 SQLite 조회로 재현했다.
  실제 MOVS 드라이버의 허용 범위는 0~255여서 촬영 시점에야 실패할 수 있었다.
  BoltInspectionRecipe에서 조명 범위, 양수인 유한 노출 시간, 유한 게인을 검증하도록 했다.
  카메라별 노출·게인 상한은 임의로 정하지 않고 SDK의 기존 검증을 유지한다.
  세 입력도 기존 RecipeInputs 바인딩 그룹에 연결했다.
- 수정 후 조명 256의 오류 표시와 Save Recipe 비활성화를 직접 확인했다.
  노출·게인 UI 검증 도중 사용자 입력이 감지되어 화면 제어를 넘겼다. 두 항목의 실제 타이핑과
  수정 후 정상 저장까지 확인했다는 의미는 아니다. 수정 전 재현에 사용한 DB 조명값은 255로
  복구·저장한 후 읽어서 확인했고, 그 뒤 수정 빌드로 재실행했다.
- 이미 저장된 잘못된 촬영 설정을 불러오는 경우를 검증하는 회귀 테스트를 추가했다.
  조명 256 / 노출 0 / 게인 1e309 각각 불러오기 오류가 현재 레시피·선택 정보를 바꾸지 않고,
  정상 레시피로 고친 뒤 다시 불러오면 오류가 해소되는 3개 사례가 통과했다.

검증: 위 제어 8개 + 기존 레시피 저장·불러오기 1개 + 잘못된 레시피 복구 3개,
총 12개 Virtual 테스트 통과. 테스트 빌드를 사용했으며 별도 전체 솔루션 빌드나 MachineFlow는
실행하지 않았다. 변경 파일 diff --check 통과. 14:08:30 재실행 로그는 모든 드라이버가 Virtual이고,
조회 시점에 ERROR/WARN/Exception/Binding 오류가 없었다. 실장비 동작을 검증한 결과는 아니다.

### 14:16–14:29 — Supply / Station Teaching 통합

- Teaching 메뉴와 TeachingViewModel 하나로 통합했다. 별도 SupplyTeaching View / ViewModel
  4개 파일을 제거하고 StationTeaching 파일은 Teaching으로 이름을 바꿨다.
- Unit Teaching의 동일한 선택 목록에 Supply, Placement, Fastening, Inspection, NG Transfer를 둔다.
  공급 픽업·안전 높이는 Supply에, 장착 위치·안전 높이는 Placement에 둔다.
- PCB Handoff는 공통 인계 위치·이탈 높이·경계 7개만 보여 준다. 선택 포인트에 따라 축 피드백과
  관련 I/O가 Supply 또는 Placement로 함께 전환된다. 해당 장치의 기존 이동·출력 인터록을 호출한다.
- 인계 Teach는 임시 보관, Apply & Save Handoff는 묶음 적용·저장을 유지한다. 같은 Teaching 안에서
  유닛을 전환하거나 일반 위치를 Teach해도 인계 임시값이 유지된다. 메뉴를 나갔다 다시 들어오면
  적용된 설정에서 새로 읽는다. 저장 중 STOP의 미저장 안내와 실행 전 취소 동작도 유지한다.
- 닫힌 티칭 화면의 카메라 초기화 알림이 UI Dispatcher를 호출하지 않도록 활성 화면만 갱신한다.
  새 통합 VM을 초기화 전에 생성하는 테스트에서 발견한 경로다.

검증: 관련 Virtual 테스트 10개 통과.
- TeachingJogStopsWhenTeachingContextChanges: Supply / Placement / Inspection 3개
- TeachingSwitchesUnitsAndHandoffAxesAfterCancellingJog: 1개
- TeachingOutputsWaitForFeedbackAndCancelWithoutReversingPneumatics: 1개
- BufferSetupRequiresIdleManualControl: 1개 (유닛 전환·일반 Teach 후 인계 임시값 유지 포함)
- BufferSetupCancelledBeforeExecutionDoesNotApply: 2개
- TeachingHintsAndCaptureAvailabilityBeforeInitializationDoNotReadInputs: 1개
- TeachingReportsMotionAndHomeBlocks: 1개

14:24:43 Virtual UI를 실행해 단일 메뉴, Inspection 화면, PCB Handoff 목록·묶음 저장 버튼,
Supply → Placement 인계 포인트 선택에 따른 축·I/O 전환을 직접 확인했다.
Unit Teaching 복귀 시 사용자 조작이 감지되어 이후 입력은 중단했다. 관찰 시점에는 Inspection으로
정상 복귀해 있었다. UI에서 축 이동이나 인계 설정 저장을 수행하지 않았고, 해당 동작은 위 테스트로
검증했다. 실행 로그에 모든 드라이버 Virtual, 초기화 Alarm=None을 확인했고 조회 시점 오류는 없었다.

UI 확인 후 남은 안내 문구의 Buffer setup을 PCB handoff로 고쳐 Virtual 앱 프로젝트만 컴파일했다
(경고 0, 오류 0). 현재 사용 중인 UI에는 마지막 문구 수정 전 통합 빌드가 실행 중이다.
TeachingMotionView의 설계 시점 DataContext도 실제 TeachingViewModel로 맞췄다.
전체 솔루션 빌드·MachineFlow·실장비 시험은 실행하지 않았다. diff --check 통과.

### 14:32 — 인계 전용 탭 제거

사용자 요청에 따라 PCB Handoff / Unit Teaching 전환을 제거했다. 인계 포인트 4개는 Supply,
3개는 Placement의 기존 티칭 목록에 넣고 축·I/O는 선택 유닛에 고정했다. 별도 모드 상태와
포인트에 따른 축 선택 분기를 삭제했다. 양쪽 인계 임시값 보관과 Apply & Save Handoff 묶음 저장은 유지한다.

Virtual 관련 테스트 6개 통과: 유닛·포인트 선택과 조그 취소 1, 출력 피드백 대기·취소 1,
유닛 전환 후 양쪽 값 보관·저장 1, 저장 실행 전 취소 2, 초기화 전 입력 접근 방지 1.
테스트의 의존 프로젝트 빌드를 사용했고 별도 전체 빌드나 MachineFlow는 실행하지 않았다.
실행 중인 Virtual UI에서 사용자 입력이 감지되어 재시작·교체하지 않았다. 이번 화면 변경의 직접
UI 조작 검증은 수행하지 않았으며, 새 빌드는 IBTM/bin/Virtual/net10.0-windows에 있다.
운영 안내 문서와 폐기된 탭 관련 테스트를 갱신했고 diff --check 통과.

## 티칭 항목 정리와 Supply 인계 하강 제거

- Supply 인계 위치를 XY로 축소하고 자동/수동 모두 `MoveToHandoffAsync`에서 Rotation Z → Y → X로 이동한다. 인계 직전 Z 하강 상태와 호출을 제거했다.
- `BufferStage`는 현재 Rotation Z와 실제 XYZ 정지·원점 피드백으로 인계 위치를 판정한다. Placement의 PCB 감지·진공·그리퍼 닫힘 확인, 각 Supply 해제 전 재확인, 해제 후 Clear Z → X 이탈은 유지했다.
- 기존 JSON의 인계 X/Y는 유지하고 옛 인계 Z는 무시한다. 저장 시 인계 위치에는 XY만 기록한다. Virtual 장비의 인계 판정도 같은 높이를 사용한다.
- 티칭 목록을 작업 위치 / 설비 기준값 / 계산 위치 / 인계 간섭 영역으로 구분했다. Supply 전달·Placement 인수 이름을 구분하고 인수 위치를 안착 위치 앞에 배치했다.
- 체결 B1/B2에는 헤드를 표시하고 계산 위치로 구분했다. 검사 목록의 좌표 없는 Bolt 항목은 제거하고 Add Bolt 안내문으로 옮겼다.
- 미설정 X 티칭은 —로 표시하고, 미저장 좌표에도 붙던 Saved 문구를 Teaching position으로 수정했다.
- Virtual 관련 회귀 테스트 9개 통과: 직접 인계의 세 신호·해제 순서, 해제 중 진공 상실, 간섭 영역, 수동 인계 XY, 이송 높이에서 중단·재개, 기존 JSON, 티칭 정의, 인계 설정 저장, 유닛 전환 중 조그 취소.
- `git diff --check` 통과. 새 Virtual 빌드를 기존 격리 실행 폴더에 반영하고 검사 티칭 화면의 그룹·좌표·안내문 표시를 확인했다. 이후 사용자가 ROI와 항목을 직접 조작하여 UI 입력을 중단했다. 새 실행 로그의 초기화 결과는 Alarm=None이며 오류 기록은 없었다.
- 실기 동작과 전체 MachineFlow 경로는 실행하지 않았다.

## 검사 대상 선택과 볼트별 이진화 설정

- Inspection Gantry에서 볼트·Data Matrix·히트싱크를 바꾸면 대상에 연결된 저장 이미지, 촬영 XY, ROI를 함께 선택한다. 등록된 FOV가 없는 대상은 이전 대상의 이미지를 지운다. FOV 목록에서 선택해도 해당 검사 대상으로 전환한다.
- 새로 촬영한 미할당 FOV를 선택한 상태에서는 Add Bolt 후에도 그 이미지와 임시 ROI를 유지한다. 단순 선택이나 미리보기 갱신은 축 이동·재촬영을 하지 않는다.
- 볼트별 BrightnessThreshold와 MinimumBrightRatio를 레시피에 저장한다. 기존 레시피에서 개별 값이 없으면 기존 공통값을 사용한다. 자동 검사도 같은 볼트별 값을 적용한다.
- 저장 FOV 옆에 선택 볼트의 임계값, 최소 밝은 비율, 이진화 ROI, 실제 밝은 비율과 OK/NG를 표시한다. 값 수정 시 즉시 갱신하고 Save Recipe로 저장한다. Data Matrix 선택 시 볼트 설정을 숨긴다.
- Live 화면에는 저장 이미지의 ROI·이진화 결과를 겹치지 않는다. 아래 저장 FOV와 검사 설정은 계속 볼 수 있다.
- 관련 Virtual 테스트 6개 통과: 대상/FOV/ROI/미리보기 연결 및 신규 볼트의 임시 FOV 유지 1개, 볼트별 설정 저장·복원 1개, 이진화 계산 2개, 촬영 위치와 이동 방지 2개. 테스트의 의존 프로젝트 빌드를 사용했다.
- 실행 중인 Virtual UI는 사용자가 조작 중인 이전 빌드이므로 교체하지 않았다. 이번 UI 변경의 직접 조작 검증은 수행하지 않았으며 새 빌드는 `IBTM/bin/Virtual/net10.0-windows`에 있다. 실장비와 전체 MachineFlow는 실행하지 않았다.

## 저장 이미지 줄자와 해상도 보정

- Clear Images 버튼·삭제 명령·관련 상태/종료 등록과 폐기된 테스트 기대값을 제거했다. 기존 저장 이미지 데이터는 삭제하지 않았다.
- 저장 FOV의 Ruler 모드에서 두 점을 드래그해 원본 픽셀 거리를 측정한다. 수평·수직·대각선을 지원하며 기존 이미지 맞춤 변환을 사용한다. ROI 모드와 분리하고 Esc, 이미지/모드 전환 시 진행 중 드래그를 취소한다.
- 실제 거리(mm)를 입력하면 mm/px 후보를 표시한다. Apply & Save Resolution은 양쪽 히트싱크의 저장 ROI로부터 볼트 좌표를 다시 계산하고 레시피에 저장한다. 촬영 XY·이미지·ROI·Data Matrix 위치는 변경하지 않으며 축 이동은 없다.
- 기준핀이 없으면 체결 좌표는 미정으로 둔다. 잘못된 거리나 이미지 로드가 끝나지 않은 상태에서는 적용할 수 없다. FOV 전환 시 이전 측정선과 실제 거리 입력을 지운다.
- 관련 Virtual 테스트 4개 통과: 줄자 보정·양쪽 볼트 좌표 저장·ROI/촬영 XY 보존·거리 검증 1개, 대상/FOV/미리보기 연결 1개, 현재 위치 촬영과 이동 방지 2개. 전체 MachineFlow와 실장비 시험은 실행하지 않았다. 테스트 의존 프로젝트 빌드를 사용했고 직접 UI 조작은 수행하지 않았다.
