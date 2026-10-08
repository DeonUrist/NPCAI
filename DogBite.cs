using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // (1.8.0) Dogs bite properly. The game's dogs (Wild / Yard Hound, Grimhound, Nightwalker, Alpha Nightwalker) have only idle and run clips -
    // no bite - and their Movement FSM runs them at full speed the whole fight while the Attack FSM turns them slowly (SmoothLookAt 6): the
    // bite is the run ray brushing you, so they circle around you. Now, within reach of their target, they stop dead, snap round to face it
    // (1080 degrees/s) and every real bite (the game's own Damage FSM hit, unchanged) is shown as a lunge: the head draws back and snaps
    // forward-down with a short jolt of the body, played on the head / neck bones after the Animator. Out of reach again: they run as before.
    internal static class DogBite
    {
        private sealed class D
        {
            public Senses.Agent A; public Transform T, Rig; public Transform[] Bones; public Vector3 RigBase;
            public float Reach, BiteT = -1f; public bool Close, Applied;
        }

        private static readonly List<D> _list = new List<D>();
        private static readonly Dictionary<int, D> _byId = new Dictionary<int, D>();
        private static string[] _prefabs; private static string _src;

        internal static void OnSceneLoaded() { DogAnimations.OnSceneLoaded(); _list.Clear(); _byId.Clear(); }

        private static bool Wanted(string prefab)
        {
            string cfg = Plugin.DogPrefabs.Value ?? "";
            if (_prefabs == null || _src != cfg) { _src = cfg; _prefabs = cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries); for (int i = 0; i < _prefabs.Length; i++) _prefabs[i] = _prefabs[i].Trim(); }
            foreach (var p in _prefabs) if (p.Length > 0 && string.Equals(prefab, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Senses.Make: every registered NPC passes here once
        internal static void Register(Senses.Agent a)
        {
            if (a == null || a.Owner == null || !Wanted(Senses.PrefabOf(a.Owner))) return;
            DogAnimations.Register(a);
            var d = new D { A = a, T = a.T, Reach = 1f };
            var ray = a.T.Find("AttackRaycast");
            var rs = ray != null ? ray.GetComponent<Micosmo.SensorToolkit.RaySensor>() : null;
            if (rs != null && rs.Length > 0.1f) d.Reach = rs.Length;
            d.Rig = a.T.Find("Rig");
            if (d.Rig != null) d.RigBase = d.Rig.localPosition;
            var rootAnim = a.Owner.GetComponent<Animator>();
            Transform boneRoot = d.Rig != null ? d.Rig : a.T;
            if (rootAnim != null) d.Rig = null;          // the Animator sits on the root (Grimhound): it owns the Rig's position - no body jolt
            // the neck and head bones: neck0 / neck1 / head0 (hounds, nightwalkers), else any bone named like Neck / Head (Grimhound)
            var bones = new List<Transform>();
            foreach (var t in boneRoot.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.StartsWith("ikt_") || n.EndsWith("_end") || n == "ik") continue;
                if (n == "neck0" || n == "neck1" || n == "head0" || ((n.StartsWith("neck") || n.StartsWith("head")) && bones.Count < 3)) { if (!bones.Contains(t)) bones.Add(t); }
            }
            d.Bones = bones.ToArray();
            _list.Add(d); _byId[a.Owner.GetInstanceID()] = d;
            if (Plugin.DogLog.Value) Plugin.Log.LogInfo("DogBite: " + a.Owner.name + " reach " + d.Reach.ToString("0.0") + " m, " + d.Bones.Length + " head/neck bone(s)");
        }

        // per frame, after the brain: in reach -> stand and face the target fast
        internal static void Tick()
        {
            DogAnimations.Tick();
            if (_list.Count == 0) return;
            float dt = Time.deltaTime;
            bool on = Plugin.DogBiteEnabled.Value && Senses.On;
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                var d = _list[i];
                if (d.A != null && DogAnimations.Owns(d.A.Owner)) { d.Close = false; continue; }
                if (d.A == null || d.A.Owner == null || d.T == null) { if (d.A != null && d.A.Owner != null) _byId.Remove(d.A.Owner.GetInstanceID()); _list.RemoveAt(i); continue; }
                var tgt = on && d.A.State == Senses.State.Combat && !d.A.Travel && d.T.parent == null ? d.A.Target : null;
                if (tgt == null) { if (d.Close) { d.Close = false; Brain.Halt(d.A.Owner, false); } continue; }
                Vector3 to = tgt.transform.position - d.T.position; to.y = 0f;
                // (1.9.1) the gap to the target's body, not to its pivot: a dog held at pivot distance reach + 0.5 m stood short of biting range
                var tc = tgt.GetComponent<Collider>();
                Vector3 tp = tc != null ? tc.ClosestPoint(d.T.position + Vector3.up * 0.25f) : tgt.transform.position;
                Vector3 gap = tp - d.T.position; gap.y = 0f;
                float dist = gap.magnitude;
                float near = d.Reach + 0.1f, far = d.Reach + 0.8f;
                if (!d.Close && dist <= near) { d.Close = true; Brain.Halt(d.A.Owner, true); }
                else if (d.Close && dist > far) { d.Close = false; Brain.Halt(d.A.Owner, false); }
                if (d.Close && to.sqrMagnitude > 0.0025f)
                    d.T.rotation = Quaternion.RotateTowards(d.T.rotation, Quaternion.LookRotation(to, Vector3.up), 1080f * dt);
            }
        }

        // a real bite (the Damage FSM sets the target's Bodypart.Damage): the lunge
        public static void AfterSetFsmFloat(SetFsmFloat __instance)
        {
            if (_list.Count == 0) return;
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Damage" || fsm.GameObject == null) return;
                D d;
                if (!DogAnimations.Owns(fsm.GameObject) && _byId.TryGetValue(fsm.GameObject.GetInstanceID(), out d) && Plugin.DogBiteEnabled.Value) d.BiteT = 0f;
            }
            catch (Exception) { }
        }

        // LateUpdate, after the Animator: the lunge on the head / neck bones (and a short jolt of the body)
        internal static void LateTick()
        {
            DogAnimations.LateTick();
            if (_list.Count == 0) return;
            float dt = Time.deltaTime;
            for (int i = 0; i < _list.Count; i++)
            {
                var d = _list[i];
                if (d.A != null && DogAnimations.Owns(d.A.Owner)) { d.BiteT = -1f; continue; }
                if (d.T == null) continue;
                if (d.BiteT < 0f) { if (d.Applied && d.Rig != null) { d.Rig.localPosition = d.RigBase; d.Applied = false; } continue; }
                d.BiteT += dt;
                float t = d.BiteT, len = 0.42f;
                if (t >= len) { d.BiteT = -1f; continue; }
                // draw back (up) 0-0.12 s, snap forward-down 0.12-0.2 s, ease back to 0.42 s
                float ang = t < 0.12f ? Mathf.Lerp(0f, -18f, t / 0.12f) : t < 0.2f ? Mathf.Lerp(-18f, 34f, (t - 0.12f) / 0.08f) : Mathf.Lerp(34f, 0f, (t - 0.2f) / (len - 0.2f));
                float push = t < 0.12f ? Mathf.Lerp(0f, -0.06f, t / 0.12f) : t < 0.2f ? Mathf.Lerp(-0.06f, 0.18f, (t - 0.12f) / 0.08f) : Mathf.Lerp(0.18f, 0f, (t - 0.2f) / (len - 0.2f));
                Vector3 axis = d.T.right;
                if (d.Bones != null)
                    for (int b = 0; b < d.Bones.Length; b++) if (d.Bones[b] != null) d.Bones[b].rotation = Quaternion.AngleAxis(ang * (b == d.Bones.Length - 1 ? 0.4f : 0.3f), axis) * d.Bones[b].rotation;
                if (d.Rig != null) { d.Rig.localPosition = d.RigBase + d.T.InverseTransformVector(d.T.forward * push); d.Applied = true; }
            }
        }
    }
}
