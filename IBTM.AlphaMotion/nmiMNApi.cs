/******************************************************************************
*
*	File Version: 1,0,1,1
*
*	Copyright(c) 2012 Alpha Motion Co,. Ltd. All Rights Reserved.
*
*	This file is strictly confidential and do not distribute it outside.
*
*	MODULE NAME :
*		nmiMNApi.cs
*
*	AUTHOR :
*		K.C. Lee
*
*	DESCRIPTION :
*		the header file for RC files of project.
*
*
* - Phone: +82-31-303-5050
* - Fax  : +82-31-303-5051
* - URL  : http://www.alphamotion.co.kr,
*
*
/****************************************************************************/

using System.Runtime.InteropServices;

public class nmiMNApi
{
    //========================================================================================================
    //                                   Motion-NET FUNCTIONS
    //========================================================================================================
    //bManual = TRUE, Con Number is set to dip switch(Default)
    //npNumCons In a computer-set number of board
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSysLoad(int bManual, ref int npNumCons);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSysUnload();

    //통신 초기화
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSysComm(int nCon);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiReset(int nCon);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConInit(int nCon);

    //사이클릭 통신
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCyclicBegin(int nCon);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCyclicEnd(int nCon);

    //통신 속도
    //0 : 2.5 Mbps, 1 : 5 Mbps, 2 : 10 Mbps, 3 : 20 Mbps
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSetCommSpeed(int nCon, int nCommSpeed);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetCommSpeed(int nCon, ref int npCommSpeed);

    //====================== Motion Parameter Management Function ============================================
    //파라미터 파일 읽어오기(.PRM)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConParamLoad();

    //파라미터 파일 저장하기(.PRM)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConParamSave();

    //======================  Motion interface I/O Configure and Control Function ============================
    //nState=0, emSERVO_OFF.
    //nState=1, emSERVO_ON.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoOn(int nCon, int nAxis, int nState);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoOn(int nCon, int nAxis, ref int npState);

    //nState=0, Reset Off
    //nState=1, Reset On
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoReset(int nCon, int nAxis, int nState);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoReset(int nCon, int nAxis, ref int npState);

    //inp_logic=0, active LOW.(Default)
    //inp_logic=1, active HIGH.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoInpLevel(int nCon, int nAxis, int nLevel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoInpLevel(int nCon, int nAxis, ref int npLevel);

