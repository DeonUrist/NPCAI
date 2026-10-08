using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // Clips contain Generic transform curves for the original creature skeletons.
    // Native FSMs retain sensing, damage amounts and their post-hit cooldown.
    internal static class DogAnimations
    {
        private enum Pose { Locomotion, LieDown, Rest, GetUp, Bite, RunningBite, JumpAttack }
        private sealed class Dog
        {
            public Senses.Agent Agent; public Animator Animator; public Rigidbody Body;
            public Dictionary<string, AnimationClip> Clips;
            public Transform[] Bones; public Vector3[] NativePosition, FadePosition;
            public Quaternion[] NativeRotation, FadeRotation;
            public Behaviour[] Ik; public bool[] IkEnabled;
            public AnimatorCullingMode Culling;
            public Pose Pose; public string Playing; public float Time, Fade, NextBite;
            public float Reach = 1f, Drag, JumpSince; public bool Applied, Held, Prepared, Jumping, Committing, HitSent;
            public bool Boss, WasQuiet, Dashing;
            public float NextIdleChange, AttackRate = 1f, RecoveryUntil, DashSpeed;
            public Vector3 DashDirection;
            public PlayMakerFSM DamageComponent;
            public readonly Dictionary<Wait, float> OriginalWaits = new Dictionary<Wait, float>();
            public GameObject BiteTarget; public Fsm DamageFsm;
        }
        private static readonly Dictionary<int, Dog> Dogs = new Dictionary<int, Dog>();
        private static readonly Dictionary<string, Dictionary<string, AnimationClip>> Clips = new Dictionary<string, Dictionary<string, AnimationClip>>(StringComparer.OrdinalIgnoreCase);
        private static ConfigEntry<bool> enabled, resting, jumping;
        private static ConfigEntry<float> idleMin, idleMax;
        private static AssetBundle bundle;
        private const int Solid = (1 << 0) | (1 << 8) | (1 << 9) | (1 << 11) | (1 << 14) | (1 << 16);
        private const int Floor = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 14);
        private static bool On { get { return enabled != null && enabled.Value && Plugin.DogBiteEnabled != null && Plugin.DogBiteEnabled.Value && Senses.On; } }

        internal static void Initialize(ConfigFile config)
        {
            enabled = config.Bind("Behaviour", "DogAnimations", true, "Retargeted animations on hounds, Grimhound and nightwalkers. Requires NPCAI/Models/npcai_dogs.bundle; otherwise the existing procedural bite remains active.");
            resting = config.Bind("Behaviour", "DogsLieDown", true, "Non-boss quadrupeds alternate standing and lying idle, spending a random 10-25 seconds in each pose. Threats interrupt rest through get-up.");
            idleMin = config.Bind("Behaviour", "DogIdlePoseMinSeconds", 10f, new ConfigDescription("Idle quadrupeds stand or lie at least this long before switching, s.", new AcceptableValueRange<float>(2f, 300f)));
            idleMax = config.Bind("Behaviour", "DogIdlePoseMaxSeconds", 25f, new ConfigDescription("... and at the most, s.", new AcceptableValueRange<float>(2f, 300f)));
            jumping = config.Bind("Behaviour", "DogJumpAttack", false, "Optional short, collision-checked jumping bite. Native damage is applied only if the target remains within reach at contact.");
            string path = Path.Combine(Path.Combine(Plugin.Dir, "Models"), "npcai_dogs.bundle");
            if (!File.Exists(path)) { Plugin.Log.LogInfo("Dog animations: bundle absent; using procedural bite."); return; }
            try
            {
                bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) throw new InvalidDataException("Unity could not load the canine animation bundle");
                foreach (var clip in bundle.LoadAllAssets<AnimationClip>())
                {
                    int split = clip.name.LastIndexOf('_'); if (split <= 0) continue;
                    string prefab = clip.name.Substring(0, split), action = clip.name.Substring(split + 1);
                    Dictionary<string, AnimationClip> list;
                    if (!Clips.TryGetValue(prefab, out list)) { list = new Dictionary<string, AnimationClip>(); Clips[prefab] = list; }
                    list[action] = clip;
                }
                var harmony = new Harmony(Plugin.GUID + ".dogs");
                harmony.Patch(AccessTools.Method(typeof(Fsm), "ProcessEvent"), prefix: new HarmonyMethod(typeof(DogAnimations), nameof(BeforeDamageEvent)));
                harmony.Patch(AccessTools.Method(typeof(SetVelocity), "DoSetVelocity"), prefix: new HarmonyMethod(typeof(DogAnimations), nameof(BeforeSetVelocity)));
                harmony.Patch(AccessTools.Method(typeof(Wait), "OnEnter"), prefix: new HarmonyMethod(typeof(DogAnimations), nameof(BeforeDamageWait)));
                Plugin.Log.LogInfo("Dog animations: loaded " + Clips.Values.Sum(c => c.Count) + " clips for " + Clips.Count + " creature rigs.");
            }
            catch (Exception e) { Clips.Clear(); Plugin.Log.LogError("Dog animations unavailable; procedural fallback: " + e); }
        }

        internal static void Register(Senses.Agent agent)
        {
            if (agent == null || agent.Owner == null) return;
            int id = agent.Owner.GetInstanceID(); if (Dogs.ContainsKey(id)) return;
            Dictionary<string, AnimationClip> clips;
            string prefab = Senses.PrefabOf(agent.Owner).Replace(' ', '_');
            if (!Clips.TryGetValue(prefab, out clips) || new[] { "Idle", "Run", "LieDown", "LieIdle", "GetUp", "Bite" }.Any(n => !clips.ContainsKey(n))) return;
            var animator = agent.Owner.GetComponentInChildren<Animator>(true);
            var body = agent.Owner.GetComponent<Rigidbody>(); if (animator == null || body == null) return;
            var bones = agent.Owner.GetComponentsInChildren<SkinnedMeshRenderer>(true).SelectMany(r => r.bones).Where(t => t != null && t != animator.transform).Distinct().ToArray();
            if (bones.Length < 10) return;
            var dog = new Dog { Agent = agent, Animator = animator, Body = body, Clips = clips, Bones = bones,
                NativePosition = new Vector3[bones.Length], NativeRotation = new Quaternion[bones.Length], FadePosition = new Vector3[bones.Length], FadeRotation = new Quaternion[bones.Length], Culling = animator.cullingMode,
                Boss = prefab == "Alpha_Nightwalker" || agent.Owner.GetComponentsInChildren<PlayMakerFSM>(true).Any(f => f != null && f.FsmName == "BossUI") };
            dog.Ik = agent.Owner.GetComponentsInChildren<Behaviour>(true).Where(b => b != null && b.GetType().Name == "FastIKFabric").ToArray();
            dog.IkEnabled = dog.Ik.Select(b => b.enabled).ToArray();
            var rayObject = agent.T.Find("AttackRaycast");
            var ray = rayObject != null ? rayObject.GetComponent<Micosmo.SensorToolkit.RaySensor>() : null;
            if (ray != null && ray.Length > .1f) dog.Reach = ray.Length;
            Dogs[id] = dog;
            if (Plugin.DogLog.Value) Plugin.Log.LogInfo("Dog animations: " + agent.Owner.name + " mapped " + bones.Length + " bones; " + clips.Count + " clips.");
        }

        private static float IdleSpan() { float lo = idleMin != null ? idleMin.Value : 10f, hi = idleMax != null ? Mathf.Max(lo, idleMax.Value) : 25f; return UnityEngine.Random.Range(lo, hi); }

        private static bool Eligible(Dog d)
        {
            var a = d.Agent;
            return On && a != null && a.Owner != null && a.Owner.activeInHierarchy && a.T != null && a.T.parent == null && !a.Blown && (!a.Travel || d.Jumping || d.Dashing)
                && d.Animator != null && d.Animator.enabled && d.Body != null && !d.Body.isKinematic && WithinRange(a);
        }
        private static bool WithinRange(Senses.Agent agent)
        {
            var player = Senses.PlayerObj;
            float distance = Plugin.MaxDistance != null ? Plugin.MaxDistance.Value : 120f;
            return player == null || (agent.T.position - player.transform.position).sqrMagnitude <= distance * distance;
        }
        internal static bool Owns(GameObject owner)
        { Dog d; return owner != null && Dogs.TryGetValue(owner.GetInstanceID(), out d) && Eligible(d); }

        private static void Restore(Dog d)
        {
            if (!d.Applied) return;
            for (int i = 0; i < d.Bones.Length; i++) if (d.Bones[i] != null) { d.Bones[i].localPosition = d.NativePosition[i]; d.Bones[i].localRotation = d.NativeRotation[i]; }
            d.Applied = false;
        }
        private static void Prepare(Dog d)
        {
            if (d.Prepared) return;
            d.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            for (int i = 0; i < d.Ik.Length; i++) if (d.Ik[i] != null) { d.IkEnabled[i] = d.Ik[i].enabled; d.Ik[i].enabled = false; }
            d.Prepared = true;
        }
        private static void ReleaseJump(Dog d)
        {
            if (!d.Jumping) return;
            if (d.Body != null) d.Body.drag = d.Drag;
            if (d.Agent != null && d.Agent.Owner != null) { Brain.SetTravel(d.Agent.Owner, false); d.Agent.Travel = false; }
            d.Jumping = false;
        }
        private static void ReleaseDash(Dog d)
        {
            if (!d.Dashing) return;
            if (d.Body != null) { d.Body.drag = d.Drag; Vector3 v = d.Body.velocity; v.x = v.z = 0f; d.Body.velocity = v; }
            if (d.Agent != null && d.Agent.Owner != null) { Brain.SetTravel(d.Agent.Owner, false); d.Agent.Travel = false; }
            d.Dashing = false;
        }
        private static void Release(Dog d)
        {
            Restore(d); ReleaseJump(d); ReleaseDash(d);
            if (d.Held && d.Agent != null && d.Agent.Owner != null) Brain.Halt(d.Agent.Owner, false);
            d.Held = false; d.DamageFsm = null; d.BiteTarget = null; d.Pose = Pose.Locomotion; d.Playing = null;
            d.WasQuiet = false; d.NextIdleChange = 0f; d.RecoveryUntil = 0f;
            foreach (var wait in d.OriginalWaits) if (wait.Key.time != null) wait.Key.time.Value = wait.Value;
            d.OriginalWaits.Clear();
            if (!d.Prepared) return;
            if (d.Animator != null) d.Animator.cullingMode = d.Culling;
            for (int i = 0; i < d.Ik.Length; i++) if (d.Ik[i] != null) d.Ik[i].enabled = d.IkEnabled[i];
            d.Prepared = false;
        }
        internal static void OnSceneLoaded() { foreach (var dog in Dogs.Values) Release(dog); Dogs.Clear(); }

        private static void Switch(Dog d, Pose pose, string clip)
        {
            for (int i = 0; i < d.Bones.Length; i++) if (d.Bones[i] != null) { d.FadePosition[i] = d.Bones[i].localPosition; d.FadeRotation[i] = d.Bones[i].localRotation; }
            d.Pose = pose; d.Playing = clip; d.Time = 0f; d.Fade = 0f;
            d.AttackRate = 1f;
        }
        private static bool Grounded(Dog d)
        {
            var c = d.Agent.Col; if (c == null) return false;
            return Physics.Raycast(c.bounds.center, Vector3.down, c.bounds.extents.y + .18f, Floor, QueryTriggerInteraction.Ignore);
        }
        private static bool InReach(Dog d, GameObject target)
        {
            if (target == null || !target.activeInHierarchy || d.Agent.Target != target) return false;
            var col = target.GetComponent<Collider>() ?? target.GetComponentInChildren<Collider>();
            Vector3 origin = d.Agent.T.position + Vector3.up * .25f;
            Vector3 point = col != null ? col.ClosestPoint(origin) : target.transform.position;
            Vector3 to = point - origin;
            if (to.magnitude > d.Reach + .2f || Mathf.Abs(to.y) > 1.2f) return false;
            Vector3 planar = to; planar.y = 0f;
            if (planar.sqrMagnitude > .01f && Vector3.Dot(d.Agent.T.forward, planar.normalized) < (d.Pose == Pose.RunningBite ? .65f : .45f)) return false;
            RaycastHit hit;
            return !Physics.Linecast(origin, point, out hit, Solid, QueryTriggerInteraction.Ignore) || IntendedTarget(hit.collider, target);
        }
        private static bool IntendedTarget(Collider collider, GameObject target)
        { return collider != null && target != null && (collider.gameObject == target || collider.transform.IsChildOf(target.transform)); }
        private static Vector3 TargetPoint(Dog d, GameObject target)
        {
            var col = target.GetComponent<Collider>() ?? target.GetComponentInChildren<Collider>();
            return col != null ? col.ClosestPoint(d.Agent.T.position + Vector3.up * .25f) : target.transform.position;
        }
        private static bool DamageReady(Dog d)
        {
            if (d.DamageComponent == null || !d.DamageComponent.enabled)
                d.DamageComponent = d.Agent.Owner.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f != null && f.enabled && f.FsmName == "Damage");
            return d.DamageComponent != null && d.DamageComponent.enabled && d.DamageComponent.Fsm != null && d.DamageComponent.Fsm.Initialized && d.DamageComponent.Fsm.ActiveStateName == "trigger";
        }
        private static void StartAttack(Dog d, Fsm damage, bool running)
        {
            d.BiteTarget = d.Agent.Target; d.DamageFsm = damage; d.HitSent = false;
            Switch(d, running ? Pose.RunningBite : Pose.Bite, running ? "MovingBite" : "Bite");
            d.AttackRate = d.Clips[d.Playing].length / (running ? .70f : .48f);
            d.NextBite = Time.time + (running ? 1.02f : .58f) + UnityEngine.Random.Range(0f, .04f);
            d.RecoveryUntil = Time.time + (running ? .90f : .56f);
            if (!running) return;
            Vector3 direction = TargetPoint(d, d.BiteTarget) - d.Agent.T.position; direction.y = 0f;
            d.DashDirection = direction.sqrMagnitude > .001f ? direction.normalized : d.Agent.T.forward;
            d.DashSpeed = Mathf.Clamp(Mathf.Max(Brain.RunSpeed(d.Agent.Owner), new Vector2(d.Body.velocity.x, d.Body.velocity.z).magnitude), 5f, 7f);
            // Once committed, pursue the snapshot direction rather than the moving target.
            if (d.Held) { d.Held = false; Brain.Halt(d.Agent.Owner, false); }
            d.Drag = d.Body.drag; d.Body.drag = 0f; d.Dashing = true;
            Brain.SetTravel(d.Agent.Owner, true); d.Agent.Travel = true;
        }
        private static void TryAttack(Dog d)
        {
            var a = d.Agent;
            if (d.Pose != Pose.Locomotion || a.State != Senses.State.Combat || a.Target == null || Time.time < d.NextBite || !DamageReady(d)) return;
            Vector3 to = TargetPoint(d, a.Target) - a.T.position; to.y = 0f;
            float distance = to.magnitude;
            if (InReach(d, a.Target)) { StartAttack(d, d.DamageComponent.Fsm, false); return; }
            float speed = Mathf.Clamp(Mathf.Max(Brain.RunSpeed(a.Owner), new Vector2(d.Body.velocity.x, d.Body.velocity.z).magnitude), 5f, 7f);
            if (d.Clips.ContainsKey("MovingBite") && distance <= d.Reach + speed * .22f && Grounded(d) && (distance < .05f || Vector3.Dot(a.T.forward, to / distance) >= .55f))
                StartAttack(d, d.DamageComponent.Fsm, true);
        }
        private static void DriveDash(Dog d, float dt)
        {
            float seconds = d.Time / Mathf.Max(.01f, d.AttackRate);
            float speed = seconds < .12f ? 2f : seconds < .48f ? d.DashSpeed : 2f;
            RaycastHit hit;
            bool blocked = d.Body.SweepTest(d.DashDirection, out hit, speed * Mathf.Max(dt, .016f) + .08f, QueryTriggerInteraction.Ignore);
            if (blocked && (IntendedTarget(hit.collider, d.BiteTarget) || hit.normal.y > .6f)) blocked = false;
            Vector3 velocity = d.DashDirection * (blocked ? 0f : speed); velocity.y = d.Body.velocity.y;
            d.Agent.T.rotation = Quaternion.LookRotation(d.DashDirection); d.Body.velocity = velocity;
        }
        public static void BeforeDamageWait(Wait __instance)
        {
            var fsm = __instance.Fsm; Dog d;
            if (fsm == null || fsm.Name != "Damage" || fsm.ActiveStateName != "attack" || fsm.GameObject == null || !Dogs.TryGetValue(fsm.GameObject.GetInstanceID(), out d) || !Eligible(d) || __instance.time == null) return;
            if (!d.OriginalWaits.ContainsKey(__instance)) d.OriginalWaits[__instance] = __instance.time.Value;
            __instance.time.Value = d.Pose == Pose.RunningBite ? .48f : .32f;
        }

        public static bool BeforeDamageEvent(Fsm __instance, FsmEvent fsmEvent)
        {
            if (__instance == null || fsmEvent == null || fsmEvent.Name != "attack" || __instance.Name != "Damage" || __instance.GameObject == null) return true;
            Dog d;
            if (!Dogs.TryGetValue(__instance.GameObject.GetInstanceID(), out d) || !Eligible(d) || d.Committing) return true;
            if (d.Pose == Pose.Bite || d.Pose == Pose.RunningBite || d.Pose == Pose.JumpAttack || d.Pose == Pose.LieDown || d.Pose == Pose.Rest || d.Pose == Pose.GetUp || Time.time < d.NextBite) return false;
            if (d.Agent.State != Senses.State.Combat || d.Agent.Target == null) return true;
            StartAttack(d, __instance, false);
            bool leap = jumping.Value && d.Clips.ContainsKey("JumpAttack") && Grounded(d) && !Senses.PlayerInCar && UnityEngine.Random.value < .25f;
            if (leap) { Switch(d, Pose.JumpAttack, "JumpAttack"); d.NextBite = Time.time + d.Clips[d.Playing].length; }
            if (leap)
            {
                Vector3 direction = d.BiteTarget.transform.position - d.Agent.T.position; direction.y = 0f;
                RaycastHit hit = new RaycastHit();
                bool blocked = direction.sqrMagnitude > .01f && d.Body.SweepTest(direction.normalized, out hit, .6f, QueryTriggerInteraction.Ignore);
                if (blocked && hit.collider != null && (hit.collider.gameObject == d.BiteTarget || hit.collider.transform.IsChildOf(d.BiteTarget.transform))) blocked = false;
                if (direction.sqrMagnitude > .01f && !blocked)
                {
                    d.Drag = d.Body.drag; d.Body.drag = 0f; d.Jumping = true; d.JumpSince = Time.time;
                    Brain.SetTravel(d.Agent.Owner, true); d.Agent.Travel = true;
                    d.Body.velocity = direction.normalized * 1.2f + Vector3.up * 2.2f;
                }
                else StartAttack(d, __instance, false);
            }
            return false;
        }

        public static bool BeforeSetVelocity(SetVelocity __instance)
        {
            var fsm = __instance.Fsm; Dog d;
            return fsm == null || fsm.Name != "Movement" || fsm.GameObject == null || !Dogs.TryGetValue(fsm.GameObject.GetInstanceID(), out d) || !Eligible(d) || (!d.Held && !d.Dashing);
        }

        internal static void Tick()
        {
            float dt = Time.deltaTime;
            var dead = new List<int>();
            foreach (var pair in Dogs)
            {
                var d = pair.Value;
                // Transition blends start from the prior displayed pose before restoring the native baseline.
                if (!Eligible(d)) { Release(d); if (d.Agent == null || d.Agent.Owner == null) dead.Add(pair.Key); continue; }
                Prepare(d);
                var a = d.Agent; Vector3 velocity = d.Body.velocity; float speed = new Vector2(velocity.x, velocity.z).magnitude;
                bool quiet = resting.Value && !d.Boss && a.State == Senses.State.Idle && a.Target == null && a.Ghost == null && speed < .15f && Grounded(d);
                if (quiet && !d.WasQuiet) d.NextIdleChange = Time.time + IdleSpan();
                if (!quiet) d.NextIdleChange = 0f;
                d.WasQuiet = quiet;
                if (d.Pose == Pose.Locomotion)
                {
                    if (quiet && Time.time >= d.NextIdleChange) Switch(d, Pose.LieDown, "LieDown");
                    else
                    {
                        string clip = speed > 3f ? "Run" : speed > .15f && d.Clips.ContainsKey("Walk") ? "Walk" : "Idle";
                        if (clip != d.Playing) Switch(d, Pose.Locomotion, clip);
                    }
                    if (a.State == Senses.State.Combat && a.Target != null)
                    {
                        Vector3 to = TargetPoint(d, a.Target) - a.T.position; to.y = 0f;
                        if (to.sqrMagnitude > .0025f && to.magnitude <= d.Reach + 2f)
                            a.T.rotation = Quaternion.RotateTowards(a.T.rotation, Quaternion.LookRotation(to), 1080f * dt);
                        TryAttack(d);
                    }
                }
                else if ((d.Pose == Pose.Rest || d.Pose == Pose.LieDown) && !quiet) Switch(d, Pose.GetUp, "GetUp");
                else if (d.Pose == Pose.Rest && quiet && Time.time >= d.NextIdleChange) Switch(d, Pose.GetUp, "GetUp");
                d.Fade += dt;
                float rate = d.Pose == Pose.Bite || d.Pose == Pose.RunningBite ? d.AttackRate : d.Playing == "Run" ? Mathf.Clamp(speed / Math.Max(2f, Brain.RunSpeed(a.Owner)), .35f, 2f) : 1f;
                d.Time += dt * rate;
                if (d.Dashing) DriveDash(d, dt);
                if (d.Jumping && ((Time.time - d.JumpSince > .12f && d.Body.velocity.y <= 0f && Grounded(d)) || Time.time - d.JumpSince > 2f)) ReleaseJump(d);
                if ((d.Pose == Pose.Bite || d.Pose == Pose.RunningBite || d.Pose == Pose.JumpAttack) && !d.HitSent && d.Time >= d.Clips[d.Playing].length * (d.Pose == Pose.Bite ? .38f : .45f))
                {
                    d.HitSent = true;
                    if (InReach(d, d.BiteTarget) && d.DamageFsm != null && d.DamageFsm.ActiveStateName == "trigger")
                    {
                        d.Committing = true;
                        try
                        {
                            var victim = d.DamageFsm.Variables.FindFsmGameObject("detectedObj");
                            if (victim != null) victim.Value = d.BiteTarget;
                            d.DamageFsm.Event("attack");
                        }
                        finally { d.Committing = false; }
                    }
                }
                if (d.Time >= d.Clips[d.Playing].length)
                {
                    if (d.Pose == Pose.LieDown) { Switch(d, Pose.Rest, "LieIdle"); d.NextIdleChange = Time.time + IdleSpan(); }
                    else if (d.Pose == Pose.GetUp || d.Pose == Pose.Bite || d.Pose == Pose.RunningBite || (d.Pose == Pose.JumpAttack && !d.Jumping))
                    {
                        ReleaseDash(d); d.DamageFsm = null; d.BiteTarget = null; Switch(d, Pose.Locomotion, "Idle");
                        if (quiet) d.NextIdleChange = Time.time + IdleSpan();
                    }
                }
                // Only hold melee position where a bite can actually reach. Outside it, continue chasing or commit to a running bite.
                bool close = a.State == Senses.State.Combat && a.Target != null && InReach(d, a.Target);
                bool held = !d.Dashing && (close || d.Pose != Pose.Locomotion || Time.time < d.RecoveryUntil);
                if (held != d.Held) { d.Held = held; Brain.Halt(a.Owner, held); }
                else if (held) Brain.Halt(a.Owner, true); // Brain may have registered after the rest/attack began.
                if (held && !d.Jumping && !d.Dashing) d.Body.velocity = new Vector3(0f, d.Body.velocity.y, 0f);
                Restore(d);
            }
            foreach (int id in dead) Dogs.Remove(id);
        }

        internal static void LateTick()
        {
            foreach (var d in Dogs.Values)
            {
                if (!Eligible(d) || string.IsNullOrEmpty(d.Playing)) continue;
                if (d.Dashing) d.Agent.T.rotation = Quaternion.LookRotation(d.DashDirection);
                for (int i = 0; i < d.Bones.Length; i++) if (d.Bones[i] != null) { d.NativePosition[i] = d.Bones[i].localPosition; d.NativeRotation[i] = d.Bones[i].localRotation; }
                var clip = d.Clips[d.Playing]; bool loop = d.Pose == Pose.Locomotion || d.Pose == Pose.Rest;
                float time = loop ? d.Time % clip.length : Mathf.Min(d.Time, clip.length);
                clip.SampleAnimation(d.Animator.gameObject, time);
                float blend = Mathf.Clamp01(d.Fade / .16f);
                if (blend < 1f) for (int i = 0; i < d.Bones.Length; i++) if (d.Bones[i] != null) { d.Bones[i].localPosition = Vector3.Lerp(d.FadePosition[i], d.Bones[i].localPosition, blend); d.Bones[i].localRotation = Quaternion.Slerp(d.FadeRotation[i], d.Bones[i].localRotation, blend); }
                d.Applied = true;
            }
        }
    }
}
