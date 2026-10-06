using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // (1.1.2) The reload sounds of a gunman: the clips the player's own copy of that gun plays while reloading (its "AnimSound" FSM -
    // mag_out / mag_in / pistol_slide for the AKs, bolt_action / shotgun_shell_insert for the shotguns, metal_screwing / ammo_in for the
    // pipe pistol ...), found once per weapon key under PlayerCameraHolder/PlayerCamera/WeaponsArm/Parent/<key> and spread evenly over
    // the NPC's reload time, played as 3D sounds at the NPC (linear rolloff to [NpcAim] ReloadSoundRange m).
    internal static class ReloadSounds
    {
        private static readonly Dictionary<string, AudioClip[]> _clips = new Dictionary<string, AudioClip[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly AudioClip[] _none = new AudioClip[0];

        private static AudioClip[] ClipsOf(string key)
        {
            AudioClip[] list;
            if (key == null) return _none;
            if (_clips.TryGetValue(key, out list)) return list;
            var found = new List<AudioClip>();
            try
            {
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                {
                    if (f == null || f.FsmName != "AnimSound" || !f.gameObject.scene.IsValid()) continue;
                    if (!string.Equals(f.gameObject.name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    var t = f.transform; bool player = false;
                    while (t != null) { if (t.name == "WeaponsArm") { player = true; break; } t = t.parent; }
                    if (!player || f.Fsm == null) continue;
                    foreach (var st in f.Fsm.States)
                    {
                        if (st == null || st.Actions == null) continue;
                        foreach (var a in st.Actions)
                        {
                            var ps = a as PlaySound; var sc = a as SetAudioClip;
                            AudioClip c = ps != null && ps.clip != null ? ps.clip.Value as AudioClip : sc != null && sc.audioClip != null ? sc.audioClip.Value as AudioClip : null;
                            if (c != null && !found.Contains(c)) found.Add(c);
                        }
                    }
                    break;
                }
            }
            catch (Exception e) { Plugin.Warn("Reload sounds: " + e.Message); }
            list = found.ToArray();
            _clips[key] = list;
            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Reload sounds for " + key + ": " + (list.Length == 0 ? "none found" : string.Join(", ", Array.ConvertAll(list, c => c.name))));
            return list;
        }

        internal static void Clear() { _clips.Clear(); }

        // called every frame while the NPC reloads: plays clip i at (i + 0.5) / count of the reload time
        internal static void Tick(Brain.Npc n, float now)
        {
            if (Plugin.ReloadVolume.Value <= 0f || n.ReloadLen <= 0.05f) return;
            var clips = ClipsOf(n.WeaponKey);
            if (clips.Length == 0 || n.ReloadSoundsPlayed >= clips.Length) return;
            float start = n.ReloadUntil - n.ReloadLen;
            float due = start + (n.ReloadSoundsPlayed + 0.5f) / clips.Length * n.ReloadLen;
            if (now < due) return;
            var clip = clips[n.ReloadSoundsPlayed++];
            Vector3 at = n.Weapon != null ? n.Weapon.position : n.T.position + Vector3.up * 1.3f;
            Play(clip, at, Plugin.ReloadVolume.Value);
        }

        private static void Play(AudioClip clip, Vector3 at, float volume)
        {
            var go = new GameObject("NPCAI.ReloadSound");
            go.transform.position = at;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip; src.volume = Mathf.Clamp01(volume); src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear; src.minDistance = 2f; src.maxDistance = Mathf.Max(5f, Plugin.ReloadSoundRange.Value);
            src.dopplerLevel = 0f;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.1f);
        }
    }
}
