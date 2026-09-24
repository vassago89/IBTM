# 메인 컨베이어와 Station 3 검사 순서

기준: 2026-09-20. 사용자 확정 동작을 코드에 반영한 기록이다.
Virtual 검증과 실장비 검증은 구분한다.

## 코드 위치

- `Stations/IBTM.Conveyor/MainConveyor.cs`: 실행 수명, 취소·정지와 출력 정리, 외부 입력.
- `Stations/IBTM.Conveyor/MainConveyor.Automatic.cs`: 현재 상태 판단, 이송 우선순위, 상태별 실행과 SMEMA.
- `Stations/IBTM.Conveyor/MainConveyor.Transfer.cs`: 반입·스테이션 간 이송·후방 배출·역방향 복귀.
  출발지 하강부터 목적지 도착·정지까지는 계속 하나의 비동기 동작이다.
- `Stations/IBTM.Inspection/InspectionStation.Carrier.cs`: 현재 캐리어의 검사 요청과 물리 조건.
- `Stations/IBTM.Inspection/InspectionStation.cs`: 검사·NG 픽업 위치 복귀·NG 이송 실행.

컨베이어 테스트는 `ConveyorTests.cs`의 기동·정지·공통 준비 코드와
`ConveyorTests.Transfer.cs`, `ConveyorTests.Discharge.cs`, `ConveyorTests.Job.cs`로 나눈다.
메인·검사 간 순서 검증은 `MachineLifecycleTests.InspectionConveyor.cs`에 둔다.

## 확정한 물리 동작

**검사는 S3 백업 플레이트 DOWN, 스토퍼 UP, 메인 벨트 정지 상태에서 수행한다.**
검사/NG 트랜스퍼의 작업 외 대기 위치는 NG 픽업 위치다.
S3에 도착해도 검사를 즉시 시작하지 않는다. 다른 캐리어가 지금 이동할 수 있으면 먼저 이동시킨다.

| 구간 | S3 플레이트 / 스토퍼 | 메인 벨트 | 검사/NG 트랜스퍼 |
| --- | --- | --- | --- |
| S2 → S3 이송 | DOWN / UP | HS2 감지 후 설정 시간 추가 구동 | 비어 있는 상태에서 입고 대기 |
| 검사 전 다른 캐리어 이송 | 플레이트 상승 확인 → 스토퍼 하강 | S1 → S2, 전단 → S1 등 현재 가능한 이송을 먼저 수행 | NG 픽업 위치 대기, 검사 시작 보류 |
| 우선 이송 종료 후 검사 준비 | 스토퍼 UP 확인 → 플레이트 DOWN 확인 | 정지 | 메인의 검사 요청을 기다림 |
| S3 검사 | DOWN / UP | 정지 유지. 다른 캐리어의 벨트 이송도 대기 | 바코드·볼트 검사 위치로 이동 |
| 검사 후 복귀 | DOWN / UP | 정지 유지 | Carrier Pickup (S3)로 XY 동시 복귀 |
| OK, 후방 Ready ON | DOWN / 스토퍼 하강 | 바로 후방 배출 | NG 픽업 위치에서 대기 |
| OK, 후방 Ready OFF | 플레이트 상승 확인 → 스토퍼 하강 | 다른 캐리어의 반입·S1 → S2 이송 가능 | NG 픽업 위치에서 대기 |
| 상승 대기 중 후방 Ready ON | 플레이트 하강 | 하강부터 후방 배출까지 한 동작으로 수행 | NG 픽업 위치에서 대기 |
| NG | 플레이트 상승 확인 → 스토퍼 하강 | 다른 캐리어의 반입·S1 → S2 이송 가능 | 상승된 S3 지지 확인 후 내려가 집고 NG 셔틀로 이송 |
| 빈 트랜스퍼의 대기 | 캐리어 유무·검사 결과에 따른 상태 유지 | 메인 컨베이어가 판단 | NG 픽업 위치로 돌아와 대기 |

검사 완료 직후 OK이고 후방이 받을 수 있으면 불필요한 플레이트 상승을 하지 않는다.
그 외에는 NG를 포함하여 플레이트를 올려 캐리어를 벨트에서 분리한다.
NG 셔틀이 준비되지 않았어도 S3는 상승한 상태로 기다릴 수 있다.
NG 픽업 위치는 `PickupSafeX`와 `CarrierPickupPosition.Y`로 정한다.
두 좌표는 `Carrier Pickup (S3)`에서 함께 티칭하고 XY 동시 이동한다.

## S3 캐리어의 세 가지 작업 단계

1. **검사 전 물류 대기** (`WaitingForConveyor`): S3를 올려 벨트에서 분리하고,
   지금 실행 가능한 다른 캐리어의 이송을 먼저 처리한다. 전단 반입도 포함한다.
