# 밝은 면적 비율 검사

`InspectionStation`이 자동 검사 순서와 이동·촬영·판정을 함께 담당한다.
촬영·조명·Live View 코드도 기본 파일 `InspectionStation.cs`에 둔다.
`InspectionStation.Station`의 Job이 캐리어별 결과와 완료 소유권을 보관한다. 검사 요청·착좌 요청과 NG 인터록 상태는 기본 파일 `InspectionStation.cs`에서 관리한다.
검사와 NG 이송은 하나의 `InspectionStation`이 소유한다. 자동 순서·집기·놓기·장치 동작을
기본 파일에 모으고, Repeat 완료 대기·S3 복귀 진입점만 `InspectionStation.Repeat.cs`에 둔다.
자동 루프는 `GetNextStep`으로 다음 동작을 선택하고 `ExecuteStepAsync`로 실행한다.
실행 내부의 `EnterStep`으로 현재 단계를 알리고, 순차 동작을 끝까지 기다린다.
실행이 외부 조건 대기를 반환하면 루프에서 `WaitForChangeAsync`로 대기한다.
현재 단계가 물리적 위치나 인계 완료를 대신하지 않는다.
`NgCarrierConveyor.cs`는 셔틀·벨트 순서와 장치 동작을, `NgCarrierConveyor.Repeat.cs`는 반복 역송을 처리한다.
빈 셔틀이 내려가 있으면 인계 해제와 픽업 상승·그리퍼 열림을 확인한 뒤 상승시켜 다음 캐리어를 받는다.

볼트 검사는 `BinaryChecker.Check`에서 저장된 사각형 ROI를 원본 크기로 처리한다.

1. BGR 픽셀을 밝기 `(299R + 587G + 114B + 500) / 1000`으로 변환한다.
2. 밝기가 `BrightnessThreshold` 이상이면 흰색, 미만이면 검은색으로 만든다.
3. `밝은 픽셀 수 / ROI 전체 픽셀 수`가 `MinimumBrightRatio` 이상이면 볼트 있음으로 판정한다.

밝기와 최소 비율은 볼트별 설정이다. 개별 값이 없는 기존 볼트는 종전 레시피 공통값을 사용하며,
신규 볼트는 그 기본값에서 시작한다(새 레시피 기본: 밝기 128, 최소 비율 1%). 실제 합격 기준은 촬영 영상으로
맞춰야 한다. Teaching의 Inspection Station에서 볼트를 선택하면 연결된 FOV와 이진화 ROI·Bright %가 표시된다.
밝기와 최소 비율을 변경하면 현재 검사 영상에 바로 반영되며, Save Recipe로 저장한다.
자동 검사도 해당 볼트의 같은 임계값·최소 비율을 사용한다. 노출·게인·조명은 다음
촬영부터 반영된다. 기준값과 같은 밝기 및 최소 비율과 같은 결과는 합격이다.

카메라·조명·검사축·Data Matrix 읽기·검사 결과와 NG 이송은 기존 Inspection
프로젝트가 담당한다. 학습·라벨링·모델 관리·자동 학습 이미지 수집 및 Torch 의존성은
제거했다. 실제 카메라와 Virtual 카메라 모두 같은 체커를 사용한다.
