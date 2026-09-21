using System;
using System.Collections.Generic;
using System.Linq;

namespace IBTM.Hantas;

// Display reference only. Never use this table to bypass live Alarm / Ready / RUN feedback.
// Mountz AD Operation Manual (2017-03-21), pp. 27-30. Unknown firmware codes stay unknown.
public sealed record AdcControllerError(ushort Code, string Group, string Description, string Action)
{
    public const string ReferenceNote = "AD 매뉴얼 2017-03-21, 27-30쪽 기준. 문서에 없는 코드는 컨트롤러 모델·펌웨어별 확인이 필요합니다.";
    public const string OperationNote = "체결 오류는 STOP 후 NG로 기록합니다. 다음 볼트 전에 알람이 남아 있으면 RESET을 1회 실행합니다. Alarm=0, Ready=ON, RUN=OFF 확인 시 진행하며, 실패하면 설비 알람으로 정지합니다.";

    static AdcControllerError()
    {
        All = Array.AsReadOnly<AdcControllerError>([
            new(10, "AL · 드라이브", "하드웨어 과전류", "케이블·모터·부하 점검"),
            new(11, "AL · 드라이브", "온도성 과부하", "온도·부하·케이블 점검"),
            new(14, "AL · 드라이브", "소프트웨어 과전류", "케이블·모터·부하 점검"),
            new(16, "AL · 드라이브", "전류 제한 초과", "케이블·모터·부하 점검"),
            new(21, "AL · 드라이브", "연속 과부하", "부하·케이블 점검"),
            new(22, "AL · 드라이브", "온도성 과부하/드라이브 과열", "문서에 중복 기재; 온도·모델 확인"),
            new(23, "AL · 드라이브", "온도성 과부하", "온도·부하·케이블 점검"),
            new(24, "AL · 드라이브", "모터 배선 단선/단락", "배선·모터 점검"),
            new(25, "AL · 드라이브", "드라이브 과열", "온도·드라이브 점검"),
            new(30, "AL · 드라이브", "엔코더 통신 오류", "엔코더 케이블·드라이브 점검"),
            new(31, "AL · 드라이브", "엔코더 배선 단선", "엔코더 케이블·드라이브 점검"),
            new(32, "AL · 드라이브", "엔코더 데이터 오류", "엔코더 케이블·드라이브 점검"),
            new(34, "AL · 드라이브", "엔코더 데이터 오류", "엔코더 케이블·드라이브 점검"),
            new(38, "AL · 드라이브", "스핀들/드라이브 인식 불일치", "모델·엔코더 확인"),
            new(40, "AL · 드라이브", "저전압", "입력전원 점검"),
            new(41, "AL · 드라이브", "과전압", "전원·가감속 설정 점검"),
            new(42, "AL · 드라이브", "주전원 이상", "입력전원 점검"),
            new(43, "AL · 드라이브", "제어전원 이상", "입력전원 점검"),
            new(50, "AL · 드라이브", "과속", "모터·엔코더·드라이브 점검"),
            new(60, "AL · 드라이브", "USB 통신 포트 오류", "제조사 확인"),
            new(63, "AL · 드라이브", "파라미터 체크섬 오류", "제조사 확인"),
            new(64, "AL · 드라이브", "파라미터 범위 오류", "제조사 확인"),
            new(70, "AL · 드라이브", "드라이브/스핀들 조합 오류", "제조사 확인"),
            new(71, "AL · 드라이브", "공장 설정 필요", "제조사 확인"),
            new(110, "Er · 시스템", "전류 오프셋 오류", "RESET; 반복되면 점검"),
            new(113, "Er · 시스템", "스핀들 파라미터 읽기 실패", "전원 재인가·점검"),
            new(114, "Er · 시스템", "드라이버 인식 실패", "연결 확인·전원 재인가"),
            new(115, "Er · 시스템", "컨트롤러 인식 실패", "전원 재인가·점검"),
            new(116, "Er · 시스템", "설정 토크 범위 초과", "토크 설정 확인·RESET"),
            new(118, "Er · 시스템", "모터 회전 미감지", "모터 점검·RESET"),
            new(200, "Er · 시스템", "파라미터 읽기 실패", "메모리·통신 점검"),
            new(201, "Er · 시스템", "파라미터 체크섬 오류", "전원 재인가·점검"),
            new(220, "Er · 시스템", "멀티시퀀스 설정 오류", "설정 확인·RESET"),
            new(300, "Er · 공정", "체결 시간 초과", "체결 상태·시간 설정 확인"),
            new(301, "Er · 공정", "풀림 시간 초과", "풀림 상태·시간 설정 확인"),
            new(302, "Er · 공정", "모델 프로그램 설정 오류", "모델 설정 확인"),
            new(303, "Er · 공정", "모델 동작 취소", "취소 원인 확인"),
            new(304, "Er · 공정", "풀림 중 모터 스톨", "걸림·부하 확인"),
            new(309, "Er · 공정", "비트 소켓 트레이 오류", "트레이 확인"),
            new(310, "Er · 공정", "스크루 카운트 시간 초과", "카운트 조건 확인"),
            new(311, "Er · 공정", "스크루 수량 부족", "누락 확인"),
            new(330, "Er · 공정", "최소각도 미달", "체결·각도 조건 확인"),
            new(331, "Er · 공정", "목표각도 설정 오류", "목표각도 확인"),
            new(332, "Er · 공정", "최대각도 초과", "체결·각도 조건 확인"),
            new(333, "Er · 공정", "목표토크 도달 전 정지", "중단 원인 확인"),
            new(334, "Er · 공정", "물림 토크 미감지", "물림 조건 확인"),
            new(335, "Er · 공정", "결과 토크 허용범위 이탈", "체결·토크 조건 확인"),
            new(336, "Er · 공정", "토크 상한 초과", "체결·토크 조건 확인"),
            new(337, "Er · 공정", "착좌 전 동기화 시간 초과", "동기화 확인"),
            new(338, "Er · 공정", "착좌 후 동기화 시간 초과", "동기화 확인"),
        ]);
    }

    public static IReadOnlyList<AdcControllerError> All { get; }
    public string CodeText => $"{Code} (0x{Code:X4})";

    public static string Describe(ushort code)
    {
        if (code == 0)
            return "0 (0x0000): 오류 없음";
        var error = All.FirstOrDefault(item => item.Code == code);
        return error is null
            ? $"{code} (0x{code:X4}): 문서에 없는 코드. 컨트롤러 모델·펌웨어별 오류표 확인 필요."
            : $"{error.CodeText}: {error.Description}. {error.Action}.";
    }
}
