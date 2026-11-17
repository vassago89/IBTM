# ML-CP

동도테크 `ML-CP-매뉴얼2026.pdf` 17–20페이지의 RS232C ASCII 통신을 사용한다.
`AddIbtmApplication`에 `MlcpController`를 등록하며, 포트는 `Open` 호출 시 연다.

왼쪽 `통신 테스트` → `LVDT`에서 포트·통신 속도를 지정하고 연결한다.
`측정 (START)`과 `최근 결과 (RECALL)`은 각각 새 측정과 마지막 측정 조회다.
측정값·판정과 TX/RX 원문을 표시하며, 명령 형식과 응답 제한 시간은 이 화면에서 바꾼다.
테스트 화면의 통신 조건은 임시값이다. 자동 검사는 장비 설정의 `MlcpSettings`를 적용한다.
창을 닫으면 이 창의 요청만 취소한다. 대기 중인 요청의 취소는 연결을 닫지만, 유휴 상태의 공유 연결은 유지한다.
자동 검사 중에는 연결 해제·테스트 요청·명령 형식 변경을 막는다.

```csharp
controller.Open("COM11", 9600); // 실제 연결된 COM 포트 지정
controller.ResponseTimeout = TimeSpan.FromSeconds(3);
var measured = await controller.MeasureAsync(cancellationToken); // START -> 측정 결과
var recalled = await controller.ReadLastMeasurementAsync(cancellationToken); // RECALL -> 마지막 결과
controller.Close();
```

## 장비 설정

- `Control -> Serial`: Send ON, Type ASCII, PC와 같은 Speed, POS2 Data Number
- `Commands`: ON (장비 기본값 OFF), Use Device ID OFF
- POS1 OK/NG이면 `Success`에 전체 판정을 반환한다. Start Number나 모델명을 선택하면 `Header`에 그대로 보관하고 `Success`는 null이다.
- `Point ON`이면 소수점을 포함한 값을 받는다. OFF면 받은 정수를 그대로 반환한다. 소수 자릿수·단위·Preset은 추정하거나 환산하지 않는다.
- `Group OK/NG`가 ON이면 각 측정값의 판정도 읽는다. OFF면 채널별 `Success`는 null이다.

`Measurements`는 출력 순서대로 받은 값이다. `RawFrame`에는 제어문자를 포함한 원문을 보관한다.
매뉴얼은 ETX 뒤 두 바이트를 `@@`로만 표시하며 계산법을 정의하지 않는다. 이 두 바이트를 보존하되 체크섬으로 검증하지 않는다.
HEX 출력은 데이터의 바이트 배치·스케일 설명이 없어 지원하지 않는다.

## 명령 형식과 응답 대기

기본값은 19페이지의 `START\r\n`, `RECALL\r\n`이다.
18페이지에 나온 `\rSTART\r`, `\rRECALL\r` 형식이 필요한 장비에서는
`CommandFormat = MlcpCommandFormat.CrCommandCr`로 지정한다. 명령을 중복 실행하지 않도록 자동으로 다른 형식을 재전송하지 않는다.
START·RECALL은 측정 데이터로 응답하므로 ACK만으로 완료하지 않는다.

동시 요청은 순서대로 처리한다. 외부 START나 자동 연속 송신과 동시에 사용하면 응답에 요청 번호가 없어 결과를 구분할 수 없다.
응답 시간 초과·취소·통신 오류 시 연결을 닫아 뒤늦은 결과를 다음 요청에 넘기지 않는다. 다시 사용하려면 Open을 호출한다.
대기 취소는 측정기 자체의 측정 정지 명령이 아니다.

검사 스테이션은 LVDT 하강 확인 후 `MeasureAsync`를 호출하고 상승 확인 후 다음 XY로 이동한다.
자동 검사에는 측정값 한 개를 반환하도록 설정한다. `ASCII / Point ON`, mm 단위이며 실제 높이가 높을수록 큰 값이 나오도록 측정기 방향을 맞춘다.
장비 OK/NG 대신 레시피의 최소·최대 높이로 판정한다.
가상 스트림으로 명령 바이트, 분할 수신, 판정·측정값 해석, 취소와 시간 초과를 검증한다. 실장비에서는 명령 끝문자와 실제 출력 형식을 확인해야 한다.
