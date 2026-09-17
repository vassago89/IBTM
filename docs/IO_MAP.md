# IO 번호와 극성 확인

기준: 2026-09-17, `TMED2_IO_MAP_260913(최종 IO CHECK 완료 MAP).xlsx`.
사용자 확인에 따라 **주소만 최종 엑셀을 따르고, 방향·접점 해석은 현재 코드를 유지**한다.
엑셀의 상승/하강, AUTO, DOOR OPEN, AIR LOW 표기로 기존 동작을 뒤집지 않는다.
**실행 중 Settings의 저장된 매핑이 우선**이며,
신규 DB의 기본값은 각 유닛의 `*HardwareSettings.cs`에 있다.
이전 엑셀 주소 표기는 프로그램의 현재 표시 채널 번호와 혼용하지 않는다.

## 변경 위치

| 대상 | 기본값 소스 |
| --- | --- |
| E-Stop, 도어, Air, 램프·부저 | `Shared/IBTM.Device/MachineHardwareSettings.cs` |
| 메인 벨트·SMEMA | `Stations/IBTM.Conveyor/ConveyorHardwareSettings.cs` |
| PCB 공급·Front 1 | `Stations/IBTM.PcbSupply/PcbSupplyHardwareSettings.cs` |
| Station 1 | `Stations/IBTM.PcbPlacement/PcbPlacementStationHardwareSettings.cs` |
| Station 2 | `Stations/IBTM.BoltFastening/BoltFasteningStationHardwareSettings.cs` |
| IO형 체결기 Head 1 / 2 | `Stations/IBTM.BoltFastening/IoBoltHardwareSettings.cs` |
| Station 3 | `Stations/IBTM.Inspection/InspectionStationHardwareSettings.cs` |
| NG 픽업·그리퍼 | `Stations/IBTM.Inspection/NgCarrierTransferHardwareSettings.cs` |
| NG 셔틀 | `Stations/IBTM.NgConveyor/NgShuttleHardwareSettings.cs` |
| NG 벨트·스토퍼 | `Stations/IBTM.NgConveyor/NgConveyorHardwareSettings.cs` |

화면 번호는 설정에 있는 채널을 10진수로 표시한다.
AJIN 카드/모듈 내 비트 번호와 프로그램 채널을 비교할 때는
`Hardware/IBTM.Ajin/AjinController.cs`와 AJIN 입력/출력 모듈 목록까지 확인한다.

## 260913 주소 반영

| 신호 | 엑셀 주소 | 프로그램 채널 |
| --- | --- | --- |
| Supply PCB 감지 | DI-106 | 22 |
| Supply 그리퍼 닫힘 / 열림 | DI-108 / DI-109 | 24 / 25 |
| Supply IPM 전진 감지 | DI-10C | 28 |
| 메인 입구 / 출구 | DI-128 / DI-134 | 56 / 68 |
| ST2 Heat Sink 1 / 2 | DI-12E / DI-12F | 62 / 63 |
| ST3 Heat Sink 1 / 2 | DI-135 / DI-136 | 69 / 70 |

Supply IPM은 DO-108(채널 24) ON으로 전진, OFF로 후진한다. 별도 후진 출력은 사용하지 않는다.
후진 센서 주소는 현장 확인 항목이며 기본값은 -1(미지정)이다.
DI-109는 그리퍼 열림이며 IPM 후진에 함께 사용할 수 없다.
입력창은 unknown으로 표시하고, 전진 출력을 껐다는 이유만으로 후진 완료로 취급하지 않는다.
Settings에서 확인된 후진 센서의 **10진수 채널**을 지정하고 저장·재시작한다.
Virtual은 실린더 동작을 모사하므로 가상 인계 성공이 실제 주소 확인을 대신하지 않는다.

위 주소는 새 설정의 기본값이다. 저장된 DB의 주소·출력 방향·피드백은 자동으로 변경하지 않는다.

추가 확인할 항목은 볼트체결기 `PST1 TABLE`(DI-118/119, DO-115/116)의 실제 용도와 필요 순서다.
현재 제어·완료 확인이 없으므로 체결용 지지 실린더인지 확인한 후 해당 시퀀스에 넣는다.

## 볼트 체결기 타입

Settings에서 ADC communication / IO only를 선택하고 저장·재시작한다. Virtual은 개발용이다.
ADC형은 기존 통신 제어·결과 수집을 유지하며 IO형 전용 채널을 스캔하거나 출력하지 않는다.
IO형은 COM 포트 없이 INPUTS / OUTPUTS 창에서 다음 원시 신호를 확인·조작한다.

| 대상 | 입력 READY / ALARM / FASTEN | 출력 PRESET 1/2/3 / START / FWD-BWD / LOCK / RESET |
| --- | --- | --- |
| Pickup (Head 1) | DI-00B~00D (11~13) | DO-140~146 (80~86) |
| Shooting (Head 2) | DI-14D~14F (93~95) | DO-147~14D (87~93) |

