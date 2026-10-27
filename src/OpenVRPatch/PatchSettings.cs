using System;
using System.Globalization;
using System.IO;

namespace OpenVRPatch;

/// <summary>
/// Optional overrides read from OpenVRPatch.ini next to EyeTrackingGuide.exe.
/// Every key has a default, so the file can be absent.
/// </summary>
internal static class PatchSettings
{
	public const string FileName = "OpenVRPatch.ini";

	// Unity's Direct3D render textures are stored bottom-up, so the compositor has to read them flipped.
	public static bool FlipV = true;

	// Colour space reported to the compositor for the eye textures: gamma, linear or auto.
	public static string ColorSpace = "gamma";

	// Treat a trigger click as the A/X button so menus work with any controller binding.
	public static bool TriggerAsA = true;

	// Multiplier applied to SteamVR's recommended per-eye render size.
	public static float ResolutionScale = 1f;

	// Extra frames of pose prediction for Unity's main thread running ahead of its render thread.
	public static float PredictionFrames = 1f;

	private static bool _loaded;

	public static void Load()
	{
		if (_loaded)
		{
			return;
		}
		_loaded = true;
		// This assembly lives in <game>/EyeTrackingGuide_Data/Managed.
		string managed = Path.GetDirectoryName(typeof(PatchSettings).Assembly.Location);
		string path = Path.Combine(Path.GetFullPath(Path.Combine(managed, "..", "..")), FileName);
		if (!File.Exists(path))
		{
			Log.Info("[OpenVRPatch] No " + FileName + " found, using defaults.");
			return;
		}
		foreach (string rawLine in File.ReadAllLines(path))
		{
			string line = rawLine.Trim();
			if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[')
			{
				continue;
			}
			int eq = line.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			string key = line.Substring(0, eq).Trim().ToLowerInvariant();
			string value = line.Substring(eq + 1).Trim();
			try
			{
				switch (key)
				{
				case "flip_v":
					FlipV = ParseBool(value);
					break;
				case "color_space":
					ColorSpace = value.ToLowerInvariant();
					break;
				case "trigger_as_a":
					TriggerAsA = ParseBool(value);
					break;
				case "resolution_scale":
					ResolutionScale = float.Parse(value, CultureInfo.InvariantCulture);
					break;
				case "prediction_frames":
					PredictionFrames = float.Parse(value, CultureInfo.InvariantCulture);
					break;
				default:
					Log.Warning("[OpenVRPatch] Unknown setting '" + key + "' in " + FileName);
					break;
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[OpenVRPatch] Bad value for '" + key + "': " + ex.Message);
			}
		}
		Log.Info(string.Format(CultureInfo.InvariantCulture, "[OpenVRPatch] Settings: flip_v={0} color_space={1} trigger_as_a={2} resolution_scale={3} prediction_frames={4}", FlipV, ColorSpace, TriggerAsA, ResolutionScale, PredictionFrames));
	}

	private static bool ParseBool(string value)
	{
		switch (value.ToLowerInvariant())
		{
		case "1":
		case "true":
		case "yes":
		case "on":
			return true;
		case "0":
		case "false":
		case "no":
		case "off":
			return false;
		default:
			throw new FormatException("expected true/false, got '" + value + "'");
		}
	}
}
