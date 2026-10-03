using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // How NPC gunmen pace their shots with distance.
    //
    // Vanilla (asset dump 2026-10-01, every human shooter): FSM "RangedAttackWait" = state wait (RandomWait WaitMin..WaitMax, e.g. 3..5 s;
    // the shotgunners' WeaponType FSM meant to set them per gun but writes minWait twice, so their 4..6 defaults stay) -> state check
    // (SendEvent Activate -> the Attack FSM's global transition "ranged": line-of-sight check, then attack_ranged = shooting animation,
    // muzzle flash, EnableFSM "Damage Ranged" = the burst) -> back to wait. The NPC runs toward the target the whole time (Attack FSM
    // trigger state); nothing in vanilla looks at the distance.
    //
    // Here (two Harmony prefixes on the actions of "RangedAttackWait" only):
    // - SendEvent "Activate": the burst is skipped when the target is farther than the gun's reach ([Tracers] ranges) or farther than
    //   [NpcAim] EngagePercent of it, so the NPC keeps following instead of firing at nothing. Beyond EngagePercent but within reach
    //   the NPC holds for EngagePatience seconds, then fires anyway (it may be stuck, or hiding).
    // - RandomWait: while holding fire the next check comes after HoldRecheckMin..Max seconds (1-4); otherwise the vanilla pause grows by
    //   AimDelayPer5m for every 5 m the target is beyond AimBaseDistance ("takes longer to aim").
    // WeaponRanges.BeforeRayHit widens the vanilla aim jitter by SpreadPer5m % for every 5 m beyond AimBaseDistance (Aim.SpreadFactor).
    internal static class Aim
    {
        private sealed class State
        {
            public GameObject Owner;
            public float HoldSince = -1f;    // when the NPC first held fire inside reach (beyond EngagePercent)
            public bool Holding;             // last decision: hold fire -> short recheck
            public bool Turning;             // last decision: not facing the target yet -> very short recheck
            public float LastLog;
        }

        private sealed class WaitRefs { public FsmFloat Min, Max, MyMin, MyMax; }

        private static readonly Dictionary<int, State> _states = new Dictionary<int, State>();
        private static readonly Dictionary<RandomWait, WaitRefs> _waits = new Dictionary<RandomWait, WaitRefs>();

        public static void OnSceneLoaded() { _states.Clear(); _waits.Clear(); _targets.Clear(); }

        // drop entries of NPCs that no longer exist (the RandomWait action would keep its whole FSM alive)
        private static readonly List<RandomWait> _deadWaits = new List<RandomWait>();
        private static readonly List<int> _deadStates = new List<int>();
        internal static void Sweep()
        {
            _deadWaits.Clear();
            foreach (var kv in _waits) if (kv.Key.Fsm == null || kv.Key.Fsm.GameObject == null) _deadWaits.Add(kv.Key);
            foreach (var k in _deadWaits) _waits.Remove(k);
            if (_states.Count > 0)
            {
                _deadStates.Clear();
                foreach (var kv in _states) if (kv.Value.Owner == null) _deadStates.Add(kv.Key);
                foreach (var k in _deadStates) _states.Remove(k);
            }
            if (_targets.Count > 0)
            {
                _deadStates.Clear();
                foreach (var kv in _targets) if (kv.Value.Owner == null) _deadStates.Add(kv.Key);
                foreach (var k in _deadStates) _targets.Remove(k);
            }
        }

        // Steps of 5 m beyond the base distance (0 at or below it).
        internal static int Steps(float distance)
        {
            float beyond = distance - Plugin.AimBaseDistance.Value;
            return beyond <= 0f ? 0 : (int)(beyond / 5f);
        }

        internal static float SpreadFactor(float distance)
        {
            return 1f + Steps(distance) * Plugin.SpreadPer5m.Value / 100f;
        }

        // Harmony prefix on HutongGames.PlayMaker.Actions.SendEvent.OnEnter. false = the Activate is not sent (no burst this cycle).
        public static bool BeforeSendEvent(SendEvent __instance)
        {
            try
            {
                if (__instance.sendEvent == null || __instance.sendEvent.Name != "Activate") return true;   // cheapest test first: SendEvent is everywhere
                if (!Plugin.AimEnabled.Value) return true;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "RangedAttackWait") return true;
                var owner = fsm.GameObject;
                if (owner == null) return true;
                WeaponRanges.Kind kind;
                if (!WeaponRanges.GunKindOf(owner, out kind)) return true;    // a monster: vanilla
                GameObject target = TargetOf(owner);
                if (target == null) return true;

                var st = StateOf(owner);
                if (Senses.IsGhostTarget(owner))
                {
                    // the "target" is a ghost (a place the NPC goes to look at): nothing to shoot at
                    st.Holding = true; st.Turning = false; st.HoldSince = -1f;
                    __instance.Finish();
                    return false;
                }
                float d = Vector3.Distance(owner.transform.position, target.transform.position);
                float reach = WeaponRanges.RangeOf(kind);
                float engage = reach * Mathf.Clamp(Plugin.EngagePercent.Value, 1f, 100f) / 100f;
                bool hold;
                if (d > reach) { hold = true; st.HoldSince = -1f; }
                else if (d > engage)
                {
                    if (st.HoldSince < 0f) st.HoldSince = Time.time;
                    hold = Time.time - st.HoldSince < Plugin.EngagePatience.Value;
                }
                else { hold = false; st.HoldSince = -1f; }
                st.Holding = hold;
                st.Turning = false;
                if (!hold)
                {
                    // the brain turns the body at a limited rate: no burst until it actually faces the target
                    float err = Brain.FacingError(owner);
                    if (err > Mathf.Max(0f, Plugin.FacingTolerance.Value)) { hold = true; st.Turning = true; Brain.FaceTarget(owner, 0.6f); }   // the brain turns the body to the target now (not the steered heading)
                }
                if (hold && Time.time - st.LastLog > 5f)
                {
                    st.LastLog = Time.time;
                    Plugin.Verbose("Aim: " + owner.name + " holds fire at " + d.ToString("0") + " m (" + kind + " reach " + reach.ToString("0") + " m, engages at " + engage.ToString("0") + " m)");
                }
                if (hold) __instance.Finish();     // the action is done (nothing sent); the state's NextFrameEvent moves on
                return !hold;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Aim: " + e);
                return true;
            }
        }

        // Harmony prefix on HutongGames.PlayMaker.Actions.RandomWait.OnEnter: the pause between bursts.
        public static bool BeforeRandomWait(RandomWait __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "RangedAttackWait") return true;
                WaitRefs w;
                if (!_waits.TryGetValue(__instance, out w))
                {
                    w = new WaitRefs { Min = __instance.min, Max = __instance.max, MyMin = new FsmFloat(), MyMax = new FsmFloat() };
                    _waits[__instance] = w;
                }
                float baseMin = w.Min != null ? w.Min.Value : 3f, baseMax = w.Max != null ? w.Max.Value : 5f;
                if (!Plugin.AimEnabled.Value)
                { __instance.min = w.Min; __instance.max = w.Max; return true; }   // vanilla pause

                var owner = fsm.GameObject;
                float extra = 0f;
                State st = owner != null && _states.TryGetValue(owner.GetInstanceID(), out st) ? st : null;
                if (st != null && st.Turning)
                {
                    w.MyMin.Value = 0.1f; w.MyMax.Value = 0.2f;     // still turning toward the target: look again almost at once
                }
                else if (st != null && st.Holding)
                {
                    // too far: look again in HoldRecheckMin..Max s (the NPC is on its way; a reaction time, and no per-frame work)
                    float lo = Mathf.Max(0.1f, Plugin.HoldRecheckMin.Value), hi = Mathf.Max(lo, Plugin.HoldRecheckMax.Value);
                    w.MyMin.Value = lo; w.MyMax.Value = hi;
                }
                else
                {
                    var target = owner != null ? TargetOf(owner) : null;
                    if (target != null)
                        extra = Steps(Vector3.Distance(owner.transform.position, target.transform.position)) * Plugin.AimDelayPer5m.Value;
                    float scale = Mathf.Clamp(Plugin.AimTimeScale.Value, 1f, 300f) / 100f;    // [NpcAim] AimTimeScale: 50 % = twice as fast between bursts
                    w.MyMin.Value = (baseMin + extra) * scale; w.MyMax.Value = (baseMax + extra) * scale;
                }
                __instance.min = w.MyMin; __instance.max = w.MyMax;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Aim: " + e);
                return true;
            }
        }

        private static State StateOf(GameObject owner)
        {
            State st;
            int id = owner.GetInstanceID();
            if (!_states.TryGetValue(id, out st)) { st = new State { Owner = owner }; _states[id] = st; }
            return st;
        }

        // The NPC's current target: Detection FSM variable detectedObj (what the Attack / Damage Ranged FSMs read too). The variable
        // reference is cached per owner (looked up with GetComponents once, not per burst check).
        private sealed class TargetRef { public GameObject Owner; public FsmGameObject Var; }
        private static readonly Dictionary<int, TargetRef> _targets = new Dictionary<int, TargetRef>();
        internal static GameObject TargetOf(GameObject owner)
        {
            int id = owner.GetInstanceID();
            TargetRef tr;
            if (_targets.TryGetValue(id, out tr) && tr.Owner == owner) return tr.Var != null ? tr.Var.Value : null;
            FsmGameObject found = null;
            foreach (var f in owner.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.FsmName != "Detection" || f.Fsm == null || !f.Fsm.Initialized) continue;
                found = f.FsmVariables.FindFsmGameObject("detectedObj");
                break;
            }
            if (found == null) return null;              // not initialised yet: look again next time
            _targets[id] = new TargetRef { Owner = owner, Var = found };
            return found.Value;
        }
    }
}
