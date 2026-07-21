# IBTM

3개 독립 공정 Station을 하나의 설비 사이클로 조율하는 .NET 10 WPF 애플리케이션입니다.

- Zone 1: PCB 픽업 및 패드 배치
- Zone 2: Fiducial 보정 및 볼트 체결
- Zone 3: 비전 검사 후 GOOD 배출 또는 NG 적재

## 프로젝트 구조

```text
IBTM/                               WPF Host, 통합 설정, 화면, DI, 인프라 구현
IBTM.Core/                          좌표, 공통 런타임, 공정 이벤트 계약
IBTM.Device/                        모션·IO 계약과 시뮬레이션 구현
IBTM.Stations.PcbPlacement/         PCB 배치 공정
IBTM.Stations.BoltFastening/        Fiducial 및 볼트 체결 공정
IBTM.Stations.Inspection/           검사와 GOOD/NG 분기 공정
IBTM.Orchestration/                 Station 실행 순서와 전체 사이클 수명 주기
```

의존성 방향은 다음과 같습니다.

```text
IBTM Host
    └─ IBTM.Orchestration
          ├─ IBTM.Stations.PcbPlacement ─┐
          ├─ IBTM.Stations.BoltFastening ├─ IBTM.Core ─ IBTM.Device
          └─ IBTM.Stations.Inspection ───┘
```

세 Station 프로젝트는 서로 참조하지 않습니다. 각 Station은 자기 `Recipe`,
`Options`, `Stages`, `Channels`, 외부 장치 포트를 직접 소유하고 `IBTM.Core`의
공통 계약만 사용합니다. 구현 클래스는 모듈 내부에 숨기고 공개 Station
인터페이스와 DI 등록 메서드만 외부에 노출합니다. `IBTM.Orchestration`만 세
Station의 실행 순서와 통합 `Recipe`를 알고 있습니다.
화면에서는 기존 설비 명칭인 Zone 1/2/3을 그대로 사용하지만 프로젝트 이름은
순서가 아니라 공정 역할을 나타냅니다.

카메라와 검사 결과는 Core에서 WPF 타입을 사용하지 않고 `ImageFrame`으로
전달됩니다. WPF `ImageSource` 변환은 Host의 Presentation 경계에서만 수행합니다.

## 주요 구성 요소

- `IPcbPlacementStation`: 셔틀 대기, PCB 픽업·배치, 해제 경계
- `IBoltFasteningStation`: Fiducial 보정과 볼트 체결 경계
- `IInspectionStation`: 검사와 GOOD/NG 후속 처리 경계
- `ProcessOrchestrator`: Station 실행 순서, 시작·정지, 생산 통계
- `ProcessStageRunner` / `ProcessEventHub`: 단계 실행과 이벤트 전달
- `StationOperations`: 단일 Station의 모션과 IO 동작
- `TeachingPointMapper`: 레시피와 화면 티칭 좌표 간 변환

통합 `MachineConfig`는 Host에 있으며 Station별 Options 객체를 묶습니다. 설정을
다시 불러와도 동일한 Options 인스턴스에 값을 복사하므로 이미 생성된 Station에
즉시 반영됩니다. Core에는 Zone별 레시피, IO 주소, 단계 목록이 없습니다.
기존 평면 구조의 레시피와 `MachineConfig.json`은 Persistence 경계에서 새 중첩
구조로 변환해 읽으며, 다음 저장부터 새 구조로 기록됩니다.

등록되지 않은 Stage, 지원하지 않는 언어·티칭 모드·검사 결과, 초기화되지 않은
모션 축은 기본값으로 대체하지 않고 즉시 실패합니다. 파일 IO처럼 사용자가 복구할
수 있는 외부 오류와 출력 해제만 경계에서 처리합니다.

공정 계층의 좌표와 속도 단위는 `mm`, `mm/s`입니다.

## 실행

```powershell
dotnet restore IBTM.slnx
dotnet build IBTM.slnx --configuration Release
dotnet run --project IBTM/IBTM.csproj
```

현재 실행 구성은 가상 모션·IO·카메라·검사·볼트 컨트롤러를 사용하는
시뮬레이션 전용입니다. 실제 장치를 연결할 때는 각 공개 포트의 구현을 추가하고
DI 구성을 교체해야 합니다.
