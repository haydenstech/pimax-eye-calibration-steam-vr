using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Installer;

/// <summary>
/// Patches the user's own copy of EyeTrackingGuide so its PVR calls go to OpenVRPatch.dll.
/// Pimax's assemblies are read from the user's install and never redistributed.
/// </summary>
internal sealed class Patcher
{
	private const string BackupSuffix = ".pimax";

	private const string PatchAssembly = "OpenVRPatch.dll";

	// Builds the patch was developed and tested against. Others are attempted if every patch point is found.
	private static readonly Dictionary<string, string> KnownHashes = new Dictionary<string, string>
	{
		["Assembly-CSharp.dll"] = "ade48c18c95660706df78b70b56e25777b948289491b8426edebd81c13b0e0e0",
		["Managed-PVR.dll"] = "94978edd4f9846d0f246bb70f0152c314879d01d1a9888eed8f187c3554892a7"
	};

	// PVRRenderLib's [DllImport("pvrUnityRenderPlugin")] methods and their replacements in OpenVRRender.
	private static readonly Dictionary<string, string> RenderPluginForwards = new Dictionary<string, string>
	{
		["GetRenderEventFunc"] = "GetRenderEventFunc",
		["LinkDebugFromUnity"] = "LinkDebug",
		["RegisterColorBufferFromUnity"] = "RegisterColorBuffer",
		["UnregisterColorBufferFromUnity"] = "UnregisterColorBuffer",
		["SubmitEyeLayerFromUnity"] = "SubmitEyeLayer"
	};

	private readonly string _gameDir;

	private readonly string _managed;

	private readonly string _plugins;

	private readonly string _payloadDir;

	public Patcher(string gameDir, string payloadDir)
	{
		_gameDir = gameDir;
		_managed = Path.Combine(gameDir, "EyeTrackingGuide_Data", "Managed");
		_plugins = Path.Combine(gameDir, "EyeTrackingGuide_Data", "Plugins", "x86_64");
		_payloadDir = payloadDir;
	}

	public static bool LooksLikeGameDir(string dir)
	{
		return File.Exists(Path.Combine(dir, "EyeTrackingGuide.exe")) && Directory.Exists(Path.Combine(dir, "EyeTrackingGuide_Data", "Managed"));
	}

	public void Install()
	{
		string csharp = Path.Combine(_managed, "Assembly-CSharp.dll");
		string pvr = Path.Combine(_managed, "Managed-PVR.dll");
		string patchDll = Path.Combine(_managed, PatchAssembly);

		// Always patch from the untouched originals so installing twice gives the same result.
		string csharpOriginal = EnsureBackup(csharp);
		string pvrOriginal = EnsureBackup(pvr);
		CheckKnownBuild(csharpOriginal, "Assembly-CSharp.dll");
		CheckKnownBuild(pvrOriginal, "Managed-PVR.dll");

		File.Copy(Path.Combine(_payloadDir, PatchAssembly), patchDll, overwrite: true);

		DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
		resolver.AddSearchDirectory(_managed);
		ReaderParameters read = new ReaderParameters
		{
			AssemblyResolver = resolver,
			InMemory = true
		};
		using (AssemblyDefinition patch = AssemblyDefinition.ReadAssembly(patchDll, read))
		using (AssemblyDefinition pvrAssembly = AssemblyDefinition.ReadAssembly(pvrOriginal, read))
		using (AssemblyDefinition csharpAssembly = AssemblyDefinition.ReadAssembly(csharpOriginal, read))
		{
			TypeDefinition backend = RequireType(patch.MainModule, "OpenVRPatch.PvrBackend");
			TypeDefinition render = RequireType(patch.MainModule, "OpenVRPatch.OpenVRRender");

			PatchPvrInterface(pvrAssembly.MainModule, backend);
			PatchRenderLib(csharpAssembly.MainModule, render);
			PatchFrameRateCap(csharpAssembly.MainModule);

			// Every patch point was found; only now touch the game's files.
			pvrAssembly.Write(pvr);
			csharpAssembly.Write(csharp);
		}

		string unusedOpenVr = Path.Combine(_plugins, "openvr_api_not_used.dll");
		string openVr = Path.Combine(_plugins, "openvr_api.dll");
		if (File.Exists(unusedOpenVr) && !File.Exists(openVr))
		{
			File.Move(unusedOpenVr, openVr);
			Console.WriteLine("Enabled Plugins/x86_64/openvr_api.dll");
		}

		string ini = Path.Combine(_gameDir, "OpenVRPatch.ini");
		string iniSource = Path.Combine(_payloadDir, "OpenVRPatch.ini");
		if (!File.Exists(ini) && File.Exists(iniSource))
		{
			File.Copy(iniSource, ini);
		}
	}