START는 ON 유지로 운전하고 OFF로 정지한다. 설비 STOP은 두 START를 모두 OFF하며,
한쪽 출력 쓰기 실패가 다른 쪽 정지 요청을 건너뛰지 않게 한다.
프리셋 1·2·3은 해당 출력 하나만 ON한다. START 전에 기존 FASTEN이 OFF인지 확인하고,
이번 START 이후 FASTEN ON → OFF를 확인하면 START를 OFF하고 결과를 OK로 기록한다.
처음부터 OFF인 입력이나 STOP으로 발생한 OFF는 정상 완료로 취급하지 않는다.
이 결과는 품질 신호 판정이 아닌 사용자 지정 `IoAssumedOk`이며 토크는 미측정(null)이다.
READY와 ALARM은 원시 표시만 하고 이 IO형 시퀀스의 운전·판정 조건에 사용하지 않는다.
설비 공통 비상정지·안전 인터록은 유지하며 RESET 펄스는 임의로 생성하지 않는다.
FASTEN ON/OFF 대기 제한은 IO형 설정의 Fastening Timeout(기본 15초)이다.
중단·타임아웃·I/O 오류 시 START를 끄고 미완료 상태를 남긴다. 임의로 재체결하지 않으며
작업자가 볼트를 확인하고 Recovery에서 완료 여부를 적용한다. 미완료로 적용하면 재체결을 허용한다.
이미 완료한 결과는 STOP 쓰기 실패 후에도
START OFF 확인 전까지 보관하므로 재운전 없이 회수할 수 있다.

## 현재 소스의 의미

- Operation 화면의 Front 1·Front 2 `TEST Available`, Rear `TEST Ready`는 티칭 스위치 ON에서만
  조작·적용하는 메모리 테스트 신호다. 티칭에서는 TEST만, 자동에서는 실제 DI만 판단한다.
  DI 표시·주소·저장 설정은 바꾸지 않는다. STOP 후에는 유지하고, 티칭 OFF나 새 프로그램
  실행 시 모두 해제한다. Front 1은 PCB 처리 완료 후 TEST를 OFF→ON해 다음
  캐리어를 모사한다. 티칭에서는 실제 Available 상태가 이 판단에 영향을 주지 않는다.
- 티칭에서는 자동 시퀀스의 전단 Ready 2개·후단 Available 출력을 내보내지 않는다.
  초기화·티칭 전환 시 기존 SMEMA 출력은 OFF한다. OUTPUTS 창은 직접 ON/OFF 조작을 유지한다.
- PCB 공급 Front 1의 상위 연동은 Available 입력과 Ready 출력이다. Ready는 입고 후에도
  PCB 1·2 확인/픽업과 마지막 운반 높이 복귀까지 ON을 유지하고, 그 뒤 OFF로 완료를 알린다.
  Available OFF 후 다음 캐리어를 받기 위해 Ready를 다시 ON한다. STOP/알람은 캐리어가
  남아 있으면 Ready를 유지하며, 입력 읽기 실패도 임의의 Ready OFF로 바꾸지 않는다.
- Station 1·2·3의 캐리어 재실은 각 위치의 Heat Sink 1 OR Heat Sink 2다.
  기존 스테이션 전용 캐리어 입력과 PCB 버퍼 입력은 사용하지 않는다.
  입구·출구 경계 센서는 유지한다.
- 정방향 안착의 추가 구동 시간은 목적지 Heat Sink 1 감지부터 시작한다.
  Heat Sink 2만으로 타이머를 시작하지 않는다. [안착·재시작 조건](STATION3_COMMISSIONING.md)을 참고한다.
- AirPressureHigh 입력 ON은 공압 정상이다. 발생했던 알람의 래치는 별도로 확인한다.
- 각 스테이션의 BackupPlateUp / StopperUp 출력 ON은 상승 명령이다.
- MainConveyorForward ON은 메인 벨트 정방향이다. NG는 Reverse 명칭을 쓴다.
  두 벨트의 Normal Speed 출력은 사용하지 않는다.
- NgCarrierPickupDown ON은 픽업 하강, OFF는 상승이다.
- NgCarrierGripperClose ON은 그리퍼 닫힘, OFF는 열림이다.
- NgShuttleDown ON은 셔틀 하강, OFF는 상승이다.
- 출력의 현재 ON/OFF와 실린더가 도착한 위치는 다르다. 완료는 연결된 DI로 확인한다.
- 피드백 없음은 OFF가 아니라 unknown이다.

번호만 바꾸는 작업과 신호 의미/극성을 바꾸는 작업은 다르다.
후자는 enum 표시명, 출력의 ON/OFF 채널, 대응 입력, 유닛 완료 조건,
Virtual 동작과 해당 회귀 검사를 같이 확인한다.
일반적인 코드 기본값 변경은 이미 저장된 설정을 덮어쓰지 않는다.
IO 키 이름이 바뀐 기존 DB는 수동으로 해당 출력 키와 피드백을 수정해야 한다.
자동 보정·구버전 변환은 없다. [설정과 저장](SETTINGS_STORAGE.md)을 참고한다. 매핑 저장 후 재시작한다.
원본 엑셀 파일과 실장비 DB는 이번 문서 정리에서 수정하지 않았다.