2. **검사 진행** (`ReadyToInspect` 및 바코드·볼트·복귀 세부 상태): 가능한 우선 이송이 끝나면
   S3를 내리고 메인이 해당 캐리어의 검사를 요청한다. 요청 이후 검사·복귀 완료까지 벨트를 정지한다.
3. **검사 완료 후 이송 대기** (`WaitingForTransfer`): 기존 OK 즉시 배출, OK 상승 대기,
   NG 상승 집기의 분기를 따른다.

예를 들어 S1·S2에 캐리어가 있고 전단 Available도 ON이면 다음 순서다.

`S2 → S3 도착 → S3 상승 → S1 → S2 이송·착좌 → 전단 → S1 반입·착좌 → S3 하강 → 검사`

S1이 비어 있고 S2 → S3 도착 뒤 전단 반입이 가능하다면,
먼저 S1에 받고 S1의 작업이 완료되어 S2로 보낼 수 있는 경우 그 이송까지 우선한다.
아직 진행 중인 S1 작업이나 미래의 SMEMA를 기다리며 검사를 미루지는 않는다.
우선 이송이 하나 끝날 때마다 현재 입력과 작업 완료를 다시 판단한다.
반대로 검사 요청 이후 들어온 반입·이송 요청은 검사와 NG 픽업 위치 복귀가 끝날 때까지 기다린다.

## 메인 컨베이어 상태

전단 반입 요청은 **입구의 가장 앞 캐리어 센서 ON 또는 Front 2 SMEMA Available ON**이다.
두 신호가 동시에 들어올 필요는 없다. SMEMA만 먼저 켜져도 벨트를 구동하고 실제 입구 도착을 확인한다.
반대로 입구에 캐리어가 이미 있으면 SMEMA를 기다리지 않고 반입한다.
S1에 받을 공간이 있어야 하며, S3 검사 중의 벨트 정지와 후방 이송 우선순위는 유지한다.
티칭에서는 기존처럼 Front 2 TEST Available을 사용하고, 자동에서는 실제 SMEMA DI를 사용한다.
Repeat는 외부 SMEMA로 새 캐리어를 받지 않고 현재 입구 센서의 캐리어를 재반입한다.

| 상태 | 동작 또는 대기 조건 |
| --- | --- |
| `WaitingForFrontCarrier` | 입구 센서 ON 또는 전단 SMEMA Available ON 대기. |
| `SeatingCarriers` | 재실한 S1·S2 중 착좌되지 않은 곳을 동시에 상승·스토퍼 하강시킨다. |
| `ReceivingFrontCarrier` | S1 DOWN/스토퍼 UP 준비 → 벨트 구동 → 입구 도착 확인 → S1 HS2 감지 후 추가 구동 → 정지·상승. |
| `MovingBoltFasteningToInspection` | 목적지 DOWN/스토퍼 UP 준비 → S2 하강 → 벨트 이송 → S3 HS2 감지와 추가 구동 → 결과 인계 → 정지. 그다음 다른 캐리어 이송과 검사 중 우선 동작을 판단한다. |
| `RaisingInspectionCarrier` | 검사 전 다른 물류를 우선할 때, 또는 검사 완료 후 즉시 배출할 수 없을 때 S3를 올리고 스토퍼를 내린다. 같은 상승 동작을 공유한다. |
| `PreparingInspectionCarrier` | 우선할 이송이 없을 때 픽업의 비어 있음·상승을 확인하고, 스토퍼 UP → 플레이트 DOWN 확인 후 해당 캐리어의 검사를 요청한다. 하강 중 새 이송 요청이 들어오면 검사 요청 전에 다시 우선 처리한다. |
| `WaitingForInspection` | 검사와 NG 픽업 위치 복귀가 끝나기를 기다린다. 메인 벨트는 정지한다. S1·S2의 플레이트 상승 및 개별 스테이션 작업은 가능하다. |
| `WaitingForInspectionTransfer` | 필요한 픽업 상승·비어 있음 또는 NG 픽업 대기 위치 피드백을 기다린다. |
| `DischargingInspectionCarrier` | 후방 Ready를 확인한 배출 대상 캐리어의 지지를 해제하고 배출한다. Ready OFF 후 설정한 추가 운전 시간이 지나면 정지한다. |

상승 대기 상태의 S3는 새 캐리어를 받을 수 없지만, 앞쪽 빈 공간의 이송을 막지 않는다.
동시에 이송 가능한 캐리어가 여러 개면 기존처럼 후방 배출, S2 → S3, S1 → S2, 반입 순서로 우선한다.
이송 분기와 SMEMA 출력은 해당 위치에서 작업 상태와 현재 입력을 직접 확인한다.
메인 컨베이어에 별도의 `Can...` 판단 속성·메서드를 두지 않는다.
실린더 응답을 기다린 뒤 검사 요청·이송을 시작하기 전에는 현재 조건을 다시 확인한다.
출발 플레이트 하강과 목적지 도착은 하나의 `await` 이송 안에 유지한다.
`_executingTransfer`는 실행 중인 명령의 표시·소유권이며 중단 후 재개 위치를 저장하지 않는다.

