# Inspection 티칭

위치·조명 티칭과 저장 이미지의 검사 설정을 별도 메뉴로 구분한다.
볼트·Data Matrix 포인트마다 티칭 영상 한 장을 저장한다.

## 위치 티칭 — Teaching → Inspection Gantry

1. Heat Sink 1 또는 2를 선택한다. 볼트는 Add Bolt로 추가하고, Data Matrix는 해당 티칭 항목을 선택한다.
2. 포인트 선택 시 저장된 조명값이 Live 조명값에 표시된다. 밝기를 조절하고 Live로 보면서 조그로 대상을 가운데 십자선에 맞춘다.
3. 축을 멈추고 Record Position을 누른다. 선택 포인트의 촬영 XY와 기준 영상을 함께 저장한다.
4. 같은 포인트를 다시 기록하면 이미지와 좌표를 교체하며 기존 ROI는 유지한다. Live는 계속 표시한다.
5. 이미 기록한 포인트에서 Grab하면 좌표를 유지하고 기준 영상과 조명값을 저장한다. 포인트를 새로 추가하지 않는다.

조명은 Data Matrix 1/2와 각 볼트별로 0–255를 저장한다. 포인트 조명값이 없으면 레시피 기본값을 사용한다.
Live 조명값은 ViewModel의 임시 값이며 Record Position 또는 Grab으로 촬영·저장할 때 선택 포인트에 반영한다.

이 화면은 수동 모드의 위치 티칭 전용이다. ROI·판독 설정·저장 영상 재검사는 Inspection Teaching 메뉴에서 한다.
좌표 기록은 Record Position만 수행한다. Save, 포인트 선택, ROI 편집, 해상도 변경으로 좌표를 바꾸지 않는다.

## 검사 설정 — Inspection Teaching

설비 운전 여부와 무관하게 사용할 수 있는 저장 이미지 편집 화면이다. 카메라·축·조명 장치를 직접 호출하지 않는다.

1. 레시피를 선택하고 Load Recipe를 누른다. 설비의 활성 레시피 선택은 바뀌지 않는다.
2. 왼쪽에서 PCB별 Data Matrix 또는 볼트를 선택한다. DB에 저장된 기준 영상과 ROI가 표시된다.
3. 영상에서 드래그해 중앙 정사각형 ROI의 크기를 조절한다. 기존 비중앙 ROI는 선택만으로 바꾸지 않는다.
4. 볼트는 Threshold와 Minimum bright (%), Data Matrix는 판독 옵션과 Binary threshold를 조정한다.
5. Inspect Image로 화면에 불러온 영상을 다시 판정한다. 이 결과는 생산 결과에 기록하지 않는다.
6. Save Inspection으로 편집한 검사 설정을 저장한다.

Data Matrix 옵션은 Try harder, Try inverted, Rotate search, Pure barcode, Binary threshold다.
Binary threshold가 비어 있으면 자동 임계값을 사용한다.
볼트는 임계값 이상인 픽셀의 비율이 Minimum bright 이상이면 OK다.
자세한 계산은 [밝은 면적 비율 검사](../Stations/IBTM.Inspection/README.md)를 참고한다.

## 저장과 적용 시점

편집은 별도 레시피 복사본에서 한다. Save Inspection이 성공하기 전에는 자동 검사 설정이 바뀌지 않는다.
DB 저장에 성공하면 활성 레시피와 이름이 같은 경우 다음 검사 포인트부터 즉시 반영한다.
이미 시작한 포인트는 시작 시 읽은 조명·ROI·판독 설정으로 끝낸다.

저장 대상은 ROI, 볼트 판정값, Data Matrix 옵션, 이미지 해상도(mm/px)다.
저장 직전의 DB 레시피에 이 항목만 합치므로 Gantry 티칭의 좌표·헤드 지정·기준 이미지·조명값과 다른 유닛 설정을 덮어쓰지 않는다.
다른 레시피를 편집·저장해도 현재 운전 레시피는 바뀌지 않는다.

## 저장된 생산 결과 이미지 불러오기

하단 Saved inspection results의 폴더는 설정된 월별 DB 저장 폴더를 기본으로 사용한다.
Refresh Results로 PCB 목록을 읽고, Older로 이전 결과를 추가로 불러온다.
PCB를 선택하고 Load Selected PCB를 누르면 저장된 검사 이미지와 당시 판정이 표시된다.
같은 레시피를 연 상태에서 Use Image for Reinspection을 누르면 PCB·볼트 번호가 맞는 편집 포인트에 연결한다.
기준 영상과 해상도가 같은 이미지를 사용한다.

화면에는 원래 저장된 판정과 재검사 판정을 구분해 표시한다.
재검사하거나 설정을 저장해도 생산 DB의 결과·검사 이미지와 레시피 기준 이미지는 교체하지 않는다.
Recipe Image를 누르면 해당 포인트의 기준 영상으로 돌아간다.

## 줄자와 표시

Ruler를 켜서 두 점 사이를 드래그하고 Distance (mm)를 입력한다.
Apply Resolution은 실제 거리 ÷ 원본 픽셀 거리로 편집본의 mm/px를 변경한다. Save Inspection으로 저장한다.
기록된 좌표는 다시 계산하지 않는다. 포인트나 영상을 바꾸면 측정선과 거리 입력은 지워진다.

십자선·ROI 테두리·이진화 오버레이·줄자는 화면 표시이며 원본 PNG 픽셀에 합성하지 않는다.
Live는 위치 티칭에서만 사용하며 저장 이미지 편집 화면과 공유하지 않는다.

## 수정 위치

- 위치 티칭/Live: `IBTM/UI/TeachingView.xaml`, `TeachingViewModel.cs`
- 검사 편집/결과 이미지: `IBTM/UI/InspectionTeachingView.xaml`, `InspectionTeachingViewModel.cs`
- 저장 이미지 판정: `IBTM/UI/InspectionPreview.cs`
- 자동 검사 포인트별 설정: `Stations/IBTM.Inspection/InspectionStation.cs`
- 검사 설정 저장: `Shared/IBTM.Storage/RecipeManager.cs`, `MachineStore.cs`, `Recipes/Recipe.cs`
- DB/이미지: [설정과 저장](SETTINGS_STORAGE.md)
