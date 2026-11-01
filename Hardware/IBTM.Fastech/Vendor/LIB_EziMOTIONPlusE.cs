using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace FASTECH
{
	partial class EziMOTIONPlusELib
	{
		// Referred by ReturnCodes_Define.h
		// typedef enum _FMM_ERROR
		public const int FMM_OK = 0;

		public const int FMM_NOT_OPEN = 1;
		public const int FMM_INVALID_PORT_NUM = 2;
		public const int FMM_INVALID_SLAVE_NUM = 3;

		public const int FMC_DISCONNECTED = 5;
		public const int FMC_TIMEOUT_ERROR = 6;
		public const int FMC_CRCFAILED_ERROR = 7;
		public const int FMC_RECVPACKET_ERROR = 8;

		public const int FMM_POSTABLE_ERROR = 9;

		public const int FMP_FRAMETYPEERROR = 0x80;
		public const int FMP_DATAERROR = 0x81;
		public const int FMP_PACKETERROR = 0x82;

		public const int FMP_RUNFAIL = 0x85;
		public const int FMP_RESETFAIL = 0x86;
		public const int FMP_SERVOONFAIL1 = 0x87;
		public const int FMP_SERVOONFAIL2 = 0x88;
		public const int FMP_SERVOONFAIL3 = 0x89;

		public const int FMP_SERVOOFF_FAIL = 0x8A;
		public const int FMP_ROMACCESS = 0x8B;

		public const int FMP_PACKETCRCERROR = 0xAA;

		public const int FMM_UNKNOWN_ERROR = 0xFF;

		// Referred by FAS_EziMOTIONPlusE.h
		// Functions.

		////------------------------------------------------------------------------------
		////			Connection Functions
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API BOOL WINAPI	FAS_Connect(BYTE sb1, BYTE sb2, BYTE sb3, BYTE sb4, int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_Connect(byte sb1, byte sb2, byte sb3, byte sb4, int iBdID);
		public static bool FAS_Connect(IPAddress ipaddr, int iBdID)
		{
			int bRtn = 0;
			byte[] addr = ipaddr.GetAddressBytes();

			bRtn = FAS_Connect(addr[0], addr[1], addr[2], addr[3], iBdID);

			return (bRtn != 0);
		}

		//EZI_PLUSE_API BOOL WINAPI	FAS_ConnectTCP(BYTE sb1, BYTE sb2, BYTE sb3, BYTE sb4, int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_ConnectTCP(byte sb1, byte sb2, byte sb3, byte sb4, int iBdID);
		public static bool FAS_ConnectTCP(IPAddress ipaddr, int iBdID)
		{
			int bRtn = 0;
			byte[] addr = ipaddr.GetAddressBytes();

			bRtn = FAS_ConnectTCP(addr[0], addr[1], addr[2], addr[3], iBdID);

			return (bRtn != 0);
		}

		//EZI_PLUSE_API BOOL WINAPI	FAS_IsBdIDExist(int iBdID, BYTE* sb1, BYTE* sb2, BYTE* sb3, BYTE* sb4);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_IsBdIDExist(int iBdID, ref byte sb1, ref byte sb2, ref byte sb3, ref byte sb4);
		public static bool FAS_IsBdIDExist(int iBdID, ref IPAddress ipaddr)
		{
			int bRtn = 0;
			byte sb1 = 0;
			byte sb2 = 0;
			byte sb3 = 0;
			byte sb4 = 0;

			bRtn = FAS_IsBdIDExist(iBdID, ref sb1, ref sb2, ref sb3, ref sb4);
			ipaddr = new IPAddress(new byte[] { sb1, sb2, sb3, sb4 });

			return (bRtn != 0);
		}

		//EZI_PLUSE_API BOOL WINAPI	FAS_IsIPAddressExist(BYTE sb1, BYTE sb2, BYTE sb3, BYTE sb4, int* iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_IsIPAddressExist(byte sb1, byte sb2, byte sb3, byte sb4, ref int iBdID);
		public static bool FAS_IsIPAddressExist(IPAddress ipaddr, ref int iBdID)
		{
			int bRtn = 0;
			byte[] addr = ipaddr.GetAddressBytes();

			bRtn = FAS_IsIPAddressExist(addr[0], addr[1], addr[2], addr[3], ref iBdID);

			return (bRtn != 0);
		}

		//EZI_PLUSE_API BOOL WINAPI	FAS_Reconnect(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_Reconnect(int iBdID);

		//EZI_PLUSE_API void WINAPI	FAS_SetAutoReconnect(BOOL bSET);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetAutoReconnect(int bSET);

		//EZI_PLUSE_API void WINAPI	FAS_Close(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern void FAS_Close(int iBdID);

		//EZI_PLUSE_API BOOL WINAPI	FAS_IsSlaveExist(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_IsSlaveExist(int iBdID);

		////------------------------------------------------------------------------------
		////			Log Functions
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API void WINAPI	FAS_EnableLog(BOOL bEnable);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern void FAS_EnableLog(int bEnable);

		//EZI_PLUSE_API void WINAPI	FAS_SetLogLevel(enum LOG_LEVEL level);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern void FAS_SetLogLevel(int level);
		public static void FAS_SetLogLevel(LOG_LEVEL level)
		{
			switch (level)
			{
			case LOG_LEVEL.LOG_LEVEL_COMM:
				FAS_SetLogLevel(0);
				break;
			case LOG_LEVEL.LOG_LEVEL_PARAM:
				FAS_SetLogLevel(1);
				break;
			case LOG_LEVEL.LOG_LEVEL_MOTION:
				FAS_SetLogLevel(2);
				break;
			//case LOG_LEVEL.LOG_LEVEL_ALL:
			default:
				FAS_SetLogLevel(3);
				break;
			}
		}

		//EZI_PLUSE_API BOOL WINAPI	FAS_SetLogPath(LPCWSTR lpPath);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetLogPath([MarshalAs(UnmanagedType.LPWStr)] string lpPath);

		//EZI_PLUSE_API void WINAPI	FAS_PrintCustomLog(int iBdID, enum LOG_LEVEL level, LPCTSTR lpszMsg);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_PrintCustomLog(int iBdID, int level, [MarshalAs(UnmanagedType.LPWStr)] string lpszMsg);
		public static int FAS_PrintCustomLog(int iBdID, LOG_LEVEL level, string lpszMsg)
		{
			int nRtn = 0;

			switch (level)
			{
			case LOG_LEVEL.LOG_LEVEL_COMM:
				nRtn = FAS_PrintCustomLog(iBdID, 0, lpszMsg);
				break;
			case LOG_LEVEL.LOG_LEVEL_PARAM:
				nRtn = FAS_PrintCustomLog(iBdID, 1, lpszMsg);
				break;
			case LOG_LEVEL.LOG_LEVEL_MOTION:
				nRtn = FAS_PrintCustomLog(iBdID, 2, lpszMsg);
				break;
			//case LOG_LEVEL.LOG_LEVEL_ALL:
			default:
				nRtn = FAS_PrintCustomLog(iBdID, 3, lpszMsg);
				break;
			}

			return nRtn;
		}

		////------------------------------------------------------------------------------
		////			Ethernet Address Functions
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_GetEthernetAddr(int iBdID, unsigned long* gateway, unsigned long* subnet, unsigned long* ip);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetEthernetAddr(int iBdID, ref uint gateway, ref uint subnet, ref uint ip);

		//EZI_PLUSE_API int WINAPI	FAS_SetEthernetAddr(int iBdID, unsigned long gateway, unsigned long subnet, unsigned long ip);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetEthernetAddr(int iBdID, uint gateway, uint subnet, uint ip);

		//EZI_PLUSE_API int WINAPI	FAS_GetMACAddress(int iBdID, unsigned long long* MACAddress);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetMACAddress(int iBdID, ref UInt64 MACAddress);

		////------------------------------------------------------------------------------
		////			Info Functions
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_GetSlaveInfo(int iBdID, BYTE* pType, LPSTR lpBuff, int nBuffSize);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_GetSlaveInfo(int iBdID, ref byte pType, StringBuilder lpBuff, int nBuffSize);
		public static int FAS_GetSlaveInfo(int iBdID, ref byte pType, ref string version)
		{
			int nRtn = FMM_OK;
			byte type = 0;
			StringBuilder sb = new StringBuilder(256);

			nRtn = FAS_GetSlaveInfo(iBdID, ref type, sb, 256);
			if (nRtn == FMM_OK)
			{
				pType = type;

				version = sb.ToString();
			}

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_GetMotorInfo(int iBdID, BYTE* pType, LPSTR lpBuff, int nBuffSize);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_GetMotorInfo(int iBdID, ref byte pType, StringBuilder lpBuff, int nBuffSize);
		public static int FAS_GetMotorInfo(int iBdID, ref byte pType, ref string motor)
		{
			int nRtn = FMM_OK;
			byte type = 0;
			StringBuilder sb = new StringBuilder(256);

			nRtn = FAS_GetMotorInfo(iBdID, ref type, sb, 256);
			if (nRtn == FMM_OK)
			{
				pType = type;

				motor = sb.ToString();
			}

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_GetSlaveInfoEx(int iBdID, DRIVE_INFO* lpDriveInfo);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_GetSlaveInfoEx(int iBdID, byte[] lpDriveInfo);
		public static int FAS_GetSlaveInfoEx(int iBdID, ref DRIVE_INFO DriveInfo)
		{
			int nRtn = FMM_OK;
			byte[] buffer = new byte[DRIVE_INFO.BUFF_SIZE];

			nRtn = FAS_GetSlaveInfoEx(iBdID, buffer);
			if (nRtn == FMM_OK)
			{
				DriveInfo.copy(buffer);
			}

			return nRtn;
		}

		////------------------------------------------------------------------------------
		////			Parameter Functions
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_SaveAllParameters(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SaveAllParameters(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_SetParameter(int iBdID, BYTE iParamNo, long lParamValue);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetParameter(int iBdID, byte iParamNo, int lParamValue);

		//EZI_PLUSE_API int WINAPI	FAS_GetParameter(int iBdID, BYTE iParamNo, long* lParamValue);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetParameter(int iBdID, byte iParamNo, ref int lParamValue);

		//EZI_PLUSE_API int WINAPI	FAS_GetROMParameter(int iBdID, BYTE iParamNo, long* lRomParam);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetROMParameter(int iBdID, byte iParamNo, ref int lRomParam);

		////------------------------------------------------------------------------------
		////			IO Functions
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_SetIOInput(int iBdID, DWORD dwIOSETMask, DWORD dwIOCLRMask);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetIOInput(int iBdID, uint dwIOSETMask, uint dwIOCLRMask);

		//EZI_PLUSE_API int WINAPI	FAS_GetIOInput(int iBdID, DWORD* dwIOInput);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetIOInput(int iBdID, ref uint dwIOInput);

		//EZI_PLUSE_API int WINAPI	FAS_SetIOOutput(int iBdID, DWORD dwIOSETMask, DWORD dwIOCLRMask);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetIOOutput(int iBdID, uint dwIOSETMask, uint dwIOCLRMask);

		//EZI_PLUSE_API int WINAPI	FAS_GetIOOutput(int iBdID, DWORD* dwIOOutput);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetIOOutput(int iBdID, ref uint dwIOOutput);

		//EZI_PLUSE_API int WINAPI	FAS_GetIOAssignMap(int iBdID, BYTE iIOPinNo, DWORD* dwIOLogicMask, BYTE* bLevel);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetIOAssignMap(int iBdID, byte iIOPinNo, ref uint dwIOLogicMask, ref byte bLevel);

		//EZI_PLUSE_API int WINAPI	FAS_SetIOAssignMap(int iBdID, BYTE iIOPinNo, DWORD dwIOLogicMask, BYTE bLevel);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetIOAssignMap(int iBdID, byte iIOPinNo, uint dwIOLogicMask, byte bLevel);

		//EZI_PLUSE_API int WINAPI	FAS_IOAssignMapReadROM(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_IOAssignMapReadROM(int iBdID);

		////------------------------------------------------------------------------------
		////			Servo Driver Control Functions
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_ServoEnable(int iBdID, BOOL bOnOff);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ServoEnable(int iBdID, int bOnOff);

		//EZI_PLUSE_API int WINAPI	FAS_ServoAlarmReset(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ServoAlarmReset(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_StepAlarmReset(int iBdID, BOOL bReset);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_StepAlarmReset(int iBdID, int bReset);

		//EZI_PLUSE_API int WINAPI	FAS_BrakeSet(int iBdID, BOOL bSet, int* nResult);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_BrakeSet(int iBdID, int bSet, ref int nResult);

		////------------------------------------------------------------------------------
		////			Read Status and Position
		////------------------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_GetAxisStatus(int iBdID, DWORD* dwAxisStatus);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetAxisStatus(int iBdID, ref uint dwAxisStatus);

		//EZI_PLUSE_API int WINAPI	FAS_GetIOAxisStatus(int iBdID, DWORD* dwInStatus, DWORD* dwOutStatus, DWORD* dwAxisStatus);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetIOAxisStatus(int iBdID, ref uint dwInStatus, ref uint dwOutStatus, ref uint dwAxisStatus);

		//EZI_PLUSE_API int WINAPI	FAS_GetMotionStatus(int iBdID, long* lCmdPos, long* lActPos, long* lPosErr, long* lActVel, WORD* wPosItemNo);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetMotionStatus(int iBdID, ref int lCmdPos, ref int lActPos, ref int lPosErr, ref int lActVel, ref ushort wPosItemNo);

		//EZI_PLUSE_API int WINAPI	FAS_GetAllStatus(int iBdID, DWORD* dwInStatus, DWORD* dwOutStatus, DWORD* dwAxisStatus, long* lCmdPos, long* lActPos, long* lPosErr, long* lActVel, WORD* wPosItemNo);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetAllStatus(int iBdID, ref uint dwInStatus, ref uint dwOutStatus, ref uint dwAxisStatus, ref int lCmdPos, ref int lActPos, ref int lPosErr, ref int lActVel, ref ushort wPosItemNo);

		//EZI_PLUSE_API int WINAPI	FAS_GetAllStatusEx(int iBdID, BYTE* pTypes, long* pDatas);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetAllStatusEx(int iBdID, byte[] pTypes, int[] pDatas);

		//EZI_PLUSE_API int WINAPI	FAS_SetCommandPos(int iBdID, long lCmdPos);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetCommandPos(int iBdID, int lCmdPos);

		//EZI_PLUSE_API int WINAPI	FAS_SetActualPos(int iBdID, long lActPos);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetActualPos(int iBdID, int lActPos);

		//EZI_PLUSE_API int WINAPI	FAS_ClearPosition(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ClearPosition(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_GetCommandPos(int iBdID, long* lCmdPos);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetCommandPos(int iBdID, ref int lCmdPos);

		//EZI_PLUSE_API int WINAPI	FAS_GetActualPos(int iBdID, long* lActPos);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetActualPos(int iBdID, ref int lActPos);

		//EZI_PLUSE_API int WINAPI	FAS_GetPosError(int iBdID, long* lPosErr);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetPosError(int iBdID, ref int lPosErr);

		//EZI_PLUSE_API int WINAPI	FAS_GetActualVel(int iBdID, long* lActVel);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetActualVel(int iBdID, ref int lActVel);

		//EZI_PLUSE_API int WINAPI	FAS_GetAlarmType(int iBdID, BYTE* nAlarmType);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetAlarmType(int iBdID, ref byte nAlarmType);

		//EZI_PLUSE_API int WINAPI	FAS_GetWarningType(int iBdID, BYTE* nWarningType);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetWarningType(int iBdID, ref byte nWarningType);

		////------------------------------------------------------------------
		////			Motion Functions.
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_MoveStop(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveStop(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_EmergencyStop(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_EmergencyStop(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_MovePause(int iBdID, BOOL bPause);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MovePause(int iBdID, int bPause);

		//EZI_PLUSE_API int WINAPI	FAS_MoveOriginSingleAxis(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveOriginSingleAxis(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_MoveSingleAxisAbsPos(int iBdID, long lAbsPos, DWORD lVelocity);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveSingleAxisAbsPos(int iBdID, int lAbsPos, uint lVelocity);

		//EZI_PLUSE_API int WINAPI	FAS_MoveSingleAxisIncPos(int iBdID, long lIncPos, DWORD lVelocity);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveSingleAxisIncPos(int iBdID, int lIncPos, uint lVelocity);

		//EZI_PLUSE_API int WINAPI	FAS_MoveToLimit(int iBdID, DWORD lVelocity, int iLimitDir);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveToLimit(int iBdID, uint lVelocity, int iLimitDir);

		//EZI_PLUSE_API int WINAPI	FAS_MoveVelocity(int iBdID, DWORD lVelocity, int iVelDir);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveVelocity(int iBdID, uint lVelocity, int iVelDir);

		//EZI_PLUSE_API int WINAPI	FAS_PositionAbsOverride(int iBdID, long lOverridePos);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PositionAbsOverride(int iBdID, int lOverridePos);

		//EZI_PLUSE_API int WINAPI	FAS_PositionIncOverride(int iBdID, long lOverridePos);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PositionIncOverride(int iBdID, int lOverridePos);

		//EZI_PLUSE_API int WINAPI	FAS_VelocityOverride(int iBdID, DWORD lVelocity);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_VelocityOverride(int iBdID, uint lVelocity);

		//EZI_PLUSE_API int WINAPI	FAS_MoveLinearAbsPos(BYTE nNoOfBds, int* iBdID, long* lplAbsPos, DWORD lFeedrate, WORD wAccelTime);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveLinearAbsPos(byte nNoOfBds, int[] iBdID, int[] lplAbsPos, uint lFeedrate, ushort wAccelTime);

		//EZI_PLUSE_API int WINAPI	FAS_MoveLinearIncPos(BYTE nNoOfBds, int* iBdID, long* lplIncPos, DWORD lFeedrate, WORD wAccelTime);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveLinearIncPos(byte nNoOfBds, int[] iBdID, int[] lplIncPos, uint lFeedrate, ushort wAccelTime);

		//EZI_PLUSE_API int WINAPI	FAS_MoveLinearAbsPos2(BYTE nNoOfBds, int* iBdID, long* lplAbsPos, DWORD lFeedrate, WORD wAccelTime);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveLinearAbsPos2(byte nNoOfBds, int[] iBdID, int[] lplAbsPos, uint lFeedrate, ushort wAccelTime);

		//EZI_PLUSE_API int WINAPI	FAS_MoveLinearIncPos2(BYTE nNoOfBds, int* iBdID, long* lplIncPos, DWORD lFeedrate, WORD wAccelTime);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveLinearIncPos2(byte nNoOfBds, int[] iBdID, int[] lplIncPos, uint lFeedrate, ushort wAccelTime);

		//EZI_PLUSE_API int WINAPI	FAS_MoveCircleAbsPos1(BYTE nNoOfBds, int* iBdID, long* lplCirEndAbs, long* lplCirCenterAbs, int iDirection, DWORD lFeedrate, WORD wAccelTime, int bSCurve);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveCircleAbsPos1(byte nNoOfBds, int[] iBdID, int[] lplCirEndAbs, int[] lplCirCenterAbs, int iDirection, uint lFeedrate, ushort wAccelTime, int bSCurve);

		//EZI_PLUSE_API int WINAPI	FAS_MoveCircleIncPos1(BYTE nNoOfBds, int* iBdID, long* lplCirEndInc, long* lplCirCenterInc, int iDirection, DWORD lFeedrate, WORD wAccelTime, int bSCurve);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveCircleIncPos1(byte nNoOfBds, int[] iBdID, int[] lplCirEndInc, int[] lplCirCenterInc, int iDirection, uint lFeedrate, ushort wAccelTime, int bSCurve);

		//EZI_PLUSE_API int WINAPI	FAS_MoveCircleAbsPos2(BYTE nNoOfBds, int* iBdID, long* lplCirEndAbs, DWORD lRadius, int iDirection, DWORD lFeedrate, WORD wAccelTime, int bSCurve);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveCircleAbsPos2(byte nNoOfBds, int[] iBdID, int[] lplCirEndAbs, uint lRadius, int iDirection, uint lFeedrate, ushort wAccelTime, int bSCurve);

		//EZI_PLUSE_API int WINAPI	FAS_MoveCircleIncPos2(BYTE nNoOfBds, int* iBdID, long* lplCirEndInc, DWORD lRadius, int iDirection, DWORD lFeedrate, WORD wAccelTime, int bSCurve);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveCircleIncPos2(byte nNoOfBds, int[] iBdID, int[] lplCirEndInc, uint lRadius, int iDirection, uint lFeedrate, ushort wAccelTime, int bSCurve);

		//EZI_PLUSE_API int WINAPI	FAS_MoveCircleAbsPos3(BYTE nNoOfBds, int* iBdID, long* lplCirCenterAbs, DWORD nAngle, int iDirection, DWORD lFeedrate, WORD wAccelTime, int bSCurve);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveCircleAbsPos3(byte nNoOfBds, int[] iBdID, int[] lplCirCenterAbs, uint nAngle, int iDirecion, uint lFeedrate, ushort wAccelTime, int bSCurve);

		//EZI_PLUSE_API int WINAPI	FAS_MoveCircleIncPos3(BYTE nNoOfBds, int* iBdID, long* lplCirCenterInc, DWORD nAngle, int iDirection, DWORD lFeedrate, WORD wAccelTime, int bSCurve);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MoveCircleIncPos3(byte nNoOfBds, int[] iBdID, int[] lplCirCenterInc, uint nAngle, int iDirecion, uint lFeedrate, ushort wAccelTime, int bSCurve);

		//EZI_PLUSE_API int WINAPI	FAS_TriggerOutput_RunA(int iBdID, BOOL bStartTrigger, long lStartPos, DWORD dwPeriod, DWORD dwPulseTime);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_TriggerOutput_RunA(int iBdID, int bStartTrigger, int lStartPos, uint dwPeriod, uint dwPulseTime);

		//EZI_PLUSE_API int WINAPI	FAS_TriggerOutput_Status(int iBdID, BYTE* bTriggerStatus);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_TriggerOutput_Status(int iBdID, ref byte bTriggerStatus);

		//EZI_PLUSE_API int WINAPI	FAS_SetTriggerOutputEx(int iBdID, BYTE nOutputNo, BYTE bRun, WORD wOnTime, BYTE nTriggerCount, long* arrTriggerPosition);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetTriggerOutputEx(int iBdID, byte nOutputNo, byte bRun, ushort wOnTime, byte nTriggerCount, int[] arrTriggerPosition);

		//EZI_PLUSE_API int WINAPI	FAS_GetTriggerOutputEx(int iBdID, BYTE nOutputNo, BYTE* bRun, WORD* wOnTime, BYTE* nTriggerCount, long* arrTriggerPosition);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetTriggerOutputEx(int iBdID, byte nOutputNo, ref byte bRun, ref ushort wOnTime, ref byte nTriggerCount, int[] arrTriggerPosition);

		//EZI_PLUSE_API int WINAPI	FAS_MovePush(int iBdID, DWORD dwStartSpd, DWORD dwMoveSpd, long lPosition, WORD wAccel, WORD wDecel, WORD wPushRate, DWORD dwPushSpd, long lEndPosition, WORD wPushMode);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_MovePush(int iBdID, uint dwStartSpd, uint dwMoveSpd, int lPosition, ushort wAccel, ushort wDecel, ushort wPushRate, uint dwPushSpd, int lEndPosition, ushort wPushMode);

		//EZI_PLUSE_API int WINAPI	FAS_GetPushStatus(int iBdID, BYTE* nPushStatus);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetPushStatus(int iBdID, ref byte nPushStatus);

		////------------------------------------------------------------------
		////			Ex-Motion Functions.
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_MoveSingleAxisAbsPosEx(int iBdID, long lAbsPos, DWORD lVelocity, MOTION_OPTION_EX* lpExOption);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_MoveSingleAxisAbsPosEx(int iBdID, int lAbsPos, uint lVelocity, byte[] lpExOption);
		public static int FAS_MoveSingleAxisAbsPosEx(int iBdID, int lAbsPos, uint lVelocity, MOTION_OPTION_EX ExOption)
		{
			int nRtn = FMM_OK;
			byte[] buff = new byte[MOTION_OPTION_EX.BUFF_SIZE];

			ExOption.copyto(buff);

			nRtn = FAS_MoveSingleAxisAbsPosEx(iBdID, lAbsPos, lVelocity, buff);

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_MoveSingleAxisIncPosEx(int iBdID, long lIncPos, DWORD lVelocity, MOTION_OPTION_EX* lpExOption);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_MoveSingleAxisIncPosEx(int iBdID, int lIncPos, uint lVelocity, byte[] lpExOption);
		public static int FAS_MoveSingleAxisIncPosEx(int iBdID, int lIncPos, uint lVelocity, MOTION_OPTION_EX ExOption)
		{
			int nRtn = FMM_OK;
			byte[] buff = new byte[MOTION_OPTION_EX.BUFF_SIZE];

			ExOption.copyto(buff);

			nRtn = FAS_MoveSingleAxisIncPosEx(iBdID, lIncPos, lVelocity, buff);

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_MoveVelocityEx(int iBdID, DWORD lVelocity, int iVelDir, VELOCITY_OPTION_EX* lpExOption);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_MoveVelocityEx(int iBdID, uint lVelocity, int iVelDir, byte[] lpExOption);
		public static int FAS_MoveVelocityEx(int iBdID, uint lVelocity, int iVelDir, VELOCITY_OPTION_EX ExOption)
		{
			int nRtn = FMM_OK;
			byte[] buff = new byte[VELOCITY_OPTION_EX.BUFF_SIZE];

			ExOption.copyto(buff);

			nRtn = FAS_MoveVelocityEx(iBdID, lVelocity, iVelDir, buff);

			return nRtn;
		}

		////------------------------------------------------------------------
		////			Position Table Functions.
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_PosTableReadItem(int iBdID, WORD wItemNo, LPITEM_NODE lpItem);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_PosTableReadItem(int iBdID, ushort wItemNo, byte[] lpItem);
		public static int FAS_PosTableReadItem(int iBdID, ushort wItemNo, ref ITEM_NODE Item)
		{
			int nRtn = FMM_OK;
			byte[] buff = new byte[ITEM_NODE.BUFF_SIZE];

			nRtn = FAS_PosTableReadItem(iBdID, wItemNo, buff);
			if (nRtn == FMM_OK)
			{
				Item.copyfrom(buff);
			}

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_PosTableWriteItem(int iBdID, WORD wItemNo, LPITEM_NODE lpItem);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_PosTableWriteItem(int iBdID, ushort wItemNo, byte[] lpItem);
		public static int FAS_PosTableWriteItem(int iBdID, ushort wItemNo, ITEM_NODE Item)
		{
			int nRtn = FMM_OK;
			byte[] buff = new byte[ITEM_NODE.BUFF_SIZE];

			Item.copyto(buff);

			nRtn = FAS_PosTableWriteItem(iBdID, wItemNo, buff);

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_PosTableWriteROM(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PosTableWriteROM(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_PosTableReadROM(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PosTableReadROM(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_PosTableRunItem(int iBdID, WORD wItemNo);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PosTableRunItem(int iBdID, ushort wItemNo);

		//EZI_PLUSE_API int WINAPI	FAS_PosTableReadOneItem(int iBdID, WORD wItemNo, WORD wOffset, long* lPosItemVal);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PosTableReadOneItem(int iBdID, ushort wItemNo, ushort wOffset, ref int lPosItemVal);

		//EZI_PLUSE_API int WINAPI	FAS_PosTableWriteOneItem(int iBdID, WORD wItemNo, WORD wOffset, long lPosItemVal);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PosTableWriteOneItem(int iBdID, ushort wItemNo, ushort wOffset, int lPosItemVal);

		//EZI_PLUSE_API int WINAPI	FAS_PosTableSingleRunItem(int iBdID, BOOL bNextMove, WORD wItemNo);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_PosTableSingleRunItem(int iBdID, int bNextMove, ushort wItemNo);

		////------------------------------------------------------------------
		////					Gap Control Functions.
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI		FAS_GapControlEnable(int iBdID, WORD wItemNo, long lGapCompSpeed, long lGapAccTime, long lGapDecTime, long lGapStartSpeed);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GapControlEnable(int iBdID, ushort wItemNo, int lGapCompSpeed, int lGapAccTime, int lGapDecTime, int lGapStartSpeed);

		//EZI_PLUSE_API int WINAPI		FAS_GapControlDisable(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GapControlDisable(int iBdID);

		//EZI_PLUSE_API int WINAPI		FAS_IsGapControlEnable(int iBdID, BOOL* bIsEnable, WORD* wCurrentItemNo);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_IsGapControlEnable(int iBdID, ref int bIsEnable, ref ushort wCurrentItemNo);

		//EZI_PLUSE_API int WINAPI		FAS_GapControlGetADCValue(int iBdID, long* lADCValue);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GapControlGetADCValue(int iBdID, ref int lADCValue);

		//EZI_PLUSE_API int WINAPI		FAS_GapOneResultMonitor(int iBdID, BYTE* bUpdated, long* iIndex, long* lGapValue, long* lCmdPos, long* lActPos, long* lCompValue, long* lReserved);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GapOneResultMonitor(int iBdID, ref byte bUpdated, ref int iIndex, ref int lGapValue, ref int lCmdPos, ref int lActPos, ref int lCompValue, ref int lReserved);

		////------------------------------------------------------------------
		////					Alarm Type History Functions.
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_GetAlarmLogs(int iBdID, ALARM_LOG* pAlarmLog);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_GetAlarmLogs(int iBdID, byte[] pAlarmLog);
		public static int FAS_GetAlarmLogs(int iBdID, ref ALARM_LOG AlarmLog)
		{
			int nRtn = FMM_OK;
			byte[] buff = new byte[ALARM_LOG.BUFF_SIZE];

			nRtn = FAS_GetAlarmLogs(iBdID, buff);
			if (nRtn == FMM_OK)
			{
				AlarmLog.copy(buff);
			}

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_ResetAlarmLogs(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ResetAlarmLogs(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_GetWarningLogs(int iBdID, ALARM_LOG* pWarningLog);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_GetWarningLogs(int iBdID, byte[] pWarningLog);
		public static int FAS_GetWarningLogs(int iBdID, ref ALARM_LOG WarningLog)
		{
			int nRtn = FMM_OK;
			byte[] buff = new byte[ALARM_LOG.BUFF_SIZE];

			nRtn = FAS_GetWarningLogs(iBdID, buff);
			if (nRtn == FMM_OK)
			{
				WarningLog.copy(buff);
			}

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_ResetWarningLogs(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ResetWarningLogs(int iBdID);

		////------------------------------------------------------------------
		////					I/O Module Functions.
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_GetInput(int iBdID, unsigned long* uInput, unsigned long* uLatch);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetInput(int iBdID, ref uint uInput, ref uint uLatch);

		//EZI_PLUSE_API int WINAPI	FAS_ClearLatch(int iBdID, unsigned long uLatchMask);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ClearLatch(int iBdID, uint uLatchMask);

		//EZI_PLUSE_API int WINAPI	FAS_GetLatchCount(int iBdID, unsigned char iInputNo, unsigned long* uCount);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetLatchCount(int iBdID, uint iInputNo, ref uint uCount);

		//EZI_PLUSE_API int WINAPI	FAS_GetLatchCountAll(int iBdID, unsigned long* puAllCount);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetLatchCountAll(int iBdID, uint[] puAllCount);

		//EZI_PLUSE_API int WINAPI	FAS_GetLatchCountAll32(int iBdID, unsigned long* puAllCount);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetLatchCountAll32(int iBdID, uint[] puAllCount);

		//EZI_PLUSE_API int WINAPI	FAS_ClearLatchCount(int iBdID, unsigned long uInputMask);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ClearLatchCount(int iBdID, uint uInputMask);

		//EZI_PLUSE_API int WINAPI	FAS_GetOutput(int iBdID, unsigned long* uOutput, unsigned long* uStatus);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetOutput(int iBdID, ref uint uOutput, ref uint uStatus);

		//EZI_PLUSE_API int WINAPI	FAS_SetOutput(int iBdID, unsigned long uSetMask, unsigned long uClearMask);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetOutput(int iBdID, uint uSetMask, uint uClearMask);

		//EZI_PLUSE_API int WINAPI	FAS_SetTrigger(int iBdID, unsigned char uOutputNo, TRIGGER_INFO* pTrigger);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_SetTrigger(int iBdID, byte uOutputNo, byte[] pTrigger);
		public static int FAS_SetTrigger(int iBdID, byte uOutputNo, TRIGGER_INFO trigger)
		{
			int nRtn = FMM_OK;

			nRtn = FAS_SetTrigger(iBdID, uOutputNo, trigger.ByteArray);
			if (nRtn == FMM_OK)
			{
			}

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_SetRunStop(int iBdID, unsigned long uRunMask, unsigned long uStopMask);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetRunStop(int iBdID, uint uRunMask, uint uStopMask);

		//EZI_PLUSE_API int WINAPI	FAS_GetTriggerCount(int iBdID, unsigned char uOutputNo, unsigned long* uCount);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetTriggerCount(int iBdID, uint uOutputNo, ref uint uCount);

		//EZI_PLUSE_API int WINAPI	FAS_GetIOLevel(int iBdID, unsigned long* uIOLevel);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetIOLevel(int iBdID, ref uint uIOLevel);

		//EZI_PLUSE_API int WINAPI	FAS_SetIOLevel(int iBdID, unsigned long uIOLevel);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetIOLevel(int iBdID, uint uIOLevel);

		//EZI_PLUSE_API int WINAPI	FAS_LoadIOLevel(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_LoadIOLevel(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_SaveIOLevel(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SaveIOLevel(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_GetInputFilter(int iBdID, unsigned short* filter);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetInputFilter(int iBdID, ref ushort filter);

		//EZI_PLUSE_API int WINAPI	FAS_SetInputFilter(int iBdID, unsigned short filter);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetInputFilter(int iBdID, ushort filter);

		//EZI_PLUSE_API int WINAPI	FAS_GetIODirection(int iBdID, unsigned long* direction);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetIODirection(int iBdID, ref uint direction);

		//EZI_PLUSE_API int WINAPI	FAS_SetIODirection(int iBdID, unsigned long direction);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetIODirection(int iBdID, uint direction);

		////------------------------------------------------------------------
		////			Ez-IO AD Functions
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_SetADConfig(int iBdID, BYTE channel, enum AD_DATA_TYPE type, long value, long* recv);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetADConfig(int iBdID, byte channel, int type, int value, ref int recv);

		//EZI_PLUSE_API int WINAPI	FAS_GetADConfig(int iBdID, BYTE channel, enum AD_DATA_TYPE type, long* value);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetADConfig(int iBdID, byte channel, int type, ref int value);

		//EZI_PLUSE_API int WINAPI	FAS_LoadADConfig(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_LoadADConfig(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_SaveADConfig(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SaveADConfig(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_ReadADValue(int iBdID, BYTE channel, short* advalue);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_ReadADValue(int iBdID, byte channel, ref short advalue);

		//EZI_PLUSE_API int WINAPI	FAS_ReadADAllValue(int iBdID, BYTE offset, AD_BUFFER* adbuffer);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_ReadADAllValue(int iBdID, byte offset, byte[] adbuffer);
		public static int FAS_ReadADAllValue(int iBdID, byte offset, out AD_BUFFER adbuffer)
		{
			byte[] buffer = new byte[16];
			int nRtn = FMM_OK;

			nRtn = FAS_ReadADAllValue(iBdID, offset, buffer);

			adbuffer = new AD_BUFFER(buffer);

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_GetAllADResult(int iBdID, AD_RESULT* result);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_GetAllADResult(int iBdID, byte[] result);
		public static int FAS_GetAllADResult(int iBdID, out AD_RESULT result)
		{
			byte[] buffer = new byte[112];
			int nRtn = FMM_OK;

			nRtn = FAS_GetAllADResult(iBdID, buffer);

			result = new AD_RESULT(buffer);

			return nRtn;
		}

		//EZI_PLUSE_API int WINAPI	FAS_GetADResult(int iBdID, BYTE channel, float* adresult);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetADResult(int iBdID, byte channel, ref float adresult);

		//EZI_PLUSE_API int WINAPI	FAS_SetADRange(int iBdID, BYTE channel, enum AD_RANGE range);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_SetADRange(int iBdID, byte channel, int range);
		public static int FAS_SetADRange(int iBdID, byte channel, AD_RANGE range)
		{
			return FAS_SetADRange(iBdID, channel, (int)range);
		}

		////------------------------------------------------------------------
		////			Ezi-IO DA Functions
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_SetDACConfig(int iBdID, BYTE channel, DAC_CONFIG type, long data, long* recv);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetDACConfig(int iBdID, byte channel, DAC_CONFIG type, int data, ref int recv);

		//EZI_PLUSE_API int WINAPI	FAS_GetDACConfig(int iBdID, BYTE channel, DAC_CONFIG type, long* data);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetDACConfig(int iBdID, byte channel, DAC_CONFIG type, ref int data);

		//EZI_PLUSE_API int WINAPI	FAS_LoadDACConfig(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_LoadDACConfig(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_SaveDACConfig(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SaveDACConfig(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_SetDACValue(int iBdID, BYTE channel, BOOL bEnable, long value);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetDACValue(int iBdID, byte channel, int bEnable, int value);

		//EZI_PLUSE_API int WINAPI	FAS_GetDACValue(int iBdID, BYTE channel, BOOL* bEnable, long* value);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetDACValue(int iBdID, byte channel, ref int bEnable, ref int value);

		////------------------------------------------------------------------
		////			Ezi-IO Counter Functions
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_CounterCommand(int iBdID, BYTE channel, COUNTER_CMD cmd, long data);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_CounterCommand(int iBdID, byte channel, COUNTER_CMD cmd, int data);

		//EZI_PLUSE_API int WINAPI	FAS_GetCounterValue(int iBdID, BYTE channel, COUNTER_VALUE type, long* value);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetCounterValue(int iBdID, byte channel, COUNTER_VALUE type, ref int value);

		//EZI_PLUSE_API int WINAPI	FAS_GetCounterStatus(int iBdID, DWORD* dwStatus);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetCounterStatus(int iBdID, ref uint dwStatus);

		//EZI_PLUSE_API int WINAPI	FAS_SetCounterTrigger(int iBdID, BYTE channel, BOOL bStartTrigger, long lStartPos, DWORD dwPeriod, DWORD dwPulseTime, DWORD dwTriggerCnt);
		[DllImport("EziMOTIONPlusE.dll")]
		protected static extern int FAS_SetCounterTrigger(int iBdID, byte channel, int bStartTrigger, int lStartPos, uint dwPeriod, uint dwPulseTime, uint dwTriggerCnt);
		public static int FAS_SetCounterTrigger(int iBdID, byte channel, bool bStartTrigger, int lStartPos, uint dwPeriod, uint dwPulseTime, uint dwTriggerCnt)
		{
			return FAS_SetCounterTrigger(iBdID, channel, bStartTrigger ? 1 : 0, lStartPos, dwPeriod, dwPulseTime, dwTriggerCnt);
		}

		//EZI_PLUSE_API int WINAPI	FAS_SetCounterConfig(int iBdID, BYTE channel, COUNTER_CONFIG type, long data, long* recv);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SetCounterConfig(int iBdID, byte channel, COUNTER_CONFIG type, int data, ref int recv);

		//EZI_PLUSE_API int WINAPI	FAS_GetCounterConfig(int iBdID, BYTE channel, COUNTER_CONFIG type, long* data);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_GetCounterConfig(int iBdID, byte channel, COUNTER_CONFIG type, ref int data);

		//EZI_PLUSE_API int WINAPI	FAS_LoadCounterConfig(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_LoadCounterConfig(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_SaveCounterConfig(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SaveCounterConfig(int iBdID);

		////------------------------------------------------------------------
		////			Ezi-SPEED Functions
		////------------------------------------------------------------------
		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetInputStatus(int iBdID, DWORD* dwDirectInput);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetInputStatus(int iBdID, ref uint dwDirectInput);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetOutputStatus(int iBdID, DWORD* dwDirectOutput);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetOutputStatus(int iBdID, ref uint dwDirectOutput);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetNETStatus(int iBdID, DWORD* dwNETStatus);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetNETStatus(int iBdID, ref uint dwNETStatus);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDSetOutput(int iBdID, DWORD dwIOSETMask, DWORD dwIOCLRMask);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDSetOutput(int iBdID, uint dwIOSETMask, uint dwIOCLRMask);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetCommandSpeed(int iBdID, int* nSpeed);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetCommandSpeed(int iBdID, ref int nSpeed);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetFeedbackSpeed(int iBdID, int* nSpeed);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetFeedbackSpeed(int iBdID, ref int nSpeed);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetGearSpeed(int iBdID, float* fSpeed);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetGearSpeed(int iBdID, ref float fSpeed);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetConveyorSpeed(int iBdID, float* fSpeed);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetConveyorSpeed(int iBdID, ref float fSpeed);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetOperationNo(int iBdID, BYTE* nOpNo);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetOperationNo(int iBdID, ref byte nOpNo);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDMoveVelocity(int iBdID, USHORT uSpeed, BOOL bForward, USHORT uAccTime, USHORT uDecTime, USHORT uTorqueLimit);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDMoveVelocity(int iBdID, ushort uSpeed, int bForward, ushort uAccTime, ushort uDecTime, ushort uTorqueLimit);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDSetOperationData(int iBdID, BYTE nOpNo, USHORT uSpeed, USHORT uAccTime, USHORT uDecTime, USHORT uTorqueLimit);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDSetOperationData(int iBdID, byte nOpNo, ushort uSpeed, ushort uAccTime, ushort uDecTime, ushort uTorqueLimit);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDGetOperationData(int iBdID, BYTE nOpNo, USHORT* uSpeed, USHORT* uAccTime, USHORT* uDecTime, USHORT* uTorqueLimit);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDGetOperationData(int iBdID, byte nOpNo, ref ushort uSpeed, ref ushort uAccTime, ref ushort uDecTime, ref ushort uTorqueLimit);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDRunOperationData(int iBdID, BYTE nOpNo, BOOL bForward);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDRunOperationData(int iBdID, byte nOpNo, int bForward);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDLoadOperationData(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDLoadOperationData(int iBdID);

		//EZI_PLUSE_API int WINAPI	FAS_SPEEDSaveOperationData(int iBdID);
		[DllImport("EziMOTIONPlusE.dll")]
		public static extern int FAS_SPEEDSaveOperationData(int iBdID);
	}
}