## 후단 배출 종료 조건

후단으로 보내는 외부 시작 조건은 **후방 SMEMA Ready ON**이다.
S3 지지 해제 후 벨트를 구동하고, **Ready OFF → 설정한 추가 운전 시간 → 정지** 순서로 배출한다.
추가 운전이 끝나면 벨트와 Available To Rear를 함께 끈다.
지지 해제 중 Ready가 먼저 OFF되면 벨트를 새로 켜지 않는다.

`ConveyorSettings.RearSmemaOffDelaySeconds`는 Settings → Operation & Timing →
Main Conveyor의 **Rear SMEMA OFF Delay (s)**로 설정한다.
기본 0.3초이며 0이면 Ready OFF 때 바로 정지한다.
S1·S2·S3 밀착용 `CarrierStopDelaySeconds`와는 별개다.
Ready가 다시 ON되어도 추가 운전 시간을 다시 시작하지 않는다.

출구 캐리어 센서는 I/O 매핑·UI·배출 판단에서 제거했다.
Ready OFF가 `TransferTimeoutSeconds` 안에 오지 않으면 정지·알람 처리한다.
OFF 이후 추가 운전 시간은 이 타임아웃과 별개다.
STOP은 추가 운전 중에도 즉시 적용되며, 해당 배출의 OFF 시각과 남은 시간을 버린다.
새 START에서 이전 배출 단계를 이어가지 않는다.
티칭에서는 기존 TEST Ready, 자동에서는 실제 후방 SMEMA DI의 OFF를 사용한다.
## 검사 스테이션 상태와 완료 시점

검사 위치는 `InspectionStation.AtInspectionPosition`으로 읽는다.
캐리어 재실, 플레이트 DOWN, 스토퍼 UP, 벨트 Run 출력 OFF가 모두 필요하다.
현재 검사 조건에는 벨트 정지 속도 피드백이 없으므로 Run 출력과 축·실린더 피드백을 구분한다.
메인 컨베이어가 활성화되어 있으면 물리 조건에 더해 메인의 `RequestInspection(job)`이 필요하다.
따라서 S3 도착 때 잠시 벨트가 정지한 것만으로 검사 루프가 먼저 출발하지 않는다.
메인 컨베이어 비활성 상태의 검사 단독 운전은 현재 물리 조건으로 검사한다.

1. `ReadingBarcode` (포인트 이동 포함): 필요한 바코드를 읽는다.
2. `InspectingBolt` (포인트 이동 포함): 해당 캐리어의 볼트를 검사한다.
3. `CompletingInspection`: NG 픽업 대기 위치 복귀부터 현재 캐리어의 완료 기록까지 한 번에 수행한다.
4. OK는 대기하거나 후방 배출한다. NG는 메인이 플레이트를 올리고 스토퍼를 내린 뒤
   `TransferringNgCarrier` 상태로 집기를 진행한다.

검사 중 플레이트·스토퍼·재실 조건이 깨지거나 벨트 Run 출력이 켜지면 진행 중 검사를 취소한다.
취소된 촬영 결과를 완료로 기록하지 않는다. 이미 수집한 결과의 캐리어별 소유권은 유지한다.
검사할 캐리어가 없거나 티칭을 기다리는 경우의 `ReturningToNgPickup`은 빈 픽업을 대기 위치로 복귀시킨다.
NG 운반 중처럼 캐리어를 잡고 있으면 기존 NG 이송 동작이 우선한다.

## 책임과 참조 방향

- `MainConveyor`: 메인 벨트, S1·S2·S3 백업 플레이트와 스토퍼, SMEMA, 물류 우선순위와 검사 시작 요청.
- `PcbPlacer`, `BoltFasteningStation`, `InspectionStation`: 각 공정 실행과 현재 캐리어의 완료 판단.
- `InspectionStation`: 바코드·볼트 검사, 검사 후 복귀, NG 집기·운반.
- `ConveyorStation`: S1/S2/S3의 현재 감지·지지대 상태와 캐리어 Job·결과·완료 소유권.
- `InspectionStation.Carrier.cs`: 검사·착좌 요청, 현재 검사 위치와 NG 픽업 인터록.
- 참조 방향은 `IBTM.Conveyor → IBTM.Inspection → IBTM.NgConveyor`다.
  메인 컨베이어는 `InspectionStation`의 상태와 요청 API를 사용하며 실행 루프를 호출하지 않는다.

