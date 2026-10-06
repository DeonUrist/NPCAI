using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace NPCAIProbe
{
    // perf.log (0.1.6): where the frame time goes, every 5 s - the whole frame (average / worst / frames over 33 ms), each NPCAI subsystem
    // (Senses, Nav, Brain, Idle, Passthrough Tick and Brain.LateTick, timed by Harmony prefix/postfix pairs: average and worst per call,
    // ms spent per second), the rigged gunmen (PlayableGraphs alive) and Nav's own status line. The Animator's work is not callable from
    // here: it is the part of the frame left after subtracting the subsystems, so a frame that gets worse with the rig count and not with
    // the subsystems points at the animation layer, a frame that tracks Nav's "baking" at the nav bake.
    internal static class Perf
    {
        private sealed class Bucket { public string Name; public Stopwatch Sw = new Stopwatch(); public double Sum, Max; public int Calls; public double SumAll; public int CallsAll; }
        private static readonly Dictionary<string, Bucket> _b = new Dictionary<string, Bucket>();
        private static readonly List<Bucket> _order = new List<Bucket>();
        private static float _frameSum, _frameMax; private static int _frames, _slow, _verySlow;
        private static float _next; private static bool _patched; private static readonly StringBuilder _sb = new StringBuilder();

        internal static void TryPatch(Harmony h)
        {
            if (_patched) return;
            var asm = Health.NpcaiAssembly;
            if (asm == null) return;
            _patched = true;
            foreach (var pair in new[] { "Senses.Tick", "Nav.Tick", "Brain.Tick", "Idle.Tick", "Passthrough.Tick", "Brain.LateTick", "Aim.Sweep",
                                         // (0.1.6b) inside them: the suspects for the 30-130 ms spikes seen in Brain.Tick / Idle.Tick
                                         "Nav.Next", "Nav.SearchPoints", "Nav.PatrolPoints", "Nav.CoverSpot", "Nav.HopAhead", "Nav.BodyPathClear", "Nav.StructureRootAt", "Nav.Pick", "Nav.BakeStep",
                                         "Idle.Think", "Idle.SearchThink", "Idle.InvestigateThink", "Idle.Resolve", "Idle.Failed", "Idle.Arrived", "Idle.LegThink", "Idle.Hop",
                                         "Brain.Think", "Brain.CoverThink", "Brain.MoveToward", "Brain.TryRig", "Brain.Drive", "Brain.Hit", "Senses.Guess", "Senses.StartFlank", "Senses.Assign", "WeaponRanges.WeaponOf" })
            {
                try
                {
                    int dot = pair.IndexOf('.');
                    var t = asm.GetType("NPCAI." + pair.Substring(0, dot));
                    var m = t != null ? AccessTools.Method(t, pair.Substring(dot + 1)) : null;
                    if (m == null) { Plugin.Log.LogWarning("Perf: no method NPCAI." + pair); continue; }
                    var b = new Bucket { Name = pair }; _b[pair] = b; _order.Add(b);
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Perf), nameof(Before)), postfix: new HarmonyMethod(typeof(Perf), nameof(After)));
                }
                catch (Exception e) { Plugin.Log.LogError("Perf: " + pair + ": " + e.Message); }
            }
            Plugin.Log.LogInfo("Perf: timing " + _order.Count + " NPCAI subsystems -> perf.log");
        }

        private static Bucket Of(MethodBase m)
        {
            Bucket b;
            return m != null && m.DeclaringType != null && _b.TryGetValue(m.DeclaringType.Name + "." + m.Name, out b) ? b : null;
        }
        public static void Before(MethodBase __originalMethod) { var b = Of(__originalMethod); if (b != null) b.Sw.Start(); }
        public static void After(MethodBase __originalMethod)
        {
            var b = Of(__originalMethod); if (b == null) return;
            b.Sw.Stop(); double ms = b.Sw.Elapsed.TotalMilliseconds; b.Sw.Reset();
            b.Sum += ms; b.Calls++; if (ms > b.Max) b.Max = ms;
            if (ms > 8.0 && _spikes < 400) { _spikes++; _spikeBuf.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" SPIKE ").Append(b.Name).Append(' ').Append(ms.ToString("0.0")).Append(" ms").AppendLine(); }
        }
        private static int _spikes; private static readonly StringBuilder _spikeBuf = new StringBuilder();

        internal static void Frame(float now)
        {
            float dt = Time.unscaledDeltaTime * 1000f;
            _frameSum += dt; _frames++; if (dt > _frameMax) _frameMax = dt; if (dt > 33f) _slow++; if (dt > 66f) _verySlow++;
            if (now < _next) return;
            float period = _next > 0f ? 5f : 0f; _next = now + 5f;
            if (_frames == 0 || period == 0f) { Reset(); return; }
            try
            {
                _sb.Length = 0;
                _sb.Append(DateTime.Now.ToString("HH:mm:ss")).Append(" frame avg ").Append((_frameSum / _frames).ToString("0.0")).Append(" ms (").Append((1000f * _frames / _frameSum).ToString("0")).Append(" fps) worst ")
                   .Append(_frameMax.ToString("0")).Append(" ms, >33ms: ").Append(_slow).Append(", >66ms: ").Append(_verySlow).Append(" of ").Append(_frames);
                _sb.Append(" | rigs ").Append(Rigs()).Append(" npcs ").Append(Npcs());
                foreach (var b in _order)
                {
                    if (b.Calls == 0) continue;
                    _sb.Append(" | ").Append(b.Name).Append(' ').Append((b.Sum / b.Calls).ToString("0.00")).Append("/").Append(b.Max.ToString("0.0")).Append(" ms, ").Append((b.Sum / period).ToString("0.0")).Append(" ms/s");
                    b.SumAll += b.Sum; b.CallsAll += b.Calls; b.Sum = b.Max = 0; b.Calls = 0;
                }
                _sb.Append(" | ").Append(Nav());
                _sb.AppendLine();
                if (_spikeBuf.Length > 0) { _sb.Append(_spikeBuf); _spikeBuf.Length = 0; }
                File.AppendAllText(Path.Combine(Plugin.OutDir, "perf.log"), _sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogError("Perf: " + e); }
            Reset();
        }

        private static void Reset() { _frameSum = _frameMax = 0f; _frames = _slow = _verySlow = 0; }

        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static IDictionary NpcDict()
        {
            var asm = Health.NpcaiAssembly; if (asm == null) return null;
            var t = asm.GetType("NPCAI.Brain"); var f = t != null ? t.GetField("_npcs", S) : null;
            return f != null ? f.GetValue(null) as IDictionary : null;
        }
        private static string Npcs() { var d = NpcDict(); return d == null ? "?" : d.Count.ToString(); }
        private static string Rigs()
        {
            var d = NpcDict(); if (d == null) return "?";
            int rigs = 0, on = 0;
            foreach (var v in d.Values)
            {
                var rf = v.GetType().GetField("Rig", I); var rig = rf != null ? rf.GetValue(v) : null;
                if (rig == null) continue;
                rigs++;
                try { var gf = rig.GetType().GetField("G", I); var g = (UnityEngine.Playables.PlayableGraph)gf.GetValue(rig); if (g.IsValid()) on++; } catch (Exception) { }
            }
            return rigs + " (" + on + " playing)";
        }
        private static string Nav()
        {
            try
            {
                var asm = Health.NpcaiAssembly; var t = asm != null ? asm.GetType("NPCAI.Nav") : null;
                var m = t != null ? t.GetMethod("Status", S) : null;
                return m != null ? "Nav: " + (string)m.Invoke(null, null) : "Nav: ?";
            }
            catch (Exception) { return "Nav: ?"; }
        }
    }
}
