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
| 체결기 Head 1 / 2 제어 출력 | `Stations/IBTM.BoltFastening/IoBoltHardwareSettings.cs` |
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
| 메인 입구 | DI-128 | 56 |
| ST2 Heat Sink 1 / 2 | DI-12E / DI-12F | 62 / 63 |
| ST3 Heat Sink 1 / 2 | DI-135 / DI-136 | 69 / 70 |

원본의 메인 출구 센서 DI-134(채널 68)는 현재 프로그램에서 사용하지 않는다.
캐리어 배출은 후단 Ready OFF 감지 후 `RearSmemaOffDelaySeconds`만큼 더 구동하고 멈춘다.

Supply IPM은 DO-108(채널 24) ON으로 전진, OFF로 후진한다. 별도 후진 출력은 사용하지 않는다.
후진 센서 주소는 현장 확인 항목이며 기본값은 -1(미지정)이다.
DI-109는 그리퍼 열림이며 IPM 후진에 함께 사용할 수 없다.
입력창은 unknown으로 표시하고, 전진 출력을 껐다는 이유만으로 후진 완료로 취급하지 않는다.
Settings에서 확인된 후진 센서의 **10진수 채널**을 지정하고 저장·재시작한다.
Virtual은 실린더 동작을 모사하므로 가상 인계 성공이 실제 주소 확인을 대신하지 않는다.

위 주소는 새 설정의 기본값이다. 저장된 DB의 주소·출력 방향·피드백은 자동으로 변경하지 않는다.

## 컨베이어 Normal Speed / 픽업 체결기 테이블

2026-09-19 추가. 원본 `IO MAP-260913 확인` 시트의 39·40·42·43·64·76행 기준이다.

| 신호 | 엑셀 주소 | 프로그램 채널 | 코드 |
| --- | --- | --- | --- |
| 메인 컨베이어 Normal Speed | DO-12E | 62 | `MainConveyorNormalSpeed` |
| NG 컨베이어 Normal Speed | DO-13A | 74 | `NgConveyorNormalSpeed` |
| PST1 TABLE 하강 / 상승 출력 | DO-115 / DO-116 | 37 / 38 | `PickupTableDown` ON / OFF |
| PST1 TABLE 하강 / 상승 감지 | DI-118 / DI-119 | 40 / 41 | `PickupTableDown` / `PickupTableUp` |

INPUTS / OUTPUTS 및 Settings의 I/O Mapping에 표시한다.
테이블은 기존 복동 실린더와 같이 하강·상승 출력을 한 행으로 묶고 두 감지 입력을 연결한다.
OUTPUTS의 ON은 하강, OFF는 상승이며 감지 상태는 DI로 표시한다.
Normal Speed는 초기화 시 ON하며, OUTPUTS에서 조작해도 ON을 유지한다.
픽업 테이블은 자동 픽업 전에 내려가고 슈팅 체결 중에는 상승 피드백을 확인한다.
기존 설정을 읽을 때 위 DI 2개·논리 DO 3개가 없으면 기본 주소로 추가한다.
이미 저장된 주소는 유지하며 DB 반영은 Save Settings에서 한다.

## 볼트 체결기 제어

실장비는 `I/O control + ADC results`를 사용한다. Virtual은 개발용이며,
기존 DB의 `Io` 타입도 현재 I/O 제어 + ADC 결과 방식으로 읽는다.

| 대상 | 출력 PRESET 1/2/3 / START / FWD-BWD / LOCK / RESET |
| --- | --- |
| Pickup (Head 1) | DO-140~146 (80~86) |
| Shooting (Head 2) | DO-147~14D (87~93) |

기존 READY / ALARM / FASTEN 입력 6개(채널 11~13, 93~95)는 사용하지 않는다.
현재 상태·알람·결과는 ADC로 읽으며, 제어 출력은 OUTPUTS와 Settings에서 확인한다.

- START는 ON 유지로 운전하고 OFF로 정지한다. 설비 STOP은 두 START를 모두 OFF한다.
- 프리셋 1·2·3은 해당 출력 하나만 ON한다. RESET은 I/O 출력을 100ms ON한 뒤 OFF한다.
- ADC 공통 모니터가 상태를 수집한다. 이번 START 이후 RUN ON → OFF를 확인한 뒤 결과를 한 번 읽고,
  시작 전 이벤트 번호와 다른 완료 결과인지 확인한다. 처음부터 RUN OFF인 상태는 완료가 아니다.
- 결과 수신·타임아웃·취소 후 START를 OFF하고 새 상태 표본을 받는다. 이 단계에서 RUN OFF가 될 때까지 반복 대기하지는 않는다.
  다음 프리셋 선택은 Alarm=0, READY=ON, RUN=OFF 및 START OFF를 확인한다.
- 해당 피더 OFF 또는 Repeat에서는 헤드 하강 출력 후 설정 시간(기본 2초)이 지나면 START를 OFF한다.
  결과는 `DryRun`, 토크는 미측정(null)으로 남긴다.

오류 기록과 다음 볼트 전 RESET 처리는 [ADC 오류 코드와 운전자 조치](ADC_ERRORS.md)를 참고한다.

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
  메인 입구 센서는 유지하며 출구 센서는 사용하지 않는다.
- 정방향 안착의 추가 구동 시간은 목적지 Heat Sink 2 감지부터 시작한다.
  Heat Sink 1만으로 타이머를 시작하지 않는다. [안착·재시작 조건](STATION3_COMMISSIONING.md)을 참고한다.
- AirPressureHigh 입력 ON은 공압 정상이다. 발생했던 알람의 래치는 별도로 확인한다.
- 각 스테이션의 BackupPlateUp / StopperUp 출력 ON은 상승 명령이다.
- MainConveyorForward ON은 메인 벨트 정방향이다. NG는 Reverse 명칭을 쓴다.
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
위에 명시한 새 신호 추가 외에는 자동 보정하지 않는다. [설정과 저장](SETTINGS_STORAGE.md)을 참고한다. 매핑 저장 후 재시작한다.
원본 엑셀 파일과 실장비 DB는 이번 문서 정리에서 수정하지 않았다.

메인·NG 컨베이어의 Normal Speed 출력은 초기화 시 ON하며 운전·정지·RESET에서 유지한다.
Run 출력과 독립적이며, OUTPUTS에서 조작해도 ON을 유지한다.