메인 컨베이어는 S1/S2의 `ConveyorStation`과 S3의 `InspectionStation`을 직접 참조한다.
공정별 Work 클래스와 중복 DI 객체는 없다. 결과 저장과 UI도 각 스테이션의 `Station`을 사용한다.
검사 스테이션의 픽업 비어 있음·상승 여부는 `InspectionStation.PickupClear`에서 읽는다.
작업 시작 시 선택한 대상, 미수집 결과의 Job, 실행 중인 이송 명령은 각각 다른 수명을 가진
작업 이력이므로 현재 센서값으로 대체하지 않는다.

`MachineController`는 S1·S2·S3 작업 루프를 Enabled와 무관하게 실행하고 취소·오류를 관리한다.
처음부터 착좌된 캐리어의 완료가 첫 이송 선택에 반영되도록 작업 루프를 컨베이어보다 먼저 시작한다.
각 스테이션은 Enabled가 false이면 작업 장치를 구동하지 않고 완료 처리한다. 안착은 잡고 있는 PCB나 미완료 Repeat 인계가 있으면 캐리어를 완료 처리하지 않는다.
작업 수행과 건너뛰기는 모두 같은 완료값을 사용하며, 별도 스킵 상태는 두지 않는다.
`ConveyorStation`은 스테이션이 기록한 완료를 보관하며, 센서나 Enabled만으로 완료를 만들어내지 않는다.
Enabled를 바꿔도 현재 캐리어의 완료는 유지되며, 새 캐리어에는 이전 완료가 승계되지 않는다.
메인 컨베이어는 이 결과와 현재 물리 조건으로 이송 여부를 판단한다.

각 자동 유닛은 자신의 비동기 실행 루프를 가진다. 입력·작업·필요한 축 피드백 변경은 대기를 깨운다.
검사 측은 메인 Run 출력 변경도 구독하여 검사 위치 해제 시 즉시 취소한다.
NG 픽업 위치 도착은 현재 축 피드백으로 판단하며, 별도의 위치 완료 플래그를 저장하지 않는다.
메인 화면의 상태 조회는 캐시된 축 피드백을 사용하고 구동 판단은 현재 축 피드백을 사용한다.
`InspectionRequested`는 현재 Job에 대한 검사 차례의 소유권이다. 위치·완료 피드백을 대체하지 않는다.
캐리어 Job이 바뀌면 이전 요청은 적용되지 않으며, 메인 운전 종료 시 요청을 해제한다.
새 START는 현재 이송 가능 조건을 다시 판단하고, 별도의 복구 단계는 없다.

S2 → S3의 결과는 HS2 감지와 추가 구동이 끝난 뒤, 벨트 OFF 변경을 알리기 **전에** 인계한다.
벨트 정지로 검사 루프가 깨어났을 때 도착 캐리어의 원래 결과가 이미 연결되어 있어야 한다.
중단 뒤 들어온 센서 신호만으로 이전 이송 결과를 다른 캐리어에 넘기지 않는다.

## 비활성 유닛·START·기존 인터록

- S1·S2는 상승·스토퍼 하강 확인 후 자기 루프에서 작업하거나, 비활성이면 바로 완료 처리한다. 이미 올라온 상태로 START해도 같다.
- Inspection 비활성이면 S3 착좌 또는 검사 위치(DOWN/스토퍼 UP/벨트 정지)에서 자기 루프가 완료 처리한다.
  검사를 건너뛴 캐리어는 NG 대상으로 처리한다.
  NG Transfer도 비활성이면 기존 후방 SMEMA 배출 경로를 사용한다.
- Inspection과 NG Transfer가 모두 비활성이면 비활성 갠트리의 대기 위치 이동·확인을 요구하지 않는다.
  컨베이어가 소유한 플레이트·스토퍼의 피드백 확인은 유지한다.
- S3 플레이트가 올라간 채 캐리어를 놓고 START해도, 먼저 가능한 다른 이송을 수행한 뒤
  스토퍼를 올리고 플레이트를 내려 검사한다. 정상적으로 지지된 캐리어의 비움·수동 확인을 요구하지 않는다.
- STOP은 중단을 완료로 만들지 않는다. 별도 복구 단계나 START/RESET 비움 제한을 추가하지 않았다.
- DI 079 `NgCarrierDetected`는 화면 표시 전용이다. 집기 완료·이송 상태·그립 상실·S3 입고 판단에 사용하지 않는다.
  NG 인계 진행 상태와 그리퍼·리프트·수취 지지부의 피드백으로 동작을 판단한다.
- Repeat의 S3 반환 지지는 기존처럼 상승 상태다. 새 검사 진행 시에는 위 DOWN 조건으로 전환한다.

현장 확인 항목은 [STATION3_COMMISSIONING.md](STATION3_COMMISSIONING.md)를 참고한다.
