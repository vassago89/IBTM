# IBTM

인라인 조립 설비 제어 프로그램. .NET 10 / WPF.

## 바로 작업하기

- [개발 안내: 수정할 파일, 디버깅 순서, 검증 명령](docs/DEVELOPMENT.md)
- [Inspection 티칭: FOV 하나에 볼트 또는 Data Matrix 하나](docs/INSPECTION_TEACHING.md)
- [Station 3 / NG / Repeat 현장 확인](docs/STATION3_COMMISSIONING.md)
- [IO 주소와 극성 확인](docs/IO_MAP.md)
- [설정·레시피 저장과 백업](docs/SETTINGS_STORAGE.md)
- [볼트 모델 학습](Stations/IBTM.Inspection.Training/README.md)
- [기구 배치 참고](docs/MACHINE_LAYOUT.md)

## 프로젝트

| 위치 | 책임 |
| --- | --- |
| `IBTM/` | 시작 프로젝트, WPF 화면, 장비 조립과 운전 조율 |
| `Stations/` | 유닛별 동작·피드백·설정 |
| `Hardware/` | AJIN, AlphaMotion, Hik, Hantas, Virtual 드라이버 |
| `Shared/` | 공통 데이터, 장치 계약, 저장소 |
| `Tests/` | 장치 SDK 대역과 Virtual 회귀 검사 |

화면은 장치가 읽은 상태에 바인딩한다. 실제 위치·실린더 완료·운전 준비 여부는
현재 IO/SDK 피드백으로 판단한다. 명령 이력이나 초기화 성공 여부로 대신하지 않는다.
작업 목적지, 취소 수명, 미수집 검사 결과 등 소프트웨어가 소유해야 하는 이력은 유지한다.

## 실장비 없이 실행

Visual Studio에서 시작 프로젝트는 **IBTM**, 구성과 실행 프로필은 모두 **Virtual**로 선택한다.

```powershell
dotnet run --project IBTM/IBTM.csproj -c Virtual --launch-profile Virtual
```

Virtual은 별도 출력 폴더와 DB를 사용하며 장치 드라이버도 Virtual로 고정한다.
첫 실행 때만 데모 데이터를 만들고, Home/Start는 자동 실행하지 않는다.
Debug/Release 실행은 실장비에 연결될 수 있다.

검증은 변경한 경로만 한다. `dotnet test`가 의존 프로젝트까지 빌드하므로 별도 전체 빌드를
겹쳐 하지 않는다. 긴 `Category=MachineFlow` 검사는 전체 경로 검증이 필요할 때만 실행한다.
Virtual 통과는 배선·극성·공압·기구 간섭의 실장비 검증을 대신하지 않는다.
