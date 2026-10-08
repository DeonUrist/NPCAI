using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAIProbe
{
    // ground.log (0.1.8): what is under the player's feet and under every Scorpion_Small near the camera - the collider (type, name, layer, tag,
    // physic material) and, on a terrain, its texture layers and their weights at that spot. Written when the dominant layer / collider changes
    // and at least every 10 s. Also writes prefabs\Burrower_Effect.txt once (the sand worm's puff: audio source, particle systems, FSM).
    // Built to learn what "sand" is in this game for NPCAI's burrowing scorpions.
    internal static class GroundTrace
    {
        private static float _next; private static readonly Dictionary<int, string> _last = new Dictionary<int, string>(); private static readonly Dictionary<int, float> _lastAt = new Dictionary<int, float>();
        private static readonly StringBuilder _buf = new StringBuilder(); private static bool _effectDone; private static float _nextEffect;

        internal static void Pump(float now)
        {
            if (now >= _nextEffect && !_effectDone) { _nextEffect = now + 15f; try { DumpEffect(); } catch (Exception e) { Plugin.Log.LogError("Probe effect: " + e); } }
            if (now < _next) return;
            _next = now + 2f;
            try { Sample(now); } catch (Exception e) { Plugin.Log.LogError("Probe ground: " + e); }
            if (_buf.Length > 0) { try { File.AppendAllText(Path.Combine(Plugin.OutDir, "ground.log"), _buf.ToString()); } catch (Exception) { } _buf.Length = 0; }
        }

        private static void Sample(float now)
        {
            var pl = GameObject.FindGameObjectWithTag("Player");
            if (pl != null) One(now, 1, "player", pl.transform.position);
            var cam = Camera.main; if (cam == null) return;
            foreach (var rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (rb == null || rb.transform.parent != null || !rb.name.StartsWith("Scorpion_Small")) continue;
                if ((rb.position - cam.transform.position).sqrMagnitude > 40f * 40f) continue;
                One(now, rb.gameObject.GetInstanceID(), rb.name, rb.position);
            }
        }

        private static void One(float now, int key, string who, Vector3 p)
        {
            var sb = new StringBuilder();
            RaycastHit h;
            if (!Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out h, 4f, ~0, QueryTriggerInteraction.Ignore)) { sb.Append("no ground within 4 m"); }
            else
            {
                var c = h.collider;
                sb.Append(c.GetType().Name).Append(" '").Append(c.name).Append("' root '").Append(c.transform.root.name).Append("' layer ").Append(c.gameObject.layer).Append(" tag ").Append(c.tag);
                sb.Append(" mat ").Append(c.sharedMaterial != null ? c.sharedMaterial.name : "-");
                var mr = c.GetComponent<Renderer>();
                if (mr != null && mr.sharedMaterial != null) sb.Append(" renderer-mat ").Append(mr.sharedMaterial.name);
                var t = c.GetComponent<Terrain>();
                if (t != null && t.terrainData != null)
                {
                    var d = t.terrainData; var layers = d.terrainLayers;
                    sb.Append(" | terrain '").Append(t.name).Append("' layers ").Append(d.alphamapLayers).Append(" alphamap ").Append(d.alphamapWidth).Append("x").Append(d.alphamapHeight).Append(" size ").Append(d.size.ToString("0"));
                    sb.Append(" matType ").Append(t.materialTemplate != null ? t.materialTemplate.shader.name : "default");
                    if (d.alphamapLayers > 0)
                    {
                        Vector3 lp = h.point - t.transform.position;
                        int ax = Mathf.Clamp((int)(lp.x / d.size.x * d.alphamapWidth), 0, d.alphamapWidth - 1), az = Mathf.Clamp((int)(lp.z / d.size.z * d.alphamapHeight), 0, d.alphamapHeight - 1);
                        var w = d.GetAlphamaps(ax, az, 1, 1);
                        var parts = new List<KeyValuePair<float, string>>();
                        for (int i = 0; i < w.GetLength(2); i++)
                        {
                            string n = i < layers.Length && layers[i] != null ? layers[i].name + (layers[i].diffuseTexture != null ? "/" + layers[i].diffuseTexture.name : "") : "#" + i;
                            parts.Add(new KeyValuePair<float, string>(w[0, 0, i], n));
                        }
                        sb.Append(" | weights: ").Append(string.Join(", ", parts.Where(x => x.Key > 0.02f).OrderByDescending(x => x.Key).Select(x => x.Value + " " + x.Key.ToString("0.00")).ToArray()));
                    }
                }
            }
            string line = sb.ToString();
            // dominant part only for change detection
            string sig = line.Length > 160 ? line.Substring(0, 160) : line; int wi = line.IndexOf("weights:"); if (wi >= 0) { int e = line.IndexOf(',', wi); sig += line.Substring(wi, (e < 0 ? line.Length : e) - wi); }
            string prev; float at;
            bool changed = !_last.TryGetValue(key, out prev) || prev != sig;
            if (!changed && _lastAt.TryGetValue(key, out at) && now - at < 10f) return;
            _last[key] = sig; _lastAt[key] = now;
            _buf.Append(Time.time.ToString("0.0")).Append(' ').Append(who).Append(" @").Append(p.ToString("0")).Append(" -> ").Append(line).AppendLine();
        }

        private static void DumpEffect()
        {
            GameObject fx = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null || f.FsmName != "DestroySelf") continue;
                var go = f.gameObject;
                if (go.scene.IsValid() || go.transform.parent != null || go.name != "Burrower_Effect") continue;
                fx = go; break;
            }
            if (fx == null) return;
            _effectDone = true;
            var sb = new StringBuilder();
            sb.AppendLine("# Burrower_Effect (prefab asset), " + DateTime.Now);
            foreach (var t in fx.GetComponentsInChildren<Transform>(true))
                sb.AppendLine("  " + Plugin.PathOf(t) + " local " + t.localPosition.ToString("0.00") + " scale " + t.localScale.ToString("0.00") + " components: " + string.Join(",", t.GetComponents<Component>().Where(x => x != null).Select(x => x.GetType().Name).ToArray()));
            foreach (var a in fx.GetComponentsInChildren<AudioSource>(true))
                sb.AppendLine("AudioSource on " + a.name + ": clip " + (a.clip != null ? a.clip.name + " " + a.clip.length.ToString("0.00") + "s" : "none") + " volume " + a.volume + " pitch " + a.pitch + " spatialBlend " + a.spatialBlend + " rolloff " + a.rolloffMode + " min " + a.minDistance + " max " + a.maxDistance + " playOnAwake " + a.playOnAwake + " loop " + a.loop);
            foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>(true))
            {
                var m = ps.main;
                sb.AppendLine("ParticleSystem " + ps.name + ": duration " + m.duration.ToString("0.00") + " loop " + m.loop + " startLifetime " + m.startLifetime.constantMax.ToString("0.00") + " startSize " + m.startSize.constantMax.ToString("0.00") + " startSpeed " + m.startSpeed.constantMax.ToString("0.00") + " maxParticles " + m.maxParticles + " scalingMode " + m.scalingMode + " simSpace " + m.simulationSpace + " playOnAwake " + m.playOnAwake);
            }
            foreach (var f in fx.GetComponents<PlayMakerFSM>())
                if (f.Fsm != null && f.Fsm.States != null)
                    foreach (var st in f.Fsm.States)
                        foreach (var act in st.Actions ?? new FsmStateAction[0])
                        {
                            var line = "FSM " + f.FsmName + " state " + st.Name + ": " + act.GetType().Name;
                            var sc = act as SetAudioClip; if (sc != null) line += " clip=" + (sc.audioClip != null && sc.audioClip.Value != null ? sc.audioClip.Value.name : "null");
                            var ap = act as AudioPlay; if (ap != null) line += " volume=" + (ap.volume != null ? ap.volume.Value.ToString() : "-") + " oneShot=" + ap.oneShotClip;
                            sb.AppendLine(line);
                        }
            File.WriteAllText(Path.Combine(Path.Combine(Plugin.OutDir, "prefabs"), "Burrower_Effect.txt"), sb.ToString());
            Plugin.Log.LogInfo("NPCAIProbe: Burrower_Effect dumped");
        }
    }
}
