using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace NPCAI
{
    // (1.1.1) Gunmen's body animations from Apocaplayer, when that mod is installed, done the way its third-person body does it
    // (Apocaplayer Body.cs): the raiders share the player's skeleton (she is built from Flexa's Anim object, humanoid avatar
    // enemy_1_IdleAvatar), so its humanoid clips retarget onto them, and its weapon-pose table (GunPose: BuiltinPoses + the player's own
    // config/Apocaplayer/weapon-poses.txt, one entry per weapon per animation, "weapon|Pose|x,y,z,rx,ry,rz" in cm and degrees, the gun's
    // local pose under mixamorig:RightHand) places the raider's own gun model exactly as it sits in the player's hand in that animation.
    //
    // Clip sets (Body.MakeSet): "Rifle" + slot and "RifleFire" + slot (the raised, aimed set), "Pistol" + slot and "PistolFire" + slot, with
    // the slots Idle, Walk, WalkBack, StrafeLeft, StrafeRight, Run, RunStrafeLeft, RunStrafeRight, CrouchIdle, CrouchWalk, CrouchWalkBack,
    // CrouchStrafeLeft, CrouchStrafeRight, and the same fallbacks: the Fire set's idle is RifleFire itself (CrouchRifleFire crouched), a
    // Fire set without a slot uses the plain set's clip, a missing WalkBack is that set's Walk reversed, missing run strafes are the Run,
    // missing strafes are the Walk. The pose names are GunPose.Poses: the slot name, or for the Fire set "Fire" (idle), "CrouchFire"
    // (crouch idle) and "Fire" + slot (FireWalk, FireStrafeLeft, FireRun, FireCrouchStrafeLeft ...).
    //
    // A rifleman aiming is the player aiming down sights: the RifleFire clip held on its first frame (Body: hold && !shooting -> speed 0),
    // pose "Fire"; moving while aiming plays the Fire set's clips; pistols are already up in PistolIdle (no aim set). The pose for a clip is
    // GunPose.Effective(weapon, pose, default) when Apocaplayer exposes it (the player's live table incl. his weapon-poses.txt), else the
    // same lookup in the parsed table (own entry, else the weapon's Idle).
    //
    // Mechanics: a PlayableGraph with a crossfading two-input mixer outputs to the NPC's own Animator and replaces its controller's output
    // while it runs; Stop() destroys the graph and the game's own clips show again at once. The gun model is reparented from the left hand
    // to the right hand (its muzzle flash and the game's ray origin, children of it, come along) and put back on Stop().
    internal static class NpcAnim
    {
        private static bool _tried, _ok;
        private static MethodInfo _get, _effective;
        private static string[] _poseNames;
        private static float _walkSpeed = 1.4f, _runSpeed = 3.8f, _crouchSpeed = 1f;   // Apocaplayer's BundleWalkSpeed / BundleRunSpeed / BundleCrouchSpeed: ground speed at which a clip plays at 1x
        private static readonly Dictionary<string, float[]> _poses = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AnimationClip> _clips = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);

        internal static readonly string[] Slots =
        {
            "Idle", "Walk", "WalkBack", "StrafeLeft", "StrafeRight", "Run", "RunStrafeLeft", "RunStrafeRight",
            "CrouchIdle", "CrouchWalk", "CrouchWalkBack", "CrouchStrafeLeft", "CrouchStrafeRight",
        };

        internal static bool Available { get { Init(); return _ok; } }

        private static void Init()
        {
            if (_tried) return;
            if (Time.unscaledTime < 3f) return;      // let Apocaplayer finish its Awake
            _tried = true;
            try
            {
                PluginInfo pi;
                if (!Chainloader.PluginInfos.TryGetValue("com.denis.apocalypter.apocaplayer", out pi) || ReferenceEquals(pi.Instance, null)) { Plugin.Log.LogInfo("NPC animations: Apocaplayer not installed - the game's clips, no sidesteps"); return; }
                var asm = pi.Instance.GetType().Assembly;
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var anims = asm.GetType("Apocaplayer.Anims");
                if (anims == null) { Plugin.Warn("NPC animations: Apocaplayer has no Anims class (older version?)"); return; }
                var loaded = anims.GetProperty("Loaded", F);
                _get = anims.GetMethod("Get", F, null, new[] { typeof(string) }, null);
                if (loaded == null || _get == null || !(bool)loaded.GetValue(null, null)) { Plugin.Log.LogInfo("NPC animations: Apocaplayer has no animation bundle - the game's clips, no sidesteps"); return; }
                // the pose table: Apocaplayer's own lookup when it is there (the player's live table), the parsed files otherwise
                var gunPose = asm.GetType("Apocaplayer.GunPose");
                if (gunPose != null)
                {
                    _effective = gunPose.GetMethod("Effective", F, null, new[] { typeof(string), typeof(int), typeof(float[]) }, null);
                    var names = gunPose.GetField("Poses", F);
                    _poseNames = names != null ? names.GetValue(null) as string[] : null;
                    if (_poseNames == null) _effective = null;
                }
                // the clips' native ground speeds as the player has them set
                var ap = pi.Instance.GetType();
                _walkSpeed = ConfigFloat(ap, "ClipWalkSpeed", _walkSpeed); _runSpeed = ConfigFloat(ap, "ClipRunSpeed", _runSpeed); _crouchSpeed = ConfigFloat(ap, "ClipCrouchSpeed", _crouchSpeed);
                int n = 0, u = 0;
                var builtin = asm.GetType("Apocaplayer.BuiltinPoses");
                var data = builtin != null ? builtin.GetField("Data", F) : null;
                var lines = data != null ? data.GetValue(null) as string[] : null;
                if (lines != null) foreach (var l in lines) { var p = l.Split('|'); float[] v; if (p.Length == 3 && (v = Parse(p[2])) != null) { _poses[p[0] + "|" + p[1]] = v; n++; } }
                string user = Path.Combine(Path.Combine(Paths.ConfigPath, "Apocaplayer"), "weapon-poses.txt");
                if (File.Exists(user))
                    foreach (var raw in File.ReadAllLines(user))
                    {
                        string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('='); if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim(); var v = Parse(line.Substring(eq + 1));
                        if (v != null && key.IndexOf('|') > 0) { _poses[key] = v; u++; }
                    }
                _ok = Clip("RifleIdle") != null && Clip("RifleStrafeLeft") != null && (_effective != null || n > 0);
                Plugin.Log.LogInfo("NPC animations: Apocaplayer's bundle " + (_ok ? "in use" : "lacks RifleIdle / RifleStrafeLeft (or has no weapon poses) - not used")
                    + "; weapon poses " + (_effective != null ? "from Apocaplayer's live table" : "parsed") + " (" + n + " built in, " + u + " from weapon-poses.txt)");
            }
            catch (Exception e) { Plugin.Warn("NPC animations: " + e.Message); }
        }

        private static float ConfigFloat(Type plugin, string field, float def)
        {
            try
            {
                var f = plugin.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var entry = f != null ? f.GetValue(null) as BepInEx.Configuration.ConfigEntry<float> : null;
                return entry != null && entry.Value > 0.2f ? entry.Value : def;
            }
            catch (Exception) { return def; }
        }

        // the ground speed (m/s) at which a clip plays at normal speed: its own root-motion speed when it was exported with it, else the
        // player's setting for its kind (Body.Native) - the body is moved at most 2x this and at least 0.5x so the feet never slide
        internal static float Native(string clipName, string slot)
        {
            var c = Clip(clipName);
            if (c != null)
            {
                float v = new Vector2(c.averageSpeed.x, c.averageSpeed.z).magnitude;
                if (v > 0.3f) return v;
            }
            return slot.StartsWith("Run") ? _runSpeed : slot.StartsWith("Crouch") ? _crouchSpeed : _walkSpeed;
        }

        private static float[] Parse(string s)
        {
            var parts = (s ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) return null;
            var v = new float[6];
            for (int i = 0; i < 6; i++) if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
            return v;
        }

        internal static AnimationClip Clip(string name)
        {
            AnimationClip c;
            if (name == null) return null;
            if (_clips.TryGetValue(name, out c)) return c;
            try { c = _get != null ? _get.Invoke(null, new object[] { name }) as AnimationClip : null; } catch (Exception) { c = null; }
            _clips[name] = c;
            return c;
        }

        // "akm_trash_model" -> "akm_trash", "9mm_borz_smg" -> "borz_smg": the pose table's weapon key (Apocaplayer.Props.Norm)
        internal static string WeaponKey(Transform weapon)
        {
            if (weapon == null) return null;
            string n = weapon.name.ToLowerInvariant();
            int c = n.IndexOf(" ("); if (c > 0) n = n.Substring(0, c);
            c = n.IndexOf("(clone)"); if (c > 0) n = n.Substring(0, c);
            if (n.EndsWith("_model")) n = n.Substring(0, n.Length - 6);
            if (n.StartsWith("9mm_")) n = n.Substring(4);
            return n.Trim();
        }

        // pistols, revolvers and SMGs use the Pistol clips (Apocaplayer.Props.KindOf); everything else the Rifle clips
        internal static string Prefix(string weaponKey)
        {
            string w = weaponKey ?? "";
            return w.Contains("pistol") || w.Contains("revolver") || w.Contains("folk_17") || w.Contains("smg") || w.Contains("borz") ? "Pistol" : "Rifle";
        }

        // the table knows this gun (its Idle at least); an unknown gun keeps the game's clips (the clips hold the gun right-handed and
        // without its right-hand pose it would stay in the left)
        internal static bool HasPoses(string weaponKey)
        {
            Init();
            if (weaponKey == null) return false;
            if (_poses.ContainsKey(weaponKey + "|Idle")) return true;
            Vector3 p; Quaternion q;
            return _effective != null && Pose(weaponKey, "Idle", out p, out q);
        }

        // GunPose.Poses name of a slot in the Fire set
        internal static string FirePose(string slot)
        {
            return slot == "Idle" ? "Fire" : slot == "CrouchIdle" ? "CrouchFire" : "Fire" + slot;
        }

        // the bundle clip that Apocaplayer plays for a pose of the Rifle / Pistol set (Body.MakeSet's fallbacks); speed -1 = the clip reversed
        internal static string ClipFor(string prefix, string pose, out float speed)
        {
            speed = 1f;
            bool fire = false; string slot = pose;
            if (pose == "Fire") { fire = true; slot = "Idle"; }
            else if (pose == "CrouchFire") { fire = true; slot = "CrouchIdle"; }
            else if (pose.StartsWith("Fire")) { fire = true; slot = pose.Substring(4); }
            if (fire)
            {
                string c = SetClip(prefix + "Fire", prefix, slot, out speed);
                if (c != null) return c;
            }
            return SetClip(prefix, prefix, slot, out speed) ?? SetClip("", "", slot, out speed) ?? prefix + "Idle";
        }

        // one set's clip for a slot, with its own fallbacks; null when the set has nothing for it
        private static string SetClip(string setPre, string basePre, string slot, out float speed)
        {
            speed = 1f;
            bool fireSet = setPre != basePre;
            if (slot == "Idle")
            {
                if (!fireSet) return Clip(setPre + "Idle") != null ? setPre + "Idle" : null;
                foreach (var c in new[] { setPre, setPre + "Idle" }) if (Clip(c) != null) return c;       // RifleFire, RifleFireIdle
                return null;
            }
            if (slot == "CrouchIdle" && fireSet)
            {
                foreach (var c in new[] { "Crouch" + setPre, setPre + "CrouchIdle", basePre + "CrouchFire" }) if (Clip(c) != null) return c;   // CrouchRifleFire
                return null;
            }
            if (Clip(setPre + slot) != null) return setPre + slot;
            string walk = slot.StartsWith("Crouch") ? setPre + "CrouchWalk" : setPre + "Walk";
            if (slot == "WalkBack" || slot == "CrouchWalkBack") { if (Clip(walk) != null) { speed = -1f; return walk; } return null; }
            if (slot == "RunStrafeLeft" || slot == "RunStrafeRight") return Clip(setPre + "Run") != null ? setPre + "Run" : null;
            if (slot == "Run") return Clip(walk) != null ? walk : null;
            if (slot.EndsWith("StrafeLeft") || slot.EndsWith("StrafeRight")) return Clip(walk) != null ? walk : null;
            return null;
        }

        // where the gun sits in the right hand for this pose: Apocaplayer's own lookup (Effective: the pose's entry, else the weapon's Idle),
        // else the same on the parsed table
        private static bool Pose(string weapon, string pose, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (weapon == null) return false;
            float[] v = null;
            if (_effective != null)
            {
                int i = Array.IndexOf(_poseNames, pose);
                if (i >= 0)
                    try { v = _effective.Invoke(null, new object[] { weapon, i, null }) as float[]; } catch (Exception) { v = null; }
            }
            if (v == null && !_poses.TryGetValue(weapon + "|" + pose, out v)) _poses.TryGetValue(weapon + "|Idle", out v);
            if (v == null) return false;
            pos = new Vector3(v[0], v[1], v[2]) * 0.01f; rot = Quaternion.Euler(v[3], v[4], v[5]);
            return true;
        }

        internal sealed class Rig
        {
            public Animator A; public Transform RightHand;
            public PlayableGraph G; public AnimationMixerPlayable Mix; public AnimationClipPlayable Cur, Old; public bool HasOld; public float Fade;
            public string Clip = "", Pose = ""; public float Speed = 1f; public bool Cycle;   // Cycle: a locomotion cycle (walk / run / strafe) - the next one starts at the same phase
            public bool RootMotionWas;
            public Transform Weapon, WeaponParent; public Vector3 WPos; public Quaternion WRot; public bool WeaponMoved;
            // (1.1.1) legs-only rig (melee humans): the NPC's own controller plays as layer 0 of the graph, the locomotion clip as a
            // leg-masked layer 1 whose weight is LegW (0 = the game's clip entirely: idle, the swing)
            public bool Legs; public AnimatorControllerPlayable Ctrl; public AnimationLayerMixerPlayable Layers; public float LegW, LegTarget;
            public bool On { get { return G.IsValid(); } }
        }

        private static AvatarMask _legMask;
        private static AvatarMask LegMask()
        {
            if (_legMask != null) return _legMask;
            _legMask = new AvatarMask();
            foreach (AvatarMaskBodyPart part in Enum.GetValues(typeof(AvatarMaskBodyPart)))
            {
                if (part == AvatarMaskBodyPart.LastBodyPart) continue;
                bool legs = part == AvatarMaskBodyPart.LeftLeg || part == AvatarMaskBodyPart.RightLeg || part == AvatarMaskBodyPart.LeftFootIK || part == AvatarMaskBodyPart.RightFootIK;
                _legMask.SetHumanoidBodyPartActive(part, legs);
            }
            return _legMask;
        }

        internal static Rig MakeLegs(Animator a, Transform root)
        {
            if (a == null || a.avatar == null || !a.avatar.isHuman || a.runtimeAnimatorController == null) return null;
            return new Rig { A = a, Legs = true };
        }

        // the game's AnimatorPlay (Movement / Attack FSMs) while the legs rig runs: the same state on the controller inside the graph
        internal static void ForwardPlay(Rig r, string state, int layer, float normalizedTime)
        {
            if (r == null || !r.Legs || !r.G.IsValid() || string.IsNullOrEmpty(state)) return;
            try { if (normalizedTime >= 0f) r.Ctrl.Play(state, layer, normalizedTime); else r.Ctrl.Play(state, layer); } catch (Exception) { }
        }

        // legs rig: the locomotion clip of the unarmed set for the slot (null / weight 0 = the game's clip alone), crossfaded like the gun rig
        internal static void PlayLegs(Rig r, string slot, float speedScale, float weight)
        {
            if (r == null || r.A == null || !r.Legs) return;
            float speed;
            string clipName = ClipFor("", slot, out speed);
            speed *= speedScale;
            var clip = Clip(clipName);
            if (clip == null) { Stop(r); return; }
            bool fresh = false;
            if (!r.G.IsValid())
            {
                r.G = PlayableGraph.Create("NPCAI.legs." + r.A.gameObject.name);
                r.G.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                var output = AnimationPlayableOutput.Create(r.G, "body", r.A);
                r.Ctrl = AnimatorControllerPlayable.Create(r.G, r.A.runtimeAnimatorController);
                var st = r.A.GetCurrentAnimatorStateInfo(0);
                try { r.Ctrl.Play(st.fullPathHash, 0, st.normalizedTime); } catch (Exception) { }     // carry on from the controller's state
                r.Mix = AnimationMixerPlayable.Create(r.G, 2);
                r.Layers = AnimationLayerMixerPlayable.Create(r.G, 2);
                r.G.Connect(r.Ctrl, 0, r.Layers, 0); r.Layers.SetInputWeight(0, 1f);
                r.G.Connect(r.Mix, 0, r.Layers, 1); r.Layers.SetInputWeight(1, 0f); r.Layers.SetLayerMaskFromAvatarMask(1, LegMask());
                output.SetSourcePlayable(r.Layers);
                r.RootMotionWas = r.A.applyRootMotion; r.A.applyRootMotion = false;
                r.Cur = AnimationClipPlayable.Create(r.G, clip);
                r.Cur.SetApplyFootIK(false);
                r.G.Connect(r.Cur, 0, r.Mix, 0); r.Mix.SetInputWeight(0, 1f); r.Mix.SetInputWeight(1, 0f);
                r.HasOld = false; r.Fade = 1f; r.LegW = 0f;
                r.G.Play();
                fresh = true;
            }
            else if (r.Clip != clipName) { Swap(r, clip, true); fresh = true; }
            if (fresh && speed < 0f && !r.Cycle) r.Cur.SetTime(clip.length);
            r.Cycle = true;
            r.Cur.SetSpeed(speed);
            r.Speed = speed; r.Clip = clipName; r.Pose = slot;
            r.LegTarget = Mathf.Clamp01(weight);
        }

        // the crossfade to a new clip (the old one fades on mixer input 1)
        private static void Swap(Rig r, AnimationClip clip, bool cycle)
        {
            if (r.HasOld) { r.G.Disconnect(r.Mix, 1); r.Old.Destroy(); }
            r.G.Disconnect(r.Mix, 0);
            double phase = -1.0;
            if (cycle && r.Cycle) { var oc = r.Cur.GetAnimationClip(); if (oc != null && oc.length > 0.01f) phase = (r.Cur.GetTime() / oc.length) % 1.0; }   // walk -> run -> strafe: same foot phase, no skip
            r.Old = r.Cur; r.G.Connect(r.Old, 0, r.Mix, 1); r.HasOld = true;
            r.Cur = AnimationClipPlayable.Create(r.G, clip);
            r.Cur.SetApplyFootIK(false);
            if (phase >= 0.0) r.Cur.SetTime(phase * clip.length);
            r.G.Connect(r.Cur, 0, r.Mix, 0);
            r.Fade = 0f; r.Mix.SetInputWeight(0, 0f); r.Mix.SetInputWeight(1, 1f);
        }

        internal static Rig Make(Animator a, Transform root)
        {
            if (a == null || a.avatar == null || !a.avatar.isHuman) return null;     // the clips are humanoid: a humanoid avatar is needed
            Transform hand = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) { string s = t.name; int i = s.LastIndexOf(':'); if ((i >= 0 ? s.Substring(i + 1) : s) == "RightHand") { hand = t; break; } }
            if (hand == null) return null;
            return new Rig { A = a, RightHand = hand };
        }

        // plays the clip for poseName of the weapon's set (crossfading 0.15 s from the one before) at speed x speedScale (0 = held on its
        // first frame), and puts the gun in the right hand at the pose
        internal static void Play(Rig r, string prefix, string poseName, float speedScale, Transform weapon, string weaponKey) { Play(r, prefix, poseName, speedScale, weapon, weaponKey, false); }
        internal static void Play(Rig r, string prefix, string poseName, float speedScale, Transform weapon, string weaponKey, bool cycle)
        {
            if (r == null || r.A == null) return;
            float speed;
            string clipName = ClipFor(prefix, poseName, out speed);
            speed *= speedScale;
            var clip = Clip(clipName);
            if (clip == null) { Stop(r); return; }
            if (r.WeaponMoved && r.Weapon != weapon) Release(r);              // the game swapped the gun model: the old one back where it was
            bool fresh = false;
            if (!r.G.IsValid())
            {
                r.G = PlayableGraph.Create("NPCAI." + r.A.gameObject.name);
                r.G.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                var output = AnimationPlayableOutput.Create(r.G, "body", r.A);
                r.Mix = AnimationMixerPlayable.Create(r.G, 2);
                output.SetSourcePlayable(r.Mix);
                r.RootMotionWas = r.A.applyRootMotion; r.A.applyRootMotion = false;
                r.Cur = AnimationClipPlayable.Create(r.G, clip);
                r.Cur.SetApplyFootIK(false);
                r.G.Connect(r.Cur, 0, r.Mix, 0); r.Mix.SetInputWeight(0, 1f); r.Mix.SetInputWeight(1, 0f);
                r.HasOld = false; r.Fade = 1f;
                r.G.Play();
                fresh = true;
            }
            else if (r.Clip != clipName) { Swap(r, clip, cycle); fresh = true; }
            if (fresh && speed < 0f && !(cycle && r.Cycle)) r.Cur.SetTime(clip.length);                 // a reversed clip starts from its end
            r.Cycle = cycle;
            if (speed == 0f && (fresh || r.Speed != 0f)) r.Cur.SetTime(0);        // the aim: the firing clip's first frame (Apocaplayer holds it the same way)
            r.Cur.SetSpeed(speed);
            r.Speed = speed; r.Clip = clipName;
            if (weapon != null && r.RightHand != null && (fresh || r.Pose != poseName || !r.WeaponMoved || r.Weapon != weapon))
            {
                Vector3 p; Quaternion q;
                if (Pose(weaponKey, poseName, out p, out q))
                {
                    if (!r.WeaponMoved) { r.Weapon = weapon; r.WeaponParent = weapon.parent; r.WPos = weapon.localPosition; r.WRot = weapon.localRotation; r.WeaponMoved = true; weapon.SetParent(r.RightHand, false); }
                    weapon.localPosition = p; weapon.localRotation = q;
                }
            }
            r.Pose = poseName;
        }

        internal static void Tick(Rig r, float dt)
        {
            if (r == null || !r.G.IsValid()) return;
            if (r.Legs && r.LegW != r.LegTarget) { r.LegW = Mathf.MoveTowards(r.LegW, r.LegTarget, dt / 0.15f); r.Layers.SetInputWeight(1, r.LegW); }
            if (!r.HasOld) return;
            r.Fade = Mathf.Min(1f, r.Fade + dt / 0.15f);
            r.Mix.SetInputWeight(0, r.Fade); r.Mix.SetInputWeight(1, 1f - r.Fade);
            if (r.Fade >= 1f) { r.G.Disconnect(r.Mix, 1); r.Old.Destroy(); r.HasOld = false; }
        }

        // the gun back in its own hand at its own pose
        internal static void Release(Rig r)
        {
            if (r == null || !r.WeaponMoved) return;
            r.WeaponMoved = false;
            if (r.Weapon != null) { r.Weapon.SetParent(r.WeaponParent, false); r.Weapon.localPosition = r.WPos; r.Weapon.localRotation = r.WRot; }
            r.Weapon = null; r.WeaponParent = null;
        }

        // back to the game's own clips and the gun back in its hand
        internal static void Stop(Rig r)
        {
            if (r == null) return;
            if (r.G.IsValid())
            {
                // legs rig: the Animator's own controller takes over where the graph's copy of it was
                if (r.Legs && r.A != null) { try { var st = r.Ctrl.GetCurrentAnimatorStateInfo(0); r.A.Play(st.fullPathHash, 0, st.normalizedTime); } catch (Exception) { } }
                r.G.Destroy(); if (r.A != null) r.A.applyRootMotion = r.RootMotionWas;
            }
            r.HasOld = false; r.Clip = ""; r.Pose = ""; r.Speed = 1f; r.Cycle = false; r.LegW = r.LegTarget = 0f;
            Release(r);
        }
    }
}
