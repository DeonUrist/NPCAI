using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

internal static class Program
{
    static string root, game;
    static int checks;
    static readonly string[] Dirs = { "womenofwasteland", "gunplayhud", "gunplay", "npcai" };
    static readonly string[] Names = { "WomenOfWasteland", "GunplayHUD", "Gunplay", "NPCAI" };
    static readonly string[] RangeFields = { "PistolRange", "SmgRange", "RifleRange", "SniperRange", "ShotgunRange", "CrossbowRange" };
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name + ".dll";
        foreach (string dir in new[] { Path.Combine(game, "BepInEx/core"), Path.Combine(game, "Apocalypter_Data/Managed") })
        { var file = Path.Combine(dir, name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
        return null; // Deliberately never resolve absent sibling mods.
    }
    static int Main(string[] args)
    {
        try
        {
            root = Path.GetFullPath(args[0]); game = Path.GetFullPath(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            if (args[2] == "ranges") { InitializePaths(); VerifyRanges(); VerifyMigration(); }
            else if (args[2] == "hooks") VerifyHooks();
            else VerifyCombination(int.Parse(args[2]));
            Console.WriteLine("PASS " + args[2] + ": " + checks + " managed checks; gameplay not executed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static Assembly Load(int index) => Assembly.LoadFrom(Path.Combine(root, Dirs[index], "bin/Release", Names[index] + ".dll"));
    static void InitializePaths()
    {
        // Configure BepInEx's managed test context before its static ConfigFile/Chainloader initialization.
        // All writes remain in this repository; the installed game is only an assembly reference.
        var testRoot = Path.Combine(root, "npcai/.verification/test-runtime");
        Directory.CreateDirectory(testRoot);
        typeof(BepInEx.Paths).GetMethod("SetExecutablePath", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { Assembly.GetExecutingAssembly().Location, testRoot,
                Path.Combine(game, "Apocalypter_Data/Managed"), new string[0] });
    }
    static void VerifyCombination(int mask)
    {
        var guids = new HashSet<string>();
        for (int i = 0; i < Names.Length; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            var a = Load(i);
            Check(a.GetName().Name == Names[i], "Wrong assembly identity");
            foreach (var reference in a.GetReferencedAssemblies())
                Check(!Names.Contains(reference.Name) && reference.Name != "Apocaraider", "Hard sibling/legacy reference: " + reference.Name);
            var types = a.GetTypes(); // Resolve all signature/base-type dependencies, without constructing Unity objects.
            Check(types.Length > 0, "Empty assembly");
            var plugin = a.GetType(Names[i] + ".Plugin", true);
            var metadata = plugin.GetCustomAttributesData().Single(x => x.AttributeType.FullName == "BepInEx.BepInPlugin");
            string guid = (string)metadata.ConstructorArguments[0].Value;
            Check(guids.Add(guid), "GUID collision");
            foreach (var dep in plugin.GetCustomAttributesData().Where(x => x.AttributeType.FullName == "BepInEx.BepInDependency"))
            {
                var dependencyGuid = (string)dep.ConstructorArguments[0].Value;
                if (Dirs.Any(d => dependencyGuid.EndsWith("." + d, StringComparison.Ordinal)))
                    Check(dep.ConstructorArguments.Count > 1 && Convert.ToInt32(dep.ConstructorArguments[1].Value) == 2,
                        "Hard sibling BepInEx dependency");
            }
            if (i == 2)
            {
                var api = a.GetType("Gunplay.Api", true);
                Check(api.GetField("ContractVersion").GetRawConstantValue().Equals(1), "Gunplay contract version");
                Check(api.GetMethod("EffectiveRange").ReturnType == typeof(float), "Range return signature");
                Check(api.GetMethod("TryGetWeaponKind").GetParameters()[1].ParameterType == typeof(int).MakeByRefType(), "Weapon kind signature");
            }
            if (i == 3)
            {
                var api = a.GetType("NPCAI.Api", true);
                Check(api.GetMethod("Shot").GetParameters()[2].ParameterType == typeof(int), "Shot contract uses shared enum");
                Check(api.GetMethods().Count(m => m.Name == "Hurt") == 2, "Hurt overload contract");
                Check(api.GetMethod("SpreadFactor").ReturnType == typeof(float), "Spread signature");
            }
            if (i == 1)
            {
                var api = a.GetType("GunplayHUD.Api", true);
                Check(api.GetMethod("PlayerHit").GetParameters().Length == 4, "HUD PlayerHit signature");
                Check(api.GetMethod("PartHit").GetParameters().Length == 3, "HUD PartHit signature");
            }
        }
    }
    static void VerifyHooks()
    {
        var assemblies = new[] { "Assembly-CSharp", "Assembly-CSharp-firstpass", "Micosmo.SensorToolkit", "PlayMaker" }
            .Select(n => Assembly.LoadFrom(Path.Combine(game, "Apocalypter_Data/Managed", n + ".dll"))).ToArray();
        var methods = new[] {
            "HutongGames.PlayMaker.Actions.CreateObject:OnEnter", "HutongGames.PlayMaker.Actions.Raycast:OnEnter",
            "HutongGames.PlayMaker.Actions.Raycast:DoRaycast", "HutongGames.PlayMaker.Actions.SetFsmFloat:OnEnter",
            "HutongGames.PlayMaker.Actions.GetLayer:OnEnter", "HutongGames.PlayMaker.Actions.SetAudioClip:OnEnter",
            "HutongGames.PlayMaker.Actions.SendEvent:OnEnter", "HutongGames.PlayMaker.Actions.RandomWait:OnEnter",
            "HutongGames.PlayMaker.Actions.SetVelocity:DoSetVelocity", "HutongGames.PlayMaker.Actions.Rotate:DoRotate",
            "HutongGames.PlayMaker.Actions.LookAt:DoLookAt", "HutongGames.PlayMaker.Actions.SmoothLookAt:DoSmoothLookAt",
            "HutongGames.PlayMaker.Actions.AddForce:DoAddForce", "HutongGames.PlayMaker.Actions.AudioPlay:OnEnter",
            "HutongGames.PlayMaker.Actions.GetButton:DoGetButton", "HutongGames.PlayMaker.Actions.GetButtonDown:OnUpdate",
            "HutongGames.PlayMaker.Actions.GetButtonUp:OnUpdate", "Micosmo.SensorToolkit.LOSSensor:OnEnable",
            "Micosmo.SensorToolkit.RangeSensor:OnEnable", "Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit:OnEnter",
            "Micosmo.SensorToolkit.PlayMaker.SensorGetDetections:DoAction", "Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult:OnEnter3D",
            "Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult:OnUpdate3D"
        };
        foreach (var item in methods)
        {
            var parts = item.Split(':');
            var type = assemblies.Select(a => a.GetType(parts[0], false)).FirstOrDefault(t => t != null);
            Check(type != null, "Missing game patch type " + parts[0]);
            MethodInfo method = null;
            for (var t = type; t != null && method == null; t = t.BaseType)
                method = t.GetMethod(parts[1], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Check(method != null, "Missing patch method " + item);
        }
    }
    static void SetRanges(Assembly a, float[] values)
    {
        var p = a.GetType(a.GetName().Name + ".Plugin", true);
        var cfg = new ConfigFile(Path.Combine(root, "npcai/.verification/ranges-" + a.GetName().Name + ".cfg"), false) { SaveOnConfigSet = false };
        for (int i = 0; i < values.Length; i++)
            p.GetField(RangeFields[i], BindingFlags.Static | BindingFlags.NonPublic).SetValue(null,
                cfg.Bind("Tracers", RangeFields[i], values[i], "Test"));
    }
    static void VerifyRanges()
    {
        var old = Assembly.LoadFrom(Path.Combine(root, "npcai/.verification/baseline/bin/Apocaraider.dll"));
        var gun = Load(2); var ai = Load(3);
        var original = old.GetType("Apocaraider.Tracers", true);
        var modern = gun.GetType("Gunplay.Tracers", true);
        var fallback = ai.GetType("NPCAI.WeaponRanges", true);
        var sets = new[] { new float[] { 60,70,120,250,35,90 }, new float[] { 5,1000,123,456,35,90 }, new float[] { -2,0,0.5f,1,2,3 } };
        foreach (var values in sets)
        {
            SetRanges(old, values); SetRanges(gun, values); SetRanges(ai, values);
            for (int kind = 0; kind < 6; kind++)
            {
                float o = Range(original, kind); float g = Range(modern, kind); float f = Range(fallback, kind);
                Check(o == g && g == f, "Range parity kind " + kind + ": " + o + "/" + g + "/" + f);
            }
        }
        foreach (string weapon in new[] { "Crossbow", "slamfire", "slamberg", "rochester", "sniper", "scoped", "redmark", "smg", "borz", "pistol", "revolver", "folk_17", "rifle", "unknown", "SCOPED_SHOTGUN", "crossbow_pistol" })
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            int o = Convert.ToInt32(original.GetMethod("Classify", flags).Invoke(null, new object[] { weapon }));
            int g = Convert.ToInt32(modern.GetMethod("Classify", flags).Invoke(null, new object[] { weapon }));
            int f = Convert.ToInt32(fallback.GetMethod("Classify", flags).Invoke(null, new object[] { weapon }));
            Check(o == g && g == f, "Classifier parity " + weapon);
        }
        // Exercise the actual optional delegate path with deliberately conflicting fallback values.
        var gunApi = gun.GetType("Gunplay.Api", true);
        var provider = (Func<int, float>)Delegate.CreateDelegate(typeof(Func<int, float>), gunApi.GetMethod("EffectiveRange"));
        var providerField = fallback.GetField("_range", BindingFlags.Static | BindingFlags.NonPublic);
        providerField.SetValue(null, provider);
        SetRanges(gun, new float[] { 66,77,133,299,44,99 });
        SetRanges(ai, new float[] { 5,5,5,5,5,5 });
        for (int kind = 0; kind < 6; kind++)
            Check(Range(fallback, kind) == provider(kind), "Gunplay authoritative delegate path " + kind);
        providerField.SetValue(null, new Func<int,float>(_ => { throw new InvalidOperationException("Unavailable provider"); }));
        for (int kind = 0; kind < 6; kind++) Check(Range(fallback, kind) == 5f, "Failed provider must fall back " + kind);
        providerField.SetValue(null, null);
    }
    static void VerifyMigration()
    {
        Directory.CreateDirectory(BepInEx.Paths.ConfigPath);
        var oldPath = Path.Combine(BepInEx.Paths.ConfigPath, "com.denis.apocalypter.apocaraider.cfg");
        const string text = "# legacy\n[General]\nApocasetter = false\nFemalePopulation = 73\n[Hud]\nFloatingDamage = 1\n[Tracers]\nPistolRange = 88\n[Detection]\nSightRange = 142\n";
        File.WriteAllText(oldPath, text);
        for (int i = 0; i < 4; i++)
        {
            var a = Load(i);
            var cfg = new ConfigFile(Path.Combine(BepInEx.Paths.ConfigPath, Names[i] + ".cfg"), false) { SaveOnConfigSet = false };
            var opt = cfg.Bind("General", "Apocasetter", true, "opt in");
            var section = new[] { "General", "Hud", "Tracers", "Detection" }[i];
            var key = new[] { "FemalePopulation", "FloatingDamage", "PistolRange", "SightRange" }[i];
            var value = cfg.Bind(section, key, 2, "migration");
            var method = a.GetType(Names[i] + ".LegacyConfig", true).GetMethod("Import", BindingFlags.NonPublic | BindingFlags.Static);
            var log = new BepInEx.Logging.ManualLogSource("Verification");
            method.Invoke(null, new object[] { cfg, log, false });
            Check(value.Value == new[] { 73,1,88,142 }[i], "First-run migration " + Names[i]);
            Check(opt.Value, "Migration must preserve new mod opt in");
            value.Value = 9;
            method.Invoke(null, new object[] { cfg, log, true });
            Check(value.Value == 9, "Existing new config must win");
            Check(File.ReadAllText(oldPath) == text, "Legacy config was modified");
        }
    }
    static float Range(Type type, int kind)
    {
        var method = type.GetMethod("RangeOf", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var value = Enum.ToObject(method.GetParameters()[0].ParameterType, kind);
        return (float)method.Invoke(null, new[] { value });
    }
}
