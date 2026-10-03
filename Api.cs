using UnityEngine;

namespace NPCAI
{
    /// <summary>Version-one optional contract. All argument types belong to the game or CLR.</summary>
    public static class Api
    {
        public const int ContractVersion = 1;
        public static void Shot(GameObject shooter, Vector3 position, int weaponKind, bool player)
        { if (weaponKind >= 0 && weaponKind <= 5) Senses.Shot(shooter, position, (WeaponRanges.Kind)weaponKind, player); }
        public static void Hurt(GameObject victim, GameObject attacker) { Senses.Hurt(victim, attacker); }
        public static void Hurt(GameObject victim, bool player)
        { if (player) Senses.Hurt(victim, GameObject.Find("Player")); }
        public static float SpreadFactor(float distance)
        { return Plugin.AimEnabled.Value ? Aim.SpreadFactor(distance) : 1f; }
        public static void Inform(GameObject npc, GameObject target) { Senses.Inform(npc, target); }
    }
}
