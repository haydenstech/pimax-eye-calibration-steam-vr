using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PVR;
using Valve.VR;

namespace OpenVRPatch;

/// <summary>
/// Replacement for the Pimax libPVRClient64 function table (PVR API 1.14), implemented on top of
/// SteamVR's openvr_api.dll.
///
/// The installer patches Managed-PVR's PVRInterface constructor to call <see cref="Bind"/>, which
/// fills each of its delegate fields with the method of the same name here. PVRClient then calls
/// these methods exactly as it called the native table.
///
/// PVR and OpenVR share the same tracking convention (right-handed, +Y up, -Z forward, metres),
/// so poses pass through without axis changes; the game converts them to Unity space itself.
/// </summary>
public sealed class PvrBackend
{
	private static readonly PvrBackend Instance = new PvrBackend();

	/// <summary>Called from the patched PVRInterface constructor with the new instance.</summary>
	public static void Bind(object pvrInterface)
	{
		int bound = 0;
		foreach (System.Reflection.FieldInfo field in pvrInterface.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
		{
			if (!typeof(Delegate).IsAssignableFrom(field.FieldType))
			{
				continue;
			}
			Delegate implementation = Delegate.CreateDelegate(field.FieldType, Instance, field.Name, ignoreCase: false, throwOnBindFailure: false);
			if (implementation == null)
			{
				Log.Error("No OpenVR implementation for PVR function '" + field.Name + "'");
				continue;
			}
			field.SetValue(pvrInterface, implementation);
			bound++;
		}
		Log.Info("Bound " + bound + " PVR functions to OpenVR.");
	}
	private const uint InvalidDevice = OpenVR.k_unTrackedDeviceIndexInvalid;

	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	private static readonly object InitLock = new object();

	private static int _hmdRefCount;

	private static CVRSystem _system;

	private static ETrackingUniverseOrigin _universe = ETrackingUniverseOrigin.TrackingUniverseSeated;

	private static bool _shouldQuit;

	private static readonly TrackedDevicePose_t[] Poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

	private static readonly Dictionary<string, object> Config = new Dictionary<string, object>();

	/// <summary>Head pose most recently handed to the app; the render path submits frames against it.</summary>
	internal static HmdMatrix34_t LastHeadPose;

	internal static bool LastHeadPoseValid;

	public pvrResult initialise()
	{
		PatchSettings.Load();
		return pvrResult.pvr_success;
	}

	public void shutdown()
	{
	}

	public pvrResult createHmd([In][Out] ref IntPtr phmdh)
	{
		lock (InitLock)
		{
			if (_system == null)
			{
				EVRInitError error = EVRInitError.None;
				CVRSystem system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Scene);
				if (error != EVRInitError.None || system == null)
				{
					Log.Error("[OpenVRPatch] OpenVR init failed: " + OpenVR.GetStringForHmdError(error) + ". Is SteamVR running?");
					return pvrResult.pvr_failed;
				}
				_system = system;
				_shouldQuit = false;
				// SteamVR's WaitGetPoses paces frames; a desktop vsync would cap rendering at the monitor rate.
				Engine.DisableVSync();
				Log.Info("[OpenVRPatch] OpenVR initialised, HMD: " + GetHmdString(ETrackedDeviceProperty.Prop_ModelNumber_String));
			}
			_hmdRefCount++;
			phmdh = new IntPtr(_hmdRefCount);
			return pvrResult.pvr_success;
		}
	}

	public void destroyHmd(IntPtr hmdh)
	{
		lock (InitLock)
		{
			if (_hmdRefCount == 0)
			{
				return;
			}
			_hmdRefCount--;
			if (_hmdRefCount == 0 && _system != null)
			{
				Log.Info("[OpenVRPatch] Shutting down OpenVR.");
				OpenVRRender.OnShutdown();
				_system = null;
				OpenVR.Shutdown();
			}
		}
	}

	public string getVersionString()
	{
		return "OpenVRPatch (PVR 1.14 over OpenVR)";
	}

	public double getTimeSeconds()
	{
		return Now();
	}

