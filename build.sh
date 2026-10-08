#!/bin/sh
# Builds the mod DLL against the game's own libraries with mcs. Usage: ./build.sh [out.dll]
# Override MANAGED / BEPCORE when the game is installed elsewhere.
cd "$(dirname "$0")"
M=${MANAGED:-/e/SteamLibrary/steamapps/common/Apocalypter/Apocalypter_Data/Managed}; B=${BEPCORE:-/e/SteamLibrary/steamapps/common/Apocalypter/BepInEx/core}
NAME=NPCAI
# (1.2.0) Apocaplayer 2.2.0+ for its ModAPI (optional at runtime); override APOCAPLAYER with its DLL
A=${APOCAPLAYER:-/e/SteamLibrary/steamapps/common/Apocalypter/BepInEx/plugins/Apocaplayer/Apocaplayer.dll}
mcs -nostdlib -noconfig -target:library -langversion:latest -optimize+ -nowarn:649 -out:${1:-$NAME.dll} \
  -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/netstandard.dll \
  -r:$B/BepInEx.dll -r:$B/0Harmony.dll \
  -r:$M/UnityEngine.dll -r:$M/UnityEngine.CoreModule.dll -r:$M/UnityEngine.PhysicsModule.dll -r:$M/UnityEngine.TerrainModule.dll -r:$M/UnityEngine.TerrainPhysicsModule.dll \
  -r:$M/UnityEngine.ImageConversionModule.dll -r:$M/UnityEngine.AudioModule.dll -r:$M/UnityEngine.UnityWebRequestModule.dll -r:$M/UnityEngine.UnityWebRequestAudioModule.dll -r:$M/UnityEngine.IMGUIModule.dll -r:$M/UnityEngine.TextRenderingModule.dll -r:$M/UnityEngine.ParticleSystemModule.dll -r:$M/UnityEngine.AssetBundleModule.dll -r:$M/UnityEngine.AnimationModule.dll -r:$M/UnityEngine.DirectorModule.dll -r:$M/Unity.InputSystem.dll -r:$M/PlayMaker.dll -r:$M/Micosmo.SensorToolkit.dll -r:$M/Assembly-CSharp.dll -r:$M/Assembly-CSharp-firstpass.dll -r:$A \
  *.cs
