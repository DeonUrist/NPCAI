using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace NPCAI
{
    // Dog Companion strips the hound's Rigidbody and FSMs. Only this adapter knows
    // its private API; both native hounds and the companion use DogAnimations.
    internal static class DogCompanionCompat
    {
        private static readonly Dictionary<int, Driver> Drivers = new Dictionary<int, Driver>();
        private static FieldInfo target, mode, lunge, lastFight, nextBite, targetGo, pawDepth;
        private static PropertyInfo feet, alive;
        private static MethodInfo step, setMoving, unstick, stuck, giveUp, hurt, snarl;
        private static ConfigEntry<float> runSpeed, biteDamage, leash;
        private static bool warnedBundle;
        private static PropertyInfo knownPlayer;

        internal static void Initialize()
        {
            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue("apocalypter.dogcompanion", out info)) return;
            var harmony = new Harmony(Plugin.GUID + ".dogcompanion");
            try
            {
                var assembly = info.Instance.GetType().Assembly;
                var dog = assembly.GetType("DogCompanion.Dog", true);
                var npc = assembly.GetType("DogCompanion.Npc", true);
                var config = assembly.GetType("DogCompanion.Plugin", true);
                knownPlayer = Property(assembly.GetType("DogCompanion.Game", true), "KnownPlayer");
                target = Field(dog, "_target"); mode = Field(dog, "_mode"); lunge = Field(dog, "_lunge");
                lastFight = Field(dog, "_lastFight"); nextBite = Field(dog, "_nextBite"); targetGo = Field(npc, "Go");
                pawDepth = Field(dog, "_pawDepth");
                feet = Property(dog, "Feet"); alive = Property(npc, "Alive");
                step = Method(dog, "Step"); setMoving = Method(dog, "SetMoving"); unstick = Method(dog, "Unstick");
                stuck = Method(dog, "Stuck"); giveUp = Method(dog, "GiveUp"); hurt = Method(npc, "Hurt"); snarl = Method(dog, "Snarl");
                runSpeed = (ConfigEntry<float>)Field(config, "RunSpeed").GetValue(null);
                biteDamage = (ConfigEntry<float>)Field(config, "BiteDamage").GetValue(null);
                leash = (ConfigEntry<float>)Field(config, "LeashDistance").GetValue(null);
                // Resolve the complete contract before installing any hooks.
                var update = Method(dog, "Update"); var chase = Method(dog, "ChaseTick");
                var late = Method(dog, "LateUpdate"); var disable = Method(dog, "OnDisable"); var steer = Method(dog, "Steer");
                var ride = Method(dog, "RideTick");
                var distraction = Method(assembly.GetType("DogCompanion.Distraction", true), "Nearest");
                harmony.Patch(update, prefix: Hook(nameof(BeforeUpdate)), postfix: Hook(nameof(AfterUpdate)));
                harmony.Patch(chase, prefix: Hook(nameof(BeforeChase)));
                harmony.Patch(late, postfix: Hook(nameof(AfterLateUpdate)));
                harmony.Patch(disable, prefix: Hook(nameof(BeforeDisable)));
                harmony.Patch(steer, prefix: Hook(nameof(BeforeSteer)));
                harmony.Patch(ride, postfix: Hook(nameof(AfterRideTick)));
                harmony.Patch(step, prefix: Hook(nameof(BeforeStep)));
                harmony.Patch(distraction, prefix: Hook(nameof(BeforeDistraction)));
                Plugin.Log.LogInfo("Dog Companion compatibility enabled (" + info.Metadata.Version + "): shared canine locomotion/combat and sitting in vehicles.");
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Plugin.Log.LogWarning("Dog Companion compatibility unavailable; keeping its original behavior: " + e.Message);
            }
        }
        private static FieldInfo Field(Type type, string name)
        { return AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name); }
        private static PropertyInfo Property(Type type, string name)
        { return AccessTools.Property(type, name) ?? throw new MissingMemberException(type.FullName, name); }
        private static MethodInfo Method(Type type, string name)
        { return AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name); }
        private static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(DogCompanionCompat), name); }

        internal sealed class Driver
        {
            internal readonly MonoBehaviour Dog;
            internal readonly Senses.Agent Agent;
            internal bool Held, Failed, FixedDash;
            internal float Speed;
            internal Vector3 Before;
            internal GameObject DashTarget;
            internal bool WantsSneak;
            private GameObject movementPlayer;
            private PlayMakerFSM playerMovement;
            private FsmFloat standingHeight;
            internal Driver(MonoBehaviour dog)
            { Dog = dog; Agent = new Senses.Agent { Owner = dog.gameObject, T = dog.transform, Col = dog.GetComponent<Collider>() }; }
            internal bool Active { get { return !Failed && Dog != null && Dog.isActiveAndEnabled; } }
            internal bool Riding { get { return Convert.ToInt32(mode.GetValue(Dog)) == 1; } }
            internal bool Sneaking
            {
                get
                {
                    object npc = target.GetValue(Dog);
                    return WantsSneak && !Riding && Agent.Target == null && (npc == null || !(bool)alive.GetValue(npc, null));
                }
            }
            internal void RefreshSneaking()
            {
                var player = Senses.PlayerObj ?? (GameObject)knownPlayer.GetValue(null, null);
                if (player != movementPlayer || (player != null && playerMovement == null))
                {
                    movementPlayer = player; playerMovement = null; standingHeight = null;
                    if (player != null) foreach (var fsm in player.GetComponents<PlayMakerFSM>()) if (fsm.FsmName == "Movement") { playerMovement = fsm; standingHeight = fsm.FsmVariables.FindFsmFloat("standingHeight"); break; }
                }
                string state = playerMovement != null && playerMovement.enabled ? playerMovement.ActiveStateName ?? "" : "";
                WantsSneak = state.StartsWith("Crouch", StringComparison.OrdinalIgnoreCase) || state.StartsWith("Crawl", StringComparison.OrdinalIgnoreCase)
                    || (playerMovement != null && playerMovement.enabled && standingHeight != null && standingHeight.Value > .1f && standingHeight.Value < 1.4f);
            }
            internal Vector3 Feet { get { return (Vector3)feet.GetValue(Dog, null); } }
            internal float PawHeight { get { return -(float)pawDepth.GetValue(Dog); } }
            internal float RunSpeed { get { return runSpeed.Value * 1.1f; } }
            internal float NativeNextBite { get { return (float)nextBite.GetValue(Dog); } }
            internal void SetCooldown(float until) { nextBite.SetValue(Dog, until); }
            internal void SyncTarget()
            {
                object npc = Riding ? null : target.GetValue(Dog);
                Agent.Target = npc != null && (bool)alive.GetValue(npc, null) ? (GameObject)targetGo.GetValue(npc) : null;
                Agent.State = Agent.Target != null ? Senses.State.Combat : Senses.State.Idle;
            }
            internal bool TargetAlive(GameObject victim)
            {
                object npc = target.GetValue(Dog);
                return victim != null && npc != null && (GameObject)targetGo.GetValue(npc) == victim && (bool)alive.GetValue(npc, null);
            }
            internal void Halt(bool hold)
            { Held = hold; if (hold && Active) setMoving.Invoke(Dog, new object[] { false, 0f }); }
            internal void Contact(GameObject victim)
            {
                if (!Active || Riding || !TargetAlive(victim)) return;
                hurt.Invoke(target.GetValue(Dog), new object[] { biteDamage.Value });
                if (Senses.PlayerObj != null) Senses.Hurt(victim, Senses.PlayerObj);
                snarl.Invoke(Dog, null);
            }
            internal void Dash(Vector3 direction, float speed, float dt, GameObject victim)
            {
                if (!Active || Riding) return;
                FixedDash = true; DashTarget = victim;
                try { step.Invoke(Dog, new object[] { Feet + direction * 5f, speed, dt }); }
                finally { FixedDash = false; DashTarget = null; }
                Dog.transform.rotation = Quaternion.LookRotation(direction);
            }
            internal bool Chase(Transform player, float dt)
            {
                SyncTarget(); if (Agent.Target == null) return false;
                lastFight.SetValue(Dog, Time.time);
                Vector3 distance = Agent.Target.transform.position - player.position; distance.y = 0f;
                if (distance.magnitude > leash.Value) { giveUp.Invoke(Dog, new object[] { 5f }); SyncTarget(); return false; }
                // The shared animation state owns holding and committed dashes. All
                // other chasing keeps Dog Companion's obstacle/ground navigation.
                if (!Held && !DogAnimations.CompanionDashing(Dog.gameObject))
                {
                    step.Invoke(Dog, new object[] { Agent.Target.transform.position, RunSpeed, dt });
                    if ((bool)stuck.Invoke(Dog, new object[] { 3f })) { giveUp.Invoke(Dog, new object[] { 6f }); SyncTarget(); }
                }
                else unstick.Invoke(Dog, null);
                return false;
            }
        }
        private static Driver Get(MonoBehaviour dog)
        {
            if (dog == null) return null;
            Driver driver; int id = dog.GetInstanceID();
            if (!Drivers.TryGetValue(id, out driver))
            {
                driver = new Driver(dog);
                if (!DogAnimations.RegisterCompanion(driver.Agent, driver))
                {
                    if (!warnedBundle) { warnedBundle = true; Plugin.Log.LogWarning("Dog Companion needs the updated npcai_dogs.bundle (including sit clips); keeping its original behavior."); }
                    driver.Failed = true;
                }
                Drivers[id] = driver;
            }
            return driver;
        }
        private static bool Owns(Driver d) { return d != null && !d.Failed && DogAnimations.Owns(d.Dog.gameObject); }
        private static void Fail(Driver d, Exception e)
        {
            if (d == null) return;
            d.Failed = true; DogAnimations.RemoveCompanion(d.Dog.gameObject);
            Plugin.Log.LogWarning("Dog Companion compatibility released this dog after an error: " + e.GetBaseException().Message);
        }
        public static void BeforeUpdate(MonoBehaviour __instance)
        {
            Driver d = null;
            try { d = Get(__instance); if (d == null) return; d.SyncTarget(); d.RefreshSneaking(); DogAnimations.RestoreCompanion(__instance.gameObject); d.Before = __instance.transform.position; }
            catch (Exception e) { Fail(d, e); }
        }
        public static void AfterUpdate(MonoBehaviour __instance)
        {
            Driver d;
            if (!Drivers.TryGetValue(__instance.GetInstanceID(), out d) || d.Failed) return;
            try
            {
                d.SyncTarget(); Vector3 moved = __instance.transform.position - d.Before; moved.y = 0f;
                d.Speed = d.Riding || moved.magnitude > 2f ? 0f : moved.magnitude / Mathf.Max(.001f, Time.deltaTime);
                if (Owns(d)) lunge.SetValue(__instance, 1f); // suppress the old procedural jolt
                DogAnimations.TickCompanion(__instance.gameObject);
            }
            catch (Exception e) { Fail(d, e); }
        }
        public static bool BeforeChase(MonoBehaviour __instance, Transform __0, float __1)
        {
            Driver d = null;
            try { d = Get(__instance); return !Owns(d) || d.Chase(__0, __1); }
            catch (Exception e) { Fail(d, e); return true; }
        }
        public static bool BeforeSteer(MonoBehaviour __instance, Vector3 __0, Vector3 __1, ref Vector3 __result)
        {
            Driver d;
            if (!Drivers.TryGetValue(__instance.GetInstanceID(), out d) || !d.FixedDash) return true;
            RaycastHit hit;
            bool blocked = Physics.SphereCast(__0 + Vector3.up * .5f, .22f, __1, out hit, .4f,
                (1 << 0) | (1 << 8) | (1 << 9) | (1 << 11) | (1 << 14) | (1 << 16), QueryTriggerInteraction.Ignore);
            if (blocked && (hit.normal.y > .6f || (hit.collider != null && d.DashTarget != null && (hit.collider.gameObject == d.DashTarget || hit.collider.transform.IsChildOf(d.DashTarget.transform))))) blocked = false;
            __result = blocked ? Vector3.zero : __1;
            return false;
        }
        public static void BeforeStep(MonoBehaviour __instance, ref float __1)
        {
            Driver d;
            if (Drivers.TryGetValue(__instance.GetInstanceID(), out d) && Owns(d) && d.Sneaking) __1 = Mathf.Min(__1, 1.6f);
        }
        public static bool BeforeDistraction(FsmStateAction __0)
        {
            var fsm = __0 != null ? __0.Fsm : null;
            return fsm == null || !Senses.On || !Plugin.BrainEnabled.Value || Senses.AgentFor(fsm.GameObject) == null;
        }
        internal static GameObject CloseThreat(GameObject enemy, float range)
        {
            if (enemy == null) return null;
            foreach (var d in Drivers.Values)
                if (Owns(d) && !d.Riding && (d.Dog.transform.position - enemy.transform.position).sqrMagnitude <= range * range) return d.Dog.gameObject;
            return null;
        }
        public static void AfterLateUpdate(MonoBehaviour __instance)
        {
            Driver d;
            if (!Drivers.TryGetValue(__instance.GetInstanceID(), out d) || d.Failed) return;
            try { DogAnimations.LateCompanion(__instance.gameObject); }
            catch (Exception e) { Fail(d, e); }
        }
        public static void AfterRideTick(MonoBehaviour __instance)
        {
            Driver d = null;
            try
            {
                d = Get(__instance);
                if (Owns(d) && d.Riding) __instance.transform.localPosition += new Vector3(0f, DogCompanionSeating.UpOffset, DogCompanionSeating.ForwardOffset);
            }
            catch (Exception e) { Fail(d, e); }
        }
        public static void BeforeDisable(MonoBehaviour __instance)
        {
            DogAnimations.RemoveCompanion(__instance.gameObject);
            Drivers.Remove(__instance.GetInstanceID());
        }
        internal static void OnSceneLoaded() { Drivers.Clear(); }
    }
}
