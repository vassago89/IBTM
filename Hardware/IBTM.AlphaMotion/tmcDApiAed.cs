//******************************************************************************
//*
//*	File Version: 1,0,0,0
//*
//*	Copyright (c) Alpha Motion 2011-
//*
//*	This file is strictly confidential and do not distribute it outside.
//*
//*	MODULE NAME :
//*		tmcDApiAed.cs
//*
//*	AUTHOR :
//*		K.C. Lee
//*
//*	DESCRIPTION :
//*		the header file for RC files of project.
//*
//*
///****************************************************************************/
/******************************************************************************
*	TMC-Axxxxx 시리즈의 함수와 TMC-Bxxxxx 시리즈 함수의 체계가 따로 있는데
    사용자 편의를 위해 같은 기능의 함수를 각각의 시리즈에 맞는 함수로 분리
	제작하여 제공	
*	TMC-Axxxxx 함수를 사용자는 // TMC-Axxxxx Series // 부분 참고
*	TMC-Bxxxxx 함수를 사용자는 // TMC-Bxxxxx Series // 부분 참고

*	Return code 다름
/****************************************************************************/

using System;
using System.Runtime.InteropServices;

namespace Shared
{
	/// <summary>
    /// Import tmcDApiAed에 대한 요약 설명입니다.
	/// </summary>
    public  class TMCAEDLL
    {
        public delegate void EventFunc(IntPtr lParam);

        ///////////////////////////////////////////////////////////
        //                                                       //
        //           TMC-AxxxxP Series Function Name             //
        //                                                       //
        ///////////////////////////////////////////////////////////
        //******************************************************************************************************//

        //======================  Loading/Unloading function ====================================================//
        //하드웨어 장치를 로드하고 초기화한다.
        // 1. AIO_LoadDevice
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_LoadDevice();

        //하드웨어 장치를 언로드한다.	
        // 2. AIO_UnloadDevice
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_UnloadDevice();

        //======================  에러 처리               ====================================================//
        //가장 최근에 실행된 함수의 에러코드를 반환한다.
        //7. AIO_GetErrorCode
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetErrorCode();

        //가장 최근에 실행된 함수의 에러코드를 문자열로 변환해줍니다.		
        //8. AIO_GetErrorString
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern string AIO_GetErrorString( int nErrorCode );

        //======================  장치 초기화     ====================================================//
        //하드웨어 및 소프트웨어를 초기화한다.
        //3. AIO_Reset
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_Reset( ushort nConNo );

        //소프트웨어를 초기화한다.	
        //4. AIO_SetSystemDefault
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_SetSystemDefault( ushort nConNo );

        //======================  범용 디지털 입출력 ====================================================//
        // Digital 입력 신호 필터 사용 유무를 설정한다.
        //5. AIO_SetDiFilter
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_SetDIFilter( ushort nConNo, ushort wEnable );


        // Digital 입력 신호 필터 사용 유무를 반환한다.
        //6. AIO_GetDiFilter
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDIFilter( ushort nConNo, ref ushort wpEnable );

        // Digital 입력 신호 필터 시간를 설정한다.
        //0	1.00(μsec)	0.875(μsec)	8	0.256(msec)	0.224(msec)
        //1	2.00(μsec)	1.75(μsec)	9	0.512(msec)	0.448(msec)
        //2	4.00(μsec)	3.50(μsec)	A	1.02(msec)	0.896(msec)
        //3	8.00(μsec)	7.00(μsec)	B	2.05(msec)	1.79(msec)
        //4	16.0(μsec)	14.0(μsec)	C	4.10(msec)	3.58(msec)
        //5	32.0(μsec)	28.0(μsec)	D	8.19(msec)	7.17(msec)
        //6	64.0(μsec)	56.0(μsec)	E	16.4(msec)	14.3(msec)
        //7	128(μsec)	112(μsec)	F	32.8(msec)	28.7(msec)
        //7. AIO_SetDiFilterTime
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_SetDIFilterTime( ushort nConNo, ushort wTime );

        // Digital 입력 신호 필터 시간를 반환한다.  
        //8. AIO_GetDiFilterTime
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDIFilterTime( ushort nConNo, ref ushort wpTime );

        //지정한 해당 채널에 신호를 출력한다. 
        //9. AIO_PutDOBit
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_PutDOBit( ushort nConNo,  ushort nChannelNo, ushort wOutStatus );

        //지정한 해당 채널에 출력 신호를 반환한다.
        //10. AIO_GetDOBit
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDOBit( ushort nConNo, ushort nChannelNo, ref ushort wpOutStatus );

        //8 점씩 Digital Output 채널에 출력한다.
        //nGroupNo : 0 	CH0  ~ CH7
        //	    1	CH8  ~ CH15
        //	    2 	CH16 ~ CH23
        //           3	CH24 ~ CH31
        //11. AIO_PutDOByte
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_PutDOByte( ushort nConNo, ushort wGroupNo, byte byOutStatus );

