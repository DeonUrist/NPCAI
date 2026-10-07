using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using UnityEngine;
using Ap = Apocaplayer.ModAPI;

namespace NPCAI
{
    // (1.2.0) NPC bodies animated through Apocaplayer's ModAPI (Apocaplayer 2.2.0+): the player's own clips and logic - idle, 8 directions x walk /
    // run / sprint, crouch idle and 8 crouched walks for every weapon (pistolmen kneel again: the pistol's hands over the crouched legs, as the
    // player's), the relaxed low-ready walk / run, aiming and firing sets, turns in place, reloads (the whole clip, or one round at a time for the
    // revolver, the shotguns, the bolt rifle and the double barrel), ShotgunPump after each burst of a pump / bolt gun, jumps for the hops, the
    // melee swing, the gun in the right hand at the player's weapon poses and the aim lift.
    //
    // Apocaplayer is optional: every call into it is in this class, in methods that are only ever run once Available said it is there, so the
    // runtime never needs Apocaplayer.dll otherwise (the Npc keeps its Character as a plain object).
    internal static class ApBody
    {
        internal const string ApocaplayerGuid = "com.denis.apocalypter.apocaplayer";
        private static bool _tried, _ok;

        internal static bool Available { get { Init(); return _ok; } }

        private static void Init()
        {
            if (_tried) return;
            if (Time.unscaledTime < 3f) return;           // let Apocaplayer finish loading its bundle
            _tried = true;
            if (!Chainloader.PluginInfos.ContainsKey(ApocaplayerGuid)) { Plugin.Log.LogInfo("NPC animations: Apocaplayer not installed - the game's clips, no sidesteps"); return; }
            try { _ok = Probe(); }
            catch (Exception e) { _ok = false; Plugin.Warn("NPC animations: this Apocaplayer has no ModAPI (2.2.0 or newer needed) - the game's clips (" + e.GetType().Name + ")"); return; }
            Plugin.Log.LogInfo("NPC animations: " + (_ok ? "Apocaplayer's ModAPI in use" : "Apocaplayer's animation bundle is missing - the game's clips"));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Probe()
        {
            bool ready = Ap.Ready;
            if (ready) Plugin.Log.LogInfo("NPC animations: Apocaplayer " + Ap.PluginVersion + ", ModAPI " + Ap.ApiVersion + ", " + Ap.ClipNames().Length + " player clips");
            return ready;
        }

        // ---------------------------------------------------------------- weapon helpers (no Apocaplayer types)
        // "akm_trash_model" -> "akm_trash", "9mm_borz_smg" -> "borz_smg": the key of the weapon-pose / magazine tables (Apocaplayer.Props.Norm)
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
        // pistols, revolvers and SMGs are held like pistols (Apocaplayer.Props.KindOf)
        internal static bool IsPistol(string weaponKey)
        {
            string w = weaponKey ?? "";
            return w.Contains("pistol") || w.Contains("revolver") || w.Contains("folk_17") || w.Contains("smg") || w.Contains("borz");
        }

        // ---------------------------------------------------------------- per NPC (only when Available)
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object Attach(Animator a)
        {
            var c = Ap.Attach(a);
            if (c == null) return null;
            c.TurnInPlace = true; c.WalkToStop = true;
            return c;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Detach(object body) { var c = body as Ap.Character; if (c != null) c.Dispose(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Alive(object body) { var c = body as Ap.Character; return c != null && c.Valid; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void SetWeapon(object body, Transform model, string key) { var c = body as Ap.Character; if (c != null) c.SetWeapon(model, model != null ? key : null); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Suspend(object body, bool off) { var c = body as Ap.Character; if (c != null) c.Suspended = off; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Suspended(object body) { var c = body as Ap.Character; return c == null || c.Suspended; }
        // this frame's situation
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Frame(object body, bool crouched, bool aiming, bool firing, bool airborne, float aimPitch)
        {
            var c = body as Ap.Character; if (c == null) return;
            c.Crouched = crouched; c.Aiming = aiming; c.Firing = firing; c.Airborne = airborne; c.AimPitch = aimPitch;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float SpeedCap(object body) { var c = body as Ap.Character; return c != null ? c.SpeedCap : 0f; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Reload(object body, float seconds, int rounds) { var c = body as Ap.Character; if (c != null) c.Reload(seconds, rounds); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CancelReload(object body) { var c = body as Ap.Character; if (c != null) c.CancelReload(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Pump(object body) { var c = body as Ap.Character; if (c != null) c.Pump(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Jump(object body) { var c = body as Ap.Character; if (c != null) c.Jump(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Strike(object body, float seconds) { var c = body as Ap.Character; if (c != null) c.Strike(seconds); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string Describe(object body)
        {
            var c = body as Ap.Character; if (c == null) return "-";
            return c.WeaponKey + " (" + c.WeaponKind + ") hands " + c.HandsClip + (c.ActionClip != "" ? ", action " + c.ActionClip : "") + (c.UpperClip != "" ? ", upper " + c.UpperClip : "");
        }

        // ---------------------------------------------------------------- the player's weapon facts (only when Available)
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool HasPoses(string key) { return Ap.HasWeaponPoses(key); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool OneRoundAtATime(string key) { return Ap.ReloadsOneRoundAtATime(key); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Cocks(string key) { return Ap.CocksAfterShot(key); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float ReloadClipSeconds(string key) { return Ap.ReloadClipSeconds(key); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float RoundSeconds(string key) { return Ap.RoundSeconds(key); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static float NativeSpeed(string tier) { return Ap.NativeSpeed(tier); }
    }
}
