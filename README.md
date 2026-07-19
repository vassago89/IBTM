# IBTM

3개 공정 구간을 제어하는 .NET 10 WPF 애플리케이션입니다.

- Zone 1: PCB 픽업 및 패드 배치
- Zone 2: Fiducial 보정 및 볼트 체결
- Zone 3: 비전 검사 후 GOOD 배출 또는 NG 적재

## 프로젝트 구조

```text
IBTM/
├─ App.xaml(.cs)                 애플리케이션 시작점
├─ Composition/                  의존성 등록과 실행 구성
├─ Domain/                       공정 설정, 레시피, 상태 값
├─ Application/
│  ├─ Abstractions/              외부 장치·기능 포트
│  └─ Process/                   공정 흐름과 기계 동작
├─ Infrastructure/
│  ├─ Persistence/               레시피 저장
│  ├─ Simulation/                시뮬레이션 구현
│  └─ Vision/                    비전 기능 구현
└─ Presentation/
   ├─ Converters/                WPF 값 변환기
   ├─ Localization/              다국어 리소스 접근
   ├─ Mappers/                   화면 편집 모델 매핑
   ├─ Models/                    화면 상태 모델
   ├─ Resources/                 공통 스타일
   ├─ Shell/                     메인 윈도우
   ├─ ViewModels/                화면 상태와 명령
   └─ Views/                     WPF 화면

IBTM.Device/
├─ Abstractions/                 모션·IO 계약
├─ Adapters/                     Ajin, Basler, MOVS 어댑터
├─ Simulation/                   가상 모션·IO
└─ Vendor/                       제조사 SDK 선언과 바이너리
```

참조 방향은 `Presentation → Application → Domain`을 기본으로 하며,
`Composition`이 장치 및 인프라 구현을 연결합니다. `IBTM.Device`는 별도 프로젝트로
분리해 WPF 화면 코드와 장치 SDK 의존성이 직접 섞이지 않도록 했습니다.

## 주요 구성 요소

- `ProcessOrchestrator`: 전체 공정 시작, 중지, 완료 수명 주기
- `Zone1Workflow` / `Zone2Workflow` / `Zone3Workflow`: 구간별 공정 실행
- `ProcessStageRunner` / `ProcessEventHub`: 단계 실행과 공정 이벤트 전달
- `MachineOperations`: 모션과 IO를 조합한 기계 동작
- `BoltTighteningService`: 볼트 컨트롤러 실행과 재시도
- `TeachingPointMapper`: 레시피와 화면 티칭 좌표 간 변환

공정 계층의 좌표와 속도 단위는 `mm`, `mm/s`입니다. Ajin 어댑터 경계에서만
컨트롤러 단위인 `μm`로 변환합니다.

## 실행

```powershell
dotnet restore IBTM.slnx
dotnet build IBTM.slnx --configuration Release
dotnet run --project IBTM/IBTM.csproj
```

기본값은 시뮬레이션 모드입니다. 실제 모드에서는 Fiducial 카메라, 검사 카메라,
볼트 컨트롤러 구현을 DI에 등록해야 합니다. 구현 없이 실제 모드로 시작하면 가상
결과를 실제 결과처럼 사용하지 않고 명시적으로 실패합니다.
