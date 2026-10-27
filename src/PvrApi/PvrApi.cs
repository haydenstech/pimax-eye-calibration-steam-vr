// Compile-time declarations of the PVR API surface that OpenVRPatch implements.
//
// This assembly is a build reference only and is never distributed. It is named
// "Managed-PVR" so that, at runtime, OpenVRPatch.dll binds to the Managed-PVR.dll
// already installed with the game. Only names, field types and enum values are
// declared; they must match the shipped assembly for the binding to succeed.
using System;

namespace PVR;

public enum pvrResult
{
	pvr_success = 0,
	pvr_failed = 1,
	pvr_invalid_param = 5
}

public enum pvrEyeType
{
	pvrEye_Left = 0,
	pvrEye_Right = 1
}

public enum pvrTrackingOrigin
{
	pvrTrackingOrigin_EyeLevel = 0,
	pvrTrackingOrigin_FloorLevel = 1
}

public enum pvrTrackedDeviceType
{
	pvrTrackedDevice_HMD = 1,
	pvrTrackedDevice_LeftController = 2,
	pvrTrackedDevice_RightController = 4
}

[Flags]
public enum pvrButton
{
	pvrButton_System = 1,
	pvrButton_ApplicationMenu = 2,
	pvrButton_Trigger = 4,
	pvrButton_Grip = 8,
	pvrButton_TouchPad = 16,
	pvrButton_JoyStick = 32,
	pvrButton_A = 64,
	pvrButton_B = 128
}

public enum pvrDispStateType
{
	pvrDispState_Direct = 1
}

public enum pvrEyeRotateType
{
	pvrEye_rotate_0 = 0
}

public enum pvrEyeDisplayType
{
	pvrEyeDisplay_horz_side_by_side = 0
}

public enum pvrLogLevel
{
}

public enum pvrTrackedDeviceProp
{
	pvrTrackedDeviceCap_BatteryLevel_int = 3,
	pvrTrackedDeviceCap_BatteryPercent_int = 4,
	pvrTrackedDeviceCap_PoseRefreshRate_Float = 5
}

public struct pvrVector2i
{
	public int x;
	public int y;
}

public struct pvrVector2f
{
	public float x;
	public float y;
}

public struct pvrVector3f
{
	public float x;
	public float y;
	public float z;
}

public struct pvrQuatf
{
	public float x;
	public float y;
	public float z;
	public float w;
}

public struct pvrPosef
{
	public pvrQuatf Orientation;
	public pvrVector3f Position;
}

public struct pvrSizei
{
	public int w;
	public int h;
}

public struct pvrRecti
{
	public pvrVector2i Pos;
	public pvrSizei Size;
}

public struct pvrFovPort
{
	public float UpTan;
	public float DownTan;
	public float LeftTan;
	public float RightTan;
}

public struct pvrMatrix4f
{
	public float[] M;
}

public struct pvrHmdInfo
{
	public string ProductName;
	public string Manufacturer;
	public string SerialNumber;
	public pvrSizei Resolution;
}

public struct pvrDisplayInfo
{
	public pvrDispStateType disp_state;
	public pvrEyeRotateType eye_rotate;
	public pvrEyeDisplayType eye_display;
	public int width;
	public int height;
	public int pos_x;
	public int pos_y;
	public float refresh_rate;
}

public struct pvrEyeRenderInfo
{
	public pvrFovPort Fov;
	public pvrVector2f PixelsPerTanAngleAtCenter;
	public pvrPosef HmdToEyePose;
	public pvrRecti DistortedViewport;
}

public struct pvrHmdStatus
{
	public byte IsVisible;
	public byte HmdPresent;
	public byte HmdMounted;
	public byte DisplayLost;
	public byte ServiceReady;
	public byte ShouldQuit;
}

public struct pvrPoseStatef
{
	public pvrPosef ThePose;
	public pvrVector3f AngularVelocity;
	public pvrVector3f LinearVelocity;
	public double TimeInSeconds;
	public uint StatusFlags;
}

public struct pvrTrackingState
{
	public pvrPoseStatef HeadPose;
	public pvrPoseStatef[] HandPoses;
}

public struct pvrInputState
{
	public double TimeInSeconds;
	public uint[] HandButtons;
	public uint[] HandTouches;
	public float[] Trigger;
	public float[] Grip;
	public pvrVector2f[] TouchPad;
	public pvrVector2f[] JoyStick;
}

public struct pvrTrackerDesc
{
}

public struct pvrTrackerPose
{
}
