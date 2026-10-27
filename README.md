# EyeTrackingGuide-OpenVR

Run Pimax's Dream Air eye-tracking calibration app (`EyeTrackingGuide.exe`) on **SteamVR**,
for example with [CustomHeadsetOpenVR](https://github.com/sboys3/CustomHeadsetOpenVR), without Pimax Play.

This repository contains **no Pimax code**. The installer patches the copy of the app on your
own PC, and keeps backups of the originals so it can be undone.

## Install

Download the latest `EyeTrackingGuide-OpenVR-*.zip` from Releases, unzip it, run `Install.exe`,
and point it at the folder containing `EyeTrackingGuide.exe`. Then start SteamVR and run the app.
See [`docs/INSTALL.txt`](docs/INSTALL.txt) (shipped as `README.txt` in the zip) for details,
settings and troubleshooting.

## How it works

The app reaches the headset through Pimax's PVR runtime. Eye tracking goes straight to Tobii's
runtime and doesn't depend on Pimax Play:

| Concern | Stock app | Patched |
|---|---|---|
| Eye tracking + calibration | `tobii_stream_engine.dll` → Tobii runtime service | unchanged |
| Head pose, eye offsets, FOV, render size, controllers | `Managed-PVR.dll` → `libPVRClient64.dll` (Pimax Play) | `OpenVRPatch.dll` → `openvr_api.dll` |
| Frame submission | `pvrUnityRenderPlugin.dll` → Pimax compositor | `OpenVRPatch.dll` → SteamVR compositor (render-thread callback) |
| `eyetracking_calibrated` and other Pimax settings | written to the Pimax runtime | logged only |

`Install.exe` uses [Mono.Cecil](https://github.com/jbevain/cecil) to make three edits to the
user's own assemblies:

1. **Managed-PVR.dll**: `PVRInterface`'s constructor, which loaded the native PVR function table,
   now calls `OpenVRPatch.PvrBackend.Bind(this)`. That fills the same delegate fields with the
   OpenVR implementations.
2. **Assembly-CSharp.dll**: the five `[DllImport("pvrUnityRenderPlugin")]` methods of
   `PVRRenderLib` become managed methods forwarding to `OpenVRPatch.OpenVRRender`.
3. **Assembly-CSharp.dll**: the `Application.targetFrameRate` cap in `PVRSession.Start` becomes
   `-1`, so SteamVR alone paces frames.

It also renames the app's bundled `openvr_api_not_used.dll` to `openvr_api.dll`. Installing
always patches from the backed-up originals, so running it again is safe. No file is written
unless every patch point is found.

## Repository layout

```
src/OpenVRPatch/   Runtime assembly copied into the game (netstandard2.0, no Unity references)
  PvrBackend.cs      PVR 1.14 API implemented on OpenVR
  OpenVRRender.cs    Replacement for pvrUnityRenderPlugin.dll
  PatchSettings.cs   OpenVRPatch.ini reader
  Engine.cs          Unity logging/settings via reflection
  ThirdParty/        Valve's OpenVR C# binding (BSD-3-Clause)
src/PvrApi/        Build-time declarations of the PVR API types (never shipped)
src/Installer/     Install.exe: Mono.Cecil patcher (net48; net8.0 build for testing)
docs/INSTALL.txt   End-user instructions (README.txt in the release zip)
```

`src/PvrApi` declares only the names, field types and enum values that OpenVRPatch uses. It
compiles as an assembly named `Managed-PVR`, so at runtime OpenVRPatch binds to the copy the
game already has.

## Build

Needs the .NET SDK 8+. Game files are not needed to build.

```
./build.sh 1.0.0     # -> dist/EyeTrackingGuide-OpenVR-1.0.0.zip
```

Pushing a `v*` tag builds the zip on GitHub Actions and attaches it to a release.

## Compatibility

Developed against EyeTrackingGuide built with Unity 2020.3.32f1. The installer recognises that
build by hash and tries other builds if all patch points are present. The OpenVR implementation
has been used on a Pimax Dream Air with SteamVR and CustomHeadsetOpenVR.

## License

MIT, see [`LICENSE`](LICENSE). Third-party components are listed in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). Pimax and Dream Air are trademarks of their
owner; this project is not affiliated with or endorsed by Pimax or Tobii.
