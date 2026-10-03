# NPCAI

Independent BepInEx 5 mod for Apocalypter, extracted from Apocaraider 1.7.0.
NPC vision, faction awareness, gunshots, hit origins, shouts, engines, horns, thrown items,
explosions and sandstorms feed remembered locations. NPCs investigate, search, pursue,
fight, steer around obstacles and follow camp/cave/building maps. Camp NPCs return home
and patrol. Busy friends can pass through each other. NPC aim pacing and firing decisions
respect weapon reach. Flying creatures and seated Apocapatrol crews keep game movement.

## Build and install

Requires the .NET SDK, .NET Framework 4.7.2 targeting support, the installed game and BepInEx 5.

```powershell
dotnet build NPCAI.csproj -c Release -p:GameDir="<installed-game-directory>"
```

Alternatively set `APOCALYPTER_GAME_DIR`. The output is `bin/Release/NPCAI.dll`.
Copy that DLL into `BepInEx/plugins/NPCAI`. Builds never deploy into the game.
Replace the combined Apocaraider plugin when using the extracted mods; installing both
would duplicate the original feature hooks.

## Settings and integrations

The normal BepInEx config is `com.denis.apocalypter.npcai.cfg`. ApocaSetter discovers
this plugin's config through `[General] Apocasetter=true`; no ApocaSetter modifications
or compile dependency are needed. Existing section/key names and defaults are retained
for AI settings, including `[Gunplay] NpcAim`. The old Apocaraider config is read once
when the new config does not already exist; later NPCAI settings always win.
Advanced settings retain Apocaraider's unsaved hidden config semantics.

Gunplay, GunplayHUD and WomenOfWasteland are optional. NPCAI declares a soft dependency
on Gunplay for load ordering. NPCAI has no sibling assembly reference. If Gunplay is installed,
NPCAI dynamically resolves its version-one public range/classification API. Gunplay's
configured ranges are authoritative even when projectile simulation is disabled.
Without Gunplay, NPC weapon discovery and classification reproduce the original rules
and use local `[Tracers]` fallback range settings: pistol 60, SMG 70, rifle 120, sniper
250, shotgun 35 and crossbow 90 metres, each clamped to at least one metre. Gunplay and
NPCAI use the same kind ordinals and classification order; focused contract tests check
consistency. Installing Gunplay intentionally makes its range settings win over fallback
settings in NPCAI.

Public `NPCAI.Api` contract version 1 exposes `Shot(GameObject,Vector3,int,bool)`,
`Hurt(GameObject,GameObject)`, `Hurt(GameObject,bool)`, `SpreadFactor(float)` and
`Inform(GameObject,GameObject)`. Gunplay invokes shot/hurt callbacks at projectile events.
NPCAI observes vanilla shooting/damage when Gunplay projectiles are disabled or absent;
melee damage remains observed regardless. This avoids duplicate simulated shot/hit events.
Ballistic target leading remains Gunplay's responsibility.
Without active Gunplay projectiles, NPC shots temporarily repulse their vanilla ray
sensor at effective reach when aim or movement AI is enabled, independently of the
detection toggle. A Harmony finalizer restores the game's sensor length even when the
original action throws. This allows
rifle/sniper NPCs to hit from the positions chosen by AI rather than stopping at the
game's original 80 metre hit limit. Shorter-range guns use their shorter effective reach.

Apocapatrol bailout and blast methods are patched dynamically when that plugin exists.
AI debug drawing lives here and does not require GunplayHUD. Persistent AI sidecars keep
the original `v1` data format and enum values, write under `BepInEx/config/NPCAI/Saves`,
and read legacy `Apocaraider/Saves` files when no new sidecar exists. Navigation dumps
write under `BepInEx/config/NPCAI/NavDump`.

## Verification and architecture

The frame order remains Senses → Nav → Brain → Idle → Passthrough, followed by Brain's
late update. Brain/Idle keep their exclusive body-driving handshake. Separate persistent
runners preserve the original scene lifetime independently of the plugin GameObject.

Build verification succeeds with the original unused `Brain.Npc.LastLog` warning CS0649.
Build/static checks are documented in `ExecPlan.md`. Runtime behavior must be checked
in the actual game, especially save restoration, firing rhythm, all installation
combinations, ghost attribution and map movement. No in-game verification is claimed.
