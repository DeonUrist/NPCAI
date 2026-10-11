using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("npcai.dogs.native-verifier", "NPCAI Dogs Native Verifier", "1.0.0")]
[BepInDependency(NPCAI.Plugin.GUID)]
[BepInDependency("apocalypter.dogcompanion", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Probe : BaseUnityPlugin
{
    internal static bool Running;
    internal static readonly HashSet<int> Allowed = new HashSet<int>();
    void Awake()
    {
        string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-npcai-dogs-report="));
        if (arg == null) return;
        Running = true; new Harmony("npcai.dogs.native-verifier").PatchAll();
        var host = new GameObject("Verifier.Host"); DontDestroyOnLoad(host);
        var checks = host.AddComponent<Checks>(); checks.Output = arg.Substring(arg.IndexOf('=') + 1);
        checks.RunCompatibility();
        Environment.Exit(File.ReadAllLines(Path.Combine(checks.Output, "report.txt")).Any(l => l.StartsWith("FAIL ")) ? 1 : 0);
    }
    [HarmonyPatch(typeof(PlayMakerFSM), "OnEnable")]
    static class NoWorldOrSaves { static bool Prefix(PlayMakerFSM __instance) { if (!Running || Allowed.Contains(__instance.GetInstanceID())) return true; __instance.enabled = false; return false; } }
}

public sealed class CountContact : FsmStateAction
{
    internal static int Hits;
    public override void OnEnter() { Hits++; Finish(); }
}

public sealed partial class Checks : MonoBehaviour
{
    public string Output;
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Assembly Mod = typeof(NPCAI.Plugin).Assembly;
    static Type DogType { get { return Mod.GetType("NPCAI.DogAnimations"); } }
    static Type SensesType { get { return Mod.GetType("NPCAI.Senses"); } }
    readonly List<string> report = new List<string>();
    readonly List<GameObject> fixtures = new List<GameObject>();
    static object Get(Type type, object obj, string field) { return type.GetField(field, Flags).GetValue(obj); }
    static void Set(Type type, object obj, string field, object value) { type.GetField(field, Flags).SetValue(obj, value); }
    static object Call(Type type, object obj, string method, params object[] args) { return type.GetMethod(method, Flags).Invoke(obj, args); }
    void Check(bool pass, string description) { report.Add((pass ? "PASS " : "FAIL ") + description); if (!pass) throw new Exception(description); }
    Dictionary<int, object> Registered()
    {
        var dict = (IDictionary)Get(DogType, null, "Dogs");
        var result = new Dictionary<int, object>(); foreach (DictionaryEntry entry in dict) result[(int)entry.Key] = entry.Value;
        return result;
    }
    object Agent(GameObject go)
    {
        var type = Mod.GetType("NPCAI.Senses+Agent"); var agent = Activator.CreateInstance(type, true);
        Set(type, agent, "Owner", go); Set(type, agent, "T", go.transform); Set(type, agent, "Col", go.GetComponent<Collider>());
        Set(type, agent, "State", Enum.Parse(Mod.GetType("NPCAI.Senses+State"), "Idle"));
        return agent;
    }
    PlayMakerFSM Damage(GameObject go)
    {
        var fsm = go.AddComponent<PlayMakerFSM>(); fsm.enabled = false;
        var data = new Fsm { Name = "Damage", StartState = "trigger" };
        data.States = new[] {
            new FsmState(data) { Name = "trigger", Actions = new FsmStateAction[0], Transitions = new[] { new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent("attack"), ToState = "attack" } } },
            new FsmState(data) { Name = "attack", Actions = new FsmStateAction[] { new CountContact() } }
        };
        foreach(var state in data.States) {foreach(var action in state.Actions)action.Init(state);state.SaveActions();}
        fsm.Fsm = data; Probe.Allowed.Add(fsm.GetInstanceID()); fsm.enabled = true; fsm.Fsm.SetState("trigger"); return fsm;
    }

}
