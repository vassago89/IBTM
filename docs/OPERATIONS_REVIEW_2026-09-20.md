# 운영 검토 — 2026-09-20

기준 커밋: `9ebdaa5`. 검토 시 기존 미추적 파일 `IBTM-update.bundle`은 건드리지 않았다.
제품 소스는 수정하지 않았다. 초기화·START/STOP/RESET/HOME, 수동 조작,
PCB 공급·안착·체결·검사, 메인/NG 이송·Repeat, 피드백 수집, 설정·레시피 저장,
종료·로그의 주요 운영 경로를 소스 중심으로 검토했다.

사용자 검토 반영 후 수정 대상으로 남은 문제는 S3 도착 후 체결 NG 결과 유실 1건이다.
센서가 없는 구간의 위치·점유 판단은 사용자가 수용한 운영 제약으로 지적에서 제외했다.
아래 재현은 Virtual 구성과
가상 I/O만 사용했으며, 실장비 앱 실행이나 네이티브 장비 호출은 하지 않았다.

## 1. [P1] S3 밀착 중 정지하면 체결 NG 결과가 목적지에서 사라진다

- 위치: `Stations/IBTM.Conveyor/MainConveyor.Transfer.cs:132–143`
- 조건: S2에서 NG 체결 결과를 가진 캐리어를 S3로 이송하고, S3 HS2가 감지된 뒤
  `CarrierStopDelaySeconds` 대기 중 STOP한다.
- 원인: `departingJob`은 해당 호출의 지역 변수이고, 결과 인계는 밀착 대기가 끝난 후에만
  수행된다. 취소되면 `TransferAssembliesTo`를 거치지 않는다. S3는 감지 시 생성된 새 Job을 유지한다.
- 영향: 이후 S3 비전 검사를 정상 완료하면 이전 체결 NG가 최종 결과에 반영되지 않는다.
  `HeatSinkAssembly.Result`는 체결 결과가 Pending이어도 비전 결과 OK를 반환하고,
  `InspectionWork.RouteToNg`는 false가 된다. 정상 배출 조건이 충족되면 불량을 후단으로 보낼 수 있다.
- 재현: 실제 이송을 가상 I/O로 실행해 밀착 중 정지한 뒤, 검사 성공 결과를 기록했다.
  원래 S2 Job은 NG인데 S3의 다른 Job은 `RouteToNg=False`였다.
  카메라와 후단 배출 전체 경로까지 실행한 테스트는 아니다.
- 수정 방향: S3 도착 센서로 감지된 캐리어의 체결 결과가 밀착 중 취소로 유실되지 않도록
  결과 인계 시점과 소유권을 보존한다. 센서가 없는 이송 구간의 점유 추적은 이 수정 범위에 포함하지 않는다.

## 검토에서 제외한 운영 제약

사용자 지시: “센서가 없는대서는 어쩔수 없어. 무시”

- 기존 2번: 메인 컨베이어의 센서 사이 정지 후 반입 판단.
- 기존 3번: NG 컨베이어의 센서 사이 정지 후 개수·수신 가능 판단.

두 항목은 결함 및 수정 대상에서 제외한다. 이를 이유로 이송 이력에 따른 추가 차단이나
수동 확인·RESET 요구를 도입하지 않으며, 관련 기존 테스트의 기대도 유지한다.

## 검증 결과와 범위

`dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual`에 다음 필터를 지정했다.

```text
FullyQualifiedName~ReviewProbe_|FullyQualifiedName~ActiveTransferKeepsOriginalResultsWhenSourceGetsAnotherCarrier|FullyQualifiedName~TopLevelAdmissionWaitsForCancelledOwnerAndChildCleanup|FullyQualifiedName~MachineStopClearsBothIoBoltStartsEvenWhenOneWriteFails|FullyQualifiedName~SoftwareResetCannotClearAnActiveAutoSafetyFault
```

- 최초 검토 시 16건 실행: 기존 회귀 13건 통과, 검토용 재현 3건 실패.
- 이 중 센서 없는 구간에 추가 차단을 기대한 2건은 사용자 지시에 따라 결함 판정에서 제외하고
  보관 재현 소스에서도 제거했다. 남은 1건은 S3 도착 후 체결 NG 결과 유실 재현이다.
  컴파일 오류나 기존 회귀 실패는 없었다. 이번 보고서 정정으로 테스트를 재실행하지 않았다.
- 정상 이송의 결과 인계, 취소 후 정리 완료까지 실행권 유지, 하나의 STOP 쓰기 실패 시
  나머지 체결 START 출력 정지, 활성 안전 입력의 RESET 차단은 선택한 기존 테스트에서 통과했다.
- 전체 `MachineFlow`/lifecycle suite는 실행하지 않았다. 배선·극성·공압·실제 정지 거리와
  기구 간섭에 대한 장비 검증을 대신하지 않는다.

재현 소스는 [OperationsReviewProbes.cs.txt](reviews/2026-09-20/OperationsReviewProbes.cs.txt)에 보관했다.
테스트 프로젝트에는 실패하는 임시 파일을 남기지 않았다. 재현할 때 해당 파일을
`Tests/IBTM.Virtual.Tests/OperationsReviewProbes.cs`로 복사하면 기존 테스트 헬퍼를 사용해 실행할 수 있다.

현재 센서로 재시작한다는 운영 정책을 유지한다.
