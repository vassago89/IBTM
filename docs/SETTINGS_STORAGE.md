# 설정·레시피 저장

## 저장 위치와 책임

| 데이터 | 위치 | 구현 |
| --- | --- | --- |
| 장비 설정, 레시피, 촬영 이미지 | 실행 폴더의 `Data/Machine.db` | `Shared/IBTM.Storage/MachineStore.cs` |
| 현재 레시피·불러오기·저장·이미지 교체 | 레시피 관리 | `Shared/IBTM.Storage/RecipeManager.cs` |
| 화면 명령·오류 표시·BitmapSource ↔ PNG | UI와 이미지 경계 | `IBTM/UI/RecipeEditor.cs` |

레시피를 사용하는 클래스에는 `RecipeManager` 싱글턴을 DI로 주입하고 `Current`에서 값을 읽는다.
레시피 데이터와 조회용 `Func`를 별도로 등록하지 않는다. 레시피 모델은 `Shared/IBTM.Storage/Recipes`에 모았다.

장치 루프는 DB를 조회하지 않는다. 호스트가 읽은 타입 있는 설정을 각 장치에 전달한다.
축/IO 매핑·속도·인계 좌표는 장비 설정, 제품별 볼트·Heat Sink·FOV/ROI·촬영 조건은 레시피다.
설비의 현재 위치나 동작 완료 상태를 설정에 저장하지 않는다.

## 설정 화면에서 운전 시간 찾기

- `Settings → Operation & Timing` 첫 항목의 **센서 감지 후 추가 구동 시간**은
  `Conveyor.CarrierStopDelaySeconds`다. 단위는 초이며, 0이면 추가 구동을 하지 않는다.
  정방향 이송 중 목적지 Heat Sink 2 감지 후 적용하며 Station 1·2·3이 같은 값을 쓴다.
  Heat Sink 2만 감지된 상태에서는 이 타이머를 시작하지 않는다.
- 같은 탭에 공통 피드백·정지 확인, 볼트 공급, 체결 완료 대기 시간을 모았다.
  이 항목들은 최대 대기 시간이며 단위는 ms다. 1,000 ms = 1초.
- `Bolt Shooting · Arrival Timing → Head Arrival Delay (s)`는 튜브 통과 감지 후
  슈팅헤드 도착을 기다리는 고정 시간이다. 기본 3초이며 소수 초 입력이 가능하다.
  진공 ON 대기 대신 사용하고, 튜브 감지 타임아웃(ms)은 별도로 유지한다.
- 사용 유닛·인터록은 `Units & Safety`, 축 설정은 `Motion`, 입출력 주소는 `I/O Mapping`,
  체결기 타입·통신 연결·카메라·조명은 `Device Connections`에서 설정한다.
- `Save Settings`로 저장한다. I/O 주소와 장치 연결 변경은 저장 후 재시작해야 적용된다.

## JSON 방식

- `Settings`: **설정 클래스 전체**를 JSON으로 저장한다. Key는 클래스 이름이다.
  IO 하나당 DB 행이나 테이블을 만들지 않는다.
- `Recipes`: 레시피 전체를 JSON으로 저장한다.
- `RecipeImages`: 이미지 번호와 원본 PNG BLOB. FOV 좌표·ROI·대상은 레시피 JSON에 둔다.
- 일반 설정/레시피 속성 추가는 DB 테이블 구조를 바꾸지 않으므로 EF migration이 필요 없다.
- DB가 없으면 `EnsureCreated`로 테이블을 만든다. 기존 파일을 임의 삭제하지 않는다.

역직렬화는 일반 `JsonSerializer.Deserialize`다.

| 소스 변경 | 기존 JSON을 읽을 때 |
| --- | --- |
| 클래스 속성 추가 | 생성자/초기값 사용 |
| 클래스 속성 이름 변경 | 옛 값은 이관하지 않고 새 속성 기본값 사용 |
| 알 수 없는 클래스 속성 | 무시 |
| 설정 섹션 없음 | 새 설정 객체 기본값 사용 |
| IO·축 이름 변경 | 고정 숫자 ID로 저장하므로 주소 유지 |
| 알 수 없는 IO·축 ID / 값 타입이 틀림 | 로드 오류. 임의 기본값 사용 없음 |

