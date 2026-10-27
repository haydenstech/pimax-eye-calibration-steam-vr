using System;
using System.IO;

namespace Installer;

internal static class Program
{
	private static int Main(string[] args)
	{
		bool uninstall = false;
		bool noPause = false;
		string gameDir = null;
		foreach (string arg in args)
		{
			switch (arg.ToLowerInvariant())
			{
			case "--uninstall":
			case "/uninstall":
				uninstall = true;
				break;
			case "--no-pause":
				noPause = true;
				break;
			default:
				gameDir = arg.Trim('"');
				break;
			}
		}

		Console.WriteLine("EyeTrackingGuide OpenVR patch " + (uninstall ? "uninstaller" : "installer"));
		Console.WriteLine();
		int exitCode;
		try
		{
			gameDir = gameDir ?? FindGameDir() ?? AskForGameDir();
			if (gameDir == null || !Patcher.LooksLikeGameDir(gameDir))
			{
				throw new PatchException("EyeTrackingGuide.exe not found in '" + gameDir + "'.");
			}
			Console.WriteLine("Game folder: " + gameDir);
			Patcher patcher = new Patcher(Path.GetFullPath(gameDir), AppDomain.CurrentDomain.BaseDirectory);
			if (uninstall)
			{
				patcher.Uninstall();
				Console.WriteLine();
				Console.WriteLine("Done. The original Pimax files are restored.");
			}
			else
			{
				patcher.Install();
				Console.WriteLine();
				Console.WriteLine("Done. Start SteamVR, then run EyeTrackingGuide.exe.");
				Console.WriteLine("Settings: OpenVRPatch.ini next to EyeTrackingGuide.exe. Run with --uninstall to undo.");
			}
			exitCode = 0;
		}
		catch (UnauthorizedAccessException ex)
		{
			Console.WriteLine("Access denied: " + ex.Message);
			Console.WriteLine("The game folder is write-protected. Run the installer as administrator.");
			exitCode = 2;
		}
		catch (PatchException ex)
		{
			Console.WriteLine("Nothing was changed: " + ex.Message);
			exitCode = 1;
		}
		catch (Exception ex)
		{
			Console.WriteLine("Failed: " + ex);
			exitCode = 1;
		}
		if (!noPause && !Console.IsInputRedirected)
		{
			Console.WriteLine();
			Console.WriteLine("Press Enter to close.");
			Console.ReadLine();
		}
		return exitCode;
	}

	/// <summary>Accepts the installer being unpacked into the game folder or one level below it.</summary>
	private static string FindGameDir()
	{
		string here = AppDomain.CurrentDomain.BaseDirectory;
		foreach (string candidate in new string[3]
		{
			Environment.CurrentDirectory,
			here,
			Path.Combine(here, "..")
		})
		{
			if (Patcher.LooksLikeGameDir(candidate))
			{
				return Path.GetFullPath(candidate);
			}
		}
		return null;
	}

	private static string AskForGameDir()
	{
		if (Console.IsInputRedirected)
		{
			return null;
		}
		Console.WriteLine("Paste the path of the folder containing EyeTrackingGuide.exe (or drag the folder here) and press Enter:");
		string input = Console.ReadLine();
		return string.IsNullOrWhiteSpace(input) ? null : input.Trim().Trim('"');
	}
}
