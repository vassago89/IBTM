# 스테이션 실행 구조

큰 실행 단계를 선택하고, 단계 안의 장치 동작은 `await`로 순서대로 실행한다.
센서가 변할 때마다 실행 중인 단계를 다시 선택하지 않는다. 피드백은 동작 완료 확인과
인터록에 계속 사용하며, 해당 단계가 끝난 뒤 다음 동작을 결정한다.

## 세 가지 정보

| 정보 | 역할 | 수명 |
| --- | --- | --- |
| `AutoUnit.Step` | 현재 실행·대기 단계. 화면 표시와 실행 경계 확인 | 현재 Run 동안, 종료 시 활성 단계 없음 |
| I/O·Motion 피드백 | 실제 위치·구동·그립·준비 조건 | 장치에서 관측한 현재 값 |
| 작업·인계 정보 | 대상 PCB, 결과 소유권, 미완료 인계 | 각 작업/인계가 끝날 때까지 |

`Step`만으로 물리적 도착이나 인계 완료를 인정하지 않는다. Repeat 복귀도 진행 중인
단계가 끝났는지와 실제 RUN·캐리어·지지대 피드백을 함께 확인한다. STOP은 실행을 취소하고,
미완료 인계나 이미 수집한 결과까지 지우지는 않는다. 볼트·검사의 실행 포인트는 다음
START에서 처음부터 선택한다. 그립이나 인계가 애매하면 현재 피드백과 보존된 소유권을
확인하고, 확인할 수 없는 상태를 자동으로 성공 처리하지 않는다.

## 파일과 실행 순서

- `Station.cs`: 생성자·의존성·피드백 속성·변경 이벤트·작업/인계 정보.
- `.Automatic.cs`: 자동 실행 진입점, 다음 단계 선택, 단계 내부의 순차 동작.
- `.Motion.cs`: 수동/자동에서 사용하는 실제 모션·I/O 동작과 인터록.
- `.Transfer.cs`: 인계 순서가 큰 검사/메인 컨베이어의 집기·놓기·스테이션 이송.
- `.Repeat.cs`: 검사·컨베이어의 반복 복귀 동작. 공급·장착의 Repeat 단계는 `.Automatic.cs`의 같은 switch에서 처리한다.
- `.Vision.cs`: 검사 촬영·조명·영상 처리.

공급·장착·볼트·검사·메인 컨베이어·NG 컨베이어의 실행 진입점은 아래 형태로 통일한다.

```csharp
BeginRun();
try
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var step = GetNextStep(/* 현재 작업·모드·피드백 */);
        if (!await ExecuteStepAsync(step, /* 작업 인자 */, cancellationToken))
            await WaitForChangeAsync(cancellationToken);
    }
}
finally
{
    // 해당 장치의 출력 정지·구독 해제
    EndRun(cancellationToken);
}
```

`GetNextStep`은 단계 선택이며 장치 명령이나 단계 확정을 하지 않는다.
`ExecuteStepAsync`는 각 스테이션의 구체적인 실행 경계다. `EnterStep`으로 실행 단계를
확정한 뒤 `switch`와 명시적인 `await`로 동작한다. 반환값 `false`는 외부 조건을
기다려야 한다는 뜻이며, 다음 대기는 `RunAsync`가 담당한다. 동작 도중 필요한
인계·장치 완료 대기는 그 동작 안에 유지한다. 동작을 끝내기 전에 다음 단계를 실행하지 않는다.

볼트·검사는 준비 단계에서 이번 Run의 대상 목록과 결과 소유 작업을 잡는다. 실행 호출 한 번에
포인트 하나를 처리한 뒤 루프로 돌아와 다음 단계를 선택한다. 검사는 PCB 1의 Data Matrix·볼트를
모두 처리한 다음 PCB 2로 진행한다. 티칭 미완료 대기도 같은 단계 선택 경로를 사용한다.
대상 목록은 완료/STOP 때 버리며, 캐리어 이탈 감시는 포인트 사이에서도 유지한다.
검사 지지대 준비 역시 `RunAsync`에서 직접 움직이지 않고 선택된 단계에서 실행한다.