        //8 점씩 Digital Output 채널에 반환한다.
        //12. AIO_GetDOByte
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDOByte( ushort nConNo, ushort wGroupNo, ref byte bypOutStatus );

        //16 점씩 Digital Output 채널에 출력한다.                                            
        //nGroupNo : 0 	CH0  ~ CH15
        //	    1 	CH16 ~ CH31
        //13. AIO_PutDOWord
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_PutDOWord( ushort nConNo, ushort wGroupNo, ushort wOutStatus );

        //16 점씩 Digital Output 채널에 반환한다.
        //14. AIO_GetDOWord
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDOWord( ushort nConNo, ushort wGroupNo, ref ushort wpOutStatus );

        //32 점씩 Digital Output 채널에 출력한다.                                            
        //nGroupNo : 0 	CH0  ~ CH31
        //15. AIO_PutDODWord
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_PutDODWord( ushort nConNo,  ushort wGroupNo,  uint dwOutStatus );

        //32 점씩 Digital Output 채널에 반환한다.      
        //16. AIO_GetDODWord
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDODWord( ushort nConNo,  ushort wGroupNo,  ref uint dwpOutStatus );

        //지정한 해당 채널에 입력 신호를 반환한다. 
        //17. AIO_GetDIBit
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDIBit( ushort nConNo, ushort nChannelNo, ref ushort wpInStatus );

        //8 점씩 Digital Input 채널에 반환한다.   
        //nGroupNo : 0 	CH0  ~ CH7
        //	    1	CH8  ~ CH15
        //	    2 	CH16 ~ CH23
        //           3	CH24 ~ CH31 
        //18. AIO_GetDIByte
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDIByte( ushort nConNo, ushort wGroupNo, ref byte bypInStatus );

        //16 점씩 Digital Input 채널에 반환한다.   
        //nGroupNo : 0 	CH0  ~ CH15
        //	    1 	CH16 ~ CH31 
        //19. AIO_GetDIWord
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDIWord( ushort nConNo, ushort wGroupNo, ref ushort wpInStatus );

        //32 점씩 Digital Input 채널에 반환한다.   
        //nGroupNo : 0 	CH0  ~ CH31 
        //20. AIO_GetDIDWord
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDIDWord( ushort nConNo, ushort wGroupNo, ref uint dwpInStatus );

        //지정된 컨트롤러의 LED 을 통해 보드 확인한다.
        //21. AIO_PutIoRun
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_PutIoRun( ushort nConNo, ushort wStatus );

        //지정된 컨트롤러의 LED 을 통해 보드 반환한다.
        //22. AIO_GetIoRun
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetIoRun( ushort nConNo, ref ushort wpStatus );

        //====================== 인터럽트 함수 ===================================================//
        //컨트롤러의 인터럽트 사용 유무를 설정한다.
        //23. AIO_SetEventEnable
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_SetEventEnable( ushort nConNo, ushort wIsEvent );

        //hWnd : 윈도우 핸들, 윈도우 메세지를 받을때 사용. 사용하지 않으면 NULL을 입력.
        //wMsg : 윈도우 핸들의 메세지, 사용하지 않거나 디폴트값을 사용하려면 0을 입력.
        //24. AIO_SetEventHandler
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_SetEventHandler( ushort nConNo, ushort wHandleType, int hHandle, uint wiMessage );

        // 지정 채널의의 사용자가 설정한 인터럽트 발생 여부를 설정한다.
        // nChNoMask1 : CH0  ~ CH31
        // nChNoMask2 : CH32 ~ CH63
        //25. AIO_SetDiEventMask
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_SetDiEventMask( ushort nConNo, uint dwChannelMask1, uint dwChannelMask2 );

        // 지정 채널의의 사용자가 설정한 인터럽트 발생 여부를 읽는다.
        // nChNoMask1 : CH0  ~ CH31
        // nChNoMask2 : CH32 ~ CH63
        //26. AIO_GetDiEventStatus
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDiEventStatus( ushort nConNo, ref uint dwpChannelStatus1, ref uint dwpChannelStatus2 );

        //====================== 기타 ===================================================//
        //지정된 컨트롤러 ID 번호를 반환한다.
        //27. AIO_GetBoardID
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetBoardID( ushort nConNo );

        //컨트롤러의 정보를 반환한다.
        //28. AIO_BoardInfo
        //npModel = Board Model
        //0xAE	= AE 
        //0xAF1 = AF(Only DI)
        //0xAF2 = AF(Only DO)
        //0xAF3 = AF(DIO)
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_BoardInfo( ushort nConNo, ref uint npModel, ref uint dwpComm, ref uint dwpDiNum, ref uint dwpDoNum );

