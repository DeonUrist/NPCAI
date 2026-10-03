using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace NPCAI
{
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.denis.apocalypter.gunplay", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.npcai", NAME = "NPCAI", VERSION = "1.0.0";
        internal static ManualLogSource Log;
        internal static string Dir;
        internal static ConfigEntry<bool> VerboseLog;
        internal static ConfigEntry<bool> AimEnabled;
        internal static ConfigEntry<float> AimTimeScale, FacingTolerance, AimBaseDistance, AimDelayPer5m, SpreadPer5m, EngagePercent, EngagePatience, HoldRecheckMin, HoldRecheckMax;
        internal static ConfigEntry<float> PistolRange, SmgRange, RifleRange, SniperRange, ShotgunRange, CrossbowRange;
        internal static ConfigEntry<bool> BrainEnabled, DropCheck, BrainLog, AimPose, ShooterPathing, ScaleWithActors;
        internal static ConfigEntry<float> TurnRate, CrouchChance, SensorInterval, ReactionTime, FeelerLength, MeleeFeelerLength, FeelerAngle, AdvanceChance, AdvanceMin, AdvanceMax, StuckBackupSeconds, StuckMemorySeconds, MaxDistance;
        internal static ConfigEntry<int> FeelerCount, StuckGiveUpCount, PursuitMin, PursuitMax;
        internal static ConfigEntry<bool> SensesEnabled, MuffleSounds, SensesLog, ShowGhosts, BailOutAware;
        internal static ConfigEntry<float> SightCone, SightRange, DarkSightRange, DaylightIntensity, NoticeSeconds, LoseSeconds, SearchSeconds, GhostTimeout, LookInterval, ArriveDistance, MuffleFactor, AllClearRange, StormSight, StormHearing, StormRadius, ShotRangePistol, ShotRangeSmg, ShotRangeRifle, ShotRangeSniper, ShotRangeShotgun, ShotRangeCrossbow, TauntRange, EngineMinRange, EngineMaxRange, EngineMinHp, EngineMaxHp, EngineIdleFactor, ThrowRange, BailOutAwareRange, ExplosionRange, BlastRange, PlayerShoutRange, ShoutCooldown, ShoutVolume;
        internal static ConfigEntry<Key> ShoutKey, ShoutModifier;
        internal static ConfigEntry<string> ShoutBlocksButtons;
        internal static ConfigEntry<string> NpcShotRanges, HumanFactions, BlastPrefabs;
        internal static ConfigEntry<bool> NavEnabled, NavLog, ShowNav, NavDump, IdleEnabled, IdleCoyotes, FriendsPassThrough;
        internal static ConfigEntry<float> MoveFullSpeedAngle, MoveSlowestAngle, MoveSlowestSpeed, BlockedRatio, BlockedSeconds, BlockedMemory;
        internal static ConfigEntry<float> IdleReturnDelay, IdleRetrySeconds, IdleWalkRadius;
        internal static ConfigEntry<int> IdleReturnTries;
        internal static ConfigEntry<bool> IdleGhostWalk;
        internal static ConfigEntry<float> NavBakeRange, NavCellSize, NavMargin, NavMaxStep, NavBakeBudgetMs, NavFieldSeconds;
        private static ConfigFile _hidden;
        private static ConfigEntry<T> H<T>(string section, string key, T value, ConfigDescription description) { return _hidden.Bind(section, key, value, description); }
        private static ConfigEntry<T> H<T>(string section, string key, T value, string description) { return _hidden.Bind(section, key, value, description); }
        private static GameObject _runner;
        private void Awake()
        {
            bool existing = File.Exists(Config.ConfigFilePath);
            Log = Logger; Dir = Path.GetDirectoryName(Info.Location);
            _hidden = new ConfigFile(Path.Combine(Paths.ConfigPath, "NPCAI/hidden-settings.not-saved"), false) { SaveOnConfigSet = false };
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            SensesEnabled = Config.Bind("General", "EnableNpcDetection", true,
                "NPCs see (field of view, light, flashlight), hear gunshots, shouts, engines and explosions, remember where you were and search. Off = the game's own sensors.");
            BrainEnabled = Config.Bind("General", "EnableNpcPathfinding", true,
                "NPCs steer around obstacles, use the maps of camps, caves and buildings, take shooting positions, kneel and aim. Off = the game's own movement.");
            AimEnabled = Config.Bind("Gunplay", "NpcAim", true, "NPC gunmen pace their bursts: they hold fire beyond their gun's reach, take longer to aim at a distance and lead moving targets.");
            ShoutKey = Config.Bind("Detection", "PlayerShoutKey", Key.Q,
                "Your shout (with PlayerShoutKeyModifier held): you yell like a raider and every NPC hostile to you within 25 m comes to check. None = off.");
            ShoutModifier = Config.Bind("Detection", "PlayerShoutKeyModifier", Key.LeftAlt,
                "Hold this and press PlayerShoutKey to shout (either Alt works when this is an Alt key); the game's own Q actions are ignored while it is held. None = the key alone.");
            SightCone = Config.Bind("Detection", "SightCone", 100f, new ConfigDescription("Width of an NPC's field of view, degrees.", new AcceptableValueRange<float>(10f, 360f)));
            SightRange = Config.Bind("Detection", "SightRange", 100f, new ConfigDescription("How far an NPC sees in daylight, m (and at any light if your flashlight is on).", new AcceptableValueRange<float>(5f, 300f)));
            DarkSightRange = Config.Bind("Detection", "DarkSightRange", 5f, new ConfigDescription("How far an NPC sees in full darkness, m.", new AcceptableValueRange<float>(0f, 100f)));
            MuffleSounds = H("Detection", "MuffleSounds", false, "Walls muffle sounds: an NPC with no line to a sound hears it only within half its range.");
            ScaleWithActors = Config.Bind("Pathfinding", "ScaleWithActors", false, "With many NPCs around, each one thinks less often (saves CPU in big fights).");
            FriendsPassThrough = Config.Bind("Pathfinding", "FriendsPassThrough", true, "NPCs of the same faction walk through each other while they fight, search or walk home - no bumping, no blocking a passage. Solid again once they are idle.");
            IdleEnabled = Config.Bind("Pathfinding", "EnableIdleBehavior", true, "Camp raiders who lose you go back to their spawn spot, and walk a short round in their camp while nothing happens.");
            IdleCoyotes = Config.Bind("Pathfinding", "CoyotesIdle", false, "The idle behaviour also for the peaceful Coyote towns.");
            VerboseLog = Config.Bind("Debug", "VerboseLog", false, "Detailed logs for every part of the mod (hits, detection, movement, maps).");
            NavDump = Config.Bind("Debug", "NavDump", false, "Map pictures (BMP) of every camp map made, and of NPCs that find no route, in config/Apocaraider/NavDump (0.5-4 MB each; for troubleshooting). With VerboseLog also a per-second Trace line for every moving NPC and a trace picture of its trail when a chase ends or it rests (cyan = the map's route, white = walked on the map, orange = walked without the map, red = backing up / resting).");
            ShowNav = Config.Bind("Debug", "ShowNavigation", false, "Draw the detection ghosts, NPC states and the structure maps' waypoints in the world.");
            PistolRange = Config.Bind("Tracers", "PistolRange", 60f, new ConfigDescription("Pistols/revolvers: damage falls off linearly with distance - half at 50 % of this range, the bullet is gone at 100 %. Metres.", new AcceptableValueRange<float>(5f, 1000f)));
            SmgRange = Config.Bind("Tracers", "SmgRange", 70f, new ConfigDescription("SMGs, falloff range in metres (half damage at half range).", new AcceptableValueRange<float>(5f, 1000f)));
            RifleRange = Config.Bind("Tracers", "RifleRange", 120f, new ConfigDescription("Automatic rifles and machine guns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            SniperRange = Config.Bind("Tracers", "SniperRange", 250f, new ConfigDescription("Sniper/scoped rifles, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            ShotgunRange = Config.Bind("Tracers", "ShotgunRange", 35f, new ConfigDescription("Shotguns, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            CrossbowRange = Config.Bind("Tracers", "CrossbowRange", 90f, new ConfigDescription("Crossbows, falloff range in metres.", new AcceptableValueRange<float>(5f, 1000f)));
            AimTimeScale = H("NpcAim", "AimTimeScale", 50f, new ConfigDescription(
                "How long NPCs take to aim between bursts, as % of the game's own pause (3-5 s, plus the distance delay): 50 = half the time, 100 = as the game, 300 = three times slower.",
                new AcceptableValueRange<float>(1f, 300f)));
            FacingTolerance = H("NpcAim", "FacingTolerance", 8f, new ConfigDescription(
                "An NPC fires only once its body faces you within this many degrees (it turns at [Brain] TurnRate); until then it keeps turning and checks again every 0.1-0.2 s.",
                new AcceptableValueRange<float>(0f, 90f)));
            AimBaseDistance = H("NpcAim", "AimBaseDistance", 5f, new ConfigDescription(
                "Up to this distance, m, NPCs aim and spread as the game does; every 5 m beyond it adds AimDelayPer5m and SpreadPer5m.", new AcceptableValueRange<float>(0f, 200f)));
            AimDelayPer5m = H("NpcAim", "AimDelayPer5m", 0.25f, new ConfigDescription(
                "Seconds added to the pause between an NPC's bursts for every 5 m the target is beyond AimBaseDistance.", new AcceptableValueRange<float>(0f, 5f)));
            SpreadPer5m = H("NpcAim", "SpreadPer5m", 10f, new ConfigDescription(
                "% added to the NPC's aim spread for every 5 m the target is beyond AimBaseDistance.", new AcceptableValueRange<float>(0f, 100f)));
            EngagePercent = H("NpcAim", "EngagePercent", 85f, new ConfigDescription(
                "NPCs open fire once the target is within this % of the gun's reach; farther away they keep closing in. Never beyond the reach itself.",
                new AcceptableValueRange<float>(1f, 100f)));
            EngagePatience = H("NpcAim", "EngagePatience", 5f, new ConfigDescription(
                "Seconds an NPC within reach but beyond EngagePercent keeps closing in before it fires anyway (stuck, hiding...).", new AcceptableValueRange<float>(0f, 60f)));
            HoldRecheckMin = H("NpcAim", "HoldRecheckMin", 1f, new ConfigDescription(
                "While holding fire (target too far), the NPC looks again after a random pause between HoldRecheckMin and HoldRecheckMax seconds: its reaction time once you come into reach.",
                new AcceptableValueRange<float>(0.1f, 30f)));
            HoldRecheckMax = H("NpcAim", "HoldRecheckMax", 4f, new ConfigDescription("See HoldRecheckMin.", new AcceptableValueRange<float>(0.1f, 30f)));
            ReactionTime = H("Brain", "ReactionTime", 100f, new ConfigDescription(
                "How quickly NPCs think and react, as % of the default: every wait of the brain (looks, backing out of a stuck, resting, keeping a way around an obstacle, rechecks while holding) is scaled by this. 50 = twice as quick, 500 = five times slower.",
                new AcceptableValueRange<float>(1f, 500f)));
            TurnRate = H("Brain", "TurnRate", 210f, new ConfigDescription(
                "How fast an NPC turns its body, degrees per second - also to face you for a shot (the game snapped instantly).", new AcceptableValueRange<float>(30f, 720f)));
            AimPose = H("Brain", "AimPose", true,
                "A gunman holding a shooting position keeps the gun up and aimed at you between bursts (the game lowered it to the idle pose and raised it only for the shot).");
            CrouchChance = H("Brain", "CrouchChance", 50f, new ConfigDescription(
                "% chance that a gunman kneels when he takes a shooting position (humans only; he stands up when he moves again). He is harder to hit kneeling: his hitbox shrinks with him.",
                new AcceptableValueRange<float>(0f, 100f)));
            FeelerLength = H("Brain", "FeelerLength", 3.5f, new ConfigDescription("How far ahead a moving NPC looks for obstacles, m.", new AcceptableValueRange<float>(1f, 10f)));
            SensorInterval = H("Brain", "SensorInterval", 0.1f, new ConfigDescription(
                "How often an NPC's eyes look, s: the game's sensors pulse on a slow fixed interval, so an NPC noticed you a second or two after you came into view. 0 = the game's own interval.",
                new AcceptableValueRange<float>(0f, 5f)));
            MeleeFeelerLength = H("Brain", "MeleeFeelerLength", 2.5f, new ConfigDescription("How far ahead a melee NPC looks, m (they turn quicker than a gunman needs).", new AcceptableValueRange<float>(1f, 10f)));
            ShooterPathing = H("Brain", "ShooterPathing", true,
                "Gunmen on the move use the melee pathing too: body-wide feelers that see low rocks, posts and fence bars, a committed way around an obstacle, wall following. Off = the simpler rays of 0.7.0 (cheaper; they stop to shoot anyway).");
            FeelerAngle = H("Brain", "FeelerAngle", 60f, new ConfigDescription("Half-angle of the feeler fan around the direction to the target, degrees.", new AcceptableValueRange<float>(15f, 120f)));
            FeelerCount = H("Brain", "FeelerCount", 7, new ConfigDescription("Feeler rays per look (odd; fewer = cheaper, coarser).", new AcceptableValueRange<int>(3, 15)));
            DropCheck = H("Brain", "DropCheck", true, "A moving NPC also checks for ground 1.5 m along its chosen direction and picks another when there is a drop (one extra ray).");
            AdvanceChance = H("Brain", "AdvanceChance", 10f, new ConfigDescription(
                "A gunman holding a shooting position rolls this % at every hold recheck ([NpcAim] HoldRecheckMin..Max s) to run toward you for AdvanceMin..AdvanceMax s instead.", new AcceptableValueRange<float>(0f, 100f)));
            AdvanceMin = H("Brain", "AdvanceMin", 2f, new ConfigDescription("Shortest advance, s.", new AcceptableValueRange<float>(0.5f, 20f)));
            AdvanceMax = H("Brain", "AdvanceMax", 4f, new ConfigDescription("Longest advance, s.", new AcceptableValueRange<float>(0.5f, 20f)));
            StuckBackupSeconds = H("Brain", "StuckBackupSeconds", 0.8f, new ConfigDescription("A stuck NPC backs up this long, s, before trying another way.", new AcceptableValueRange<float>(0.1f, 5f)));
            StuckMemorySeconds = H("Brain", "StuckMemorySeconds", 5f, new ConfigDescription("How long the heading it got stuck on is avoided, s.", new AcceptableValueRange<float>(0f, 60f)));
            StuckGiveUpCount = H("Brain", "StuckGiveUpCount", 3, new ConfigDescription("Stucks within 10 s after which the NPC stands still for a second (facing you) before trying again.", new AcceptableValueRange<int>(1, 20)));
            MaxDistance = H("Brain", "MaxDistance", 150f, new ConfigDescription("NPCs farther than this from their target move the game's way (no cost).", new AcceptableValueRange<float>(20f, 1000f)));
            DaylightIntensity = H("Senses", "DaylightIntensity", 0f, new ConfigDescription("Main-light intensity that counts as full daylight. 0 = Enviro's own sun setting. Raise it if nights feel too bright to NPCs, lower it if days feel dark (VerboseLog prints the light reading every minute).", new AcceptableValueRange<float>(0f, 20f)));
            NoticeSeconds = H("Senses", "NoticeSeconds", 0.2f, new ConfigDescription("How long a target has to be in view before the NPC reacts, s.", new AcceptableValueRange<float>(0f, 5f)));
            LoseSeconds = H("Senses", "LoseSeconds", 10f, new ConfigDescription("How long a target can be out of view before the NPC counts it as lost and goes to where it last saw it, s.", new AcceptableValueRange<float>(0f, 10f)));
            SearchSeconds = H("Senses", "SearchSeconds", 30f, new ConfigDescription("How long an NPC looks around at the place it went to check before it loses interest, s.", new AcceptableValueRange<float>(0f, 120f)));
            PursuitMin = H("Senses", "PursuitMin", 1, new ConfigDescription("An NPC that loses you from sight goes to where it last saw you and, finding nothing, follows to where you really are this many times at least before it starts searching.", new AcceptableValueRange<int>(0, 10)));
            PursuitMax = H("Senses", "PursuitMax", 3, new ConfigDescription("... and this many times at most (rolled per chase).", new AcceptableValueRange<int>(0, 10)));
            GhostTimeout = H("Senses", "GhostTimeout", 60f, new ConfigDescription("An NPC walking to a spot it goes to check (a ghost) keeps going until it gets there; only if this many seconds pass with no news about that spot (the ghost not renewed or moved) does it give up the walk and search from where it is, s.", new AcceptableValueRange<float>(5f, 600f)));
            ArriveDistance = H("Senses", "ArriveDistance", 1.5f, new ConfigDescription("How close to the remembered spot counts as being there, m.", new AcceptableValueRange<float>(0.5f, 10f)));
            LookInterval = H("Senses", "LookInterval", 0.15f, new ConfigDescription("How often each NPC looks, s (a couple of rays per look).", new AcceptableValueRange<float>(0.05f, 2f)));
            MuffleFactor = H("Senses", "MuffleFactor", 50f, new ConfigDescription("Hearing range through walls, % of the open-air range (with MuffleSounds).", new AcceptableValueRange<float>(0f, 100f)));
            ShotRangePistol = H("Senses", "ShotRangePistol", 80f, new ConfigDescription("A pistol or revolver shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeSmg = H("Senses", "ShotRangeSmg", 120f, new ConfigDescription("An SMG shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeRifle = H("Senses", "ShotRangeRifle", 150f, new ConfigDescription("A rifle or machine-gun shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeSniper = H("Senses", "ShotRangeSniper", 150f, new ConfigDescription("A sniper rifle shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeShotgun = H("Senses", "ShotRangeShotgun", 150f, new ConfigDescription("A shotgun blast is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ShotRangeCrossbow = H("Senses", "ShotRangeCrossbow", 15f, new ConfigDescription("A crossbow shot is heard this far, m.", new AcceptableValueRange<float>(0f, 1000f)));
            NpcShotRanges = H("Senses", "NpcShotRanges", "Flexa=150, Gungirl=150, Lugnut=150, Scrud=150, Boltjaw=120, Sprokka=80, Shoota=80, Pistoleer=80, Gunnar=150, Lugger=150",
                "How far each NPC type's gunfire is heard, m, as Type=metres pairs; a type not listed uses the range of its weapon class above.");
            AllClearRange = H("Senses", "AllClearRange", 30f, new ConfigDescription("A human that searched a spot and found nothing tells same-faction humans within this range who are going to (or searching) the same spot: they give up and go back too.", new AcceptableValueRange<float>(0f, 200f)));
            TauntRange = H("Senses", "TauntRange", 15f, new ConfigDescription("A human's shout passes on what it knows to same-faction humans within this range - what it sees (as sight) or the spot it is going to check (at that spot's own rank) - and tells its enemies where it stands, m. 0 = off.", new AcceptableValueRange<float>(0f, 200f)));
            ShoutBlocksButtons = H("Senses", "ShoutBlocksButtons", "Kick,ShiftDown",
                "The game's input buttons (Input Manager names) ignored while ShoutModifier is held.");
            PlayerShoutRange = H("Senses", "PlayerShoutRange", 25f, new ConfigDescription("How far your shout carries, m.", new AcceptableValueRange<float>(0f, 300f)));
            ShoutCooldown = H("Senses", "ShoutCooldown", 1.5f, new ConfigDescription("Shortest time between two of your shouts, s.", new AcceptableValueRange<float>(0f, 30f)));
            StormSight = H("Senses", "StormSight", 80f, new ConfigDescription("Sight range in a sandstorm, % of normal: when the NPC looking or what it looks at is inside one (applied once, not twice).", new AcceptableValueRange<float>(0f, 100f)));
            StormHearing = H("Senses", "StormHearing", 50f, new ConfigDescription("Hearing range in a sandstorm, % of normal: gunfire, explosions, shouts and engines, when the sound or the listener is inside one (applied once, not twice).", new AcceptableValueRange<float>(0f, 100f)));
            StormRadius = H("Senses", "StormRadius", 2500f, new ConfigDescription("A sandstorm covers this far around its centre, m (the game's own distance for the sand around the player).", new AcceptableValueRange<float>(100f, 10000f)));
            ShoutVolume = H("Senses", "ShoutVolume", 1f, new ConfigDescription("Volume of your shout.", new AcceptableValueRange<float>(0f, 1f)));
            HumanFactions = H("Senses", "HumanFactions", "Scrapyard,Coyotes", "Which factions (object tags) count as humans for taunts.");
            EngineMinRange = H("Senses", "EngineMinRange", 50f, new ConfigDescription("Your running engine is heard this far with the weakest engine (EngineMinHp), m.", new AcceptableValueRange<float>(0f, 1000f)));
            EngineMaxRange = H("Senses", "EngineMaxRange", 150f, new ConfigDescription("... and this far with the strongest (EngineMaxHp), m.", new AcceptableValueRange<float>(0f, 1000f)));
            EngineMinHp = H("Senses", "EngineMinHp", 40f, new ConfigDescription("Horsepower that counts as the weakest engine.", new AcceptableValueRange<float>(1f, 2000f)));
            EngineMaxHp = H("Senses", "EngineMaxHp", 300f, new ConfigDescription("Horsepower that counts as the strongest engine.", new AcceptableValueRange<float>(1f, 2000f)));
            EngineIdleFactor = H("Senses", "EngineIdleFactor", 50f, new ConfigDescription("Engine range while idling (no throttle, standing), % of the driving range. A switched-off engine is silent.", new AcceptableValueRange<float>(0f, 100f)));
            BailOutAware = H("Senses", "BailOutAware", true,
                "With Apocapatrol: a raider bailing out of a car keeps what its crew knew - it fights at once if it sees you, otherwise it heads for where you were and searches.");
            BailOutAwareRange = H("Senses", "BailOutAwareRange", 150f, new ConfigDescription("With Apocapatrol: a crew bailing out knows where you are if you are within this range of its car, m.", new AcceptableValueRange<float>(0f, 1000f)));
            ExplosionRange = H("Senses", "ExplosionRange", 150f, new ConfigDescription("With Apocapatrol: an exploding raider car is heard this far (like a gunshot), m. 0 = silent.", new AcceptableValueRange<float>(0f, 1000f)));
            BlastRange = H("Senses", "BlastRange", 150f, new ConfigDescription("Grenades, blast lances, Blast Rats and Blast Zombies exploding are heard this far (like a gunshot), m. 0 = silent.", new AcceptableValueRange<float>(0f, 1000f)));
            BlastPrefabs = H("Senses", "BlastPrefabs", "Explosion_Grenade,Explosion_Can,Explosion_BlastRat,Explosion_BlastZombie", "Explosion prefabs heard within BlastRange when the game spawns them (Explosion_Grenade = grenade + blast lance, Explosion_Can = stronger blast lance, Explosion_BlastRat / Explosion_BlastZombie = the exploding creatures).");
            ThrowRange = H("Senses", "ThrowRange", 10f, new ConfigDescription("An item you throw draws NPCs within this range of where it lands, m. 0 = off.", new AcceptableValueRange<float>(0f, 200f)));
            NavBakeRange = H("Nav", "BakeRange", 200f, new ConfigDescription("A structure is mapped when you come within this distance of it, m.", new AcceptableValueRange<float>(30f, 1000f)));
            NavCellSize = H("Nav", "CellSize", 0.5f, new ConfigDescription("Map resolution, m (smaller = narrower gaps found, slower mapping). Very large structures get coarser cells automatically.", new AcceptableValueRange<float>(0.25f, 2f)));
            NavMargin = H("Nav", "Margin", 4f, new ConfigDescription("Open ground mapped around a structure's outline, m.", new AcceptableValueRange<float>(1f, 20f)));
            NavMaxStep = H("Nav", "MaxStep", 0.25f, new ConfigDescription("Largest height step between neighbouring map cells (0.5 m apart) an NPC can walk, m. An NPC's body can't climb much more than a kerb.", new AcceptableValueRange<float>(0.1f, 2f)));
            NavBakeBudgetMs = H("Nav", "BakeBudgetMs", 1f, new ConfigDescription("CPU time per frame spent mapping a structure, ms.", new AcceptableValueRange<float>(0.2f, 10f)));
            NavFieldSeconds = H("Nav", "FieldSeconds", 1f, new ConfigDescription("How long a computed route to one goal is reused by every NPC heading there, s.", new AcceptableValueRange<float>(0.2f, 10f)));
            IdleReturnDelay = H("Idle", "ReturnDelay", 0f, new ConfigDescription("Seconds an NPC stays where it lost track of everything before it heads home.", new AcceptableValueRange<float>(0f, 300f)));
            IdleReturnTries = H("Idle", "ReturnTries", 10, new ConfigDescription("Failed attempts to get home before the NPC forgets its home.", new AcceptableValueRange<int>(1, 100)));
            IdleRetrySeconds = H("Idle", "RetrySeconds", 5f, new ConfigDescription("Pause between two attempts to get home, s.", new AcceptableValueRange<float>(0f, 60f)));
            MoveFullSpeedAngle = H("Movement", "FullSpeedAngle", 30f, new ConfigDescription("A moving NPC runs at full speed while its body faces within this many degrees of where it wants to go.", new AcceptableValueRange<float>(0f, 180f)));
            MoveSlowestAngle = H("Movement", "SlowestAngle", 120f, new ConfigDescription("At this many degrees off (and beyond) it moves at SlowestSpeed - a big turn is made almost on the spot instead of as an arc.", new AcceptableValueRange<float>(1f, 180f)));
            MoveSlowestSpeed = H("Movement", "SlowestSpeed", 0.1f, new ConfigDescription("Speed factor (0-1) at SlowestAngle; between the two angles it goes linearly.", new AcceptableValueRange<float>(0f, 1f)));
            BlockedRatio = H("Movement", "BlockedRatio", 0.33f, new ConfigDescription("A chasing NPC that covers less than this share of the distance it is driven (running in place, grinding along a wall) is blocked: it hops or steps back and picks another way.", new AcceptableValueRange<float>(0.05f, 0.9f)));
            BlockedSeconds = H("Movement", "BlockedSeconds", 0.5f, new ConfigDescription("Over how many seconds that share is measured.", new AcceptableValueRange<float>(0.2f, 3f)));
            BlockedMemory = H("Movement", "BlockedMemory", 1.5f, new ConfigDescription("For how many seconds the direction it was blocked in is not taken again (on a map route).", new AcceptableValueRange<float>(0f, 10f)));
            IdleWalkRadius = H("Idle", "WalkRadius", 200f, new ConfigDescription("Camp walks only for NPCs within this distance of the camera, m.", new AcceptableValueRange<float>(20f, 1000f)));
            IdleGhostWalk = H("Idle", "GhostWalk", true, "(1.6.0) A camp raider going to check a spot (a shot, a noise, where you were last seen) is walked there the way it walks home: the camp map to the exit on that side, then straight. Off: the fight brain walks it (pre-1.6).");
            NavEnabled = BrainEnabled;
            NavLog = SensesLog = BrainLog = VerboseLog;
            ShowGhosts = ShowNav;
            LegacyConfig.Import(Config, Log, existing);
            try
            {
                var h = new Harmony(GUID + ".brain");
                h.Patch(AccessTools.Method(typeof(SetVelocity), "DoSetVelocity"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeSetVelocity)));
                h.Patch(AccessTools.Method(typeof(Rotate), "DoRotate"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeRotate)));
                h.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.Raycast), "DoRaycast"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeRaycast)));
                h.Patch(AccessTools.Method(typeof(LookAt), "DoLookAt"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeLookAt)));
                h.Patch(AccessTools.Method(typeof(SmoothLookAt), "DoSmoothLookAt"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeSmoothLookAt)));
                h.Patch(AccessTools.Method(typeof(SendEvent), "OnEnter"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeSendEvent)));
                h.Patch(AccessTools.Method(typeof(AddForce), "DoAddForce"), prefix: new HarmonyMethod(typeof(Brain), nameof(Brain.BeforeAddForce)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.LOSSensor), "OnEnable"), postfix: new HarmonyMethod(typeof(Brain), nameof(Brain.AfterLosEnable)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.RangeSensor), "OnEnable"), postfix: new HarmonyMethod(typeof(Brain), nameof(Brain.AfterRangeEnable)));
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, no NPC brain: " + e); }
            try
            {
                var h = new Harmony(GUID + ".senses");
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetDetections), "DoAction"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetDetections)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult), "OnEnter3D"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeLosResult)));
                h.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult), "OnUpdate3D"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeLosResult)));
                h.Patch(AccessTools.Method(typeof(AudioPlay), "OnEnter"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeAudioPlay)));
                h.Patch(AccessTools.Method(typeof(CreateObject), "OnEnter"), postfix: new HarmonyMethod(typeof(Senses), nameof(Senses.AfterCreateObject)));
                h.Patch(AccessTools.Method(typeof(CreateObject), "OnEnter"), postfix: new HarmonyMethod(typeof(Idle), nameof(Idle.AfterCreateObject)));
                h.Patch(AccessTools.Method(typeof(GetButton), "DoGetButton"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetButton)));
                h.Patch(AccessTools.Method(typeof(GetButtonDown), "OnUpdate"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetButtonDown)));
                h.Patch(AccessTools.Method(typeof(GetButtonUp), "OnUpdate"), prefix: new HarmonyMethod(typeof(Senses), nameof(Senses.BeforeGetButtonUp)));
            }
            catch (Exception e) { Log.LogError("Harmony patch failed, no senses: " + e); }
            var aim = new Harmony(GUID + ".aim");
            aim.Patch(AccessTools.Method(typeof(SendEvent), "OnEnter"), prefix: new HarmonyMethod(typeof(Aim), nameof(Aim.BeforeSendEvent)));
            aim.Patch(AccessTools.Method(typeof(RandomWait), "OnEnter"), prefix: new HarmonyMethod(typeof(Aim), nameof(Aim.BeforeRandomWait)));
            var vanilla = new Harmony(GUID + ".vanilla");
            vanilla.Patch(AccessTools.Method(typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit), "OnEnter"), prefix: new HarmonyMethod(typeof(VanillaEvents), nameof(VanillaEvents.BeforeNpcShot)), finalizer: new HarmonyMethod(typeof(VanillaEvents), nameof(VanillaEvents.AfterNpcShot)));
            vanilla.Patch(AccessTools.Method(typeof(Raycast), "OnEnter"), prefix: new HarmonyMethod(typeof(VanillaEvents), nameof(VanillaEvents.BeforePlayerShot)));
            vanilla.Patch(AccessTools.Method(typeof(SetFsmFloat), "OnEnter"), postfix: new HarmonyMethod(typeof(VanillaEvents), nameof(VanillaEvents.AfterDamage)));
            SceneManager.sceneLoaded += SceneLoaded;
            EnsureRunner();
            Log.LogInfo(NAME + " " + VERSION + " loaded");
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        { EnsureRunner(); Aim.OnSceneLoaded(); WeaponRanges.OnSceneLoaded(); Brain.OnSceneLoaded(); Senses.OnSceneLoaded(); Nav.OnSceneLoaded(); Idle.OnSceneLoaded(); Passthrough.OnSceneLoaded(); }
        private static void EnsureRunner()
        {
            if (_runner != null) return;
            _runner = new GameObject("NPCAI.Runner") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_runner); _runner.AddComponent<Runner>();
        }
        internal static void Verbose(string s) { if (VerboseLog != null && VerboseLog.Value && Log != null) Log.LogInfo(s); }
        internal static void Warn(string s) { if (Log != null) Log.LogWarning(s); }
    }
    internal sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            try { Senses.Tick(this); } catch (Exception e) { Plugin.Log.LogError("Senses: " + e); }
            try { Nav.Tick(); } catch (Exception e) { Plugin.Log.LogError("Nav: " + e); }
            try { Brain.Tick(); } catch (Exception e) { Plugin.Log.LogError("Brain: " + e); }
            try { Idle.Tick(); } catch (Exception e) { Plugin.Log.LogError("Idle: " + e); }
            try { Passthrough.Tick(); } catch (Exception e) { Plugin.Log.LogError("Passthrough: " + e); }
            Aim.Sweep();
            WeaponRanges.Sweep();
        }
        private void LateUpdate() { try { Brain.LateTick(); } catch (Exception e) { Plugin.Log.LogError("Brain: " + e); } }
        private void OnGUI() { DebugOverlay.OnGUI(); }
    }
}