공급·장착 Repeat의 역인계도 같은 `GetNextStep`/`ExecuteStepAsync` switch에서 선택·실행한다.
별도의 Repeat 실행 루프나 중복 State는 없다. 공급 준비 → 반환 PCB 제시 → 그립 확인 → 해제·이탈
→ 공급 복귀 → 정방향 재인계 순서이며, 상대 유닛 대기는 루프에 반환한다.
장착의 Repeat 작업 정보에는 결과 소유 Job과 대상 HeatSink만 보관한다.
STOP으로는 이 정보를 지우지 않고 원래 캐리어에 다시 놓았을 때 해제한다.
공급의 반환 슬롯도 함께 유지하며, 재시작 때 현재 그립과 원래 캐리어를 다시 확인한다.
Disabled는 화면·대기 표시로만 사용하며 미완료 인계 단계를 덮어쓰지 않는다.
출력 정지·이벤트 구독 해제 등은 각 장치 경계의 `finally`에서 처리하고,
`EndRun`에서 실행 단계 표시와 공통 이벤트 구독을 정리한다.

`GetNextStep` / `GetNextTransferStep`은 다음 동작 선택이다. 진행 중 동작을 설명할 때는
`Step`을 사용한다. 단계 저장은 `AutoUnit.SequenceStep` 하나다. 공급·장착의 형식이 있는
`State`도 이 값을 사용하며 별도 필드나 `Step` 재정의를 두지 않는다. `Step`은 실행 중에만
노출된다. 공급·장착은 미완료 인계를 위해 `BeginRun`에 기존 단계를 명시적으로 전달한다.
볼트·검사·컨베이어는 `BeginRun()`으로 시작하여 이전 실행 단계를 이어가지 않는다.
보존된 단계는 소프트웨어 이력이며 인계 위치·그립 등 실제 피드백을 대신하지 않는다.

단계 전환은 `.Automatic.cs`와 `.Repeat.cs`의 명시적인 시퀀스에서 수행한다.
`PrepareHandoffAsync` / `PrepareReceiptAsync`는 이동 후 인계 준비를 확정하는 시퀀스 진입점이다.
티칭의 `MoveToTeachingPositionAsync`와 `.Motion.cs`의 축 이동은 시퀀스 단계를 변경하지 않는다.

공통 기반인 `AutoUnit`에는 실행 수명·변경 대기·단계 통지만 둔다. 장치 호출, 분기,
인계 판단은 각 스테이션에 명시한다. 피더처럼 감지와 보충만 하는 유닛에 별도의
단계나 범용 실행기를 추가하지 않는다. 결과 저장은 기존 저장 관리 객체가 담당한다.

## 캐리어 작업과 장치 책임

별도 `StationWork` 계층은 두지 않는다. `ConveyorStation.Job.cs`가 기존 S1/S2/S3
지지대 객체에 Job 식별, 결과, 완료 여부와 결과 인계를 보관한다. 새 캐리어가 감지되면
새 Job을 만들고, 이전 Job의 결과 쓰기와 완료 요청은 거부한다. STOP은 수집한 결과를 지우지 않는다.

Enabled와 공정 준비 조건은 `PcbPlacer`, `BoltFasteningStation`, `InspectionStation`이 판단한다.
검사 요청·착좌 요청·NG 인계 소유권·공유 축 및 픽업 인터록은 `InspectionStation`에 있다.
메인 컨베이어는 S1/S2의 `ConveyorStation`과 `InspectionStation`을 직접 참조한다.
UI와 결과 저장도 각 스테이션의 `Station`을 사용하며 별도의 Work DI 등록이나 표시용 복사본은 없다.

검사 스테이션 생성 시 NG 컨베이어에 인계 피드백을 한 번 연결한다. 피드백이 연결되지 않은
상태는 픽업 간섭 해제로 판단하지 않는다. 검사 변경은 NG 실행 대기만 깨워서 양쪽 Changed 알림이 순환하지 않게 한다.
