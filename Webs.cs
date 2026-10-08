using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace NPCAI
{
    // (1.6.0, TEST) Spider webs drawn in code - no asset in the game is a web. A web is real web geometry built once into one static mesh
    // (one GameObject, one MeshRenderer, one draw call, nothing per frame): thin flat strands with a soft-edged thread texture (mipmapped, so
    // strands thin out naturally with distance and vanish edge-on like real silk), white, semi-transparent, darkened with the game's day/night
    // light so they never glow at night. Two kinds:
    //  * an orb web hung in the air between rocks / wrecks / walls / the ground: spokes to the surfaces around, a frame, a sagging capture
    //    spiral with gaps and uneven spacing, a tangled hub;
    //  * a sheet web on the ground: an irregular net of strands following the bumps of the terrain, denser in the middle, fraying at the edge.
    // Test: [Webs] TestKey (F9) puts a web where you look (a hanging web if there are surfaces around to hold it, a sheet on the ground);
    // Shift + the key removes them all. Nothing else uses webs yet.
    internal static class Webs
    {
        internal sealed class Web { public GameObject Go; public Mesh Mesh; public float Born; public bool Orb; public Vector3 Center, N; public float R; public Renderer Rend; public float Sky = 1f, Shown = -1f; }

        private static readonly List<Web> _webs = new List<Web>();       // the F9 test webs
        private static readonly List<Web> _all = new List<Web>();        // (1.7.3) every web (test and nests): lit each second / by the flashlight
        private static MaterialPropertyBlock _mpb; private static float _k = 0.88f; private static Light _flLight; private static Transform _flOf;
        private static Material _mat; private static Texture2D _tex; private static float _nextTint;
        private static Key _key = Key.None; private static string _keySrc;
        private const int Anchors = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 14) | (1 << 16);    // Default, Car, Door, Ground, SeeThrough
        private const int GroundMask = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 14) | (1 << 16);

        // mesh building buffers, reused (no garbage per web beyond the mesh itself)
        private static readonly List<Vector3> _v = new List<Vector3>(8192);
        private static readonly List<Vector2> _uv = new List<Vector2>(8192);
        private static readonly List<Color32> _c = new List<Color32>(8192);
        private static readonly List<int> _t = new List<int>(12288);
        private static Vector3 _origin;

        internal static void OnSceneLoaded() { Clear(); _all.Clear(); _flOf = null; _flLight = null; }

        internal static void Tick()
        {
            float now = Time.time;
            if (_all.Count > 0) { if (now >= _nextTint) { _nextTint = now + 1f; Tint(); } else Flash(false); }
            if (_webs.Count > 0)
            {
                float life = Plugin.WebTestLifetime.Value;
                if (life > 0f && now - _webs[0].Born > life) { Kill(_webs[0]); _webs.RemoveAt(0); }
            }
            var kb = Keyboard.current;
            if (kb == null) return;
            string cfg = Plugin.WebTestKey.Value ?? "";
            if (_keySrc != cfg) { _keySrc = cfg; _key = Key.None; try { if (cfg.Trim().Length > 0) _key = (Key)Enum.Parse(typeof(Key), cfg.Trim(), true); } catch (Exception) { Plugin.Warn("Webs: unknown key '" + cfg + "'"); } }
            if (_key == Key.None || !kb[_key].wasPressedThisFrame) return;
            if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) { int n = _webs.Count; Clear(); Plugin.Log.LogInfo("Webs: removed " + n + " test web(s)"); return; }
            try { PlaceAtAim(); } catch (Exception e) { Plugin.Log.LogError("Webs: " + e); }
        }

        private static void Clear() { foreach (var w in _webs) Kill(w); _webs.Clear(); }
        internal static void Kill(Web w) { _all.Remove(w); if (w.Go != null) UnityEngine.Object.Destroy(w.Go); if (w.Mesh != null) UnityEngine.Object.Destroy(w.Mesh); }

        // ---------- the test: a web where the camera looks ----------
        private static void PlaceAtAim()
        {
            var cam = Camera.main; if (cam == null) return;
            RaycastHit h;
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out h, 40f, Anchors, QueryTriggerInteraction.Ignore)) { Plugin.Log.LogInfo("Webs: nothing within 40 m where you look"); return; }
            var sw = Stopwatch.StartNew();
            Vector3 toCam = cam.transform.position - h.point; toCam.y = 0f; toCam = toCam.sqrMagnitude > 0.001f ? toCam.normalized : Vector3.forward;
            string made = "";
            // a hanging web: in front of a wall / rock face, or about a metre above the ground, wherever surfaces around can hold it
            Vector3 hub = h.normal.y < 0.6f ? h.point + h.normal * 0.45f + Vector3.up * 0.2f : h.point + Vector3.up * UnityEngine.Random.Range(0.8f, 1.2f);
            Vector3 n1 = toCam, n2 = Vector3.Cross(Vector3.up, toCam);
            if (h.normal.y < 0.6f) { n1 = Vector3.Cross(Vector3.up, h.normal).normalized; n2 = (n1 + toCam).normalized; }     // across a corner, at an angle to the face
            var orb = Orb(hub, n1) ?? Orb(hub, n2);
            if (orb != null) { Add(orb); made = "a hanging web (" + orb.Mesh.vertexCount + " vertices)"; }
            if (h.normal.y >= 0.6f || orb == null)
            {
                RaycastHit g;
                Vector3 gp = h.normal.y >= 0.6f ? h.point : (Physics.Raycast(h.point + h.normal * 0.6f + Vector3.up, Vector3.down, out g, 5f, GroundMask, QueryTriggerInteraction.Ignore) ? g.point : h.point);
                var sheet = Sheet(gp);
                if (sheet != null) { Add(sheet); made += (made.Length > 0 ? " and " : "") + "a ground web (" + sheet.Mesh.vertexCount + " vertices)"; }
            }
            sw.Stop();
            Plugin.Log.LogInfo("Webs: " + (made.Length > 0 ? made : "no room for a web") + " in " + sw.Elapsed.TotalMilliseconds.ToString("0.00") + " ms (" + _webs.Count + " test webs; " + (orb == null ? "nothing around to hang a web on, aim between rocks / at a wreck or wall" : "hung") + ")");
        }

        private static void Add(Web w)
        {
            w.Born = Time.time;
            _webs.Add(w);
            int cap = Mathf.Max(1, Plugin.WebTestMax.Value);
            while (_webs.Count > cap) { Kill(_webs[0]); _webs.RemoveAt(0); }
            Tint();
        }

        // ---------- the orb web ----------
        // spokes from the hub in the plane (normal n); a spoke that reaches a surface within MaxR is tied to it. Needs 3 tied spokes around
        // the hub (no gap over 150 degrees between them), else null.
        internal static Web Orb(Vector3 hub, Vector3 n)
        {
            n.Normalize();
            Vector3 u = Vector3.Cross(n, Vector3.up); if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(n, Vector3.forward); u.Normalize();
            Vector3 v = Vector3.Cross(u, n).normalized;
            const float maxR = 2.6f;
            int S = UnityEngine.Random.Range(14, 21);
            var ang = new float[S]; var len = new float[S]; var tied = new bool[S]; var end = new Vector3[S];
            float step = 360f / S; int tiedCount = 0; float sumTied = 0f;
            for (int i = 0; i < S; i++)
            {
                ang[i] = i * step + UnityEngine.Random.Range(-0.3f, 0.3f) * step;
                Vector3 d = Dir(u, v, ang[i]);
                RaycastHit h;
                if (Physics.Raycast(hub, d, out h, maxR, Anchors, QueryTriggerInteraction.Ignore) && h.distance > 0.25f) { tied[i] = true; len[i] = h.distance; end[i] = h.point; tiedCount++; sumTied += h.distance; }
            }
            if (tiedCount < 3) return null;
            float maxGap = 0f, first = -1f, prev = -1f;
            for (int i = 0; i < S; i++) if (tied[i]) { if (prev >= 0f) maxGap = Mathf.Max(maxGap, ang[i] - prev); else first = ang[i]; prev = ang[i]; }
            maxGap = Mathf.Max(maxGap, first + 360f - prev);
            if (maxGap > 150f) return null;
            // the frame: untied spokes end on a frame thread at about the reach of their tied neighbours
            float meanTied = sumTied / tiedCount;
            var frame = new Vector3[S]; var spokeLen = new float[S];
            for (int i = 0; i < S; i++)
            {
                float r = tied[i] ? Mathf.Min(len[i] * UnityEngine.Random.Range(0.7f, 0.85f), maxR * 0.85f) : NeighbourReach(tied, len, i, S, meanTied) * UnityEngine.Random.Range(0.6f, 0.8f);
                r = Mathf.Clamp(r, 0.25f, 1.9f);
                spokeLen[i] = r;
                frame[i] = hub + Dir(u, v, ang[i]) * r;
            }
            Begin(hub);
            // anchor lines: frame corner -> the surface (with a little sag)
            for (int i = 0; i < S; i++) if (tied[i]) Strand(frame[i], end[i], n, 0.009f, A(0.55f), 0.02f, 3);
            // frame threads between neighbouring spoke ends
            for (int i = 0; i < S; i++) Strand(frame[i], frame[(i + 1) % S], n, 0.008f, A(0.6f), 0.025f, 3);
            // spokes
            for (int i = 0; i < S; i++) Strand(hub, frame[i], n, 0.006f, A(0.5f), 0f, 1);
            // the capture spiral: from 85 % of each spoke inwards to 18 %, spacing 3-4.5 cm (uneven), sagging slightly between spokes, a few gaps
            float meanLen = 0f; for (int i = 0; i < S; i++) meanLen += spokeLen[i]; meanLen /= S;
            float spacing = UnityEngine.Random.Range(0.03f, 0.045f);
            int turns = Mathf.Clamp(Mathf.RoundToInt(meanLen * (0.85f - 0.18f) / spacing), 6, 40);
            int total = turns * S;
            Vector3 last = Vector3.zero; bool haveLast = false;
            for (int k = 0; k <= total; k++)
            {
                int i = k % S;
                float f = 0.85f - (0.85f - 0.18f) * k / (float)total;
                f *= 1f + UnityEngine.Random.Range(-0.04f, 0.04f);
                Vector3 p = hub + (frame[i] - hub) * f;
                if (haveLast && UnityEngine.Random.value > 0.04f)
                {
                    Vector3 mid = (last + p) * 0.5f;
                    Vector3 sag = (hub - mid) * 0.06f + Vector3.down * 0.004f;          // pulled a little towards the hub and down
                    Strand2(last, mid + sag, p, n, 0.0045f, A(0.42f));
                }
                last = p; haveLast = true;
            }
            // the hub: a small tangle and a few tight loops
            float hubR = Mathf.Clamp(meanLen * 0.1f, 0.05f, 0.14f);
            for (int k = 0; k < 26; k++)
            {
                Vector3 a = hub + Dir(u, v, UnityEngine.Random.Range(0f, 360f)) * UnityEngine.Random.Range(0f, hubR);
                Vector3 b = hub + Dir(u, v, UnityEngine.Random.Range(0f, 360f)) * UnityEngine.Random.Range(0f, hubR);
                Strand(a, b, n, 0.004f, A(0.5f), 0f, 1);
            }
            var web = End("Web_Orb");
            if (web != null) { web.Orb = true; web.Center = hub; web.N = n; float mx = 0f; for (int i = 0; i < S; i++) mx = Mathf.Max(mx, spokeLen[i]); web.R = mx; }
            return web;
        }

        // (1.7.0) a cheap look at a spot before building there: 8 rays in the plane; how many reach a surface within 2.6 m, the widest gap
        // between them (degrees) and their mean length
        internal static int Probe(Vector3 hub, Vector3 n, out float maxGap, out float mean)
        {
            n.Normalize();
            Vector3 u = Vector3.Cross(n, Vector3.up); if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(n, Vector3.forward); u.Normalize();
            Vector3 v = Vector3.Cross(u, n).normalized;
            int hits = 0; float sum = 0f, first = -1f, prev = -1f; maxGap = 360f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * 45f; RaycastHit h;
                if (Physics.Raycast(hub, Dir(u, v, a), out h, 2.6f, Anchors, QueryTriggerInteraction.Ignore) && h.distance > 0.35f)
                {
                    hits++; sum += h.distance;
                    if (prev >= 0f) maxGap = hits == 2 ? a - prev : Mathf.Max(maxGap, a - prev); else first = a;
                    prev = a;
                }
            }
            if (hits >= 2) maxGap = Mathf.Max(maxGap, first + 360f - prev);
            mean = hits > 0 ? sum / hits : 0f;
            return hits;
        }

        private static float NeighbourReach(bool[] tied, float[] len, int i, int S, float fallback)
        {
            for (int k = 1; k < S; k++) { if (tied[(i + k) % S]) return Mathf.Min(len[(i + k) % S], fallback * 1.2f); if (tied[(i - k + S) % S]) return Mathf.Min(len[(i - k + S) % S], fallback * 1.2f); }
            return fallback;
        }

        private static Vector3 Dir(Vector3 u, Vector3 v, float deg) { float r = deg * Mathf.Deg2Rad; return u * Mathf.Cos(r) + v * Mathf.Sin(r); }

        // ---------- the sheet web on the ground ----------
        internal static Web Sheet(Vector3 center)
        {
            float R = UnityEngine.Random.Range(0.7f, 1.3f);
            // the ground under it: a 9 x 9 height grid (81 rays, once), strands read it bilinearly
            const int G = 9; var hgt = new float[G, G]; float cell = 2.2f * R / (G - 1);
            Vector3 corner = center - new Vector3(1.1f * R, 0f, 1.1f * R);
            for (int x = 0; x < G; x++) for (int z = 0; z < G; z++)
            {
                Vector3 p = corner + new Vector3(x * cell, 0f, z * cell);
                RaycastHit h;
                hgt[x, z] = Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out h, 3f, GroundMask, QueryTriggerInteraction.Ignore) ? h.point.y : center.y;
            }
            // knots: denser in the middle; a ring of edge knots tied higher (grass tips, pebbles)
            int inner = 34, edge = 11;
            var pts = new Vector2[inner + edge]; var lift = new float[inner + edge];
            for (int i = 0; i < inner; i++) { float a = UnityEngine.Random.Range(0f, Mathf.PI * 2f), r = R * Mathf.Pow(UnityEngine.Random.value, 0.75f) * 0.85f; pts[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r); lift[i] = UnityEngine.Random.Range(0.012f, 0.035f); }
            for (int i = 0; i < edge; i++) { float a = (i + UnityEngine.Random.Range(-0.35f, 0.35f)) * Mathf.PI * 2f / edge, r = R * UnityEngine.Random.Range(0.9f, 1.05f); pts[inner + i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r); lift[inner + i] = UnityEngine.Random.Range(0.04f, 0.1f); }
            Begin(center);
            int N = pts.Length;
            var done = new HashSet<int>();
            for (int i = 0; i < N; i++)
            {
                // tied to its 3 nearest knots (an irregular net)
                for (int pick = 0; pick < 3; pick++)
                {
                    int best = -1; float bd = float.MaxValue;
                    for (int j = 0; j < N; j++)
                    {
                        if (j == i) continue;
                        int key = i < j ? i * 1000 + j : j * 1000 + i;
                        if (done.Contains(key)) continue;
                        float d = (pts[j] - pts[i]).sqrMagnitude;
                        if (d < bd) { bd = d; best = j; }
                    }
                    if (best < 0 || bd > R * R * 0.5f) break;
                    done.Add(i < best ? i * 1000 + best : best * 1000 + i);
                    GroundStrand(center, corner, cell, hgt, G, pts[i], pts[best], lift[i], lift[best], A(i >= inner || best >= inner ? 0.38f : 0.5f));
                }
            }
            // the dense middle: short criss-cross threads
            for (int k = 0; k < 18; k++)
            {
                Vector2 a = UnityEngine.Random.insideUnitCircle * R * 0.22f, b = UnityEngine.Random.insideUnitCircle * R * 0.22f;
                GroundStrand(center, corner, cell, hgt, G, a, b, 0.02f, 0.02f, A(0.55f));
            }
            var sheet = End("Web_Sheet");
            if (sheet != null) { sheet.Orb = false; sheet.Center = center; sheet.N = Vector3.up; sheet.R = R; }
            return sheet;
        }

        private static void GroundStrand(Vector3 center, Vector3 corner, float cell, float[,] hgt, int G, Vector2 a, Vector2 b, float la, float lb, Color32 col)
        {
            float d = (b - a).magnitude;
            int seg = Mathf.Clamp(Mathf.CeilToInt(d / 0.15f), 1, 16);
            Vector3 prev = Vector3.zero;
            for (int s = 0; s <= seg; s++)
            {
                float t = s / (float)seg;
                Vector2 q = Vector2.Lerp(a, b, t);
                float x = center.x + q.x, z = center.z + q.y;
                float y = Height(corner, cell, hgt, G, x, z) + Mathf.Lerp(la, lb, t) - Mathf.Sin(t * Mathf.PI) * Mathf.Min(0.01f, d * 0.01f);
                Vector3 p = new Vector3(x, Mathf.Max(y, Height(corner, cell, hgt, G, x, z) + 0.006f), z);
                if (s > 0) Quad(prev, p, Vector3.up, 0.005f, col);
                prev = p;
            }
        }

        private static float Height(Vector3 corner, float cell, float[,] h, int G, float x, float z)
        {
            float fx = Mathf.Clamp((x - corner.x) / cell, 0f, G - 1.001f), fz = Mathf.Clamp((z - corner.z) / cell, 0f, G - 1.001f);
            int ix = (int)fx, iz = (int)fz; float tx = fx - ix, tz = fz - iz;
            return Mathf.Lerp(Mathf.Lerp(h[ix, iz], h[ix + 1, iz], tx), Mathf.Lerp(h[ix, iz + 1], h[ix + 1, iz + 1], tx), tz);
        }

        // ---------- mesh bits ----------
        private static Color32 A(float alpha) { float a = Mathf.Clamp01(alpha * UnityEngine.Random.Range(0.75f, 1.15f)); byte w = (byte)UnityEngine.Random.Range(225, 256); return new Color32(w, w, w, (byte)(a * 255f)); }

        private static void Begin(Vector3 origin) { _origin = origin; _v.Clear(); _uv.Clear(); _c.Clear(); _t.Clear(); }

        // a thread a -> b sagging by `sag` m in the middle, in `pieces` straight pieces
        private static void Strand(Vector3 a, Vector3 b, Vector3 n, float w, Color32 col, float sag, int pieces)
        {
            Vector3 prev = a;
            for (int i = 1; i <= pieces; i++)
            {
                float t = i / (float)pieces;
                Vector3 p = Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t));
                Quad(prev, p, n, w, col); prev = p;
            }
        }

        private static void Strand2(Vector3 a, Vector3 m, Vector3 b, Vector3 n, float w, Color32 col) { Quad(a, m, n, w, col); Quad(m, b, n, w, col); }

        // one flat strip in the plane with normal n (the texture's soft edge across its width)
        private static void Quad(Vector3 a, Vector3 b, Vector3 n, float w, Color32 col)
        {
            if (_v.Count > 64000) return;
            Vector3 d = b - a; if (d.sqrMagnitude < 1e-8f) return;
            Vector3 s = Vector3.Cross(d, n); if (s.sqrMagnitude < 1e-10f) s = Vector3.Cross(d, Vector3.up);
            s = s.normalized * (w * 0.5f);
            int i0 = _v.Count;
            a -= _origin; b -= _origin;
            _v.Add(a - s); _v.Add(a + s); _v.Add(b + s); _v.Add(b - s);
            _uv.Add(new Vector2(0f, 0f)); _uv.Add(new Vector2(1f, 0f)); _uv.Add(new Vector2(1f, 1f)); _uv.Add(new Vector2(0f, 1f));
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _t.Add(i0); _t.Add(i0 + 1); _t.Add(i0 + 2); _t.Add(i0); _t.Add(i0 + 2); _t.Add(i0 + 3);
        }

        private static Web End(string name)
        {
            if (_v.Count == 0) return null;
            var mesh = new Mesh { name = name };
            mesh.indexFormat = _v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_v); mesh.SetUVs(0, _uv); mesh.SetColors(_c); mesh.SetTriangles(_t, 0, true);
            mesh.UploadMeshData(true);          // no CPU copy kept: it never changes
            var go = new GameObject(name);
            go.transform.position = _origin;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Mat();
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off; r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            var web = new Web { Go = go, Mesh = mesh, Rend = r, Sky = SkyOpen(_origin + Vector3.up * 0.3f) };
            _all.Add(web);
            Apply(web, Level(web), true);
            return web;
        }

        // one shared material for every web: an alpha-blended, double-sided, unlit shader the game ships (Sprites/Default), the colour
        // set from the game's light level once a second (dim grey at night, pale white by day)
        private static Material Mat()
        {
            if (_mat != null) return _mat;
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Particles/Standard Unlit");
            _mat = new Material(sh) { name = "NPCAI_Web", renderQueue = 3000 };
            _mat.mainTexture = Tex();
            Tint();
            return _mat;
        }

        private static Texture2D Tex()
        {
            if (_tex != null) return _tex;
            const int W = 32, H = 4;
            _tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = "NPCAI_Thread", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color32[W * H];
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W - 0.5f;
                byte a = (byte)(255f * Mathf.Exp(-(u * u) / (2f * 0.16f * 0.16f)) * (Mathf.Abs(u) > 0.47f ? 0f : 1f));
                for (int y = 0; y < H; y++) px[y * W + x] = new Color32(255, 255, 255, a);
            }
            _tex.SetPixels32(px); _tex.Apply(true, true);
            return _tex;
        }

        // (1.7.3) how bright a web is: the game's day/night light (dim at night), times how much open sky is above it (a cave's inside or under
        // a roof: dark), and the player's flashlight on top (a lit web is as bright as by day). The shared material stays white; each web gets
        // its brightness through a property block (set once a second, and while the flashlight is on only when a web's brightness changes).
        internal static void Tint()
        {
            if (_mat != null) _mat.color = Color.white;
            _k = Mathf.Lerp(0.05f, 0.88f, Mathf.Clamp01(Senses.LightLevel()));
            for (int i = _all.Count - 1; i >= 0; i--) { var w = _all[i]; if (w.Go == null) { _all.RemoveAt(i); continue; } }
            Flash(true);
        }

        private static float Level(Web w) { return _k * (0.12f + 0.88f * w.Sky); }

        private static void Flash(bool all)
        {
            var fl = Senses.Flashlight;
            if (fl == null && !all)
            {
                // the flashlight was just switched off: lit webs go back to their own light
                for (int i = 0; i < _all.Count; i++) { var w = _all[i]; if (w.Go != null && w.Shown > Level(w) + 0.01f) Apply(w, Level(w), false); }
                return;
            }
            float range = 30f, cosOuter = Mathf.Cos(35f * Mathf.Deg2Rad);
            Vector3 fp = Vector3.zero, fwd = Vector3.forward;
            if (fl != null)
            {
                if (_flOf != fl) { _flOf = fl; _flLight = fl.GetComponentInChildren<Light>(true); }
                if (_flLight != null) { range = Mathf.Max(5f, _flLight.range); cosOuter = Mathf.Cos(Mathf.Clamp(_flLight.spotAngle, 10f, 170f) * 0.5f * Mathf.Deg2Rad); }
                fp = fl.position; fwd = fl.forward;
            }
            for (int i = 0; i < _all.Count; i++)
            {
                var w = _all[i]; if (w.Go == null) continue;
                float v = Level(w);
                if (fl != null)
                {
                    Vector3 to = w.Center - fp; float d = to.magnitude;
                    if (d < range + w.R)
                    {
                        // the nearest part of the web to the beam's axis decides (a 2 m web is lit when the beam touches its edge)
                        float along = Mathf.Max(0f, Vector3.Dot(to, fwd));
                        Vector3 axis = fp + fwd * along;
                        float off = Mathf.Max(0f, (w.Center - axis).magnitude - w.R * 0.7f);
                        float cos = along <= 0f ? -1f : along / Mathf.Sqrt(along * along + off * off);
                        float spot = Mathf.Clamp01((cos - cosOuter) / Mathf.Max(0.01f, 1f - cosOuter) * 3f);
                        float fall = 1f - Mathf.Clamp01((d - w.R) / range); fall *= fall;
                        v = Mathf.Max(v, 0.9f * spot * fall);
                    }
                }
                if (all || Mathf.Abs(v - w.Shown) > 0.02f) Apply(w, v, false);
            }
        }

        private static void Apply(Web w, float v, bool force)
        {
            if (w.Rend == null) return;
            if (!force && Mathf.Abs(v - w.Shown) < 0.004f) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _mpb.SetColor("_Color", new Color(v, v, v * 1.03f, 1f));
            w.Rend.SetPropertyBlock(_mpb);
            w.Shown = v;
        }

        // the share of the sky open above p: 7 rays (straight up and 6 at 50 degrees), 80 m
        internal static float SkyOpen(Vector3 p)
        {
            int open = 0;
            for (int i = 0; i < 7; i++)
            {
                Vector3 d = i == 0 ? Vector3.up : Quaternion.Euler(0f, i * 60f, 0f) * new Vector3(0f, Mathf.Cos(50f * Mathf.Deg2Rad), Mathf.Sin(50f * Mathf.Deg2Rad));
                if (!Physics.Raycast(p, d, 80f, Anchors, QueryTriggerInteraction.Ignore)) open++;
            }
            return open / 7f;
        }
    }
}
