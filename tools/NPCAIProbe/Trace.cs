using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAIProbe
{
    // Observes FSM activity of the FSMs NPCAI works with: entered states, transitions (from --event--> to) and received events.
    // Per FSM instance the counters are keyed by object references (no strings per call); every write folds them into text totals.
    internal static class Trace
    {
        internal static readonly HashSet<string> NpcFsms = new HashSet<string> { "Detection", "Attack", "Movement", "Rotate", "Unstuck", "RangedAttackWait", "Damage Ranged", "Sound", "PlayerIsEnemy", "Health" };
        private static readonly HashSet<string> OtherFsms = new HashSet<string> { "InCar", "GrabItem", "SaveLoadGame", "INPUT_Horn", "Reload" };

        private sealed class Counter { public string Group, Line; public int N; }
        private static readonly Dictionary<FsmState, Counter> _entered = new Dictionary<FsmState, Counter>();
        private static readonly Dictionary<FsmTransition, Dictionary<FsmState, Counter>> _trans = new Dictionary<FsmTransition, Dictionary<FsmState, Counter>>();
        private static readonly Dictionary<FsmState, Dictionary<FsmEvent, Counter>> _events = new Dictionary<FsmState, Dictionary<FsmEvent, Counter>>();
        private static readonly Dictionary<Fsm, Counter> _noState = new Dictionary<Fsm, Counter>();
        private static readonly Dictionary<int, string> _groupOf = new Dictionary<int, string>();   // GameObject id -> "Prefab"
        // totals: group ("Lugnut [Attack]") -> line -> count
        private static readonly SortedDictionary<string, SortedDictionary<string, int>> _totals = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        internal static readonly SortedDictionary<string, int> Spawns = new SortedDictionary<string, int>(StringComparer.Ordinal);   // CreateObject observations

        internal static void OnSceneLoaded() { Fold(); _groupOf.Clear(); }

        private static bool Wanted(Fsm fsm, out string group)
        {
            group = null;
            if (fsm == null || !Plugin.Enabled.Value || !Plugin.TraceTransitions.Value) return false;
            string name = fsm.Name;
            bool npc = NpcFsms.Contains(name);
            if (!npc && !OtherFsms.Contains(name)) return false;
            var go = fsm.GameObject;
            if (go == null) return false;
            int id = go.GetInstanceID();
            string g;
            if (!_groupOf.TryGetValue(id, out g))
            {
                var root = go.transform.root.gameObject;
                bool isNpc = Checks.IsNpcRoot(root);
                if (isNpc) { g = Plugin.Prefab(root.name); Checks.SeenNpc(root); }
                else if (name == "Reload" || (name == "Attack" && Checks.PlayerWeapon(go))) { g = "WEAPON " + Plugin.Prefab(go.name); Checks.SeenWeapon(go); }
                else if (npc) g = "other " + Plugin.Prefab(root.name);
                else g = Plugin.Prefab(go.name);
                if (name == "INPUT_Horn") Checks.SeenHorn(fsm);
                if (_groupOf.Count > 20000) _groupOf.Clear();
                _groupOf[id] = g;
            }
            group = g + " [" + name + "]";
            return true;
        }

        public static void AfterEnterState(Fsm __instance, FsmState state)
        {
            try
            {
                if (state == null) return;
                Counter c;
                if (_entered.TryGetValue(state, out c)) { c.N++; return; }
                string g;
                if (!Wanted(__instance, out g)) return;
                _entered[state] = new Counter { Group = g, Line = "enter  " + state.Name, N = 1 };
            }
            catch (Exception) { }
        }

        public static void BeforeDoTransition(Fsm __instance, FsmTransition transition, bool isGlobal)
        {
            try
            {
                if (transition == null) return;
                var from = __instance.ActiveState;
                Dictionary<FsmState, Counter> byFrom;
                Counter c;
                if (from == null) return;      // no active state (FSM starting): not a transition worth counting
                var key = from;
                if (_trans.TryGetValue(transition, out byFrom) && byFrom.TryGetValue(key, out c)) { c.N++; return; }
                string g;
                if (!Wanted(__instance, out g)) return;
                if (byFrom == null) _trans[transition] = byFrom = new Dictionary<FsmState, Counter>();
                byFrom[key] = new Counter { Group = g, Line = "trans  " + from.Name + " --" + transition.EventName + "--> " + transition.ToState + (isGlobal ? "  (global)" : ""), N = 1 };
            }
            catch (Exception) { }
        }

        public static void BeforeProcessEvent(Fsm __instance, FsmEvent fsmEvent)
        {
            try
            {
                if (fsmEvent == null) return;
                var st = __instance.ActiveState;
                Counter c;
                if (st == null)
                {
                    if (_noState.TryGetValue(__instance, out c)) { c.N++; return; }
                    string g0; if (!Wanted(__instance, out g0)) return;
                    _noState[__instance] = new Counter { Group = g0, Line = "event  (no state) <- " + fsmEvent.Name, N = 1 };
                    return;
                }
                Dictionary<FsmEvent, Counter> byEv;
                if (_events.TryGetValue(st, out byEv) && byEv.TryGetValue(fsmEvent, out c)) { c.N++; return; }
                string g;
                if (!Wanted(__instance, out g)) return;
                if (byEv == null) _events[st] = byEv = new Dictionary<FsmEvent, Counter>();
                bool handled = false;
                if (st.Transitions != null) foreach (var t in st.Transitions) if (t != null && t.EventName == fsmEvent.Name) { handled = true; break; }
                if (!handled && __instance.GlobalTransitions != null) foreach (var t in __instance.GlobalTransitions) if (t != null && t.EventName == fsmEvent.Name) { handled = true; break; }
                byEv[fsmEvent] = new Counter { Group = g, Line = "event  in " + st.Name + " <- " + fsmEvent.Name + (handled ? "" : "  (no transition: ignored)"), N = 1 };
            }
            catch (Exception) { }
        }

        // what the game spawns with CreateObject (spawners -> NPCAI Idle homes; explosion prefab names -> [Senses] BlastPrefabs)
        public static void AfterCreateObject(CreateObject __instance)
        {
            try
            {
                if (!Plugin.Enabled.Value) return;
                var prefab = __instance.gameObject != null ? __instance.gameObject.Value : null;
                var owner = __instance.Fsm != null ? __instance.Fsm.GameObject : null;
                if (prefab == null || owner == null) return;
                string on = Plugin.Prefab(owner.name);
                bool spawner = on.StartsWith("EnemySpawn", StringComparison.Ordinal);
                bool blastish = prefab.name.StartsWith("Explosion", StringComparison.Ordinal);
                if (!spawner && !blastish && !Checks.IsNpcRoot(prefab)) return;
                string extra = "";
                if (spawner)
                {
                    var sp = __instance.spawnPoint != null ? __instance.spawnPoint.Value : null;
                    bool stored = __instance.storeObject != null && !__instance.storeObject.IsNone;
                    var t = owner.transform; var p = t.parent;
                    string next = p != null && t.GetSiblingIndex() + 1 < p.childCount ? p.GetChild(t.GetSiblingIndex() + 1).name : "(none)";
                    extra = " spawnPoint=" + (sp != null ? Plugin.Prefab(sp.transform.parent != null ? sp.transform.parent.name : "") + "/" + sp.name : "NONE")
                          + " storeObject=" + (stored ? "set" : "NONE") + " nextSibling=" + Plugin.Prefab(next)
                          + (next.StartsWith("spawn_locations_enemy", StringComparison.Ordinal) ? "" : "  <-- Idle expects spawn_locations_enemy* right after the spawner");
                }
                string key = on + " [" + (__instance.Fsm != null ? __instance.Fsm.Name : "?") + "] spawns " + Plugin.Prefab(prefab.name) + extra;
                int n; Spawns.TryGetValue(key, out n); Spawns[key] = n + 1;
            }
            catch (Exception) { }
        }

        private static void Add(Counter c)
        {
            if (c.N <= 0) return;
            SortedDictionary<string, int> lines;
            if (!_totals.TryGetValue(c.Group, out lines)) _totals[c.Group] = lines = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int n; lines.TryGetValue(c.Line, out n); lines[c.Line] = n + c.N;
            c.N = 0;
        }

        // the reference-keyed counters into the text totals; the references are dropped (despawned NPCs are not kept alive)
        private static void Fold()
        {
            foreach (var c in _entered.Values) Add(c);
            foreach (var d in _trans.Values) foreach (var c in d.Values) Add(c);
            foreach (var d in _events.Values) foreach (var c in d.Values) Add(c);
            foreach (var c in _noState.Values) Add(c);
            _entered.Clear(); _trans.Clear(); _events.Clear(); _noState.Clear();
        }

        internal static bool Saw(string group, string linePrefix)
        {
            SortedDictionary<string, int> lines;
            if (!_totals.TryGetValue(group, out lines)) return false;
            foreach (var l in lines.Keys) if (l.StartsWith(linePrefix, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static IEnumerable<KeyValuePair<string, string>> Lines()
        {
            Fold();
            foreach (var g in _totals) foreach (var l in g.Value.Keys) yield return new KeyValuePair<string, string>(g.Key, l);
        }

        internal static void Write()
        {
            Fold();
            var sb = new StringBuilder();
            sb.AppendLine("# NPCAIProbe transitions, " + DateTime.Now + "  (counts since the game started; 'ignored' = the event reached a state with no transition for it)");
            foreach (var kv in _totals)
            {
                sb.AppendLine();
                sb.AppendLine("== " + kv.Key);
                foreach (var l in kv.Value) sb.AppendLine("   " + l.Value.ToString().PadLeft(7) + "  " + l.Key);
            }
            sb.AppendLine();
            sb.AppendLine("== CreateObject spawns seen (spawners, explosions, NPCs)");
            foreach (var kv in Spawns) sb.AppendLine("   " + kv.Value.ToString().PadLeft(7) + "  " + kv.Key);
            File.WriteAllText(Path.Combine(Plugin.OutDir, "transitions.txt"), sb.ToString());
        }
    }
}
