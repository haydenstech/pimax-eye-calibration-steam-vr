using System;
using System.Reflection;

namespace OpenVRPatch;

/// <summary>
/// The few Unity calls the patch needs, made by reflection so this assembly builds without
/// Unity's DLLs. At runtime they resolve to the game's own UnityEngine.CoreModule.
/// </summary>
internal static class Engine
{
	private static readonly Type QualitySettings = Type.GetType("UnityEngine.QualitySettings, UnityEngine.CoreModule");

	public static void DisableVSync()
	{
		PropertyInfo vSyncCount = QualitySettings?.GetProperty("vSyncCount", BindingFlags.Static | BindingFlags.Public);
		vSyncCount?.SetValue(null, 0, null);
	}
}

/// <summary>Writes to Unity's Player.log, or the console outside Unity.</summary>
internal static class Log
{
	private static readonly Type Debug = Type.GetType("UnityEngine.Debug, UnityEngine.CoreModule");

	private static readonly MethodInfo LogInfo = Find("Log");

	private static readonly MethodInfo LogWarning = Find("LogWarning");

	private static readonly MethodInfo LogError = Find("LogError");

	public static void Info(string message)
	{
		Write(LogInfo, message);
	}

	public static void Warning(string message)
	{
		Write(LogWarning, message);
	}

	public static void Error(string message)
	{
		Write(LogError, message);
	}

	private static MethodInfo Find(string name)
	{
		return Debug?.GetMethod(name, BindingFlags.Static | BindingFlags.Public, null, new Type[1] { typeof(object) }, null);
	}

	private static void Write(MethodInfo method, string message)
	{
		if (!message.StartsWith("[OpenVRPatch]", StringComparison.Ordinal))
		{
			message = "[OpenVRPatch] " + message;
		}
		if (method != null)
		{
			method.Invoke(null, new object[1] { message });
		}
		else
		{
			Console.WriteLine(message);
		}
	}
}