        //지정된 컨트롤러에서 지원하는 범용 디지털입력 채널 수를 반환한다.
        //29. AIO_GetDiNum
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDiNum( ushort nConNo, ref ushort wpDiNum );

        //지정된 컨트롤러에서 지원하는 범용 디지털출력 채널 수를 반환한다. 
        //30. AIO_GetDoNum
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_GetDoNum( ushort nConNo, ref ushort wpDoNum );

        //지정된 컨트롤러에서 Dll 버전을 반환한다. 
        //31. AIO_GetDllVersion
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern uint AIO_GetDllVersion( ref string cpDllName );

        //컨트롤러의 정보를 저장한다.
        //32. AIO_SaveFile
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern uint AIO_SaveFile( ushort nConNo );

        //로그 파일 생성 유무를 설정한다.
        //33. AIO_LogCheck
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_LogCheck( ushort wLogCheck );       

        //-------------------------------------------------------------------------------------------------------------------
        ///////////////////////////////////////////////////////////
        //                                                       //
        //            TMC-BxxxxP Series Function Name            //
        //                                                       //
        ///////////////////////////////////////////////////////////
        //******************************************************************************************************//

        //====================== System Management Function ====================================================//
        //bManual = FALE, Con Number is set automatically
        //bManual = TRUE, Con Number is set to dip switch(Default)
        //npNumCons In a computer-set number of board
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiSysLoad( int bManual, ref int nNumCons );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiSysUnload();

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConInit( int nConNo );

        //====================== Digital In/Out FUNCTIONS =============================================//
        //0 nGroupNo => 0  ~ 31
        //1 nGroupNo => 32 ~ 63
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDiGetData( int nConNo, int nGroupNo, ref uint dwpInStatus );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDiGetBit( int nConNo, int nChannelNo, ref uint npInStatus );

        //0 nGroupNo => 0  ~ 31
        //1 nGroupNo => 32 ~ 63
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDoSetData( int nConNo, int nGroupNo, uint dwOutStatus );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDoGetData( int nConNo, int nGroupNo, ref uint dwpOutStatus );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDoSetBit( int nConNo, int nGroupNo, uint nOutStatus );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDoGetBit( int nConNo, int nGroupNo, ref uint npOutStatus );

        //0 nId => 0  ~ 15
        //1 nId => 16 ~ 31
        //2 nId => 32 ~ 47
        //3 nId => 48 ~ 63
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDiSetFilterEnable( int nConNo, int nId, int nEnable );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDiGetFilterEnable( int nConNo, int nId, ref int npEnable );

        //0 nId => 0  ~ 15
        //1 nId => 16 ~ 31
        //2 nId => 32 ~ 47
        //3 nId => 48 ~ 63
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDiSetFilterTime( int nConNo, int nId, int nTime );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDiGetFilterTime( int nConNo, int nId, ref int npTime );

        //====================== Motion Parameter Management Function ====================================================//
        // 파라미터 저장 경로는 C:\Program Files\Alpha Motion\DigitalPro\Parameter\' 로 고정
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConParamSave( int nConNo );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConParamLoad( int nConNo );

        //====================== DEBUG-LOGGING FUNCTIONS ==============================================//
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDLogSetFile( string szFilename );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDLogGetFile( ref string szpFilename );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDLogSetLevel( int nLevel );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiDLogGetLevel( ref int npLevel );

        //====================== ERROR HANDLING FUNCTIONS =============================================//
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiErrGetCode( int nConNo, ref int npCode );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiErrGetString( int nConNo, int nErrorCode, ref string szpStr );

        //====================== interrupt FUNCTIONS ==================================================//
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiIntSetHandler( int nConNo, int nType, int hHandler, uint nMsg );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiIntSetHandlerEnable( int nConNo, int nEnable );

        //0 nGroup => 0  ~ 31
        //1 nGroup => 32 ~ 63
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiIntSetDiEnable( int nConNo, int nGroup, uint nMask );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiIntGetDiStatus( int nConNo, int nGroup, ref uint npStatus );

        //===============================================================================================//
        //모션 보드에 설치된 디지털 범용 I/O 개수
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiGnGetDioNum( int nConNo, ref int npDiChNum, ref int npDoChNum );

        //DLL 버전 읽어오기
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConGetMApiVersion( int nConNo, ref int npVer );

        //보드 LED ON/OFF
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConSetCheckOn( int nConNo, int nOn );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConGetCheckOn( int nConNo, ref int npOn );

        //npModel = Board Model
        //0xAE	= AE 
        //0xAF1 = AF(Only DI)
        //0xAF2 = AF(Only DO)
        //0xAF3 = AF(DIO)
        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConGetModel( int nConNo, ref int npModel );

        [DllImport("tmcDApiAed_x64.dll")]
        public static extern int AIO_pmiConGetFwVersion( int nConNo, ref int npver );
    }
}
