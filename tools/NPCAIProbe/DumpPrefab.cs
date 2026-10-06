using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace NPCAIProbe
{
    // prefabs\<Prefab>.txt: every FSM on the NPC and its children with every action's public fields (ApocaFsmInspector's detail format),
    // the SensorToolkit sensors' settings and the Animator's clips, from the first live instance.
    internal static class DumpPrefab
    {
        internal static void Write(GameObject go, string prefab)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + prefab + " (" + Plugin.PathOf(go.transform) + "), " + DateTime.Now + ", tag " + go.tag + ", layer " + go.layer);
            foreach (var fsm in go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                try { WriteFsm(sb, fsm); } catch (Exception e) { sb.AppendLine("   !! " + e.Message); }
            }
            sb.AppendLine();
            sb.AppendLine("== SENSORS");
            foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null) continue;
                string tn = b.GetType().Name;
                if (!tn.EndsWith("Sensor", StringComparison.Ordinal)) continue;
                sb.AppendLine("   " + Plugin.PathOf(b.transform) + "  " + tn + (b.enabled ? "" : " (disabled)"));
                foreach (var f in b.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v; try { v = f.GetValue(b); } catch (Exception e) { v = "!" + e.Message; }
                    sb.AppendLine("        " + f.Name + " = " + Deep(v, 0));
                }
            }
            sb.AppendLine();
            sb.AppendLine("== ANIMATORS");
            foreach (var a in go.GetComponentsInChildren<Animator>(true))
            {
                var c = a.runtimeAnimatorController;
                sb.AppendLine("   " + Plugin.PathOf(a.transform) + " controller " + (c != null ? c.name : "none") + " clips: " + (c != null ? string.Join(", ", c.animationClips.Select(x => x.name + " " + x.length.ToString("0.00") + "s" + (x.isLooping ? " loop" : "")).Distinct().ToArray()) : ""));
            }
            sb.AppendLine();
            sb.AppendLine("== COLLIDERS");
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                sb.AppendLine("   " + Plugin.PathOf(col.transform) + " " + col.GetType().Name + " layer " + col.gameObject.layer + (col.isTrigger ? " trigger" : "") + (col.enabled ? "" : " (disabled)"));
            string name = new string(prefab.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray());
            File.WriteAllText(Path.Combine(Path.Combine(Plugin.OutDir, "prefabs"), name + ".txt"), sb.ToString());
        }

        private static string Deep(object v, int depth)
        {
            if (v == null) return "null";
            if (v is string || v.GetType().IsPrimitive || v is Enum) return v.ToString();
            var uo = v as UnityEngine.Object;
            if (uo != null) return uo.name + " (" + v.GetType().Name + ")";
            if (v is LayerMask) return "mask " + ((LayerMask)v).value;
            var en = v as IEnumerable;
            if (en != null) { var l = new List<string>(); foreach (var e in en) { l.Add(Deep(e, depth + 1)); if (l.Count > 30) { l.Add("..."); break; } } return "[" + string.Join(", ", l.ToArray()) + "]"; }
            if (depth >= 2 || v.GetType().Namespace == "UnityEngine") return v.ToString();
            var parts = new List<string>();
            foreach (var f in v.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (f.Name.Contains("<")) continue;
                object x; try { x = f.GetValue(v); } catch { x = "?"; }
                parts.Add(f.Name + "=" + Deep(x, depth + 1));
                if (parts.Count > 20) break;
            }
            return "{" + string.Join(", ", parts.ToArray()) + "}";
        }

        private static void WriteFsm(StringBuilder sb, PlayMakerFSM fsm)
        {
            bool init = fsm.Fsm != null && fsm.Fsm.Initialized;
            sb.AppendLine();
            sb.AppendLine("== " + Plugin.PathOf(fsm.transform) + "  [" + fsm.FsmName + "]  enabled=" + fsm.enabled + " init=" + init + " state=" + (init ? fsm.ActiveStateName : ""));
            var vars = fsm.FsmVariables;
            if (vars != null)
            {
                foreach (var v in vars.FloatVariables) sb.AppendLine("   float  " + v.Name + " = " + v.Value);
                foreach (var v in vars.IntVariables) sb.AppendLine("   int    " + v.Name + " = " + v.Value);
                foreach (var v in vars.BoolVariables) sb.AppendLine("   bool   " + v.Name + " = " + v.Value);
                foreach (var v in vars.StringVariables) sb.AppendLine("   string " + v.Name + " = \"" + v.Value + "\"");
                foreach (var v in vars.Vector3Variables) sb.AppendLine("   vec3   " + v.Name + " = " + v.Value);
                foreach (var v in vars.GameObjectVariables) sb.AppendLine("   go     " + v.Name + " = " + (v.Value != null ? v.Value.name : "null"));
                foreach (var v in vars.ObjectVariables) sb.AppendLine("   obj    " + v.Name + " = " + (v.Value != null ? v.Value.name + " (" + v.Value.GetType().Name + ")" : "null"));
                foreach (var v in vars.ArrayVariables) sb.AppendLine("   array  " + v.Name + " [" + v.Length + "] " + v.ElementType);
            }
            var gt = fsm.FsmGlobalTransitions;
            if (gt != null && gt.Length > 0) sb.AppendLine("   globalTransitions: " + string.Join(", ", gt.Select(t => t.EventName + "->" + t.ToState).ToArray()));
            if (fsm.FsmStates == null) return;
            foreach (var st in fsm.FsmStates)
            {
                sb.AppendLine("   state " + st.Name);
                sb.AppendLine("        transitions: " + string.Join(", ", (st.Transitions ?? new FsmTransition[0]).Select(t => t.EventName + "->" + t.ToState).ToArray()));
                var actions = st.Actions ?? new FsmStateAction[0];
                for (int i = 0; i < actions.Length; i++)
                {
                    var a = actions[i];
                    if (a == null) { sb.AppendLine("        [" + i + "] null"); continue; }
                    sb.AppendLine("        [" + i + "] " + a.GetType().FullName + (a.Enabled ? "" : " (DISABLED)"));
                    foreach (var fld in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (fld.DeclaringType == typeof(FsmStateAction)) continue;
                        object v; try { v = fld.GetValue(a); } catch (Exception e) { v = "!" + e.Message; }
                        sb.AppendLine("              " + fld.Name + " = " + Fmt(v, fsm.Fsm, 0));
                    }
                }
            }
        }

        internal static string Target(FsmEventTarget t, Fsm owner)
        {
            if (t == null) return "self";
            var sb = new StringBuilder(t.target.ToString());
            try
            {
                if (t.target == FsmEventTarget.EventTarget.GameObject || t.target == FsmEventTarget.EventTarget.GameObjectFSM)
                {
                    var go = t.gameObject != null ? (t.gameObject.OwnerOption == OwnerDefaultOption.UseOwner ? (owner != null ? owner.GameObject : null) : (t.gameObject.GameObject != null ? t.gameObject.GameObject.Value : null)) : null;
                    sb.Append(" go=" + (go != null ? go.name : "null"));
                    if (t.target == FsmEventTarget.EventTarget.GameObjectFSM) sb.Append(" fsm=" + (t.fsmName != null ? t.fsmName.Value : ""));
                }
                else if (t.target == FsmEventTarget.EventTarget.FSMComponent) sb.Append(" fsm=" + (t.fsmComponent != null ? t.fsmComponent.FsmName : "null"));
            }
            catch (Exception e) { sb.Append(" !" + e.Message); }
            return sb.ToString();
        }

        private static string Fmt(object v, Fsm owner, int depth)
        {
            if (v == null) return "null";
            if (depth > 3) return v.ToString();
            var s = v as string; if (s != null) return "\"" + s + "\"";
            if (v is FsmEvent) return "event:" + ((FsmEvent)v).Name;
            if (v is FsmEventTarget) return Target((FsmEventTarget)v, owner);
            if (v is FsmOwnerDefault) { var od = (FsmOwnerDefault)v; return od.OwnerOption == OwnerDefaultOption.UseOwner ? "Owner" : "GO:" + Fmt(od.GameObject, owner, depth + 1); }
            if (v is FsmGameObject) { var g = (FsmGameObject)v; return Var(g) + (g.Value != null ? g.Value.name + " (GameObject)" : "null"); }
            if (v is FsmObject) { var o = (FsmObject)v; return Var(o) + (o.Value != null ? o.Value.name + " (" + o.Value.GetType().Name + ")" : "null"); }
            if (v is FsmString) { var x = (FsmString)v; return Var(x) + (x.Value ?? ""); }
            if (v is FsmInt) { var x = (FsmInt)v; return Var(x) + x.Value; }
            if (v is FsmFloat) { var x = (FsmFloat)v; return Var(x) + x.Value; }
            if (v is FsmBool) { var x = (FsmBool)v; return Var(x) + x.Value; }
            if (v is FsmVector3) { var x = (FsmVector3)v; return Var(x) + (x.IsNone ? "none" : x.Value.ToString()); }
            if (v is FsmVector2) { var x = (FsmVector2)v; return Var(x) + x.Value; }
            if (v is FsmColor) { var x = (FsmColor)v; return Var(x) + x.Value; }
            if (v is FsmQuaternion) { var x = (FsmQuaternion)v; return Var(x) + x.Value; }
            if (v is FsmEnum) { var x = (FsmEnum)v; return Var(x) + x.Value; }
            if (v is FsmArray) { var x = (FsmArray)v; return Var(x) + "array[" + x.Length + "] " + x.ElementType; }
            if (v is FsmVar) { var x = (FsmVar)v; return "var:" + x.variableName + " (" + x.Type + ")"; }
            if (v is NamedVariable) { var x = (NamedVariable)v; return "{" + x.Name + "}"; }
            var uo = v as UnityEngine.Object;
            if (uo != null) { try { return uo.name + " (" + uo.GetType().Name + ")"; } catch { return "(destroyed " + v.GetType().Name + ")"; } }
            if (v is FsmProperty) { var p = (FsmProperty)v; return "prop:" + p.PropertyName + " target=" + Fmt(p.TargetObject, owner, depth + 1); }
            if (v is LayerMask) return "mask " + ((LayerMask)v).value;
            var arr = v as IEnumerable;
            if (arr != null)
            {
                var parts = new List<string>();
                foreach (var e in arr) { parts.Add(Fmt(e, owner, depth + 1)); if (parts.Count > 40) { parts.Add("..."); break; } }
                return "[" + string.Join(", ", parts.ToArray()) + "]";
            }
            return v.ToString();
        }

        private static string Var(NamedVariable nv) { return nv != null && !string.IsNullOrEmpty(nv.Name) ? "{" + nv.Name + "}" : ""; }
    }
}
