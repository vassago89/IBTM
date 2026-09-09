///******************************************************************************
//*
//*	File Version: 1,0,0,0
//*
//*	Copyright (c) Alpha Motion 2011-
//*
//*	This file is strictly confidential and do not distribute it outside.
//*
//*	MODULE NAME :
//*		tmcDApiDef.cs
//*
//*	AUTHOR :
//*		K.C. Lee
//*
//*	DESCRIPTION :
//*		the header file for RC files of project.
//*
//*
///****************************************************************************/

using System;
using System.Text;

namespace Shared
{
    public sealed class tmcDef
    {
        /******************************************************************************
        *	TMC-Axxxxx 시리즈의 함수와 TMC-Bxxxxx 시리즈 함수의 체계가 따로 있는데
            사용자 편의를 위해 같은 기능의 함수를 각각의 시리즈에 맞는 함수로 분리
	        제작하여 제공	
        *	TMC-Axxxxx 함수를 사용자는 // TMC-Axxxxx Series // 부분 참고
        *	TMC-Bxxxxx 함수를 사용자는 // TMC-Bxxxxx Series // 부분 참고

        *	Return code 다름
        /****************************************************************************/

        //***********************************************************************************************
        //									ERROR CODE DEFINITIONs										*
        //***********************************************************************************************
        //'사용자 이벤트 메시지
        public const uint WM_USER = 0x400;
        public const uint WM_DI_INTERRUPT = (WM_USER + 2000);

        public const int TMC_AE                         = (0xAE);   // TMC_AExxDIO
        public const int TMC_AFDI                       = (0xAF1);  // TMC_AFxxDI
        public const int TMC_AFDO                       = (0xAF2);  // TMC_AFxxDO
        public const int TMC_AFDIO                      = (0xAF3);  // TMC_AFxxDIO

        ///////////////////////////////////////////////////////////
        //                                                       //
        //                   TMC-Axxxxx Series                   //
        //                                                       //
        ///////////////////////////////////////////////////////////
        //******************************************************************************************************//
        
        // Status code( Return code )
        public const int TMC_ST_FALSE					= 0;	    // 수행 실패
        public const int TMC_ST_OK						= 1;	    // 수행 성공       
        
        public const int CMD_FALSE						= 0;
        public const int CMD_TRUE						= 1;

        /* Error code = Erroc code */
        public const int ERR_SUCCESS					= 0;		// 에러 없음

        public const int ERR_DEVICE_LOAD				= -1;	    // 디바이스가 로드 되어 있지 않습니다.
        public const int ERR_PRM_LOAD					= -2;	    // 파라미터 일치 하지 않습니다.
        public const int ERR_DEVICE_EXIST				= -3;	    // 동일한 DEVICE ID가 존재 합니다.
        public const int ERR_DEVICE_PCI_BUS				= -4;	    // PCI 버스 데이타가 이상합니다.
        public const int ERR_UPBOARD_LOAD				= -5;	    // 모듈 순서가 잘못되었습니다 
        public const int ERR_CPLD_VER					= -6;	    // CPLD VERSION 에러 
        public const int ERR_HW_TYPE					= -7;	    // H/W TYPE 에러 
        public const int ERR_EEPROM_VER                 = -8;       // ERR_EEPROM_VER 에러
        public const int ERR_SUPPORT_PROCESS            = -10;      // 지원하지 않은 프로세스
                         
        public const int ERR_INVALID_HANDLE				= -100;	    // 디바이스 핸들값이 에러
        public const int ERR_CREATE_KERNEL_DRIVER_FAIL	= -101;	    // 커널 드라이브 생성 에러
        public const int ERR_CREATE_MEM_FAIL			= -102;	    // 메모리 생성 에러
                         
        public const int ERR_INVALID_CHANNEL			= -200;	    // 채널 세팅이 잘못 되었습니다.
        public const int ERR_INVALID_GROUP				= -201;	    // 그룹 세팅이 잘못 되었습니다.

