using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // (1.3.0) Small scorpions burrow. A scorpion that is not aware of anything sinks into the sand (its body goes down, only the top - the eyes -
    // stays above the surface) and, every BurrowMin..BurrowMax seconds, comes up or goes down again, only ever on sand. Spawned on sand it burrows
    // at once. Anything that makes it aware (it sees you, hears something, is hit) brings it up and it does not burrow again until it is calm.
    // The sand puff and the sound are the sand worm's own (the Burrower_Effect prefab), played by hand at a small scale and a low volume.
    // Burrowed it sees all around (Senses: cone 360). The body is only moved visually (the Rig child), colliders and FSMs are untouched.
    //
    // (1.4.0) Big scorpions hunt underground. They burrow like the small ones, but lying in wait they engage anything within HuntRange (15 m).
    // Then, and also when a target it is fighting gets farther than HuntRange, the scorpion dives (the whole body, tail included, goes under),
    // "travels" for as long as running there would take (distance / its own run speed), comes up at a spot behind the enemy and attacks.
    // The spot must be open ground (terrain, not an object) with room for the body; one other spot is tried; if that fails too it comes back up
    // where it went down 3-5 s later and chases as usual. While travelling the body is kinematic, its colliders are off, its senses and brain
    // are paused and no other NPC can see it.
    internal static class Burrow
    {
        private enum Phase { Out, Burrowing, Under, Rising, Diving, Travel }

        private sealed class B
        {
            public Senses.Agent A; public Transform T, Rig; public Vector3 RigBase;
            public Phase Phase; public float Amount, Depth, NextToggle; public bool WasAware, Applied, Fast;
            // (1.4.0) hunters
            public bool Hunter, Hunted, WasKinematic, Fallback, Held; public Rigidbody Rb; public Collider[] Cols; public bool[] ColOn;
            public float Deep, Height, Radius, OffY, TravelEnd, NextHunt, NextHuntCheck, RiseRate; public int Extensions;
            public Vector3 Origin, Dest;
        }

        private static readonly List<B> _list = new List<B>();
        private static GameObject _effect; private static AudioClip _clip; private static float _nextFind;
        private static string[] _prefabs, _hunters; private static string _prefabsSrc, _huntersSrc;
        private static readonly Dictionary<TerrainData, bool[]> _sandLayers = new Dictionary<TerrainData, bool[]>();
        private static readonly Collider[] _overlap = new Collider[16];
        private const int GroundMask = (1 << 0) | (1 << 8) | (1 << 9) | (1 << 11) | (1 << 14) | (1 << 16);           // what can be stood on
        private const int RoomMask = (1 << 0) | (1 << 6) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | (1 << 14) | (1 << 16);   // + Player, Actor

        // (1.4.0) NPCs burrowed or travelling right now: Senses.Visible looks a target up in the agent table only while there is any
        internal static int Hidden;

        internal static void OnSceneLoaded() { _list.Clear(); _effect = null; _clip = null; _nextFind = 0f; _sandLayers.Clear(); Hidden = 0; }

        private static bool In(string prefab, string cfg, ref string[] cache, ref string src)
        {
            cfg = cfg ?? "";
            if (cache == null || src != cfg) { src = cfg; cache = cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries); for (int i = 0; i < cache.Length; i++) cache[i] = cache[i].Trim(); }
            foreach (var p in cache) if (p.Length > 0 && string.Equals(prefab, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Senses.Make: every registered NPC passes here once
        internal static void Register(Senses.Agent a)
        {
            if (!Plugin.BurrowEnabled.Value || a == null || a.Owner == null) return;
            string prefab = Senses.PrefabOf(a.Owner);
            bool hunter = In(prefab, Plugin.HuntPrefabs.Value, ref _hunters, ref _huntersSrc);
            if (!hunter && !In(prefab, Plugin.BurrowPrefabs.Value, ref _prefabs, ref _prefabsSrc)) return;
            Transform rig = a.T.Find("Rig");
            if (rig == null) { var an = a.Owner.GetComponentInChildren<Animator>(true); if (an != null && an.transform != a.T) rig = an.transform; }
            if (rig == null) return;
            Bounds bb = new Bounds(); bool any = false;
            foreach (var r in rig.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                if (!any) { bb = r.bounds; any = true; } else bb.Encapsulate(r.bounds);
            }
            float h = any ? bb.size.y : 0.15f;
            var n = new B { A = a, T = a.T, Rig = rig, RigBase = rig.localPosition, Depth = Mathf.Clamp(h * Mathf.Clamp(Plugin.BurrowDepth.Value, 0.2f, 1f), 0.04f, 0.5f) };
            n.Height = Mathf.Max(0.1f, h);
            n.Deep = Mathf.Max(n.Depth, h + 0.2f);       // a hunter's dive: the whole body and the tail under, with a margin for slopes
            n.Radius = Mathf.Clamp(any ? Mathf.Max(bb.extents.x, bb.extents.z) * 0.6f : 0.3f, 0.15f, 1.2f);
            if (hunter)
            {
                n.Rb = a.Owner.GetComponent<Rigidbody>();
                if (n.Rb == null) hunter = false;      // nothing to hold still while it travels: it only burrows
                else
                {
                    if (n.Rb.isKinematic) n.Rb.isKinematic = false;     // (1.7.0) saved while travelling underground: never left kinematic after a load
                    var solid = new List<Collider>();
                    foreach (var c in a.Owner.GetComponentsInChildren<Collider>(true)) if (c != null && !c.isTrigger) solid.Add(c);
                    n.Cols = solid.ToArray(); n.ColOn = new bool[n.Cols.Length];
                }
            }
            n.Hunter = hunter;
            n.NextToggle = 0f;      // spawned: burrows at once, if it stands on sand and nothing alerts it
            _list.Add(n);
            if (Plugin.BurrowLog.Value) Plugin.Log.LogInfo("Burrow: " + a.Owner.name + (hunter ? " hunts underground" : " can burrow") + " (depth " + n.Depth.ToString("0.00") + " m, body " + h.ToString("0.00") + " m, radius " + n.Radius.ToString("0.00") + " m)");
        }

        internal static void Tick()
        {
            if (_list.Count == 0) { Hidden = 0; return; }
            float now = Time.time, dt = Time.deltaTime;
            bool on = Plugin.BurrowEnabled.Value && Senses.On;
            int hidden = 0;
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                var b = _list[i];
                if (b.A == null || b.A.Owner == null || b.T == null) { _list.RemoveAt(i); continue; }
                bool aware = !on || b.A.State != Senses.State.Idle || b.A.Blown;
                bool hunting = on && b.Hunter && b.A.State == Senses.State.Combat && b.A.Target != null && !b.A.Blown && b.T.parent == null;
                if (aware && !b.WasAware) { b.NextToggle = now + Calm(); }
                if (!aware && b.WasAware && b.Phase == Phase.Out) b.NextToggle = now + Calm();      // calm again: the cycle restarts
                b.WasAware = aware;
                switch (b.Phase)
                {
                    case Phase.Out:
                        if (hunting)
                        {
                            // fighting above ground: the target got away farther than HuntRange -> after it underground (2 checks a second, cheap first)
                            if (now >= b.NextHunt && now >= b.NextHuntCheck) { b.NextHuntCheck = now + 0.5f; if (Far(b) && OnSand(b.T.position)) StartHunt(b, now); }
                        }
                        else if (!aware && now >= b.NextToggle) { if (OnSand(b.T.position)) Go(b, Phase.Burrowing, false); else b.NextToggle = now + Calm(); }
                        break;
                    case Phase.Burrowing:
                        if (hunting) { if (Within(b, HuntMax)) StartHunt(b, now); else Go(b, Phase.Rising, true); break; }
                        if (aware) { Go(b, Phase.Rising, true); break; }
                        b.Amount = Mathf.MoveTowards(b.Amount, 1f, dt / (Mathf.Max(0.1f, Plugin.BurrowSeconds.Value) * Slow(b)));
                        if (b.Amount >= 1f) { b.Phase = Phase.Under; b.A.Burrowed = true; b.NextToggle = now + Calm(); }
                        break;
                    case Phase.Under:
                        b.A.Burrowed = true;
                        if (hunting) { if (Within(b, HuntMax)) StartHunt(b, now); else Go(b, Phase.Rising, true); }      // shot from afar: comes up and fights as usual
                        else if (aware) Go(b, Phase.Rising, true);
                        else if (now >= b.NextToggle) Go(b, Phase.Rising, false);
                        break;
                    case Phase.Rising:
                        b.Amount = Mathf.MoveTowards(b.Amount, 0f, dt * (b.RiseRate > 0f ? b.RiseRate : 1f / (Mathf.Max(0.1f, b.Fast ? Plugin.RiseSeconds.Value * 0.5f : Plugin.RiseSeconds.Value) * Slow(b))));
                        if (b.Amount <= 0f) { b.Phase = Phase.Out; b.NextToggle = now + Calm(); if (b.Hunted) EndHunt(b, now); }
                        break;
                    case Phase.Diving:
                        if (!b.Held) b.Held = Brain.SetTravel(b.A.Owner, true);     // not known to the brain when the dive began: hold it now
                        if (!on) { Emerge(b, b.Origin); break; }
                        b.Amount = Mathf.MoveTowards(b.Amount, 2f, dt * 2f / Mathf.Max(0.1f, Plugin.HuntDiveSeconds.Value));
                        if (b.Amount >= 2f) BeginTravel(b, now);
                        break;
                    case Phase.Travel:
                        if (!on) { Emerge(b, b.Origin); break; }
                        if (now >= b.TravelEnd) Arrive(b, now);
                        break;
                }
                if (b.A.Burrowed || b.A.Travel) hidden++;
            }
            Hidden = hidden;
        }

        private static float Slow(B b) { return b.Hunter ? Mathf.Clamp(Plugin.HuntBurrowFactor.Value, 0.25f, 10f) : 1f; }
        private static float Calm() { float lo = Mathf.Max(1f, Plugin.BurrowMin.Value), hi = Mathf.Max(lo, Plugin.BurrowMax.Value); return UnityEngine.Random.Range(lo, hi); }
        private static float HuntRange { get { return Mathf.Max(2f, Plugin.HuntRange.Value); } }
        private static float HuntMax { get { return Mathf.Max(HuntRange, Plugin.HuntMaxDistance.Value); } }

        private static bool Within(B b, float r)
        {
            Vector3 d = b.A.Target.transform.position - b.T.position; d.y = 0f;
            return d.sqrMagnitude <= r * r;
        }

        private static bool Far(B b)
        {
            Vector3 d = b.A.Target.transform.position - b.T.position; d.y = 0f;
            float s = d.sqrMagnitude, r = HuntRange, m = HuntMax;
            return s > r * r && s <= m * m;
        }

        private static void Go(B b, Phase p, bool fast)
        {
            b.Phase = p; b.Fast = fast;
            if (p == Phase.Burrowing)
            {
                b.RiseRate = 0f;
                // lying in wait: a small one lets you come within a distance chosen now (AmbushMin..AmbushMax m) before it comes up and attacks;
                // a hunter engages anything within HuntRange and goes for it underground
                b.A.AmbushDist = b.Hunter ? HuntRange : UnityEngine.Random.Range(Mathf.Max(0.5f, Plugin.BurrowAmbushMin.Value), Mathf.Max(Plugin.BurrowAmbushMin.Value, Plugin.BurrowAmbushMax.Value));
            }
            if (p == Phase.Rising) { b.A.AmbushDist = 0f; b.A.Burrowed = false; }      // eyes were at the surface all along; the 360 degree look ends when it comes out
            Fx(b, b.T.position);
            if (Plugin.BurrowLog.Value) Plugin.Log.LogInfo("Burrow: " + b.A.Owner.name + (p == Phase.Burrowing ? " burrows (" + (b.Hunter ? "hunts within " : "ambush at ") + b.A.AmbushDist.ToString("0.0") + " m)" : fast ? " is alerted, comes up" : " comes up"));
        }

        // ---------- (1.4.0) the underground hunt ----------
        private static void StartHunt(B b, float now)
        {
            b.Hunted = true; b.Extensions = 0; b.Fallback = false; b.RiseRate = 0f;
            b.Origin = b.T.position;
            RaycastHit g;
            b.OffY = Ground(b.T.position, out g) ? Mathf.Clamp(b.T.position.y - g.point.y, -0.1f, 0.6f) : 0f;
            b.A.AmbushDist = HuntRange;          // no ghosts handed to it while it is under (Senses.Assign)
            Vector3 spot;
            if (Pick(b, out spot))
            {
                b.Dest = spot;
                Vector3 d = spot - b.Origin; d.y = 0f;
                b.TravelEnd = d.magnitude / RunSpeed(b);          // the travel time (made absolute when the dive ends): as long as running there would take
            }
            else
            {
                b.Fallback = true; b.Dest = b.Origin;
                b.TravelEnd = UnityEngine.Random.Range(Mathf.Max(0f, Plugin.HuntFallbackMin.Value), Mathf.Max(Plugin.HuntFallbackMin.Value, Plugin.HuntFallbackMax.Value));
            }
            b.Held = Brain.SetTravel(b.A.Owner, true);       // stands still from now on: no pedal, no thinking, no stuck judgement
            Fx(b, b.T.position);
            b.Phase = Phase.Diving;
            if (Plugin.BurrowLog.Value) Plugin.Log.LogInfo("Burrow: " + b.A.Owner.name + " dives after " + b.A.Target.name + (b.Fallback ? " (no room behind it: comes back up here in " + b.TravelEnd.ToString("0.0") + " s)" : ", travels " + b.TravelEnd.ToString("0.0") + " s at " + RunSpeed(b).ToString("0.0") + " m/s"));
        }

        private static void BeginTravel(B b, float now)
        {
            b.Phase = Phase.Travel;
            b.TravelEnd = now + Mathf.Clamp(b.TravelEnd, 0.2f, 30f);
            b.WasKinematic = b.Rb.isKinematic;
            if (!b.Rb.isKinematic) { b.Rb.velocity = Vector3.zero; b.Rb.angularVelocity = Vector3.zero; }
            b.Rb.isKinematic = true;
            for (int i = 0; i < b.Cols.Length; i++) { var c = b.Cols[i]; b.ColOn[i] = c != null && c.enabled; if (b.ColOn[i]) c.enabled = false; }
            b.A.Travel = true; b.A.Burrowed = true;
        }

        private static void Arrive(B b, float now)
        {
            if (!b.Fallback && b.A.Target != null)
            {
                // the enemy moved meanwhile: behind where it is now; a longer way is travelled at the same speed (twice at the most)
                Vector3 spot;
                if (Pick(b, out spot))
                {
                    Vector3 d = spot - b.Dest; d.y = 0f;
                    float extra = d.magnitude, lim = HuntMax * 1.5f;
                    if (extra > 2f && b.Extensions < 2 && (spot - b.Origin).sqrMagnitude <= lim * lim)
                    {
                        b.Dest = spot; b.Extensions++;
                        b.TravelEnd = now + Mathf.Min(extra / RunSpeed(b), 15f);
                        return;
                    }
                    if (extra <= 2f) b.Dest = spot;                          // about where it was going
                    else if (!Room(b, b.Dest)) b.Dest = b.Origin;            // too far off now: the planned spot, if there is still room
                }
                else if (!Room(b, b.Dest)) b.Dest = b.Origin;      // the planned spot is taken now (a car parked on it): back where it went down
            }
            Emerge(b, b.Dest);
        }

        private static void Emerge(B b, Vector3 at)
        {
            if (b.A.Travel)
            {
                RaycastHit g;
                Vector3 pos = at;
                if (Ground(at, out g)) pos.y = g.point.y + b.OffY;
                Quaternion rot = b.T.rotation;
                if (b.A.Target != null)
                {
                    Vector3 f = b.A.Target.transform.position - pos; f.y = 0f;
                    if (f.sqrMagnitude > 0.01f) rot = Quaternion.LookRotation(f.normalized, Vector3.up);
                }
                b.T.SetPositionAndRotation(pos, rot);
                if (b.Rb != null) { b.Rb.position = pos; b.Rb.rotation = rot; }
                for (int i = 0; i < b.Cols.Length; i++) if (b.ColOn[i] && b.Cols[i] != null) b.Cols[i].enabled = true;
                b.A.Travel = false;
            }
            b.RiseRate = Mathf.Max(0.05f, b.Amount) / Mathf.Max(0.1f, Plugin.HuntRiseSeconds.Value);      // bursts out
            Go(b, Phase.Rising, true);
            if (Plugin.BurrowLog.Value) Plugin.Log.LogInfo("Burrow: " + b.A.Owner.name + " bursts out " + (b.Fallback || at == b.Origin ? "where it went down" : (Vector3.Distance(at, b.Origin).ToString("0.0") + " m away")));
        }

        private static void EndHunt(B b, float now)
        {
            b.Hunted = false; b.RiseRate = 0f;
            if (b.Rb != null && b.Rb.isKinematic && !b.WasKinematic) { b.Rb.isKinematic = false; b.Rb.velocity = Vector3.zero; }
            Brain.SetTravel(b.A.Owner, false); b.Held = false;
            b.NextHunt = now + Mathf.Max(0f, Plugin.HuntCooldown.Value);
        }

        // underground it goes TravelSpeedFactor (2) times faster than it runs
        private static float RunSpeed(B b) { float s = Brain.RunSpeed(b.A.Owner); return Mathf.Max(0.5f, s > 0f ? s : Plugin.HuntSpeed.Value) * Mathf.Clamp(Plugin.HuntTravelFactor.Value, 0.25f, 10f); }

        // behind the target (behind where it looks: the camera for the player), else one other spot back and to a side
        private static bool Pick(B b, out Vector3 spot)
        {
            spot = Vector3.zero;
            var target = b.A.Target; if (target == null) return false;
            Vector3 tp = target.transform.position;
            Vector3 fwd = target.transform.forward;
            if (target == Senses.PlayerObj) { var cam = Camera.main; if (cam != null) fwd = cam.transform.forward; }
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) { fwd = tp - b.T.position; fwd.y = 0f; }
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            float dist = Mathf.Max(b.Radius + 0.5f, Plugin.HuntBehind.Value);
            spot = tp - fwd * dist;
            if (Room(b, spot)) return true;
            float ang = (UnityEngine.Random.value < 0.5f ? -1f : 1f) * UnityEngine.Random.Range(50f, 80f);
            spot = tp - (Quaternion.Euler(0f, ang, 0f) * fwd) * dist;
            return Room(b, spot);
        }

        // open ground (the terrain, not an object), not too steep, and room for the body
        private static bool Room(B b, Vector3 p)
        {
            RaycastHit g;
            if (!Ground(p, out g) || !(g.collider is TerrainCollider) || g.normal.y < 0.75f) return false;
            float r = b.Radius;
            Vector3 p0 = g.point + Vector3.up * (r + 0.15f), p1 = g.point + Vector3.up * Mathf.Max(r + 0.15f, b.Height + 0.1f);
            int n = Physics.OverlapCapsuleNonAlloc(p0, p1, r, _overlap, RoomMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = _overlap[i];
                if (c == null || c is TerrainCollider || c.transform.root == b.T) continue;
                return false;
            }
            return true;
        }

        private static bool Ground(Vector3 p, out RaycastHit h)
        {
            return Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out h, 6f, GroundMask, QueryTriggerInteraction.Ignore);
        }

        // runs in LateUpdate, after the Animator: the body (the Rig child) is sunk along the NPC's own up axis.
        // Amount 0..1 = the burrow (the eyes stay up), 1..2 = a hunter's dive (the tail goes under too)
        internal static void LateTick()
        {
            for (int i = 0; i < _list.Count; i++)
            {
                var b = _list[i];
                if (b.Rig == null || b.T == null) continue;
                if (b.Amount <= 0.0005f && !b.Applied) continue;
                float a1 = Mathf.Clamp01(b.Amount), a2 = Mathf.Clamp01(b.Amount - 1f);
                float sink = b.Depth * a1 * a1 * (3f - 2f * a1) + (b.Deep - b.Depth) * a2 * a2 * (3f - 2f * a2);
                b.Rig.localPosition = b.RigBase + b.T.InverseTransformVector(-b.T.up * sink);
                b.Applied = b.Amount > 0.0005f;
                if (!b.Applied) b.Rig.localPosition = b.RigBase;
            }
        }

        // ---------- the sand worm's puff and sound, small and quiet ----------
        private static void FindEffect()
        {
            if (_effect != null || Time.time < _nextFind) return;
            _nextFind = Time.time + 60f;       // (1.4.0) a scan of every FSM in memory: at most once a minute while the worm's effect is not loaded (was 10 s)
            try
            {
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                {
                    if (f == null || f.gameObject == null || f.FsmName != "DestroySelf") continue;
                    var go = f.gameObject;
                    if (go.scene.IsValid() || go.transform.parent != null || go.name != "Burrower_Effect") continue;
                    _effect = go;
                    if (f.Fsm != null && f.Fsm.States != null)
                        foreach (var st in f.Fsm.States)
                        {
                            if (st == null || st.Actions == null) continue;
                            foreach (var act in st.Actions) { var sc = act as SetAudioClip; if (sc != null && sc.audioClip != null && sc.audioClip.Value as AudioClip != null) _clip = sc.audioClip.Value as AudioClip; }
                        }
                    if (Plugin.BurrowLog.Value) Plugin.Log.LogInfo("Burrow: sand effect found (" + (_clip != null ? _clip.name : "no clip") + ")");
                    break;
                }
            }
            catch (Exception e) { Plugin.Warn("Burrow: effect lookup: " + e.Message); }
        }

        private static void Fx(B b, Vector3 at)
        {
            var cam = Camera.main;
            if (cam == null || (cam.transform.position - at).sqrMagnitude > Plugin.BurrowFxRange.Value * Plugin.BurrowFxRange.Value) return;
            FindEffect();
            if (_effect == null) return;
            try
            {
                float s = Mathf.Clamp(b.Hunter ? Plugin.HuntFxScale.Value : Plugin.BurrowFxScale.Value, 0.05f, 1f);
                var fx = UnityEngine.Object.Instantiate(_effect, at, Quaternion.identity);
                foreach (var f in fx.GetComponentsInChildren<PlayMakerFSM>(true)) f.enabled = false;     // played by hand, at our scale and volume
                fx.SetActive(true);
                fx.transform.localScale = Vector3.one * s;
                foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var m = ps.main; m.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    ps.Play(true);
                }
                var src = fx.GetComponent<AudioSource>() ?? fx.GetComponentInChildren<AudioSource>(true);
                float life = 3f;
                if (src != null)
                {
                    if (_clip != null) src.clip = _clip;
                    src.volume = Mathf.Clamp01(b.Hunter ? Plugin.HuntFxVolume.Value : Plugin.BurrowFxVolume.Value);
                    src.pitch = Mathf.Clamp(b.Hunter ? Plugin.HuntFxPitch.Value : Plugin.BurrowFxPitch.Value, 0.5f, 3f);
                    src.spatialBlend = 1f; src.rolloffMode = AudioRolloffMode.Linear; src.minDistance = 1f; src.maxDistance = Mathf.Max(5f, Plugin.BurrowFxRange.Value * (b.Hunter ? 1f : 0.5f));
                    src.loop = false; src.dopplerLevel = 0f;
                    if (src.clip != null) { src.Play(); life = Mathf.Max(life, src.clip.length / src.pitch + 0.5f); }
                }
                UnityEngine.Object.Destroy(fx, life);
            }
            catch (Exception e) { Plugin.Warn("Burrow: effect: " + e.Message); }
        }

        // ---------- sand ----------
        // The ground under p is "sand" when it is the terrain itself and not any other object (the default). With [Burrow] UseTextureLayers on,
        // only terrain whose texture layers at that spot are mostly layers named like a sand keyword
        // (layer name or its diffuse texture's name contains one of [Burrow] SandKeywords). A terrain with no such layer at all (the keyword
        // never matches, or no readable layers) is judged by [Burrow] TerrainIsSand; meshes (rock, floors, roads, cars) are not sand.
        internal static bool OnSand(Vector3 p)
        {
            try
            {
                RaycastHit h;
                if (!Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out h, 3f, (1 << 0) | (1 << 14) | (1 << 8) | (1 << 11) | (1 << 16), QueryTriggerInteraction.Ignore)) return false;
                var tc = h.collider as TerrainCollider;
                if (tc == null) return false;                       // standing on any other object (rock, floor, road mesh, car, wreck): not sand
                if (!Plugin.BurrowUseLayers.Value) return true;     // the open ground itself (terrain) is sand
                var terrain = h.collider.GetComponent<Terrain>();
                if (terrain == null || terrain.terrainData == null) return Plugin.BurrowTerrainIsSand.Value;
                var d = terrain.terrainData;
                bool[] sand;
                if (!_sandLayers.TryGetValue(d, out sand)) { sand = Classify(d); if (_sandLayers.Count > 64) _sandLayers.Clear(); _sandLayers[d] = sand; }
                if (sand == null || d.alphamapLayers == 0) return Plugin.BurrowTerrainIsSand.Value;
                Vector3 lp = h.point - terrain.transform.position;
                int ax = Mathf.Clamp((int)(lp.x / d.size.x * d.alphamapWidth), 0, d.alphamapWidth - 1);
                int az = Mathf.Clamp((int)(lp.z / d.size.z * d.alphamapHeight), 0, d.alphamapHeight - 1);
                float[,,] w = d.GetAlphamaps(ax, az, 1, 1);
                float total = 0f, s = 0f;
                for (int i = 0; i < w.GetLength(2) && i < sand.Length; i++) { total += w[0, 0, i]; if (sand[i]) s += w[0, 0, i]; }
                return total > 0.0001f && s / total >= 0.5f;
            }
            catch (Exception e) { Plugin.Verbose("Burrow: sand check: " + e.Message); return Plugin.BurrowTerrainIsSand.Value; }
        }

        // per layer: a sand keyword in its name / texture name; null when no layer matches at all (then TerrainIsSand decides)
        private static bool[] Classify(TerrainData d)
        {
            var layers = d.terrainLayers;
            if (layers == null || layers.Length == 0) return null;
            var keys = (Plugin.BurrowSandKeywords.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var res = new bool[layers.Length]; bool any = false;
            for (int i = 0; i < layers.Length; i++)
            {
                var l = layers[i]; if (l == null) continue;
                string n = (l.name ?? "") + "|" + (l.diffuseTexture != null ? l.diffuseTexture.name : "");
                foreach (var k in keys) { string kk = k.Trim(); if (kk.Length > 0 && n.IndexOf(kk, StringComparison.OrdinalIgnoreCase) >= 0) { res[i] = true; any = true; break; } }
            }
            if (Plugin.BurrowLog.Value) Plugin.Log.LogInfo("Burrow: terrain layers " + string.Join(", ", Array.ConvertAll(layers, l => l == null ? "-" : l.name)) + (any ? "" : " (no sand-named layer: TerrainIsSand rules)"));
            return any ? res : null;
        }
    }
}
