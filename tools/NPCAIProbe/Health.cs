using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using UnityEngine;

namespace NPCAIProbe
{
    // NPCAI's internal state, read by reflection every WriteSeconds: the size of every static collection (first / max / now -> leaks),
    // the senses' bookkeeping (agents holding a ghost that no longer exists, Combat without a target, ...) and the modules' Status().
    internal static class Health
    {
        private sealed class Size { public int First = -1, Max, Now; }
        private static readonly SortedDictionary<string, Size> _sizes = new SortedDictionary<string, Size>(StringComparer.Ordinal);
        internal static string Last = "(no snapshot yet)";
        internal static readonly List<string> Problems = new List<string>();     // every inconsistency ever seen (deduplicated)
        private static readonly HashSet<string> _problemSet = new HashSet<string>();
        private static Assembly _asm; private static bool _tried;
        private static readonly BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static Assembly NpcaiAssembly { get { return Asm(); } }
        private static Assembly Asm()
        {
            if (_asm != null || _tried) return _asm;
            BepInEx.PluginInfo pi;
            if (Chainloader.PluginInfos.TryGetValue("com.denis.apocalypter.npcai", out pi) && !ReferenceEquals(pi.Instance, null)) _asm = pi.Instance.GetType().Assembly;   // the manager object is destroyed in this game: Unity's == null is true, the C# object is still there
            _tried = _asm != null || Time.unscaledTime > 60f;
            return _asm;
        }

        private static object SF(string type, string field) { var t = _asm.GetType(type); var f = t != null ? t.GetField(field, S) : null; return f != null ? f.GetValue(null) : null; }
        private static object IF(object o, string field) { if (o == null) return null; var f = o.GetType().GetField(field, I); return f != null ? f.GetValue(o) : null; }
        private static string Call(string type, string method)
        {
            try { var t = _asm.GetType(type); var m = t != null ? t.GetMethod(method, S) : null; return m != null ? (string)m.Invoke(null, null) : "-"; }
            catch (Exception e) { return "! " + e.Message; }
        }
        private static float CallF(string type, string method)
        {
            try { var t = _asm.GetType(type); var m = t != null ? t.GetMethod(method, S, null, Type.EmptyTypes, null) : null; return m != null ? Convert.ToSingle(m.Invoke(null, null)) : -1f; }
            catch (Exception) { return -1f; }
        }
        private static bool Dead(object o) { var u = o as UnityEngine.Object; return o != null && u == null; }   // Unity-destroyed (fake null)

        private static void Problem(string s)
        {
            if (_problemSet.Add(s)) { Problems.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s); Plugin.Log.LogWarning("NPCAIProbe: " + s); }
        }

