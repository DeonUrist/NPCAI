# NPCAI

Independent BepInEx 5 mod for Apocalypter, extracted from Apocaraider 1.7.0.
NPC vision, faction awareness, gunshots, hit origins, shouts, engines, horns, thrown items,
explosions and sandstorms feed remembered locations. NPCs investigate, search, pursue,
fight, steer around obstacles and follow camp/cave/building maps. Camp NPCs return home
and patrol. Busy friends can pass through each other. NPC aim pacing and firing decisions
respect weapon reach. Flying creatures and seated Apocapatrol crews keep game movement.

## Version 1.9.1

**Quadrupeds fight properly** (dogs with the 1.9.0 animation bundle: Wild / Yard Hound, Grimhound, Nightwalker, Alpha Nightwalker).
- They no longer stop short: 1.9.0 held a dog when its pivot was within reach + 0.5 m of yours, about half a metre before its bite could touch you, and it stood there until you stepped closer. Now a dog is held only where its bite really reaches (from its body to your body, facing you, nothing in between); anywhere else it keeps chasing.
- **Running bite**: closing in at speed, from about its reach + 0.22 s of running (2-2.5 m), it commits to a lunge along the line it had at that moment (it does not steer after you) and bites at the lunge's middle - only if you are still in reach and in front of it. Step aside and it misses, carries on a little and needs about a second to recover.
- **Close bites are faster**: standing in reach it bites about every 0.6 s (the game's own wait after a bite is shortened to 0.32 s; 0.48 s after a running bite). Damage per bite is the game's own. Dangerous up close, dodgeable on the approach.
- **Idle**: every non-boss quadruped with the bundle alternates standing and lying (lie down, rest, get up), each pose for a random 10-25 s (`[Behaviour] DogIdlePoseMinSeconds` / `DogIdlePoseMaxSeconds`); anything that alerts it makes it get up. Bosses stay standing.
- Without the bundle the procedural bite (1.8.0) holds a dog by the gap to your body (reach + 0.1 m), not to your pivot.

## Version 1.2.0

With **Apocaplayer 2.2.0** or newer installed, gunmen are animated by Apocaplayer's **ModAPI**: the
player's own third-person body, clip for clip and decision for decision, on the NPC. NPCAI only
tells it what the gunman does; the rest is the player's logic.

- **Moving**: idle, 8 directions x walk / run / sprint, crouch idle and 8 crouched walks for every
  gun, from the body's real velocity; the relaxed low-ready walk / run while chasing, the aiming
  set when squared up (hold, cover, backing up, the hit sidestep) or firing; smooth direction
  changes, turns in place with stepping feet, the walk-to-stop.
- **Crouching**: riflemen **and pistolmen** kneel again (the pistol's hands over the crouched legs,
  as on the player); the capsule is 0.45 m shorter while kneeling.
- **Aiming and firing**: the player's aim / fire clips, the aim lift and the spine bent toward the
  target's height.
- **Reloads**: the player's `RifleReload` / `PistolReload` on the hands over whatever the legs do.
  Guns the player loads **one round at a time** (revolver, Slambergs, Redmarks, Rochesters - read
  from the player's copy of the gun) load the rounds fired one by one (hidden `[NpcAim]
  RoundSeconds`, 0.6 s a round); the rest play the whole clip.
- **Pump / bolt guns** (Slamberg, Redmark): `ShotgunPump` racks the gun after every burst that
  didn't empty it, and after the reload.
- **Hops** over low lips play the player's jump (take-off, in the air, landing).
- The gun sits in the right hand at the player's per-weapon, per-clip weapon poses. Dead, seated
  (Apocapatrol) or swinging a machete, the body goes back to the game's own clips and the gun to
  its own hand.

Apocaplayer stays optional (a soft dependency; without it, or with an older one, the game's clips).
`tools/depcheck/depcheck.py` checks the built DLL: only `ApBody` touches Apocaplayer.
NPCAI 1.1.x's own animation code (`NpcAnim`, the melee legs rig) is gone.

## Version 1.1.2

Gunmen reload. The magazine of each gun is the player's own weapon's capacity (read from the
game's Reload FSMs: AKs / M16 30, akm_drum 70, borz 25, folk_17 17, revolver 5, Slambergs 5,
Redmarks 4, Rochesters 2, pipe pistol / slam-fire / crossbow 1; the 22_pipe_smg's 165 is cut
to 30 - hidden `[NpcAim] MagazineTable`). Every ray of a burst is a shot, a burst never fires
past the magazine (a single-shot gun fires one ray per burst), and the next burst is replaced
by the reload: Apocaplayer's `RifleReload` / `PistolReload` on an upper-body layer over the
legs - he keeps strafing, backing up or kneeling while reloading, like the player - for the
clip's length (`ReloadSeconds` without the clip; `SingleShotReload`, 1 s, for magazines of
1-2 with the clip sped up to it), with the sounds the player's copy of that gun plays while
reloading (`ReloadVolume`, `ReloadSoundRange`). A running chaser with an empty gun reloads when
he next squares up. `Magazines = false` turns it off. The bone-bent kneel is gone: crouching
exists only with Apocaplayer's crouch clips (clip + a 0.45 m shorter capsule). Shots are
counted whatever else is enabled (the counter used to sit behind an early return).

## Version 1.1.1

With **Apocaplayer** installed (its animation bundle and weapon-pose table), human NPCs are
animated the way the player's third-person body is. Gunmen play its Rifle / Pistol clip sets
with the gun in the right hand at Apocaplayer's curated per-weapon, per-animation pose
(`GunPose.Effective`: the player's live table incl. `config/Apocaplayer/weapon-poses.txt`):
the aim is `RifleFire` held on its first frame (the player's aim-down-sights), the burst the
Fire set, sidesteps the (Fire)Strafe clips, backing up `WalkBack`, running `Run`, kneeling the
Crouch set; the slot comes from the body's real velocity (speed and direction relative to
the facing, with hysteresis and the foot phase carried from one cycle to the next). Clips
play at the body's speed over their own and the body is capped at twice the clip's speed,
so the feet never slide. The held gun is tracked (the WeaponType FSM's swap at spawn and any
later toggle re-bind it) from the first frames, standing, walking, running and shooting; a
machete, death or a seat hands the body back to the game's clips. Melee humans keep their
own upper body and swing and get Apocaplayer's legs for walking, running and strafing
(their controller runs inside the graph, the game's AnimatorPlay calls are forwarded).
Any animated human sidesteps when hit and, chasing, keeps facing you while it skirts an
obstacle or comes in at an angle (`[Brain] StrafeAngle`, 50 deg) instead of turning like a
car. Without Apocaplayer nothing changes: the game's clips, no sidesteps.
Hidden `[Brain] ApocaplayerClips` (true) turns it off. Spawned NPCs are registered at once.

`tools/NPCAIProbe` is the debugging helper (source + DLL): drop its DLL into
`BepInEx/plugins/NPCAIProbe/` and it writes `BepInEx/NPCAIProbe/` - `report.txt` (every FSM
assumption checked live), `transitions.txt`, `anim.log` (what each gunman near you plays vs
what the game plays vs the hands), `perf.log` (frame time, every NPCAI subsystem's time,
spikes over 8 ms by method), `health.log`, `nav.txt`, `prefabs/`. Remove it for normal play.

## Version 1.8.0

**Dogs bite properly** (`[Behaviour] DogBite`, hidden `[Dogs]`). The game's dogs (Wild / Yard Hound, Grimhound, Nightwalker, Alpha Nightwalker) have only idle and run animations - no bite - and the game runs them at full speed the whole fight while turning them slowly, so they circled you, biting when their reach brushed you. Now a dog within its reach + 0.5 m of its target stops (brain halted, no stuck handling), turns to face it at 1080 degrees/s, and every real bite (the game's own Damage FSM hit, damage unchanged) is shown as a lunge: the head draws back and snaps forward-down over 0.4 s on the neck / head bones, with a short forward jolt of the body (hounds and nightwalkers; the Grimhound's Animator owns its body, it gets the head only). Farther than reach + 1.3 m: it runs as before.

## Version 1.7.5

Nest webs without a floor map (a wreck, or a cave whose map was not ready) are placed around the spiders' own spots: they already stand on the place's floor, often under its roof; a spot is the spider's own or up to 3 m from it with nothing solid in between. Every hanging web gets the nearest calm big spider of the nest, wherever it is (was: only within 1.5 x the nest's radius); the log says how many sit. A spider sitting in its web strikes only at what comes within 1 m (`[Nests] SitterReach`) - unless it is shot or the nest's webs are touched, then it comes at once; off the web it lies in wait at its usual 3-5 m again.

## Version 1.7.4

**Nests always get their webs, and keep them.** Every nest's layout (where its webs hang and lie, in the place's own coordinates) is remembered in `BepInEx/config/NPCAI/nest-layouts.txt`, keyed by map tile, place name and position in the tile: after a load, a restart or a return the same webs come back at once, without looking for spots again (a remembered layout that no longer fits the place is forgotten and rebuilt). A cave's floor map is waited for 6 s at the most; without it, spots are taken from the open space around the nest's own spiders (a line of sight from a spider, rock overhead), so they are inside the cave all the same. No spot for a hanging web: a second, less strict search; still too few webs: sheet webs on the floor under the spiders. The touch alarm reaches every web of the nest, however far from its middle.

## Version 1.7.3

**Scorpions and spiders lie in wait** (`[Behaviour] Ambush`, hidden `[Ambush]`). Small and big scorpions and spiders no longer come at you (or at another creature they hate) from the edge of sight: calm, they let a target come within 3-5 m (rolled each time they settle) and only then attack, and sounds do not draw them. Shot, they go for whoever hit them from any distance; a nest's touched web sends them at you; when the fight is over and they are calm again they lie in wait again. Burrowed scorpions keep their own distances (small 3-5 m, a big one hunting underground 15 m). A small spider that engages at 3-5 m leaps at once (its leap distance is 5-6 m).

**Webs follow the light.** Each web's brightness is the game's day/night light (much darker at night than before) times how much open sky is above it (inside a cave or under a roof they are dark), and the player's flashlight lights them up as by day where its beam falls (by the flashlight's own range and cone). Set once a second per web, and while the flashlight is on only for webs whose brightness changes; no per-frame cost otherwise.

**Nests**: distances are measured from the camera (also while flying in god mode); a place more than 675 m away is no longer planned and dropped over and over (1.7.1 logged that every 2 s). Nests prefer enclosed spots: hanging-web spots under a roof / inside a shelter score higher, and ground webs go to sheltered ground first (the open only when nothing sheltered is found).

## Version 1.7.0 (1.7.1: nests were missed - spiders more than 600 m away when they were registered, e.g. loaded with a save, were forgotten; a nest freed when you left was never rebuilt; the nest's centre was unset until building began. Places are now also found from the geometry around the spiders, and the log says when a nest is planned.)

**Spider nests** (`[Behaviour] SpiderNests`, hidden `[Nests]` settings). A cave, wreck or camp where spiders are (the game spawns them when you come within 500 m; after a load they come from the save) becomes a nest. Hanging webs are spun where the place has openings to span (1.7.2: in a cave or camp NPCAI has mapped, the spots come from its map - for a cave only the walkable floor under its roof, so webs are always inside the cave, never in the rock between its inner and outer walls or out on its slopes; the nest waits for the map): spots 0.9-2.6 m above a floor with surfaces around them in a vertical plane (between beams, walls, the wreck, rocks); 3-7 of them, at least 3.5 m apart, plus 4-7 sheet webs on the ground. The big spiders sit in the hanging webs, one per web, head down with their belly to the silk; the others stay on the ground. A spider that notices something drops from its web and fights. **Touch any web of the nest** (walk through a hanging web or onto a ground web) and every spider of the nest knows where you are and comes at you 1.5 times faster for 20 s (`AlarmSpeed`, `AlarmSeconds`), at most every 5 s. The nest is built while you are still far away, in slices of at most 1 ms a frame (`FrameBudgetMs`); the log says how long it took in total, over how many frames and the longest single frame. The layout is the same every visit (seeded by the place). Webs are never saved: they are rebuilt when you come back (they are removed beyond 750 m) and after a load; a spider saved while sitting in a web is never left hanging after the load (it drops and is seated again). Spiders far from any place (wild ones) make no nest. The F9 test webs of 1.6.0 still work.

## Version 1.6.0 (test)

**Spider webs, drawn in code (test only).** The game has no web asset, so webs are built from real web geometry: a hanging orb web tied to the rocks, wrecks, walls or ground around it (spokes, frame, a sagging capture spiral with gaps and uneven spacing, a tangled hub), or a sheet web lying on the ground following its bumps (an irregular net, dense in the middle, fraying at the edge). Thin soft-edged strands, pale and semi-transparent, darkened with the game's day/night light; they thin out with distance and vanish edge-on like real silk. Each web is one static mesh (one object, one draw call, built once in a millisecond or two, nothing per frame, no collider, no shadow). Press **F9** (hidden `[Webs] TestKey`) to put one where you look; Shift+F9 removes them all; at most 24 (`TestMax`), each gone after 15 min (`TestLifetime`). The log says what was built, its vertex count and the time it took. Test webs are never saved: they are not registered with the game, so a save made with webs out does not contain them and loading a save starts without them (they are cleared on every scene load). Nothing in the game uses webs yet (no slowing, no spider behaviour).

## Version 1.5.0 (1.5.1: zigzag from 8-11 m, leap from 5-6 m, bite feedback; 1.5.2: the leap flies until it lands, faster)

**Small spiders pounce** (`[Behaviour] Pounce`, hidden `[Pounce]` settings). A small spider chasing you on foot, with open ground between you (no wall, rock, car or drop on the way, room for a zigzag to either side, less than 2 m height difference), starts running in zigzags when it gets within 8-11 m and leaps at you from 5-6 m (both chosen at random each try; 1.5.0 was 3-5 m and 2 m): a jump aimed at your body where you will be, the spider reared back in the air with its belly towards you. If it reaches you it bites (its own bite damage, read from the game's Damage FSM: -7, with the game's hurt sound, red flash and head jolt - 1.5.1), drops off and runs away for 1.5-2.5 s, then chases as usual and tries again 4-7 s later. A leap that misses flies on at full speed until it comes down (1.5.2: nothing cuts it short any more, and it is faster, 10 m/s), then the chase goes on. Shot dead in the air, its carcass flies on in the reared pose and tumbles to the ground. During the pounce the game's own bite is switched off (no double bite). It never leaps at you in a car, and other NPCs are not pounced on.

## Version 1.4.0 (1.4.1: faster travel, slower burrowing, bigger and louder sand burst)

**Big scorpions hunt underground** (`[Behaviour] Burrow`, hidden `[Hunt]` settings). They burrow and lie in wait like the small ones, but engage anything within 15 m (`[Hunt] Range`). Then, and also whenever the enemy they are fighting gets farther than 15 m (up to `[Hunt] MaxDistance` 60 m), the scorpion dives (tail included, nothing shows), travels underground twice as fast as it runs (`[Hunt] TravelSpeedFactor` 2; its run speed comes from the game's Movement FSM, `[Hunt] Speed` 5 m/s until that is known), and bursts out 2.5 m behind the enemy (`[Hunt] Behind`; behind the camera for the player) facing it, then attacks. The spot must be open ground (the terrain, not an object, not steeper than about 40 degrees) with room for the body; if it is blocked, one other spot (back and to a side) is tried; if that fails too it comes back up where it went down 3-5 s later (`[Hunt] FallbackMin/Max`) and chases as usual. If the enemy moves meanwhile it comes up behind where the enemy is now, travelling the extra way at the same speed (twice at the most). After coming up it fights above ground for at least 6 s (`[Hunt] Cooldown`) before it can dive again. While travelling the body is kinematic with its colliders off, its senses and brain pause, and no other NPC can see it. Diving takes 1.6 s and bursting out 1 s (`[Hunt] DiveSeconds/RiseSeconds`); its ordinary burrowing and coming up while lying in wait take twice as long as a small scorpion's (`[Hunt] BurrowSlowdown` 2). The sand puff and sound are the worm's at 90 % size and full volume, heard up to 45 m (`[Hunt] FxScale/FxVolume/FxPitch`).

Performance: the burrowed-target lookup in NPC sight (1.3.3) now runs only while some NPC is burrowed or travelling; the search for the worm's sand effect, while it is not loaded, runs at most once a minute (was every 10 s). The hunt itself adds a ground ray twice a second only for a big scorpion fighting an enemy beyond 15 m, and 1-4 ground and room checks per dive.

## Version 1.3.0

**Vision arcs by kind of creature.** Humans and zombies use `[Detection] SightCone` (Apocasetter). Spiders, scorpions, wasps and arachnids see 210 degrees, rats, hounds, skinwals and nightwalkers 190, professors, teachers and juggernauts 210, bosses (anything with the game's BossUI) 300, sand worms 360. The values and the name lists are hidden settings (`SightConeInsects` ... `ConeBurrowers`).

**Small scorpions burrow** (`[Behaviour] Burrow`). A scorpion that is not aware of anything sinks into the sand with only the top of its body (the eyes) showing, and comes up or goes down again every 10-30 s, only on sand (the open ground itself, never on rocks, floors, roads or other objects). Spawned on sand it burrows at once. Burrowed it sees 360 degrees. It lies in wait: it watches you come and only comes up and attacks when you are within 3-5 m (chosen at random each time it burrows; sounds are ignored while it waits). Other NPCs notice a burrowed creature only from 5 m (`[Burrow] SeenFrom`). A hit brings it up at once, and being alerted brings it up and it does not burrow again until it is calm. The puff and the sound are the sand worm's own, small and quiet (`[Burrow]` hidden settings). 1.2.1: eye height / arrival distance / no step-back after a swing, so small creatures no longer lose the player 1 m in front of them.

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


## Staged canine animations (1.9.0)

With `NPCAI/Models/npcai_dogs.bundle` installed, Wild/Yard Hounds, Grimhound, Nightwalker and Alpha Nightwalker use target-specific retargeted Labrador clips for idle, walking, running, lying down, lying idle, getting up and biting. Threats wake resting creatures through get-up before they resume chasing. The Damage FSM's attack event is held until the bite contact frame; a target that leaves reach receives no hit. Native damage amounts and the native cooldown after contact stay under the game's FSM. The animation introduces an anticipation interval before that cooldown starts.

`Behaviour/DogAnimations` and `DogsLieDown` default on. `DogRestDelay` defaults to 20 quiet seconds plus an individual 0-10 second stagger. The existing `DogBite` switch gates this feature too. `DogJumpAttack` defaults off; when enabled, some bites can use a short collision-checked jump. Jump physics and normal elapsed-time gameplay still need live validation. Missing/incomplete bundles retain the existing procedural bite. Distant creatures release the custom animation at NPCAI's existing distance limit. Disabling or changing scenes restores native poses, solver enabled states, culling and movement ownership.

The original materials, meshes and bone weights are preserved. Hound and Nightwalker mouths have no independent jaw bone in the shipped rig, so their bite uses full-body/neck/head motion. Grimhound's separate `Head.001` bone is mapped to the donor mouth; it needs visual gameplay review. No new terrain IK is added.

The bundle was built with Unity 2020.3.49f1 from the user's purchased Radik Bilalov Labrador pack. Raw purchased source files are kept outside the repository and deployment. Controlled native Unity/PlayMaker checks passed for five rig bindings, rest/get-up sequencing, deferred contact, duplicate prevention, missed bites, disable/re-enable, distance release and scene cleanup. These checks explicitly advance clip clocks and do not replace gameplay testing.
