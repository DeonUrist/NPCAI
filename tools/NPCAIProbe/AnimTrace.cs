using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace NPCAIProbe
{
    // anim.log: what every human gunman near the camera shows, 4 times a second, written only when something changed for him:
    //   who / where he is for NPCAI (registered? mode, rig on, the clip + pose + speed NPCAI plays, the gun it holds + its parent + local pose)
    //   what the game does underneath (Attack / Movement FSM states, the Animator controller's current clip, animator speed, root motion)
    //   the truth in the scene: every child of mixamorig:LeftHand / RightHand with its active flag, the body's planar speed, crouch
    // Reads NPCAI's Brain._npcs by reflection (private fields of Npc / NpcAnim.Rig), so a renamed field shows as "?" rather than breaking.
    internal static class AnimTrace
    {
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<int, string> _last = new Dictionary<int, string>();
        private static readonly StringBuilder _buf = new StringBuilder();
        private static float _next;
        private static int _lines;

        internal static void Pump(float now)
        {
            if (now < _next) return;
            _next = now + 0.25f;
            try { Sample(); } catch (Exception e) { Plugin.Log.LogError("Probe anim: " + e); }
        }

        private static object IF(object o, string f) { if (o == null) return null; var fi = o.GetType().GetField(f, I); return fi != null ? fi.GetValue(o) : null; }
        private static string IS(object o, string f) { var v = IF(o, f); return v == null ? "?" : v is float ? ((float)v).ToString("0.00") : v.ToString(); }

        private static void Sample()
        {
            var asm = Health.NpcaiAssembly;
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 cp = cam.transform.position;
            IDictionary npcs = null;
            if (asm != null)
            {
                var brain = asm.GetType("NPCAI.Brain");
                var f = brain != null ? brain.GetField("_npcs", S) : null;
                npcs = f != null ? f.GetValue(null) as IDictionary : null;
            }
            // every gunman with a humanoid Animator within 40 m
            foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>())
            {
                if (a == null || a.avatar == null || !a.avatar.isHuman) continue;
                var root = a.transform.root;
                if ((root.position - cp).sqrMagnitude > 40f * 40f) continue;
                if (root.GetComponent<Rigidbody>() == null) continue;              // the player's body, props
                var hands = Hands(root);
                if (hands.Length == 0) continue;
                var sb = new StringBuilder();
                sb.Append(root.name).Append(" @").Append(((root.position - cp).magnitude).ToString("0")).Append("m | ");
                // NPCAI's view
                object n = null;
                if (npcs != null) { int id = root.gameObject.GetInstanceID(); if (npcs.Contains(id)) n = npcs[id]; }
                if (n == null) sb.Append("NPCAI: not registered");
                else
                {
                    sb.Append("mode=").Append(IS(n, "Mode")).Append(" ranged=").Append(IS(n, "Ranged")).Append(" crouch=").Append(IS(n, "Crouched")).Append(" strafing=").Append(IS(n, "Strafing"))
                      .Append(" cmd=").Append(IS(n, "CmdSpeed")).Append(" cap=").Append(IS(n, "SpeedCap")).Append(" dev=").Append(IS(n, "StrafeDev")).Append(" dirty=").Append(IS(n, "WeaponDirty"));
                    var rig = IF(n, "Rig");
                    if (rig == null) sb.Append(" rig=none(tried ").Append(IS(n, "RigTried")).Append(")");
                    else
                    {
                        bool on = false; try { var g = (UnityEngine.Playables.PlayableGraph)IF(rig, "G"); on = g.IsValid() && g.IsPlaying(); } catch (Exception) { }
                        sb.Append(" rig=").Append(on ? "ON" : "off").Append(" clip=").Append(IS(rig, "Clip")).Append(" pose=").Append(IS(rig, "Pose")).Append(" x").Append(IS(rig, "Speed"))
                          .Append(" moved=").Append(IS(rig, "WeaponMoved"));
                        var w = IF(n, "Weapon") as Transform;
                        sb.Append(" gun=").Append(w == null ? "null" : w.name + (w.gameObject.activeInHierarchy ? "" : "(INACTIVE)") + " under " + (w.parent != null ? w.parent.name : "-") + " lp" + w.localPosition.ToString("0.00") + " lr" + w.localEulerAngles.ToString("0"));
                        var rw = IF(rig, "Weapon") as Transform;
                        if (rw != w) sb.Append(" rig.gun=").Append(rw == null ? "null" : rw.name);
                    }
                }
                // the game underneath
                sb.Append(" | FSM attack=").Append(FsmState(root, "Attack")).Append(" move=").Append(FsmState(root, "Movement")).Append(" weap=").Append(FsmState(root, "WeaponType"));
                sb.Append(" | anim ctrl=").Append(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "none").Append(" clip=").Append(CtrlClip(a)).Append(" speed=").Append(a.speed.ToString("0.0"))
                  .Append(" rootMotion=").Append(a.applyRootMotion).Append(" enabled=").Append(a.enabled);
                var rb = root.GetComponent<Rigidbody>();
                if (rb != null) { var v = rb.velocity; v.y = 0f; sb.Append(" | v=").Append(v.magnitude.ToString("0.0")).Append(" local=").Append(root.InverseTransformDirection(v).ToString("0.0")); }
                // the hands
                sb.Append(" | hands:");
                foreach (var h in hands)
                {
                    sb.Append(' ').Append(h.name.Replace("mixamorig:", "")).Append('[');
                    for (int i = 0; i < h.childCount; i++) { var c = h.GetChild(i); if (i > 0) sb.Append(','); sb.Append(c.name).Append(c.gameObject.activeInHierarchy ? "" : "-off"); }
                    sb.Append(']');
                }
                string line = sb.ToString();
                int key = root.gameObject.GetInstanceID();
                string prev;
                if (_last.TryGetValue(key, out prev) && prev == line) continue;
                _last[key] = line;
                _buf.Append(Time.time.ToString("0.00")).Append(' ').Append(line).AppendLine();
                if (++_lines % 20 == 0) Flush();
            }
            if (_buf.Length > 0) Flush();
        }

        private static Transform[] Hands(Transform root)
        {
            var list = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith("LeftHand") || t.name.EndsWith("RightHand")) list.Add(t);
            return list.ToArray();
        }

        private static string FsmState(Transform root, string fsmName)
        {
            foreach (var f in root.GetComponents<PlayMakerFSM>())
                if (f != null && f.FsmName == fsmName) return f.Fsm != null && f.Fsm.Initialized ? (f.enabled ? "" : "(disabled)") + f.Fsm.ActiveStateName : "uninit";
            return "-";
        }

        private static string CtrlClip(Animator a)
        {
            try
            {
                if (a.runtimeAnimatorController == null || a.layerCount == 0) return "-";
                var sb = new StringBuilder();
                for (int l = 0; l < a.layerCount; l++)
                {
                    var info = a.GetCurrentAnimatorClipInfo(l);
                    if (l > 0) sb.Append('/');
                    sb.Append(info.Length > 0 && info[0].clip != null ? info[0].clip.name : "-");
                    if (l > 0) sb.Append("(w").Append(a.GetLayerWeight(l).ToString("0.0")).Append(')');
                }
                return sb.ToString();
            }
            catch (Exception) { return "?"; }
        }

        internal static void Flush()
        {
            if (_buf.Length == 0) return;
            try { File.AppendAllText(Path.Combine(Plugin.OutDir, "anim.log"), _buf.ToString()); } catch (Exception) { }
            _buf.Length = 0;
        }
    }
}
