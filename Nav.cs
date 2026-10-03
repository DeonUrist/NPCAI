using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NPCAI
{
    // Structure navigation: NPCs know the camps, buildings and caves they are in.
    //
    // The world's structures are instances of 30 prefabs (Camp_1..16, Building_1..7, Cave_1..7; asset read 2026-10-02), found by a slow
    // incremental sweep of the scene (MapMagic places them itself). Each is baked before the player could get near enough for his noise to
    // reach its NPCs (Pick: distance, the fastest he moved lately, the bake's estimated time), or when he comes within [Nav] BakeRange:
    // its footprint (the union of its solid colliders + Margin) is baked into a walkability grid
    // (CellSize, world-aligned): per cell one downward ray for the floor nearest the structure's base height (caves have a roof above the
    // floor), then a body-sized capsule from ankle (0.2 m) to head (1.7 m) height must be free of anything solid except cars, loose items
    // and creatures - so spikes at a cave mouth, a brazier, crates and walls are obstacles, the clean opening is not. Neighbouring cells
    // connect when their floors differ by at most MaxStep. Baking is spread over frames (BakeBudgetMs per frame) and kept per instance.
    //
    // Routing: an NPC standing inside a baked footprint whose goal (target or ghost) is not in straight sight on the grid follows a
    // distance field built for that goal (Dijkstra over the grid: from the goal cell when the goal is inside, otherwise from every edge
    // cell weighted by its distance to the goal, so the NPC leaves through the exit that is shortest overall; since 1.6.0 an outside goal
    // goes to ONE exit cell with a clear line out, routed as an inside goal, then straight - see OutNext). The next waypoint is the
    // farthest cell along the descent that is in grid sight, handed to the brain's feelers as a waypoint. Fields are cached per goal
    // (shared by every NPC heading there) for FieldSeconds. Outside any footprint nothing runs.
    internal static class Nav
    {
        internal sealed class Structure
        {
            public Transform Root; public string Name;
            public Bounds Box;                      // world bounds of the footprint incl. margin
            public float Cell, RefY; public int W, H;
            public float[] FloorY;                  // NaN = blocked
            public bool Baked; public int Next;     // bake progress (cell index)
            public int Walkable, NoFloor, Tight, Solid, ScanLimit; public float BakeMs; public int BakeFrames;
            public byte[] Why;                      // per cell: 0 walkable, 1 no floor, 2 too tight, 3 inside rock
            public bool[] Open;                     // walkable cell with open sky above (not under a cave roof / building)
            public bool[] Near;                     // (1.4.11) walkable cell next to a body-height obstacle (too tight / rock): routes avoid it, walked centre to centre
            public int EdgeCells, EdgeWalkable;     // the outer ring of the footprint
            public Dictionary<int, int> FloorHits;  // collider id -> walkable cells it is the floor of (during the bake)
            public float NextDump;
            public byte[] Edges;                    // per cell, 1 = open edge toward +x (bit 0), +z (1), +x+z (2), -x+z (3): no wall or spike between the two cells;
                                                    // bits 4 (+x) and 5 (+z): a HOP edge - a low lip / kerb / step of MaxStep..HopStep a body hops over
            public int[] Comp; public int[] CompSize;   // connected areas (flood fill over open edges) and their sizes
            public int Phase;                       // bake: 0 floors, 1 edges, 2 connected areas (sliced too), then Baked
            public int CompScan, CompCur, CompCurSize; public Stack<int> CompStack; public List<int> CompSizes;   // phase 2 progress
            public readonly Dictionary<long, Field> Fields = new Dictionary<long, Field>();
        }

        internal sealed class Field { public float[] Dist; public float Made; public bool GoalInside; public int GoalCell; }
        // distance arrays are big (W*H floats); fields that expire give theirs back here, Build takes one from here when it fits
        private static readonly Dictionary<int, Stack<float[]>> _pool = new Dictionary<int, Stack<float[]>>();
        private static float[] Rent(int n)
        {
            Stack<float[]> st;
            if (_pool.TryGetValue(n, out st) && st.Count > 0) return st.Pop();
            return new float[n];
        }
        private static void Return(Field f)
        {
            if (f == null || f.Dist == null) return;
            Stack<float[]> st;
            if (!_pool.TryGetValue(f.Dist.Length, out st)) _pool[f.Dist.Length] = st = new Stack<float[]>();
            if (st.Count < 8) st.Push(f.Dist);
            f.Dist = null;
        }
        private static readonly List<long> _expired = new List<long>();
        internal static bool LogOn { get { return Plugin.BrainLog != null && Plugin.BrainLog.Value; } }

        private static readonly List<Structure> _structures = new List<Structure>();
        private static readonly HashSet<int> _known = new HashSet<int>();
        private static Structure _baking;
        private static float _nextPick;
        private static readonly Stopwatch _sw = new Stopwatch();
        // solid for baking: everything the feelers see, minus cars (8) and loose items (9) - those move
        private static readonly int BakeMask = ~((1 << 1) | (1 << 2) | (1 << 4) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 12) | (1 << 13) | (1 << 15) | (1 << 17) | (1 << 19) | (1 << 22));
        private const float Radius = 0.28f, Ankle = 0.2f, HeadTop = 1.5f;   // a human NPC: capsule r 0.28, 1.5 m tall
        // (1.4.11) clearance: from knee height (0.3 m) up the map tests a body 7 cm fatter than the real one - an NPC is never exactly on a
        // cell centre, and a spike passing 3 cm from the centre stopped the body 15 cm off it. Gaps under ~0.7 m at body height close.
        private const float BodyR = 0.35f, BodyLo = 0.3f;
        private const float NearCost = 1f;       // entering a cell next to an obstacle costs like 1 m more: routes keep to open ground
        private const int MinArea = 30;          // connected areas smaller than this (7.5 m2) are noise next to props: never start or end a route there
        internal const float HopStep = 0.5f;     // a floor step / lip up to this high is crossed with a hop (the brain's hop clears ~0.5 m)
        private const float HopCost = 2f;        // a hop edge costs like 2 m more walking: the map prefers a flat way when there is one
        private static bool _noHop;              // patrol / search points must be reachable without hops
        private static readonly HashSet<int> _floorCols = new HashSet<int>();   // colliders that are the floor of >= FloorColCells map cells (cave floors, camp decks)
        private const int FloorColCells = 40;

        internal static bool On { get { return Plugin.NavEnabled != null && Plugin.NavEnabled.Value; } }

        public static void OnSceneLoaded() { _structures.Clear(); _known.Clear(); _baking = null; _debug.Clear(); _floorCols.Clear(); _exits.Clear(); _relaxedFields.Clear(); _pool.Clear(); _player = null; _sweep.Clear(); _nodeKind.Clear(); _lastPickAt = 0f; _vPeak = 0f; _outPlans.Clear(); _outMemo.Clear(); }

        // ---------- discovery + baking (per frame) ----------
        public static void Tick()
        {
            if (!On) return;
            float now = Time.unscaledTime;
            var player = Player();
            if (player == null) return;
            float udt = Time.unscaledDeltaTime;
            if (udt > 0f && udt < 0.5f) _dtAvg = _dtAvg <= 0f ? udt : _dtAvg + (udt - _dtAvg) * 0.1f;
            // discovery: a slow walk over the scene hierarchy, a few hundred nodes per frame (each node's name read once and remembered by id)
            try { SweepStep(now); } catch (Exception e) { Plugin.Log.LogError("Nav: discovery: " + e); _sweep.Clear(); _nextSweep = now + 5f; }
            if (now >= _nextPick) { _nextPick = now + 0.5f; Pick(player.position, now); }
            if (_baking != null)
            {
                try { BakeStep(_baking); }
                catch (Exception e) { Plugin.Log.LogError("Nav: bake of " + _baking.Name + " failed: " + e); _baking.Baked = true; _baking.FloorY = null; _baking = null; }
            }
        }

        // ---------- which structure to bake, and how fast ----------
        // Every structure the player could reach before his noise could pull its NPCs out is mapped in time: "relevant" = within the
        // farthest the player's own noise or sight carries (R, about 150 m); "in time" = the player, at the fastest he moved lately (or a
        // run), could get within R of it before its bake would finish (estimated from its cell count and the measured cost per cell)
        // x1.5 + 5 s. The direction does not matter (he could turn). Soonest-needed first; a far more urgent one takes over (the bake in
        // progress pauses and resumes later). Within [Nav] BakeRange everything is baked as before.
        private static float _lastPickAt, _vPeak, _vNow, _dtAvg, _msPerCell = 0.026f; private static Vector3 _lastPos;
        private static bool _urgent;
        private const float MaxBakeDistance = 2500f;

        private static float Relevance()
        {
            float r = Mathf.Max(Plugin.SightRange.Value, Plugin.PlayerShoutRange.Value);
            r = Mathf.Max(r, Mathf.Max(Plugin.ShotRangePistol.Value, Plugin.ShotRangeSmg.Value));
            r = Mathf.Max(r, Mathf.Max(Plugin.ShotRangeRifle.Value, Plugin.ShotRangeSniper.Value));
            r = Mathf.Max(r, Mathf.Max(Plugin.ShotRangeShotgun.Value, Plugin.BlastRange.Value));
            return Mathf.Max(r, Plugin.EngineMaxRange.Value);
        }

        // remaining CPU time of a structure's bake, ms (phase 0 = floors ~65 %, phase 1 = edges ~30 %, phase 2 = areas)
        private static float RemainingMs(Structure s)
        {
            int n = Math.Max(1, s.W * s.H);
            float done = s.FloorY == null ? 0f : s.Phase == 0 ? 0.65f * s.Next / n : s.Phase == 1 ? 0.65f + 0.3f * s.Next / n : 0.95f;
            return n * _msPerCell * (1f - done);
        }

        private static void Pick(Vector3 pp, float now)
        {
            float dtp = now - _lastPickAt;
            if (_lastPickAt > 0f && dtp > 0.05f)
            {
                Vector3 dv = pp - _lastPos; dv.y = 0f;
                float v = dv.magnitude / dtp;
                if (v > 150f) v = 0f;                                       // a load / teleport, not a drive
                _vNow = v;
                _vPeak = Mathf.Max(v, _vPeak * Mathf.Pow(0.93f, dtp));      // the fastest lately, fading over ~15 s
            }
            _lastPos = pp; _lastPickAt = now;
            float vEff = Mathf.Max(_vPeak, 7f), R = Relevance(), frame = Mathf.Clamp(_dtAvg > 0f ? _dtAvg : 1f / 60f, 1f / 240f, 0.1f);
            float budget = BaseBudget();
            Structure best = null; float bestSlack = float.MaxValue, curSlack = float.MaxValue;
            for (int i = 0; i < _structures.Count; i++)
            {
                var s = _structures[i];
                if (s.Baked || s.Root == null) continue;
                Vector3 c = s.Box.ClosestPoint(new Vector3(pp.x, s.Box.center.y, pp.z));
                float d = Mathf.Sqrt((c.x - pp.x) * (c.x - pp.x) + (c.z - pp.z) * (c.z - pp.z));
                if (d > MaxBakeDistance) continue;
                float bakeSec = RemainingMs(s) / budget * frame;
                float slack = (d - R) / vEff - (bakeSec * 1.5f + 5f);       // seconds to spare before this map would come too late
                if (s == _baking) curSlack = slack;
                if (slack > 0f && d > Plugin.NavBakeRange.Value) continue;  // not needed yet
                if (slack < bestSlack) { bestSlack = slack; best = s; }
            }
            if (best != null && (_baking == null || (best != _baking && bestSlack < curSlack - 5f)))
            {
                if (_baking != null && Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: pauses the bake of " + _baking.Name + " for " + best.Name);
                _baking = best;
                if (best.FloorY == null) BeginBake(best);        // else: resumes where it stopped
            }
            // the map is already late (the player is or soon will be within R) and the player stands still: bake faster
            _urgent = _baking != null && (_baking == best ? bestSlack : curSlack) < 0f && _vNow < 2f;
        }

        private static float BaseBudget() { return Mathf.Max(0.2f, Plugin.NavBakeBudgetMs.Value); }

        // this frame's slice: fixed by [Nav] BakeBudgetMs and the headroom - never more because the player is fast, only when frames are
        // cheap (x2 above ~90 fps), the game is paused (x4, nothing moves), or a late map is needed where the player stands (x3 above ~50 fps);
        // halved below ~45 fps
        private static float FrameBudget()
        {
            float b = BaseBudget(), dt = _dtAvg > 0f ? _dtAvg : 1f / 60f;
            if (Time.timeScale <= 0f) return b * 4f;
            if (dt > 1f / 45f) return b * 0.5f;
            if (_urgent && dt < 1f / 50f) return b * 3f;
            if (dt < 1f / 90f) return b * 2f;
            return b;
        }

        // ---------- discovery: an incremental sweep ----------
        private static readonly Stack<KeyValuePair<Transform, int>> _sweep = new Stack<KeyValuePair<Transform, int>>();
        private static readonly Dictionary<int, byte> _nodeKind = new Dictionary<int, byte>();   // 1 descend, 2 leaf (body / too big), 3 structure
        private static float _nextSweep; private static int _sweepAdded;
        private const int SweepNodesPerFrame = 250;

        private static void SweepStep(float now)
        {
            if (_sweep.Count == 0)
            {
                if (now < _nextSweep) return;
                int gone = _structures.RemoveAll(s => s.Root == null);      // despawned camps: drop their grids and cached routes
                if (gone > 0 && Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: " + gone + " structure(s) gone");
                if (_baking != null && _baking.Root == null) _baking = null;
                if (_nodeKind.Count > 200000) _nodeKind.Clear();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var sc = SceneManager.GetSceneAt(i);
                    if (!sc.isLoaded) continue;
                    _roots.Clear(); sc.GetRootGameObjects(_roots);
                    foreach (var r in _roots) _sweep.Push(new KeyValuePair<Transform, int>(r.transform, 0));
                }
                if (_sweep.Count == 0) { _nextSweep = now + 2f; return; }
            }
            int budget = SweepNodesPerFrame;
            while (_sweep.Count > 0 && budget-- > 0)
            {
                var e = _sweep.Pop();
                var t = e.Key;
                if (t == null) continue;
                int id = t.GetInstanceID();
                byte k;
                if (!_nodeKind.TryGetValue(id, out k))
                {
                    k = IsStructure(t.name) ? (byte)3 : t.GetComponent<Rigidbody>() != null ? (byte)2 : (byte)1;   // creatures, cars, items never hold structures
                    _nodeKind[id] = k;
                }
                if (k == 3) { if (!_known.Contains(id) && t.gameObject.activeInHierarchy) _sweepAdded += Scan(t, 0); continue; }
                if (k == 2 || e.Value >= 4) continue;
                int cc = t.childCount;
                if (cc > 3000) continue;
                for (int i = 0; i < cc; i++) _sweep.Push(new KeyValuePair<Transform, int>(t.GetChild(i), e.Value + 1));
            }
            if (_sweep.Count == 0)
            {
                if (_sweepAdded > 0 && Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: " + _sweepAdded + " new structure(s), " + _structures.Count + " known");
                _sweepAdded = 0;
                _nextSweep = now + 2f;      // a full pass takes a few dozen frames; then a short pause
            }
        }

        private static Transform _player; private static float _nextPlayer;
        internal static Transform Player()
        {
            if (_player == null && Time.unscaledTime >= _nextPlayer) { _nextPlayer = Time.unscaledTime + 2f; var g = GameObject.Find("Player"); _player = g != null ? g.transform : null; }
            return _player;
        }

        // structures are found by name (Camp_N / Building_N / Cave_N, any "(Clone)" suffix) among the scene roots and their children
        private static readonly List<GameObject> _roots = new List<GameObject>();
        // a structure instantiated with CreateObject: taken at once (MapMagic places its camps itself - those the sweep finds)
        internal static void Spawned(GameObject go)
        {
            if (go == null || !On || !IsStructure(go.name)) return;
            Scan(go.transform, 0);
        }

        private static int Scan(Transform t, int depth)
        {
            if (IsStructure(t.name))
            {
                if (!t.gameObject.activeInHierarchy || !_known.Add(t.GetInstanceID())) return 0;
                var s = Make(t);
                if (s == null) return 0;
                _structures.Add(s);
                if (Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: found " + Path(t) + " at " + t.position + ", footprint " + s.Box.size.x.ToString("0") + " x " + s.Box.size.z.ToString("0") + " m");
                return 1;
            }
            if (depth >= 4 || t.childCount > 3000) return 0;
            if (t.GetComponent<Rigidbody>() != null) return 0;       // creatures, cars, items: never contain structures
            int n = 0;
            for (int i = 0; i < t.childCount; i++) n += Scan(t.GetChild(i), depth + 1);
            return n;
        }

        private static bool IsStructure(string name)
        {
            string p = name.StartsWith("Camp_") ? "Camp_" : name.StartsWith("Building_") ? "Building_" : name.StartsWith("Cave_") ? "Cave_" : null;
            if (p == null || name.Length <= p.Length || !char.IsDigit(name[p.Length])) return false;
            for (int i = p.Length; i < name.Length; i++) { char c = name[i]; if (!char.IsDigit(c)) return c == '(' || c == ' '; }
            return true;
        }

        private static string Path(Transform t) { return t.parent != null ? t.parent.name + "/" + t.name : t.name; }

        private static Structure Make(Transform root)
        {
            bool any = false; Bounds b = new Bounds();
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                int l = c.gameObject.layer;
                if (((1 << l) & BakeMask) == 0) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any) return null;
            float margin = Mathf.Max(1f, Plugin.NavMargin.Value);
            b.Expand(new Vector3(margin * 2f, 0f, margin * 2f));
            float cell = Mathf.Max(0.25f, Plugin.NavCellSize.Value);
            float maxSide = Mathf.Max(b.size.x, b.size.z);
            if (maxSide / cell > 600f) cell = maxSide / 600f;           // very large footprints get coarser cells (<= 600 x 600)
            var s = new Structure { Root = root, Name = root.name, Box = b, Cell = cell };
            s.W = Mathf.Max(2, Mathf.CeilToInt(b.size.x / cell)); s.H = Mathf.Max(2, Mathf.CeilToInt(b.size.z / cell));
            return s;
        }

        private static void BeginBake(Structure s)
        {
            s.FloorY = new float[s.W * s.H]; s.Why = new byte[s.W * s.H]; s.Open = new bool[s.W * s.H]; s.FloorHits = new Dictionary<int, int>();
            s.Edges = new byte[s.W * s.H]; s.Comp = null; s.CompSize = null; s.Phase = 0; s.CompStack = null; s.CompSizes = null;
            s.Next = 0; s.Walkable = 0; s.NoFloor = 0; s.Tight = 0; s.Solid = 0; s.ScanLimit = 0; s.BakeMs = 0f; s.BakeFrames = 0;
            // the base height: the floor under the structure's pivot (a ray from well above, first walkable surface below the pivot + 2 m)
            Vector3 p = s.Root.position;
            s.RefY = p.y;
            RaycastHit h;
            if (Physics.Raycast(new Vector3(p.x, p.y + 2f, p.z), Vector3.down, out h, 12f, BakeMask, QueryTriggerInteraction.Ignore)) s.RefY = h.point.y;
        }

        private static void BakeStep(Structure s)
        {
            if (s.Root == null) { _baking = null; return; }
            _sw.Reset(); _sw.Start();
            float budget = FrameBudget();
            int n = s.W * s.H;
            if (s.Phase == 1) { BakeEdges(s, budget); return; }
            if (s.Phase == 2)
            {
                bool done = ComponentsStep(s, budget);
                _sw.Stop();
                s.BakeMs += (float)_sw.Elapsed.TotalMilliseconds; s.BakeFrames++;
                if (done) Finish(s);
                return;
            }
            while (s.Next < n && _sw.Elapsed.TotalMilliseconds < budget)
            {
                int i = s.Next++;
                Collider fc; byte why; bool open;
                s.FloorY[i] = Floor(s, i % s.W, i / s.W, out fc, out why, out open);
                s.Why[i] = why; s.Open[i] = open;
                if (!float.IsNaN(s.FloorY[i]))
                {
                    s.Walkable++;
                    if (fc != null) { int id = fc.GetInstanceID(), c; s.FloorHits.TryGetValue(id, out c); s.FloorHits[id] = c + 1; }
                }
            }
            _sw.Stop();
            s.BakeMs += (float)_sw.Elapsed.TotalMilliseconds; s.BakeFrames++;
            if (s.Next >= n) { s.Phase = 1; s.Next = 0; }
        }

        // second pass: which neighbouring walkable cells can a body walk between - a thin wall, a cave's rock shell or a spike between two
        // free cells blocks the edge (two lines, knee and chest high, with back faces on so a one-sided mesh blocks from both sides)
        private static readonly int[] EdgeDx = { 1, 0, 1, -1 }, EdgeDz = { 0, 1, 1, 1 };
        private static void BakeEdges(Structure s, float budget)
        {
            int n = s.W * s.H;
            bool old = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                while (s.Next < n && _sw.Elapsed.TotalMilliseconds < budget)
                {
                    int i = s.Next++;
                    float ya = s.FloorY[i];
                    if (float.IsNaN(ya)) continue;
                    int x = i % s.W, z = i / s.W; byte bits = 0;
                    Vector3 a = CellCenter(s, x, z, ya);
                    for (int k = 0; k < 4; k++)
                    {
                        int cx = x + EdgeDx[k], cz = z + EdgeDz[k];
                        if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                        float yb = s.FloorY[cz * s.W + cx];
                        if (float.IsNaN(yb)) continue;
                        float dy = Mathf.Abs(ya - yb);
                        Vector3 b = CellCenter(s, cx, cz, yb);
                        if (dy <= Mathf.Max(0.1f, Plugin.NavMaxStep.Value)
                            && LowWayClear(a, b, Mathf.Max(ya, yb))                                                                      // a low rock lip / kerb
                            && BodySweep(a, b, Mathf.Max(ya, yb), 0.3f))                                                                 // the whole body, knee to shoulders
                        { bits |= (byte)(1 << k); continue; }
                        // not walkable: a hop edge? (straight neighbours only) a step / lip up to HopStep with the body's way clear above it
                        if (k > 1 || dy > HopStep) continue;
                        float top = Mathf.Max(ya, yb);
                        Vector3 a2 = new Vector3(a.x, top, a.z), b2 = new Vector3(b.x, top, b.z);
                        if (!BodySweep(a2, b2, top, 0.6f)) continue;
                        bits |= (byte)(1 << (k + 4));
                    }
                    s.Edges[i] = bits;
                }
            }
            finally { Physics.queriesHitBackfaces = old; }
            _sw.Stop();
            s.BakeMs += (float)_sw.Elapsed.TotalMilliseconds; s.BakeFrames++;
            if (s.Next >= n) { s.Phase = 2; s.Next = 0; }     // the connected areas next frame, sliced as well
        }

        private static void Finish(Structure s)
        {
            int n = s.W * s.H;
            {
                // (1.4.11) cells next to a body-height obstacle (8 neighbours too tight / inside rock)
                s.Near = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    if (float.IsNaN(s.FloorY[i])) continue;
                    int x = i % s.W, z = i / s.W;
                    for (int dz = -1; dz <= 1 && !s.Near[i]; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int cx = x + dx, cz = z + dz;
                            if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                            byte w = s.Why[cz * s.W + cx];
                            if (w == 2 || w == 3) { s.Near[i] = true; break; }
                        }
                }
                s.Baked = true; _baking = null;
                _msPerCell = Mathf.Clamp(_msPerCell + (s.BakeMs / Math.Max(1, n) - _msPerCell) * 0.5f, 0.005f, 0.2f);   // learned for the next estimates
                int floorCols = 0;
                foreach (var kv in s.FloorHits) if (kv.Value >= FloorColCells && _floorCols.Add(kv.Key)) floorCols++;
                s.FloorHits = null;
                s.EdgeCells = 0; s.EdgeWalkable = 0;
                for (int i = 0; i < n; i++) if (IsEdge(s, i)) { s.EdgeCells++; if (!float.IsNaN(s.FloorY[i])) s.EdgeWalkable++; }
                if (!Plugin.NavLog.Value && !Plugin.NavDump.Value) return;     // the rest is statistics for the log
                int open = 0; for (int i = 0; i < n; i++) if (s.Open[i]) open++;
                int areas = 0, biggest = 0, islands = 0, walls = 0;
                foreach (int sz in s.CompSize) { if (sz >= MinArea) areas++; else islands += sz; if (sz > biggest) biggest = sz; }
                for (int i = 0; i < n; i++) if (WallNext(s, i)) walls++;
                if (Plugin.NavLog.Value) Plugin.Log.LogInfo("Nav: baked " + s.Name + ": " + s.W + " x " + s.H + " cells of " + s.Cell.ToString("0.00") + " m, " + s.Walkable + " walkable (" + (100f * s.Walkable / Mathf.Max(1, s.W * s.H)).ToString("0") + " %, " + open + " under open sky), "
                    + s.NoFloor + " no floor (" + s.ScanLimit + " of them ran out of ray hits), " + s.Tight + " too tight, " + s.Solid + " inside rock; outer ring " + s.EdgeWalkable + "/" + s.EdgeCells + " walkable; " + floorCols + " new floor collider(s); " + areas + " area(s), largest " + biggest + " cells, " + islands + " cells in small islands, " + walls + " cells with a wall to a neighbour; "
                    + s.BakeMs.ToString("0") + " ms over " + s.BakeFrames + " frames");
                if (Plugin.NavDump.Value) Dump(s, "baked", -1, -1, -1, null);
            }
        }

        // the floor of a cell: the lowest walkable surface (upward facing, room for a body above it, not inside rock) within 6 m of the base height.
        // Ray by ray from the top down (a multi-hit query reports one hit per collider, and a cave's roof and floor can be one mesh; ray
        // casts skip back faces, so the inside of a cave roof is passed through and its floor is found).
        // connected areas over open edges (flood fill); the size of each
        // connected areas, a slice per frame: a flood fill over open edges whose stack and scan position live on the structure
        private static bool ComponentsStep(Structure s, float budget)
        {
            int n = s.W * s.H;
            if (s.CompStack == null)
            {
                s.Comp = new int[n];
                for (int i = 0; i < n; i++) s.Comp[i] = -1;
                s.CompSizes = new List<int>(); s.CompStack = new Stack<int>(); s.CompScan = 0; s.CompCur = -1; s.CompCurSize = 0;
            }
            int ops = 0;
            while (true)
            {
                if ((++ops & 255) == 0 && _sw.Elapsed.TotalMilliseconds >= budget) return false;
                if (s.CompStack.Count > 0)
                {
                    int c = s.CompStack.Pop(); s.CompCurSize++;
                    int x = c % s.W, z = c / s.W;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int cx = x + dx, cz = z + dz;
                            if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                            int j = cz * s.W + cx;
                            if (s.Comp[j] >= 0 || !Move(s, c, j)) continue;
                            s.Comp[j] = s.CompCur; s.CompStack.Push(j);
                        }
                    continue;
                }
                if (s.CompCur >= 0) { s.CompSizes.Add(s.CompCurSize); s.CompCur = -1; }
                while (s.CompScan < n && (s.Comp[s.CompScan] >= 0 || float.IsNaN(s.FloorY[s.CompScan]))) s.CompScan++;
                if (s.CompScan >= n) { s.CompSize = s.CompSizes.ToArray(); s.CompSizes = null; s.CompStack = null; return true; }
                s.CompCur = s.CompSizes.Count; s.CompCurSize = 0;
                s.Comp[s.CompScan] = s.CompCur; s.CompStack.Push(s.CompScan);
            }
        }

        private static void Components(Structure s)
        {
            int n = s.W * s.H;
            s.Comp = new int[n];
            for (int i = 0; i < n; i++) s.Comp[i] = -1;
            var sizes = new List<int>(); var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (s.Comp[i] >= 0 || float.IsNaN(s.FloorY[i])) continue;
                int id = sizes.Count, size = 0;
                s.Comp[i] = id; stack.Push(i);
                while (stack.Count > 0)
                {
                    int c = stack.Pop(); size++;
                    int x = c % s.W, z = c / s.W;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            int cx = x + dx, cz = z + dz;
                            if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                            int j = cz * s.W + cx;
                            if (s.Comp[j] >= 0 || !Move(s, c, j)) continue;
                            s.Comp[j] = id; stack.Push(j);
                        }
                }
                sizes.Add(size);
            }
            s.CompSize = sizes.ToArray();
        }

        private static float Floor(Structure s, int x, int z, out Collider col, out byte why, out bool open)
        {
            Vector3 c = CellCenter(s, x, z, s.RefY);
            // start just above the band a floor can be in (RefY +- 6): the rock and roofs above it never use up the hits. A cave under a big
            // rock has many stacked faces (the rock's top, the cave shell's outside and inside, the base rock): 32 hits at most.
            float y0 = s.RefY + 6.5f, bottom = s.RefY - 8f;
            float best = float.NaN; col = null; why = 1; open = false;
            RaycastHit h;
            int k = 0;
            for (; k < 32 && y0 > bottom; k++)
            {
                if (!Physics.Raycast(new Vector3(c.x, y0, c.z), Vector3.down, out h, y0 - bottom, BakeMask, QueryTriggerInteraction.Ignore)) break;
                y0 = h.point.y - 0.05f;
                if (h.normal.y < WalkNormal) continue;                 // a wall or a rock face steeper than a body can walk
                float y = h.point.y;
                if (y > s.RefY + 6f) continue;                          // roofs, rock tops above the structure
                if (y < s.RefY - 6f) break;
                Vector3 f = new Vector3(c.x, y, c.z);
                if (InsideSolid(f)) { s.Solid++; why = 3; continue; }
                if (!BodyFits(f)) { s.Tight++; why = 2; continue; }
                best = y; col = h.collider; why = 0;                                              // keep going: the LOWEST free surface is the floor (a wreck's deck,
            }                                                           // a crate top or a cave roof above it is not where NPCs walk)
            if (float.IsNaN(best)) { if (why == 1) { s.NoFloor++; if (k >= 32) { why = 4; s.ScanLimit++; } } }
            else open = !Physics.Raycast(new Vector3(c.x, best + 1.6f, c.z), Vector3.up, 40f, BakeMask, QueryTriggerInteraction.Ignore);
            return best;
        }

        // Something the brain's sweeps hit is floor, not an obstacle, when it faces up (a slope you can walk) and is the terrain or a collider
        // that the maps found to be the floor of a large area (a cave's rock mesh, which is one Default-layer collider for floor, walls and
        // roof; a camp's deck). A brazier, a crate or a spike is never one of those, so its top still counts as an obstacle.
        // ... and only where a body can actually go: a slope no steeper than WalkNormal (~37 deg) and a contact no higher than Climb above the
        // feet (a rock lip or a ledge the physics capsule can't get over is an obstacle even if its top faces up)
        internal const float WalkNormal = 0.8f, Climb = 0.25f;
        internal static bool IsFloor(Collider c, Vector3 normal, float pointY, float feetY)
        {
            if (c == null || normal.y < WalkNormal || pointY - feetY > Climb) return false;
            return c.gameObject.layer == 14 || _floorCols.Contains(c.GetInstanceID());
        }
        internal static bool IsFloorCollider(Collider c) { return c != null && (c.gameObject.layer == 14 || _floorCols.Contains(c.GetInstanceID())); }

        // An overlap test against a non-convex mesh collider (a cave's rock) only sees its triangles, so a capsule wholly inside the rock
        // passes as free and the terrain under the rock looked like floor. One ray up with back faces on: from inside solid rock it meets the
        // inside of the rock's surface; under a real roof there is open air up to head height.
        private static bool InsideSolid(Vector3 floor)
        {
            bool old = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try { return Physics.Raycast(floor + Vector3.up * 0.05f, Vector3.up, HeadTop, BakeMask, QueryTriggerInteraction.Ignore); }
            finally { Physics.queriesHitBackfaces = old; }
        }

        private static bool BodyFits(Vector3 f)
        {
            return !Physics.CheckCapsule(f + Vector3.up * (Ankle + Radius), f + Vector3.up * (HeadTop - Radius), Radius, BakeMask, QueryTriggerInteraction.Ignore)
                && !Physics.CheckCapsule(f + Vector3.up * (BodyLo + BodyR), f + Vector3.up * Mathf.Max(BodyLo + BodyR, HeadTop - BodyR), BodyR, BakeMask, QueryTriggerInteraction.Ignore);
        }

        // for the log: why the map has nothing walkable where an NPC stands
        internal static string Probe(Vector3 pos)
        {
            foreach (var s in _structures)
            {
                if (!s.Baked || s.FloorY == null || s.Root == null || !Inside(s, pos)) continue;
                int x, z; CellOf(s, pos, out x, out z);
                var sb = new System.Text.StringBuilder();
                sb.Append(" [cell ").Append(x).Append(',').Append(z).Append(" floor ").Append(float.IsNaN(s.FloorY[z * s.W + x]) ? "none" : s.FloorY[z * s.W + x].ToString("0.0")).Append(", NPC y ").Append(pos.y.ToString("0.0"));
                // re-run the tests at the NPC's own feet
                Vector3 f = new Vector3(pos.x, pos.y - 0.98f, pos.z);
                RaycastHit h;
                if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out h, 3f, BakeMask, QueryTriggerInteraction.Ignore)) { f.y = h.point.y; sb.Append(", ground ").Append(h.collider.name).Append(" n.y ").Append(h.normal.y.ToString("0.00")); }
                else sb.Append(", no ground under it");
                sb.Append(InsideSolid(f) ? ", inside solid" : ", not inside solid");
                sb.Append(BodyFits(f) ? ", body fits" : ", body does not fit (0.2-1.5 m, r 0.28)");
                int w = 0; for (int dz = -4; dz <= 4; dz++) for (int dx = -4; dx <= 4; dx++) { int cx = x + dx, cz = z + dz; if (cx >= 0 && cz >= 0 && cx < s.W && cz < s.H && !float.IsNaN(s.FloorY[cz * s.W + cx])) w++; }
                sb.Append(", walkable within 2 m: ").Append(w).Append("/81]");
                return sb.ToString();
            }
            return "";
        }

        private static Vector3 CellCenter(Structure s, int x, int z, float y)
        {
            return new Vector3(s.Box.min.x + (x + 0.5f) * s.Cell, y, s.Box.min.z + (z + 0.5f) * s.Cell);
        }

        private static bool CellOf(Structure s, Vector3 p, out int x, out int z)
        {
            x = Mathf.FloorToInt((p.x - s.Box.min.x) / s.Cell); z = Mathf.FloorToInt((p.z - s.Box.min.z) / s.Cell);
            return x >= 0 && z >= 0 && x < s.W && z < s.H;
        }

        private static bool Inside(Structure s, Vector3 p) { int x, z; return CellOf(s, p, out x, out z); }

        // the nearest walkable cell within r cells (an NPC hugging a wall stands in a blocked cell)
        // the cell an NPC really stands in / next to: nearest cells first, not in a tiny island, with no wall between the NPC and the cell
        private static readonly List<int> _cand = new List<int>();
        private static int NearestReachable(Structure s, Vector3 pos, int x, int z, int r)
        {
            if (x >= 0 && z >= 0 && x < s.W && z < s.H)
            {
                int own = z * s.W + x;
                if (!float.IsNaN(s.FloorY[own]) && BigArea(s, own)) return own;      // the usual case: standing on a mapped cell
            }
            _cand.Clear();
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                    int i = cz * s.W + cx;
                    if (float.IsNaN(s.FloorY[i]) || !BigArea(s, i)) continue;
                    _cand.Add(i);
                }
            _cand.Sort((a, b) => Dist2(s, a, x, z).CompareTo(Dist2(s, b, x, z)));
            int tried = 0;
            foreach (int i in _cand)
            {
                Vector3 c = CellCenter(s, i % s.W, i / s.W, s.FloorY[i]);
                if (!Physics.Linecast(new Vector3(pos.x, c.y + 0.5f, pos.z), c + Vector3.up * 0.5f, BakeMask, QueryTriggerInteraction.Ignore)) return i;
                if (++tried >= 12) break;
            }
            return _cand.Count > 0 ? _cand[0] : -1;
        }
        private static int Dist2(Structure s, int i, int x, int z) { int dx = i % s.W - x, dz = i / s.W - z; return dx * dx + dz * dz; }

        private static int NearestWalkable(Structure s, int x, int z, int r)
        {
            int best = -1; int bestD = int.MaxValue;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                    int i = cz * s.W + cx;
                    if (float.IsNaN(s.FloorY[i]) || !BigArea(s, i)) continue;
                    int dd = dx * dx + dz * dz;
                    if (dd < bestD) { bestD = dd; best = i; }
                }
            return best;
        }

        // Relaxed routing (the fallback when the NPC's area of the map is sealed off): cells with no floor found (black) may be crossed -
        // a floor the bake missed is more likely than a wall there; too-tight cells (walls, spikes) and rock never.
        private static bool _relax;
        private static bool Crossable(Structure s, int i) { return !float.IsNaN(s.FloorY[i]) || s.Why[i] == 1 || s.Why[i] == 4; }

        private static bool Step(Structure s, int a, int b) { return EdgeKind(s, a, b) > 0; }

        // 0 = no way, 1 = walk, 2 = hop (a lip / step the body hops; not while _noHop)
        private static int EdgeKind(Structure s, int a, int b)
        {
            float ya = s.FloorY[a], yb = s.FloorY[b];
            if (_relax && (float.IsNaN(ya) || float.IsNaN(yb))) return s.Why != null && Crossable(s, a) && Crossable(s, b) ? 1 : 0;
            if (float.IsNaN(ya) || float.IsNaN(yb)) return 0;
            bool walkStep = Mathf.Abs(ya - yb) <= Mathf.Max(0.1f, Plugin.NavMaxStep.Value);
            if (s.Edges == null || s.Phase < 2) return walkStep ? 1 : 0;
            int ax = a % s.W, az = a / s.W, bx = b % s.W, bz = b / s.W;
            int dx = bx - ax, dz = bz - az;
            if (dz < 0 || (dz == 0 && dx < 0)) { int t = a; a = b; b = t; dx = -dx; dz = -dz; }
            int k = dz == 0 ? 0 : dx == 0 ? 1 : dx > 0 ? 2 : 3;
            if (walkStep && (s.Edges[a] & (1 << k)) != 0) return 1;
            if (!_noHop && k <= 1 && (s.Edges[a] & (1 << (k + 4))) != 0) return 2;
            return 0;
        }

        // the route from pos toward next (first 2 m) crosses a hop edge
        internal static bool HopAhead(Vector3 pos, Vector3 next)
        {
            int hc; var s = BakedAt(pos, out hc);
            if (s == null) return false;
            Vector3 d = next - pos; d.y = 0f;
            float len = Mathf.Min(2f, d.magnitude);
            if (len < 0.05f) return false;
            d /= d.magnitude;
            int prev = hc, x, z;
            for (float t = s.Cell * 0.5f; t <= len; t += s.Cell * 0.5f)
            {
                if (!CellOf(s, pos + d * t, out x, out z)) return false;
                int i = z * s.W + x;
                if (i == prev) continue;
                int px = prev % s.W, pz = prev / s.W;
                if (px != x && pz != z)
                {   // diagonal: through either straight neighbour
                    int m1 = pz * s.W + x, m2 = z * s.W + px;
                    if (EdgeKind(s, prev, m1) == 2 || EdgeKind(s, m1, i) == 2 || EdgeKind(s, prev, m2) == 2 || EdgeKind(s, m2, i) == 2) return true;
                }
                else if (EdgeKind(s, prev, i) == 2) return true;
                prev = i;
            }
            return false;
        }

        // a walkable cell with a walkable neighbour at a walkable height that it still can't reach (a wall / spike between them)
        private static bool WallNext(Structure s, int i)
        {
            if (float.IsNaN(s.FloorY[i]) || s.Edges == null) return false;
            int x = i % s.W, z = i / s.W;
            for (int k = 0; k < 4; k++)
            {
                int cx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), cz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                int j = cz * s.W + cx; float o = s.FloorY[j];
                if (float.IsNaN(o) || Mathf.Abs(o - s.FloorY[i]) > Mathf.Max(0.1f, Plugin.NavMaxStep.Value)) continue;
                if (!Step(s, i, j)) return true;
            }
            return false;
        }

        // one step on the grid as a body takes it: a diagonal step also needs both cells beside it passable from here - no corner cutting
        // (the route search, the straight-line check and the area grouping all use this, so they agree)
        private static bool Move(Structure s, int a, int b)
        {
            if (!Step(s, a, b)) return false;
            int ax = a % s.W, az = a / s.W, bx = b % s.W, bz = b / s.W;
            if (ax == bx || az == bz) return true;
            return Step(s, a, az * s.W + bx) && Step(s, a, bz * s.W + ax);
        }

        private static bool BigArea(Structure s, int i) { return s.Comp == null || (s.Comp[i] >= 0 && s.CompSize[s.Comp[i]] >= MinArea); }

        // ---------- routing ----------
        // A waypoint toward goal for an NPC at pos, or false when no structure is involved / the way is straight. pathLeft = path length to the
        // goal (inside) or to the exit plus the straight rest (outside), for the brain's progress check.
        internal static string LastReason = "";
        internal static bool Next(GameObject owner, Vector3 pos, Vector3 goal, out Vector3 next, out float pathLeft)
        {
            next = goal; pathLeft = 0f; LastReason = "";
            _tS = null; _tFrom = _tGoal = _tExit = _tPick = -1; _tD0 = float.NaN; _tRelaxed = false;
            if (!On) return false;
            // footprints overlap (4 m margins): of the structures containing the NPC take the one whose floor is nearest its feet
            Structure s = null; int from = -1; float bestDy = float.MaxValue;
            foreach (var t in _structures)
            {
                if (!t.Baked || t.FloorY == null || t.Root == null || !Inside(t, pos)) continue;
                int tx, tz; CellOf(t, pos, out tx, out tz);
                int tf = NearestReachable(t, pos, tx, tz, 6);
                if (tf < 0) { if (s == null) { s = t; } continue; }
                float dy = Mathf.Abs(pos.y - t.FloorY[tf]);
                if (dy < bestDy) { bestDy = dy; s = t; from = tf; }
            }
            if (s == null) { LastReason = ""; return false; }
            _tS = s; _tFrom = from; _tDy = bestDy;
            if (from < 0) { LastReason = LogOn ? "no free map cell near it in " + s.Name : "-"; return false; }
            if (bestDy > 2.5f) { LastReason = LogOn ? "not on the floor of " + s.Name : "-"; return false; }   // on the roof of a cave, on a rock above a camp: not on this map
            int gx, gz;
            bool goalInside = CellOf(s, goal, out gx, out gz);
            int goalCell = goalInside ? NearestWalkable(s, gx, gz, 4) : -1;
            if (goalInside && (goalCell < 0 || Mathf.Abs(goal.y - s.FloorY[goalCell]) > 3f)) goalInside = false;   // in a wall / above the map: outside
            _tGoal = goalInside ? goalCell : -1;
            if (goalInside && GridSight(s, from, goalCell)) { LastReason = LogOn ? "straight line to the goal on the " + s.Name + " map" : "-"; return false; }  // straight across the floor: the feelers do the rest
            if (!goalInside)
            {
                // (1.6.0) the walk out = the walk in reversed: the map to one exit cell, then the straight line (OutNext)
                bool handled;
                bool r = OutNext(owner, s, from, goal, out next, out pathLeft, out handled);
                if (handled) return r;
                next = goal; pathLeft = 0f;      // no cell with a clear line out: the pre-1.6 way below
            }
            if (!goalInside && IsEdge(s, from)) { LastReason = LogOn ? "at the edge of " + s.Name : "-"; return false; }             // already at the edge of the footprint: out we go

            var f = FieldFor(s, goal, goalInside, goalCell);
            float d0 = f.Dist[from];
            if (float.IsInfinity(d0))
            {
                // The map has no way from here to the goal (the goal is outside and the footprint's outer ring can't be reached, or the goal sits
                // on a part of the map this spot doesn't connect to). Then the map's only job is to get the NPC out into the open: head for the
                // reachable open-sky cell that is best overall (path to it + straight line from it to the goal) - the cave mouth, the yard
                // outside a building - and the feelers take it from there.
                bool relaxed;
                int exit = ExitCell(owner, s, from, goal, goalInside, goalCell, out relaxed);
                _tExit = exit; _tRelaxed = relaxed;
                if (exit < 0 || exit == from) { LastReason = LogOn ? "no way out of this spot on the " + s.Name + " map" + (exit == from ? " (already at the best open spot)" : "") : "-"; return false; }
                if (relaxed) return RelaxedNext(owner, s, from, exit, pos, goal, out next, out pathLeft);
                f = FieldFor(s, CellCenter(s, exit % s.W, exit / s.W, s.FloorY[exit]), true, exit);
                d0 = f.Dist[from];
                if (float.IsInfinity(d0)) { LastReason = LogOn ? "no way to the exit on the " + s.Name + " map" : "-"; return false; }
                Vector3 ec = CellCenter(s, exit % s.W, exit / s.W, 0f);
                d0 += new Vector2(goal.x - ec.x, goal.z - ec.z).magnitude;
                if (GridSight(s, from, exit))
                {
                    _tD0 = d0; _tPick = exit;
                    next = CellCenter(s, exit % s.W, exit / s.W, s.FloorY[exit]);
                    pathLeft = d0; Remember(owner, next, s); return true;
                }
            }
            pathLeft = d0;
            // descend the field up to 16 cells, keep the farthest cell still in grid sight - but next to an obstacle (careful mode, 1.4.11)
            // the waypoint is no farther than the first such cell, so the body walks that stretch centre to centre instead of cutting across
            int cur = from, pick = from;
            bool careful = s.Near != null && s.Near[from];
            for (int k = 0; k < 16; k++)
            {
                int nb = Downhill(s, f, cur);
                if (nb < 0) break;
                cur = nb;
                if (GridSight(s, from, cur)) pick = cur; else break;
                if (careful || (s.Near != null && s.Near[cur])) break;
            }
            if (pick == from) { int nb = Downhill(s, f, from); if (nb < 0) { LastReason = LogOn ? "no downhill cell on the " + s.Name + " map" : "-"; return false; } pick = nb; }
            _tD0 = d0; _tPick = pick;
            next = CellCenter(s, pick % s.W, pick / s.W, s.FloorY[pick]);
            Remember(owner, next, s);
            return true;
        }

        // ---------- [Debug] NavTrace: what the last Next call saw (set by Next, read right after it by the brain's trace) ----------
        private static Structure _tS; private static int _tFrom, _tGoal, _tExit, _tPick; private static float _tD0, _tDy; private static bool _tRelaxed;
        internal static string TraceInfo()
        {
            var s = _tS;
            if (s == null) return "no baked map here";
            var sb = new System.Text.StringBuilder();
            sb.Append(s.Name.Replace("(Clone)", ""));
            if (_tFrom < 0) return sb.Append(" no cell").ToString();
            int comp = s.Comp != null ? s.Comp[_tFrom] : -1;
            sb.Append(" cell ").Append(_tFrom % s.W).Append(',').Append(_tFrom / s.W).Append(" dy ").Append(_tDy.ToString("0.0"));
            if (comp >= 0) sb.Append(" area#").Append(comp).Append(" (").Append(s.CompSize[comp]).Append(CompReachesRing(s, comp) ? ", reaches ring)" : ", NO ring)");
            sb.Append(_tGoal >= 0 ? " goal cell " + (_tGoal % s.W) + "," + (_tGoal / s.W) + (s.Comp != null && s.Comp[_tGoal] == comp ? " same area" : " area#" + (s.Comp != null ? s.Comp[_tGoal] : -1)) : " goal outside");
            if (_tExit >= 0) sb.Append(" EXIT ").Append(_tExit % s.W).Append(',').Append(_tExit / s.W).Append(_tRelaxed ? " relaxed" : "");
            if (!float.IsNaN(_tD0)) sb.Append(" path ").Append(_tD0.ToString("0.0")).Append(" m");
            if (_tPick >= 0) sb.Append(" wp ").Append(_tPick % s.W).Append(',').Append(_tPick / s.W);
            return sb.ToString();
        }

        private static readonly Dictionary<Structure, bool[]> _compRing = new Dictionary<Structure, bool[]>();
        private static bool CompReachesRing(Structure s, int comp)
        {
            bool[] r;
            if (!_compRing.TryGetValue(s, out r) || r.Length != s.CompSize.Length)
            {
                r = new bool[s.CompSize.Length];
                for (int i = 0; i < s.W * s.H; i++) if (IsEdge(s, i) && s.Comp[i] >= 0 && s.Comp[i] < r.Length) r[s.Comp[i]] = true;
                _compRing[s] = r;
            }
            return comp >= 0 && comp < r.Length && r[comp];
        }

        // the cell of a world point on the map the NPC was traced on ("x,z" or "-")
        internal static string CellText(Vector3 p)
        {
            var s = _tS; int x, z;
            if (s == null || s.Root == null || !CellOf(s, p, out x, out z)) return "-";
            int i = z * s.W + x;
            return x + "," + z + (float.IsNaN(s.FloorY[i]) ? (s.Why != null ? " (" + WhyName(s.Why[i]) + ")" : " (no floor)") : "");
        }
        private static string WhyName(byte w) { return w == 2 ? "too tight" : w == 3 ? "in rock" : w == 4 ? "why4" : "no floor"; }

        // A trace picture: the map, the NPC's area (from where the trace started) tinted, the route the map plans from the start cell to the
        // goal (cyan cells; to the exit when the goal is not reachable), and the NPC's real trail (white = on the map, orange = steering
        // without the map, red = backing up / resting). File <structure>_<x>_<z>_trace_<npc>.bmp
        internal static void TraceDump(string who, List<Vector3> trail, List<byte> kinds, Vector3 goal)
        {
            if (trail == null || trail.Count < 2) return;
            Structure s = null; int from = -1;
            foreach (var p in trail)
            {
                int c; s = BakedAt(p, out c);
                if (s != null) { from = c; break; }
            }
            if (s == null) { Plugin.Log.LogInfo("Nav: trace of " + who + ": the trail is on no baked map"); return; }
            int gx, gz; bool inside = CellOf(s, goal, out gx, out gz);
            int gc = inside ? NearestWalkable(s, gx, gz, 4) : -1;
            if (inside && gc < 0) inside = false;
            Field f = Build(s, goal, inside, gc);
            Field mine = Build(s, goal, true, from);
            var route = new List<int>();
            int cur = from;
            if (!float.IsInfinity(f.Dist[from]))
            {
                route.Add(cur);
                for (int k = 0; k < 4000; k++) { int nb = Downhill(s, f, cur); if (nb < 0) break; cur = nb; route.Add(cur); }
            }
            var tr = new List<int>(); var tk = new List<byte>();
            for (int i = 0; i < trail.Count; i++) { int x, z; if (CellOf(s, trail[i], out x, out z)) { tr.Add(z * s.W + x); tk.Add(kinds[i]); } }
            Dump(s, "trace_" + who.Replace("(Clone)", ""), from, inside ? gc : -1, route.Count > 0 ? route[route.Count - 1] : -1, mine.Dist, route, tr, tk);
            Plugin.Log.LogInfo("Nav: trace of " + who + " on " + s.Name + ": start cell " + (from % s.W) + "," + (from / s.W) + ", goal " + (inside ? "cell " + (gc % s.W) + "," + (gc / s.W) : "outside")
                + (route.Count > 0 ? ", map route " + route.Count + " cells" : ", NO map route from the start cell") + ", trail " + trail.Count + " points");
            Return(f); Return(mine);
        }

        // ---------- (1.6.0) a goal outside the footprint: the walk out is the walk in, reversed ----------
        // Before 1.6 an outside goal was routed by a field seeded at every ring cell with its straight distance to the goal - a straight line
        // that ignored the structure itself - and the map let go at the first ring cell. A ghost behind the camp: out of the gate, then the
        // feelers aimed back through the wall, the map pulled outward again inside the margin (wall hugging, turnarounds).
        // Now, like the walk home (a field seeded at ONE cell inside the map, then a straight line):
        // - leg 1: the map walks the NPC to one exit cell, routed as an inside goal. The exit = the cell of the NPC's area with the shortest
        //   map path + straight line to the goal among the cells whose straight line to the goal is clear on the map until it leaves the
        //   footprint (OutClear): the cave mouth's apron when the goal is in front of it, the margin on the goal's side when it is behind.
        //   Shared per (structure, area, goal 8 m bucket) for 5 s, then kept per NPC for the trip; re-picked when the goal moves > 8 m.
        // - leg 2: standing on a cell with a clear line out, Next says "straight" (false). Drifting off the line picks a new exit from there
        //   (the nearest clear line), never the old one behind it.
        private sealed class OutPlan { public Structure S; public Vector3 Goal; public int Exit; public float Made; public bool Out; }
        private static readonly Dictionary<int, OutPlan> _outPlans = new Dictionary<int, OutPlan>();
        private sealed class OutMemo { public Structure S; public int Exit; public float Until; }
        private static readonly Dictionary<long, OutMemo> _outMemo = new Dictionary<long, OutMemo>();
        private const float OutReplan = 8f; private const int MaxOutTests = 6000;
        private static float[] _ocKeys = new float[0]; private static int[] _ocIdx = new int[0];

        // the straight line from cell a toward the goal crosses only cells a body walks through (the map's own step rule), up to the edge of
        // the footprint (or the goal's cell)
        private static bool OutClear(Structure s, int a, Vector3 goal)
        {
            int x0 = a % s.W, z0 = a / s.W;
            int x1 = Mathf.FloorToInt((goal.x - s.Box.min.x) / s.Cell), z1 = Mathf.FloorToInt((goal.z - s.Box.min.z) / s.Cell);
            int dx = Math.Abs(x1 - x0), dz = Math.Abs(z1 - z0), sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1, err = dx - dz;
            int prev = a, guard = 2 * (s.W + s.H) + 4;
            while (guard-- > 0)
            {
                if (x0 == x1 && z0 == z1) return true;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; x0 += sx; }
                if (e2 < dx) { err += dx; z0 += sz; }
                if (x0 < 0 || z0 < 0 || x0 >= s.W || z0 >= s.H) return true;     // out of the footprint: open ground from here
                int i = z0 * s.W + x0;
                if (!Move(s, prev, i)) return false;
                prev = i;
            }
            return true;
        }

        private static bool OutNext(GameObject owner, Structure s, int from, Vector3 goal, out Vector3 next, out float pathLeft, out bool handled)
        {
            next = goal; pathLeft = 0f; handled = true;
            int id = owner != null ? owner.GetInstanceID() : 0;
            OutPlan p = null;
            if (owner != null) _outPlans.TryGetValue(id, out p);
            if (p != null && (p.S != s || FlatDist(p.Goal, goal) > OutReplan)) p = null;     // another map, or news about another spot: plan again
            if (OutClear(s, from, goal))
            {
                if (p != null) p.Out = true;
                LastReason = LogOn ? "straight out of " + s.Name : "-";
                return false;
            }
            float now = Time.time;
            if (p != null && p.Exit < 0 && now - p.Made < 3f) { handled = false; return false; }   // no exit a moment ago: the old way, no new search yet
            Field f = null; float d0 = float.PositiveInfinity; int pick = -1;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0 || p == null || p.Out || p.Exit < 0 || p.Exit == from || float.IsNaN(s.FloorY[p.Exit]))
                {
                    bool fresh = attempt > 0 || (p != null && p.Exit >= 0);   // drifted off its line / plan gone stale / exit doesn't connect: an exit from here (after a failed search: the area's memo)
                    int exit = PickOutExit(owner, s, from, goal, fresh);
                    p = new OutPlan { S = s, Goal = goal, Exit = exit, Made = now };
                    if (owner != null)
                    {
                        if (_outPlans.Count > 256) PruneOutPlans(now);
                        _outPlans[id] = p;
                    }
                    if (exit < 0) { handled = false; return false; }
                }
                f = ExitField(s, p.Exit, now);
                d0 = f.Dist[from];
                if (float.IsInfinity(d0) || d0 <= 0f) continue;
                pick = Descend(s, f, from);
                if (pick >= 0) break;
            }
            if (pick < 0) { p.Exit = -1; p.Made = now; handled = false; return false; }
            Vector3 ec = CellCenter(s, p.Exit % s.W, p.Exit / s.W, 0f);
            pathLeft = d0 + new Vector2(goal.x - ec.x, goal.z - ec.z).magnitude;
            _tExit = p.Exit; _tD0 = pathLeft; _tPick = pick;
            next = CellCenter(s, pick % s.W, pick / s.W, s.FloorY[pick]);
            Remember(owner, next, s);
            return true;
        }

        // the field to one exit cell: seeded at exactly that cell (FieldFor shares a field between goal cells up to 2 cells apart - an exit
        // whose own line out was never checked)
        private static Field ExitField(Structure s, int exit, float now)
        {
            long key = (2L << 40) | (long)(uint)exit;
            float life = Mathf.Max(0.2f, Plugin.NavFieldSeconds.Value);
            Field f;
            if (s.Fields.TryGetValue(key, out f) && now - f.Made < life) return f;
            if (f != null) { Return(f); s.Fields.Remove(key); }
            if (s.Fields.Count >= 16)
            {
                _expired.Clear();
                foreach (var kv in s.Fields) if (now - kv.Value.Made >= life) _expired.Add(kv.Key);
                foreach (var k in _expired) { Return(s.Fields[k]); s.Fields.Remove(k); }
                if (s.Fields.Count >= 32) { foreach (var kv in s.Fields) Return(kv.Value); s.Fields.Clear(); }
            }
            f = Build(s, Vector3.zero, true, exit);
            s.Fields[key] = f;
            return f;
        }

        // the exit for an outside goal: the cheapest (map path + straight line) cell of the NPC's area with a clear line out
        private static int PickOutExit(GameObject owner, Structure s, int from, Vector3 goal, bool fresh)
        {
            int comp = s.Comp != null ? s.Comp[from] : 0;
            int gkey = Mathf.FloorToInt(goal.x / OutReplan) * 73856093 ^ Mathf.FloorToInt(goal.z / OutReplan) * 19349663;
            long key = ((long)s.Root.GetInstanceID() << 40) ^ ((long)(comp & 0xFFFFF) << 20) ^ (long)(gkey & 0xFFFFF);
            float now = Time.time;
            OutMemo m;
            if (!fresh && _outMemo.TryGetValue(key, out m) && m.S == s && now < m.Until && (m.Exit < 0 || (m.Exit != from && !float.IsNaN(s.FloorY[m.Exit])))) return m.Exit;
            var sw = Stopwatch.StartNew();
            var mine = Build(s, goal, true, from);
            int n = s.W * s.H, cnt = 0;
            if (_ocKeys.Length < n) { _ocKeys = new float[n]; _ocIdx = new int[n]; }
            for (int i = 0; i < n; i++)
            {
                float d = mine.Dist[i];
                if (float.IsInfinity(d) || !BigArea(s, i)) continue;
                Vector3 c = CellCenter(s, i % s.W, i / s.W, 0f);
                _ocKeys[cnt] = d + new Vector2(goal.x - c.x, goal.z - c.z).magnitude; _ocIdx[cnt] = i; cnt++;
            }
            Return(mine);
            Array.Sort(_ocKeys, _ocIdx, 0, cnt);
            int exit = -1, tested = 0;
            for (int k = 0; k < cnt && tested < MaxOutTests; k++)
            {
                tested++;
                if (_ocIdx[k] == from) continue;          // its own line was just found blocked
                if (OutClear(s, _ocIdx[k], goal)) { exit = _ocIdx[k]; break; }
            }
            if (!fresh)
            {
                if (_outMemo.Count > 64) _outMemo.Clear();
                _outMemo[key] = new OutMemo { S = s, Exit = exit, Until = now + 5f };
            }
            if (LogOn)
                Plugin.Log.LogInfo("Nav: " + (owner != null ? owner.name : "?") + " way out of " + s.Name + " toward " + goal.x.ToString("0") + "," + goal.z.ToString("0") + ": "
                    + (exit < 0 ? "no cell with a clear line out (old routing)" : "exit cell " + (exit % s.W) + "," + (exit / s.W) + ", path + line " + _ocKeys[tested - 1].ToString("0") + " m")
                    + " (" + tested + "/" + cnt + " cells tested, " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms" + (fresh ? ", from here" : "") + ")");
            return exit;
        }

        // down the field up to 16 cells: the farthest cell still in grid sight (next to an obstacle: no farther than the first such cell)
        private static int Descend(Structure s, Field f, int from)
        {
            int cur = from, pick = from;
            bool careful = s.Near != null && s.Near[from];
            for (int k = 0; k < 16; k++)
            {
                int nb = Downhill(s, f, cur);
                if (nb < 0) break;
                cur = nb;
                if (GridSight(s, from, cur)) pick = cur; else break;
                if (careful || (s.Near != null && s.Near[cur])) break;
            }
            if (pick == from) pick = Downhill(s, f, from);
            return pick;
        }

        private static void PruneOutPlans(float now)
        {
            _expired.Clear();
            foreach (var kv in _outPlans) if (now - kv.Value.Made > 60f) _expired.Add(kv.Key);
            foreach (var k in _expired) _outPlans.Remove((int)k);
            if (_outPlans.Count > 256) _outPlans.Clear();
        }

        private static float FlatDist(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }

        // ---------- the way out when the map can't reach the goal ----------
        // The answer depends on the area the NPC is in and the goal, not on the NPC: one memo per (structure, area, goal bucket), 2 s
        private sealed class ExitMemo { public int Exit; public float Until; public bool Relaxed; public Structure S; }
        private static readonly Dictionary<long, ExitMemo> _exits = new Dictionary<long, ExitMemo>();
        private static int ExitCell(GameObject owner, Structure s, int from, Vector3 goal, bool goalInside, int goalCell, out bool exitRelaxed)
        {
            exitRelaxed = false;
            int gkey = Mathf.FloorToInt(goal.x / 4f) * 73856093 ^ Mathf.FloorToInt(goal.z / 4f) * 19349663;
            int comp = s.Comp != null ? s.Comp[from] : from;
            long mkey = ((long)s.Root.GetInstanceID() << 40) ^ ((long)(comp & 0xFFFFF) << 20) ^ (long)(gkey & 0xFFFFF);
            ExitMemo m;
            float now = Time.time;
            if (_exits.TryGetValue(mkey, out m) && m.S == s && now < m.Until && m.Exit >= 0 && m.Exit < s.W * s.H && !float.IsNaN(s.FloorY[m.Exit])) { exitRelaxed = m.Relaxed; return m.Exit; }
            if (_exits.Count > 64) _exits.Clear();
            // distances from the NPC over its part of the map
            var mine = Build(s, goal, true, from);
            int n = s.W * s.H, best = -1, bestAny = -1, size = 0, open = 0; bool edge = false;
            float bc = float.MaxValue, bca = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float d = mine.Dist[i];
                if (float.IsInfinity(d)) continue;
                size++;
                if (IsEdge(s, i)) edge = true;
                Vector3 c = CellCenter(s, i % s.W, i / s.W, 0f);
                float cost = d + new Vector2(goal.x - c.x, goal.z - c.z).magnitude;
                if (cost < bca) { bca = cost; bestAny = i; }
                if (s.Open == null || !s.Open[i]) continue;
                open++;
                if (cost < bc) { bc = cost; best = i; }
            }
            bool relaxed = false;
            if (open == 0 && !edge)
            {
                // the area is sealed on the map (no open sky, no way to the outer ring): the bake must have missed a floor or a passage. Cross
                // no-floor cells: the nearest open-sky cell of the largest area, by relaxed path + straight line to the goal.
                int bigId = -1, bigSize = 0;
                for (int c2 = 0; c2 < s.CompSize.Length; c2++) if (s.CompSize[c2] > bigSize) { bigSize = s.CompSize[c2]; bigId = c2; }
                _relax = true;
                Field rel;
                try { rel = Build(s, goal, true, from); }
                finally { _relax = false; }
                float rb = float.MaxValue; int ri = -1;
                for (int i = 0; i < n; i++)
                {
                    if (float.IsInfinity(rel.Dist[i]) || float.IsNaN(s.FloorY[i]) || !s.Open[i] || s.Comp[i] != bigId) continue;
                    Vector3 c = CellCenter(s, i % s.W, i / s.W, 0f);
                    float cost = rel.Dist[i] + new Vector2(goal.x - c.x, goal.z - c.z).magnitude;
                    if (cost < rb) { rb = cost; ri = i; }
                }
                if (ri >= 0) { best = ri; relaxed = true; }
                Return(rel);
            }
            if (best < 0) best = bestAny;
            _exits[mkey] = new ExitMemo { Exit = best, Until = now + 2f, Relaxed = relaxed, S = s };
            if (Plugin.NavLog.Value)
                Plugin.Log.LogInfo("Nav: " + (owner != null ? owner.name : "?") + " has no map route to its goal on " + s.Name + " (goal " + (goalInside ? "inside, cell " + (goalCell % s.W) + "," + (goalCell / s.W) : "outside the footprint")
                    + " at " + goal.x.ToString("0") + "," + goal.z.ToString("0") + "; its area " + size + " cells, " + open + " under open sky, " + (edge ? "reaches" : "does NOT reach") + " the outer ring ("
                    + s.EdgeWalkable + "/" + s.EdgeCells + " ring cells walkable)) -> " + (best < 0 ? "nowhere to go" : "heads for cell " + (best % s.W) + "," + (best / s.W) + (relaxed ? " (open sky, across cells the map has no floor for)" : s.Open != null && s.Open[best] ? " (open sky)" : " (no open sky in its area)")));
            if (Plugin.NavDump.Value && now >= s.NextDump)
            {
                s.NextDump = now + 20f;
                Dump(s, "noroute_" + (owner != null ? owner.name.Replace("(Clone)", "") : "npc"), from, goalInside ? goalCell : -1, best, mine.Dist);
            }
            Return(mine);
            exitRelaxed = relaxed;
            return best;
        }

        // ---------- [Debug] NavDump: the map as a picture ----------
        // (also: orange = a wall or spike between this cell and a walkable neighbour; purple = a small island, never used for routes)
        // BepInEx/config/Apocaraider/NavDump/<structure>_<x>_<z>_<tag>.bmp, 2 px per cell, north up. Walkable: grey by height (open sky
        // greenish, under a roof bluish); a walkable cell next to a walkable one more than MaxStep higher or lower: yellow; no floor: black;
        // too tight for a body: red; inside rock: brown. On a "no route" dump: the NPC's reachable area is tinted, the NPC white, the goal
        // magenta, the chosen exit cyan.
        private static void Dump(Structure s, string tag, int npc, int goalCell, int exit, float[] area) { Dump(s, tag, npc, goalCell, exit, area, null, null, null); }
        private static void Dump(Structure s, string tag, int npc, int goalCell, int exit, float[] area, List<int> route, List<int> trail, List<byte> trailKind)
        {
            try
            {
                int W = s.W, H = s.H, sc = 2, pw = W * sc, ph = H * sc, row = (pw * 3 + 3) & ~3;
                var px = new byte[row * ph];
                float step = Mathf.Max(0.1f, Plugin.NavMaxStep.Value);
                for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = z * W + x; byte r, g, b;
                        if (float.IsNaN(s.FloorY[i]))
                        {
                            switch (s.Why[i]) { case 2: r = 200; g = 30; b = 30; break; case 3: r = 110; g = 70; b = 30; break; case 4: r = 20; g = 20; b = 110; break; default: r = 0; g = 0; b = 0; break; }
                            if (area != null && !float.IsInfinity(area[i])) { r = (byte)Math.Min(255, r + 60); g = (byte)Math.Min(255, g + 90); b = (byte)Math.Min(255, b + 80); }
                        }
                        else
                        {
                            float t = Mathf.Clamp01((s.FloorY[i] - s.RefY + 3f) / 6f);
                            int v = (int)(90 + 140 * t);
                            if (s.Open[i]) { r = (byte)(v * 0.85f); g = (byte)v; b = (byte)(v * 0.8f); } else { r = (byte)(v * 0.75f); g = (byte)(v * 0.8f); b = (byte)v; }
                            bool cliff = false;
                            for (int k = 0; k < 4 && !cliff; k++)
                            {
                                int cx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), cz = z + (k == 2 ? 1 : k == 3 ? -1 : 0);
                                if (cx < 0 || cz < 0 || cx >= W || cz >= H) continue;
                                float o = s.FloorY[cz * W + cx];
                                if (!float.IsNaN(o) && Mathf.Abs(o - s.FloorY[i]) > step) cliff = true;
                            }
                            if (cliff) { r = 240; g = 220; b = 40; }
                            if (WallNext(s, i)) { r = 255; g = 130; b = 0; }
                            if (!BigArea(s, i)) { r = 150; g = 60; b = 170; }
                            if (area != null && !float.IsInfinity(area[i])) { r = (byte)(r * 0.6f); g = (byte)(g * 0.6f + 80); b = (byte)(b * 0.6f + 60); }
                        }
                        if (i == exit) { r = 0; g = 255; b = 255; }
                        if (i == goalCell) { r = 255; g = 0; b = 255; }
                        if (i == npc) { r = 255; g = 255; b = 255; }
                        for (int dy = 0; dy < sc; dy++)
                            for (int dx = 0; dx < sc; dx++)
                            {
                                int o = (z * sc + dy) * row + (x * sc + dx) * 3;
                                px[o] = b; px[o + 1] = g; px[o + 2] = r;
                            }
                    }
                if (route != null) foreach (int i in route) Paint(px, row, sc, i % W, i / W, 0, 200, 255);
                if (trail != null)
                    for (int t = 0; t < trail.Count; t++)
                    {
                        byte k = trailKind[t];
                        if (k == 0) Paint(px, row, sc, trail[t] % W, trail[t] / W, 255, 255, 255);
                        else if (k == 1) Paint(px, row, sc, trail[t] % W, trail[t] / W, 255, 140, 0);
                        else Paint(px, row, sc, trail[t] % W, trail[t] / W, 255, 0, 0);
                    }
                // marks a little bigger so they show
                foreach (var m in new[] { npc, goalCell, exit })
                {
                    if (m < 0) continue;
                    int mx = m % W, mz = m / W;
                    byte r = m == npc ? (byte)255 : m == goalCell ? (byte)255 : (byte)0, g = m == npc ? (byte)255 : m == goalCell ? (byte)0 : (byte)255, b = 255;
                    for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++)
                    {
                        int cx = mx + dx, cz = mz + dz; if (cx < 0 || cz < 0 || cx >= W || cz >= H) continue;
                        for (int yy = 0; yy < sc; yy++) for (int xx = 0; xx < sc; xx++) { int o = (cz * sc + yy) * row + (cx * sc + xx) * 3; px[o] = b; px[o + 1] = g; px[o + 2] = r; }
                    }
                }
                string dir = System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "NPCAI"), "NavDump");
                System.IO.Directory.CreateDirectory(dir);
                string file = System.IO.Path.Combine(dir, s.Name.Replace("(Clone)", "") + "_" + Mathf.RoundToInt(s.Root.position.x) + "_" + Mathf.RoundToInt(s.Root.position.z) + "_" + tag + ".bmp");
                using (var fs = new System.IO.FileStream(file, System.IO.FileMode.Create))
                using (var w = new System.IO.BinaryWriter(fs))
                {
                    w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + px.Length); w.Write(0); w.Write(54);
                    w.Write(40); w.Write(pw); w.Write(ph); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(px.Length); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
                    w.Write(px);
                }
                Plugin.Log.LogInfo("Nav: map picture " + file + " (cell " + s.Cell.ToString("0.00") + " m, origin " + s.Box.min.x.ToString("0.0") + "," + s.Box.min.z.ToString("0.0") + ", base y " + s.RefY.ToString("0.0") + ")");
            }
            catch (Exception e) { Plugin.Log.LogError("Nav: dump failed: " + e.Message); }
        }

        private static void Paint(byte[] px, int row, int sc, int x, int z, byte r, byte g, byte b)
        {
            for (int dy = 0; dy < sc; dy++) for (int dx = 0; dx < sc; dx++) { int o = (z * sc + dy) * row + (x * sc + dx) * 3; if (o >= 0 && o + 2 < px.Length) { px[o] = b; px[o + 1] = g; px[o + 2] = r; } }
        }

        // the relaxed route to an exit: a field seeded at the exit with no-floor cells crossable, cached like the others; the waypoint is the
        // farthest cell down it in (relaxed) grid sight - the brain's leg check still vetoes a waypoint behind a real wall
        private static readonly Dictionary<long, Field> _relaxedFields = new Dictionary<long, Field>();
        private static bool RelaxedNext(GameObject owner, Structure s, int from, int exit, Vector3 pos, Vector3 goal, out Vector3 next, out float pathLeft)
        {
            next = goal; pathLeft = 0f;
            long key = ((long)s.Root.GetInstanceID() << 32) ^ exit;
            Field f;
            if (!_relaxedFields.TryGetValue(key, out f) || Time.time - f.Made > 5f)
            {
                if (f != null) Return(f);
                if (_relaxedFields.Count > 32) { foreach (var kv in _relaxedFields) Return(kv.Value); _relaxedFields.Clear(); }
                _relax = true;
                try { f = Build(s, goal, true, exit); }
                finally { _relax = false; }
                _relaxedFields[key] = f;
            }
            if (float.IsInfinity(f.Dist[from])) { LastReason = LogOn ? "no relaxed way out on the " + s.Name + " map" : "-"; return false; }
            _relax = true;
            int pick = from;
            try
            {
                int cur = from;
                for (int k = 0; k < 16; k++)
                {
                    int nb = Downhill(s, f, cur);
                    if (nb < 0) break;
                    cur = nb;
                    if (GridSight(s, from, cur)) pick = cur; else break;
                }
                if (pick == from) { int nb = Downhill(s, f, from); if (nb >= 0) pick = nb; }
            }
            finally { _relax = false; }
            if (pick == from) { LastReason = LogOn ? "no downhill cell on the relaxed " + s.Name + " map" : "-"; return false; }
            float y = float.IsNaN(s.FloorY[pick]) ? pos.y - 0.98f : s.FloorY[pick];
            next = CellCenter(s, pick % s.W, pick / s.W, y);
            Vector3 ec = CellCenter(s, exit % s.W, exit / s.W, 0f);
            pathLeft = f.Dist[from] + new Vector2(goal.x - ec.x, goal.z - ec.z).magnitude;
            Remember(owner, next, s);
            return true;
        }

        private static bool IsEdge(Structure s, int i) { int x = i % s.W, z = i / s.W; return x == 0 || z == 0 || x == s.W - 1 || z == s.H - 1; }

        private static int Downhill(Structure s, Field f, int i)
        {
            int x = i % s.W, z = i / s.W; float best = f.Dist[i]; int bi = -1;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                    int j = cz * s.W + cx;
                    if (!Step(s, i, j)) continue;
                    if (dx != 0 && dz != 0 && (!Step(s, i, z * s.W + cx) || !Step(s, i, cz * s.W + x))) continue;
                    if (f.Dist[j] < best) { best = f.Dist[j]; bi = j; }
                }
            return bi;
        }

        // every cell on the grid line between two cells walkable and step-connected (a body-wide corridor, since cells are body-checked)
        private static bool GridSight(Structure s, int a, int b)
        {
            int x0 = a % s.W, z0 = a / s.W, x1 = b % s.W, z1 = b / s.W;
            int dx = Math.Abs(x1 - x0), dz = Math.Abs(z1 - z0), sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1, err = dx - dz;
            int prev = a;
            while (true)
            {
                if (x0 == x1 && z0 == z1) return true;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; x0 += sx; }
                if (e2 < dx) { err += dx; z0 += sz; }
                int i = z0 * s.W + x0;
                if (!Move(s, prev, i)) return false;     // diagonal steps never cut a corner (a pinhole in a one-cell wall is no way through)
                prev = i;
            }
        }

        private static Field FieldFor(Structure s, Vector3 goal, bool inside, int goalCell)
        {
            float now = Time.time;
            // an inside goal is bucketed to 4 x 4 cells (2 m): a running target doesn't force a new Dijkstra every think; the cached field
            // is kept while its goal cell is still within 2 cells of the real one (the feelers close the last metres anyway)
            long key = inside ? ((long)(goalCell % s.W / 4) << 20) | (long)(goalCell / s.W / 4)
                              : (long)1 << 40 | (long)(Mathf.FloorToInt(goal.x / 4f) & 0xFFFFF) << 20 | (long)(Mathf.FloorToInt(goal.z / 4f) & 0xFFFFF);
            float life = Mathf.Max(0.2f, Plugin.NavFieldSeconds.Value);
            Field f;
            if (s.Fields.TryGetValue(key, out f) && now - f.Made < life && (!inside || f.GoalCell == goalCell || f.Dist[goalCell] <= s.Cell * 2.9f)) return f;
            if (f != null) { Return(f); s.Fields.Remove(key); }
            if (s.Fields.Count >= 16)
            {
                _expired.Clear();
                foreach (var kv in s.Fields) if (now - kv.Value.Made >= life) _expired.Add(kv.Key);
                foreach (var k in _expired) { Return(s.Fields[k]); s.Fields.Remove(k); }
                if (s.Fields.Count >= 32) { foreach (var kv in s.Fields) Return(kv.Value); s.Fields.Clear(); }
            }
            f = Build(s, goal, inside, goalCell);
            s.Fields[key] = f;
            return f;
        }

        // Dijkstra over the grid (8 neighbours, no corner cutting, step limit)
        private static Field Build(Structure s, Vector3 goal, bool inside, int goalCell)
        {
            int n = s.W * s.H;
            var dist = Rent(n);
            for (int i = 0; i < n; i++) dist[i] = float.PositiveInfinity;
            var heap = new Heap(Math.Max(64, n / 4));
            if (inside) { dist[goalCell] = 0f; heap.Push(goalCell, 0f); }
            else
            {
                for (int x = 0; x < s.W; x++) { Seed(s, x, 0, goal, dist, heap); Seed(s, x, s.H - 1, goal, dist, heap); }
                for (int z = 1; z < s.H - 1; z++) { Seed(s, 0, z, goal, dist, heap); Seed(s, s.W - 1, z, goal, dist, heap); }
            }
            float c1 = s.Cell, c2 = s.Cell * 1.41421356f;
            int i0; float d;
            while (heap.Pop(out i0, out d))
            {
                if (d > dist[i0]) continue;
                int x = i0 % s.W, z = i0 / s.W;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int cx = x + dx, cz = z + dz;
                        if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H) continue;
                        int j = cz * s.W + cx;
                        if (!Step(s, i0, j)) continue;
                        if (dx != 0 && dz != 0 && (!Step(s, i0, z * s.W + cx) || !Step(s, i0, cz * s.W + x))) continue;
                        float nd = d + (dx != 0 && dz != 0 ? c2 : c1);
                        if (dx == 0 || dz == 0) { if (EdgeKind(s, i0, j) == 2) nd += HopCost; }
                        if (s.Near != null && s.Near[j]) nd += NearCost;
                        if (nd < dist[j]) { dist[j] = nd; heap.Push(j, nd); }
                    }
            }
            return new Field { Dist = dist, Made = Time.time, GoalInside = inside, GoalCell = inside ? goalCell : -1 };
        }

        private static void Seed(Structure s, int x, int z, Vector3 goal, float[] dist, Heap heap)
        {
            int i = z * s.W + x;
            if (float.IsNaN(s.FloorY[i])) return;
            Vector3 c = CellCenter(s, x, z, 0f); c.y = goal.y = 0f;
            float d = Vector3.Distance(c, new Vector3(goal.x, 0f, goal.z));
            if (d < dist[i]) { dist[i] = d; heap.Push(i, d); }
        }

        private sealed class Heap
        {
            private int[] _i; private float[] _k; private int _n;
            public Heap(int cap) { _i = new int[cap]; _k = new float[cap]; }
            public void Push(int i, float k)
            {
                if (_n == _i.Length) { Array.Resize(ref _i, _n * 2); Array.Resize(ref _k, _n * 2); }
                int c = _n++;
                while (c > 0) { int p = (c - 1) >> 1; if (_k[p] <= k) break; _i[c] = _i[p]; _k[c] = _k[p]; c = p; }
                _i[c] = i; _k[c] = k;
            }
            public bool Pop(out int i, out float k)
            {
                if (_n == 0) { i = -1; k = 0f; return false; }
                i = _i[0]; k = _k[0];
                int li = _i[--_n]; float lk = _k[_n];
                int c = 0;
                while (true)
                {
                    int a = 2 * c + 1; if (a >= _n) break;
                    int b = a + 1; int m = b < _n && _k[b] < _k[a] ? b : a;
                    if (_k[m] >= lk) break;
                    _i[c] = _i[m]; _k[c] = _k[m]; c = m;
                }
                if (_n > 0) { _i[c] = li; _k[c] = lk; }
                return true;
            }
        }

        // ---------- debug ([Debug] ShowNav) ----------
        private struct Mark { public Vector3 Next; public string Where; public float At; }
        private static readonly Dictionary<GameObject, Mark> _debug = new Dictionary<GameObject, Mark>();
        private static void Remember(GameObject owner, Vector3 next, Structure s)
        {
            if (owner == null || !Plugin.ShowNav.Value) return;
            _debug[owner] = new Mark { Next = next, Where = s.Name, At = Time.time };
        }

        // ---------- for Idle (read-only) ----------
        // the root of the structure whose footprint contains p (any bake state), or null
        internal static Transform StructureRootAt(Vector3 p)
        {
            Transform best = null; float bestDy = float.MaxValue;
            foreach (var s in _structures)
            {
                if (s.Root == null || !Inside(s, p)) continue;
                float dy = Mathf.Abs(p.y - s.RefY);
                if (dy < bestDy) { bestDy = dy; best = s.Root; }
            }
            return best;
        }

        // the baked structure under p whose floor there is closest to p.y (the NPC is on that map), or null (not baked yet / outside)
        private static Structure BakedAt(Vector3 p, out int cell)
        {
            cell = -1; Structure best = null; float bestDy = float.MaxValue;
            foreach (var s in _structures)
            {
                int x, z;
                if (!s.Baked || s.FloorY == null || s.Root == null || !CellOf(s, p, out x, out z)) continue;
                int i = z * s.W + x;
                if (float.IsNaN(s.FloorY[i])) continue;
                float dy = Mathf.Abs(p.y - s.FloorY[i]);
                if (dy < 2.5f && dy < bestDy) { bestDy = dy; best = s; cell = i; }
            }
            return best;
        }

        // Up to max patrol points around home, each CLEARLY reachable on a straight line: every cell of the line walkable and connected to
        // the next one (Move: no wall, spike or step), the cells half a metre either side walkable too (elbow room), the end at least 1 m
        // from anything unwalkable, and a body-sized capsule swept along the line hits nothing solid. 16 bearings x 5 distances (longest
        // clear one per bearing), then the points are picked >= 60 deg apart, longest first. -1 = home's map is not baked (yet), 0 = none.
        internal static int PatrolPoints(Vector3 home, List<Vector3> pts, int max, float minD, float maxD)
        {
            _noHop = true;
            try { return PatrolPointsNoHop(home, pts, max, minD, maxD); }
            finally { _noHop = false; }
        }

        private static int PatrolPointsNoHop(Vector3 home, List<Vector3> pts, int max, float minD, float maxD)
        {
            pts.Clear();
            int hc; var s = BakedAt(home, out hc);
            if (s == null) return -1;
            float hy = s.FloorY[hc];
            Vector3 h0 = new Vector3(home.x, hy, home.z);
            var cand = new List<KeyValuePair<float, Vector3>>();     // bearing (deg), point
            float[] dists = { maxD, maxD * 0.8f, maxD * 0.6f, (maxD + minD) * 0.5f * 0.7f, minD };
            for (int b = 0; b < 16; b++)
            {
                float ang = b * 22.5f;
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;
                foreach (float d in dists)
                {
                    if (d < minD - 0.01f) continue;
                    Vector3 end;
                    if (ClearLine(s, h0, dir, d, out end)) { cand.Add(new KeyValuePair<float, Vector3>(ang, end)); break; }
                }
            }
            cand.Sort((a, c) => (c.Value - h0).sqrMagnitude.CompareTo((a.Value - h0).sqrMagnitude));
            foreach (var c in cand)
            {
                bool far = true;
                foreach (var p in pts) { if (Mathf.Abs(Mathf.DeltaAngle(Bearing(h0, p), c.Key)) < 60f) { far = false; break; } }
                if (!far) continue;
                pts.Add(c.Value);
                if (pts.Count >= max) break;
            }
            return pts.Count;
        }

        private static float Bearing(Vector3 from, Vector3 to) { Vector3 d = to - from; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }

        private static bool ClearLine(Structure s, Vector3 h0, Vector3 dir, float dist, out Vector3 end)
        {
            end = h0;
            int prev = -1, x, z;
            if (!CellOf(s, h0, out x, out z)) return false;
            int comp = s.Comp != null ? s.Comp[z * s.W + x] : -2;
            Vector3 side = new Vector3(dir.z, 0f, -dir.x) * 0.5f;
            float step = s.Cell * 0.5f;
            for (float t = 0f; t <= dist + 1e-3f; t += step)
            {
                Vector3 p = h0 + dir * t;
                if (!CellOf(s, p, out x, out z)) return false;
                int i = z * s.W + x;
                if (float.IsNaN(s.FloorY[i]) || (comp != -2 && s.Comp[i] != comp)) return false;
                if (prev >= 0 && prev != i && !Move(s, prev, i)) return false;
                prev = i;
                if (!Walkable(s, p + side) || !Walkable(s, p - side)) return false;
            }
            Vector3 e = h0 + dir * dist;
            if (!CellOf(s, e, out x, out z)) return false;
            for (int dz = -2; dz <= 2; dz++)                      // >= 1 m from anything unwalkable
                for (int dx = -2; dx <= 2; dx++)
                {
                    int cx = x + dx, cz = z + dz;
                    if (cx < 0 || cz < 0 || cx >= s.W || cz >= s.H || float.IsNaN(s.FloorY[cz * s.W + cx])) return false;
                }
            float ey = s.FloorY[z * s.W + x];
            end = new Vector3(e.x, ey, e.z);
            // physics: the body swept along the line (floors differ a little: from the higher end, 0.25 m clear of the floor)
            float top = Mathf.Max(h0.y, ey);
            Vector3 a1 = new Vector3(h0.x, top + 0.25f + Radius, h0.z), a2 = new Vector3(h0.x, top + HeadTop - Radius, h0.z);
            Vector3 flat = new Vector3(e.x - h0.x, 0f, e.z - h0.z);
            if (Physics.CapsuleCast(a1, a2, Radius + 0.05f, flat.normalized, flat.magnitude, BakeMask, QueryTriggerInteraction.Ignore)) return false;
            return true;
        }

        private static bool Walkable(Structure s, Vector3 p)
        {
            int x, z;
            return CellOf(s, p, out x, out z) && !float.IsNaN(s.FloorY[z * s.W + x]);
        }

        // Points for a search round around origin: the camp map's patrol points when origin is on a baked map, otherwise physics only:
        // 12 bearings, the longest of 3 distances where a body sweep is clear, the ground under the end (and the middle) is walkable
        // (normal >= WalkNormal, within 1.5 m of origin's height) - no cliffs, no rock; picked >= 60 deg apart. Returns the count, or
        // -(count) - 1 when it had to go by physics (so the caller can tell; 0 points then = -1).
        internal static int SearchPoints(Vector3 origin, List<Vector3> pts, int max, float minD, float maxD)
        {
            int r = PatrolPoints(origin, pts, max, minD, maxD);
            if (r >= 0) return r;
            pts.Clear();
            RaycastHit g;
            Vector3 o = origin;
            if (Physics.Raycast(origin + Vector3.up * 1f, Vector3.down, out g, 3f, (1 << 0) | (1 << 14), QueryTriggerInteraction.Ignore)) o = g.point;
            var cand = new List<KeyValuePair<float, Vector3>>();
            float[] dists = { maxD, (maxD + minD) * 0.5f, minD };
            for (int b = 0; b < 12; b++)
            {
                float ang = b * 30f;
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * Vector3.forward;
                foreach (float d in dists)
                {
                    Vector3 e = o + dir * d, m = o + dir * (d * 0.5f);
                    Vector3 ge, gm;
                    if (!Ground(e, o.y, out ge) || !Ground(m, o.y, out gm)) continue;
                    if (!BodyPathClear(o, ge)) continue;
                    cand.Add(new KeyValuePair<float, Vector3>(ang, ge)); break;
                }
            }
            cand.Sort((a, c) => (c.Value - o).sqrMagnitude.CompareTo((a.Value - o).sqrMagnitude));
            foreach (var c in cand)
            {
                bool far = true;
                foreach (var p in pts) { if (Mathf.Abs(Mathf.DeltaAngle(Bearing(o, p), c.Key)) < 60f) { far = false; break; } }
                if (!far) continue;
                pts.Add(c.Value);
                if (pts.Count >= max) break;
            }
            return -pts.Count - 1;
        }

        private static bool Ground(Vector3 p, float refY, out Vector3 hit)
        {
            hit = p; RaycastHit h;
            if (!Physics.Raycast(new Vector3(p.x, refY + 2f, p.z), Vector3.down, out h, 4f, (1 << 0) | (1 << 14), QueryTriggerInteraction.Ignore)) return false;
            if (h.normal.y < WalkNormal || Mathf.Abs(h.point.y - refY) > 1.5f) return false;
            hit = h.point;
            return true;
        }

        // The ankle-high line between two neighbouring cells. Hitting something there used to make a wall - but the rim of a camp's base plate
        // (camp_base_rock, a few cm higher than the line) is just ground the body walks over: that made the orange ring around Camp_7 that
        // sealed the camp and its cave off from the outside. So when the line hits, measure the top of what it hit: no higher than
        // MaxStep above the higher of the two floors = a rim the body steps over (clear); higher = a lip / kerb / wall.
        private static bool LowWayClear(Vector3 a, Vector3 b, float topFloor)
        {
            RaycastHit h;
            Vector3 d = b - a;
            if (!Physics.Linecast(a + Vector3.up * 0.15f, b + Vector3.up * 0.15f, out h, BakeMask, QueryTriggerInteraction.Ignore)) return true;
            float step = Mathf.Max(0.1f, Plugin.NavMaxStep.Value);
            // just past the hit point (into the obstacle), from above the highest a step could be, straight down
            Vector3 flat = new Vector3(d.x, 0f, d.z); if (flat.sqrMagnitude > 1e-6f) flat.Normalize();
            Vector3 probe = new Vector3(h.point.x, topFloor + step + 0.6f, h.point.z) + flat * 0.02f;
            RaycastHit top;
            if (!Physics.Raycast(probe, Vector3.down, out top, step + 0.6f + 0.3f, BakeMask, QueryTriggerInteraction.Ignore)) return false;
            // the top of the very thing the line hit (a thin rail the probe misses lands on the ground behind it: still a wall),
            // facing up, no higher than a step
            return top.collider == h.collider && top.point.y - topFloor <= step && top.normal.y >= 0.5f;
        }

        // (1.4.9) The way between two neighbouring cells for the whole body: the NPC's own capsule (r 0.28, up to the shoulders at 1.5 m)
        // swept from one cell centre to the other, from lo above the higher floor (0.3: what LowWayClear / a step leaves; 0.6 over a hop).
        // The old test was two thin lines (0.5 and 1.2 m) between the centres: a diagonal spike or a pipe at chest height passing between
        // them, or between two cell centres, was invisible to the map while the body ran into it.
        private static bool BodySweep(Vector3 a, Vector3 b, float top, float lo)
        {
            Vector3 d = new Vector3(b.x - a.x, 0f, b.z - a.z);
            float len = d.magnitude;
            if (len < 1e-4f) return true;
            Vector3 p1 = new Vector3(a.x, top + lo + BodyR, a.z), p2 = new Vector3(a.x, top + HeadTop - BodyR, a.z);
            if (p2.y < p1.y) p2 = p1;
            return !Physics.CapsuleCast(p1, p2, BodyR, d / len, len, BakeMask, QueryTriggerInteraction.Ignore);   // with the clearance margin
        }

        // a body capsule swept from a to b (patrol legs are re-checked before walking: a car parked there since the bake)
        internal static bool BodyPathClear(Vector3 a, Vector3 b)
        {
            Vector3 flat = new Vector3(b.x - a.x, 0f, b.z - a.z);
            float top = Mathf.Max(a.y, b.y);
            Vector3 a1 = new Vector3(a.x, top + 0.25f + Radius, a.z), a2 = new Vector3(a.x, top + HeadTop - Radius, a.z);
            return flat.sqrMagnitude < 0.01f || !Physics.CapsuleCast(a1, a2, Radius, flat.normalized, flat.magnitude, BakeMask, QueryTriggerInteraction.Ignore);
        }

        internal static void DrawDebug()
        {
            if (!On || !Plugin.ShowNav.Value) return;
            var cyan = new Color(0.3f, 0.9f, 1f);
            foreach (var s in _structures)
            {
                if (s.Root == null) continue;
                var p = Player();
                if (p != null && s.Box.SqrDistance(p.position) > 150f * 150f) continue;
                DebugOverlay.Label(new Vector3(s.Box.center.x, s.RefY + 3f, s.Box.center.z), s.Name + (s.Baked ? (s.FloorY != null ? " baked, " + s.Walkable + "/" + (s.W * s.H) + " cells" : " (bake failed)") : _baking == s ? " baking " + (100 * s.Next / Math.Max(1, s.W * s.H)) + " %" : " not baked"), cyan);
            }
            float now = Time.time;
            var dead = new List<GameObject>();
            foreach (var kv in _debug)
            {
                if (kv.Key == null || now - kv.Value.At > 1.5f) { dead.Add(kv.Key); continue; }
                DebugOverlay.Mark(kv.Value.Next + Vector3.up * 0.3f, cyan, 8f);
                DebugOverlay.Label(kv.Value.Next + Vector3.up * 0.7f, "nav " + kv.Value.Where, cyan);
            }
            foreach (var k in dead) _debug.Remove(k);
        }

        internal static string Status()
        {
            int b = 0; foreach (var s in _structures) if (s.Baked) b++;
            return _structures.Count + " structures, " + b + " baked";
        }
    }
}