	public pvrResult getHmdInfo(IntPtr hmdh, [In][Out] ref pvrHmdInfo outInfo)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		uint w = 0u;
		uint h = 0u;
		_system.GetRecommendedRenderTargetSize(ref w, ref h);
		outInfo.ProductName = GetHmdString(ETrackedDeviceProperty.Prop_ModelNumber_String);
		outInfo.Manufacturer = GetHmdString(ETrackedDeviceProperty.Prop_ManufacturerName_String);
		outInfo.SerialNumber = GetHmdString(ETrackedDeviceProperty.Prop_SerialNumber_String);
		outInfo.Resolution = new pvrSizei
		{
			w = (int)w * 2,
			h = (int)h
		};
		return pvrResult.pvr_success;
	}

	public pvrResult getEyeDisplayInfo(IntPtr hmdh, pvrEyeType eye, [In][Out] ref pvrDisplayInfo outInfo)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		uint w = 0u;
		uint h = 0u;
		_system.GetRecommendedRenderTargetSize(ref w, ref h);
		outInfo.disp_state = pvrDispStateType.pvrDispState_Direct;
		outInfo.eye_rotate = pvrEyeRotateType.pvrEye_rotate_0;
		outInfo.eye_display = pvrEyeDisplayType.pvrEyeDisplay_horz_side_by_side;
		outInfo.width = (int)w;
		outInfo.height = (int)h;
		outInfo.pos_x = ((eye != pvrEyeType.pvrEye_Left) ? ((int)w) : 0);
		outInfo.pos_y = 0;
		outInfo.refresh_rate = DisplayFrequency();
		return pvrResult.pvr_success;
	}

	public pvrResult getEyeRenderInfo(IntPtr hmdh, pvrEyeType eye, [In][Out] ref pvrEyeRenderInfo outInfo)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		EVREye vrEye = ToVREye(eye);
		pvrFovPort fov = GetFov(vrEye);
		uint w = 0u;
		uint h = 0u;
		_system.GetRecommendedRenderTargetSize(ref w, ref h);
		outInfo.Fov = fov;
		outInfo.PixelsPerTanAngleAtCenter = new pvrVector2f
		{
			x = (float)w / (fov.LeftTan + fov.RightTan),
			y = (float)h / (fov.UpTan + fov.DownTan)
		};
		outInfo.HmdToEyePose = ToPose(_system.GetEyeToHeadTransform(vrEye));
		outInfo.DistortedViewport = new pvrRecti
		{
			Pos = default(pvrVector2i),
			Size = new pvrSizei
			{
				w = (int)w,
				h = (int)h
			}
		};
		OpenVRRender.SetEyeFov((int)eye, fov);
		return pvrResult.pvr_success;
	}

	public pvrResult getHmdStatus(IntPtr hmdh, [In][Out] ref pvrHmdStatus outInfo)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		PumpEvents();
		bool present = _system.IsTrackedDeviceConnected(OpenVR.k_unTrackedDeviceIndex_Hmd);
		outInfo.ServiceReady = 1;
		outInfo.HmdPresent = (byte)(present ? 1 : 0);
		outInfo.HmdMounted = (byte)(present ? 1 : 0);
		outInfo.IsVisible = 1;
		outInfo.DisplayLost = 0;
		outInfo.ShouldQuit = (byte)(_shouldQuit ? 1 : 0);
		return pvrResult.pvr_success;
	}

	public pvrResult setTrackingOriginType(IntPtr hmdh, pvrTrackingOrigin outOrigin)
	{
		_universe = ((outOrigin == pvrTrackingOrigin.pvrTrackingOrigin_FloorLevel) ? ETrackingUniverseOrigin.TrackingUniverseStanding : ETrackingUniverseOrigin.TrackingUniverseSeated);
		OpenVRRender.SetTrackingUniverse(_universe);
		return pvrResult.pvr_success;
	}

	public pvrResult getTrackingOriginType(IntPtr hmdh, [In][Out] ref pvrTrackingOrigin outOrigin)
	{
		outOrigin = ((_universe == ETrackingUniverseOrigin.TrackingUniverseStanding) ? pvrTrackingOrigin.pvrTrackingOrigin_FloorLevel : pvrTrackingOrigin.pvrTrackingOrigin_EyeLevel);
		return pvrResult.pvr_success;
	}

	public pvrResult recenterTrackingOrigin(IntPtr hmdh)
	{
		CVRChaperone chaperone = OpenVR.Chaperone;
		if (chaperone == null)
		{
			return pvrResult.pvr_failed;
		}
		chaperone.ResetZeroPose(_universe);
		return pvrResult.pvr_success;
	}

	public pvrResult getTrackingState(IntPtr hmdh, double absTime, [In][Out] ref pvrTrackingState outState)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		UpdatePoses(absTime);
		outState.HeadPose = ToPoseState(OpenVR.k_unTrackedDeviceIndex_Hmd, absTime);
		outState.HandPoses = new pvrPoseStatef[2]
		{
			ToPoseState(ControllerIndex(0), absTime),
			ToPoseState(ControllerIndex(1), absTime)
		};
		TrackedDevicePose_t hmd = Poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
		if (hmd.bPoseIsValid)
		{
			LastHeadPose = hmd.mDeviceToAbsoluteTracking;
			LastHeadPoseValid = true;
		}
		return pvrResult.pvr_success;
	}

	public pvrResult getInputState(IntPtr hmdh, ref pvrInputState inputState)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		inputState.TimeInSeconds = Now();
		inputState.HandButtons = new uint[2];
		inputState.HandTouches = new uint[2];
		inputState.Trigger = new float[2];
		inputState.Grip = new float[2];
		inputState.TouchPad = new pvrVector2f[2];
		inputState.JoyStick = new pvrVector2f[2];
		uint size = (uint)Marshal.SizeOf(typeof(VRControllerState_t));
		for (int hand = 0; hand < 2; hand++)
		{
			uint index = ControllerIndex(hand);
			VRControllerState_t state = default(VRControllerState_t);
			if (index == InvalidDevice || !_system.GetControllerState(index, ref state, size))
			{
				continue;
			}
			inputState.HandButtons[hand] = MapButtons(state.ulButtonPressed);
			inputState.HandTouches[hand] = MapButtons(state.ulButtonTouched);
			if (PatchSettings.TriggerAsA && (state.ulButtonPressed & ButtonMask(EVRButtonId.k_EButton_SteamVR_Trigger)) != 0)
			{
				inputState.HandButtons[hand] |= (uint)pvrButton.pvrButton_A;
			}
			inputState.Trigger[hand] = state.rAxis1.x;
			inputState.Grip[hand] = state.rAxis2.x;
			pvrVector2f axis0 = new pvrVector2f
			{
				x = state.rAxis0.x,
				y = state.rAxis0.y
			};
			inputState.TouchPad[hand] = axis0;
			inputState.JoyStick[hand] = axis0;
		}
		return pvrResult.pvr_success;
	}

	public pvrResult triggerHapticPulse(IntPtr hmdh, pvrTrackedDeviceType device, float intensity)
	{
		uint index = DeviceIndex(device);
		if (_system == null || index == InvalidDevice)
		{
			return pvrResult.pvr_failed;
		}
		_system.TriggerHapticPulse(index, 0u, (ushort)Clamp(intensity * 3999f, 0f, 3999f));
		return pvrResult.pvr_success;
	}

	// Pimax runtime settings (e.g. "eyetracking_calibrated", "disable_hmd_ipd_auto_adjust") have no
	// OpenVR equivalent. They are kept in memory so reads return what was written, and logged.
	public float getFloatConfig(IntPtr hmdh, string key, float def_val)
	{
		return GetConfig(key, def_val);
	}

	public pvrResult setFloatConfig(IntPtr hmdh, string key, float val)
	{
		return SetConfig(key, val);
	}

	public int getIntConfig(IntPtr hmdh, string key, int def_val)
	{
		return GetConfig(key, def_val);
	}

	public pvrResult setIntConfig(IntPtr hmdh, string key, int val)
	{
		return SetConfig(key, val);
	}

	public int getStringConfig(IntPtr hmdh, string key, byte[] val, int size)
	{
		byte[] bytes = Encoding.ASCII.GetBytes(GetConfig(key, ""));
		if (val != null)
		{
			int count = Math.Min(bytes.Length, size - 1);
			if (count >= 0)
			{
				Array.Copy(bytes, val, count);
				val[count] = 0;
			}
		}
		return bytes.Length + 1;
	}

	public pvrResult setStringConfig(IntPtr hmdh, string key, string val)
	{
		return SetConfig(key, val);
	}

	public pvrVector3f getVector3fConfig(IntPtr hmdh, string key, pvrVector3f def_val)
	{
		return GetConfig(key, def_val);
	}

	public pvrResult setVector3fConfig(IntPtr hmdh, string key, pvrVector3f val)
	{
		return SetConfig(key, val);
	}

	public pvrQuatf getQuatfConfig(IntPtr hmdh, string key, pvrQuatf def_val)
	{
		return GetConfig(key, def_val);
	}

	public pvrResult setQuatfConfig(IntPtr hmdh, string key, pvrQuatf val)
	{
		return SetConfig(key, val);
	}

	public double getPredictedDisplayTime(IntPtr hmdh, long frameIndex)
	{
		return Now() + PredictedSecondsFromNow();
	}

	/// <summary>
	/// PVRSurface renders each eye with a symmetric frustum spanning the larger tangent on each axis,
	/// so the texture is grown from SteamVR's recommended size to cover that wider frustum.
	/// OpenVRRender later crops it back to the real projection with texture bounds.
	/// </summary>
	public pvrResult getFovTextureSize(IntPtr hmdh, pvrEyeType eye, pvrFovPort fov, float pixelsPerDisplayPixel, [In][Out] ref pvrSizei outSize)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		uint w = 0u;
		uint h = 0u;
		_system.GetRecommendedRenderTargetSize(ref w, ref h);
		pvrFovPort raw = GetFov(ToVREye(eye));
		float scale = pixelsPerDisplayPixel * PatchSettings.ResolutionScale;
		float width = (float)w * (2f * Math.Max(fov.LeftTan, fov.RightTan)) / (raw.LeftTan + raw.RightTan) * scale;
		float height = (float)h * (2f * Math.Max(fov.UpTan, fov.DownTan)) / (raw.UpTan + raw.DownTan) * scale;
		outSize.w = Clamp((int)Math.Ceiling(width), 16, 16384);
		outSize.h = Clamp((int)Math.Ceiling(height), 16, 16384);
		Log.Info("[OpenVRPatch] Eye texture size " + outSize.w + "x" + outSize.h + " (recommended " + w + "x" + h + ")");
		return pvrResult.pvr_success;
	}

	// Same layout as LibOVR's ovrMatrix4f_Projection, which the PVR API mirrors (row-major M[row * 4 + col]).
	public void Matrix4f_Projection(pvrFovPort fov, float znear, float zfar, byte right_handled, [In][Out] ref pvrMatrix4f outMat)
	{
		float handedness = ((right_handled != 0) ? (-1f) : 1f);
		float xScale = 2f / (fov.LeftTan + fov.RightTan);
		float xOffset = (fov.LeftTan - fov.RightTan) * xScale * 0.5f;
		float yScale = 2f / (fov.UpTan + fov.DownTan);
		float yOffset = (fov.UpTan - fov.DownTan) * yScale * 0.5f;
		float[] m = new float[16];
		m[0] = xScale;
		m[2] = handedness * xOffset;
		m[5] = yScale;
		m[6] = (0f - handedness) * yOffset;
		m[10] = (0f - handedness) * zfar / (znear - zfar);
		m[11] = zfar * znear / (znear - zfar);
		m[14] = handedness;
		outMat.M = m;
	}

	// Unused by the app; returns the input projection unchanged.
	public void Matrix4f_OrthoSubProjection([In] pvrMatrix4f projection, [In] pvrVector2f orthoScale, float orthoDistance, float hmdToEyeOffsetX, [In][Out] ref pvrMatrix4f outMat)
	{
		outMat.M = (float[])projection.M.Clone();
	}

	public void calcEyePoses(pvrPosef headPose, [In][MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] pvrPosef[] hmdToEyePose, [In][Out][MarshalAs(UnmanagedType.LPArray, SizeConst = 2)] pvrPosef[] outEyePoses)
	{
		pvrQuatf head = headPose.Orientation;
		for (int i = 0; i < 2; i++)
		{
			pvrPosef offset = hmdToEyePose[i];
			pvrVector3f rotated = Rotate(head, offset.Position);
			outEyePoses[i] = new pvrPosef
			{
				Orientation = Multiply(head, offset.Orientation),
				Position = new pvrVector3f
				{
					x = headPose.Position.x + rotated.x,
					y = headPose.Position.y + rotated.y,
					z = headPose.Position.z + rotated.z
				}
			};
		}
	}

	private static pvrQuatf Multiply(pvrQuatf a, pvrQuatf b)
	{
		return new pvrQuatf
		{
			x = a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
			y = a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
			z = a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
			w = a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
		};
	}

	// v' = v + 2w(u × v) + 2u × (u × v), with u the quaternion's vector part.
	private static pvrVector3f Rotate(pvrQuatf q, pvrVector3f v)
	{
		float tx = 2f * (q.y * v.z - q.z * v.y);
		float ty = 2f * (q.z * v.x - q.x * v.z);
		float tz = 2f * (q.x * v.y - q.y * v.x);
		return new pvrVector3f
		{
			x = v.x + q.w * tx + (q.y * tz - q.z * ty),
			y = v.y + q.w * ty + (q.z * tx - q.x * tz),
			z = v.z + q.w * tz + (q.x * ty - q.y * tx)
		};
	}

	private static float Clamp(float value, float min, float max)
	{
		return Math.Min(Math.Max(value, min), max);
	}

	private static int Clamp(int value, int min, int max)
	{
		return Math.Min(Math.Max(value, min), max);
	}

	// Mirror across the YZ plane, as LibOVR's ovrPosef_FlipHandedness does.
	public pvrResult Posef_FlipHandedness([In] ref pvrPosef inPose, [In][Out] ref pvrPosef outPose)
	{
		pvrPosef pose = inPose;
		pose.Orientation.x = 0f - pose.Orientation.x;
		pose.Orientation.w = 0f - pose.Orientation.w;
		pose.Position.x = 0f - pose.Position.x;
		outPose = pose;
		return pvrResult.pvr_success;
	}

	public pvrDispStateType getDisplayState(uint edid_vid, uint edid_pid)
	{
		return pvrDispStateType.pvrDispState_Direct;
	}

	public int getClientCount()
	{
		return 1;
	}

	public int getClientPids([In][Out] uint[] pids, int buf_count)
	{
		if (pids != null && buf_count > 0)
		{
			pids[0] = (uint)Process.GetCurrentProcess().Id;
		}
		return 1;
	}

	public void logMessage(pvrLogLevel level, string message)
	{
		Log.Info("[OpenVRPatch] PVR log (" + level.ToString() + "): " + message);
	}

	public int getTrackerCount(IntPtr hmdh)
	{
		return 0;
	}

	public pvrResult getTrackerDesc(IntPtr hmdh, uint idx, [In][Out] ref pvrTrackerDesc desc)
	{
		return pvrResult.pvr_invalid_param;
	}

	public pvrResult getTrackerPose(IntPtr hmdh, uint idx, [In][Out] ref pvrTrackerPose pose)
	{
		return pvrResult.pvr_invalid_param;
	}

	public pvrResult getConnectedDevices(IntPtr hmdh, [In][Out] ref uint pDevices)
	{
		pDevices = 0u;
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		foreach (pvrTrackedDeviceType device in new pvrTrackedDeviceType[3]
		{
			pvrTrackedDeviceType.pvrTrackedDevice_HMD,
			pvrTrackedDeviceType.pvrTrackedDevice_LeftController,
			pvrTrackedDeviceType.pvrTrackedDevice_RightController
		})
		{
			uint index = DeviceIndex(device);
			if (index != InvalidDevice && _system.IsTrackedDeviceConnected(index))
			{
				pDevices |= (uint)device;
			}
		}
		return pvrResult.pvr_success;
	}

	public pvrResult getTrackedDevicePoseState(IntPtr hmdh, pvrTrackedDeviceType device, double absTime, [In][Out] ref pvrPoseStatef state)
	{
		if (_system == null)
		{
			return pvrResult.pvr_failed;
		}
		UpdatePoses(absTime);
		state = ToPoseState(DeviceIndex(device), absTime);
		return pvrResult.pvr_success;
	}

	public float getTrackedDeviceFloatProperty(IntPtr hmdh, pvrTrackedDeviceType device, pvrTrackedDeviceProp prop, float def_val)
	{
		if (prop == pvrTrackedDeviceProp.pvrTrackedDeviceCap_PoseRefreshRate_Float)
		{
			return DisplayFrequency();
		}
		return def_val;
	}

	public int getTrackedDeviceIntProperty(IntPtr hmdh, pvrTrackedDeviceType device, pvrTrackedDeviceProp prop, int def_val)
	{
		uint index = DeviceIndex(device);
		if (_system == null || index == InvalidDevice || (prop != pvrTrackedDeviceProp.pvrTrackedDeviceCap_BatteryPercent_int && prop != pvrTrackedDeviceProp.pvrTrackedDeviceCap_BatteryLevel_int))
		{
			return def_val;
		}
		ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
		float battery = _system.GetFloatTrackedDeviceProperty(index, ETrackedDeviceProperty.Prop_DeviceBatteryPercentage_Float, ref error);
		if (error != ETrackedPropertyError.TrackedProp_Success)
		{
			return def_val;
		}
		return (int)Math.Round(battery * 100f);
	}

	public int getTrackedDeviceStringProperty(IntPtr hmdh, pvrTrackedDeviceType device, pvrTrackedDeviceProp prop, byte[] val, int size)
	{
		return 0;
	}

	public pvrVector3f getTrackedDeviceVector3fProperty(IntPtr hmdh, pvrTrackedDeviceType device, pvrTrackedDeviceProp prop, pvrVector3f def_val)
	{
		return def_val;
	}

	public pvrQuatf getTrackedDeviceQuatfProperty(IntPtr hmdh, pvrTrackedDeviceType device, pvrTrackedDeviceProp prop, pvrQuatf def_val)
	{
		return def_val;
	}

	private static double Now()
	{
		return Clock.Elapsed.TotalSeconds;
	}

	private static float DisplayFrequency()
	{
		if (_system == null)
		{
			return 90f;
		}
		ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
		float hz = _system.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error);
		if (error != ETrackedPropertyError.TrackedProp_Success || !(hz > 0f))
		{
			return 90f;
		}
		return hz;
	}

	/// <summary>Seconds from now until the frame being prepared reaches the display (Valve's recommended formula).</summary>
	private static double PredictedSecondsFromNow()
	{
		if (_system == null)
		{
			return 0.0;
		}
		float sinceVsync = 0f;
		ulong frameCounter = 0uL;
		_system.GetTimeSinceLastVsync(ref sinceVsync, ref frameCounter);
		float frameDuration = 1f / DisplayFrequency();
		ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
		float vsyncToPhotons = _system.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref error);
		if (error != ETrackedPropertyError.TrackedProp_Success)
		{
			vsyncToPhotons = 0f;
		}
		return Math.Max(0.0, (double)(frameDuration * (1f + PatchSettings.PredictionFrames) - sinceVsync + vsyncToPhotons));
	}

	private static void UpdatePoses(double absTime)
	{
		float secondsFromNow = (float)Math.Max(0.0, absTime - Now());
		_system.GetDeviceToAbsoluteTrackingPose(_universe, secondsFromNow, Poses);
	}

	private static void PumpEvents()
	{
		VREvent_t ev = default(VREvent_t);
		uint size = (uint)Marshal.SizeOf(typeof(VREvent_t));
		while (_system.PollNextEvent(ref ev, size))
		{
			if (ev.eventType == 700)
			{
				Log.Info("[OpenVRPatch] SteamVR requested quit.");
				_system.AcknowledgeQuit_Exiting();
				_shouldQuit = true;
			}
		}
	}

	private static EVREye ToVREye(pvrEyeType eye)
	{
		if (eye != pvrEyeType.pvrEye_Left)
		{
			return EVREye.Eye_Right;
		}
		return EVREye.Eye_Left;
	}

	/// <summary>OpenVR raw tangents are signed with +Y down; PVR tangents are all positive.</summary>
	private static pvrFovPort GetFov(EVREye eye)
	{
		float left = 0f;
		float right = 0f;
		float top = 0f;
		float bottom = 0f;
		_system.GetProjectionRaw(eye, ref left, ref right, ref top, ref bottom);
		return new pvrFovPort
		{
			LeftTan = 0f - left,
			RightTan = right,
			UpTan = 0f - top,
			DownTan = bottom
		};
	}

	private static uint ControllerIndex(int hand)
	{
		return _system.GetTrackedDeviceIndexForControllerRole((hand != 0) ? ETrackedControllerRole.RightHand : ETrackedControllerRole.LeftHand);
	}

	private static uint DeviceIndex(pvrTrackedDeviceType device)
	{
		if (_system == null)
		{
			return InvalidDevice;
		}
		return device switch
		{
			pvrTrackedDeviceType.pvrTrackedDevice_HMD => OpenVR.k_unTrackedDeviceIndex_Hmd,
			pvrTrackedDeviceType.pvrTrackedDevice_LeftController => ControllerIndex(0),
			pvrTrackedDeviceType.pvrTrackedDevice_RightController => ControllerIndex(1),
			_ => InvalidDevice,
		};
	}

	private static ulong ButtonMask(EVRButtonId id)
	{
		return (ulong)(1L << (int)id);
	}

	private static uint MapButtons(ulong buttons)
	{
		uint result = 0u;
		if ((buttons & ButtonMask(EVRButtonId.k_EButton_System)) != 0)
		{
			result |= (uint)pvrButton.pvrButton_System;
		}
		if ((buttons & ButtonMask(EVRButtonId.k_EButton_ApplicationMenu)) != 0)
		{
			// Legacy bindings report the upper face button (B/Y) as ApplicationMenu.
			result |= (uint)(pvrButton.pvrButton_ApplicationMenu | pvrButton.pvrButton_B);
		}
		if ((buttons & ButtonMask(EVRButtonId.k_EButton_A)) != 0)
		{
			result |= (uint)pvrButton.pvrButton_A;
		}
		if ((buttons & ButtonMask(EVRButtonId.k_EButton_Grip)) != 0)
		{
			result |= (uint)pvrButton.pvrButton_Grip;
		}
		if ((buttons & ButtonMask(EVRButtonId.k_EButton_SteamVR_Trigger)) != 0)
		{
			result |= (uint)pvrButton.pvrButton_Trigger;
		}
		if ((buttons & ButtonMask(EVRButtonId.k_EButton_SteamVR_Touchpad)) != 0)
		{
			result |= (uint)(pvrButton.pvrButton_TouchPad | pvrButton.pvrButton_JoyStick);
		}
		return result;
	}

	private static pvrPoseStatef ToPoseState(uint index, double absTime)
	{
		pvrPoseStatef state = default(pvrPoseStatef);
		state.TimeInSeconds = absTime;
		state.ThePose.Orientation.w = 1f;
		if (index == InvalidDevice || index >= Poses.Length)
		{
			return state;
		}
		TrackedDevicePose_t pose = Poses[index];
		if (!pose.bPoseIsValid)
		{
			return state;
		}
		state.ThePose = ToPose(pose.mDeviceToAbsoluteTracking);
		state.LinearVelocity = new pvrVector3f
		{
			x = pose.vVelocity.v0,
			y = pose.vVelocity.v1,
			z = pose.vVelocity.v2
		};
		state.AngularVelocity = new pvrVector3f
		{
			x = pose.vAngularVelocity.v0,
			y = pose.vAngularVelocity.v1,
			z = pose.vAngularVelocity.v2
		};
		state.StatusFlags = 3u;
		return state;
	}

	internal static pvrPosef ToPose(HmdMatrix34_t m)
	{
		float w = (float)Math.Sqrt(Math.Max(0f, 1f + m.m0 + m.m5 + m.m10)) * 0.5f;
		float x = (float)Math.Sqrt(Math.Max(0f, 1f + m.m0 - m.m5 - m.m10)) * 0.5f;
		float y = (float)Math.Sqrt(Math.Max(0f, 1f - m.m0 + m.m5 - m.m10)) * 0.5f;
		float z = (float)Math.Sqrt(Math.Max(0f, 1f - m.m0 - m.m5 + m.m10)) * 0.5f;
		return new pvrPosef
		{
			Orientation = new pvrQuatf
			{
				x = CopySign(x, m.m9 - m.m6),
				y = CopySign(y, m.m2 - m.m8),
				z = CopySign(z, m.m4 - m.m1),
				w = w
			},
			Position = new pvrVector3f
			{
				x = m.m3,
				y = m.m7,
				z = m.m11
			}
		};
	}

	private static float CopySign(float magnitude, float sign)
	{
		if (!(sign < 0f))
		{
			return magnitude;
		}
		return 0f - magnitude;
	}

	private static string GetHmdString(ETrackedDeviceProperty prop)
	{
		if (_system == null)
		{
			return "";
		}
		ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
		StringBuilder sb = new StringBuilder(256);
		_system.GetStringTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, prop, sb, (uint)sb.Capacity, ref error);
		return sb.ToString();
	}

	private static T GetConfig<T>(string key, T defaultValue)
	{
		lock (Config)
		{
			if (key != null && Config.TryGetValue(key, out var value) && value is T typed)
			{
				return typed;
			}
		}
		return defaultValue;
	}

	private static pvrResult SetConfig(string key, object value)
	{
		if (key == null)
		{
			return pvrResult.pvr_invalid_param;
		}
		lock (Config)
		{
			Config[key] = value;
		}
		Log.Info("[OpenVRPatch] Pimax setting '" + key + "' = " + value?.ToString() + " (kept locally, no Pimax runtime)");
		return pvrResult.pvr_success;
	}
}
