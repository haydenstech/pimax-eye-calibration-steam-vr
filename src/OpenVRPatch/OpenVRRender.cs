using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PVR;
using Valve.VR;

namespace OpenVRPatch;

/// <summary>
/// Managed replacement for pvrUnityRenderPlugin.dll. Assembly-CSharp's PVRRenderLib forwards its
/// five plugin entry points here.
///
/// Frame flow (matching the original plugin's contract):
///   main thread   PVRSurface.OnPreCull -> SubmitEyeLayer(eye, texture)   records the eye texture
///   main thread   PVRSession.EndOfFrame -> GetRenderEventFunc()          seals the frame
///   render thread GL.IssuePluginEvent(func, RENDER_EVENT)                submits to the SteamVR compositor
/// The render-thread callback is a managed delegate; Unity's Mono runtime attaches the render
/// thread when it calls into it.
/// </summary>
public static class OpenVRRender
{
	private const int RenderEvent = 0;

	private sealed class Frame
	{
		public readonly IntPtr[] Textures = new IntPtr[2];

		public readonly VRTextureBounds_t[] Bounds = new VRTextureBounds_t[2];

		public bool HeadLocked;

		public bool HasPose;

		public HmdMatrix34_t HeadPose;

		public bool HasTextures => Textures[0] != IntPtr.Zero || Textures[1] != IntPtr.Zero;
	}

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void RenderEventCallback(int eventId);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate EVRCompositorError SubmitWithPoseFn(EVREye eEye, ref VRTextureWithPose_t pTexture, ref VRTextureBounds_t pBounds, EVRSubmitFlags nSubmitFlags);

	private static SubmitWithPoseFn _submitWithPose;

	private static readonly RenderEventCallback Callback = OnRenderEvent;

	private static readonly IntPtr CallbackPtr = Marshal.GetFunctionPointerForDelegate(Callback);

	private static readonly object QueueLock = new object();

	private static readonly Queue<Frame> Sealed = new Queue<Frame>();

	private static Frame _pending = new Frame();

	private static readonly VRTextureBounds_t[] EyeBounds = new VRTextureBounds_t[2]
	{
		FullBounds(),
		FullBounds()
	};

	private static readonly TrackedDevicePose_t[] RenderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

	private static readonly TrackedDevicePose_t[] GamePoses = new TrackedDevicePose_t[0];

	private static volatile bool _active;

	private static bool _posesPrimed;

	private static readonly HashSet<EVRCompositorError> ReportedErrors = new HashSet<EVRCompositorError>();

	private static bool _reportedException;

	public static IntPtr GetRenderEventFunc()
	{
		lock (QueueLock)
		{
			if (_pending.HasTextures)
			{
				Sealed.Enqueue(_pending);
				// The render thread only ever needs the newest frame.
				while (Sealed.Count > 2)
				{
					Sealed.Dequeue();
				}
				_pending = new Frame();
			}
		}
		return CallbackPtr;
	}

	public static void LinkDebug(IntPtr debugCallback)
	{
	}

	public static pvrResult RegisterColorBuffer(IntPtr texturePtr)
	{
		_active = true;
		return pvrResult.pvr_success;
	}

	public static void UnregisterColorBuffer(IntPtr texturePtr)
	{
	}

	public static void SubmitEyeLayer(int layerUid, int eye, IntPtr texturePtr, ref pvrPosef pose, double sensorSampleTime, bool headLocked)
	{
		if (eye < 0 || eye > 1)
		{
			return;
		}
		lock (QueueLock)
		{
			_pending.Textures[eye] = texturePtr;
			_pending.Bounds[eye] = EyeBounds[eye];
			_pending.HeadLocked = headLocked;
			_pending.HasPose = PvrBackend.LastHeadPoseValid;
			_pending.HeadPose = PvrBackend.LastHeadPose;
			_active = true;
		}
	}

	/// <summary>
	/// PVRSurface renders a symmetric frustum (the larger tangent on each axis), so each eye's real
	/// projection is a sub-rectangle of the texture. These bounds select it.
	/// </summary>
	internal static void SetEyeFov(int eye, pvrFovPort fov)
	{
		if (eye < 0 || eye > 1)
		{
			return;
		}
		float maxH = Math.Max(fov.LeftTan, fov.RightTan);
		float maxV = Math.Max(fov.UpTan, fov.DownTan);
		if (!(maxH > 0f) || !(maxV > 0f))
		{
			return;
		}
		float top = (maxV - fov.UpTan) / (2f * maxV);
		float bottom = (maxV + fov.DownTan) / (2f * maxV);
		if (PatchSettings.FlipV)
		{
			top = 1f - top;
			bottom = 1f - bottom;
		}
		lock (QueueLock)
		{
			EyeBounds[eye] = new VRTextureBounds_t
			{
				uMin = (maxH - fov.LeftTan) / (2f * maxH),
				uMax = (maxH + fov.RightTan) / (2f * maxH),
				vMin = top,
				vMax = bottom
			};
		}
	}

	internal static void SetTrackingUniverse(ETrackingUniverseOrigin universe)
	{
		OpenVR.Compositor?.SetTrackingSpace(universe);
	}

