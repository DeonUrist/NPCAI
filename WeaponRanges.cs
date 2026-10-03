using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace NPCAI
{
    // Small deterministic fallback kept consistent with Gunplay's version-one contract.
    // No sibling assembly or sibling-defined types are referenced at compile time.
    internal static class WeaponRanges
    {
        internal enum Kind { Pistol, Smg, Rifle, Sniper, Shotgun, Crossbow }
        private sealed class Info { public GameObject Owner; public Transform Weapon; public Kind Kind; public bool IsGun; public int Tries; public float Retry; }
        private static readonly Dictionary<int, Info> _shooters = new Dictionary<int, Info>();
        private static readonly List<int> _deadShooters = new List<int>();
        private static float _nextSweep;
        private delegate bool TryKind(GameObject owner, out int kind);
        private static Func<int, float> _range;
        private static TryKind _kind;
        private static Func<bool> _projectiles;
        private static DateTime _nextResolve;

        private static void Resolve()
        {
            if (_range != null || DateTime.UtcNow < _nextResolve) return;
            _nextResolve = DateTime.UtcNow.AddSeconds(5);
            try
            {
                BepInEx.PluginInfo info;
                if (!Chainloader.PluginInfos.TryGetValue("com.denis.apocalypter.gunplay", out info) || ReferenceEquals(info.Instance, null)) return;
                var api = info.Instance.GetType().Assembly.GetType("Gunplay.Api", false);
                if (api == null) return;
                var version = api.GetField("ContractVersion", BindingFlags.Public | BindingFlags.Static);
                if (version != null && Convert.ToInt32(version.GetValue(null)) != 1) return;
                var range = api.GetMethod("EffectiveRange", new[] { typeof(int) });
                var kind = api.GetMethod("TryGetWeaponKind", new[] { typeof(GameObject), typeof(int).MakeByRefType() });
                var projectiles = api.GetProperty("ProjectilesEnabled", BindingFlags.Public | BindingFlags.Static);
                if (range != null) _range = (Func<int, float>)Delegate.CreateDelegate(typeof(Func<int, float>), range);
                if (kind != null) _kind = (TryKind)Delegate.CreateDelegate(typeof(TryKind), kind);
                if (projectiles != null) _projectiles = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), projectiles.GetGetMethod());
                Plugin.Log.LogInfo("Gunplay optional range integration: " + (_range != null ? "active" : "unavailable; using fallback"));
            }
            catch (Exception e) { Plugin.Warn("Gunplay optional integration unavailable: " + e.Message); }
        }

        internal static bool ProjectilesEnabled
        {
            get { Resolve(); try { return _projectiles != null && _projectiles(); } catch (Exception) { return false; } }
        }

        internal static float RangeOf(Kind k)
        {
            Resolve();
            if (_range != null)
                try { float range = _range((int)k); if (!float.IsNaN(range) && !float.IsInfinity(range)) return Mathf.Max(1f, range); }
                catch (Exception e) { Plugin.Warn("Gunplay range query failed: " + e.Message); }
            switch (k)
            {
                case Kind.Pistol: return Mathf.Max(1f, Plugin.PistolRange.Value);
                case Kind.Smg: return Mathf.Max(1f, Plugin.SmgRange.Value);
                case Kind.Sniper: return Mathf.Max(1f, Plugin.SniperRange.Value);
                case Kind.Shotgun: return Mathf.Max(1f, Plugin.ShotgunRange.Value);
                case Kind.Crossbow: return Mathf.Max(1f, Plugin.CrossbowRange.Value);
                default: return Mathf.Max(1f, Plugin.RifleRange.Value);
            }
        }

        internal static Kind Classify(string weapon)
        {
            string n = weapon.ToLowerInvariant();
            if (n.Contains("crossbow")) return Kind.Crossbow;
            if (n.Contains("shotgun") || n.Contains("slamfire") || n.Contains("slamberg") || n.Contains("rochester")) return Kind.Shotgun;
            if (n.Contains("scoped") || n.Contains("sniper") || n.Contains("redmark")) return Kind.Sniper;
            if (n.Contains("smg") || n.Contains("borz")) return Kind.Smg;
            if (n.Contains("pistol") || n.Contains("revolver") || n.Contains("folk_17")) return Kind.Pistol;
            return Kind.Rifle;
        }

        internal static bool GunKindOf(GameObject owner, out Kind kind)
        {
            kind = Kind.Rifle;
            if (owner == null) return false;
            Resolve();
            if (_kind != null)
                try { int found; bool gun = _kind(owner, out found); if (found >= 0 && found <= 5) kind = (Kind)found; return gun; }
                catch (Exception e) { Plugin.Warn("Gunplay weapon query failed: " + e.Message); }
            int id = owner.GetInstanceID(); Info info;
            if (_shooters.TryGetValue(id, out info) && info.Owner == owner && (info.Weapon != null ? info.Weapon.gameObject.activeInHierarchy : Time.time < info.Retry))
            { kind = info.Kind; return info.IsGun; }
            bool ranged = false;
            foreach (var f in owner.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Damage Ranged") { ranged = true; break; }
            if (!ranged) return false;
            int tries = info != null && info.Weapon == null ? info.Tries + 1 : 0;
            info = new Info { Owner = owner, Tries = tries, Retry = tries < 10 ? Time.time + 1f : float.MaxValue };
            _shooters[id] = info;
            foreach (var t in owner.GetComponentsInChildren<Transform>(true))
                if (t.name == "fire_effect" && t.parent != null && t.parent.name != "fire_effect" && t.parent.gameObject.activeInHierarchy)
                { info.Weapon = t.parent; break; }
            if (info.Weapon == null)
                foreach (var t in owner.GetComponentsInChildren<Transform>(false))
                    if (t.name.StartsWith("crossbow", StringComparison.OrdinalIgnoreCase) && t.GetComponent<Renderer>() != null) { info.Weapon = t; break; }
            if (info.Weapon == null) return false;
            info.IsGun = true; info.Kind = Classify(info.Weapon.name); kind = info.Kind;
            return true;
        }
        internal static void Sweep()
        {
            if (Time.unscaledTime < _nextSweep) return;
            _nextSweep = Time.unscaledTime + 30f;
            _deadShooters.Clear();
            foreach (var kv in _shooters) if (kv.Value.Owner == null) _deadShooters.Add(kv.Key);
            foreach (var id in _deadShooters) _shooters.Remove(id);
        }
        internal static void OnSceneLoaded() { _shooters.Clear(); _deadShooters.Clear(); _nextSweep = 0f; }
    }
}
