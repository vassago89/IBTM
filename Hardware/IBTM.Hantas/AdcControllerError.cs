using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Core;

namespace IBTM.Hantas;

// Display reference only. Never use this table to bypass live Alarm / Ready / RUN feedback.
// Error code list (v1.02.7), Error list sheet: ADC column, including its code-specific meanings.
public sealed record AdcControllerError(ushort Code, string Group, string Description, string Action)
{
    public const string ReferenceNote = "Error code list (v1.02.7) · ADC 기준";

    static AdcControllerError()
    {
        All = Array.AsReadOnly<AdcControllerError>([
            new(10, "AL · 드라이브", "하드웨어 과전류", "케이블·모터·부하 점검"),
            new(11, "AL · 드라이브", "온도성 과부하", "온도·부하·케이블 점검"),
            new(14, "AL · 드라이브", "소프트웨어 과전류", "케이블·모터·부하 점검"),
            new(16, "AL · 드라이브", "전류 제한 초과", "케이블·모터·부하 점검"),
            new(21, "AL · 드라이브", "연속 과부하", "부하·케이블 점검"),
            new(22, "AL · 드라이브", "드라이브 과열", "온도·드라이브 점검"),
            new(23, "AL · 드라이브", "온도성 과부하", "온도·부하·케이블 점검"),
            new(24, "AL · 드라이브", "모터 배선 단선/단락", "배선·모터 점검"),
            new(25, "AL · 드라이브", "드라이브 과열", "온도·드라이브 점검"),
            new(30, "AL · 드라이브", "엔코더 통신 오류", "엔코더 케이블·드라이브 점검"),
            new(31, "AL · 드라이브", "엔코더 배선 단선", "엔코더 케이블·드라이브 점검"),
            new(32, "AL · 드라이브", "엔코더 데이터 오류", "엔코더 케이블·드라이브 점검"),
            new(34, "AL · 드라이브", "엔코더 데이터 오류", "엔코더 케이블·드라이브 점검"),
            new(38, "AL · 드라이브", "스핀들/드라이브 조합 불일치", "스핀들·드라이브 모델 확인"),
            new(40, "AL · 드라이브", "저전압", "입력전압·운전 중 전압 강하 확인"),
            new(41, "AL · 드라이브", "과전압", "전원·가감속 설정 점검"),
            new(42, "AL · 드라이브", "주전원 이상", "입력전원 점검"),
            new(43, "AL · 드라이브", "제어전원 이상", "입력전원 점검"),
            new(50, "AL · 드라이브", "과속", "드라이버 최대 속도 확인"),
            new(60, "AL · 드라이브", "USB 포트 인식 실패", "USB 포트·연결 확인"),
            new(63, "AL · 드라이브", "파라미터 체크섬 오류", "제조사 확인"),
            new(64, "AL · 드라이브", "파라미터 범위 오류", "파라미터 설정 범위 확인"),
            new(70, "AL · 드라이브", "드라이브/스핀들 조합 오류", "스핀들·드라이브 모델 확인"),
            new(71, "AL · 드라이브", "공장 설정 필요", "제조사 확인"),
            new(110, "Er · 시스템", "전류 오프셋 오류", "전원 재인가 후 반복되면 수리 필요"),
            new(113, "Er · 시스템", "드라이버 데이터 읽기 실패", "전원 재인가 후 반복되면 드라이버 점검"),
            new(114, "Er · 시스템", "드라이버 모델 설정 불일치", "Driver model 설정 확인"),
            new(115, "Er · 시스템", "컨트롤러·드라이버 모델 불일치", "호환되는 컨트롤러·드라이버 조합 확인"),
            new(116, "Er · 시스템", "I2C 오류 · 드라이버 파라미터 읽기 실패", "케이블 확인 후 전원 재인가 · 반복되면 드라이버·컨트롤러 점검"),
            new(118, "Er · 시스템", "모터 회전 미감지", "케이블·드라이버 확인 후 전원 재인가 · 반복되면 드라이버 점검"),
            new(125, "Er · 시스템", "토크 상승 이상", "케이블·모터·센서 확인 · 반복되면 드라이버 점검"),
            new(200, "Er · 시스템", "파라미터 읽기·쓰기 실패", "전원 재인가 후 반복되면 컨트롤러 점검"),
            new(220, "Er · 시스템", "멀티시퀀스 설정 오류", "멀티시퀀스 동작 설정 확인"),
            new(300, "Er · 공정", "정방향 운전 시간 초과", "Forward run time limit 설정 확인"),
            new(301, "Er · 공정", "역방향 운전 시간 초과", "Reverse run time limit 설정 확인"),
            new(302, "Er · 공정", "모델 프로그램 설정 오류", "모델 설정 확인"),
            new(303, "Er · 공정", "모델 동작 취소", "취소 원인 확인"),
            new(304, "Er · 공정", "풀림 중 모터 스톨", "Use max torque for loosen 또는 Motor stall time limit 설정 확인"),
            new(310, "Er · 공정", "스크루 카운트 시간 초과", "스크루 카운트 Time limit 설정 확인"),
            new(311, "Er · 공정", "스크루 수량 부족", "설정 수량·완료 신호 확인"),
            new(330, "Er · 공정", "최소각도 미달 상태에서 목표토크 도달", "Min angle 설정 확인"),
            new(331, "Er · 공정", "AC/TM 목표각도 0 설정", "Target angle 설정 확인"),
            new(332, "Er · 공정", "최대각도 초과", "Max angle 설정 확인"),
            new(333, "Er · 공정", "체결 완료 전 정지", "중단 원인 확인"),
            new(334, "Er · 공정", "물림 토크 감지 전 시간·각도 제한 도달", "물림 토크·시간·각도 설정 확인"),
            new(335, "Er · 공정", "체결 토크 허용범위 오류", "Torque limit 설정·체결물 상태 확인"),
            new(336, "Er · 공정", "모터 한계 토크 도달", "모터·케이블 연결 확인 · 반복되면 드라이버 점검"),
            // ADC meanings differ from the shared Code / Details columns for 337–339.
            new(337, "Er · 공정", "착좌 전 동기화 시간 초과", "착좌 전 동기화 대기시간 확인"),
            new(338, "Er · 공정", "착좌 후 동기화 시간 초과", "착좌 후 동기화 대기시간 확인"),
            new(339, "Er · 공정", "Free speed 구간 토크 발생", "Free speed 설정 확인"),
            new(340, "Er · 공정", "탭 가공 최대토크 초과", "Thread tap의 Max torque 설정 확인"),
            new(341, "Er · 공정", "탭 가공 토크 범위·설정 오류", "탭 가공 토크 범위 확인 · Max torque는 Start torque·End torque보다 크게 설정"),
            new(342, "Er · 공정", "토크 유지 중 최대각도 초과", "Holding time max angle 설정 확인"),
        ]);
    }

    public static IReadOnlyList<AdcControllerError> All { get; }
    public string CodeText => $"{Code} (0x{Code:X4})";

    public static string Describe(ushort code)
    {
        if (code == 0)
            return $"0 (0x0000): {UiText.Get("오류 없음")}";
        var error = All.FirstOrDefault(item => item.Code == code);
        return error is null
            ? $"{code} (0x{code:X4}): {UiText.Get("문서에 없는 코드. 컨트롤러 모델·펌웨어별 오류표 확인 필요.")}"
            : $"{error.CodeText}: {UiText.Get(error.Description)}. {UiText.Get(error.Action)}.";
    }
}
