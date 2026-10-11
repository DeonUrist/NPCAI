using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using HutongGames.PlayMaker;

public sealed partial class Checks
{
    public void RunCompatibility()
    {
        Directory.CreateDirectory(Output);
        try
        {
            var loaded = (IDictionary)Get(DogType, null, "Clips");
            if (Environment.GetCommandLineArgs().Contains("-npcai-companion-fixes"))
            {
                RunCompanionFixes(Environment.GetCommandLineArgs().First(a => a.StartsWith("-npcai-dogs-targets=")).Split(new[] { '=' }, 2)[1]); return;
            }
            bool fallback = Environment.GetCommandLineArgs().Contains("-npcai-companion-fallback");
            Check(loaded.Count == 5 && loaded.Values.Cast<Dictionary<string, AnimationClip>>().Sum(c => c.Count) == (fallback ? 45 : 57), fallback ? "Native Unity loads the old bundle without sit clips for the fallback check" : "Native Unity loads 57 clips for five rigs, including sitting and sneaking animations");
            string source = Environment.GetCommandLineArgs().First(a => a.StartsWith("-npcai-dogs-targets=")).Split(new[] { '=' }, 2)[1];
            if (Environment.GetCommandLineArgs().Contains("-npcai-height-diagnostics")) { HeightDiagnostics(source); return; }
            // Exercise the original NPC path through the refactored shared state machine.
            foreach (string file in Directory.GetFiles(source, "*.bin"))
            {
                var root = ExportedRig(file, fixtures.Count); var agent = Agent(root);
                Call(Mod.GetType("NPCAI.DogBite"), null, "Register", agent);
                var d = Registered()[root.GetInstanceID()]; var type = d.GetType();
                if ((bool)Get(type, d, "Boss")) { Call(DogType, null, "Tick"); Check(Get(type, d, "Pose").ToString() == "Locomotion", "Native boss remains standing"); continue; }
                Set(type, d, "WasQuiet", true); Set(type, d, "NextIdleChange", Time.time - 1f);
                Call(DogType, null, "Tick"); Check(Get(type, d, "Pose").ToString() == "LieDown", "Native " + root.name + " still enters lying rest");
                Set(type, d, "Time", 2f); Call(DogType, null, "Tick");
                Check(Get(type, d, "Pose").ToString() == "Rest", "Native " + root.name + " still loops its resting animation");
            }
            var nativeDog = Registered().Values.First(d => !((bool)Get(d.GetType(), d, "Boss")));
            var nativeType = nativeDog.GetType(); var nativeAgent = Get(nativeType, nativeDog, "Agent"); var nativeAgentType = nativeAgent.GetType();
            var nativeOwner = (GameObject)Get(nativeAgentType, nativeAgent, "Owner");
            var nativeVictim = new GameObject("Verifier.NativeVictim"); fixtures.Add(nativeVictim); nativeVictim.layer = 10;
            nativeVictim.AddComponent<BoxCollider>().size = new Vector3(.2f, .6f, .2f); nativeVictim.transform.position = nativeOwner.transform.position + Vector3.forward * .6f + Vector3.up * .25f;
            Set(nativeAgentType, nativeAgent, "Target", nativeVictim); Set(nativeAgentType, nativeAgent, "State", Enum.Parse(Mod.GetType("NPCAI.Senses+State"), "Combat")); Physics.SyncTransforms();
            Call(DogType, null, "Tick"); Check(Get(nativeType, nativeDog, "Pose").ToString() == "GetUp", "Native dog wakes through GetUp when combat interrupts rest");
            Set(nativeType, nativeDog, "Time", 3f); Call(DogType, null, "Tick");
            var damage = Damage(nativeOwner); CountContact.Hits = 0; damage.Fsm.Event("attack");
            Check(Get(nativeType, nativeDog, "Pose").ToString() == "Bite" && CountContact.Hits == 0, "Native FSM attack retains shared anticipation timing");
            Set(nativeType, nativeDog, "Time", .7f); Call(DogType, null, "Tick"); Check(CountContact.Hits == 1, "Native FSM still receives exactly one contact attack");
            Call(DogType, null, "OnSceneLoaded");
            BepInEx.PluginInfo companion;
            if (!Chainloader.PluginInfos.TryGetValue("apocalypter.dogcompanion", out companion))
            {
                Check(true, "NPC Overhaul loads and native canine states work with Dog Companion absent");
                return;
            }
            var assembly = companion.Instance.GetType().Assembly;
            var componentType = assembly.GetType("DogCompanion.Dog");
            var npcType = assembly.GetType("DogCompanion.Npc");
            var rootDog = ExportedRig(Path.Combine(source, "Wild_Hound.bin"), fixtures.Count);
            Call(assembly.GetType("DogCompanion.Manager"), null, "Strip", rootDog);
            var dogComponent = (MonoBehaviour)rootDog.AddComponent(componentType);
            rootDog.name = "Companion Dog";
            Call(componentType, dogComponent, "Update"); // real Harmony registration
            if (fallback)
            {
                Check(!Registered().ContainsKey(rootDog.GetInstanceID()), "An old animation bundle leaves the companion under its original controller");
                var fallbackVictim = new GameObject("Verifier.FallbackVictim"); fixtures.Add(fallbackVictim);
                fallbackVictim.transform.position = (Vector3)componentType.GetProperty("Feet", Flags).GetValue(dogComponent, null) + Vector3.forward * .6f;
                var fallbackNpc = Activator.CreateInstance(npcType, true); var fallbackDamage = new FsmFloat();
                Set(npcType, fallbackNpc, "Go", fallbackVictim); Set(npcType, fallbackNpc, "Transform", fallbackVictim.transform);
                Set(npcType, fallbackNpc, "Health", new FsmFloat { Value = 100f }); Set(npcType, fallbackNpc, "Damage", fallbackDamage); Set(npcType, fallbackNpc, "Fsms", new PlayMakerFSM[0]);
                Set(componentType, dogComponent, "_target", fallbackNpc); Call(componentType, dogComponent, "ChaseTick", rootDog.transform, .016f);
                Check(fallbackDamage.Value < 0f, "The original companion attack still applies damage when sit clips are missing");
                return;
            }
            Check(rootDog.GetComponent<Rigidbody>() == null && rootDog.GetComponent<Collider>() == null && Registered().ContainsKey(rootDog.GetInstanceID()), "Actual Dog Companion component registers its stripped hound without a Rigidbody or Collider");
            var dog = Registered()[rootDog.GetInstanceID()]; var dogType = dog.GetType();
            var driver = Get(dogType, dog, "Companion"); var driverType = driver.GetType();
            CheckSupport(rootDog, dog, driver, "Ground scale 1");
            rootDog.transform.localScale = Vector3.one * 1.25f;
            CheckSupport(rootDog, dog, driver, "Ground scale 1.25");
            rootDog.transform.localScale = Vector3.one;
            Action tick = () => Call(DogType, null, "TickDog", dog, .016f);
            Action reset = () => { Set(dogType, dog, "NextBite", 0f); Set(dogType, dog, "RecoveryUntil", 0f); Set(dogType, dog, "Time", 3f); tick(); };
            Action sync = () => Call(driverType, driver, "SyncTarget");
            Func<string> pose = () => Get(dogType, dog, "Pose").ToString();
            Set(driverType, driver, "Speed", 1f); tick(); Check((string)Get(dogType, dog, "Playing") == "Walk", "Companion uses Overhaul walking animation at low speed");
            Set(driverType, driver, "Speed", 6f); tick(); Check((string)Get(dogType, dog, "Playing") == "Run", "Companion uses Overhaul running animation at high speed");
            Set(driverType, driver, "Speed", 0f); tick(); Check((string)Get(dogType, dog, "Playing") == "Idle", "Companion uses Overhaul idle animation while stationary");
            // Ride is parented/kinematic in the mod; the bridge must still own it.
            var car = new GameObject("Verifier.Car"); fixtures.Add(car); rootDog.transform.SetParent(car.transform, true);
            car.transform.rotation = Quaternion.Euler(18f, 23f, 12f); rootDog.transform.localRotation = Quaternion.identity; rootDog.transform.localScale = Vector3.one * .9f;
            CheckSupport(rootDog, dog, driver, "Tilted car scale 0.9");
            rootDog.transform.localScale = Vector3.one; car.transform.rotation = Quaternion.identity;
            Set(componentType, dogComponent, "_mode", Enum.ToObject(componentType.GetNestedType("Mode", Flags), 1)); sync(); tick();
            Check(pose() == "SitDown" && (bool)Call(DogType, null, "Owns", rootDog), "Parented companion starts SitDown in the vehicle rather than LieDown");
            Set(dogType, dog, "Time", 1.24f); Set(dogType, dog, "Fade", 1f); Call(componentType, dogComponent, "LateUpdate");
            var displayedBones = (Transform[])Get(dogType, dog, "Bones"); var previousPose = displayedBones.Select(b => b.localRotation).ToArray();
            Call(DogType, null, "RestoreCompanion", rootDog);
            Set(dogType, dog, "Time", 2f); tick(); Check(pose() == "Sitting" && (string)Get(dogType, dog, "Playing") == "SitIdle", "Vehicle dog transitions to the sitting loop");
            Call(componentType, dogComponent, "LateUpdate"); Check((bool)Get(dogType, dog, "Applied"), "Harmony LateUpdate samples the sitting clip after the companion's own pose update");
            Check(displayedBones.Select((b, i) => Quaternion.Angle(b.localRotation, previousPose[i])).Max() < 1f, "SitDown to SitIdle keeps the displayed pose continuous across baseline restoration");
            var sittingClip = ((Dictionary<string, AnimationClip>)Get(dogType, dog, "Clips"))["SitIdle"];
            var animator = (Animator)Get(dogType, dog, "Animator");
            var bones = (Transform[])Get(dogType, dog, "Bones");
            sittingClip.SampleAnimation(animator.gameObject, .5f); var sitRotations = bones.Select(b => b.localRotation).ToArray();
            ((Dictionary<string, AnimationClip>)Get(dogType, dog, "Clips"))["LieIdle"].SampleAnimation(animator.gameObject, .5f);
            Check(bones.Select((b, i) => Quaternion.Angle(b.localRotation, sitRotations[i])).Max() > 20f, "Sitting has a distinct animated pose from lying on the original Wild Hound skeleton");
            Call(DogType, null, "RestoreCompanion", rootDog);
            CheckSeating(rootDog, dogComponent, dog);
            rootDog.transform.SetParent(null, true); Set(componentType, dogComponent, "_mode", Enum.ToObject(componentType.GetNestedType("Mode", Flags), 0)); sync(); tick();
            Check(pose() == "Locomotion", "Leaving a car immediately resumes companion locomotion");
            var feetValue = (Vector3)componentType.GetProperty("Feet", Flags).GetValue(dogComponent, null);
            var victim = new GameObject("Verifier.Victim"); fixtures.Add(victim); victim.layer = 10; victim.AddComponent<BoxCollider>().size = new Vector3(.3f, 1f, .3f);
            victim.transform.position = feetValue + Vector3.forward * .65f + Vector3.up * .3f;
            rootDog.transform.rotation = Quaternion.identity;
            var npc = Activator.CreateInstance(npcType, true);
            Set(npcType, npc, "Go", victim); Set(npcType, npc, "Transform", victim.transform); Set(npcType, npc, "Health", new FsmFloat { Value = 100f });
            var damageValue = new FsmFloat { Value = 0f }; Set(npcType, npc, "Damage", damageValue); Set(npcType, npc, "Fsms", new PlayMakerFSM[0]);
            Set(componentType, dogComponent, "_target", npc); sync(); Physics.SyncTransforms();
            Set(dogType, dog, "NextBite", 0f); tick(); Check(pose() == "Bite" && damageValue.Value == 0f, "Close bite starts the shared anticipation animation without immediate damage");
            Call(componentType, dogComponent, "ChaseTick", rootDog.transform, .016f);
            Check(damageValue.Value == 0f, "The actual patched ChaseTick suppresses Dog Companion's immediate-damage attack");
            Set(dogType, dog, "Time", .6f); tick();
            Check(damageValue.Value == -((ConfigEntry<float>)Get(assembly.GetType("DogCompanion.Plugin"), null, "BiteDamage")).Value, "Shared contact frame applies the companion's configured bite damage");
            damageValue.Value = 0f; tick(); Check(damageValue.Value == 0f, "Repeated updates never duplicate contact damage");
            reset(); Set(dogType, dog, "NextBite", 0f); tick(); Check(pose() == "Bite", "Another close bite can begin after recovery");
            victim.transform.position += Vector3.forward * 5f; Physics.SyncTransforms(); Set(dogType, dog, "Time", .6f); tick();
            Check(damageValue.Value == 0f, "Target moving away during anticipation makes the bite miss");
            reset(); victim.transform.position = feetValue + Vector3.forward * .65f + Vector3.up * .3f; Physics.SyncTransforms();
            Set(dogType, dog, "NextBite", 0f); tick();
            var wall = new GameObject("Verifier.Wall"); fixtures.Add(wall); wall.AddComponent<BoxCollider>().size = new Vector3(2f, 2f, .1f); wall.transform.position = feetValue + Vector3.forward * .3f; Physics.SyncTransforms();
            Set(dogType, dog, "Time", .6f); tick(); Check(damageValue.Value == 0f, "Solid cover blocks a bite at the contact frame");
            UnityEngine.Object.DestroyImmediate(wall); reset();
            victim.transform.position = feetValue + Vector3.forward * 2f + Vector3.up * .3f; Physics.SyncTransforms();
            var floor = new GameObject("Verifier.CompanionFloor"); fixtures.Add(floor); floor.AddComponent<BoxCollider>().size = new Vector3(20f, .1f, 20f); floor.transform.position = feetValue + Vector3.down * .06f; Physics.SyncTransforms();
            Set(dogType, dog, "NextBite", 0f); tick();
            Check(pose() == "RunningBite" && (bool)Get(dogType, dog, "Dashing"), "Approach uses the same committed running bite as native Overhaul dogs");
            var direction = (Vector3)Get(dogType, dog, "DashDirection"); victim.transform.position += Vector3.right * 4f; Physics.SyncTransforms();
            Set(dogType, dog, "Time", .7f); tick(); Check((Vector3)Get(dogType, dog, "DashDirection") == direction && damageValue.Value == 0f, "Running bite retains its snapshot direction and a sidestep avoids damage");
            Set(componentType, dogComponent, "_target", null); sync(); tick(); Check(!(bool)Get(dogType, dog, "Dashing") && pose() == "Locomotion", "Losing a target releases the committed attack for follow/fetch behavior");
            victim.transform.position = (Vector3)componentType.GetProperty("Feet", Flags).GetValue(dogComponent, null) + Vector3.forward * .65f + Vector3.up * .3f; Physics.SyncTransforms();
            Set(componentType, dogComponent, "_target", npc); sync(); Set(dogType, dog, "NextBite", 0f); tick();
            Check(pose() == "Bite", "A living target can begin another close bite");
            ((FsmFloat)Get(npcType, npc, "Health")).Value = 0f; Set(dogType, dog, "Time", .6f); tick();
            Check(pose() == "Locomotion" && damageValue.Value == 0f, "A target dying during anticipation cancels the attack without posthumous damage");
            ((ConfigEntry<bool>)Get(DogType, null, "enabled")).Value = false; tick();
            Check(!(bool)Get(dogType, dog, "Prepared") && !(bool)Get(dogType, dog, "Held") && !(bool)Get(dogType, dog, "Applied"), "Disabling animations restores native pose, culling and movement ownership");
            Check(((Animator)Get(dogType, dog, "Animator")).transform.localPosition == (Vector3)Get(componentType, dogComponent, "_rigBase"), "Disabling animations restores the companion's original visual rig height");
            ((ConfigEntry<bool>)Get(DogType, null, "enabled")).Value = true; tick();
            rootDog.SetActive(false); Check(!Registered().ContainsKey(rootDog.GetInstanceID()), "Dismiss/disable unregisters the companion and restores ownership");
            rootDog.SetActive(true); Call(componentType, dogComponent, "Update"); Check(Registered().ContainsKey(rootDog.GetInstanceID()), "Recall/re-enable re-registers the existing companion");
            Call(DogType, null, "OnSceneLoaded"); Check(Registered().Count == 0, "Scene cleanup removes all animation and companion driver state");
            report.Add("Controlled native Unity checks use the shipped Dog Companion DLL and exported original skeletons. Clip clocks are explicitly advanced; normal gameplay, fetch/tool props and vehicle-specific seating offsets require live visual review.");
        }
        catch (Exception e) { report.Add("FAIL " + e); }
        finally { File.WriteAllLines(Path.Combine(Output, "report.txt"), report); }
    }
}
