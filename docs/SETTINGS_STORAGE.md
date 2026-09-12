# 설정·레시피 저장

## 저장 위치와 책임

| 데이터 | 위치 | 구현 |
| --- | --- | --- |
| 장비 설정, 레시피, 촬영 이미지 | 실행 폴더의 `Data/Machine.db` | `Shared/IBTM.Storage/MachineStore.cs` |
| 학습 데이터·모델·자동 수집 이미지 | 실행 폴더의 `TrainingData/BoltTraining.db` | `Stations/IBTM.Inspection.Training/` |
| BitmapSource ↔ PNG | Machine.db 이미지 경계 | `IBTM/RecipeStore.cs` |
| 레시피 선택·저장·이미지 교체 | UI와 저장소 경계 | `IBTM/UI/RecipeEditor.cs` |

장치 루프는 DB를 조회하지 않는다. 호스트가 읽은 타입 있는 설정을 각 장치에 전달한다.
축/IO 매핑·속도·간섭 좌표는 장비 설정, 제품별 볼트·Heat Sink·FOV/ROI·촬영 조건은 레시피다.
설비의 현재 위치나 동작 완료 상태를 설정에 저장하지 않는다.

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
| enum 사전 키가 잘못됨 / 값 타입이 틀림 | 로드 오류. 임의 변환 없음 |

과거 저장 형식과의 호환 변환은 유지하지 않는다.
**enum을 사전 키로 쓰는 IO 이름 변경은 일반 클래스 속성 이름 변경과 다르다.**
호환되지 않는 DB는 프로그램을 닫고 별도 보관한 뒤 새 DB로 장비를 재설정한다.

## 저장과 반영

Settings에서 Save하고, 하드웨어 매핑·드라이버 설정 변경 후에는 재시작한다.
코드의 기본값 수정은 이미 저장된 DB 값을 변경하지 않는다.
티칭의 저장 메시지와 오류를 확인하고, 일반 카메라 조건 변경은 Save Recipe로 남긴다.
FOV/ROI는 Heat Sink별로 독립적이며 캐리어 전체 맵/공유 PCB 영역은 사용하지 않는다.

이미지는 PNG BLOB으로 저장되며 레시피 이미지 교체는 트랜잭션으로 처리한다.
화면에서 쓰는 십자선·ROI 테두리는 저장 이미지 픽셀에 합성하지 않는다.

## 백업과 복원

Settings의 **Back Up Database**는 SQLite 백업 API로 저장된 Machine.db 내용을 백업한다.
미저장 편집과 별도 학습 DB는 포함하지 않는다. 열린 DB 파일을 탐색기에서 단순 복사하는 것으로 대신하지 않는다.

**Restore and Exit**는 복원 파일을 준비한 뒤 정상 종료한다.
다음 시작에서 현재 DB를 `Machine.db.previous`로 보관하고 준비된 백업을 적용한다.
이미 만들어진 장치 객체의 설정을 실행 중 갑자기 교체하지 않는다.

학습 DB는 프로그램 종료 후 별도로 백업한다. SQLite journal/WAL/SHM 파일 존재도 확인한다.
AJIN은 `AxlOpenNoReset`을 쓰고 시작 시 .mot를 로드하지 않는다.
기존 제조사 설정 파일은 별도 백업 대상이며 임의 삭제하지 않는다.

Virtual 구성은 별도 출력 폴더와 DB를 사용한다. 데모 설정을 실장비에 복사하지 않는다.