	public void Uninstall()
	{
		foreach (string name in new string[2] { "Assembly-CSharp.dll", "Managed-PVR.dll" })
		{
			string target = Path.Combine(_managed, name);
			string backup = target + BackupSuffix;
			if (File.Exists(backup))
			{
				File.Copy(backup, target, overwrite: true);
				File.Delete(backup);
				Console.WriteLine("Restored " + name);
			}
		}
		string patchDll = Path.Combine(_managed, PatchAssembly);
		if (File.Exists(patchDll))
		{
			File.Delete(patchDll);
		}
		string openVr = Path.Combine(_plugins, "openvr_api.dll");
		string unusedOpenVr = Path.Combine(_plugins, "openvr_api_not_used.dll");
		if (File.Exists(openVr) && !File.Exists(unusedOpenVr))
		{
			File.Move(openVr, unusedOpenVr);
		}
	}

	/// <summary>
	/// PVRInterface's constructor looks up libPVRClient64.dll's function table. Replace its body with
	/// a call that fills the same delegate fields from OpenVRPatch.PvrBackend.
	/// </summary>
	private static void PatchPvrInterface(ModuleDefinition module, TypeDefinition backend)
	{
		TypeDefinition type = RequireType(module, "PVR.PVRInterface");
		MethodDefinition ctor = type.Methods.SingleOrDefault((MethodDefinition m) => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0) ?? throw new PatchException("PVRInterface has no default constructor");
		MethodDefinition bind = backend.Methods.Single((MethodDefinition m) => m.Name == "Bind");
		// System.Object::.ctor from the game's own mscorlib, not the installer's runtime.
		MethodReference objectCtor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object)
		{
			HasThis = true
		};

