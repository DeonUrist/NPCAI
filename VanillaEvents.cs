using System;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Micosmo.SensorToolkit.PlayMaker;
using UnityEngine;

namespace NPCAI
{
    // Observe vanilla actions without replacing them. Gunplay sends corresponding events
    // at projectile creation/impact, including its immediate over-cap fallback.
    internal static class VanillaEvents
    {
        internal sealed class RayState { public Micosmo.SensorToolkit.RaySensor Sensor; public float Length; }
        public static void BeforeNpcShot(SensorGetDetectionRayHit __instance, out RayState __state)
        {
            __state = null;
            try
            {
                if (WeaponRanges.ProjectilesEnabled || (!Senses.On && !Plugin.AimEnabled.Value && !Plugin.BrainEnabled.Value)) return;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Damage Ranged" || fsm.GameObject == null) return;
                WeaponRanges.Kind kind;
                var owner = fsm.GameObject;
                if (!WeaponRanges.GunKindOf(owner, out kind)) return;
                // Vanilla's gun sensor is only 80 m. Aim chooses positions from the
                // effective range; repulse at that reach so rifle/sniper NPCs can hit
                // from those positions without requiring Gunplay's projectiles.
                if (Plugin.AimEnabled.Value || Plugin.BrainEnabled.Value)
                {
                    var rayObject = owner.transform.Find("AttackRaycast_Ranged");
                    var sensor = rayObject != null ? rayObject.GetComponent<Micosmo.SensorToolkit.RaySensor>() : null;
                    if (sensor != null)
                    {
                        __state = new RayState { Sensor = sensor, Length = sensor.Length };
                        sensor.Length = WeaponRanges.RangeOf(kind);
                        sensor.Pulse();
                    }
                }
                Vector3 origin = owner.transform.position;
                foreach (var t in owner.GetComponentsInChildren<Transform>(true))
                    if (t.name == "fire_effect" && t.parent != null && t.parent.name != "fire_effect" && t.parent.gameObject.activeInHierarchy)
                    { origin = t.position; break; }
                if (Senses.On) Senses.Shot(owner.transform.root.gameObject, origin, kind, false);
            }
            catch (Exception e) { Plugin.Warn("Vanilla NPC shot observation: " + e.Message); }
        }

        public static void AfterNpcShot(RayState __state)
        { if (__state != null && __state.Sensor != null) __state.Sensor.Length = __state.Length; }

        internal static bool PlayerWeapon(GameObject weapon)
        {
            for (var t = weapon != null ? weapon.transform : null; t != null; t = t.parent)
                if (t.name == "PlayerCamera" || t.name == "PlayerCameraHolder") return true;
            return false;
        }

        public static void BeforePlayerShot(Raycast __instance)
        {
            try
            {
                if (!Senses.On || WeaponRanges.ProjectilesEnabled) return;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || fsm.GameObject == null || __instance.repeatInterval == null || __instance.repeatInterval.Value != 0f) return;
                bool reload = false;
                foreach (var f in fsm.GameObject.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Reload") { reload = true; break; }
                if (!reload || !PlayerWeapon(fsm.GameObject)) return;
                var kind = WeaponRanges.Classify(fsm.GameObject.name);
                bool shotgun = false;
                var hit = fsm.GetState("hit");
                if (hit != null && hit.Actions != null)
                    foreach (var a in hit.Actions)
                    { var ic = a as IntCompare; if (ic != null && ic.integer1 != null && ic.integer1.Name == "pellets" && ic.integer2 != null) { shotgun = true; break; } }
                if (shotgun) kind = WeaponRanges.Kind.Shotgun;
                else if (kind == WeaponRanges.Kind.Shotgun) kind = WeaponRanges.Kind.Rifle;
                var from = fsm.GetOwnerDefaultTarget(__instance.fromGameObject);
                Vector3 position = from != null ? from.transform.position : fsm.GameObject.transform.position;
                foreach (var t in fsm.GameObject.GetComponentsInChildren<Transform>(true))
                    if ((t.name.StartsWith("muzzle_flesh_effect", StringComparison.Ordinal) || t.name.StartsWith("muzzle_flash", StringComparison.Ordinal)) && t.gameObject.activeInHierarchy)
                    { position = t.position; break; }
                Senses.Shot(GameObject.Find("Player"), position, kind, true);
            }
            catch (Exception e) { Plugin.Warn("Vanilla player shot observation: " + e.Message); }
        }

        public static void AfterDamage(SetFsmFloat __instance)
        {
            try
            {
                if (!Senses.On || __instance.fsmName == null || __instance.fsmName.Value != "Bodypart" || __instance.variableName == null || __instance.variableName.Value != "Damage" || __instance.setValue == null || __instance.setValue.Value >= 0f) return;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.GameObject == null) return;
                bool player = fsm.Name == "Attack" && PlayerWeapon(fsm.GameObject);
                bool ranged = fsm.Name == "Damage Ranged";
                if (!player && !ranged) return;
                // Melee stays vanilla with Gunplay installed, and still needs this observer.
                bool gun = ranged;
                if (player)
                    foreach (var f in fsm.GameObject.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Reload") { gun = true; break; }
                if (gun && WeaponRanges.ProjectilesEnabled) return;
                var target = fsm.GetOwnerDefaultTarget(__instance.gameObject);
                if (target == null) return;
                Senses.Hurt(target, player ? GameObject.Find("Player") : fsm.GameObject.transform.root.gameObject);
            }
            catch (Exception e) { Plugin.Warn("Vanilla hurt observation: " + e.Message); }
        }
    }
}
