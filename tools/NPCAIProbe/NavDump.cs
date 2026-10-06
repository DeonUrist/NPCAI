using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace NPCAIProbe
{
    // nav.txt: NPCAI's structure maps (found / baking / baked, size, cost, yaw, distance), the bake scheduler's state, every Idle
    // controller's home / patrol state, the Hide-tagged cover objects around the player, and the Azure clock + sun elevation.
    internal static class NavDump
    {
        private static readonly BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Assembly _asm;
        private static object SF(string type, string field) { var t = _asm.GetType(type); var f = t != null ? t.GetField(field, S) : null; return f != null ? f.GetValue(null) : null; }
        private static object IF(object o, string field) { if (o == null) return null; var f = o.GetType().GetField(field, I); return f != null ? f.GetValue(o) : null; }
        private static string F(object v, string fmt) { return v == null ? "-" : v is float ? ((float)v).ToString(fmt) : v.ToString(); }

        internal static void Write(Assembly npcai)
        {
            _asm = npcai;
            var sb = new StringBuilder();
            sb.AppendLine("# NPCAIProbe nav, " + DateTime.Now + "  t=" + Time.time.ToString("0"));
            var player = GameObject.Find("Player");
            if (player == null) return;      // menu / scene unload: keep the last in-world file
            Vector3 pp = player.transform.position;
            sb.AppendLine("player " + (player != null ? pp.ToString() + " layer " + player.layer : "none") + " | " + Azure());
            if (_asm == null) { sb.AppendLine("NPCAI not loaded"); File.WriteAllText(Path.Combine(Plugin.OutDir, "nav.txt"), sb.ToString()); return; }
            try
            {
                var baking = SF("NPCAI.Nav", "_baking");
                sb.AppendLine("scheduler: msPerCell " + F(SF("NPCAI.Nav", "_msPerCell"), "0.000") + " vPeak " + F(SF("NPCAI.Nav", "_vPeak"), "0.0") + " vNow " + F(SF("NPCAI.Nav", "_vNow"), "0.0") + " dtAvg " + F(SF("NPCAI.Nav", "_dtAvg"), "0.0000")
                    + " urgent " + SF("NPCAI.Nav", "_urgent") + " baking " + (baking != null ? IF(baking, "Name") : "none"));
                var list = SF("NPCAI.Nav", "_structures") as IList;
                sb.AppendLine();
                sb.AppendLine("== STRUCTURES (" + (list != null ? list.Count : 0) + ")   dist = flat distance from the player; state = baked / baking phase.progress% / found");
                var rows = new List<KeyValuePair<float, string>>();
                if (list != null)
                    foreach (var s in list)
                    {
                        var root = IF(s, "Root") as Transform;
                        var box = (Bounds)IF(s, "Box");
                        int w = (int)IF(s, "W"), h = (int)IF(s, "H"), n = Math.Max(1, w * h);
                        bool baked = (bool)IF(s, "Baked"); int phase = (int)IF(s, "Phase"), next = (int)IF(s, "Next");
                        var floor = IF(s, "FloorY") as float[];
                        float d = new Vector2(box.center.x - pp.x, box.center.z - pp.z).magnitude;
                        string state = baked ? (floor == null ? "BAKE FAILED" : "baked " + IF(s, "Walkable") + "/" + n + " walkable, " + F(IF(s, "BakeMs"), "0") + " ms / " + IF(s, "BakeFrames") + " frames")
                                             : floor == null ? "found" : (ReferenceEquals(s, baking) ? "BAKING " : "paused ") + "phase " + phase + " " + (100 * next / n) + " %, " + F(IF(s, "BakeMs"), "0") + " ms so far";
                        string rootInfo = root == null ? "ROOT GONE" : (root.gameObject.activeInHierarchy ? "" : "INACTIVE ") + "yaw " + root.eulerAngles.y.ToString("0") + " at " + root.position.x.ToString("0") + "," + root.position.y.ToString("0") + "," + root.position.z.ToString("0") + " parent " + (root.parent != null ? root.parent.name : "-");
                        rows.Add(new KeyValuePair<float, string>(d, "   " + d.ToString("0").PadLeft(5) + " m  " + ((string)IF(s, "Name")).PadRight(22) + " " + w + "x" + h + " cells of " + F(IF(s, "Cell"), "0.00") + " m (" + box.size.x.ToString("0") + "x" + box.size.z.ToString("0") + " m)  " + state + "  | " + rootInfo));
                    }
                rows.Sort((a, b) => a.Key.CompareTo(b.Key));
                foreach (var r in rows) sb.AppendLine(r.Value);
            }
            catch (Exception e) { sb.AppendLine("nav read failed: " + e); }
            try
            {
                sb.AppendLine();
                sb.AppendLine("== IDLE (camp raiders: home / patrol points)");
                var ctl = SF("NPCAI.Idle", "_ctl") as IDictionary;
                var navT = _asm.GetType("NPCAI.Nav");
                var rootAt = navT != null ? navT.GetMethod("StructureRootAt", S) : null;
                var patrol = navT != null ? navT.GetMethod("PatrolPoints", S) : null;
                if (ctl != null)
                    foreach (var c in ctl.Values)
                    {
                        var a = IF(c, "A"); var owner = IF(a, "Owner") as GameObject;
                        if (owner == null) continue;
                        bool hasHome = (bool)IF(c, "HasHome");
                        Vector3 home = (Vector3)IF(c, "Home");
                        string homeMap = "";
                        if (hasHome && rootAt != null)
                        {
                            var r = rootAt.Invoke(null, new object[] { home }) as Transform;
                            homeMap = " home-in " + (r != null ? r.name : "NO STRUCTURE");
                            if (patrol != null)
                            {
                                var pts = new List<Vector3>();
                                int res = (int)patrol.Invoke(null, new object[] { home, pts, 4, 4f, 15f });
                                homeMap += ", PatrolPoints now " + (res < 0 ? "-1 (home not on a baked map: not baked, or its cell has no floor / floor > 2.5 m from home)" : res + " point(s)");
                            }
                        }
                        sb.AppendLine("   " + Plugin.Prefab(owner.name).PadRight(14) + " at " + owner.transform.position.x.ToString("0") + "," + owner.transform.position.y.ToString("0") + "," + owner.transform.position.z.ToString("0")
                            + " | " + (((bool)IF(c, "Excluded")) ? "excluded" : !((bool)IF(c, "Resolved")) ? "unresolved" : ((bool)IF(c, "NoHome")) ? "NO HOME (no camp / no free spawn point)" : ((bool)IF(c, "Forgotten")) ? "home forgotten" : hasHome ? "home " + home.x.ToString("0") + "," + home.y.ToString("0") + "," + home.z.ToString("0") : "no home")
                            + homeMap + " | points " + IF(c, "PointsState") + " tries " + IF(c, "PointsTries") + " leg " + IF(c, "Leg") + (((bool)IF(c, "InSearch")) ? " SEARCHING" : "") + " | agent " + IF(a, "State"));
                    }
            }
            catch (Exception e) { sb.AppendLine("idle read failed: " + e); }
            try
            {
                var hides = GameObject.FindGameObjectsWithTag("Hide");
                int near = hides.Count(h => h != null && (h.transform.position - pp).sqrMagnitude < 300f * 300f);
                sb.AppendLine();
                sb.AppendLine("== HIDE-tagged cover objects: " + hides.Length + " in the world, " + near + " within 300 m" + (near > 0 ? ": " + string.Join(", ", hides.Where(h => h != null && (h.transform.position - pp).sqrMagnitude < 300f * 300f).Take(8).Select(h => Plugin.Prefab(h.name) + " " + (h.transform.position - pp).magnitude.ToString("0") + " m").ToArray()) : ""));
            }
            catch (Exception e) { sb.AppendLine("hide read failed: " + e.Message); }
            File.WriteAllText(Path.Combine(Plugin.OutDir, "nav.txt"), sb.ToString());
        }

        private static Type _azure; private static bool _azureTried;
        internal static string Azure()
        {
            try
            {
                if (!_azureTried) { _azureTried = true; foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { _azure = asm.GetType("UnityEngine.AzureSky.AzureTimeController"); if (_azure != null) break; } }
                if (_azure == null) return "Azure: no type";
                foreach (var o in Resources.FindObjectsOfTypeAll(_azure))
                {
                    var b = o as Behaviour; if (b == null || !b.gameObject.scene.IsValid()) continue;
                    float tl = Convert.ToSingle(_azure.GetMethod("GetTimeline").Invoke(b, null));
                    float se = Convert.ToSingle(_azure.GetMethod("GetSunElevation").Invoke(b, null));
                    float me = Convert.ToSingle(_azure.GetMethod("GetMoonElevation").Invoke(b, null));
                    var sun = RenderSettings.sun;
                    return "Azure time " + tl.ToString("0.00") + " h, sun elevation " + se.ToString("0.00") + ", moon " + me.ToString("0.00") + (sun != null ? ", RenderSettings.sun i=" + sun.intensity.ToString("0.00") + " dir.y=" + (-sun.transform.forward.y).ToString("0.00") : "");
                }
                return "Azure: none in scene";
            }
            catch (Exception e) { return "Azure: " + e.Message; }
        }
    }
}
