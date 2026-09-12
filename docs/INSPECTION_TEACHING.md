# Inspection 티칭

기준: 2026-09-11. 캐리어 전체 맵과 PCB 영역은 사용하지 않는다.
**저장 FOV 하나에는 볼트 하나 또는 Data Matrix 하나만 연결한다.**

## 티칭 순서

1. Station Teaching → Inspection Gantry → Heat Sink 1 또는 2를 선택한다.
2. 볼트는 왼쪽 **Add Bolt**로 추가해 선택한다. 바코드는 **Data Matrix**를 선택한다.
3. **Live**로 보면서 조그로 직접 이동한다. 자동 전체 스캔은 하지 않는다.
4. 축을 멈추고 **Add Current Image**를 누른다. 새 이미지와 실제 촬영 XY가 저장되며 Live는 유지된다.
5. 아래 저장 이미지에서 ROI 하나를 드래그한다. 선택 대상이 준비되어 있으면 자동 저장된다.
   대상을 나중에 선택했다면 **Apply ROI**로 연결한다.
6. 다른 위치도 같은 순서로 반복한다. Heat Sink 1과 2는 각각 티칭한다.

**Saved FOV / ROI**에서 기존 FOV를 고르면 연결된 Heat Sink, 볼트/Data Matrix와 ROI가 함께 선택된다.
한 FOV를 다른 대상으로 재지정하면 이전 연결은 교체된다. 새 대상을 같은 이미지에 중복 등록하는 방식은 아니다.

## 확인 버튼

| 버튼 | 동작 |
| --- | --- |
| Move & Inspect | 해당 FOV의 원래 촬영 XY로 이동 → 새 촬영 → 선택 ROI 검사 |
| Reinspect | 마지막 검사 이미지, 없으면 선택한 저장 FOV를 다시 판정. 축 이동 없음 |
| Read Data Matrix | 저장 FOV의 바코드 ROI만 읽고 바로 결과 표시. 축 이동 없음 |
| Clear Images | 저장 FOV 삭제. 필요한 이미지가 있으면 먼저 DB 백업 |

Data Matrix 결과는 화면의 읽기 전용 텍스트 박스에서 복사할 수 있다.
모션을 쓰는 버튼의 준비 조건과 이미지 재판정 조건은 구분되어 있다.

## 좌표와 기준핀

- 검사 위치는 **촬영 당시 XY**다. ROI 중심으로 카메라를 다시 이동시키지 않는다.
- ROI는 원본 이미지의 픽셀 좌표다.
- Resolution은 mm/px이며 ROI 중심 오프셋을 볼트 좌표로 환산할 때 사용한다.
- 기준핀이 정의되어 있으면 환산 결과를 볼트 체결 좌표로 저장할 수 있다.
- 기준핀이 없어도 Inspection FOV/ROI 저장과 검사는 가능하다.
  볼트 체결기의 기준핀·헤드 좌표 준비 조건과 혼동하지 않는다.
- Inspection용 볼트 포인트는 일반 Teach 버튼 대신 이미지 + ROI로 티칭한다.

## 화면 표시와 원본

위쪽 카메라 화면의 가운데 십자선은 티칭 보조 표시다.
아래 저장 FOV에는 십자선을 표시하지 않는다.
십자선·ROI 테두리·판정 오버레이는 `ImageTeachingView` 컨트롤이 그리며,
원본 프레임이나 DB의 PNG 픽셀에 합성하지 않는다.

Exposure/Gain/Light는 다음 촬영 또는 Live 시작에 적용된다.
카메라 설정 변경은 **Save Recipe**로 저장한다.
FOV 이미지·ROI 티칭은 저장 동작이 완료됐는지 오류 표시까지 확인한다.

## 수정 위치

- 화면·버튼: `IBTM/UI/StationTeachingView.xaml`
- FOV 선택·ROI·카메라 명령: `IBTM/UI/StationTeachingViewModel.Camera.cs`
- 왼쪽 티칭 목록: `IBTM/UI/StationTeachingViewModel.cs`
- 검사 위치와 ROI 사용: `Stations/IBTM.Inspection/BoltInspector.cs`
- 픽셀 변환/표시: `IBTM/UI/InspectionPreview.cs`, `ImageTeachingView.cs`
- DB/이미지: [설정과 저장](SETTINGS_STORAGE.md)

현장에서는 저장 후 재시작, Heat Sink/FOV 전환, Data Matrix 결과,
낮은 속도의 Move & Inspect를 차례로 확인한다. Virtual 검사로 실제 간섭을 보장하지 않는다.