        public const int ERR_INVALID_PARAMETER			= -300;	    // 하나 또는 다수의 파라미터 에러
        public const int ERR_INVALID_BOARD_ID			= -400;	    // 보드 ID 설정 에러
        public const int ERR_INVALID_BOARD_MAX			= -410;	    // CONTROLLER 최대 갯수 에러
        public const int ERR_FILE_CREATE				= -500;	    // 파일 생성 에러
                         
        public const int ERR_EVENT_CREATE				= -510;	    // 이벤트 생성 에러
        public const int ERR_THREAD_CREATE				= -520;	    // 스레드 생성 에러         
                         
        public const int ERR_UNKNOWN					= -9999;	// 알수 없는 에러

        public const int CMD_OFF						= 0;		// OFF = 0
        public const int CMD_ON							= 1;		// ON = 1 
                         
        public const int CMD_DISABLE					= 0;		// DISABLE = 0
        public const int CMD_ENABLE						= 1;		// ENABLE = 1
                         
        public const int CMD_INT_MESSAGE                = 0;        // 윈도우 메시지 전달 방식을 통하여 인터럽트 통지
        public const int CMD_INT_CALLBACK               = 1;        // 콜백함수를 통하여 인터럽트 통지

        public const int CMD_EVT_MC                     = 0;        // motion evnet 발생                         
        public const int CMD_EVT_IO                     = 1;        // 범용 I/O evnet 발생

        public const int EVT_DISABLE                    = 0;        // disable all event                         
        public const int EVT_ENABLE                     = 1;        // enable  all event

        ///////////////////////////////////////////////////////////
        //                                                       //
        //                   TMC-Bxxxxx Series                   //
        //                                                       //
        ///////////////////////////////////////////////////////////
        //******************************************************************************************************//

        //String
        public const int TMC_MAX_STR  	                = 256;      // Maximum String Length

        // Interrupt Handler Type //
        public const int emIHT_MESSAGE					= 0;
        public const int emIHT_CALLBACK					= 1;
        public const int emIHT_EVENT 		        	= 2;

        // Function Log Level //
        public const int emDLOG_NONE			        = 0;        // LOG 출력 하지 않음
        public const int emDLOG_ERROR_ONLY	            = 1;        // 에러만 발생한 경우 
        public const int emDLOG_MOT_SET		            = 2;        // 모션 관련 함수 + 설정 함수(SET)  
        public const int emDLOG_DO_SET		            = 3;        // LEVEL 2 + 반환 함수(SET) + 디지털 출력 함수(DO)
        public const int emDLOG_DI_SET		            = 4;        // LEVEL 3 + 디지털 입력 함수(DI) 
        public const int emDLOG_ALL	    	            = 5;        // 전부


        // =======================================
        // == IO Bit
        // =======================================
        public const int TMC_BIT_OFF					= 0;
        public const int TMC_BIT_ON					    = 1;

        // Boolean type definition //
        public const int TMC_FALSE 					    = 0;
        public const int TMC_TRUE 					    = 1;

        // Used type definition //
        public const int TMC_NOTUSED 				    = 0;
        public const int TMC_USED		 			    = 1;
            
        // Used type definition //
        public const int TMC_LOW                        = 0;
        public const int TMC_HIGH                       = 1;

        // Function Log Level //
        public const int TMC_DLOG_LEVEL0			    = 0;        // LOG 출력 하지 않음
        public const int TMC_DLOG_LEVEL1		        = 1;        // 에러만 발생한 경우 
        public const int TMC_DLOG_LEVEL2		        = 2;        // 모션 관련 함수 + 설정 함수(SET)  
        public const int TMC_DLOG_LEVEL3		        = 3;        // LEVEL 2 + 반환 함수(SET) + 디지털 출력 함수(DO)
        public const int TMC_DLOG_LEVEL4		        = 4;        // LEVEL 3 + 디지털 입력 함수(DI) 
        public const int TMC_DLOG_LEVEL5		        = 5;        // 전부


        /* Return code () */
        /////////////////////////////////////////////////////////////////

