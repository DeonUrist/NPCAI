# NPCAI

Independent BepInEx 5 mod for Apocalypter, extracted from Apocaraider 1.7.0.
NPC vision, faction awareness, gunshots, hit origins, shouts, engines, horns, thrown items,
explosions and sandstorms feed remembered locations. NPCs investigate, search, pursue,
fight, steer around obstacles and follow camp/cave/building maps. Camp NPCs return home
and patrol. Busy friends can pass through each other. NPC aim pacing and firing decisions
respect weapon reach. Flying creatures and seated Apocapatrol crews keep game movement.

## Version 1.1.0

NPCs now lose you when you break their line of sight (1.5 s, was 10 s of "memory" during
which gunmen fired through the wall you had just stepped behind) and never shoot without a
clear line. A human that loses you goes to where it last saw you and then *guesses* where
you went (8 m off, then 16, then 24, along the way you were going); hounds, pups, Grimhounds,
Nightwalkers, spiders, arachnids and scorpions track your real position (`[Senses] Trackers`).
Hits, gunshots and shouts give rough spots, not your exact position. Noticing takes longer
at a distance and in the dark (`NoticeFar`). Night is read from the game's clock (Azure sun
elevation; `NightBelow` / `DayAbove`) - it had never been dark for NPC eyes. Shouts only
while an NPC sees you: at once, then every 12-25 s (`TauntMin`/`TauntMax`; the game shouted
every 0.1-4 s at anything, ghosts included), and a shout sends friends to look near you
rather than handing them your live position. A gunshot or an explosion is checked by the
nearest 4 NPCs plus everyone within 25 m (`GunshotResponders`, `GunshotNearRange`), not a
whole camp. Cover: the game's own "hide" (a fast target) is now walked with the map instead
of blindly, and a human below 50 % health (`[Brain] CoverBelow`, `CoverRange`, `CoverMin`/
`CoverMax`) runs to a cover object or a spot its map shows breaks your line of sight and
fights from there. A hit gunman steps aside and kneels more often; gunmen advance more when
you have not fired for 8 s and never when hurt. Nightwalkers (chase state "trigger 2") are
steered like everyone else. Map baking finishes what it starts (flying or driving past a
hundred camps used to leave none of them mapped), bakes faster where the player is, and a
camp an NPC needs is baked first. An NPC in cover is in its own HIDING state: it keeps its
target and takes no ghosts until it comes out. A human sent to check a spot comes in from
one side (`FlankDistance`/`FlankWidth`), the side alternating between NPCs. Same-faction
NPCs walk through each other only in corridors (next to a wall on the map). Save/load bug
fixed: NPCs alert at the save were given a dead ghost after the load and stood still for a
minute and a half. Several per-frame allocations and leaks removed.

Simplified: night comes from a table of hours (`NightHours`), the bake scheduler is
"finish what you started, nearest first"; the old plain-ray shooter pathing, the pre-1.6
ghost walk, sound muffling, the sensor-interval patch and the Apocaraider config import are
gone; `PursuitMin/Max` became `Guesses` (3); `[NpcAim]` keeps `AimTimeScale`, `FacingTolerance`
and `EngagePercent` (the distance delay, spread, patience and recheck pauses are fixed values);
ghosts rank in two classes, seen beats heard, newest wins.

## Version 1.0.1

NPCAI's six fallback weapon ranges are now fixed internal values rather than editable
settings. Gunplay's range settings remain authoritative when that mod is installed.
Old NPCAI range entries are retired automatically; all other AI settings and behavior
are preserved.

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
for AI settings, including `[Gunplay] NpcAim`. Advanced settings keep the unsaved hidden
config semantics: they are not written to disk; to change one, put the key in
`BepInEx/config/NPCAI/hidden-settings.not-saved` (the same `[Section]` / `Key = value` format).

Gunplay, GunplayHUD and WomenOfWasteland are optional. NPCAI declares a soft dependency
on Gunplay for load ordering. NPCAI has no sibling assembly reference. If Gunplay is installed,
NPCAI dynamically resolves its version-one public range/classification API. Gunplay's
configured ranges are authoritative even when projectile simulation is disabled.
Without Gunplay, NPC weapon discovery and classification reproduce the original rules
and use fixed internal fallback ranges: pistol 60, SMG 70, rifle 120, sniper
250, shotgun 35 and crossbow 90 metres, each clamped to at least one metre. Gunplay and
NPCAI use the same kind ordinals and classification order; focused contract tests check
consistency. These fallback values are not configurable in NPCAI. Installing Gunplay
makes its editable range settings authoritative. The six old NPCAI `[Tracers]` range
entries are removed from its config on startup; all other settings are preserved.

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