	internal static void OnShutdown()
	{
		_active = false;
		_posesPrimed = false;
		_submitWithPose = null;
		lock (QueueLock)
		{
			Sealed.Clear();
			_pending = new Frame();
		}
	}

	private static void OnRenderEvent(int eventId)
	{
		if (eventId != RenderEvent || !_active)
		{
			return;
		}
		try
		{
			Frame frame = null;
			lock (QueueLock)
			{
				while (Sealed.Count > 0)
				{
					frame = Sealed.Dequeue();
				}
			}
			if (frame != null)
			{
				SubmitFrame(frame);
			}
		}
		catch (Exception ex)
		{
			// An exception must never unwind into Unity's native render thread.
			if (!_reportedException)
			{
				_reportedException = true;
				Log.Error("[OpenVRPatch] Render thread error: " + ex);
			}
		}
	}

	private static void SubmitFrame(Frame frame)
	{
		CVRCompositor compositor = OpenVR.Compositor;
		if (compositor == null)
		{
			return;
		}
		if (!_posesPrimed)
		{
			// Submit is rejected until WaitGetPoses has been called once.
			Report(compositor.WaitGetPoses(RenderPoses, GamePoses), "WaitGetPoses");
			_posesPrimed = true;
		}
		EColorSpace colorSpace = ColorSpace();
		for (int eye = 0; eye < 2; eye++)
		{
			if (frame.Textures[eye] == IntPtr.Zero)
			{
				continue;
			}
			EVREye vrEye = ((eye == 0) ? EVREye.Eye_Left : EVREye.Eye_Right);
			VRTextureBounds_t bounds = frame.Bounds[eye];
			if (frame.HeadLocked || !frame.HasPose)
			{
				// Content rendered with a fixed camera: let the compositor keep it locked to the head.
				Texture_t texture = new Texture_t
				{
					handle = frame.Textures[eye],
					eType = ETextureType.DirectX,
					eColorSpace = colorSpace
				};
				Report(compositor.Submit(vrEye, ref texture, ref bounds, EVRSubmitFlags.Submit_Default), "Submit");
			}
			else
			{
				SubmitWithPose(compositor, vrEye, frame.Textures[eye], colorSpace, frame.HeadPose, ref bounds);
			}
		}
		// Blocks until the compositor wants the next frame, which paces Unity to the headset.
		Report(compositor.WaitGetPoses(RenderPoses, GamePoses), "WaitGetPoses");
	}

	/// <summary>
	/// Submits with the head pose the frame was actually rendered from, so reprojection is correct even
	/// though Unity's main thread runs a frame ahead of the render thread.
	/// </summary>
	private static void SubmitWithPose(CVRCompositor compositor, EVREye eye, IntPtr handle, EColorSpace colorSpace, HmdMatrix34_t pose, ref VRTextureBounds_t bounds)
	{
		SubmitWithPoseFn submit = GetSubmitWithPose();
		if (submit == null)
		{
			Texture_t plain = new Texture_t
			{
				handle = handle,
				eType = ETextureType.DirectX,
				eColorSpace = colorSpace
			};
			Report(compositor.Submit(eye, ref plain, ref bounds, EVRSubmitFlags.Submit_Default), "Submit");
			return;
		}
		VRTextureWithPose_t texture = new VRTextureWithPose_t
		{
			handle = handle,
			eType = ETextureType.DirectX,
			eColorSpace = colorSpace,
			mDeviceToAbsoluteTracking = pose
		};
		Report(submit(eye, ref texture, ref bounds, EVRSubmitFlags.Submit_TextureWithPose), "Submit");
	}

	/// <summary>
	/// Valve's C# wrapper types Submit's texture as Texture_t, which is too small for the pose variant.
	/// Bind the same native IVRCompositor::Submit entry with the full VRTextureWithPose_t instead.
	/// </summary>
	private static SubmitWithPoseFn GetSubmitWithPose()
	{
		if (_submitWithPose == null)
		{
			EVRInitError error = EVRInitError.None;
			IntPtr table = OpenVR.GetGenericInterface("FnTable:" + OpenVR.IVRCompositor_Version, ref error);
			if (table == IntPtr.Zero || error != EVRInitError.None)
			{
				return null;
			}
			IntPtr fn = Marshal.ReadIntPtr(table, Marshal.OffsetOf(typeof(IVRCompositor), "Submit").ToInt32());
			_submitWithPose = (SubmitWithPoseFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(SubmitWithPoseFn));
		}
		return _submitWithPose;
	}

	private static EColorSpace ColorSpace()
	{
		return PatchSettings.ColorSpace switch
		{
			"linear" => EColorSpace.Linear,
			"auto" => EColorSpace.Auto,
			_ => EColorSpace.Gamma,
		};
	}

	private static void Report(EVRCompositorError error, string call)
	{
		if (error != EVRCompositorError.None && ReportedErrors.Add(error))
		{
			Log.Warning("[OpenVRPatch] " + call + " returned " + error);
		}
	}

	private static VRTextureBounds_t FullBounds()
	{
		VRTextureBounds_t result = default(VRTextureBounds_t);
		result.uMin = 0f;
		result.uMax = 1f;
		result.vMin = (PatchSettings.FlipV ? 1f : 0f);
		result.vMax = (PatchSettings.FlipV ? 0f : 1f);
		return result;
	}
}
