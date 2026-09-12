# Station 3 / NG / Repeat 현장 확인

기준: 2026-09-11 소스. 아래는 현재 구현을 확인하는 체크리스트이며,
실장비에서 검증 완료했다는 뜻은 아니다. 실제 배선·센서·간섭은 별도 확인한다.

## 메인 컨베이어

- 시작 준비에서 백업 플레이트를 내린다. 평소에는 내려두고 작업할 때만 올린다.
- 정방향 이송은 목적지 캐리어 감지를 기다린 **뒤** 설정된 밀착 시간만큼 더 구동한다.
  `CarrierStopDelaySeconds`는 센서 도착 제한 시간이 아니다.
- 백업 플레이트를 올리면 해당 스토퍼는 내린다.
- 비활성 스테이션은 작업 유닛의 모션/작업 출력을 실행하지 않는다.
  컨베이어 소유 백업 플레이트·스토퍼 처리는 남는다.
- 2→3은 Station 2 이송 준비와 Station 3 비어 있음 등을 확인한다.
  NG 픽업 상승은 입고 조건이 아니지만, 픽업이 이미 캐리어를 잡고 있으면 기다린다.
- Inspection이 비활성이면 Station 3은 NG로 처리한다.
  NG Transfer까지 비활성이면 기존 후방 SMEMA 배출 경로를 사용한다.

관련 코드: `MainConveyor.cs`, `ConveyorStation.cs`, `InspectionWork.cs`.
벨트가 안 돌면 DO 명령 여부와 실제 구동을 구분해서 확인한다.

## NG Transfer

- Station 3에서 집으러 갈 때: **Safe X → Pickup Y**, 그 자리에서 집는다.
  별도의 Pickup X 접근은 없다.
- 셔틀에 놓는 위치는 티칭한 Shuttle Place 좌표를 사용한다.
- 돌아와 Station 3에 놓은 뒤 빠질 때:
  **Safe X → 첫 번째 검사 FOV의 Y → 그 FOV의 X**.
- 첫 FOV가 없으면 빠지는 이동을 하지 않는다.
- Home은 위 일반 픽업 접근 순서와 별개다.

관련 코드: `Stations/IBTM.Inspection/NgCarrierMove.cs`.

## Repeat

Repeat는 별도 가상 시퀀스가 아니라 기존 자동 유닛 동작에 반환 구간을 연결한다.
장비 안 캐리어가 하나라는 전제로 Teaching/Manual에서 사용한다.

| 마지막 활성 범위 | 반환 전 동작 |
| --- | --- |
| NG Transfer까지 | 셔틀 위치에서 내려가지만 그리퍼를 풀지 않고 다시 올라와 Station 3으로 반환 |
| NG Shuttle까지 | 셔틀에 놓고 셔틀 하강·상승 후 다시 집어 반환 |
| NG Conveyor까지 | NG 끝까지 보낸 뒤 셔틀로 역방향 반환하고 Station 3으로 반환 |

NG Transfer만 사용하는 Repeat에서는 비활성 셔틀의 Up/Down/캐리어 입력을 기다리지 않는다.
실제 셔틀에 내려놓는 경로는 셔틀 피드백을 확인한다.
픽업 자체의 실린더·그리퍼·캐리어 피드백과 Station 3 지지 상태는 여전히 필요하다.

**임시 앞단 센서 대체:** 메인 반환은 Station 1 캐리어 센서에서 멈추며
Station 1 백업 플레이트는 내려둔다. 다음 전진은 Station 2부터 처리한다.
전용 앞단 센서 설치 시 이 임시 처리를 바꿔야 한다.

관련 코드: `IBTM/MachineController.Repeat.cs`,
`Stations/IBTM.Conveyor/MainConveyor.cs`의 `ReturnToStartAsync`.

## 짧은 현장 확인 순서

1. 실제 Settings의 축/IO 매핑과 센서 표시를 먼저 대조한다. 코드 기본값만 믿지 않는다.
2. 캐리어 하나로 Start 시 플레이트 하강, 목적지 감지, 감지 후 밀착 구동을 확인한다.
3. NG Transfer까지만 켜서 그리퍼를 유지한 왕복을 확인한다.
4. 셔틀과 NG Conveyor는 각각 활성화해 다음 구간을 확인한다.
5. 이동 중/센서 대기 중 Stop이 먹고, 재개 시 현재 피드백과 모순되는 동작을 하지 않는지 확인한다.
6. 첫 FOV가 있다면 복귀 후 빠지는 실제 경로를 저속으로 확인한다.

알람·대기를 만나면 현재 DI/DO, 축 상태, Enabled Units, Repeat 여부, 해당 시각 로그를 같이 남긴다.
조건을 추측해서 새 지연이나 재시도를 추가하지 않는다.