Supply의 `HandoffPosition`은 인계 XYZ를 저장한다. 회전은 `RotationZ`에서 수행하고,
인계와 다음 픽업 XY 복귀는 `HandoffPosition.Z`에서 수행한다. 이전 XY 전용 설정에는
인계 Z가 없으므로 `PCB Handoff`의 XYZ를 티칭하고 `Save`로 저장해야 한다.
옛 `BufferClearZ`는 읽을 때 무시하고 다음 저장에서 제외한다. 별도 복귀 좌표는 저장하지 않으며
PCB1·PCB2 각 픽업의 X/Y를 사용한다. `PCB 1 Pickup`과 `PCB 2 Pickup`은 각각 XYZ를
레시피에 저장한다. 공통 `CarrierY` 설정은 더 이상 사용하지 않는다.
기존 레시피에 픽업별 Y가 없으면 미티칭으로 표시하고 해당 픽업 이동을 차단한다.
각 픽업을 XYZ로 다시 티칭한 뒤 `Save`로 저장한다.
Placement의 `HandoffPosition`은 인계 대기 XYZ이며 그 Z를 XY 이동 높이로 사용한다.
실린더 Up 상태에서 PCB를 받는 Z는 별도 `ReceiveZ`로 저장한다. `PCB Receive Z`를 티칭하면
자동 저장되며, 기존 설정에 없으면 미티칭 상태로 남긴다. 옛 `BufferEntryZ`는 무시·제외한다.
인계 영역 경계 설정은 읽거나 저장하지 않는다. 기존 DB 행은 삭제하지 않는다.
인계 좌표 속성은 `HandoffPosition`이며, 기존 장비 좌표 보존을 위해 JSON 키 `BufferHandoffPosition`은 유지한다.
카메라 Live FPS와 레시피의 노출·게인 설정도 읽거나 저장하지 않는다. 카메라에 설정된 값을 그대로 사용한다.
Recipe의 이름 편집·New·Save는 Teaching 상단에 있으며, 메인 화면에서는 현재 이름과 불러오기만 표시한다.
Teaching의 저장 버튼은 `Save` 하나다. 양쪽 인계 임시값을 적용·저장한 뒤 현재 레시피를 저장한다.
레시피 이름이 비어 있거나 수동 정지 상태가 아니면 실행하지 않는다.
인계 저장이 실패하면 레시피 저장을 진행하지 않으며, 인계 저장 후 레시피 저장이 실패·취소되면
부분 저장 안내를 표시한다. 같은 `Save`로 다시 저장할 수 있다. `New Recipe`는 별도로 유지한다.

`InputIo`, `OutputIo`, `MachineAxis`의 명시적 숫자 값은 저장용 ID다. 물리 IO 주소와 별개이며,
기존 ID를 바꾸거나 재사용하지 않는다. 코드 이름·표시 문구를 바꿔도 저장 ID는 유지한다.
이전 문자열 키는 공통 JSON 변환기에서 읽는다. 이미 변경된 옛 이름은 enum의
`JsonStringEnumMemberName`으로 선언하며, 이후 저장은 숫자 ID를 사용한다.

출력 설정은 ON/OFF 주소만 저장한다. 완료 센서 연결은 각 스테이션 코드가 정의하며,
옛 JSON의 `Feedback`은 읽지 않는다. 센서의 물리 DI 주소는 계속 Settings에서 편집한다.
시작 시 DB를 수정하거나 저장된 주소·티칭값을 초기화하지 않는다.
2026-09-19 추가한 메인·NG Normal Speed와 Pickup Table DI/DO는 이전 설정에 없으면
해당 항목만 기본 주소로 추가한다. 이미 저장된 주소와 그 외 누락된 매핑은 변경하지 않는다.
추가 항목의 DB 반영은 Save Settings에서 한다. 주소는 [I/O 맵](IO_MAP.md)을 참고한다.

## 저장과 반영

Settings에서 Save하고, 하드웨어 매핑·드라이버 설정 변경 후에는 재시작한다.
체결기 타입은 `DriverSettings.Bolt`에 저장하며 기존 `HantasAdc` 값은 그대로 유지한다.
`Io` 선택 시 `IoBoltHardwareSettings`의 전용 DI/DO를 사용하고 ADC 통신을 생성하지 않는다.
IO형 주소는 별도 설정 객체로 저장하므로 ADC형으로 전환해도 편집한 주소는 유지한다.
체결기 DI/DO는 타입과 관계없이 일반 IO로 등록하며 Input·Output 창에서 항상 표시·조작한다.
코드의 기본값 수정은 이미 저장된 DB 값을 변경하지 않는다.
Settings 저장은 설비 STOP 신호로 취소하지 않으며, 실제 저장 실패는 화면과 로그에 표시한다.
티칭의 저장 메시지와 오류를 확인하고, 검사 기준·조명 밝기 변경은 Teaching의 Save로 남긴다.
티칭값 적용 후 저장 중 STOP으로 취소되면 미저장 안내를 표시한다. 편집값은 메모리에 남지만
DB에는 이전 값이 유지되므로, 같은 값을 다시 저장해야 재시작 후에도 유지된다.
FOV/ROI는 Heat Sink별로 독립적이며 캐리어 전체 맵/공유 PCB 영역은 사용하지 않는다.

이미지는 PNG BLOB으로 저장되며 레시피 이미지 교체는 트랜잭션으로 처리한다.
화면에서 쓰는 십자선·ROI 테두리는 저장 이미지 픽셀에 합성하지 않는다.

프로그램 내 DB 백업·복원 기능은 없다. 시작 시 다른 DB 파일로 교체하거나 이전 DB 사본을 만들지 않는다.

AJIN은 `AxlOpen` 후 DIO 모듈을 확인하며 .mot를 로드하지 않는다. 설정 파일 경로는 필요하지 않다.

Virtual 구성은 별도 출력 폴더와 DB를 사용한다. 데모 설정을 실장비에 복사하지 않는다.

볼트 검사는 레시피의 각 `Pcb.TaughtBolts` 항목에 BrightnessThreshold(0–255)와 MinimumBrightRatio(0–1)를 저장한다.
개별 값이 없는 기존 볼트는 `BoltInspection`의 종전 공통값을 사용한다. 편집한 볼트는 개별 값으로 저장하며 다른 볼트의 값은 바뀌지 않는다.
옛 모델의 마스크 비율은 밝은 면적 비율로 변환하지 않는다. 기존 레시피를 열면 새 항목의 기본값을 사용하므로 실제 영상으로 기준을 맞춘 뒤 저장한다.
학습·모델 프로젝트는 제거되었으며, 기존 학습 DB와 이미지 파일은 읽거나 삭제하지 않는다.
