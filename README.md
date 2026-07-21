# IBTM

3개 독립 공정 Station을 하나의 설비 사이클로 조율하는 .NET 10 WPF 애플리케이션입니다.

- Zone 1: PCB 픽업 및 패드 배치
- Zone 2: Fiducial 보정 및 볼트 체결
- Zone 3: 비전 검사 후 GOOD 배출 또는 NG 적재

## 프로젝트 구조

```text
IBTM/                               WPF Host, 통합 설정, 화면, DI
IBTM.Core/                          좌표, 공통 런타임, 공정 이벤트 계약
IBTM.Device/                        모션·IO·카메라 계약
IBTM.Virtual/                       가상 장비와 공정 장치 구현
IBTM.Virtual.Tests/                 가상 장비 동작 테스트
IBTM.Stations.PcbPlacement/         PCB 배치 공정
IBTM.Stations.BoltFastening/        Fiducial 및 볼트 체결 공정
IBTM.Stations.Inspection/           검사와 GOOD/NG 분기 공정
IBTM.Orchestration/                 Station 실행 순서와 전체 사이클 수명 주기
```

의존성 방향은 다음과 같습니다.

```text
IBTM Host
    ├─ IBTM.Virtual
    └─ IBTM.Orchestration
          ├─ IBTM.Stations.PcbPlacement ─┐
          ├─ IBTM.Stations.BoltFastening ├─ IBTM.Core ─ IBTM.Device
          └─ IBTM.Stations.Inspection ───┘
```

세 Station 프로젝트는 서로 참조하지 않습니다. 각 프로젝트는 자기 `Recipe`,
설정, Stage와 구체 Station 클래스를 소유합니다. IO 채널은 사용하는 Station
안에 두며, 티칭 화면에서 필요한 레이저 채널만 공개합니다. 별도 Station
인터페이스나 등록 모듈은 두지 않고 `IBTM.Orchestration`이 세 구체 Station의
실행 순서와 통합 `Recipe`를 직접 조합합니다. 인터페이스는 모션, IO, 카메라,
검사기처럼 실제 구현을 교체하는 장치 경계에만 사용합니다.
화면에서는 기존 설비 명칭인 Zone 1/2/3을 그대로 사용하지만 프로젝트 이름은
순서가 아니라 공정 역할을 나타냅니다.

카메라와 검사 결과는 Core에서 WPF 타입을 사용하지 않고 `ImageFrame`으로
전달됩니다. WPF `ImageSource` 변환은 Host의 Presentation 경계에서만 수행합니다.

## 주요 구성 요소

- `PcbPlacementStation`: 셔틀 대기, PCB 픽업·배치, 해제
- `BoltFasteningStation`: Fiducial 보정과 볼트 체결
- `InspectionStation`: 검사와 GOOD/NG 후속 처리
- `ProcessOrchestrator`: Station 실행 순서, 시작·정지, 생산 통계
- `ProcessEvents`: 단계 실행 상태와 공정 이벤트 전달
- `StationOperations`: 단일 Station의 모션과 IO 동작
- `TeachingPointMapper`: 레시피와 화면 티칭 좌표 간 변환

통합 `MachineConfig`는 시작 시 한 번 읽어 같은 인스턴스를 화면과 Station에
주입합니다. 한 항목만 감싸던 PCB 모션 설정은 바로 노출하고, 언어 설정과 실제로
쓰이지 않던 가감속·소프트 리밋·홈 오프셋 설정은 제거했습니다. 설정과 레시피는
현재 모델로 바로 역직렬화하며, 알 수 없는 예전 형식을 추정해 보정하지 않습니다.
Core에는 Zone별 레시피, IO 주소, 단계 목록이 없습니다.

등록되지 않은 Stage, 지원하지 않는 티칭 모드·검사 결과, 초기화되지 않은
모션 축은 기본값으로 대체하지 않고 즉시 실패합니다. 파일 IO처럼 사용자가 복구할
수 있는 외부 오류와 출력 해제만 경계에서 처리합니다.

공정 계층의 좌표와 속도 단위는 `mm`, `mm/s`입니다.

## 실행

```powershell
dotnet restore IBTM.slnx
dotnet build IBTM.slnx --configuration Release
dotnet run --project IBTM/IBTM.csproj
```

현재 실행 구성은 `IBTM.Virtual`의 모션·IO·카메라·Fiducial·검사·볼트
구현을 사용합니다. 실제 장치를 연결할 때는 `IBTM.Device`와 각 Station의 공개
인터페이스를 구현하고 DI 등록만 교체하면 됩니다.
