# IBTM

세 개의 독립 검사 Station과 하나의 공용 Conveyor를 조율하는 .NET 10 WPF 애플리케이션입니다.

- Station 1 · PCB Placement: PCB 픽업 및 배치
- Station 2 · Bolt Fastening: Fiducial 보정 및 볼트 체결
- Station 3 · Inspection: 비전 검사 후 GOOD 배출 또는 NG 적재

## 프로젝트 구조

```text
IBTM/                               WPF Host, 통합 설정, 화면, DI
IBTM.Core/                          좌표, 공정 이벤트, 공통 데이터
IBTM.Device/                        모션·IO·카메라 장치 계약
IBTM.Transport/                     서보 Conveyor, SMEMA, Station별 Carrier Jig 위치 결정
IBTM.Stations.PcbPlacement/         PCB 배치 공정
IBTM.Stations.BoltFastening/        Fiducial 및 볼트 체결 공정
IBTM.Stations.Inspection/           검사와 GOOD/NG 분기 공정
IBTM.Orchestration/                 병렬 공정 실행과 전체 사이클 수명 주기
IBTM.Virtual/                       가상 장비와 가상 Conveyor 신호
IBTM.Virtual.Tests/                 가상 설비 통합 동작 검증
```

의존성은 설비 구조와 같은 방향으로만 흐릅니다.

```text
IBTM Host
    ├─ IBTM.Virtual
    └─ IBTM.Orchestration
          ├─ IBTM.Stations.PcbPlacement ─┐
          ├─ IBTM.Stations.BoltFastening ├─ IBTM.Core ─ IBTM.Device
          ├─ IBTM.Stations.Inspection ───┘
          └─ IBTM.Transport ──────────────────────── IBTM.Device
```

Station 프로젝트는 서로 참조하지 않습니다. 각 Station은 자기 Recipe, Options,
Stage, IO 주소와 공정 순서를 소유합니다. 공용 Conveyor와 각 Station에 설치된
Stopper·Backup Plate는 `IBTM.Transport`가 소유합니다. 장치 구현을 교체해야 하는
모션, IO, 카메라, 검사기 경계에만 인터페이스를 둡니다.

## 설비 동작

Conveyor는 한 개의 서보 축이며 동시에 Carrier Jig 하나만 이송합니다. Station 1은 전단
`Board Available` 신호를 기다린 뒤 `Machine Ready`를 출력하고 Conveyor를 일정 속도로
운전합니다. 전단 Conveyor도 함께 운전하며, Station 1 감지 신호가 켜지면 우리 Conveyor를
정지합니다.

내부 Station 간 이송도 같은 속도 운전 방식입니다. 목적지가 비어 있는지 확인한 뒤
Conveyor를 Run하고, 출발 Station의 Backup Plate를 내리고 Stopper를 올립니다. 목적지
Carrier Jig 감지 신호가 켜지는 즉시 Conveyor를 Stop합니다. 고정 거리 위치 이동은 하지
않습니다.

Station 3의 판정과 생산 수량은 Carrier Jig 단위입니다. PCB를 여러 번 검사하더라도 하나라도
NG이면 Carrier Jig 전체를 NG Stack으로 옮기고 후단으로 보내지 않습니다. GOOD Carrier Jig만
후단에 `Board Available`을 출력하고 `Machine Ready`를 받은 뒤 양쪽 Conveyor를 함께 운전해
배출합니다. 각 Station은 Carrier Jig를 감지하면 Backup Plate를 올려 Conveyor에서 분리한 뒤
공정을 수행합니다.

다른 Station의 Carrier Jig는 Backup Plate로 들려 있으므로 이송 중에도 공정을 계속할 수
있습니다. 따라서 세 Station 감지 입력은 동시에 켜질 수 있지만 Conveyor 이송은 항상
하나씩 실행됩니다. 모든 이송은 목적지 Carrier Jig 감지 신호가 꺼져 있을 때만 시작하므로
앞 Carrier Jig가 빠지기 전에 다음 Carrier Jig가 들어와 충돌할 수 없습니다.

## 클래스 이름과 책임

| 클래스 | 책임 |
| --- | --- |
| `IConveyorServo` | 실제 Conveyor 서보의 초기화, 속도 운전, 정지 경계 |
| `Conveyor` | 서보 속도 운전, SMEMA 반입·반출, 단일 이송과 목적지 Empty 조건을 조율함 |
| `ConveyorSettings` | Conveyor 운전 속도만 보관함 |
| `CarrierJigPositioner` | 한 Station의 Carrier Jig 감지, Stopper, Backup Plate를 제어함 |
| `PcbPlacementStation` | Carrier Jig 위치 결정 후 PCB 픽업·배치 공정을 실행함 |
| `BoltFasteningStation` | Carrier Jig 위치 결정, Fiducial 검출, 볼트 체결을 실행함 |
| `InspectionStation` | 검사 결과를 Carrier Jig 단위로 판정하고 GOOD 배출 또는 NG Jig 적재를 실행함 |
| `ProcessOrchestrator` | 세 Station 작업을 병렬 실행하고 Conveyor 이송, 정지, 통계를 조율함 |
| `ProcessEvents` | Stage 실행 상태를 열고 닫고 공정 결과를 UI에 전달함 |
| `StationMotionSettings` | Station의 XY/Z 속도 값만 보관함 |
| `TeachingPointMapper` | Recipe 좌표와 화면의 Teaching Point를 변환함 |
| `VirtualConveyorServo` | 속도 운전형 Conveyor 서보를 재현함 |
| `VirtualIoService` | SMEMA, Carrier Jig 이동과 액추에이터 피드백을 재현함 |

공통 Station 인터페이스, 범용 하드웨어 래퍼, Station 등록 모듈 같은 중간 계층은 두지
않습니다. 각 Station 클래스가 자기 모션과 공정용 IO를 직접 사용합니다. 공통 클래스로
분리한 것은 실제로 공유되는 Conveyor와 반복 설치되는 Carrier Jig 위치 결정 장치뿐입니다.

카메라와 검사 결과는 WPF 타입 대신 `ImageFrame`으로 전달합니다. `ImageSource` 변환은
Host의 Presentation 경계에서만 수행합니다. 설정과 Recipe는 현재 모델로 바로 읽으며
이전 이름이나 알 수 없는 형식을 추정해 보정하지 않습니다. 등록되지 않은 Stage와
지원하지 않는 값은 기본값으로 숨기지 않고 즉시 실패합니다.

공정 좌표와 속도 단위는 `mm`, `mm/s`입니다.

## 실행

```powershell
dotnet restore IBTM.slnx
dotnet build IBTM.slnx --configuration Release
dotnet run --project IBTM/IBTM.csproj
```

현재 실행 구성은 `IBTM.Virtual`의 모션, IO, 카메라, Fiducial, 검사, 볼트 구현을
사용합니다. 실제 장치를 연결할 때는 `IBTM.Device`의 계약 구현과 DI 등록만 교체합니다.
