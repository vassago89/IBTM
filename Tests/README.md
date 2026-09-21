# 테스트 실행

장비를 실행하지 않고 `Virtual` 구성 또는 SDK 대역으로 검증한다. 변경한 동작의 테스트만 선택하고, 같은 테스트를 Debug/Release로 반복 실행하지 않는다. `dotnet test`가 의존 프로젝트도 빌드하므로 별도 전체 빌드는 필요 없다.

## 볼트 테스트 구성

| 파일 | 검증 대상 |
| --- | --- |
| `IBTM.Virtual.Tests/AdcProtocolTests.cs` | 시리얼 설정, 포트 분리, 프레임·CRC, 자동 결과 수신, 읽기 취소 |
| `IBTM.Virtual.Tests/AdcBoltHeadTests.cs` | ADC START/STOP, 실제 RUN 피드백 확인, 결과 소유권, 드라이런·취소·통신 오류 |
| `IBTM.Virtual.Tests/IoBoltHeadTests.cs` | I/O START/FASTEN 신호, 드라이런·취소 |
| `IBTM.Virtual.Tests/BoltFasteningTests.cs` | 스테이션 이동 순서, 공급, 실린더 인터록, 재시작 |
| `IBTM.Virtual.Tests/MachineLifecycleTests.Feeders.cs` | 피더 사용 설정과 설비 운전 연결, 상승 피드백 인터록 |

`AdcControllerStub`은 ADC 헤드 테스트에서 응답과 RUN 피드백을 직접 지정하는 공통 대역이다. STOP 전송 횟수와 현재 운전 상태는 별개이며, 새 START 뒤에는 RUN이 다시 켜져야 한다. 정상 프레임과 가상 장비 전체 동작은 기존 `VirtualAdcBus`로 검증한다.

## 선택 실행 예

저장소 루트에서 실행한다. 의존성을 아직 복원하지 않았다면 처음에는 `--no-restore`를 생략한다.

ADC 통신 또는 헤드 드라이버 변경:

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~AdcProtocolTests|FullyQualifiedName~AdcBoltHeadTests" --verbosity minimal
```

I/O 헤드 변경:

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~IoBoltHeadTests" --verbosity minimal
```

스테이션 변경은 영향받는 메서드 이름으로 좁히거나 다음 범위를 사용한다:

```powershell
dotnet test Tests/IBTM.Virtual.Tests/IBTM.Virtual.Tests.csproj -c Virtual --no-restore --filter "FullyQualifiedName~BoltFasteningTests&Category!=MachineFlow" --verbosity minimal
```

긴 전체 경로 시뮬레이션은 `Category=MachineFlow`로 구분한다. 전체 경로 검증을 명시적으로 요청받았을 때만 선택 실행한다. 필터 없이 실행하면 이 테스트들도 포함되므로 일상적인 수정 확인에는 위처럼 범위를 지정한다.