        internal static void Snapshot()
        {
            if (Asm() == null) { Last = "NPCAI not loaded"; return; }
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append("  t=").Append(Time.time.ToString("0")).Append("  ");
            sb.Append("light ").Append(CallF("NPCAI.Senses", "LightLevel").ToString("0.00")).Append(" sight ").Append(CallF("NPCAI.Senses", "SightRange").ToString("0")).Append(" m | ").Append(NavDump.Azure()).Append(" | ");
            sb.Append("Senses: ").Append(Call("NPCAI.Senses", "Status")).Append(" | Brain: ").Append(Call("NPCAI.Brain", "Status")).Append(" | Nav: ").Append(Call("NPCAI.Nav", "Status"));

            // senses bookkeeping
            try
            {
                var agents = SF("NPCAI.Senses", "_agents") as IDictionary;
                var ghosts = SF("NPCAI.Senses", "_ghosts") as IList;
                var persist = _asm.GetType("NPCAI.Senses+Persist");
                var restoring = persist != null ? persist.GetField("_restoring", S) : null;
                bool isRestoring = restoring != null && (bool)restoring.GetValue(null);
                var states = new SortedDictionary<string, int>();
                int deadGhost = 0, lostGhost = 0, noGhost = 0, deadTarget = 0, seated = 0;
                if (agents != null)
                    foreach (var a in agents.Values)
                    {
                        var owner = IF(a, "Owner") as GameObject;
                        if (owner == null) continue;
                        string st = Convert.ToString(IF(a, "State"));
                        int n; states.TryGetValue(st, out n); states[st] = n + 1;
                        if (owner.transform.parent != null) seated++;
                        var g = IF(a, "Ghost"); var target = IF(a, "Target");
                        string who = Plugin.Prefab(owner.name) + "#" + owner.GetInstanceID();
                        if ((st == "Investigate" || st == "Search") && g == null) { noGhost++; Problem("agent " + who + " in " + st + " without a ghost"); }
                        if (g != null)
                        {
                            var obj = IF(g, "Obj");
                            if (obj == null || Dead(obj)) { deadGhost++; Problem("agent " + who + " (" + st + ") holds ghost #" + IF(g, "Id") + " whose object is destroyed -> its Detection target is null" + (isRestoring ? " (restore running)" : "")); }
                            else if (ghosts != null && !ghosts.Contains(g)) { lostGhost++; Problem("agent " + who + " (" + st + ") holds ghost #" + IF(g, "Id") + " that is no longer in the ghost list"); }
                        }
                        if (st == "Combat" && (target == null || Dead(target))) { deadTarget++; Problem("agent " + who + " in Combat with a destroyed/null target (cleared on its next look)"); }
                    }
                int noHolders = 0;
                if (ghosts != null) foreach (var g in ghosts) { var h = IF(g, "Holders") as IList; if (h != null && h.Count == 0) noHolders++; }
                sb.Append(" | agents ").Append(string.Join(" ", states.Select(kv => kv.Key + ":" + kv.Value).ToArray())).Append(" seated:").Append(seated)
                  .Append(" | ghosts ").Append(ghosts != null ? ghosts.Count : -1).Append(" (unheld ").Append(noHolders).Append(isRestoring ? ", restoring" : "").Append(")");
                if (deadGhost + lostGhost + noGhost + deadTarget > 0) sb.Append(" | BAD deadGhost ").Append(deadGhost).Append(" lostGhost ").Append(lostGhost).Append(" noGhost ").Append(noGhost).Append(" deadTarget ").Append(deadTarget);
            }
            catch (Exception e) { sb.Append(" | senses read failed: ").Append(e.Message); }

            // brain modes
            try
            {
                var npcs = SF("NPCAI.Brain", "_npcs") as IDictionary;
                if (npcs != null)
                {
                    var modes = new SortedDictionary<string, int>();
                    foreach (var n in npcs.Values) { if (IF(n, "Owner") as GameObject == null) continue; string m = Convert.ToString(IF(n, "Mode")); int c; modes.TryGetValue(m, out c); modes[m] = c + 1; }
                    sb.Append(" | brain ").Append(string.Join(" ", modes.Select(kv => kv.Key + ":" + kv.Value).ToArray()));
                }
            }
            catch (Exception e) { sb.Append(" | brain read failed: ").Append(e.Message); }

            // every static collection in NPCAI
            try
            {
                foreach (var t in _asm.GetTypes())
                    foreach (var f in t.GetFields(S))
                    {
                        if (f.IsLiteral || f.FieldType.IsPrimitive || f.FieldType == typeof(string)) continue;
                        object v; try { v = f.GetValue(null); } catch { continue; }
                        int count;
                        var col = v as ICollection;
                        if (col != null) count = col.Count;
                        else
                        {
                            if (v == null || v is Array) continue;
                            var cp = v.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                            if (cp == null || cp.PropertyType != typeof(int)) continue;
                            count = (int)cp.GetValue(v, null);
                        }
                        string key = t.FullName.Replace("NPCAI.", "") + "." + f.Name;
                        Size s; if (!_sizes.TryGetValue(key, out s)) _sizes[key] = s = new Size();
                        if (s.First < 0) s.First = count;
                        s.Now = count; if (count > s.Max) s.Max = count;
                    }
            }
            catch (Exception e) { sb.Append(" | sizes failed: ").Append(e.Message); }

            Last = sb.ToString();
            File.AppendAllText(Path.Combine(Plugin.OutDir, "health.log"), Last + Environment.NewLine);
        }

        internal static void WriteSizes(StringBuilder sb)
        {
            sb.AppendLine("   collection".PadRight(48) + "first     max     now   (a 'now' that only grows over a long session = a leak)");
            foreach (var kv in _sizes)
                sb.AppendLine("   " + kv.Key.PadRight(45) + kv.Value.First.ToString().PadLeft(6) + kv.Value.Max.ToString().PadLeft(8) + kv.Value.Now.ToString().PadLeft(8));
        }
    }
}
