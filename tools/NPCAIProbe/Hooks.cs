using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;

namespace NPCAIProbe
{
    // NPCAI's Harmony hooks: installed? and which other mods patch the same methods (a prefix returning false there changes what NPCAI sees)
    internal static class Hooks
    {
        private sealed class H { public Type T; public string M; public Type[] Args; public string Kind; public string Why; }
        private static readonly H[] Expected =
        {
            new H { T = typeof(SetVelocity), M = "DoSetVelocity", Kind = "prefix", Why = "brain pedal" },
            new H { T = typeof(Rotate), M = "DoRotate", Kind = "prefix", Why = "no random yaw" },
            new H { T = typeof(Raycast), M = "DoRaycast", Kind = "prefix", Why = "bumper rays clear" },
            new H { T = typeof(LookAt), M = "DoLookAt", Kind = "prefix", Why = "turn rate" },
            new H { T = typeof(SmoothLookAt), M = "DoSmoothLookAt", Kind = "prefix", Why = "melee chase facing" },
            new H { T = typeof(SendEvent), M = "OnEnter", Kind = "prefix", Why = "Animal_Run/rotateRandom + Aim Activate" },
            new H { T = typeof(AddForce), M = "DoAddForce", Kind = "prefix", Why = "no Unstuck hop" },
            new H { T = typeof(Micosmo.SensorToolkit.LOSSensor), M = "OnEnable", Kind = "postfix", Why = "sensor interval" },
            new H { T = typeof(Micosmo.SensorToolkit.RangeSensor), M = "OnEnable", Kind = "postfix", Why = "sensor interval" },
            new H { T = typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetDetections), M = "DoAction", Kind = "prefix", Why = "Senses answers Detection" },
            new H { T = typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult), M = "OnEnter3D", Kind = "prefix", Why = "burst LOS" },
            new H { T = typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetLineOfSightResult), M = "OnUpdate3D", Kind = "prefix", Why = "burst LOS" },
            new H { T = typeof(AudioPlay), M = "OnEnter", Kind = "prefix", Why = "taunt shout, horn" },
            new H { T = typeof(CreateObject), M = "OnEnter", Kind = "postfix", Why = "blasts, camps, Idle homes" },
            new H { T = typeof(GetButton), M = "DoGetButton", Kind = "prefix", Why = "shout modifier blocks" },
            new H { T = typeof(GetButtonDown), M = "OnUpdate", Kind = "prefix", Why = "shout modifier blocks" },
            new H { T = typeof(GetButtonUp), M = "OnUpdate", Kind = "prefix", Why = "shout modifier blocks" },
            new H { T = typeof(RandomWait), M = "OnEnter", Kind = "prefix", Why = "Aim pause" },
            new H { T = typeof(Micosmo.SensorToolkit.PlayMaker.SensorGetDetectionRayHit), M = "OnEnter", Kind = "prefix", Why = "NPC shot observer (no Gunplay projectiles)" },
            new H { T = typeof(Raycast), M = "OnEnter", Kind = "prefix", Why = "player shot observer" },
            new H { T = typeof(SetFsmFloat), M = "OnEnter", Kind = "postfix", Why = "hurt observer" },
        };

        private static string Owners(System.Collections.ObjectModel.ReadOnlyCollection<Patch> l, string kind)
        {
            return l == null ? "" : string.Join(" ", l.Select(p => kind + ":" + p.owner + (p.priority != 400 ? "(prio " + p.priority + ")" : "")).ToArray());
        }

        internal static void Check()
        {
            foreach (var h in Expected)
            {
                string id = "H " + h.T.Name + "." + h.M;
                MethodBase m = AccessTools.Method(h.T, h.M);
                if (m == null) { Report.Hook(id, false, "method not found", h.Why); continue; }
                var info = Harmony.GetPatchInfo(m);
                var list = info == null ? new List<Patch>() : info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).ToList();
                bool ours = list.Any(p => p.owner != null && p.owner.StartsWith("com.denis.apocalypter.npcai.", StringComparison.Ordinal)
                    && ((h.Kind == "prefix" && info.Prefixes.Contains(p)) || (h.Kind == "postfix" && info.Postfixes.Contains(p))));
                string all = info == null ? "no patches" : (Owners(info.Prefixes, "pre") + " " + Owners(info.Postfixes, "post") + " " + Owners(info.Finalizers, "fin")).Trim();
                if (m.DeclaringType != h.T) all = "declared on " + m.DeclaringType.Name + " | " + all;
                Report.Hook(id, ours, all, h.Why);
            }
        }
    }
}