    //inp_enable=0, Disabled (Default)
    //inp_enable=1, Enabled
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoInpEnable(int nCon, int nAxis, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoInpEnable(int nCon, int nAxis, ref int npEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoInp(int nCon, int nAxis, ref int nInp);

    //alm_logic=0, active LOW. (Default)
    //alm_logic=1, active HIGH.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoAlarmLevel(int nCon, int nAxis, int nLevel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoAlarmLevel(int nCon, int nAxis, ref int npLevel);

    //alm_mode=0, motor immediately stops(Default)
    //alm_mode=1, motor decelerates then stops
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoAlarmAction(int nCon, int nAxis, int nAction);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoAlarmAction(int nCon, int nAxis, ref int npAction);

    //el_logic=0, active LOW.(Default)
    //el_logic=1, active HIGH.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetLimitLevel(int nCon, int nAxis, int nLevel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetLimitLevel(int nCon, int nAxis, ref int npLevel);

    //el_act=0, motor immediately stops.(Default)
    //el_act=1, motor decelerates then stops.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetLimitAction(int nCon, int nAxis, int nAction);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetLimitAction(int nCon, int nAxis, ref int npAction);

    //org_logic=0, active LOW.(Default)
    //org_logic=1, active HIGH.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetOrgLevel(int nCon, int nAxis, int nLevel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetOrgLevel(int nCon, int nAxis, ref int npLevel);

    //CPhase_logic=0, active LOW.(Default)
    //CPhase_logic=1, active HIGH.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetEzLevel(int nCon, int nAxis, int nLevel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetEzLevel(int nCon, int nAxis, ref int npLevel);

    //soft limit of positive direction -134,217,728 <= P <= 134,217,727
    //soft limit of negative direction -134,217,728 <= N <= 134,217,727
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSoftLimitPos(int nCon, int nAxis, double dLimitP, double dLimitN);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSoftLimitPos(int nCon, int nAxis, ref double dpLimitP, ref double dpLimitN);

    //Action: The reacting method of soft limit
    //Action =0, INT
    //Action =1, Immediately stop
    //Action =2, slow down then stop
    //Action =3, Change Velocity
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSoftLimitAction(int nCon, int nAxis, int nAction);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSoftLimitAction(int nCon, int nAxis, ref int npAction);

    //0 softlimit disale (default)
    //1 softlimit enable
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSoftLimitEnable(int nCon, int nAxis, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSoftLimitEnable(int nCon, int nAxis, ref int npEnable);

    //limit pos ( 0 <= ResetPos <= 134217727)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetRCountResetPos(int nCon, int nAxis, double dPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetRCountResetPos(int nCon, int nAxis, ref double dpPos);

    //0 ring counter(Position RollOver) disale (default)
    //1 ring counter(Position RollOver) enable
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetRCountEnable(int nCon, int nAxis, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetRCountEnable(int nCon, int nAxis, ref int npEnable);

    //erc_logic=0, active LOW.(Default)
    //erc_logic=1, active HIGH    
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoCrcLevel(int nCon, int nAxis, int nLevel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoCrcLevel(int nCon, int nAxis, ref int npLevel);

    //crc_on_time=0 12us
    //crc_on_time=1 102us
    //crc_on_time=2 409us
    //crc_on_time=3 1.6ms
    //crc_on_time=4 13ms
    //crc_on_time=5 52ms
    //crc_on_time=6 104ms (Default)
    //crc_on_time=7 erc level out 
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoCrcTime(int nCon, int nAxis, int nOnTime);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoCrcTime(int nCon, int nAxis, ref int npOnTime);

    //Automatically outputs an CRC signal when the axis is stopped immediately by a +EL,-EL, ALM, or #CEMG input signal. However, the CRC signal is not output when a
    //deceleration stop occurs on the axis. 
    //Even if the EL signal is specified for a normal stop, by setting MOD = "010X000" (feed to the EL position) in the RMD register, 
    //the CRC signal is output if an immediate stop occurs
    //crc_enable=0, manual error counter clear output 
    //crc_enable=1, automatic error counter clear output (Default)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoCrcEnable(int nCon, int nAxis, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetServoCrcEnable(int nCon, int nAxis, ref int npEnable);

    //crc signal active command
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoCrcOn(int nCon, int nAxis);

    //crc signal deactive command
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetServoCrcReset(int nCon, int nAxis);

    //sd_logic=0, active LOW.(Default)
    //sd_logic=1, active HIGH.  
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSdLevel(int nCon, int nAxis, int nLevel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSdLevel(int nCon, int nAxis, ref int npLevel);

    //sd_mode=0, slow down only(Default)
    //sd_mode=1, slow down then stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSdAction(int nCon, int nAxis, int nAction);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSdAction(int nCon, int nAxis, ref int npAction);

    // SDLT Specify the latch function of the SD input. (0: OFF. 1: ON.)
    //sd_latch=0, do not latch.(Default)
    //sd_latch=1, latch.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSdLatch(int nCon, int nAxis, int nLatch);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSdLatch(int nCon, int nAxis, ref int npLatch);

    //Decelerates (deceleration stop) by turning ON the input
    ////Connected to DI[0]
    //enable=0, Disabled (Default)
    //enable=1, Enabled
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSdEnable(int nCon, int nAxis, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSdEnable(int nCon, int nAxis, ref int npEnable);

    //target=0, PA/PB.
    //target=1, EA/EB/EZ.
    //enable=0.
    //enable=1.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetFilterABEnable(int nCon, int nAxis, int nTarget, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetFilterABEnable(int nCon, int nAxis, int nTarget, ref int npEnable);

    //Command pulse signal output mode
    //0 OUT/DIR OUT Falling edge, DIR+ is high level
    //1 OUT/DIR OUT Rising edge,  DIR+ is high level
    //2 OUT/DIR OUT Falling edge, DIR+ is low level
    //3 OUT/DIR OUT Rising edge,  DIR+ is low level
    //4 CW/CCW Falling edge(Default)
    //5 CW/CCW Rising edge
    //6 CW/CCW Falling edge Inverse
    //7 CW/CCW Rising  edge Inverse
    //8 Two Phase Mode Pulse Phase lead than Dir
    //9 Two Phase Mode Dir   Phase lead than Pulse
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetPulseType(int nCon, int nAxis, int nType);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetPulseType(int nCon, int nAxis, ref int npType);

    //Encoder pulse input signal mode
    //0x00 1X A/B
    //0x01 2X A/B
    //0x02 4X A/B(Default)
    //0x03 CW/CCW
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetEncType(int nCon, int nAxis, int nType);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetEncType(int nCon, int nAxis, ref int npType);

    //Feedback counter UP / DOWN direction is opposite
    //0 normal counting(Default)
    //1 reverse counting
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetEncDir(int nCon, int nAxis, int nDir);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetEncDir(int nCon, int nAxis, ref int npDir);

    //0 Command pulse(Default) 
    //1 External Feedback   
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetEncCount(int nCon, int nAxis, int nCount);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetEncCount(int nCon, int nAxis, ref int npCount);

    //Motion setting the Velocity limit
    //0 < dVel <= 6553500 ) [pps]  
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetMaxVel(int nCon, int nAxis, double dVel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetMaxVel(int nCon, int nAxis, ref double dpVel);

    //Set the jOG start velocity
    //0 <= dVel <= 6553500 ) [pps] 
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetInitJogVel(int nCon, int nAxis, double dVel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetInitJogVel(int nCon, int nAxis, ref double dpVel);

    //Set the POS start velocity
    //0 <= dVel <= 6553500 ) [pps] 
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetInitVel(int nCon, int nAxis, double dVel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetInitVel(int nCon, int nAxis, ref double dpVel);

    //Jog Motion transfer Velocity setting standards
    //nType = 0 Constant
    //nType = 1 T-Curve
    //nType = 2 S-Curve
    //0 <= dVel <= 6553500 [pps]
    //0 <= Tacc < 60000  [ms]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetJogVelProf(int nCon, int nAxis, int nProfileType, double dVel, double dTacc);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetJogVelProf(int nCon, int nAxis, ref int npProfileType, ref double dpVel, ref double dpTacc);

    //Motion transfer Velocity setting standards
    //nType = 0 Constant
    //nType = 1 T-Curve
    //nType = 2 S-Curve
    //0 <= dVel <= 6553500 [pps]
    //0 <= Tacc < 60000  [ms]
    //0 <= Tdec < 60000  [ms]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetVelProf(int nCon, int nAxis, int nProfileType, double dVel, double dTacc, double dTdec);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetVelProf(int nCon, int nAxis, ref int npProfileType, ref double dpVel, ref double dpTacc, ref double dpTdec);

    //Set the deceleration start point detection method
    //nType = 0 AutoDetect
    //nType = 1 ManulDetect
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetDecelType(int nCon, int nAxis, int nType);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetDecelType(int nCon, int nAxis, ref int npType);

    //The ramping down point is the position where deceleration starts. 
    //The data is stored as pulse count, and it cause the axis start to decelerate when remaining pulse count reach the data.
    //-8388608 <= dPul <= 8388607
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetRemainPul(int nCon, int nAxis, double dPulse);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetRemainPul(int nCon, int nAxis, ref double dpPulse);

    //+el.-el.sd.org. alm.inp
    //+el.-el.sd.org. alm.inp
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetFilterEnable(int nCon, int nAxis, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetFilterEnable(int nCon, int nAxis, ref int npEnable);

    //====================== HOME-RETURN FUNCTIONS ===========================================================

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetResetPos(int nCon, int nAxis, int nResetPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetResetPos(int nCon, int nAxis, ref int npResetPos);

    //Automatically outputs an CRC signal when the axis is stopped immediately by a ORG input signal. However, the CRC signal is not output when a
    //deceleration stop occurs on the axis. 
    //Even if the EL signal is specified for a normal stop, by setting MOD = "010X000" (feed to the EL position) in the RMD register, 
    //the CRC signal is output if an immediate stop occurs
    //crc_enable=0
    //crc_enable=1
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetCrcEnable(int nCon, int nAxis, int nEnable);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetCrcEnable(int nCon, int nAxis, ref int npEnable);

    //Home search mode setting one axis
    //TYPE=0 ORG ON -> Slow down -> Stop 
    //TYPE=1 ORG ON -> Stop -> Go back(Rev Spd) -> ORG OFF -> Go forward(Rev Spd) -> ORG ON -> Stop(Default)
    //TYPE=2 ORG ON -> Slow down(Low Spd) -> Stop on EZ signal
    //TYPE=3 ORG ON -> EZ signal -> Slow down -> Stop
    //TYPE=4 ORG ON -> Stop -> Go back(Rev Spd) -> ORG OFF -> Stop on EZ signal
    //TYPE=5 ORG ON -> Stop -> Go back(High Spd) -> ORG OFF -> EZ signal -> Slow down -> Stop
    //TYPE=6 EL ON -> Stop -> Go back(Rev Spd) -> EL OFF -> Stop
    //TYPE=7 EL ON -> Stop -> Go back(Rev Spd) -> EL OFF -> Stop on EZ signal
    //TYPE=8 EL ON -> Stop -> Go back(High Spd) -> EL OFF -> Stop on EZ signal
    //TYPE=9 ORG ON -> Slow down -> Stop -> Go back -> Stop at beginning edge of ORG
    //TYPE=10 ORG ON -> EZ signal -> Slow down -> Stop -> Go back -> Stop at beginning edge of EZ;
    //TYPE=11 ORG ON -> Slow down -> Stop -> Go back (High Spd) -> ORG OFF -> EZ signal -> Slow down -> Stop -> Go forward(High Spd) -> Stop at beginning edge of EZ
    //TYPE=12 EL ON -> Stop -> Go back (High Spd) -> EL OFF -> EZ signal -> Slow down -> Stop -> Go forward(High Spd) -> Stop at beginning edge of EZ
    //TYPE=13 EZ signal -> Slow down -> Stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetType(int nCon, int nAxis, int nType);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetType(int nCon, int nAxis, ref int npType);

    //Home search direction of one axis
    //home_dir = 0, CW direction (Default)
    //home_dir = 1, CCW direction
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetDir(int nCon, int nAxis, int nDir);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetDir(int nCon, int nAxis, ref int npDir);

    //The origin of the origin of the behavior to escape from the distance sensor
    //pps ( -134217728 ~ 134217727 )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetEscapePul(int nCon, int nAxis, double dEscape);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetEscapePul(int nCon, int nAxis, ref double dpEscape);

    //EZ Count
    //nEzCount(1 ~ 16)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetEzCount(int nCon, int nAxis, int nEzCnt);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetEzCount(int nCon, int nAxis, ref int npEzCnt);

    //Driving home from the relative position of one axis to complete the search
    //pps ( -134217728 ~ 134217727 )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetShiftDist(int nCon, int nAxis, double dShift);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetShiftDist(int nCon, int nAxis, ref double dpShift);

    //pps ( 0 ~ 6553500 )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetInitVel(int nCon, int nAxis, double dVel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetInitVel(int nCon, int nAxis, ref double dpVel);

    //Home search drive Vel setting one axis
    //nType=0 Constant
    //nType=1 T-Curve (Default)
    //nType=2 S-Curve
    //dVel    pps ( 0 ~ 6553500 )
    //dRevVel pps ( 0 ~ 6553500 )
    //0 <= Tacc < 60000  [ms]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetVelProf(int nCon, int nAxis, int nProfileType, double dVel, double dRevVel, double dTacc);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetVelProf(int nCon, int nAxis, ref int npProfileType, ref double dpVel, ref double dpRevVel, ref double dpTacc);

    //One axis driving home search
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeMove(int nCon, int nAxis);

    //Multi axis driving home search
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxHomeMove(int nCon, int nNAxis, int[] naAxis);

    //Home search check one axis driving
    //0 stop
    //1 Home search driving
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeCheckDone(int nCon, int nAxis, ref int npDone);

    //Home search has been completed successfully one axis
    //0 Home search incomplete one axis
    //1 Home search completed one axis
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeSetStatus(int nCon, int nAxis, int nStatus);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxHomeGetStatus(int nCon, int nAxis, ref int npStatus);

    //======================  Velocity mode And Single Axis Position Motion Configure  =======================

    //Driving one axis continuous operation
    //PMI_DIR_P                      0  //Positive Direction
    //PMI_DIR_N                      1  //Negative Direction
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxJogMove(int nCon, int nAxis, int nDir);

    //One axis transported to a specified location
    //nAbsMode = 0 Driving the relative coordinates
    //nAbsMode = 1 Driving the absolute coordinate
    //-134217728 <= pos <= 134217727
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxPosMove(int nCon, int nAxis, int nAbsMode, double dPos);

    //nDone = 0 stop
    //nDone = 1 driving
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxCheckDone(int nCon, int nAxis, ref int npDone);

    //One axis deceleration stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxStop(int nCon, int nAxis);

    //One axis emergency stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxEStop(int nCon, int nAxis);

    //======================  Velocity mode And Multi Axis Point to Point Motion Configure  ==================

    //Driving multi axis continuous operation
    //PMI_DIR_P                      0  //Positive Direction
    //PMI_DIR_N                      1  //Negative Direction
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxJogMove(int nCon, int nNAxis, int[] naAxis, int[] naDir);

    //Multi axis transported to a specified location
    //0 Driving the relative coordinates
    //1 Driving the absolute coordinate
    //-134217728 <= pos <= 134217727
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxPosMove(int nCon, int nNAxis, int nAbsMode, int[] naAxis, double[] daDist);

    //motion check multi axis driving
    //0 stop
    //1 driving
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxCheckDone(int nCon, int nNAxis, int[] naAxis, ref int npDone);

    //Multi axis deceleration stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxStop(int nCon, int nNAxis, int[] naAxis);

    //Multi axis emergency stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxEStop(int nCon, int nNAxis, int[] naAxis);

    //====================== Motion I/O Monitoring FUNCTIONS =================================================

    //BIT0 EMG pin input
    //BIT1 ALM Alarm Signal
    //BIT2 +EL Positive Limit Switch
    //BIT3 -EL Negative Limit Switch
    //BIT4 ORG Origin Switch
    //BIT5 DIR DIR output( 0 : +방향 , 1 : -방향)
    //BIT6 Home Success
    //BIT7 PCS PCS signal input
    //BIT8 CRC CRC pin output
    //BIT9 EZ Index signal
    //BIT10 CLR 입력 신호 상태
    //BIT11 Latch Latch signal input
    //BIT12 SD Slow Down signal input
    //BIT13 INP In-Position signal input
    //BIT14 SVON Servo-ON output status
    //BIT15 ServoAlarmReset output status
    //BIT16 STA signal input
    //BIT17 STP signal input
    //BIT18 SYNC Pos Error signal input
    //BIT19 GANT Pos Erorr signal input
    //BIT20 DRV	 구동중
    //BIT21 CMP  CMP 동작중
    //BIT22 SYNC  SYNC 동작중
    //BIT23 GANT  GANT 동작중
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetMechanical(int nCon, int nAxis, ref uint npMechanical);

    //BIT0 DRV_STOP:   1;	모션 구동 중
    //BIT1 WAIT_DR:    1;	DR 입력 신호 기다림
    //BIT2 WAIT_STA:   1;	STA 입력 신호 기다림
    //BIT3 WAIT_INSYNC:1;	내부 동기 신호 기다림
    //BIT4 WAIT_OTHER: 1;	타축 정지 신호 기다림
    //BIT5 WAIT_:   1;	ERC 출력 완료 기다림
    //BIT6 WAIT_DIR:   1;	방향 변화를 기다림
    //BIT7 BACKLASH:   1;	BACKLASH 상태
    //BIT8 WAIT_PE:    1;	PE 입력 신호 기다림
    //BIT9 IN_FA:      1;	FA 정속 동작 중 ( 홈 스페셜 속도 )
    //BIT10 IN_FL:     1;	FL 정속 동작 중
    //BIT11 IN_FUR:    1;	가속 중
    //BIT12 IN_FH:     1;	FH 정속 동작 중 
    //BIT13 IN_FDR:    1;	감속 중 
    //BIT14 WAIT_INP:  1;	INP 입력 신호 기다림
    //BIT15 IN_CMP:    1;	CMP 동작중
    //BIT16 WAIT_SYNC:  1;	SYNC 동작중
    //BIT17 WAIT_GANT: 1;	GANT 동작중
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetMotion(int nCon, int nAxis, ref uint npMotion);

    //BIT0 EMG Error
    //BIT1 ALM Alarm Signal Error
    //BIT2 +EL Positive Limit Switch Error
    //BIT3 -EL Negative Limit Switch Error
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetErrStatus(int nCon, int nAxis, ref uint npErrStatus);

    //current Vel in pps
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetCmdVel(int nCon, int nAxis, ref double dpVel);

    //Command position counter value
    //-134,217,728 <= <= 143,217,727
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetCmdPos(int nCon, int nAxis, double dPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetCmdPos(int nCon, int nAxis, ref double dpPos);

    //Feedback position counter value
    //-134,217,728 <= <= 143,217,727
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetActPos(int nCon, int nAxis, double dPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetActPos(int nCon, int nAxis, ref double dpPos);

    //Position error counter value
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetPosError(int nCon, int nAxis, double dPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetPosError(int nCon, int nAxis, ref double dpPos);

    //sync_mode=1, 내부 동기 시작 신호에의해 시작 
    //sync_mode=2, 지정축의 정지에 의해 시작
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSyncType(int nCon, int nAxis, int nType);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSyncType(int nCon, int nAxis, ref int npType);

    //다른 축 동기 구동 동기 신호
    //sync_condition=0 내부 동기 신호 출력 OFF
    //sync_condition=1 가속 개시 때 이송 
    //sync_condition=2 가속 종료 때 이송
    //sync_condition=3 감속 개시 때 이송
    //sync_condition=4 감속 종료 때 이송
    //sync_condition=5 -SL 신호가 검출 되었을 때 이송 시작
    //sync_condition=6 +SL 신호가 검출 되었을 때 이송 시작
    //sync_condition=7 범용 비교기기에 설정된 조건이 만족하면 이송 
    //sync_condition=8 TRG-CMP 조건이 만족되었을 때 이송을 시작합니다. 
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetSyncAction(int nCon, int nAxis, int nMaskAxes, int nContion);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetSyncAction(int nCon, int nAxis, ref int npMaskAxes, ref int npContion);

    //This function is select a auto speed change method to use when the comparator conditions are satisfied. 
    //By giving an tolerance value
    //Counter = 0 , Command counter
    //Counter = 1 , Feedback counter
    //Counter = 2 , Error Counter 
    //Counter = 3 , General Counter
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetCmpModifySource(int nCon, int nAxis, int nCounter);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetCmpModifySource(int nCon, int nAxis, ref int npCounter);

    //Method =0, 조건 성립 하지 않음
    //Method =1, Counter 방향과 무관
    //Method =2, Counter Up 중
    //Method =3, Counter Down 중
    //Action =0, 사용하지 않음
    //Action =1, Immediately stop
    //Action =2, slow down then stop
    //Action =3, 속도 변경
    // -134217728 < dPos < 134217727 [pps]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetCmpModifyAction(int nCon, int nAxis, int nMethod, int nAction, double dPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetCmpModifyAction(int nCon, int nAxis, ref int npMethod, ref int npAction, ref double dpPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxSetCmpModifyVel(int nCon, int nAxis, double dVel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxGetCmpModifyVel(int nCon, int nAxis, ref double dpVel);

    //====================== Overriding FUNCTIONS ============================================================

    //new target position -134,217,728 <= new pos <= 134,217,727
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxModifyPos(int nCon, int nAxis, double dPos);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxModifyPos(int nCon, int nNAxis, int[] naAxes, double[] daPos);

    //new Vel in pps 1 ~ 6553500
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxModifyVel(int nCon, int nAxis, double dOvr);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMxModifyVel(int nCon, int nNAxis, int[] naAxes, double[] daOvr);

    //new Vel in pps 1 ~ 6553500
    //0 <= Tacc < 60000  [ms]
    //0 <= Tdec < 60000  [ms]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiAxModifyVelProf(int nCon, int nAxis, double dVel, double dTacc, double dTdec);

    //====================== Coordinat Motion Control ========================================================

    //nCS = 0,1,2,3
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsSetAxis(int nCon, int nCs, int nNAxis, int[] naAxis);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsSetInitVel(int nCon, int nCs, double dVel);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsGetInitVel(int nCon, int nCs, ref double dpVel);

    //Op_type = 0 vector vel, 
    //Op_type = 1 long axis vel, 
    //nType=0 Constant
    //nType=1 T-Curve (Default)
    //nType=2 S-Curve
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsSetVelProf(int nCon, int nCs, int nOpType, int nProfileType, double dVel, double dTacc, double dTdec);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsGetVelProf(int nCon, int nCs, int nOpType, ref int npProfileType, ref double dpVel, ref double dTacc, ref double dTdec);

    //Line Coordinate Motion 
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsLinMove(int nCon, int nCs, int nAbsMode, double[] daPos);

    //End Point Circular Coordinate Motion 
    //0 : CW
    //1 : CCW
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsArcPMove(int nCon, int nCs, int nAbsMode, double[] daCent, double[] daPos, int nDir);

    //Angle Circular Coordinate Motion 
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsArcAMove(int nCon, int nCs, int nAbsMode, double[] daCent, double dAngle);

    //-- Helical Motion -------------------
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsHelMove(int nCon, int nHelId, double dCenX, double dCenY, double dPosX, double dPosY, double dPosZ, int nDir);

    //motion check Coordinate axis driving
    //0 stop
    //1 driving
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsCheckDone(int nCon, int nCs, ref int npDone);

    //Multi axis deceleration stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsStop(int nCon, int nCs);

    //Multi axis emergency stop
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsEStop(int nCon, int nCs);

    //======================Continuous Motion ========================================================

    //연속 보간 구동을 위해 저장된 내부 Queue를 모두 삭제하는 함수이다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContClearQueue(int nCon);

    //지정된 좌표계에 연속보간에서 수행할 작업들의 등록을 시작한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContBeginQueue(int nCon);

    //지정된 좌표계에서 연속보간을 수행할 작업들의 등록을 종료한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContEndQueue(int nCon);

    //저장된 내부 연속 보간 Queue의 구동을 시작하는 함수이다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContMove(int nCon);

    //저장된 내부 연속 보간 Queue의 구동을 정지하는 함수이다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContStop(int nCon);

    //연속 보간 구동 중 현재 구동중인 연속 보간 인덱스 번호를 확인하는 함수이다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContGetCurIndex(int nCon, int nLsi, ref int npIndex);

    //보간이송축이 0 ~ 3축이면 nLsi = 0
    //보간이송축이 4 ~ 7축이면 nLsi = 1
    //저장된 내부 연속 보간 Queue의 복수개의 구동을 시작하는 함수이다
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContMoveEx(int nCon, int nLsi);


    //보간이송축이 0 ~ 3축이면 nLsi = 0
    //보간이송축이 4 ~ 7축이면 nLsi = 1
    //저장된 내부 연속 보간 Queue의 복수개의 구동을 정지하는 함수이다
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCsContStopEx(int nCon, int nLsi);

    //====================== Position Compare ========================================================

    //Position Trigger 출력 신호 Active Level를 설정한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    //nLevel         : 0    emLOGIC_A   A접점(NORMAL OPEN) 및 Active Low Level Trigger
    //               : 1    emLOGIC_B   B접점(NORMAL CLOSE) 및 Active High Level Trigger
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpSetLevel(int nCon, int nCmp, int nLevel);

    //Position Trigger 출력 신호 Active Level를 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpGetLevel(int nCon, int nCmp, ref int npLevel);

    //Position Trigger를 출력할 축를 설정한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpSetAxis(int nCon, int nCmp, int nAxis);

    //Position Trigger를 출력할 축를 반환한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpGetAxis(int nCon, int nCmp, ref int npAxis);

    //Position Trigger 출력 신호 펄스 폭를 설정한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    //nPul           : 1 ~ 50000(Pulses)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpSetHoldTime(int nCon, int nCmp, int nPulse);

    //Position Trigger 출력 신호 펄스 폭를 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpGetHoldTime(int nCon, int nCmp, ref int npPulse);

    //Position Trigger 출력 신호를 사용자 지정한 위치에서 한 개의 트리거 펄스를 출력한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    //dPos           :
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpSetSinglePos(int nCon, int nCmp, int nMethod, double dPos);

    //Position Trigger 출력 신호를 사용자 지정한 위치 구간에서 트리거 펄스를 출력한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    //nMethod        : 0    emEQ_PDIR   - Counting up 중
    //               : 1    emEQ_NDIR   - Counting down 중
    //dNPos          :  -134217728 ~ +134217727    트리거 출력 시작 위치
    //dPPos          :  -134217728 ~ +134217727    트리거 출력 종료 위치
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpSetRangePos(int nCon, int nCmp, int nMethod, double dNPos, double dPPos);

    //Position Trigger 출력 신호를 사용자 지정한 시작위치부터 종료위치까지 일정구간마다 트리거 출력을 설정한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    //nMethod        : 0    emEQ_PDIR   - Counting up 중
    //               : 1    emEQ_NDIR   - Counting down 중
    //nNum           : 0 ~ 1024                    - 출력할 갯수
    //dSPos          :  -134217728 ~ +134217727    - 트리거 출력 시작 위치
    //dDist          : 0 ~ +134217727              - 트리거 출력 주기 간격
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpSetMultPos(int nCon, int nCmp, int nMethod, int nNum, double dSPos, double dDist);

    //Position Trigger 출력 신호를 사용자 지정한 시작위치부터 종료위치까지 일정구간마다 트리거 출력을 설정한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    //nMethod        : 0    emEQ_PDIR  - Counting up 중
    //               : 1    emEQ_NDIR  - Counting down 중
    //nNum           : 0 ~ 1024        - 출력할 갯수(배열 갯수)
    //daPos          :                 -트리거 출력 위치배열(nNum 설정한 개수보다 같거나 크게 선언해야됨)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpSetPosTable(int nCon, int nCmp, int nMethod, int nNum, double[] daPos);

    //Position Trigger 출력 신호 트리거를 시작한다.
    //nCmp           : 0   비교기[0]
    //               : 1    비교기[1]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpBegin(int nCon, int nCmp);

    //Position Trigger 출력 신호 트리거를 해체한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpEnd(int nCon, int nCmp);

    //Position Compare Trigger 신호 출력 발생 할 위치값을 반환한다.
    //nCmp           : 0    비교기[0]
    //               : 1    비교기[1]
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCmpGetPos(int nCon, int nCmp, ref int npNum, ref double dpPos);

    //====================== Master/Slave Motion Control ========================================================

    //동기 제어 마스터축을 설정한다.
    //int nMAxisNo   :   마스터축 번호( 0 ~ [최대 축개수 - 1] )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncSetMaster(int nCon, int nMAxis);

    //동기 제어 마스터축을 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncGetMaster(int nCon, ref int npMAxis);

    //동기 제어 오차 검출시 동기 오차 알람 발생 여부을 설정한다.
    //int nSAxis   : 슬레이브축 번호( 0 ~ [최대 축개수 - 1] )
    //nAction    : 0 emNOTUSED    - 동기 오차 알람 발생하지 않음
    //             1 emUSED      -  동기 오차 알람 발생
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncSetAction(int nCon, int nSAxis, int nAction);

    //동기 제어 오차 검출시 동기 오차 알람를 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncGetAction(int nCon, int nSAxis, ref int npAction);

    //동기 제어 오차 검출시 동기 오차 알람 발생 여부을 설정한다.
    //int nSAxis   : 슬레이브축 번호( 0 ~ [최대 축개수 - 1] )
    //dLimit     : 1 ~ 134217727   - 마스터 축과 슬레이브 사이의 제어 편차 허용량
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncSetPosErrorLimit(int nCon, int nSAxis, double dLimit);

    //동기 제어 오차 검출시 동기 오차 알람를 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncGetPosErrorLimit(int nCon, int nSAxis, ref double dpLimit);

    //동기 제어 현재 오차 값, 최대 오차값을 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncGetPosError(int nCon, int nSAxis, ref double dpError, ref double dpMaxError);

    //Position Trigger 출력 신호 트리거를 시작한다.
    //int nSAxis   : 슬레이브축 번호( 0 ~ [최대 축개수 - 1] )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncBegin(int nCon, int nSAxis);

    //지정 축에 대하여 마스터 축과 동기를 해체 시킨다.
    //int nSAxis   : 슬레이브축 번호( 0 ~ [최대 축개수 - 1] )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiSyncEnd(int nCon, int nSAxis);

    //====================== Gantry Motion Control ========================================================

    //겐트리(Gantry) 제어 마스터축을 설정한다.
    //int nMAxisNo   :   마스터축 번호( 0 ~ [최대 축개수 - 1] )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantSetMaster(int nCon, int nId, int nMAxis);

    //겐트리(Gantry) 제어 마스터축을 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantGetMaster(int nCon, int nId, ref int npMAxis);

    //겐트리(Gantry) 제어 오차 검출시 동기 오차 알람을 설정한다.
    //int nSAxis   : 슬레이브축 번호( 1,3,5,7 )
    //nAction    : 0  emNOTUSED   동기 오차 알람 발생하지 않음
    //             1  emUSED      동기 오차 알람 발생
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantSetAction(int nCon, int nId, int nSAxis, int nAction);

    //겐트리(Gantry) 제어 오차 검출시 동기 오차 알람를 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantGetAction(int nCon, int nId, int nSAxis, ref int npAction);

    //겐트리(Gantry) 제어 마스터 축과 슬레이브 축 사이의 제어 편차의 허용량를 설정한다.
    //int nSAxis   : 슬레이브축 번호( 1,3,5,7 )
    //dLimit     : 1 ~ 134217727   - 마스터 축과 슬레이브 사이의 제어 편차 허용량
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantSetPosErrorLimit(int nCon, int nId, int nSAxis, double dLimit);

    //겐트리(Gantry) 제어 마스터 축과 슬레이브 축 사이의 제어 편차 허용량 설정 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantGetPosErrorLimit(int nCon, int nId, int nSAxis, ref double dpLimit);

    //겐트리(Gantry) 제어 현재 오차 값, 최대 오차값을 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantGetPosError(int nCon, int nId, int nSAxis, ref double dpError, ref double dpMaxError);

    //겐트리(Gantry) 제어 마스터 축과 슬레이브 축을 연결 시킨다.
    //int nSAxis   : 슬레이브축 번호( 1,3,5,7 )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantBegin(int nCon, int nId, int nSAxis);

    //겐트리(Gantry) 제어 마스터 축과 슬레이브 축 연결을 해제 시킨다.
    //int nSAxis   : 슬레이브축 번호( 1,3,5,7 )
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGantEnd(int nCon, int nId, int nSAxis);

    //====================== Manual Pulsar Control ========================================================

    //PA/PB(MPG) input signal mode
    //0x00 1X A/B
    //0x01 2X A/B
    //0x02 4X A/B
    //0x03 CW/CCW(Default)
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgSetInType(int nCon, int nAxis, int nDir);

    //PA/PB(MPG) input signal mode
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgGetInType(int nCon, int nAxis, ref int npDir);

    //지정 축에서 MPG(Manual Pulsar)  펄스 입력 방향을 설정한다.
    //nDir        : 0   emNORMAL    정방향
    //            : 1   emRESERVE   역방향
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgSetDir(int nCon, int nAxis, int nDir);

    //지정 축에서 MPG(Manual Pulsar)  펄스 입력 방향을 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgGetDir(int nCon, int nAxis, ref int npDir);

    //지정 축에서 MPG(Manual Pulsar)  펄스 기어비를 설정한다.
    //nMultiFactor        :  1 ~ 32      1차 출력펄스를 1 ~ 32 배수의 펄스를 재 생성
    //nDivFactor          :  1 ~ 2048    2차 출력펄스에 (nDivFactor/2048)가 곱해져서 최종 출력펄스 생성
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgSetGain(int nCon, int nAxis, int nMultiFactor, int nDivFactor);

    //지정 축에서 MPG(Manual Pulsar)  펄스 기어비를 반환한다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgGetGain(int nCon, int nAxis, ref int npMultiFactor, ref int npDivFactor);

    //지정 축에서 MPG(Manual Pulsar)  펄스 입력 작업을 수행한다..
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgBegin(int nCon, int nAxis);

    //지정 축에서 MPG(Manual Pulsar)  펄스 입력 작업을 해체한다..
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiMpgEnd(int nCon, int nAxis);


    //========================================================================================================
    //                                  Interrupt public static extern intS
    //========================================================================================================
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiIntSetHandler(int nCon, int nType, uint hWnd, ref int hHandler, uint nMsg);

    //
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiIntSetHandlerEnable(int nCon, int nEnable);

    //-------- Axis--------------------------------------------------------------------------------------
    //
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiIntSetAxisEnable(int nCon, int nAxis, uint nMask);

    //
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiIntGetAxisEnable(int nCon, int nAxis, ref uint npMask);

    //BIT0	; 자동 정지때
    //BIT1	; 다음 동작 계속 START 때
    //BIT2	; 동작용 2nd pre register 기입 가능 때
    //BIT3	; Comparator 5용 2nd pre register 기입 가능 때
    //BIT4	; 가속 개시 때
    //BIT5	; 가속 종료 때
    //BIT6	; 감속 개시 때
    //BIT7	; 감속 종료 때
    //BIT8	; Compatator1 조건 성립 때
    //BIT9	; Compatator2 조건 성립 때
    //BIT10	; Compatator3 조건 성립 때
    //BIT11	; Compatator4 조건 성립 때
    //BIT12	; Compatator6 조건 성립 때
    //BIT13	; CLR 신호 입력에 의해 COUNTER 값 RESET 때
    //BIT14	; LTC 신호 입력에 의해 COUNTER 값 LATCH 때
    //BIT15	; ORG 신호 입력에 의해 COUNTER 값 LATCH 때
    //BIT16	; SD 입력 ON 때
    //BIT17	; +DR 입력 변화 때
    //BIT18	; -DR 입력 변화 때
    //BIT19	; /STA 입력 ON 때

    //지정 축의 상태를 반환합니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiIntGetAxisStatus(int nCon, int nAxis, ref uint npStatus);

    //BIT0 STOP_BY_SLP:   1;	양의 소프트 리미트에 의해 정지
    //BIT1 STOP_BY_SLN:   1;	음의 소프트 리미트에 의해 정지
    //BIT2 STOP_BY_CMP3:  1;	비교기3에 의해 정지
    //BIT3 STOP_BY_CMP4:  1;	비교기4에 의해 정지
    //BIT4 STOP_BY_CMP5:  1;	비교기5에 의해 정지
    //BIT5 STOP_BY_ELP:   1;	+EL 에 의해 정지
    //BIT6 STOP_BY_ELN:   1;	-EL 에 의해 정지
    //BIT7 STOP_BY_ALM:   1;	알람에 의해 정지
    //BIT8 STOP_BY_STP:   1;	CSTP에 의해 정지
    //BIT9 STOP_BY_EMG:   1;	EMG에 의해 정지
    //BIT10 STOP_BY_SD:   1;	SD 입력에 의해 정지
    //BIT11 STOP_BY_DT:   1;	보간 동작 DATA 이상에 의해 정지
    //BIT12 STOP_BY_IP:   1;	보간 동작 중에 타 축의 이상 정지에 의해 동시 정지
    //BIT13 STOP_BY_PO:   1;	PA/PB 입력용 buffer counter dml overflow 에 의해 정지
    //BIT14 STOP_BY_AO:   1;	보간 동작 때의 위치 범위를 벗어나서 정지
    //BIT15	STOP_BY_EE:   1;	EA/EB 입력 에러 발생 (정지 하지 않음)
    //BIT16	STOP_BY_PE:   1;	PA/PB 입력 에러 발생 (정지 하지 않음)

    //지정 축의 Error 상태를 반환합니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiIntGetAxisErrStatus(int nCon, int nAxis, ref uint npStatus);

    //====================== DIGITAL I/O FUNCTIONS ===========================================================

    //통신 Digital In/Out FUNCTIONS
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDiGetData(int nCon, int nStNo, ref uint unpData);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDiGetBit(int nCon, int nStNo, int nBit, ref uint unpData);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDoSetData(int nCon, int nStNo, uint unData);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDoGetData(int nCon, int nStNo, ref uint unpData);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDoSetBit(int nCon, int nStNo, int nBit, uint unData);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDoGetBit(int nCon, int nStNo, int nBit, ref uint unpData);

    //====================== DEBUG-LOGGING FUNCTIONS =========================================================

    //Log 파일을 저장 합니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDLogSetFile(string szFilename);

    //Log 파일을 읽어 옵니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDLogGetFile(byte[] szFilename);

    //Log 저장 Level을 설정합니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDLogSetLevel(int nLevel);

    //Log 저장 Level을 반환합니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDLogGetLevel(ref int npLevel);

    //Log 저장 활성화를 설정합니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDLogSetEnable(int nEnable);

    //Log 저장 활성화를 반환합니다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiDLogGetEnable(ref int npEnable);

    //====================== GENERAL FUNCTIONS ===============================================================

    //모션 보드에 설치된 전체 축 개수
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGnGetAxesNumAll(int nCon, ref int npNAxesNum);

    //스테이션 별 축 개수
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGnGetAxesNumSlave(int nCon, int nStNo, ref int npNAxesNum);

    //스테이션 별 맵핑 축 범위 확인
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGnGetSlaveAxisRange(int nCon, int nStNo, ref int npInitAxis, ref int npEndAxis);

    //모델명 읽어오기
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConGetModel(int nCon, ref int npModel);

    //F/W 버전 읽어오기
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConGetFwVersion(int nCon, ref int npVer);

    //H/W 버전 읽어오기
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConGetHwVersion(int nCon, ref int npVer);

    //dll 버전 읽어오기
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConGetMApiVersion(int nCon, ref int npVer);

    //보드 LED ON/OFF
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConSetCheckOn(int nCon, int nOn);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiConGetCheckOn(int nCon, ref int npOn);

    //모션 보드에 설치된 전체 디지털 범용 I/O 개수
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGnGetDioNum(int nCon, ref int npNDiChNum, ref int npNDoChNum);

    //스테이션 별 디지털 범용 I/O 개수
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetSlaveDioNum(int nCon, int nStNo, ref int npDiNum, ref int npDoNum);

    // 전체 통신 에러 발생 횟수
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetCommErrNum(int nCon, ref int npErrNum);

    // 전체 통신 에러 발생 횟수 0으로 클리어
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCommErrNumClear(int nCon);

    // 각 스테이션 별 에러 발생 플래그
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetCyclicErrFlag(int nCon, int nStNo, ref int npFlag);

    // 각 스테이션 별 에러 발생 플래그 해제
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCyclicErrFlagClear(int nCon, int nStNo);

    // 전체 통신 에러 발생 플래그
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetCyclicErrFlagAll(int nCon, ref uint unpHighFlag, ref uint unpLowFlag);

    // 전체 통신 에러 발생 플래그 해제
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiCyclicErrFlagClearAll(int nCon);

    // 사이클릭 통신 에러 상태
    // 1 || 0  비트
    // 0    0  통신 종료
    // 0    1  사이클릭 통신 중
    // 1    0  사이클릭 통신 종료. 에러 해제 안한 상태
    // 1    1  사이클릭 통신 중 에러 발생
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetCyclicStatus(int nCon, ref uint unpStatus);

    // 사이클릭 통신 속도 확인. [us] 단위.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetCyclicSpeed(int nCon, ref int npTime);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetSlaveTotal(int nCon, ref int npSlvNum);

    // 매핑된 축의 Station 번호와, Station에서 축 번호를 가져온다. 
    // 축 번호는 Station 번호 순으로 매핑된다.
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGnCheckAxesMap(int nCon, int nAxis, ref int npActStNo, ref int npActAxis);

    //디바이스 정보
    // 2 || 1 || 0  비트
    // 0    0    0  : 32점 출력 전용
    // 0    1    0  : 16점 출력 16점 입력 
    // 1    1    1  : 32점 입력 전용
    // 3            비트
    // 0            : I/O 전용
    // 1            : Motion 전용
    // 4            비트
    // 0            : 사용 안함
    // 1            : 사용
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetSlaveInfo(int nCon, int nStNo, ref int npSlvInfo);

    // 1: 사이클릭 통신 에러 발생
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetEIOE(int nCon, ref uint unpStatus);

    // 1: 사이클릭 통신 중
    [DllImport("nmiMNApi.dll")]
    public static extern int nmiGetSBSY(int nCon, ref uint unpStatus);

    //====================== ERROR HANDLING FUNCTIONS ========================================================

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiErrGetCode(int nCon, ref int nCode);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiErrGetString(int nCon, int nCode, byte[] szStr);

    [DllImport("nmiMNApi.dll")]
    public static extern int nmiErrAxGetCode(int nCon, int nAxis, ref int npCode);
}