        public const int TMC_RV_OK		                = 1;        // 성공

        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_MOT_INIT_ERR			= -100;     // 라이브러리 초기화 실패
        public const int TMC_RV_MOT_FILE_SAVE_ERR       = -101;     // 모션 설정값을 저장하는 파일 저장에 실패함
        public const int TMC_RV_MOT_FILE_LOAD_ERR       = -102;     // 모션 설정값이 저장된 파일이 로드가 안됨

        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_DRV_VER_ERR             = -1000;
        public const int TMC_RV_LOC_MEM_ERR             = -1001;    // 메모리 생성 되지 실패
        public const int TMC_RV_GLB_MEM_ERR             = -1002;    // 공유 메모리 생성 되지 실패
        public const int TMC_RV_HANDLE_ERR              = -1003;    // 드바이스 핸들값이 에러
        public const int TMC_RV_CREATE_KERNEL_ERR       = -1004;    // 커널 드라이브 생성 에러
        public const int TMC_RV_CREATE_THREAD_ERR       = -1005;    // 스레드 생성 에러
        public const int TMC_RV_CREATE_EVENT_ERR        = -1006;    // 이벤트 생성 에러
        public const int TMC_RV_CREATE_FILE_ERR	        = -1007;    // 파일 생성 에러

        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_CON_NOT_FOUND_ERR	    = -1030;    // CONTROLLER NOT FOUND 에러
        public const int TMC_RV_CON_NOT_LOAD_ERR		= -1031;    // CONTROLLER LOAD 에러
        public const int TMC_RV_CON_DIP_SW_ERR		    = -1032;    // 보드 ID 세팅 에러
        public const int TMC_RV_CON_MAX_ERR		        = -1033;    // CONTROLLER 최대 갯수 에러
        public const int TMC_RV_PCI_BUS_LINE_ERR		= -1034;    // PCI 버스 데이타가 이상합니다.
        public const int TMC_RV_MOD_POS_ERR		        = -1035;    // 모듈 순서가 잘못되었습니다.
        public const int TMC_RV_SUPPORT_PROCESS_ERR     = -1036;    // 지원하지 않은 프로세스
        public const int TMC_RV_SUPPORT_FUCTION_ERR     = -1037;    // 지원하지 않은 함수
        public const int TMC_RV_CON_OPEN_MODE_ERR       = -1038;    // 수동/자동 모드가 틀림
        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_PRM_LOAD_ERR            = -1050;    // 파라미터 로드 에러
        public const int TMC_RV_PRM_VAL_ERR             = -1051;    // 파라미터 값 에러
        public const int TMC_RV_PRM_FILENAME_ERR        = -1052;    // 파라미터 파일이 존재하지 않음

        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_NOT_SPT_ERR             = -1100;    // 모델에서 지원하지 않는 기능
        public const int TMC_RV_DIV_BY_ZERO_ERR         = -1101;    // DIVIDE BY ZERO 에러
        public const int TMC_RV_TIME_OUT_ERR            = -1102;    // TIME OUT 에러
        public const int TMC_RV_WM_QUIT_ERR             = -1103;    // WM_QUIT 발생 에러
        public const int TMC_RV_CON_NO_ERR              = -1120;    // 해당 카드번호가 존재하지 않음
            
        public const int TMC_RV_ARG_RNG_ERR             = -1125;    // 함수 인자 범위 에러
        public const int TMC_RV_CS_AXIS_ERR             = -1126;    // COORDINATE AXIS 에러
        public const int TMC_RV_INT_CFG_ERR             = -1127;    // 인터럽트 설정 에러

        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_GROUP_RNG_ERR           = -1130;    // Group 범위 에러

        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_DLOG_ERR                = -1180;    // FUNCTION 로그 에러
        public const int TMC_RV_DLOG_LEVEL_ERR          = -1181;    // FUNCTION 로그 레벨 에러

        /////////////////////////////////////////////////////////////////
        public const int TMC_RV_UNKNOWN_ERR             = -9999;    // 알수 없는 에러
    }
}