		ctor.Body = new MethodBody(ctor);
		ILProcessor il = ctor.Body.GetILProcessor();
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Call, objectCtor);
		il.Emit(OpCodes.Ldarg_0);
		il.Emit(OpCodes.Call, module.ImportReference(bind));
		il.Emit(OpCodes.Ret);
		Console.WriteLine("Patched Managed-PVR: PVRInterface now binds to OpenVR");
	}

	/// <summary>Turns PVRRenderLib's native plugin imports into calls to OpenVRPatch.OpenVRRender.</summary>
	private static void PatchRenderLib(ModuleDefinition module, TypeDefinition render)
	{
		TypeDefinition type = RequireType(module, "PVR.Unity.PVRRenderLib");
		foreach (KeyValuePair<string, string> forward in RenderPluginForwards)
		{
			MethodDefinition method = type.Methods.SingleOrDefault((MethodDefinition m) => m.Name == forward.Key) ?? throw new PatchException("PVRRenderLib." + forward.Key + " not found");
			if (!method.IsPInvokeImpl || method.PInvokeInfo?.Module.Name != "pvrUnityRenderPlugin")
			{
				throw new PatchException("PVRRenderLib." + forward.Key + " is not the expected pvrUnityRenderPlugin import");
			}
			MethodDefinition target = render.Methods.SingleOrDefault((MethodDefinition m) => m.Name == forward.Value) ?? throw new PatchException("OpenVRRender." + forward.Value + " not found");
			if (target.Parameters.Count != method.Parameters.Count)
			{
				throw new PatchException("Signature mismatch for " + forward.Key);
			}

			// Order matters: Cecil's PInvokeInfo setter turns IsPInvokeImpl back on.
			method.PInvokeInfo = null;
			method.IsPInvokeImpl = false;
			method.IsPreserveSig = false;
			method.IsIL = true;
			method.IsManaged = true;
			// LinkDebugFromUnity's [MarshalAs(FunctionPtr)] parameter metadata is left in place:
			// it only affects native calls and is ignored now that the method is managed.

			method.Body = new MethodBody(method);
			ILProcessor il = method.Body.GetILProcessor();
			for (int i = 0; i < method.Parameters.Count; i++)
			{
				il.Emit(OpCodes.Ldarg, method.Parameters[i]);
			}
			il.Emit(OpCodes.Call, module.ImportReference(target));
			il.Emit(OpCodes.Ret);
			if (!method.HasBody)
			{
				throw new PatchException("PVRRenderLib." + forward.Key + " is still marked as a native import");
			}
		}
		// The plugin's module reference is now unused.
		ModuleReference plugin = module.ModuleReferences.FirstOrDefault((ModuleReference r) => r.Name == "pvrUnityRenderPlugin");
		if (plugin != null && !module.GetTypes().SelectMany((TypeDefinition t) => t.Methods).Any((MethodDefinition m) => m.PInvokeInfo?.Module == plugin))
		{
			module.ModuleReferences.Remove(plugin);
		}
		Console.WriteLine("Patched Assembly-CSharp: render plugin calls go to the SteamVR compositor");
	}

	/// <summary>
	/// PVRSession.Start caps Application.targetFrameRate at the headset refresh rate. SteamVR already
	/// paces frames, so replace the value with -1 (no cap).
	/// </summary>
	private static void PatchFrameRateCap(ModuleDefinition module)
	{
		TypeDefinition type = RequireType(module, "PVR.Unity.PVRSession");
		MethodDefinition start = type.Methods.SingleOrDefault((MethodDefinition m) => m.Name == "Start" && m.HasBody) ?? throw new PatchException("PVRSession.Start not found");
		Instruction setter = start.Body.Instructions.FirstOrDefault((Instruction i) => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.Name == "set_targetFrameRate" && m.DeclaringType.FullName == "UnityEngine.Application") ?? throw new PatchException("targetFrameRate assignment not found in PVRSession.Start");
		ILProcessor il = start.Body.GetILProcessor();
		il.InsertBefore(setter, il.Create(OpCodes.Pop));
		il.InsertBefore(setter, il.Create(OpCodes.Ldc_I4_M1));
		Console.WriteLine("Patched Assembly-CSharp: frame pacing left to SteamVR");
	}

	private static TypeDefinition RequireType(ModuleDefinition module, string fullName)
	{
		return module.GetType(fullName) ?? throw new PatchException(module.Name + " has no type " + fullName);
	}

	private static string EnsureBackup(string path)
	{
		string backup = path + BackupSuffix;
		if (!File.Exists(backup))
		{
			if (!File.Exists(path))
			{
				throw new PatchException(Path.GetFileName(path) + " not found in " + Path.GetDirectoryName(path));
			}
			File.Copy(path, backup);
			Console.WriteLine("Backed up " + Path.GetFileName(path) + " to " + Path.GetFileName(backup));
		}
		return backup;
	}

	private static void CheckKnownBuild(string path, string name)
	{
		string hash;
		using (SHA256 sha = SHA256.Create())
		using (FileStream stream = File.OpenRead(path))
		{
			hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
		}
		if (hash != KnownHashes[name])
		{
			Console.WriteLine("Note: " + name + " is a different build from the one this patch was tested with. Trying anyway.");
		}
	}
}

internal sealed class PatchException : Exception
{
	public PatchException(string message)
		: base(message)
	{
	}
}
