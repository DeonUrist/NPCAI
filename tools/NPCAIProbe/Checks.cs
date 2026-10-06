using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAIProbe
{
    // Every assumption NPCAI (1.0.x) makes about the game, checked on live objects. Per NPC prefab once (the first instance, 2 s after
    // it was first seen), per player weapon once, globally every 15 s. Results go to Report.
    internal static class Checks
    {
        internal enum St { OK, FAIL, INFO, NA }
        internal sealed class Row { public St S; public string Detail; }
        internal sealed class Check { public string Id, Desc; public readonly SortedDictionary<string, Row> Rows = new SortedDictionary<string, Row>(StringComparer.Ordinal); }
        internal static readonly SortedDictionary<string, Check> All = new SortedDictionary<string, Check>(StringComparer.Ordinal);

        private static void Put(string id, string desc, string subject, St s, string detail)
        {
            Check c;
            if (!All.TryGetValue(id, out c)) All[id] = c = new Check { Id = id, Desc = desc };
            c.Rows[subject] = new Row { S = s, Detail = detail ?? "" };
        }
        private static void Put(string id, string desc, string subject, bool ok, string detail) { Put(id, desc, subject, ok ? St.OK : St.FAIL, detail); }

        // ---------- discovery ----------
        private static readonly HashSet<string> _npcDone = new HashSet<string>(), _weaponDone = new HashSet<string>();
        private static readonly List<KeyValuePair<GameObject, float>> _queue = new List<KeyValuePair<GameObject, float>>();
        private static readonly HashSet<int> _queued = new HashSet<int>();
        private static readonly Dictionary<int, bool> _isNpc = new Dictionary<int, bool>();

        internal static bool IsNpcRoot(GameObject root)
        {
            if (root == null) return false;
            root = root.transform.root.gameObject;
            int id = root.GetInstanceID(); bool r;
            if (_isNpc.TryGetValue(id, out r)) return r;
            r = Fsm(root, "Detection") != null && Fsm(root, "Attack") != null;
            if (_isNpc.Count > 20000) _isNpc.Clear();
            _isNpc[id] = r;
            return r;
        }

        internal static bool PlayerWeapon(GameObject weapon)
        {
            for (var t = weapon != null ? weapon.transform : null; t != null; t = t.parent)
                if (t.name == "PlayerCamera" || t.name == "PlayerCameraHolder") return true;
            return false;
        }

        internal static void SeenNpc(GameObject root)
        {
            string p = Plugin.Prefab(root.name);
            if (_npcDone.Contains(p) || !_queued.Add(root.GetInstanceID())) return;
            _queue.Add(new KeyValuePair<GameObject, float>(root, Time.unscaledTime + 2f));
        }
        internal static void SeenWeapon(GameObject w)
        {
            if (w == null || Fsm(w, "Attack") == null || Fsm(w, "Reload") == null || !PlayerWeapon(w)) return;
            string p = Plugin.Prefab(w.name);
            if (_weaponDone.Contains(p)) return;
            _weaponDone.Add(p);
            try { CheckWeapon(w, p); } catch (Exception e) { Put("W00", "weapon check crashed", p, St.FAIL, e.Message); }
        }
        private static bool _hornDone;
        internal static void SeenHorn(Fsm f)
        {
            if (_hornDone || f == null) return;
            _hornDone = true;
            var on = f.GetState("on");
            Put("G09", "car horn FSM [INPUT_Horn]: state 'on' with AudioPlay (Senses horn noise)", "horn", on != null && Has<AudioPlay>(on),
                "states: " + StateNames(f) + (on != null ? "; 'on' actions: " + ActionNames(on) : ""));
        }

        internal static void Pump(float now)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                var kv = _queue[i];
                if (kv.Key == null) { _queue.RemoveAt(i); continue; }
                if (now < kv.Value) continue;
                _queue.RemoveAt(i);
                string p = Plugin.Prefab(kv.Key.name);
                if (_npcDone.Contains(p)) continue;
                if (kv.Key.transform.parent != null) { _queued.Remove(kv.Key.GetInstanceID()); continue; }   // seated in a car: a free one later
                _npcDone.Add(p);
                try { CheckNpc(kv.Key, p); } catch (Exception e) { Put("N00", "NPC check crashed", p, St.FAIL, e.ToString()); }
                try { DumpPrefab.Write(kv.Key, p); } catch (Exception e) { Plugin.Log.LogWarning("Probe dump " + p + ": " + e.Message); }
                Plugin.Log.LogInfo("NPCAIProbe: checked " + p);
                break;      // one per frame
            }
        }

        // ---------- helpers ----------
        internal static PlayMakerFSM Fsm(GameObject go, string name)
        {
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == name) return f;
            return null;
        }
        private static FsmState State(PlayMakerFSM f, string name) { return f != null && f.Fsm != null ? f.Fsm.GetState(name) : null; }
        private static IEnumerable<KeyValuePair<FsmState, T>> Actions<T>(PlayMakerFSM f) where T : FsmStateAction
        {
            if (f == null || f.FsmStates == null) yield break;
            foreach (var s in f.FsmStates) if (s != null && s.Actions != null) foreach (var a in s.Actions) { var t = a as T; if (t != null) yield return new KeyValuePair<FsmState, T>(s, t); }
        }
        private static bool Has<T>(FsmState s) where T : FsmStateAction { return s != null && s.Actions != null && s.Actions.Any(a => a is T); }
        private static string StateNames(PlayMakerFSM f) { return f == null || f.FsmStates == null ? "-" : string.Join(", ", f.FsmStates.Select(s => s.Name).ToArray()); }
        private static string StateNames(Fsm f) { return f == null || f.States == null ? "-" : string.Join(", ", f.States.Select(s => s.Name).ToArray()); }
        private static string ActionNames(FsmState s) { return s == null || s.Actions == null ? "-" : string.Join(", ", s.Actions.Select(a => a == null ? "null" : a.GetType().Name + (a.Enabled ? "" : "(off)")).ToArray()); }
        private static string States<T>(IEnumerable<KeyValuePair<FsmState, T>> l) { return string.Join(", ", l.Select(kv => kv.Key.Name).Distinct().ToArray()); }
        private static bool HandlesEvent(PlayMakerFSM f, string ev, out string where)
        {
            var w = new List<string>();
            if (f != null && f.Fsm != null)
            {
                if (f.FsmGlobalTransitions != null) foreach (var t in f.FsmGlobalTransitions) if (t.EventName == ev) w.Add("global->" + t.ToState);
                if (f.FsmStates != null) foreach (var s in f.FsmStates) if (s.Transitions != null) foreach (var t in s.Transitions) if (t.EventName == ev) w.Add(s.Name + "->" + t.ToState);
            }
            where = w.Count == 0 ? "none" : string.Join(", ", w.ToArray());
            return w.Count > 0;
        }
        private static string F(FsmFloat v) { return v == null ? "null" : v.IsNone ? "none" : (string.IsNullOrEmpty(v.Name) ? "" : "{" + v.Name + "}") + v.Value; }
        private static Transform FindDeep(Transform t, Func<Transform, bool> pred)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>(true)) if (pred(c)) return c;
            return null;
        }
        private static string Last(string n) { int i = n.LastIndexOf(':'); if (i < 0) i = n.LastIndexOf('_'); return i >= 0 ? n.Substring(i + 1) : n; }
        private static object Read(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            return null;
        }

        // ---------- per NPC prefab ----------
        private static void CheckNpc(GameObject go, string p)
        {
            var det = Fsm(go, "Detection"); var att = Fsm(go, "Attack"); var mov = Fsm(go, "Movement"); var uns = Fsm(go, "Unstuck");
            var rot = Fsm(go, "Rotate"); var raw = Fsm(go, "RangedAttackWait"); var dr = Fsm(go, "Damage Ranged"); var snd = Fsm(go, "Sound"); var pie = Fsm(go, "PlayerIsEnemy");
            var rb = go.GetComponent<Rigidbody>();
            bool ranged = dr != null;
            Transform muzzle = FindDeep(go.transform, t => t.name == "fire_effect" && t.parent != null && t.parent.name != "fire_effect" && t.parent.gameObject.activeInHierarchy);
            bool gun = muzzle != null;

            Put("N01", "root object: tag, Rigidbody (gravity = brain-eligible), root collider, FSM list", p, St.INFO,
                "tag " + go.tag + ", rb " + (rb == null ? "NONE" : "useGravity=" + rb.useGravity) + ", collider " + (go.GetComponent<Collider>() != null ? go.GetComponent<Collider>().GetType().Name : "NONE")
                + ", FSMs: " + string.Join(", ", go.GetComponents<PlayMakerFSM>().Select(f => f.FsmName).ToArray()));
            Put("N02", "Brain eligibility: Attack+Movement+Detection+Unstuck FSMs and Rigidbody with gravity", p,
                att != null && mov != null && det != null && uns != null && rb != null && rb.useGravity ? St.OK : St.INFO,
                (att == null ? "no Attack " : "") + (mov == null ? "no Movement " : "") + (uns == null ? "no Unstuck " : "") + (rb == null || !rb.useGravity ? "flyer/no gravity" : ""));

            // Detection
            var dv = det != null ? det.FsmVariables.FindFsmGameObject("detectedObj") : null;
            Put("N03", "Detection FSM has GameObject variable detectedObj (Senses/Brain/Aim target)", p, dv != null, det == null ? "no Detection FSM" : "vars: " + string.Join(", ", det.FsmVariables.GameObjectVariables.Select(v => v.Name).ToArray()));
            var sgd = Actions<Micosmo.SensorToolkit.PlayMaker.SensorGetDetections>(det).ToList();
            bool sr = State(det, "searchRange") != null;
            Put("N04", "Detection: SensorGetDetections actions (answered by Senses) and state 'searchRange' (skipped)", p, sgd.Count > 0 && sr,
                "states " + StateNames(det) + " | SensorGetDetections in: " + string.Join("; ", sgd.Select(kv => kv.Key.Name + " (detected=" + (kv.Value.detectedEvent != null ? kv.Value.detectedEvent.Name : "null") + ", none=" + (kv.Value.noneDetectedEvent != null ? kv.Value.noneDetectedEvent.Name : "null") + ")").ToArray()));

            // Attack
            string[] want = ranged ? new[] { "trigger", "attack_ranged", "ranged" } : new[] { "trigger" };
            var missing = want.Where(s => State(att, s) == null && !(s == "trigger" && State(att, "trigger 2") != null)).ToArray();   // NPCAI 1.0.2 accepts "trigger 2" (Nightwalker)
            Put("N05", "Attack states the brain reads (trigger, run, attack_ranged, attack_melee, hide; ranged: 'ranged' LOS state)", p, att != null && missing.Length == 0,
                (missing.Length > 0 ? "MISSING " + string.Join(",", missing) + " | " : "") + "states " + StateNames(att));
            if (ranged)
            {
                var los = State(att, "ranged");
                Put("N06", "Attack 'ranged' has SensorGetLineOfSightResult (Senses answers it)", p, los != null && Has<Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult>(los), los != null ? ActionNames(los) : "no 'ranged' state");
            }
            else Put("N06", "Attack 'ranged' has SensorGetLineOfSightResult (Senses answers it)", p, St.NA, "melee");
            var run = Actions<SendEvent>(att).Where(kv => kv.Value.sendEvent != null && kv.Value.sendEvent.Name == "Animal_Run").ToList();
            Put("N07", "Attack sends Animal_Run (brain turns it into Animal_Idle while holding)", p, run.Count > 0 ? St.OK : St.INFO, "in: " + States(run));
            var rays = Actions<Raycast>(att).ToList();
            Put("N08", "Attack Raycast bumper rays have repeatInterval != 0 (brain reports them clear)", p, rays.Count == 0 ? St.INFO : rays.All(kv => kv.Value.repeatInterval != null && kv.Value.repeatInterval.Value != 0) ? St.OK : St.FAIL,
                string.Join("; ", rays.Select(kv => kv.Key.Name + " repeat=" + (kv.Value.repeatInterval != null ? kv.Value.repeatInterval.Value.ToString() : "null")).ToArray()));
            var look = Actions<LookAt>(att).Where(kv => kv.Value.gameObject != null && kv.Value.gameObject.OwnerOption == OwnerDefaultOption.UseOwner).ToList();
            var slook = Actions<SmoothLookAt>(att).Where(kv => kv.Value.gameObject != null && kv.Value.gameObject.OwnerOption == OwnerDefaultOption.UseOwner).ToList();
            Put("N09", "Attack body-facing actions (LookAt/SmoothLookAt on the owner): brain replaces them in trigger/run only", p, St.INFO,
                "LookAt(owner) in: " + States(look) + " | SmoothLookAt(owner) in: " + States(slook));
            var rotA = Actions<Rotate>(att).ToList();
            Put("N10", "random yaw (Rotate actions) the brain blocks: Rotate FSM + Attack states", p, St.INFO, "Rotate FSM: " + (rot == null ? "none" : Actions<Rotate>(rot).Count() + " action(s) in " + States(Actions<Rotate>(rot))) + " | Attack: " + States(rotA));

            // Movement
            if (mov != null)
            {
                var vel = Actions<SetVelocity>(mov).ToList();
                string v = string.Join("; ", vel.Select(kv => kv.Key.Name + " z=" + F(kv.Value.z) + (kv.Value.vector != null && !kv.Value.vector.IsNone ? " vec=" + kv.Value.vector.Value : "") + " space=" + kv.Value.space + " everyFrame=" + kv.Value.everyFrame).ToArray());
                bool runOk = vel.Any(kv => kv.Key.Name == "Run" && kv.Value.z != null && !kv.Value.z.IsNone && kv.Value.z.Value > 0f);
                Put("N11", "Movement SetVelocity (brain's pedal): 'Run' pushes local z > 0", p, runOk ? St.OK : St.INFO, "states " + StateNames(mov) + " | " + v);
                string w1, w2;
                bool r1 = HandlesEvent(mov, "Animal_Run", out w1), r2 = HandlesEvent(mov, "Animal_Idle", out w2);
                Put("N12", "Movement handles Animal_Run / Animal_Idle (brain + Idle send them)", p, r1 && r2, "Animal_Run: " + w1 + " | Animal_Idle: " + w2);
                var ar = State(mov, "AttackRanged");
                var ap = ar != null && ar.Actions != null ? ar.Actions.OfType<AnimatorPlay>().FirstOrDefault() : null;
                string aim = ap != null && ap.stateName != null ? ap.stateName.Value : null;
                Put("N13", "Movement 'AttackRanged' AnimatorPlay stateName (brain AimPose)", p, ranged ? (aim != null ? St.OK : St.FAIL) : St.NA, aim ?? (ar == null ? "no AttackRanged state" : ActionNames(ar)));
                var anim = go.transform.Find("Anim") != null ? go.transform.Find("Anim").GetComponent<Animator>() : go.GetComponentInChildren<Animator>(true);
                if (anim != null && anim.runtimeAnimatorController != null)
                {
                    var names = new[] { "run", "idle", aim }.Where(n => n != null).ToArray();
                    var miss = names.Where(n => !anim.HasState(0, Animator.StringToHash(n))).ToArray();
                    Put("N14", "Animator layer 0 has the states NPCAI plays (run, idle = Idle walk; aim pose)", p, miss.Length == 0,
                        (miss.Length > 0 ? "MISSING " + string.Join(",", miss) + " | " : "") + "animator on " + anim.name + ", clips: " + string.Join(", ", anim.runtimeAnimatorController.animationClips.Select(c => c.name).Distinct().Take(30).ToArray()));
                }
                else Put("N14", "Animator layer 0 has the states NPCAI plays (run, idle = Idle walk; aim pose)", p, St.FAIL, "no Animator (or no controller)");
            }
            else { Put("N11", "Movement SetVelocity (brain's pedal): 'Run' pushes local z > 0", p, St.NA, "no Movement FSM"); }

            // Unstuck
            if (uns != null)
            {
                var spin = Actions<SendEvent>(uns).Where(kv => kv.Value.sendEvent != null && kv.Value.sendEvent.Name == "Animal_rotateRandom").ToList();
                var hop = Actions<AddForce>(uns).ToList();
                Put("N15", "Unstuck sends Animal_rotateRandom + AddForce hop (both cancelled by the brain)", p, spin.Count > 0 && hop.Count > 0, "spin in: " + States(spin) + " | AddForce in: " + States(hop) + " | states " + StateNames(uns));
            }

            // RangedAttackWait / Damage Ranged
            if (raw != null)
            {
                var rw = Actions<RandomWait>(raw).ToList();
                var act = Actions<SendEvent>(raw).Where(kv => kv.Value.sendEvent != null && kv.Value.sendEvent.Name == "Activate").ToList();
                string targets = string.Join("; ", act.Select(kv => kv.Key.Name + " -> " + DumpPrefab.Target(kv.Value.eventTarget, raw.Fsm)).ToArray());
                string where; HandlesEvent(att, "Activate", out where);
                Put("N16", "RangedAttackWait: RandomWait (Aim pause) + SendEvent Activate (Aim holds it); who handles Activate", p, rw.Count > 0 && act.Count > 0,
                    "RandomWait: " + string.Join("; ", rw.Select(kv => kv.Key.Name + " " + F(kv.Value.min) + ".." + F(kv.Value.max)).ToArray()) + " | Activate: " + targets + " | Attack handles Activate: " + where);
            }
            else Put("N16", "RangedAttackWait: RandomWait (Aim pause) + SendEvent Activate (Aim holds it); who handles Activate", p, ranged ? St.FAIL : St.NA, "no RangedAttackWait FSM");
            if (dr != null)
            {
                var hit = Actions<Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit>(dr).ToList();
                var dmg = Actions<SetFsmFloat>(dr).Where(kv => kv.Value.fsmName != null && kv.Value.fsmName.Value == "Bodypart" && kv.Value.variableName != null && kv.Value.variableName.Value == "Damage").ToList();
                Put("N17", "Damage Ranged: SensorGetDetectionRayHit (NPC shot noise) + SetFsmFloat Bodypart.Damage < 0 (hurt)", p, hit.Count > 0 && dmg.Count > 0 && dmg.All(kv => kv.Value.setValue != null && kv.Value.setValue.Value < 0f) ? St.OK : St.FAIL,
                    "RayHit in: " + States(hit) + " | Damage writes: " + string.Join("; ", dmg.Select(kv => kv.Key.Name + " " + F(kv.Value.setValue)).ToArray()) + " | states " + StateNames(dr));
            }
            else Put("N17", "Damage Ranged: SensorGetDetectionRayHit (NPC shot noise) + SetFsmFloat Bodypart.Damage < 0 (hurt)", p, St.NA, "melee");

            // Sound / relation
            if (snd != null)
            {
                var atk = State(snd, "attack");
                var arr = snd.FsmVariables.FindFsmArray("soundArray");
                Put("N18", "Sound FSM 'attack' state with AudioPlay (= the combat shout -> taunt); soundArray (player shout voice)", p, atk != null && Has<AudioPlay>(atk) ? St.OK : St.INFO,
                    "states " + StateNames(snd) + " | soundArray " + (arr == null ? "none" : arr.Length + " clips"));
            }
            else Put("N18", "Sound FSM 'attack' state with AudioPlay (= the combat shout -> taunt); soundArray (player shout voice)", p, St.NA, "no Sound FSM");
            if (pie != null) Put("N19", "PlayerIsEnemy states PlayerEnemy / PlayerFriendly (faction relation)", p, State(pie, "PlayerEnemy") != null && State(pie, "PlayerFriendly") != null, "states " + StateNames(pie) + " now " + pie.ActiveStateName);
            else Put("N19", "PlayerIsEnemy states PlayerEnemy / PlayerFriendly (faction relation)", p, St.NA, "none");

            // children / bones
            var sensors = go.transform.Find("Sensors");
            var behs = go.GetComponentsInChildren<Behaviour>(true).Where(b => b != null && (b.GetType().Name == "LOSSensor" || b.GetType().Name == "RangeSensor")).ToList();
            var range = behs.FirstOrDefault(b => b.GetType().Name == "RangeSensor");
            string[] tags = null;
            if (range != null) { var filt = Read(range, "SignalFilter") ?? Read(range, "signalFilter"); tags = Read(filt, "AllowedTags") as string[]; }
            Put("N20", "Sensors: LOSSensor + RangeSensor (switched off by Senses); RangeSensor SignalFilter.AllowedTags = hostile factions", p, behs.Count >= 2 && tags != null && tags.Length > 0,
                "Sensors child " + (sensors != null ? "yes" : "NO") + ", " + string.Join(", ", behs.Select(b => b.GetType().Name + "@" + b.name + (b.enabled ? "" : "(off)")).ToArray()) + " | tags " + (tags == null ? "UNREADABLE" : string.Join("/", tags)));
            var rayT = go.transform.Find("AttackRaycast_Ranged");
            var raySensor = rayT != null ? rayT.GetComponent<Micosmo.SensorToolkit.RaySensor>() : null;
            Put("N21", "AttackRaycast_Ranged RaySensor (burst ray; VanillaEvents stretches its Length)", p, ranged ? (raySensor != null ? St.OK : St.FAIL) : St.NA, raySensor != null ? "Length " + raySensor.Length : "none");
            Put("N22", "gun in hand: fire_effect (muzzle) under an active weapon -> NPCAI weapon class by name", p, ranged ? (gun ? St.OK : St.INFO) : St.NA,
                gun ? muzzle.parent.name + " -> " + Classify(muzzle.parent.name) : "no active fire_effect (crossbow / monster ranged / gun not drawn yet)");
            var head = FindDeep(go.transform, t => Last(t.name) == "Head");
            Put("N23", "head bone (Senses eyes / Brain kneel)", p, head != null ? St.OK : St.INFO, head != null ? head.name : "none (eyes from the collider top)");
            var legs = new[] { "Hips", "LeftUpLeg", "LeftLeg", "RightUpLeg", "RightLeg" }.Where(b => FindDeep(go.transform, t => Last(t.name) == b) == null).ToArray();
            Put("N24", "leg bones + CapsuleCollider for the kneel (CrouchChance)", p, !ranged ? St.NA : legs.Length == 0 && go.GetComponent<CapsuleCollider>() != null ? St.OK : St.INFO,
                (legs.Length > 0 ? "missing bones " + string.Join(",", legs) : "bones ok") + ", capsule " + (go.GetComponent<CapsuleCollider>() != null ? "yes" : "no"));
        }

        private static string Classify(string weapon)
        {
            string n = weapon.ToLowerInvariant();
            if (n.Contains("crossbow")) return "Crossbow";
            if (n.Contains("shotgun") || n.Contains("slamfire") || n.Contains("slamberg") || n.Contains("rochester")) return "Shotgun";
            if (n.Contains("scoped") || n.Contains("sniper") || n.Contains("redmark")) return "Sniper";
            if (n.Contains("smg") || n.Contains("borz")) return "Smg";
            if (n.Contains("pistol") || n.Contains("revolver") || n.Contains("folk_17")) return "Pistol";
            return "Rifle (default - check the name if this is not a rifle)";
        }

        // ---------- player weapons ----------
        private static void CheckWeapon(GameObject w, string p)
        {
            var att = Fsm(w, "Attack");
            var rays = Actions<Raycast>(att).ToList();
            var shot = rays.Where(kv => kv.Value.repeatInterval != null && kv.Value.repeatInterval.Value == 0).ToList();
            var hit = State(att, "hit");
            bool pellets = Actions<IntCompare>(att).Any(kv => kv.Key.Name == "hit" && kv.Value.integer1 != null && kv.Value.integer1.Name == "pellets");
            var dmg = Actions<SetFsmFloat>(att).Where(kv => kv.Value.fsmName != null && kv.Value.fsmName.Value == "Bodypart" && kv.Value.variableName != null && kv.Value.variableName.Value == "Damage").ToList();
            Put("W01", "player gun: Attack Raycast with repeatInterval 0 (= a shot -> gunshot noise), 'hit' state, pellets (shotgun), Bodypart.Damage write (hurt)", p,
                shot.Count > 0 && dmg.Count > 0,
                "shot rays in: " + States(shot) + " | 'hit' " + (hit != null ? "yes" : "NO") + " | pellets " + pellets + " | class by name " + Classify(w.name) + " | Damage in: " + string.Join("; ", dmg.Select(kv => kv.Key.Name + " " + F(kv.Value.setValue)).ToArray()));
        }

        // ---------- global ----------
        internal static void Global()
        {
            var player = GameObject.Find("Player");
            Put("G01", "Player object 'Player' + child 'head' (sight target)", "game", player != null && player.GetComponentsInChildren<Transform>(true).Any(t => t.name == "head"), player == null ? "no Player (menu?)" : "");
            if (player == null) return;
            var inCar = Fsm(player, "InCar");
            Put("G02", "Player InCar FSM: state 'InCar', GameObject var 'Car' (engine noise, car hides nobody)", "game",
                inCar != null && State(inCar, "InCar") != null && inCar.FsmVariables.FindFsmGameObject("Car") != null, inCar == null ? "no InCar FSM" : "states " + StateNames(inCar) + " now " + inCar.ActiveStateName);
            var cam = Camera.main;
            var grab = cam != null ? Fsm(cam.gameObject, "GrabItem") : null;
            Put("G03", "Camera.main GrabItem FSM: state 'Throw', GameObject var 'Item' (thrown item noise)", "game",
                grab != null && State(grab, "Throw") != null && grab.FsmVariables.FindFsmGameObject("Item") != null, cam == null ? "no Camera.main" : grab == null ? "no GrabItem on " + cam.name : "states " + StateNames(grab));
            Put("G04", "Camera.main child 'Flashlight' (full sight range when on)", "game", cam != null && cam.transform.Find("Flashlight") != null, cam != null ? "camera " + Plugin.PathOf(cam.transform) : "");
            var sl = GameObject.Find("SaveLoadGame"); var slf = sl != null ? Fsm(sl, "SaveLoadGame") : null;
            if (slf != null)
            {
                var parts = new List<string>(); bool ok = true;
                foreach (var n in new[] { "SaveGame", "LoadGame", "setSeed", "isPlay" })
                {
                    var s = State(slf, n);
                    if (s == null) { ok = false; parts.Add(n + " MISSING"); continue; }
                    bool lasts = n == "isPlay" || Has<Wait>(s) || s.Actions.Any(a => a != null && a.GetType().Name.Contains("Wait"));
                    if (!lasts) ok = false;
                    parts.Add(n + (lasts ? "" : " (no wait: a per-frame poll can miss it)"));
                }
                var sf = slf.FsmVariables.GetFsmString("SaveFile");
                Put("G05", "SaveLoadGame states SaveGame/LoadGame/setSeed/isPlay last >= 1 frame (Senses persistence polls them), var SaveFile", "game", ok && sf != null, string.Join(", ", parts.ToArray()) + " | SaveFile=" + (sf != null ? sf.Value : "none") + " | now " + slf.ActiveStateName);
            }
            else Put("G05", "SaveLoadGame states SaveGame/LoadGame/setSeed/isPlay last >= 1 frame (Senses persistence polls them), var SaveFile", "game", St.FAIL, "no SaveLoadGame object/FSM");
            var reg = GameObject.Find("NewGO_ArrayList");
            var storms = reg != null ? reg.GetComponents<PlayMakerArrayListProxy>().FirstOrDefault(x => x.referenceName == "ArrayList_Sandstorms") : null;
            Put("G06", "NewGO_ArrayList proxy ArrayList_Sandstorms (storm sight/hearing)", "game", storms != null, storms != null ? (storms.arrayList != null ? storms.arrayList.Count + " storm(s)" : "list null") : reg == null ? "no NewGO_ArrayList" : "lists: " + string.Join(", ", reg.GetComponents<PlayMakerArrayListProxy>().Select(x => x.referenceName).ToArray()));
            Put("G07", "EnviroSkyLite.instance.MainLight + lightSettings sun/moon intensity (sight by daylight)", "game", St.INFO, Enviro());
            Put("G08", "BepInEx plugins around NPCAI", "game", St.INFO, Plugins());
            CheckApocapatrol();
            CheckGunplay();
            Hooks.Check();
        }

        private static string Enviro()
        {
            try
            {
                Type core = null, lite = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { if (core == null) core = asm.GetType("EnviroCore"); if (lite == null) lite = asm.GetType("EnviroSkyLite"); if (core != null && lite != null) break; }
                if (lite == null) return "FAIL: type EnviroSkyLite not found (sight uses RenderSettings.sun)";
                var inst = lite.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                object o = inst != null ? inst.GetValue(null, null) : null;
                var sb = new System.Text.StringBuilder("EnviroSkyLite.instance " + (ReferenceEquals(o, null) ? "NULL" : (o as UnityEngine.Object) == null ? "destroyed" : "ok"));
                var sun = RenderSettings.sun;
                if (sun != null)
                {
                    float el = Vector3.Dot(-sun.transform.forward, Vector3.up);
                    if (_sunSeen == 0) { _sunMin = _sunMax = sun.intensity; _elMin = _elMax = el; }
                    _sunSeen++; _sunMin = Mathf.Min(_sunMin, sun.intensity); _sunMax = Mathf.Max(_sunMax, sun.intensity); _elMin = Mathf.Min(_elMin, el); _elMax = Mathf.Max(_elMax, el);
                }
                sb.Append(" | RenderSettings.sun ").Append(sun != null ? sun.name + " i=" + sun.intensity.ToString("0.00") + " elev=" + Vector3.Dot(-sun.transform.forward, Vector3.up).ToString("0.00") + " (seen i " + _sunMin.ToString("0.00") + ".." + _sunMax.ToString("0.00") + ", elev " + _elMin.ToString("0.00") + ".." + _elMax.ToString("0.00") + " over " + _sunSeen + " reads)" : "null");
                sb.Append(" | Azure time ").Append(AzureHour());
                var all = core != null ? Resources.FindObjectsOfTypeAll(core) : new UnityEngine.Object[0];
                sb.Append(" | EnviroCore components in scene: ");
                int n = 0;
                foreach (var c in all)
                {
                    var b = c as Behaviour;
                    if (b == null || !b.gameObject.scene.IsValid()) continue;
                    n++;
                    var light = Read(b, "MainLight") as Light;
                    var ls = Read(b, "lightSettings");
                    sb.Append(b.GetType().Name).Append("@").Append(Plugin.PathOf(b.transform)).Append(b.isActiveAndEnabled ? "" : "(off)")
                      .Append(" MainLight=").Append(light != null ? light.name + " i=" + light.intensity.ToString("0.00") + (light == RenderSettings.sun ? " (=RenderSettings.sun)" : "") : "null")
                      .Append(" isNight=").Append(Read(b, "isNight")).Append(" sunCurve=").Append(Curve(Read(ls, "directLightSunIntensity"))).Append(" moonCurve=").Append(Curve(Read(ls, "directLightMoonIntensity"))).Append("; ");
                }
                if (n == 0) sb.Append("none");
                return sb.ToString();
            }
            catch (Exception e) { return "error " + e.Message; }
        }

        private static float _sunMin, _sunMax, _elMin, _elMax; private static int _sunSeen;
        // the game's sky is Azure[Sky] (AzureTimeController.GetTimeline = hour of day), not Enviro
        private static string AzureHour()
        {
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { t = asm.GetType("AzureTimeController"); if (t != null) break; }
                if (t == null) return "(no AzureTimeController type)";
                var objs = Resources.FindObjectsOfTypeAll(t);
                foreach (var o in objs)
                {
                    var b = o as Behaviour; if (b == null || !b.gameObject.scene.IsValid()) continue;
                    var m = t.GetMethod("GetTimeline", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    return m != null ? Convert.ToSingle(m.Invoke(b, null)).ToString("0.00") + " h" : "(no GetTimeline)";
                }
                return "(none in scene)";
            }
            catch (Exception e) { return "(" + e.Message + ")"; }
        }

        private static string Curve(object v)
        {
            var c = v as AnimationCurve;
            if (c == null) return v == null ? "null" : v.GetType().Name + " " + v;
            if (c.keys.Length == 0) return "empty";
            float lo = c.keys[0].value, hi = lo; foreach (var k in c.keys) { if (k.value < lo) lo = k.value; if (k.value > hi) hi = k.value; }
            return "curve " + c.keys.Length + " keys " + lo.ToString("0.00") + ".." + hi.ToString("0.00");
        }

        private static string Plugins()
        {
            var ids = new[] { "com.denis.apocalypter.npcai", "com.denis.apocalypter.gunplay", "com.denis.apocalypter.apocapatrol", "com.denis.apocalypter.apocaraider", "com.denis.apocalypter.womenofwasteland" };
            var parts = new List<string>();
            foreach (var id in ids)
            {
                BepInEx.PluginInfo pi;
                parts.Add(id.Substring(id.LastIndexOf('.') + 1) + " " + (Chainloader.PluginInfos.TryGetValue(id, out pi) ? pi.Metadata.Version.ToString() : "-"));
            }
            if (Chainloader.PluginInfos.ContainsKey("com.denis.apocalypter.apocaraider") && Chainloader.PluginInfos.ContainsKey("com.denis.apocalypter.npcai"))
                parts.Add("WARNING: Apocaraider + NPCAI both loaded - NPC hooks run twice");
            return string.Join(", ", parts.ToArray());
        }

        private static void CheckApocapatrol()
        {
            if (!Chainloader.PluginInfos.ContainsKey("com.denis.apocalypter.apocapatrol")) { Put("G10", "Apocapatrol Patrol.BailOut(GameObject,GameObject,string,float) + Explode.Blast(GameObject) (crew handover, car blast noise)", "game", St.NA, "Apocapatrol not loaded"); return; }
            var patrol = AccessTools.TypeByName("Apocapatrol.Patrol"); var explode = AccessTools.TypeByName("Apocapatrol.Explode");
            var bail = patrol != null ? AccessTools.Method(patrol, "BailOut", new[] { typeof(GameObject), typeof(GameObject), typeof(string), typeof(float) }) : null;
            var blast = explode != null ? AccessTools.Method(explode, "Blast", new[] { typeof(GameObject) }) : null;
            Put("G10", "Apocapatrol Patrol.BailOut(GameObject,GameObject,string,float) + Explode.Blast(GameObject) (crew handover, car blast noise)", "game", bail != null && blast != null && bail.ReturnType == typeof(GameObject),
                "BailOut " + (bail != null ? "returns " + bail.ReturnType.Name : "MISSING") + ", Blast " + (blast != null ? "ok" : "MISSING"));
        }

        private static void CheckGunplay()
        {
            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue("com.denis.apocalypter.gunplay", out info) || info.Instance == null) { Put("G11", "Gunplay.Api contract 1 (EffectiveRange, TryGetWeaponKind, ProjectilesEnabled)", "game", St.NA, "Gunplay not loaded: NPCAI fallback ranges + vanilla shot observers"); return; }
            var api = info.Instance.GetType().Assembly.GetType("Gunplay.Api", false);
            var ver = api != null ? api.GetField("ContractVersion", BindingFlags.Public | BindingFlags.Static) : null;
            var r = api != null ? api.GetMethod("EffectiveRange", new[] { typeof(int) }) : null;
            var k = api != null ? api.GetMethod("TryGetWeaponKind", new[] { typeof(GameObject), typeof(int).MakeByRefType() }) : null;
            var pe = api != null ? api.GetProperty("ProjectilesEnabled", BindingFlags.Public | BindingFlags.Static) : null;
            string ranges = "";
            try { if (r != null) for (int i = 0; i <= 5; i++) ranges += (i > 0 ? "/" : "") + r.Invoke(null, new object[] { i }); } catch (Exception e) { ranges = e.Message; }
            Put("G11", "Gunplay.Api contract 1 (EffectiveRange, TryGetWeaponKind, ProjectilesEnabled)", "game", api != null && ver != null && Convert.ToInt32(ver.GetValue(null)) == 1 && r != null && k != null && pe != null,
                "api " + (api != null) + " ver " + (ver != null ? ver.GetValue(null) : "-") + " | ranges P/S/R/Sn/Sg/C " + ranges + " | projectiles " + (pe != null ? pe.GetValue(null, null) : "-"));
        }
    }
}
