# ADC 체결기 프로토콜 검토 — 2026-09-14

## 결론

**STOP 후 RUN OFF 확인, 작업별 프리셋·방향 대조, RTU 프레임 간격을 반영했다.**
아래 문제는 수정 전 가상 버스 또는 테스트용 응답으로 재현했고, 수정 후 관련 회귀 테스트로 확인했다.
실제 컨트롤러 모델·펌웨어와 수신 프레임은 아직 확인하지 않았다.

## 1. 수정 — STOP 응답 후 실제 RUN OFF 확인

- 위치: `Hardware/IBTM.Hantas/AdcBoltHead.cs`, `StopAfterOperationAsync`.
- 수정 전에는 `4003 = 0` 쓰기 응답만 기다려, RUN이 ON인 테스트용 버스에서도 성공을 반환했다.
- 이제 `WaitForStoppedAsync`에서 현재 `3307 Motor RUN`을 50 ms 간격으로 읽는다. 기존 응답 제한 시간(`ResponseTimeoutMilliseconds`, 기본 1,000 ms) 안에 OFF가 확인돼야 반환한다.
- 피드백 누락·정지 미완료는 성공으로 반환하지 않고 미수집 결과를 보존한다. 원래 작업 실패와 STOP 실패가 겹치면 두 오류를 함께 전달한다.
- Reset과 미수집 결과 회수에도 같은 확인을 적용했다. 결과 회수 시 이미 완료 레코드를 받았다면, 취소가 들어와도 제한 시간 내 정지 확인과 결과 반환을 마친다. 상위 시퀀스는 원래 캐리어·볼트·공정에 기록한다.

## 2. 수정 — 중단 전 프리셋을 보관하고 재개 조건 대조

- 위치: `AdcBoltHead.SelectPresetAsync`, `TightenAsync`.
- 수정 전에는 이벤트 번호만 보관해, 프리셋 3으로 시작한 작업을 중단한 뒤 7로 바꿔도 그대로 재개했다.
- `_pendingFastening`에 시작 전 이벤트 번호와 요구 프리셋을 함께 보관한다. 시작 직전 현재 프리셋과 다르면 START를 보내지 않고 오류를 낸다.
- 프리셋 선택 직후와 실제 시작 사이의 전면 조작도 확인하도록 선택한 요구값을 보관한다. 이 값은 명령 조건이며 실제 장비 상태로 사용하지 않는다.
- 불일치 시 미수집 결과를 유지한다. 원래 프리셋으로 복원하거나 기존 작업 복구 절차에서 해당 결과를 명시적으로 처리해야 한다.

## 3. 수정 — 설정 readback과 수신 결과의 프리셋·방향 검증

- 위치: `AdcBoltHead.IsCompleted`, `Complete`, `SelectPresetAsync`.
- 수정 전에는 이벤트 번호가 바뀐 OK/NG 결과라면 다른 프리셋·역회전 결과도 수락했다.
- 프리셋 변경 후 현재값을 다시 읽고, 정·역회전 모두 방향 설정 뒤 현재 방향과 Ready/RUN을 확인한 다음 START를 보낸다. 같은 프리셋 선택도 현재 Ready/RUN 조건을 확인한다.
- 체결 결과는 이벤트 번호·OK/NG에 더해 요구 프리셋과 정회전 여부를 대조한다. 불일치 결과는 오류로 알리고 해당 볼트의 OK/NG로 기록하지 않는다.
- 기존 컨트롤러 Error 처리는 유지한다. 별도 상태 머신이나 프로토콜 프레임워크는 추가하지 않았다.

## 4. 수정 — 버스에서 RTU 프레임 간격 적용

- 위치: `Hardware/IBTM.Hantas/AdcBus.cs`, `ExchangeAsync`.
- 수정 전에는 요청 중첩만 막았고, 프리셋·방향·START 연속 명령에 프레임 간격이 없었다.
- 공개된 [MDC/ADC COM Protocol Rev. 1.3, p.4](https://www.mountztorque.com/core/media/media.nl?c=456127&h=xjrm4kM4rm6a1zBowbMrJLeB3XsNtA-CuQGx33V2pl4g0Ptc&id=5743761)는 프레임 사이 최소 3.5 문자 시간의 무통신 간격을 요구한다.
- 공유 버스 잠금을 가진 상태에서 매 송신 전 `max(2, ceil(35000 / baudRate))` ms를 기다린다. 현재 8N1 기준 3.5 문자 시간이며, 9,600 bps는 4 ms, 115,200 bps는 2 ms다.
- 이 원인으로 실장비에서 통신 오류가 발생했다는 판단은 아니다. 실제 시리얼 선로의 간격은 측정하지 않았다.

## 확인한 기존 처리

- 16비트 레지스터는 상위 바이트부터 송수신하고, CRC는 하위 바이트부터 기록한다.
- 응답 주소·기능 코드·CRC, 읽기 바이트 수와 쓰기 echo를 검사한다.
- 부분 수신은 필요한 바이트가 모일 때까지 읽고, 검증 전 원시 수신 로그를 남긴다.
- 버스 요청은 세마포어로 직렬화하며, Windows 네이티브 시리얼 취소 후 진행 중 I/O가 끝나기를 기다린다.
- 작업 실패 후에도 STOP을 시도하고 양쪽 실패를 함께 전달한다.
- 스테이션은 미수집 결과의 캐리어·히트싱크·볼트·공정 귀속을 보관한다. 이 소유권은 유지해야 한다.

## 검증

수정 검증은 Virtual 구성에서 직접 영향받는 테스트 **25개 통과**(새 회귀 9개, 기존 16개).

- 새 회귀: STOP 응답 후 RUN 지연·미정지·피드백 누락, 정지 실패 뒤 결과 회수, 중단 후 프리셋 변경, 결과 프리셋·방향 불일치, 설정 readback 실패.
- 기존 회귀: 현재 Ready/RUN, 연결 설정 적용, 중단·타임아웃 결과 보존, 작업/STOP 오류 보존, 컨트롤러 알람, 역회전 취소·통신 실패, 준비 실패와 시리얼 취소 정리.
- 캐리어·볼트·공정별 결과 귀속 테스트도 포함한다. 복구 결과 수신 순간 취소가 들어오는 경우 발생한 회귀를 수정하고 해당 3개 경우와 새 회귀 9개를 다시 통과했다.
- 관련 테스트가 의존 프로젝트를 빌드했다. 전체 MachineFlow와 실장비 프로그램은 실행하지 않았다.

수정 전 별도 재현 기록: `artifacts/adc-protocol-review/Program.cs`. 아래 출력은 수정 전 동작이며, 현재 회귀 검증은 `Tests/IBTM.Virtual.Tests/AdcProtocolTests.cs`(통신)와 `Tests/IBTM.Virtual.Tests/AdcBoltHeadTests.cs`(헤드 동작)에서 수행한다. 실행 범위는 `Tests/README.md`를 참고한다.

```text
RESUME_PRESET: originally requested 3; resumed preset=7, accepted success=True
RESULT_METADATA: requested preset=3/fastening; result preset=7/loosening, accepted success=True
STOP_FEEDBACK: STOP acknowledged, returned success=True, motor still running=True
```

실장비 적용 전 모델·펌웨어에 맞는 레지스터 정의와 프리셋/방향 변경 직후 상태 갱신 시점을 대조해야 한다.
슬레이브 주소·통신 설정은 이번 검토에서 임의로 변경하지 않았다.
