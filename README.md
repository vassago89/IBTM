# IBTM

인라인 조립 설비 제어 프로그램. .NET 10 / WPF.

## 바로 작업하기

장비 코드 수정·디버깅은 `IBTM.slnx`를 연다. 테스트 프로젝트는 `Tests/IBTM.Tests.slnx`로 분리했다.
SDK 대역 테스트는 드라이버 소스를 다시 컴파일하므로, 장비용 솔루션에 함께 넣으면 같은 파일이
실제 SDK와 테스트 스텁을 참조하는 두 프로젝트 문맥으로 열린다.

- [개발 안내: 수정할 파일, 디버깅 순서, 검증 명령](docs/DEVELOPMENT.md)
- [Inspection 티칭: FOV 하나에 볼트 또는 Data Matrix 하나](docs/INSPECTION_TEACHING.md)
- [납품 전 공통 확인 / Station 3 / NG 현장 확인](docs/STATION3_COMMISSIONING.md)
- [IO 주소와 극성 확인](docs/IO_MAP.md)
- [설정·레시피 저장과 백업](docs/SETTINGS_STORAGE.md)
- [밝은 면적 비율 검사](Stations/IBTM.Inspection/README.md)
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

## 실장비 없이 검증

가상 장치는 테스트 프로젝트에서만 주입한다. 앱에는 가상 실행 프로필이나 드라이버 선택이 없다.
`Virtual`은 테스트용 빌드 구성 이름이며, 앱을 이 구성으로 실행해도 가상 장치로 전환되지 않는다.
앱 실행은 실제 장치에 연결하므로 장비 없이 확인할 때는 [선택 테스트](Tests/README.md)를 사용한다.

검증은 변경한 경로만 한다. `dotnet test`가 의존 프로젝트까지 빌드하므로 별도 전체 빌드를
겹쳐 하지 않는다. 긴 `Category=MachineFlow` 검사는 전체 경로 검증이 필요할 때만 실행한다.
Virtual 통과는 배선·극성·공압·기구 간섭의 실장비 검증을 대신하지 않는다.
