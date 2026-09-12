# IO 번호와 극성 확인

기준: 2026-09-11. **실행 중 Settings의 저장된 매핑이 우선**이며,
신규 DB의 기본값은 각 유닛의 `*HardwareSettings.cs`에 있다.
이전 엑셀 주소 표기는 프로그램의 현재 표시 채널 번호와 혼용하지 않는다.

## 변경 위치

| 대상 | 기본값 소스 |
| --- | --- |
| E-Stop, 도어, Air, 램프·부저 | `Shared/IBTM.Device/MachineHardwareSettings.cs` |
| 메인 벨트·SMEMA | `Stations/IBTM.Conveyor/ConveyorHardwareSettings.cs` |
| Station 1 | `Stations/IBTM.PcbPlacement/PcbPlacementStationHardwareSettings.cs` |
| Station 2 | `Stations/IBTM.BoltFastening/BoltFasteningStationHardwareSettings.cs` |
| Station 3 | `Stations/IBTM.Inspection/InspectionStationHardwareSettings.cs` |
| NG 픽업·그리퍼 | `Stations/IBTM.Inspection/NgCarrierTransferHardwareSettings.cs` |
| NG 셔틀 | `Stations/IBTM.NgConveyor/NgShuttleHardwareSettings.cs` |
| NG 벨트·스토퍼 | `Stations/IBTM.NgConveyor/NgConveyorHardwareSettings.cs` |

화면 번호는 설정에 있는 채널을 10진수로 표시한다.
AJIN 카드/모듈 내 비트 번호와 프로그램 채널을 비교할 때는
`Hardware/IBTM.Ajin/AjinController.cs`와 AJIN 입력/출력 모듈 목록까지 확인한다.

## 현재 소스의 의미

- AirPressureHigh 입력 ON은 공압 정상이다. 발생했던 알람의 래치는 별도로 확인한다.
- 각 스테이션의 BackupPlateDown / StopperDown 출력 ON은 하강 명령이다.
- MainConveyorForward ON은 메인 벨트 정방향이다. NG는 Reverse 명칭을 쓴다.
  두 벨트의 Normal Speed 출력은 사용하지 않는다.
- NgCarrierPickupUp ON은 픽업 상승, NgCarrierGripperOpen ON은 그리퍼 열기다.
- 현재 셔틀 소스는 `NgShuttleUp` ON을 상승으로 매핑한다.
  현장 동작이 반대라면 설정과 소스의 출력/피드백 쌍을 함께 대조한다.
- 출력의 현재 ON/OFF와 실린더가 도착한 위치는 다르다. 완료는 연결된 DI로 확인한다.
- 피드백 없음은 OFF가 아니라 unknown이다.

번호만 바꾸는 작업과 신호 의미/극성을 바꾸는 작업은 다르다.
후자는 enum 표시명, 출력의 ON/OFF 채널, 대응 입력, 유닛 완료 조건,
Virtual 동작과 해당 회귀 검사를 같이 확인한다.
코드 기본값을 바꿔도 이미 저장된 설정은 덮어쓰지 않는다. 매핑 저장 후 재시작한다.
원본 엑셀 파일과 실장비 DB는 이번 문서 정리에서 수정하지 않았다.
