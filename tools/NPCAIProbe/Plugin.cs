using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NPCAIProbe
{
    // Debug-only helper for NPCAI. Install it, play normally (camps, fights, cars, saves/loads), quit; then read BepInEx\NPCAIProbe\:
    //   report.txt       every assumption NPCAI makes about the game's FSMs/objects, checked per NPC prefab and globally (OK / FAIL / n/a),
    //                    NPCAI's Harmony hooks and who else patches the same methods, the latest NPCAI health snapshot
    //   transitions.txt  the state transitions, entered states and received events that really happened, per prefab and FSM
    //   prefabs\*.txt    a full FSM dump (every action with its fields) + sensors/animator of the first instance of each NPC prefab
    //   health.log       NPCAI's internal state every 30 s (collection sizes -> leaks; agents holding dead ghosts, etc.)
    //   perf.log         (0.1.6) frame time and each NPCAI subsystem's time every 5 s, rig count, Nav status
    //   melee.log        (0.1.7) every animal / melee NPC near the camera: NPCAI mode vs the game's FSM states vs the attack ray / collider geometry vs the player
    //   ground.log       (0.1.8) the ground under the player and every Scorpion_Small: collider, terrain texture layers and weights (what is "sand"?); prefabs\Burrower_Effect.txt = the sand worm's puff
    //   anim.log         (0.1.6) every gunman near the camera: NPCAI's rig state vs the game's FSM / Animator state vs the hands' children
    // Files are rewritten every 30 s and on quit. Nothing in the game is changed: all hooks only observe.
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.denis.apocalypter.npcai", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.npcaiprobe", NAME = "NPCAI Probe", VERSION = "0.1.8";
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled, TraceTransitions;
        internal static ConfigEntry<float> WriteSeconds;
        internal static string OutDir;
        internal static Plugin Instance; internal static Harmony HarmonyInstance;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true, "Run the NPCAI checks and traces (debug tool: remove the DLL when done).");
            TraceTransitions = Config.Bind("General", "TraceTransitions", true, "Record the NPC FSMs' state transitions and received events.");
            WriteSeconds = Config.Bind("General", "WriteSeconds", 30f, new ConfigDescription("How often the report files are rewritten, s.", new AcceptableValueRange<float>(5f, 600f)));
            OutDir = Path.Combine(Paths.BepInExRootPath, "NPCAIProbe");
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(Path.Combine(OutDir, "prefabs"));
            try
            {
                var h = new Harmony(GUID); HarmonyInstance = h;
                h.Patch(AccessTools.Method(typeof(Fsm), "EnterState"), postfix: new HarmonyMethod(typeof(Trace), nameof(Trace.AfterEnterState)));
                h.Patch(AccessTools.Method(typeof(Fsm), "DoTransition"), prefix: new HarmonyMethod(typeof(Trace), nameof(Trace.BeforeDoTransition)));
                h.Patch(AccessTools.Method(typeof(Fsm), "ProcessEvent"), prefix: new HarmonyMethod(typeof(Trace), nameof(Trace.BeforeProcessEvent)));
                h.Patch(AccessTools.Method(typeof(CreateObject), "OnEnter"), postfix: new HarmonyMethod(typeof(Trace), nameof(Trace.AfterCreateObject)));
            }
            catch (Exception e) { Log.LogError("NPCAIProbe hooks failed: " + e); }
            SceneManager.sceneLoaded += (s, m) => { Trace.OnSceneLoaded(); EnsureRunner(); };
            EnsureRunner();
            try { File.AppendAllText(Path.Combine(OutDir, "health.log"), Environment.NewLine + "=== game start " + DateTime.Now + Environment.NewLine); } catch (Exception) { }
            Log.LogInfo(NAME + " " + VERSION + " loaded, output in " + OutDir);
        }

        // the game disables BepInEx's own manager object (a plugin's Update never runs here): our own runner, like NPCAI's
        private static GameObject _runner;
        private static void EnsureRunner()
        {
            if (_runner != null) return;
            _runner = new GameObject("NPCAIProbe.Runner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(_runner);
            _runner.AddComponent<Runner>();
            Log.LogInfo("NPCAIProbe runner created");
        }
    }

    internal sealed class Runner : MonoBehaviour
    {
        private float _nextWrite = 10f, _nextGlobal = 5f;
        private bool _firstWrite;
        private void Update()
        {
            if (!Plugin.Enabled.Value) return;
            float now = Time.unscaledTime;
            try { Checks.Pump(now); } catch (Exception e) { Plugin.Log.LogError("Probe checks: " + e); }
            AnimTrace.Pump(now);
            MeleeTrace.Pump(now);
            GroundTrace.Pump(now);
            Perf.TryPatch(Plugin.HarmonyInstance);
            Perf.Frame(now);
            if (now >= _nextGlobal) { _nextGlobal = now + 15f; try { Checks.Global(); } catch (Exception e) { Plugin.Log.LogError("Probe global checks: " + e); } }
            if (now >= _nextWrite)
            {
                _nextWrite = now + Mathf.Max(5f, Plugin.WriteSeconds.Value);
                Plugin.WriteAll();
                if (!_firstWrite) { _firstWrite = true; Plugin.Log.LogInfo("NPCAIProbe: first report written"); }
            }
        }

        private void OnApplicationQuit() { if (Plugin.Enabled.Value) { Plugin.WriteAll(); AnimTrace.Flush(); MeleeTrace.Flush(); } }
    }

    public sealed partial class Plugin
    {
        internal static void WriteAll()
        {
            try { Health.Snapshot(); } catch (Exception e) { Log.LogError("Probe health: " + e); }
            try { Report.Write(); } catch (Exception e) { Log.LogError("Probe report: " + e); }
            try { Trace.Write(); } catch (Exception e) { Log.LogError("Probe trace: " + e); }
            try { NavDump.Write(Health.NpcaiAssembly); } catch (Exception e) { Log.LogError("Probe nav: " + e); }
        }

        internal static string Prefab(string n)
        {
            if (n == null) return "?";
            int c = n.IndexOf('(');
            if (c > 0) return n.Substring(0, c).Trim();
            int e = n.Length; while (e > 0 && (char.IsDigit(n[e - 1]) || n[e - 1] == ' ')) e--;
            return e > 0 ? n.Substring(0, e) : n;
        }

        internal static string PathOf(Transform t)
        {
            if (t == null) return "?";
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
