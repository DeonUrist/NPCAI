using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace NPCAI
{
    // (1.7.0) Spider nests. A camp / cave / wreck where spiders are (the game spawns them when you come within 500 m, or they come back with
    // a save) becomes a nest: hanging webs where the place has openings to span (between beams, walls, wrecks, rocks: spots with surfaces
    // around them in a vertical plane), sheet webs on the ground around, and the big spiders sitting in the hanging webs, one per web, head
    // down. Touching any web of the nest alarms every spider of it: they know where you are and run at you 1.5 times faster for a while.
    // A spider that notices something itself drops from its web and fights as usual.
    // Built in slices of at most [Nests] FrameBudgetMs (1 ms) a frame while you are still far away; the same layout every visit (seeded by
    // the place). Webs are never saved (not registered with the game) and are rebuilt when you come back / after a load.
    internal static class Nests
    {
        private sealed class Cand { public Vector3 Hub, N; public float Score; }
        private sealed class Sit { public Senses.Agent A; public Webs.Web W; public Rigidbody Rb; public bool WasKinematic; }

        private sealed class Nest
        {
            public Transform Poi; public string Name; public Vector3 C; public float RefY, Radius;
            public readonly List<Senses.Agent> Spiders = new List<Senses.Agent>();
            public readonly List<Webs.Web> Hanging = new List<Webs.Web>(), Ground = new List<Webs.Web>();
            public readonly List<Sit> Sitters = new List<Sit>();
            public int Phase, Step, Want, GroundWant, Tries; public readonly List<Cand> Cands = new List<Cand>(); public List<Cand> Chosen;
            public int Seed; public UnityEngine.Random.State Rnd; public bool RndOn;
            public float BuildMs, MaxFrameMs, NextAlarm; public int Frames; public bool Logged, Waiting, Hold; public float NextTry;
            public readonly List<Vector3> Floor = new List<Vector3>();      // (1.7.2) a mapped place: floor points of its map (a cave: only under its roof)
            public string Key; public bool FromCache, Relaxed, Cave, SpiderSpots; public List<Vector3> CachedGround; public float WaitSince; public int Forced;   // (1.7.4)
        }

        private static readonly List<Nest> _nests = new List<Nest>();
        private static readonly List<Senses.Agent> _pending = new List<Senses.Agent>();
        private static readonly List<Transform> _pois = new List<Transform>();
        private static string[] _prefabs, _sitters; private static string _prefabsSrc, _sittersSrc;
        private static GameObject _mapMagic; private static float _nextPending, _nextStatus, _nextMap, _nextPois, _nextMaint, _nextTouch, _nextSit;
        private static readonly Stopwatch _sw = new Stopwatch();
        private const int GroundMask = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 14) | (1 << 16);
        private const int Solid = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 14) | (1 << 16);

        // (1.7.4) the layout of every nest built so far (where its webs hang / lie, in the place's own coordinates), kept in
        // BepInEx/config/NPCAI/nest-layouts.txt: a nest is rebuilt exactly the same after a load, a restart or a return - and at once,
        // without looking for spots again. Keyed by the map tile, the place's name and its position in the tile.
        private static Dictionary<string, string> _layouts;
        private static string LayoutFile { get { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "NPCAI/nest-layouts.txt"); } }
        private static void LoadLayouts()
        {
            if (_layouts != null) return;
            _layouts = new Dictionary<string, string>();
            try
            {
                if (!System.IO.File.Exists(LayoutFile)) return;
                foreach (var line in System.IO.File.ReadAllLines(LayoutFile)) { int t = line.IndexOf('\t'); if (t > 0) _layouts[line.Substring(0, t)] = line.Substring(t + 1); }
            }
            catch (Exception e) { Plugin.Warn("Nests: layouts not read: " + e.Message); }
        }
        private static void SaveLayouts()
        {
            try
            {
                var lines = new List<string>(); foreach (var kv in _layouts) lines.Add(kv.Key + "\t" + kv.Value);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LayoutFile));
                System.IO.File.WriteAllLines(LayoutFile, lines.ToArray());
            }
            catch (Exception e) { Plugin.Warn("Nests: layouts not written: " + e.Message); }
        }
        private static string F(Vector3 v) { return v.x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "," + v.y.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "," + v.z.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); }
        private static bool P(string s, out Vector3 v)
        {
            v = Vector3.zero; var p = s.Split(','); if (p.Length != 3) return false;
            var ci = System.Globalization.CultureInfo.InvariantCulture; float x, y, z;
            if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, ci, out x) || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, ci, out y) || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, ci, out z)) return false;
            v = new Vector3(x, y, z); return true;
        }
        private static string KeyOf(Transform poi)
        {
            string tile = poi.parent != null && poi.parent.parent != null ? poi.parent.parent.name : "-";
            Vector3 l = poi.localPosition;
            return tile + "|" + poi.name + "|" + Mathf.RoundToInt(l.x) + "," + Mathf.RoundToInt(l.y) + "," + Mathf.RoundToInt(l.z);
        }
        // "H:hub;normal|hub;normal...  G:centre|centre..." in the place's local space
        private static void Remember(Nest n)
        {
            if (n.Poi == null || n.Key == null || n.Hanging.Count + n.Ground.Count == 0) return;
            var sb = new System.Text.StringBuilder("H:");
            for (int i = 0; i < n.Hanging.Count; i++) { var w = n.Hanging[i]; if (i > 0) sb.Append('|'); sb.Append(F(n.Poi.InverseTransformPoint(w.Center))).Append(';').Append(F(n.Poi.InverseTransformDirection(w.N))); }
            sb.Append(" G:");
            for (int i = 0; i < n.Ground.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(F(n.Poi.InverseTransformPoint(n.Ground[i].Center))); }
            _layouts[n.Key] = sb.ToString();
            SaveLayouts();
        }
        private static bool Recall(Nest n)
        {
            string v; if (n.Key == null || !_layouts.TryGetValue(n.Key, out v)) return false;
            n.Chosen = new List<Cand>(); n.CachedGround = new List<Vector3>();
            try
            {
                int g = v.IndexOf(" G:");
                string hs = v.Substring(2, (g < 0 ? v.Length : g) - 2), gs = g < 0 ? "" : v.Substring(g + 3);
                foreach (var part in hs.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var q = part.Split(';'); Vector3 h, nn;
                    if (q.Length == 2 && P(q[0], out h) && P(q[1], out nn)) n.Chosen.Add(new Cand { Hub = n.Poi.TransformPoint(h), N = n.Poi.TransformDirection(nn) });
                }
                foreach (var part in gs.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)) { Vector3 c; if (P(part, out c)) n.CachedGround.Add(n.Poi.TransformPoint(c)); }
            }
            catch (Exception) { n.Chosen.Clear(); n.CachedGround.Clear(); }
            return n.Chosen.Count + n.CachedGround.Count > 0;
        }

        internal static void OnSceneLoaded() { foreach (var n in _nests) Free(n, false); _nests.Clear(); _pending.Clear(); _pois.Clear(); _mapMagic = null; _nextMap = 0f; _nextPois = 0f; }

        private static bool In(string prefab, string cfg, ref string[] cache, ref string src)
        {
            cfg = cfg ?? "";
            if (cache == null || src != cfg) { src = cfg; cache = cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries); for (int i = 0; i < cache.Length; i++) cache[i] = cache[i].Trim(); }
            foreach (var p in cache) if (p.Length > 0 && string.Equals(prefab, p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Senses.Make: every registered NPC passes here once
        internal static void Register(Senses.Agent a)
        {
            if (a == null || a.Owner == null || !In(Senses.PrefabOf(a.Owner), Plugin.NestPrefabs.Value, ref _prefabs, ref _prefabsSrc)) return;
            // a spider saved while it sat in a web: never left held in the air after a load (spiders are never kinematic in the game)
            var rb = a.Owner.GetComponent<Rigidbody>(); if (rb != null && rb.isKinematic) rb.isKinematic = false;
            _pending.Add(a);
        }

        private static bool CanSit(Senses.Agent a) { return a != null && a.Owner != null && In(Senses.PrefabOf(a.Owner), Plugin.NestSitters.Value, ref _sitters, ref _sittersSrc); }

        internal static void Tick()
        {
            float now = Time.time;
            bool on = Plugin.NestsEnabled.Value && Senses.On;
            var player = Senses.PlayerObj;
            if (!on || player == null)
            {
                if (_nests.Count > 0 && !on) { foreach (var n in _nests) Free(n, true); _nests.Clear(); }
                return;
            }
            Vector3 pp = player.transform.position;
            var cam = Camera.main;
            Vector3 vp = cam != null ? cam.transform.position : pp;          // (1.7.3) where you look from: also while flying in god mode
            if (now >= _nextMaint) { _nextMaint = now + 1f; Maintain(vp, now); }
            if (_nests.Count == 0) return;
            // one nest is built at a time, in slices
            for (int i = 0; i < _nests.Count; i++)
            {
                var n = _nests[i];
                if (n.Phase >= 5 || now < n.NextTry) continue;
                if (Flat(n.C - vp) > Plugin.NestBuildRange.Value) continue;
                Build(n, now);
                break;
            }
            if (now >= _nextTouch) { _nextTouch = now + 0.1f; Touch(player, pp, now); }
            if (now >= _nextSit) { _nextSit = now + 0.2f; Sitters(player, now); }
        }

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        // ---------- which nests exist ----------
        private static void Maintain(Vector3 pp, float now)
        {
            for (int i = _nests.Count - 1; i >= 0; i--)
            {
                var n = _nests[i];
                n.Spiders.RemoveAll(a => a == null || a.Owner == null);
                if (n.Poi == null || !n.Poi.gameObject.activeInHierarchy || Flat(n.C - pp) > Plugin.NestFreeRange.Value)
                {
                    Free(n, true); _nests.RemoveAt(i);
                    foreach (var a in n.Spiders) if (a != null && a.Owner != null && !_pending.Contains(a)) _pending.Add(a);     // (1.7.1) the nest comes back when you do
                    if (Plugin.NestLog.Value) Plugin.Log.LogInfo("Nests: " + n.Name + " nest freed (place gone or far)");
                }
            }
            if (_pending.Count == 0 || now < _nextPending) return;
            _nextPending = now + 2f;
            if (now >= _nextPois) { _nextPois = now + 5f; Pois(); }
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var a = _pending[i];
                if (a == null || a.Owner == null) { _pending.RemoveAt(i); continue; }
                if (a.T.parent != null) continue;
                var poi = PoiAt(a.T.position);
                if (Flat(a.T.position - pp) > Plugin.NestFreeRange.Value * 0.9f) continue;    // (1.7.3) far away: planned when you come closer (was planned and freed every 2 s)
                if (poi == null) continue;          // (1.7.1) no place found (yet): asked again later - was dropped for good when > 600 m away
                _pending.RemoveAt(i);
                Nest nest = null;
                foreach (var x in _nests) if (x.Poi == poi) { nest = x; break; }
                if (nest == null) { nest = Make(poi); Plugin.Log.LogInfo("Nests: spiders at " + poi.name + " (" + Flat(poi.position - pp).ToString("0") + " m away) - a nest will be built there"); }
                if (!nest.Spiders.Contains(a)) nest.Spiders.Add(a);
            }
            if (_pending.Count > 0 && Plugin.NestLog.Value && now >= _nextStatus)
            {
                _nextStatus = now + 30f;
                var a0 = _pending[0];
                Plugin.Log.LogInfo("Nests: " + _pending.Count + " spider(s) not at any place (first " + (a0.Owner != null ? a0.Owner.name : "?") + "), " + _pois.Count + " places known" + (_mapMagic == null ? ", MapMagic not found" : ""));
            }
        }

        private static Nest Make(Transform poi)
        {
            LoadLayouts();
            var n = new Nest { Poi = poi, Name = poi.name, C = poi.position, RefY = poi.position.y, Key = KeyOf(poi), Cave = poi.name.StartsWith("Cave_") };      // (1.7.1) was (0,0,0) until the build began
            int px = Mathf.RoundToInt(poi.position.x), pz = Mathf.RoundToInt(poi.position.z);
            n.Seed = px * 73856093 ^ pz * 19349663;
            _nests.Add(n);
            return n;
        }

        // the places: children of MapMagic/Tile */Objects named Camp_ / Cave_ / Wreck_ / Building_
        private static void Pois()
        {
            if (_mapMagic == null && Time.time >= _nextMap) { _nextMap = Time.time + 10f; _mapMagic = GameObject.Find("MapMagic"); }
            _pois.Clear();
            if (_mapMagic == null) return;
            var mm = _mapMagic.transform;
            for (int i = 0; i < mm.childCount; i++)
            {
                var objs = mm.GetChild(i).Find("Objects");
                if (objs == null) continue;
                for (int k = 0; k < objs.childCount; k++)
                {
                    var t = objs.GetChild(k);
                    string nm = t.name;
                    if (t.gameObject.activeInHierarchy && IsPlace(nm)) _pois.Add(t);
                }
            }
        }

        // Cave_3(Clone), Wreck_6, Camp_12 ...: the prefix and then a digit (not a prop called Camp_fire)
        private static bool IsPlace(string nm)
        {
            int k = nm.StartsWith("Cave_") ? 5 : nm.StartsWith("Wreck_") ? 6 : nm.StartsWith("Camp_") ? 5 : nm.StartsWith("Building_") ? 9 : -1;
            return k > 0 && nm.Length > k && char.IsDigit(nm[k]);
        }

        private static Transform PoiAt(Vector3 p)
        {
            Transform best = null; float bd = Plugin.NestPoiRadius.Value;
            foreach (var t in _pois) { if (t == null || !t.gameObject.activeInHierarchy) continue; float d = Flat(t.position - p); if (d < bd) { bd = d; best = t; } }
            return best ?? PoiByColliders(p);
        }

        // (1.7.1) fallback: the place whose own geometry is around the spider (a collider within 15 m whose ancestors include a
        // Camp_ / Cave_ / Wreck_ / Building_ object) - works whatever the scene hierarchy looks like
        private static readonly Collider[] _near = new Collider[48];
        private static Transform PoiByColliders(Vector3 p)
        {
            int n = Physics.OverlapSphereNonAlloc(p, 15f, _near, Solid, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var t = _near[i] != null ? _near[i].transform : null;
                for (int k = 0; k < 12 && t != null; k++, t = t.parent)
                {
                    string nm = t.name;
                    if (IsPlace(nm)) return t;
                }
            }
            return null;
        }

        private static void Free(Nest n, bool drop)
        {
            if (drop) foreach (var s in n.Sitters) Drop(s, null);
            n.Sitters.Clear();
            foreach (var w in n.Hanging) Webs.Kill(w);
            foreach (var w in n.Ground) Webs.Kill(w);
            n.Hanging.Clear(); n.Ground.Clear();
        }

        // ---------- building, sliced ----------
        // 0 centre, 1 spot search (rays), 2 choose, 3 hanging webs (one mesh per step), 4 ground webs, 5 ready
        private static void Build(Nest n, float now)
        {
            float budget = Mathf.Max(0.2f, Plugin.NestFrameBudgetMs.Value);
            var saved = UnityEngine.Random.state;
            if (!n.RndOn) { UnityEngine.Random.InitState(n.Seed); n.RndOn = true; } else UnityEngine.Random.state = n.Rnd;
            _sw.Reset(); _sw.Start();
            try
            {
                n.Hold = false;
                while (n.Phase < 5 && !n.Hold && _sw.Elapsed.TotalMilliseconds < budget) Step(n);
            }
            catch (Exception e) { Plugin.Log.LogError("Nests: " + n.Name + ": " + e); n.Phase = 5; }
            finally { n.Rnd = UnityEngine.Random.state; UnityEngine.Random.state = saved; }
            _sw.Stop();
            float ms = (float)_sw.Elapsed.TotalMilliseconds;
            n.BuildMs += ms; n.Frames++; if (ms > n.MaxFrameMs) n.MaxFrameMs = ms;
            if (n.Phase >= 5 && !n.Logged)
            {
                n.Logged = true;
                if (n.FromCache && n.Hanging.Count + n.Ground.Count == 0 && n.Poi != null)
                {
                    // the remembered spots no longer hold (the place changed): forget them and build anew
                    _layouts.Remove(n.Key); SaveLayouts();
                    n.FromCache = false; n.Logged = false; n.Phase = 0; n.Relaxed = true;
                    return;
                }
                if (!n.FromCache) Remember(n);
                // the touch check and the seating reach every web of the nest (a cave's webs may lie farther than its spiders' spread)
                foreach (var w in n.Hanging) n.Radius = Mathf.Max(n.Radius, Flat(w.Center - n.C) + w.R + 2f);
                foreach (var w in n.Ground) n.Radius = Mathf.Max(n.Radius, Flat(w.Center - n.C) + w.R + 2f);
                Plugin.Log.LogInfo("Nests: " + n.Name + " is a spider nest: " + n.Hanging.Count + " hanging + " + n.Ground.Count + " ground webs, " + n.Spiders.Count + " spider(s), built in " + n.BuildMs.ToString("0.0") + " ms over " + n.Frames + " frames (at most " + n.MaxFrameMs.ToString("0.00") + " ms in one)");
                SeatAll(n);
                Plugin.Log.LogInfo("Nests: " + n.Name + ": " + n.Sitters.Count + " big spider(s) sit in its webs" + (n.SpiderSpots ? " (webs placed around the spiders' own spots)" : ""));
            }
        }

        private static void Step(Nest n)
        {
            switch (n.Phase)
            {
                case 0:
                {
                    // the centre: the place itself, unless its spiders are elsewhere in it (a deep cave) - then where they are, on a 2 m grid
                    Vector3 sum = Vector3.zero; int c = 0;
                    foreach (var a in n.Spiders) if (a != null && a.Owner != null) { sum += a.T.position; c++; }
                    Vector3 mean = c > 0 ? sum / c : n.Poi.position;
                    n.C = Flat(mean - n.Poi.position) <= 15f ? n.Poi.position : new Vector3(Mathf.Round(mean.x / 2f) * 2f, mean.y, Mathf.Round(mean.z / 2f) * 2f);
                    n.RefY = c > 0 ? mean.y : n.Poi.position.y;
                    float spread = 0f; foreach (var a in n.Spiders) if (a != null && a.Owner != null) spread = Mathf.Max(spread, Flat(a.T.position - n.C));
                    n.Radius = Mathf.Clamp(Mathf.Ceil((spread + 8f) / 5f) * 5f, 12f, Plugin.NestMaxRadius.Value);
                    // (1.7.4) built here before: the same webs again, at once
                    if (!n.Relaxed && Recall(n)) { n.FromCache = true; n.Phase = 3; n.Step = 0; break; }
                    // (1.7.2) a camp / cave / building NPCAI maps: its map gives the webs' spots - for a cave only the floor under its roof
                    bool known, baked;
                    bool cave = n.Cave;
                    Nav.FloorCells(n.Poi, cave, n.Floor, 1500, out known, out baked);
                    if (known && !baked)
                    {
                        // (1.7.4) the map is waited for 6 s at the most (it may be busy elsewhere); then the spiders' own surroundings decide
                        if (n.WaitSince <= 0f) n.WaitSince = Time.time;
                        if (Time.time - n.WaitSince < 6f)
                        {
                            if (!n.Waiting) { n.Waiting = true; if (Plugin.NestLog.Value) Plugin.Log.LogInfo("Nests: " + n.Name + " waits for its map"); }
                            n.Hold = true; n.NextTry = Time.time + 1f;      // asked again in a second; other nests build meanwhile
                            return;
                        }
                        n.Floor.Clear();
                    }
                    if (n.Floor.Count == 0)
                    {
                        // (1.7.5) no map (a wreck, or the map was not ready): the floor the spiders stand on is the floor - they are already
                        // where the place has room, often under its roof
                        foreach (var a in n.Spiders)
                        {
                            RaycastHit sg;
                            if (a != null && a.Owner != null && Physics.Raycast(a.T.position + Vector3.up * 0.5f, Vector3.down, out sg, 3f, GroundMask, QueryTriggerInteraction.Ignore)) n.Floor.Add(sg.point);
                        }
                        n.SpiderSpots = n.Floor.Count > 0;
                    }
                    else
                    {
                        // the map decides where webs go: keep the floor points within reach of the middle (a cave: its whole inside, up to 2 x MaxRadius)
                        float lim = cave ? Plugin.NestMaxRadius.Value * 2f : n.Radius;
                        n.Floor.RemoveAll(f => Flat(f - n.C) > lim);
                    }
                    int big = 0; foreach (var a in n.Spiders) if (CanSit(a)) big++;
                    n.Want = Mathf.Clamp(big + UnityEngine.Random.Range(1, 4), Plugin.NestMinWebs.Value, Mathf.Max(Plugin.NestMinWebs.Value, Plugin.NestMaxWebs.Value));
                    n.GroundWant = UnityEngine.Random.Range(Plugin.NestGroundMin.Value, Mathf.Max(Plugin.NestGroundMin.Value, Plugin.NestGroundMax.Value) + 1);
                    n.Phase = 1; n.Step = 0;
                    break;
                }
                case 1:
                {
                    // one candidate spot: a point 0.9-2.6 m above a floor, free itself, with surfaces around it in a vertical plane (8 rays, 2 planes)
                    if (n.Step++ >= Plugin.NestSpots.Value) { n.Phase = 2; break; }
                    float h = UnityEngine.Random.Range(0.9f, 2.6f), yaw = UnityEngine.Random.Range(0f, 180f);
                    Vector3 floor;
                    if (n.SpiderSpots)
                    {
                        // a spider's spot, or up to 3 m from it with nothing solid in between
                        Vector3 sp = n.Floor[UnityEngine.Random.Range(0, n.Floor.Count)];
                        Vector2 j = UnityEngine.Random.insideUnitCircle * 3f;
                        Vector3 a0 = sp + Vector3.up * 1f, a1 = a0 + new Vector3(j.x, 0f, j.y);
                        RaycastHit jg;
                        if (!Physics.Linecast(a0, a1, Solid, QueryTriggerInteraction.Ignore) && Physics.Raycast(a1, Vector3.down, out jg, 3f, GroundMask, QueryTriggerInteraction.Ignore)) floor = jg.point;
                        else floor = sp;
                    }
                    else if (n.Floor.Count > 0) floor = n.Floor[UnityEngine.Random.Range(0, n.Floor.Count)];
                    else if (n.Cave) { if (!SpiderFloor(n, 10f, out floor)) break; }
                    else
                    {
                        Vector2 d = UnityEngine.Random.insideUnitCircle * n.Radius;
                        RaycastHit g;
                        Vector3 top = new Vector3(n.C.x + d.x, n.RefY + 4f, n.C.z + d.y);
                        if (!Physics.Raycast(top, Vector3.down, out g, 12f, GroundMask, QueryTriggerInteraction.Ignore)) break;
                        floor = g.point;
                    }
                    Vector3 hub = floor + Vector3.up * h;
                    if (n.Floor.Count > 0 && Physics.Raycast(floor + Vector3.up * 0.1f, Vector3.up, h, Solid, QueryTriggerInteraction.Ignore)) { h *= 0.6f; if (h < 0.7f || Physics.Raycast(floor + Vector3.up * 0.1f, Vector3.up, h + 0.3f, Solid, QueryTriggerInteraction.Ignore)) break; }   // a low roof: lower, or no room
                    if (Physics.CheckSphere(hub, 0.3f, Solid, QueryTriggerInteraction.Ignore)) break;
                    for (int k = 0; k < 2; k++)
                    {
                        Vector3 nrm = Quaternion.Euler(0f, yaw + k * 90f, 0f) * Vector3.forward;
                        float gap, mean;
                        int hits = Webs.Probe(hub, nrm, out gap, out mean);
                        if (n.Relaxed ? (hits < 3 || gap > 170f || mean < 0.4f || mean > 2.5f) : (hits < 4 || gap > 135f || mean < 0.6f || mean > 2.4f)) continue;
                        // (1.7.3) enclosed spots first: under a roof / inside a shelter, the share of open sky above counts against it
                        float sky = Webs.SkyOpen(hub);
                        n.Cands.Add(new Cand { Hub = hub, N = nrm, Score = hits + mean * 0.8f - gap / 180f + (1f - sky) * 4f });
                        break;
                    }
                    break;
                }
                case 2:
                {
                    if (n.Cands.Count == 0 && !n.Relaxed) { n.Relaxed = true; n.Phase = 1; n.Step = 0; if (Plugin.NestLog.Value) Plugin.Log.LogInfo("Nests: " + n.Name + ": no spot for a hanging web, looking again less strictly"); break; }   // (1.7.4)
                    n.Cands.Sort((a, b) => b.Score.CompareTo(a.Score));
                    n.Chosen = new List<Cand>();
                    foreach (var c in n.Cands)
                    {
                        if (n.Chosen.Count >= n.Want) break;
                        bool close = false; foreach (var o in n.Chosen) if ((o.Hub - c.Hub).sqrMagnitude < 3.5f * 3.5f) { close = true; break; }
                        if (!close) n.Chosen.Add(c);
                    }
                    n.Cands.Clear(); n.Phase = 3; n.Step = 0;
                    break;
                }
                case 3:
                {
                    if (n.Step >= n.Chosen.Count) { n.Phase = 4; n.Step = 0; n.Tries = 0; break; }
                    var c = n.Chosen[n.Step++];
                    var w = Webs.Orb(c.Hub, c.N);
                    if (w != null) { w.Go.name = "NPCAI_Nest_" + n.Name; n.Hanging.Add(w); }
                    break;
                }
                case 4:
                {
                    if (n.FromCache)
                    {
                        if (n.Step >= n.CachedGround.Count) { n.Phase = 5; break; }
                        var cw = Webs.Sheet(n.CachedGround[n.Step++]);
                        if (cw != null) { cw.Go.name = "NPCAI_Nest_" + n.Name; n.Ground.Add(cw); }
                        break;
                    }
                    if (n.Ground.Count >= n.GroundWant || n.Tries++ >= n.GroundWant * 5)
                    {
                        // (1.7.4) never a nest without webs: whatever is missing goes on the floor under its spiders (one per step)
                        if (n.Ground.Count + n.Hanging.Count < 3 && n.Forced < n.Spiders.Count && n.Forced < 4)
                        {
                            var sp = n.Spiders[n.Forced++];
                            RaycastHit fg;
                            if (sp != null && sp.Owner != null && Physics.Raycast(sp.T.position + Vector3.up * 0.5f, Vector3.down, out fg, 3f, GroundMask, QueryTriggerInteraction.Ignore))
                            {
                                bool near = false; foreach (var o in n.Ground) if ((o.Center - fg.point).sqrMagnitude < 2f * 2f) { near = true; break; }
                                if (!near) { var fw = Webs.Sheet(fg.point); if (fw != null) { fw.Go.name = "NPCAI_Nest_" + n.Name; n.Ground.Add(fw); } }
                            }
                            break;
                        }
                        n.Phase = 5; break;
                    }
                    RaycastHit g;
                    Vector3 at;
                    if (n.Cave && n.Floor.Count == 0)
                    {
                        Vector3 sf; if (!SpiderFloor(n, 10f, out sf)) break;
                        at = sf;
                    }
                    else if (n.Floor.Count > 0)
                    {
                        Vector3 f = n.Floor[UnityEngine.Random.Range(0, n.Floor.Count)];
                        if (!Physics.Raycast(f + Vector3.up * 0.5f, Vector3.down, out g, 1.5f, GroundMask, QueryTriggerInteraction.Ignore) || g.normal.y < 0.75f) break;
                        at = g.point;
                    }
                    else
                    {
                        Vector2 d = UnityEngine.Random.insideUnitCircle * n.Radius * 0.9f;
                        if (!Physics.Raycast(new Vector3(n.C.x + d.x, n.RefY + 4f, n.C.z + d.y), Vector3.down, out g, 12f, GroundMask, QueryTriggerInteraction.Ignore) || g.normal.y < 0.75f) break;
                        at = g.point;
                    }
                    foreach (var o in n.Ground) if ((o.Center - at).sqrMagnitude < 3f * 3f) return;
                    // (1.7.3) enclosed ground first: in the open only after 3 tries per web found nothing sheltered
                    if (n.Tries <= n.GroundWant * 3 && Webs.SkyOpen(at + Vector3.up * 0.3f) > 0.6f) break;
                    var w = Webs.Sheet(at);
                    if (w != null) { w.Go.name = "NPCAI_Nest_" + n.Name; n.Ground.Add(w); }
                    break;
                }
            }
        }

        // (1.7.4) a floor point in the same open space as one of the nest's spiders (within r m, nothing solid between): inside a cave
        // even without its map - never in the rock between its walls, never outside
        private static bool SpiderFloor(Nest n, float r, out Vector3 floor)
        {
            floor = Vector3.zero;
            if (n.Spiders.Count == 0) return false;
            var a = n.Spiders[UnityEngine.Random.Range(0, n.Spiders.Count)];
            if (a == null || a.Owner == null) return false;
            Vector3 from = a.T.position + Vector3.up * 1.2f;
            Vector2 d = UnityEngine.Random.insideUnitCircle * r;
            Vector3 to = from + new Vector3(d.x, 0f, d.y);
            if (Physics.Linecast(from, to, Solid, QueryTriggerInteraction.Ignore)) return false;
            RaycastHit g;
            if (!Physics.Raycast(to, Vector3.down, out g, 5f, GroundMask, QueryTriggerInteraction.Ignore)) return false;
            if (!Physics.Raycast(g.point + Vector3.up * 0.2f, Vector3.up, 40f, Solid, QueryTriggerInteraction.Ignore)) return false;   // a cave: rock above
            floor = g.point; return true;
        }

        // ---------- big spiders in the hanging webs ----------
        private static void SeatAll(Nest n)
        {
            foreach (var w in n.Hanging)
            {
                bool taken = false; foreach (var s in n.Sitters) if (s.W == w) { taken = true; break; }
                if (taken) continue;
                Senses.Agent best = null; float bd = float.MaxValue;     // (1.7.5) any calm big spider of the nest, the nearest first
                foreach (var a in n.Spiders)
                {
                    if (a == null || a.Owner == null || !CanSit(a) || a.State != Senses.State.Idle || a.T.parent != null || a.Travel) continue;
                    bool sits = false; foreach (var s in n.Sitters) if (s.A == a) { sits = true; break; }
                    if (sits) continue;
                    float d = (a.T.position - w.Center).magnitude;
                    if (d < bd) { bd = d; best = a; }
                }
                if (best != null) Seat(n, best, w);
            }
        }

        private static void Seat(Nest n, Senses.Agent a, Webs.Web w)
        {
            var rb = a.Owner.GetComponent<Rigidbody>();
            if (rb == null) return;
            // on the side of the web facing the middle of the nest, belly to the silk, head down
            Vector3 up = w.N; if (Vector3.Dot(n.C - w.Center, up) < 0f) up = -up;
            Vector3 fwd = Vector3.ProjectOnPlane(Vector3.down, up); if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
            var rot = Quaternion.LookRotation(fwd.normalized, up);
            var s = new Sit { A = a, W = w, Rb = rb, WasKinematic = rb.isKinematic };
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.isKinematic = true;
            Brain.SetTravel(a.Owner, true);
            Vector3 pos = w.Center + up * 0.02f;
            a.T.SetPositionAndRotation(pos, rot); rb.position = pos; rb.rotation = rot;
            n.Sitters.Add(s);
            a.AmbushDist = Mathf.Max(0.3f, Plugin.NestSitterReach.Value);      // (1.7.5) in its web it strikes only at what comes within 1 m
            if (Plugin.NestLog.Value) Plugin.Log.LogInfo("Nests: " + a.Owner.name + " sits in a web at " + n.Name);
        }

        private static void Sitters(GameObject player, float now)
        {
            foreach (var n in _nests)
            {
                for (int i = n.Sitters.Count - 1; i >= 0; i--)
                {
                    var s = n.Sitters[i];
                    if (s.A == null || s.A.Owner == null) { n.Sitters.RemoveAt(i); continue; }
                    if (s.A.State != Senses.State.Idle || s.A.Blown) { Drop(s, player); n.Sitters.RemoveAt(i); }
                }
                if (n.Phase >= 5 && n.Sitters.Count < n.Hanging.Count && now >= n.NextAlarm) SeatAll(n);     // new / loaded spiders, calm again
            }
        }

        // off the web: upright again, facing what it goes for, and falls to the ground under the web
        private static void Drop(Sit s, GameObject player)
        {
            if (s.A == null || s.A.Owner == null) return;
            Vector3 f = s.A.Target != null ? s.A.Target.transform.position - s.A.T.position : (player != null ? player.transform.position - s.A.T.position : s.A.T.forward);
            f.y = 0f; if (f.sqrMagnitude < 0.01f) f = Vector3.forward;
            var rot = Quaternion.LookRotation(f.normalized, Vector3.up);
            s.A.T.rotation = rot;
            if (s.Rb != null) { s.Rb.rotation = rot; s.Rb.isKinematic = false; s.Rb.velocity = Vector3.zero; }
            Brain.SetTravel(s.A.Owner, false);
            if (s.A.AmbushDist > 0f && s.A.AmbushDist <= Plugin.NestSitterReach.Value + 0.01f) s.A.AmbushDist = 0f;   // off the web: the usual ambush distance again when calm
            if (Plugin.NestLog.Value) Plugin.Log.LogInfo("Nests: " + s.A.Owner.name + " drops from its web");
        }

        // ---------- touching a web ----------
        private static void Touch(GameObject player, Vector3 pp, float now)
        {
            if (Senses.PlayerInCar) return;
            Vector3 feet = pp + Vector3.up * 0.2f, mid = pp + Vector3.up * 0.9f, head = pp + Vector3.up * 1.6f;
            foreach (var n in _nests)
            {
                if (n.Phase < 3 || now < n.NextAlarm || Flat(n.C - pp) > n.Radius + 6f) continue;
                Webs.Web hit = null;
                foreach (var w in n.Hanging) if (w.Go != null && (InOrb(w, feet) || InOrb(w, mid) || InOrb(w, head))) { hit = w; break; }
                if (hit == null) foreach (var w in n.Ground) if (w.Go != null && Flat(feet - w.Center) < w.R * 0.9f && Mathf.Abs(pp.y - w.Center.y) < 0.6f) { hit = w; break; }
                if (hit == null) continue;
                n.NextAlarm = now + Mathf.Max(1f, Plugin.NestAlarmCooldown.Value);
                int c = 0;
                foreach (var a in n.Spiders)
                {
                    if (a == null || a.Owner == null) continue;
                    if (Senses.Alert(a.Owner, player)) { Brain.Boost(a.Owner, Plugin.NestAlarmSpeed.Value, Plugin.NestAlarmSeconds.Value); c++; }
                }
                for (int i = n.Sitters.Count - 1; i >= 0; i--) { Drop(n.Sitters[i], player); n.Sitters.RemoveAt(i); }
                Plugin.Log.LogInfo("Nests: you touched a " + (hit.Orb ? "hanging" : "ground") + " web at " + n.Name + " - " + c + " spider(s) come at you");
            }
        }

        private static bool InOrb(Webs.Web w, Vector3 q)
        {
            Vector3 d = q - w.Center; float off = Vector3.Dot(d, w.N);
            if (Mathf.Abs(off) > 0.35f) return false;
            return (d - w.N * off).sqrMagnitude < w.R * w.R * 0.8f;
        }
    }
}
