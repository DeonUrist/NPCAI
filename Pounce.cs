using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // (1.5.0) Small spiders pounce. A small spider chasing the player on foot, with open ground between them, starts running in zigzags when it
    // comes within 3-5 m (chosen at random per try), and from 2 m it leaps at the player: a ballistic jump aimed where the player will be, the
    // body reared back in the air with its belly towards the player. On contact it bites (the game's own bite damage, read from its Damage FSM),
    // drops off and runs away for 1.5-2.5 s, then chases as usual; it tries again 4-7 s later. A spider killed in the air leaves its carcass
    // flying on and tumbling (the carcass gets its velocity, a spin and the reared pose). A leap that misses lands and the chase goes on.
    // While zigzagging, in the air and running off, NPCAI drives the body itself (the brain is held, Brain.SetTravel) and the spider's vanilla
    // bite ray is shortened to nothing, so the game does not bite on top of the pounce.
    internal static class Pounce
    {
        private enum Phase { Ready, Zig, Air, Bite, Flee }

        private sealed class P
        {
            public Senses.Agent A; public Transform T, Rig; public Rigidbody Rb; public Animator An; public Micosmo.SensorToolkit.RaySensor Ray;
            public Quaternion RigLocal; public float RayLen, Drag, BodyH, Damage;
            public Phase Phase; public bool Held, Tilted;
            public float Trigger, LeapD, NextTry, NextCheck, NextProbe, Since, Until, ZigSwitch, FlightT, Pitch, PitchGoal;
            public int ZigSide; public Vector3 FleeDir, LeapH; public bool Carry;
        }

        private static readonly List<P> _list = new List<P>();
        private static string[] _prefabs; private static string _prefabsSrc;
        private static readonly Dictionary<int, PlayMakerFSM> _bodypart = new Dictionary<int, PlayMakerFSM>();
        private static GameObject _pcolOf; private static Collider _pcol;
        private static Vector3 _pLast, _pVel; private static float _pLastAt;
        private const int Obst = (1 << 0) | (1 << 8) | (1 << 9) | (1 << 11) | (1 << 14) | (1 << 16);   // Default, Car, Item, Door, Ground, SeeThrough
        private const int GroundMask = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 14) | (1 << 16);
        private static int _hashAttack = Animator.StringToHash("attack"), _hashBite = Animator.StringToHash("bite"), _hashRun = Animator.StringToHash("run");

        internal static int Airborne;      // spiders in the air right now: the CreateObject hook does nothing while there is none

        internal static void OnSceneLoaded() { _list.Clear(); _bodypart.Clear(); _pcolOf = null; _pcol = null; Airborne = 0; }

        private static bool Wanted(string prefab)
        {
            string cfg = Plugin.PouncePrefabs.Value ?? "";
            if (_prefabs == null || _prefabsSrc != cfg) { _prefabsSrc = cfg; _prefabs = cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries); for (int i = 0; i < _prefabs.Length; i++) _prefabs[i] = _prefabs[i].Trim(); }
            foreach (var p in _prefabs) if (p.Length > 0 && string.Equals(prefab, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Senses.Make: every registered NPC passes here once
        internal static void Register(Senses.Agent a)
        {
            if (a == null || a.Owner == null || !Wanted(Senses.PrefabOf(a.Owner))) return;
            var rb = a.Owner.GetComponent<Rigidbody>();
            var rig = a.T.Find("Rig");
            if (rb == null || rig == null) return;
            var p = new P { A = a, T = a.T, Rig = rig, Rb = rb, RigLocal = rig.localRotation, Damage = -7f };
            p.An = rig.GetComponent<Animator>() ?? a.Owner.GetComponentInChildren<Animator>(true);
            var ray = a.T.Find("AttackRaycast");
            if (ray != null) p.Ray = ray.GetComponent<Micosmo.SensorToolkit.RaySensor>();
            p.BodyH = a.Col != null ? Mathf.Clamp(a.Col.bounds.size.y, 0.05f, 1f) : 0.12f;
            // its bite: the Damage FSM's attack state, SetFsmFloat Bodypart.Damage = -N on the target
            try
            {
                foreach (var f in a.Owner.GetComponents<PlayMakerFSM>())
                {
                    if (f == null || f.FsmName != "Damage" || f.Fsm == null || f.Fsm.States == null) continue;
                    foreach (var st in f.Fsm.States)
                    {
                        if (st == null || st.Name != "attack" || st.Actions == null) continue;
                        foreach (var act in st.Actions) { var s = act as SetFsmFloat; if (s != null && s.setValue != null && !s.setValue.IsNone && s.setValue.Value < 0f) p.Damage = s.setValue.Value; }
                    }
                }
            }
            catch (Exception) { }
            p.NextTry = Time.time + 1f;
            _list.Add(p);
            if (Plugin.PounceLog.Value) Plugin.Log.LogInfo("Pounce: " + a.Owner.name + " can pounce (bite " + p.Damage.ToString("0") + ", body " + p.BodyH.ToString("0.00") + " m)");
        }

        internal static void Tick()
        {
            if (_list.Count == 0) { Airborne = 0; return; }
            float now = Time.time, dt = Time.deltaTime;
            bool on = Plugin.PounceEnabled.Value && Senses.On && Brain.On;
            var player = Senses.PlayerObj;
            if (player != null && dt > 0f)
            {
                // the player's velocity (aiming the leap), smoothed over ~0.1 s; a jump of > 30 m/s is a teleport
                Vector3 pp = player.transform.position;
                if (_pLastAt > 0f && now - _pLastAt < 0.5f) { Vector3 v = (pp - _pLast) / Mathf.Max(0.001f, now - _pLastAt); if (v.sqrMagnitude > 900f) v = Vector3.zero; _pVel = Vector3.Lerp(_pVel, v, Mathf.Clamp01(dt * 10f)); }
                _pLast = pp; _pLastAt = now;
            }
            int air = 0;
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                var p = _list[i];
                if (p.A == null || p.A.Owner == null || p.T == null || p.Rb == null) { _list.RemoveAt(i); continue; }
                bool ok = on && player != null && p.A.State == Senses.State.Combat && p.A.Target == player && !p.A.Blown && p.T.parent == null && !p.A.Travel;
                try
                {
                    switch (p.Phase)
                    {
                        case Phase.Ready: Ready(p, ok, player, now); break;
                        case Phase.Zig: Zig(p, ok, player, now); break;
                        case Phase.Air: Air(p, player, now); break;
                        case Phase.Bite: if (now >= p.Until) StartFlee(p, player, now); break;
                        case Phase.Flee: Flee(p, now); break;
                    }
                }
                catch (Exception e) { Plugin.Log.LogError("Pounce: " + e); End(p, now, 3f, "error"); }
                if (p.Phase == Phase.Air || p.Phase == Phase.Bite) air++;
            }
            Airborne = air;
        }

        private static float Run(P p) { float s = Brain.RunSpeed(p.A.Owner); return s > 0f ? s : 3f; }
        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        private static void Ready(P p, bool ok, GameObject player, float now)
        {
            if (!ok || now < p.NextTry || now < p.NextCheck) return;
            p.NextCheck = now + 0.2f;
            if (p.Trigger <= 0f)
            {
                // per try: where the zigzag starts (8-11 m) and where it leaps (5-6 m), the zigzag always at least a metre before the leap
                p.LeapD = UnityEngine.Random.Range(Mathf.Max(0.5f, Plugin.PounceLeapMin.Value), Mathf.Max(Plugin.PounceLeapMin.Value, Plugin.PounceLeapMax.Value));
                p.Trigger = Mathf.Max(p.LeapD + 1f, UnityEngine.Random.Range(Mathf.Max(1f, Plugin.PounceZigMin.Value), Mathf.Max(Plugin.PounceZigMin.Value, Plugin.PounceZigMax.Value)));
            }
            float d = Flat(player.transform.position - p.T.position).magnitude, leap = p.LeapD;
            if (d > p.Trigger) return;                                   // cheap: nothing else is checked out of the window
            if (Senses.PlayerInCar) return;
            var pc = PlayerCollider(player); if (pc == null) return;
            if (!Clear(p, player, d)) { p.NextCheck = now + 0.5f; return; }   // no room for it here: the usual chase
            p.Held = Brain.SetTravel(p.A.Owner, true);
            if (p.Ray != null) { p.RayLen = p.Ray.Length; p.Ray.Length = 0.001f; }   // no vanilla bite in the middle of it
            if (d <= leap) { Leap(p, player, pc, now); return; }
            p.Phase = Phase.Zig; p.Since = now; p.ZigSide = UnityEngine.Random.value < 0.5f ? -1 : 1; p.ZigSwitch = now + UnityEngine.Random.Range(0.15f, 0.3f);
            Log(p, "zigzags in from " + d.ToString("0.0") + " m");
        }

        private static void Zig(P p, bool ok, GameObject player, float now)
        {
            if (!p.Held) p.Held = Brain.SetTravel(p.A.Owner, true);
            if (!ok) { End(p, now, 2f, "lost the player"); return; }
            Vector3 to = Flat(player.transform.position - p.T.position);
            float d = to.magnitude;
            if (d > p.Trigger + 1.5f || now - p.Since > 6f) { End(p, now, 2f, "the player got away"); return; }
            if (d <= p.LeapD)
            {
                var pc = PlayerCollider(player);
                if (pc != null && LineClear(p, pc)) Leap(p, player, pc, now); else End(p, now, 2f, "no clear leap");
                return;
            }
            if (now >= p.ZigSwitch) { p.ZigSide = -p.ZigSide; p.ZigSwitch = now + UnityEngine.Random.Range(Plugin.PounceZigSeconds.Value * 0.7f, Plugin.PounceZigSeconds.Value * 1.3f); }
            Vector3 dir = d > 0.01f ? to / d : p.T.forward;
            Vector3 head = Quaternion.Euler(0f, p.ZigSide * Plugin.PounceZigAngle.Value, 0f) * dir;
            if (now >= p.NextProbe)
            {
                p.NextProbe = now + 0.1f;
                if (Blocked(p, head, 0.6f))
                {
                    p.ZigSide = -p.ZigSide; head = Quaternion.Euler(0f, p.ZigSide * Plugin.PounceZigAngle.Value, 0f) * dir;
                    p.ZigSwitch = now + Plugin.PounceZigSeconds.Value;
                    if (Blocked(p, head, 0.6f)) { End(p, now, 2f, "blocked"); return; }
                }
            }
            Drive(p, head, Run(p) * Mathf.Max(0.5f, Plugin.PounceZigSpeed.Value));
        }

        private static void Drive(P p, Vector3 head, float speed)
        {
            Vector3 v = head * speed; v.y = p.Rb.velocity.y; p.Rb.velocity = v;
            if (head.sqrMagnitude > 0.0001f) p.T.rotation = Quaternion.RotateTowards(p.T.rotation, Quaternion.LookRotation(head, Vector3.up), 900f * Time.deltaTime);
        }

        private static void Leap(P p, GameObject player, Collider pc, float now)
        {
            Vector3 start = p.T.position;
            Vector3 aim = pc.bounds.center - Vector3.up * (pc.bounds.extents.y * 0.15f);       // the chest / belly of the player
            float t = Mathf.Clamp(Flat(aim - start).magnitude / Mathf.Max(1f, Plugin.PounceLeapSpeed.Value), 0.22f, 1f);
            Vector3 pv = Flat(_pVel); if (pv.sqrMagnitude > 49f) pv = pv.normalized * 7f;
            aim += pv * t;
            Vector3 v = (aim - start) / t - 0.5f * Physics.gravity * t;
            p.Drag = p.Rb.drag; p.Rb.drag = 0f;
            p.Rb.velocity = v;
            p.LeapH = Flat(v); p.Carry = true;          // the flight speed is held until it touches something: no FSM / drag cuts the jump short
            Vector3 f = Flat(aim - start); if (f.sqrMagnitude > 0.0001f) p.T.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);
            p.Phase = Phase.Air; p.Since = now; p.FlightT = t; p.PitchGoal = Mathf.Clamp(Plugin.PouncePitch.Value, 0f, 110f);
            Play(p, _hashAttack, _hashAttack);
            Log(p, "leaps (" + Flat(aim - start).magnitude.ToString("0.0") + " m, " + t.ToString("0.00") + " s)");
        }

        private static void Air(P p, GameObject player, float now)
        {
            float el = now - p.Since;
            var pc = player != null ? PlayerCollider(player) : null;
            if (pc != null)
            {
                Vector3 c = p.T.position + p.T.up * (p.BodyH * 0.5f);
                float reach = 0.3f + p.BodyH * 0.5f;
                if ((pc.ClosestPoint(c) - c).sqrMagnitude <= reach * reach) { Bite(p, player, now); return; }
                Vector3 f = Flat(pc.bounds.center - p.T.position);
                if (f.sqrMagnitude > 0.0001f) p.T.rotation = Quaternion.RotateTowards(p.T.rotation, Quaternion.LookRotation(f.normalized, Vector3.up), 360f * Time.deltaTime);
            }
            // it flies on past a dodging player until it comes down: the ground under it, or it stopped against something
            if (p.Carry)
            {
                Vector3 cur = p.Rb.velocity;
                if (el > 0.1f && Flat(cur).sqrMagnitude < p.LeapH.sqrMagnitude * 0.09f) p.Carry = false;     // hit a wall / a rock: physics takes over
                else { cur.x = p.LeapH.x; cur.z = p.LeapH.z; p.Rb.velocity = cur; }
            }
            bool landed = el > 0.12f && p.Rb.velocity.y <= 0.05f && Physics.Raycast(p.T.position + Vector3.up * 0.1f, Vector3.down, 0.18f, GroundMask, QueryTriggerInteraction.Ignore);
            if (landed || el > 4f) End(p, now, UnityEngine.Random.Range(2f, 3f), "missed, landed " + el.ToString("0.00") + " s after the leap");
        }

        private static void Bite(P p, GameObject player, float now)
        {
            var target = p.A.DetectedVar != null && p.A.DetectedVar.Value != null ? p.A.DetectedVar.Value : player;
            var bp = BodypartOf(player) ?? BodypartOf(target);      // the player's body: its DamageEffectSound has the hurt sound and the red flash
            if (bp != null)
            {
                var dv = bp.FsmVariables.GetFsmFloat("Damage");
                if (dv != null)
                {
                    // as the game's own bite (Damage FSM: SetFsmFloat Bodypart.Damage, then SendEvent Damage to the whole GameObject): every FSM on
                    // the hit object gets "Damage" - Bodypart takes the health, DamageEffectSound plays the hurt sound, the red flash and the head punch
                    dv.Value = p.Damage;
                    foreach (var f in bp.gameObject.GetComponents<PlayMakerFSM>()) if (f != null && f.enabled) f.SendEvent("Damage");
                }
            }
            Play(p, _hashBite, _hashAttack);
            Vector3 back = Flat(p.T.position - player.transform.position); back = back.sqrMagnitude > 0.0001f ? back.normalized : -p.T.forward;
            p.Rb.velocity = back * 1.5f + Vector3.down * 1f;          // lets go and drops off
            p.Phase = Phase.Bite; p.Until = now + 0.25f; p.PitchGoal = 0f;
            Log(p, "bites the player (" + p.Damage.ToString("0") + (bp == null ? ", no Bodypart found!" : "") + ")");
        }

        private static void StartFlee(P p, GameObject player, float now)
        {
            if (p.Rb.drag != p.Drag) p.Rb.drag = p.Drag;
            Vector3 away = player != null ? Flat(p.T.position - player.transform.position) : -p.T.forward;
            away = away.sqrMagnitude > 0.0001f ? away.normalized : -p.T.forward;
            p.FleeDir = Quaternion.Euler(0f, UnityEngine.Random.Range(-35f, 35f), 0f) * away;
            p.Phase = Phase.Flee; p.Until = now + UnityEngine.Random.Range(Mathf.Max(0.2f, Plugin.PounceFleeMin.Value), Mathf.Max(Plugin.PounceFleeMin.Value, Plugin.PounceFleeMax.Value));
            Play(p, _hashRun, _hashRun);
        }

        private static void Flee(P p, float now)
        {
            if (now >= p.Until) { End(p, now, UnityEngine.Random.Range(Plugin.PounceCooldownMin.Value, Mathf.Max(Plugin.PounceCooldownMin.Value, Plugin.PounceCooldownMax.Value)), "ran off, chases again"); return; }
            if (now >= p.NextProbe)
            {
                p.NextProbe = now + 0.1f;
                if (Blocked(p, p.FleeDir, 0.6f))
                {
                    Vector3 l = Quaternion.Euler(0f, -70f, 0f) * p.FleeDir, r = Quaternion.Euler(0f, 70f, 0f) * p.FleeDir;
                    if (!Blocked(p, l, 0.6f)) p.FleeDir = l; else if (!Blocked(p, r, 0.6f)) p.FleeDir = r; else p.FleeDir = -p.FleeDir;
                }
            }
            Drive(p, p.FleeDir, Run(p) * Mathf.Max(0.5f, Plugin.PounceZigSpeed.Value));
        }

        // back to the brain: the vanilla bite ray, the drag, the run animation and the chase
        private static void End(P p, float now, float cooldown, string why)
        {
            if (p.Ray != null && p.RayLen > 0f) { p.Ray.Length = p.RayLen; p.RayLen = 0f; }
            if (p.Phase == Phase.Air || p.Phase == Phase.Bite) { if (p.Rb.drag != p.Drag) p.Rb.drag = p.Drag; }
            p.PitchGoal = 0f;
            p.Phase = Phase.Ready; p.Trigger = 0f;
            p.NextTry = now + Mathf.Max(0f, cooldown);
            Brain.SetTravel(p.A.Owner, false); p.Held = false;
            if (p.A.Owner != null && p.A.State == Senses.State.Combat) { var m = Movement(p); if (m != null) m.SendEvent("Animal_Run"); }
            Log(p, why);
        }

        private static PlayMakerFSM Movement(P p)
        {
            foreach (var f in p.A.Owner.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Movement") return f;
            return null;
        }

        private static void Play(P p, int want, int fallback)
        {
            if (p.An == null || !p.An.isActiveAndEnabled) return;
            if (p.An.HasState(0, want)) p.An.Play(want, 0, 0f); else if (p.An.HasState(0, fallback)) p.An.Play(fallback, 0, 0f);
        }

        // ---------- room ----------
        // open ground straight to the player and a body length to either side (the zigzag), no drop, no wall, no big height difference
        private static bool Clear(P p, GameObject player, float d)
        {
            Vector3 from = p.T.position, to = player.transform.position;
            if (Mathf.Abs(to.y - from.y) > 2f) return false;
            Vector3 dir = Flat(to - from); if (dir.sqrMagnitude < 0.0001f) return false; dir.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, dir) * 0.7f;
            float h = Mathf.Clamp(p.BodyH * 0.7f, 0.08f, 0.5f), len = Mathf.Max(0.1f, d - 0.4f);
            Vector3 o = from + Vector3.up * h;
            if (RayBlocked(p, o, dir, len, player) || RayBlocked(p, o + side, dir, len, player) || RayBlocked(p, o - side, dir, len, player)) return false;
            for (int k = 1; k <= 2; k++)
            {
                float f = k / 3f;
                Vector3 m = Vector3.Lerp(from, to, f);
                RaycastHit g;
                if (!Physics.Raycast(m + Vector3.up * 1.5f, Vector3.down, out g, 3f, GroundMask, QueryTriggerInteraction.Ignore)) return false;   // a hole / a drop
                if (Mathf.Abs(g.point.y - m.y) > 1f) return false;
            }
            return true;
        }

        private static bool RayBlocked(P p, Vector3 o, Vector3 dir, float len, GameObject player)
        {
            RaycastHit h;
            if (!Physics.Raycast(o, dir, out h, len, Obst, QueryTriggerInteraction.Ignore)) return false;
            var r = h.collider.transform.root;
            if (r == p.T || (player != null && r == player.transform.root)) return false;
            if (h.collider is TerrainCollider && h.normal.y >= 0.6f) return false;      // the ground rising gently
            return true;
        }

        private static bool Blocked(P p, Vector3 dir, float len)
        {
            float h = Mathf.Clamp(p.BodyH * 0.7f, 0.08f, 0.5f);
            return RayBlocked(p, p.T.position + Vector3.up * h, dir, len, Senses.PlayerObj);
        }

        private static bool LineClear(P p, Collider pc)
        {
            Vector3 o = p.T.position + Vector3.up * Mathf.Clamp(p.BodyH * 0.7f, 0.08f, 0.5f);
            Vector3 d = pc.bounds.center - o; float len = d.magnitude;
            return len < 0.05f || !RayBlocked(p, o, d / len, len, pc.gameObject);
        }

        private static Collider PlayerCollider(GameObject player)
        {
            if (_pcolOf == player && _pcol != null) return _pcol;
            _pcolOf = player; _pcol = null;
            foreach (var c in player.GetComponents<Collider>()) if (c != null && !c.isTrigger) { _pcol = c; if (c is CapsuleCollider || c is CharacterController) break; }
            if (_pcol == null) foreach (var c in player.GetComponentsInChildren<Collider>()) if (c != null && !c.isTrigger && (c is CapsuleCollider || c is CharacterController)) { _pcol = c; break; }
            return _pcol;
        }

        private static PlayMakerFSM BodypartOf(GameObject go)
        {
            if (go == null) return null;
            int id = go.GetInstanceID(); PlayMakerFSM f;
            if (_bodypart.TryGetValue(id, out f) && f != null) return f;
            f = null;
            foreach (var x in go.GetComponents<PlayMakerFSM>()) if (x != null && x.FsmName == "Bodypart") { f = x; break; }
            if (f == null) foreach (var x in go.GetComponentsInChildren<PlayMakerFSM>(true)) if (x != null && x.FsmName == "Bodypart") { f = x; break; }
            if (f != null) { if (_bodypart.Count > 64) _bodypart.Clear(); _bodypart[id] = f; }
            return f;
        }

        // ---------- the pose ----------
        // runs in LateUpdate, after the Animator: in the air the body rears back (nose up, about the feet) - the belly towards the player
        internal static void LateTick()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _list.Count; i++)
            {
                var p = _list[i];
                if (p.Rig == null) continue;
                if (p.Pitch == p.PitchGoal && !p.Tilted) continue;
                float rate = p.PitchGoal > p.Pitch ? 600f : 300f;
                p.Pitch = Mathf.MoveTowards(p.Pitch, p.PitchGoal, rate * dt);
                p.Rig.localRotation = Quaternion.AngleAxis(-p.Pitch, Vector3.right) * p.RigLocal;
                p.Tilted = p.Pitch > 0.01f;
                if (!p.Tilted) p.Rig.localRotation = p.RigLocal;
            }
        }

        // ---------- killed in the air ----------
        // CreateObject.OnEnter postfix: the Health FSM's carcass of a spider that is in the air flies on, reared back and tumbling
        public static void AfterCreateObject(CreateObject __instance)
        {
            if (Airborne == 0) return;
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Health") return;
                var owner = fsm.GameObject;
                var made = __instance.storeObject != null ? __instance.storeObject.Value : null;
                if (owner == null || made == null) return;
                foreach (var p in _list)
                {
                    if (p.A == null || p.A.Owner != owner || (p.Phase != Phase.Air && p.Phase != Phase.Bite)) continue;
                    Vector3 vel = p.Rb.velocity;
                    made.transform.SetPositionAndRotation(p.T.position, p.T.rotation * Quaternion.AngleAxis(-p.Pitch, Vector3.right));
                    var rb = made.GetComponent<Rigidbody>();
                    if (rb == null) { rb = made.AddComponent<Rigidbody>(); rb.mass = Mathf.Max(0.05f, p.Rb.mass); UnityEngine.Object.Destroy(rb, 6f); }
                    if (rb.isKinematic) break;
                    rb.velocity = vel;
                    rb.angularVelocity = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(6f, 12f);
                    if (p.Ray != null && p.RayLen > 0f) { p.Ray.Length = p.RayLen; p.RayLen = 0f; }
                    Log(p, "killed in the air, the carcass tumbles on (" + vel.magnitude.ToString("0.0") + " m/s)");
                    break;
                }
            }
            catch (Exception e) { Plugin.Warn("Pounce: carcass: " + e.Message); }
        }

        private static void Log(P p, string s) { if (Plugin.PounceLog.Value) Plugin.Log.LogInfo("Pounce: " + (p.A != null && p.A.Owner != null ? p.A.Owner.name : "?") + " " + s); }
    }
}
