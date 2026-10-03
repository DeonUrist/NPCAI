using System;
using System.Collections.Generic;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // [Idle] EnableIdleBehavior: what camp raiders do when nothing is going on (prototype, 1.3.0). Separate from the fight logic: it only
    // drives an NPC while the senses have it Idle (no target, no ghost, not searching) and nothing is detected - the moment it sees or
    // hears anything it lets go (animation and velocity handed back) and the senses / brain take over as before.
    //
    // Who: raiders spawned by a camp's EnemySpawn_Scrapyard_* spawner (incl. boss and bodyguards; the _Attack raid spawners excluded),
    // Coyotes from EnemySpawn_Coyotes_* with [Idle] Coyotes. Home = the exact spawn point ("spawn (n)" under the spawner's
    // spawn_locations_enemy* sibling) the CreateObject used; after a load (restored NPCs are not re-spawned) the nearest free spawn
    // point of the right faction in the structure it stands in, the floor under it.
    //
    // 1. Return home: idle for [Idle] ReturnDelay (15 s) since it last had a target / ghost / search and > 2.5 m from home -> runs back
    //    (camp map routes when on a baked map, else straight with three body feelers). An attempt fails after 2 s without getting
    //    closer; then 5 s standing, then the next; after 10 failed attempts home is forgotten. Any loaded NPC, any distance.
    // 2. Base walk: standing at home, within [Idle] WalkRadius (200 m) of the camera, on a baked map: 1-4 patrol points (Nav.PatrolPoints:
    //    clearly reachable on a straight line, elbow room, body sweep). Stand 10-25 s, walk to one, stand 4-10 s looking around, walk
    //    back. Every leg is swept again before it starts; a bump drops that point for 5 min. No points = it just stands, as vanilla.
    // 3. Search walk: while the senses have it searching (a spot it went to check, nothing there), it walks short rounds from where the
    //    search began instead of standing: 1-3 points made like the camp walk's (Nav.SearchPoints: the camp map when it stands on one,
    //    otherwise physics only), out to a point and back through the start, the brain's look-around in the pauses. When the search
    //    ends it heads home at once (ReturnDelay counts only after a fight that did not end in a search).
    // Gait: humans have only idle / run / attack clips - "walking" is the run clip at half speed (2.5 m/s); full run when far from home.
    internal static class Idle
    {
        private enum Leg { None, Home, ToPoint, Back, SearchOut, SearchBack, Investigate }

        private sealed class Ctl
        {
            public Senses.Agent A; public Rigidbody Rb; public Animator Anim;
            public bool Resolved, Excluded; public float NextResolve;
            public Vector3 Home; public bool HasHome, Forgotten; public int SpotId;
            public float BusyAt, NextThink, WaitUntil, LegStart, ProgressAt, BestLeft;
            public int Tries; public Leg Leg; public Vector3 Goal; public int PointIdx = -1;
            public Vector3 Steer; public float Speed; public bool Moving, AtHome;
            public List<Vector3> Points; public int PointsState;   // 0 not tried, -1 map not baked yet, 1 done
            public float NextPoints; public float[] DroppedUntil; public int PointsTries;
            public float LookYaw, NextLook; public bool Turning, PendingBack; public float LastHop = -10f;
            public bool NoHome, LastWasSearch, Fresh;
            public PlayMakerFSM Movement; public bool MovementLooked; public float NextAnimCheck;
            public int SNext; public bool SLooked;      // (1.4.10) the round: next point to walk, looked around at the end
            public bool InSearch, SAtOrigin; public Vector3 SOrigin; public List<Vector3> SPts; public float[] SDropped; public int SLast = -1;
            public bool Inv; public int InvGhost = -1, InvFails, SkipGhost = -1; public Vector3 SkipPos; public bool InvOnMap; public float BestStraight, LastMapLeft, LastStraightLeft;   // (1.6.0) the walk to a ghost
        }

        private sealed class Spot { public Transform T; public bool Coyotes; }

        private static readonly Dictionary<int, Ctl> _ctl = new Dictionary<int, Ctl>();
        private static readonly Dictionary<int, Spot> _spawnOf = new Dictionary<int, Spot>();        // NPC instance id -> its spawn point
        private static readonly Dictionary<int, List<Spot>> _spots = new Dictionary<int, List<Spot>>();   // structure root id -> spawn points
        private static readonly HashSet<int> _claimed = new HashSet<int>();                          // spawn point ids already someone's home
        private static readonly List<int> _dead = new List<int>();
        private static int _stagger;

        internal static bool On { get { return Plugin.IdleEnabled != null && Plugin.IdleEnabled.Value && Senses.On; } }

        public static void OnSceneLoaded() { _ctl.Clear(); _spawnOf.Clear(); _spots.Clear(); _claimed.Clear(); }

        // ---------- spawn points ----------
        // postfix on CreateObject.OnEnter (its own; Senses' and Gungirl's postfixes are separate): a camp spawner made an NPC at a spawn point
        public static void AfterCreateObject(CreateObject __instance)
        {
            try
            {
                if (__instance.spawnPoint == null || __instance.storeObject == null) return;
                var made = __instance.storeObject.Value; var at = __instance.spawnPoint.Value;
                var owner = __instance.Fsm != null ? __instance.Fsm.GameObject : null;
                if (made == null || at == null || owner == null) return;
                string n = owner.name;
                bool scrap = n.StartsWith("EnemySpawn_Scrapyard", StringComparison.Ordinal), coy = n.StartsWith("EnemySpawn_Coyotes", StringComparison.Ordinal);
                if ((!scrap && !coy) || n.IndexOf("Attack", StringComparison.Ordinal) >= 0) return;
                _spawnOf[made.GetInstanceID()] = new Spot { T = at.transform, Coyotes = coy };
            }
            catch (Exception e) { Plugin.Log.LogError("Idle: " + e); }
        }

        // the spawn points of a structure: children of each spawn_locations_enemy* that follows an EnemySpawn_Scrapyard / _Coyotes spawner
        private static List<Spot> SpotsOf(Transform root)
        {
            List<Spot> list;
            int id = root.GetInstanceID();
            if (_spots.TryGetValue(id, out list)) return list;
            list = new List<Spot>();
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                string n = c.name;
                if (!n.StartsWith("spawn_locations_enemy", StringComparison.Ordinal)) continue;
                // the spawner is the sibling just before it (EnemySpawn_X then spawn_locations_enemy_X in every camp prefab read)
                var sp = i > 0 ? root.GetChild(i - 1).name : "";
                bool scrap = sp.StartsWith("EnemySpawn_Scrapyard", StringComparison.Ordinal), coy = sp.StartsWith("EnemySpawn_Coyotes", StringComparison.Ordinal);
                if ((!scrap && !coy) || sp.IndexOf("Attack", StringComparison.Ordinal) >= 0) continue;
                for (int k = 0; k < c.childCount; k++) list.Add(new Spot { T = c.GetChild(k), Coyotes = coy });
            }
            _spots[id] = list;
            return list;
        }

        private static bool Wanted(Spot s) { return s != null && s.T != null && (!s.Coyotes || Plugin.IdleCoyotes.Value); }

        private static Vector3 FloorUnder(Vector3 p)
        {
            RaycastHit h;
            return Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out h, 6f, (1 << 0) | (1 << 14), QueryTriggerInteraction.Ignore) ? h.point : p;
        }

        private static void Resolve(Ctl c, float now)
        {
            var a = c.A;
            if (a.Tag != "Scrapyard" && (a.Tag != "Coyotes" || !Plugin.IdleCoyotes.Value)) { c.Excluded = true; c.Resolved = true; return; }
            Spot spot;
            if (_spawnOf.TryGetValue(a.Owner.GetInstanceID(), out spot))
            {
                c.Resolved = true;
                if (!Wanted(spot)) { c.Excluded = true; return; }
                SetHome(c, spot);
                return;
            }
            // restored from a save: the nearest free spawn point of its faction in the structure it stands in
            var root = Nav.StructureRootAt(a.T.position);
            if (root == null) { c.NextResolve = now + 10f; c.NoHome = true; return; }      // not in a known camp (yet): try again later
            Spot best = null; float bd = 40f * 40f;
            foreach (var s in SpotsOf(root))
            {
                if (s.T == null || _claimed.Contains(s.T.GetInstanceID()) || s.Coyotes != (a.Tag == "Coyotes")) continue;
                float d = (s.T.position - a.T.position).sqrMagnitude;
                if (d < bd) { bd = d; best = s; }
            }
            c.Resolved = true;
            if (best == null) { c.NoHome = true; return; }          // still searches like the rest, just has no home to go back to
            if (!Wanted(best)) { c.Excluded = true; return; }
            SetHome(c, best);
        }

        private static void SetHome(Ctl c, Spot s)
        {
            c.Home = FloorUnder(s.T.position); c.HasHome = true; c.SpotId = s.T.GetInstanceID(); _claimed.Add(c.SpotId);
            Log(c, "home at " + s.T.parent.name + "/" + s.T.name);
        }

        // ---------- per frame ----------
        public static void Tick()
        {
            if (!On) { if (_ctl.Count > 0) { foreach (var kv in _ctl) Stop(kv.Value, true); _ctl.Clear(); _claimed.Clear(); } return; }
            float now = Time.time, dt = Time.deltaTime;
            if (dt <= 0f) return;
            foreach (var a in Senses.AllAgents)
            {
                if (a.Owner == null) continue;
                int id = a.Owner.GetInstanceID();
                Ctl c;
                if (!_ctl.TryGetValue(id, out c))
                {
                    c = new Ctl { A = a, Rb = a.Owner.GetComponent<Rigidbody>(), BusyAt = -1000f, Fresh = true, NextThink = now + (_stagger++ % 10) * 0.02f };   // never busy yet: no return delay after a load
                    var anim = a.T.Find("Anim");
                    c.Anim = anim != null ? anim.GetComponent<Animator>() : a.Owner.GetComponentInChildren<Animator>();
                    _ctl[id] = c;
                }
                if (c.Excluded) continue;
                if (a.T.parent != null)        // seated in a car (Apocapatrol crews): not ours - no home lookup, no logic, no label
                {
                    if (c.Moving || c.Leg != Leg.None || c.Turning) Stop(c, false);
                    c.InSearch = false; c.Inv = false;
                    continue;
                }
                if (a.Blown)                   // shoved by a tornado: stands as vanilla, nothing counts (no failed tries)
                {
                    if (c.Moving || c.Leg != Leg.None || c.Turning) Stop(c, true);
                    c.InSearch = false; c.PendingBack = false; c.Inv = false;
                    continue;
                }
                if (!c.Resolved && now >= c.NextResolve) { try { Resolve(c, now); } catch (Exception e) { Plugin.Log.LogError("Idle: " + e); c.Excluded = true; continue; } if (c.Excluded) continue; }
                bool searching = a.State == Senses.State.Search && a.Target == null && a.T.parent == null;
                // (1.6.0) going to check a ghost: walked like the way home (camp map to its exit, then straight), the brain stands aside
                var gh = a.Ghost;
                bool investigating = InvestigateOn && a.State == Senses.State.Investigate && a.Target == null && gh != null && gh.Obj != null
                                     && !(gh.Id == c.SkipGhost && Flat(gh.Pos - c.SkipPos) < 8f);
                if (c.Inv && !investigating)
                {
                    c.Inv = false;
                    if (c.Leg == Leg.Investigate || c.Moving) Stop(c, a.State != Senses.State.Combat);   // to a search / idle: the idle clip, not the run
                }
                if (investigating)
                {
                    if (c.InSearch) c.InSearch = false;
                    if (!c.Inv)
                    {
                        if (c.Leg != Leg.None || c.Moving || c.Turning) Stop(c, false);
                        c.Inv = true; c.InvGhost = gh.Id; c.InvFails = 0; c.WaitUntil = 0f;
                        Log(c, "walks to ghost #" + gh.Id + " (" + Flat(a.T.position - gh.Pos).ToString("0") + " m)");
                    }
                    else if (c.InvGhost != gh.Id) { c.InvGhost = gh.Id; c.InvFails = 0; c.BestLeft = c.BestStraight = float.MaxValue; c.ProgressAt = now; }   // news: a new spot
                    c.BusyAt = now; c.LastWasSearch = false; c.Fresh = false; c.Tries = 0; c.PendingBack = false; c.AtHome = false;
                    if (now >= c.NextThink)
                    {
                        c.NextThink = now + 0.2f;
                        try { InvestigateThink(c, now); }
                        catch (Exception e) { Plugin.Log.LogError("Idle ghost walk: " + e); Stop(c, false); c.Inv = false; c.SkipGhost = gh.Id; c.SkipPos = gh.Pos; }
                    }
                    if (c.Leg == Leg.Investigate && !Brain.IsWalk(a.Owner)) { Stop(c, false); c.WaitUntil = 0f; }   // the brain took the body back: no two drivers
                    if (c.Moving) Drive(c, dt);
                    continue;
                }
                bool idle = a.State == Senses.State.Idle && a.Target == null && a.Ghost == null && a.T.parent == null
                            && (a.DetectedVar == null || a.DetectedVar.Value == null);
                if (searching)
                {
                    if (!c.InSearch && (c.Leg != Leg.None || c.Moving || c.Turning)) Stop(c, false);
                    c.BusyAt = now; c.LastWasSearch = true; c.Fresh = false; c.Tries = 0; c.PendingBack = false; c.AtHome = false;
                    if (now >= c.NextThink)
                    {
                        c.NextThink = now + 0.2f;
                        try { SearchThink(c, now); } catch (Exception e) { Plugin.Log.LogError("Idle search: " + e); Stop(c, false); c.InSearch = false; }
                    }
                    if (c.Moving) Drive(c, dt);
                    continue;
                }
                if (c.InSearch) { c.InSearch = false; if (c.Moving) Stop(c, idle); c.WaitUntil = 0f; }   // the search is over
                if (!idle)
                {
                    if (c.Leg != Leg.None || c.Moving || c.Turning) Stop(c, false);   // the fight logic takes over: its own animation, not ours
                    c.BusyAt = now; c.LastWasSearch = false; c.Fresh = false; c.Tries = 0; c.WaitUntil = 0f; c.PendingBack = false; c.AtHome = false;
                    continue;
                }
                if (now >= c.NextThink)
                {
                    c.NextThink = now + 0.2f;
                    try { Think(c, now); } catch (Exception e) { Plugin.Log.LogError("Idle: " + e); Stop(c); c.Excluded = true; }
                }
                if (!c.Moving && !c.Turning && now >= c.NextAnimCheck) { c.NextAnimCheck = now + 0.5f; RunInPlace(c); }
                if (c.Moving) Drive(c, dt);
                else if (c.Turning)
                {
                    float y = Mathf.MoveTowardsAngle(a.T.eulerAngles.y, c.LookYaw, Mathf.Max(10f, Plugin.TurnRate.Value) * 0.5f * dt);
                    a.T.rotation = Quaternion.Euler(0f, y, 0f);
                    if (Mathf.Abs(Mathf.DeltaAngle(y, c.LookYaw)) < 1f) c.Turning = false;
                }
            }
            // forget dead NPCs (and free their spawn points)
            if (_ctl.Count > 0 && (Time.frameCount & 63) == 0)
            {
                _dead.Clear();
                foreach (var kv in _ctl) if (kv.Value.A.Owner == null) { _dead.Add(kv.Key); if (kv.Value.HasHome) _claimed.Remove(kv.Value.SpotId); }
                foreach (var k in _dead) _ctl.Remove(k);
            }
        }

        private static void Think(Ctl c, float now)
        {
            if (c.Excluded || !c.HasHome || c.Forgotten) return;
            Vector3 pos = c.A.T.position;
            // after a search: home at once; after a fight that ended some other way: a moment where it is first
            if (!c.LastWasSearch && now - c.BusyAt < Mathf.Max(0f, Plugin.IdleReturnDelay.Value)) return;

            if (c.Leg != Leg.None) { LegThink(c, now, pos); return; }
            if (now < c.WaitUntil) { LookAround(c, now); return; }
            if (c.PendingBack) { c.PendingBack = false; StartLeg(c, Leg.Back, c.Home, now, 2.5f); return; }   // stood at the patrol point: back home

            float dHome = Flat(pos - c.Home);
            if (dHome > 2.5f || Mathf.Abs(pos.y - c.Home.y) > 2.5f) { c.AtHome = false; StartLeg(c, Leg.Home, c.Home, now, dHome > 25f ? 5f : 2.5f); return; }

            // at home: the base walk, near the camera only
            if (!c.AtHome) { c.AtHome = true; c.WaitUntil = now + (c.Fresh ? UnityEngine.Random.Range(1f, 4f) : UnityEngine.Random.Range(10f, 25f)); c.Fresh = false; return; }   // just loaded: the first round soon
            var cam = Camera.main;
            if (cam == null || Flat(cam.transform.position - pos) > Plugin.IdleWalkRadius.Value) return;
            if (c.PointsState != 1 && now >= c.NextPoints)
            {
                if (c.Points == null) c.Points = new List<Vector3>();
                int r = Nav.PatrolPoints(c.Home, c.Points, 4, 4f, 15f);
                if (r < 0)       // home's map not baked yet (or home is off the map: a platform high above the camp floor)
                {
                    c.PointsState = -1; c.NextPoints = now + 10f;
                    if (++c.PointsTries >= 30) { c.PointsState = 1; c.Points.Clear(); c.DroppedUntil = new float[0]; Log(c, "no camp map at its home - stands"); }
                    return;
                }
                c.PointsState = 1; c.DroppedUntil = new float[c.Points.Count];
                Log(c, r + " patrol point(s)" + (r == 0 ? " - stands" : ""));
            }
            if (c.PointsState != 1 || c.Points.Count == 0) return;
            int pick = -1, tries = 0;
            while (tries++ < 6) { int i = UnityEngine.Random.Range(0, c.Points.Count); if (now >= c.DroppedUntil[i]) { pick = i; break; } }
            if (pick < 0) { c.WaitUntil = now + 10f; return; }
            if (!Nav.BodyPathClear(c.Home, c.Points[pick])) { c.DroppedUntil[pick] = now + 300f; c.WaitUntil = now + 5f; Log(c, "patrol point " + pick + " blocked now, skipped"); return; }
            c.PointIdx = pick;
            StartLeg(c, Leg.ToPoint, c.Points[pick], now, 2.5f);
        }

        private static void SearchThink(Ctl c, float now)
        {
            Vector3 pos = c.A.T.position;
            if (!c.InSearch)
            {
                c.InSearch = true; c.SOrigin = pos; c.SAtOrigin = true; c.SLast = -1; c.SNext = 0; c.SLooked = false;
                if (c.SPts == null) c.SPts = new List<Vector3>();
                int r = Nav.SearchPoints(pos, c.SPts, 3, 4f, 12f);
                c.SDropped = new float[c.SPts.Count];
                c.WaitUntil = now + UnityEngine.Random.Range(1.5f, 3f);
                Log(c, "searches: " + c.SPts.Count + " point(s) to walk to" + (r < 0 ? " (off a camp map)" : ""));
                return;
            }
            if (c.Leg != Leg.None) { LegThink(c, now, pos); return; }
            if (now < c.WaitUntil) return;
            if (!c.SAtOrigin) { StartLeg(c, Leg.SearchBack, c.SOrigin, now, 2.5f); return; }
            // (1.4.10) each point of the round once (out and back to the spot), then a short look around and the search is over: home.
            // No point to walk (or none clear any more): it looks around a few seconds where it stands, then home.
            while (c.SNext < c.SPts.Count)
            {
                int i = c.SNext++;
                if (now < c.SDropped[i]) continue;
                if (!Nav.BodyPathClear(c.SOrigin, c.SPts[i])) { c.SDropped[i] = now + 300f; continue; }
                c.SLast = i;
                StartLeg(c, Leg.SearchOut, c.SPts[i], now, 2.5f);
                return;
            }
            if (!c.SLooked) { c.SLooked = true; c.WaitUntil = now + (c.SPts.Count == 0 ? 6f : 2f); return; }
            Log(c, c.SPts.Count == 0 ? "no round to walk, looked around - nothing here" : "walked its round - nothing here");
            Senses.EndSearch(c.A.Owner, c.SPts.Count == 0 ? "looked around, nothing here" : "walked the round, nothing here");
            c.WaitUntil = now + 1f;
        }

        // for the brain: this NPC is walking its search round (the brain leaves the body's facing to it while it does)
        // walking home or on a search round (Passthrough keeps friends' bodies apart only once this is over)
        internal static bool Busy(GameObject owner)
        {
            Ctl c;
            return owner != null && _ctl.TryGetValue(owner.GetInstanceID(), out c) && (c.InSearch || c.Leg == Leg.Home || c.Inv);
        }

        // (1.6.0) for the brain: Idle has this NPC's walk to a ghost (the brain switches to Mode Walk and stands aside)
        internal static bool WalksToGhost(GameObject owner)
        {
            Ctl c;
            return On && owner != null && _ctl.TryGetValue(owner.GetInstanceID(), out c) && c.Inv;
        }

        private static bool InvestigateOn { get { return Brain.On && Plugin.IdleGhostWalk.Value; } }

        // the walk to a ghost: starts once the brain has stood aside, follows the ghost when it moves, arrival -> the senses' search
        private static void InvestigateThink(Ctl c, float now)
        {
            var a = c.A; var g = a.Ghost;
            if (g == null) return;
            if (!Brain.IsWalk(a.Owner)) { c.NextThink = now + 0.1f; return; }      // the brain hasn't let go yet (its next think)
            if (c.Leg == Leg.Investigate)
            {
                if (Flat(g.Pos - c.Goal) > 0.25f)
                {
                    if (Flat(g.Pos - c.Goal) > 4f) { c.BestLeft = c.BestStraight = float.MaxValue; c.ProgressAt = now; }   // the spot moved: progress counts from here
                    c.Goal = g.Pos;
                }
                LegThink(c, now, a.T.position);
                return;
            }
            if (now < c.WaitUntil) return;
            c.InvOnMap = false; c.BestStraight = c.LastMapLeft = c.LastStraightLeft = float.MaxValue;
            StartLeg(c, Leg.Investigate, g.Pos, now, 5f);
        }

        internal static bool SearchWalking(GameObject owner)
        {
            Ctl c;
            return owner != null && _ctl.TryGetValue(owner.GetInstanceID(), out c) && c.InSearch && c.Moving;
        }

        private static void StartLeg(Ctl c, Leg leg, Vector3 goal, float now, float speed)
        {
            c.Leg = leg; c.Goal = goal; c.LegStart = now; c.ProgressAt = now; c.BestLeft = float.MaxValue; c.Speed = speed;
            if (leg == Leg.Home) Log(c, "runs home (" + Flat(c.A.T.position - goal).ToString("0") + " m, try " + (c.Tries + 1) + ")");
            LegThink(c, now, c.A.T.position);
            if (c.Leg != Leg.None) Go(c, speed);
        }

        private static void LegThink(Ctl c, float now, Vector3 pos)
        {
            float left = Flat(pos - c.Goal);
            bool inv = c.Leg == Leg.Investigate;
            float arrive = c.Leg == Leg.Home ? 1.5f : c.Leg == Leg.SearchBack ? 1.2f : inv ? Mathf.Max(0.5f, Plugin.ArriveDistance.Value) : 0.8f;
            if (left <= arrive && (inv || Mathf.Abs(pos.y - c.Goal.y) < 2.5f)) { Arrived(c, now); return; }   // a ghost: flat distance, as the brain judged it
            Vector3 next = c.Goal; float pathLeft = left;
            bool onMap = Nav.On && Nav.Next(c.A.Owner, pos, c.Goal, out next, out pathLeft);
            if (onMap)
            {
                c.Steer = next; left = pathLeft;
                if (Nav.HopAhead(pos, next)) Hop(c, now);             // the map's way crosses a low lip here
            }
            else { c.Steer = c.Goal; }
            if (c.Leg == Leg.Home && left <= 25f && c.Speed > 2.5f) Go(c, 2.5f);      // slows to a walk near home
            if (inv)
            {
                // map path left and straight distance are two measures: each counts against its own best (switching between them is no
                // progress by itself); a jump up of one (a new exit, the spot moved) starts that measure again from there
                c.InvOnMap = onMap;
                if (onMap) { if (left > c.LastMapLeft + 2f) c.BestLeft = left; c.LastMapLeft = left; if (left < c.BestLeft - 0.5f) { c.BestLeft = left; c.ProgressAt = now; } }
                else { if (left > c.LastStraightLeft + 2f) c.BestStraight = left; c.LastStraightLeft = left; if (left < c.BestStraight - 0.5f) { c.BestStraight = left; c.ProgressAt = now; } }
            }
            else if (left < c.BestLeft - 0.5f) { c.BestLeft = left; c.ProgressAt = now; }
            float patience = c.Leg == Leg.Home || inv ? 2f : 1f;
            if (now - c.ProgressAt > patience) Failed(c, now);
        }

        // a small hop over a low lip ahead (same impulse as the brain's): low ray hits something, knee-high ray doesn't; once per 2 s
        private static void Hop(Ctl c, float now)
        {
            if (c.Rb == null || now - c.LastHop < 2f) return;
            var col = c.A.Col; if (col == null) return;
            var b = col.bounds;
            Vector3 fwd = c.A.T.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) return; fwd.Normalize();
            float reach = Mathf.Min(b.extents.x, b.extents.z) + 0.45f;
            Vector3 low = new Vector3(b.center.x, b.min.y + 0.1f, b.center.z), knee = new Vector3(b.center.x, b.min.y + 0.55f, b.center.z);
            RaycastHit h;
            if (!Physics.Raycast(low, fwd, out h, reach, FeelMask | (1 << 14), QueryTriggerInteraction.Ignore) || h.collider.transform.root == c.A.T) return;
            if (Physics.Raycast(knee, fwd, reach + 0.2f, FeelMask | (1 << 14), QueryTriggerInteraction.Ignore)) return;
            c.LastHop = now;
            c.Rb.AddForce(Vector3.up * 3.2f + fwd * 1.5f, ForceMode.VelocityChange);
            Log(c, "hops over a low edge (" + h.collider.name + ")");
        }

        private static void Arrived(Ctl c, float now)
        {
            var leg = c.Leg;
            Stop(c);
            if (leg == Leg.Investigate) { c.InvFails = 0; Log(c, "is at the ghost"); Senses.ArrivedAt(c.A.Owner); return; }   // the senses start the search (or a pursuit)
            if (leg == Leg.SearchOut) { c.SAtOrigin = false; c.WaitUntil = now + UnityEngine.Random.Range(2f, 4f); return; }   // the brain looks around
            if (leg == Leg.SearchBack) { c.SAtOrigin = true; c.WaitUntil = now + UnityEngine.Random.Range(1.5f, 3f); return; }
            if (leg == Leg.Home) { c.Tries = 0; c.AtHome = true; c.WaitUntil = now + UnityEngine.Random.Range(10f, 25f); Log(c, "is home"); }
            else if (leg == Leg.ToPoint) { c.WaitUntil = now + UnityEngine.Random.Range(4f, 10f); c.NextLook = now + 1f; c.AtHome = false; c.PendingBack = true; }
            else { c.AtHome = true; c.WaitUntil = now + UnityEngine.Random.Range(10f, 25f); }
        }

        private static void Failed(Ctl c, float now)
        {
            var leg = c.Leg;
            Stop(c);
            if (leg == Leg.Investigate)
            {
                c.InvFails++;
                var g = c.A.Ghost;
                if (c.InvFails >= 3 && g != null)
                {
                    // no good three times: the brain takes this ghost (feelers, back-ups, hops) - the pre-1.6 walk, never worse than before
                    c.SkipGhost = g.Id; c.SkipPos = g.Pos; c.Inv = false;
                    Log(c, "gets nowhere walking to ghost #" + g.Id + " (3 tries), the brain takes over");
                    return;
                }
                Hop(c, now);
                c.WaitUntil = now + 0.5f;
                Log(c, "gets nowhere walking to the ghost (try " + c.InvFails + "/3), again");
                return;
            }
            if (leg == Leg.SearchOut || leg == Leg.SearchBack)
            {
                if (leg == Leg.SearchOut && c.SLast >= 0 && c.SDropped != null && c.SLast < c.SDropped.Length) c.SDropped[c.SLast] = now + 300f;
                if (leg == Leg.SearchBack) { c.SPts.Clear(); Log(c, "bumped on its search round, stands"); }   // can't get back to the start: no more rounds
                c.SAtOrigin = leg == Leg.SearchBack; c.WaitUntil = now + 1f;
                return;
            }
            if (leg == Leg.Home)
            {
                c.Tries++;
                int max = Math.Max(1, Plugin.IdleReturnTries.Value);
                if (c.Tries >= max) { c.Forgotten = true; _claimed.Remove(c.SpotId); Log(c, "can't get home after " + c.Tries + " tries, forgets it"); return; }
                c.WaitUntil = now + Mathf.Max(0f, Plugin.IdleRetrySeconds.Value);
                Log(c, "gets nowhere going home (try " + c.Tries + "/" + max + "), waits");
            }
            else
            {
                if (leg == Leg.ToPoint && c.PointIdx >= 0 && c.DroppedUntil != null && c.PointIdx < c.DroppedUntil.Length) c.DroppedUntil[c.PointIdx] = now + 300f;
                Log(c, "bumped on its walk, goes home");
                c.AtHome = false;          // next think: back home (a normal return)
            }
        }

        private static void LookAround(Ctl c, float now)
        {
            if (!c.PendingBack || now < c.NextLook) return;       // looks around at the patrol point only (at home it just stands, as vanilla)
            c.NextLook = now + UnityEngine.Random.Range(1.5f, 3f);
            c.LookYaw = c.A.T.eulerAngles.y + UnityEngine.Random.Range(-70f, 70f);
            c.Turning = true;
        }

        // (1.4.9) An idle NPC that nobody moves (waiting to go back, standing at home) must not play the run: the game's Movement FSM left in
        // its Run state (the run animation, a forward push every frame) or our own run animation left on -> back to the game's Idle.
        private static void RunInPlace(Ctl c)
        {
            try
            {
                if (!c.MovementLooked)
                {
                    c.MovementLooked = true;
                    foreach (var f in c.A.Owner.GetComponents<PlayMakerFSM>()) if (f.FsmName == "Movement") { c.Movement = f; break; }
                }
                bool fsmRun = c.Movement != null && c.Movement.Fsm != null && c.Movement.Fsm.ActiveStateName == "Run";
                bool animRun = c.Anim != null && c.Anim.isActiveAndEnabled && c.Anim.GetCurrentAnimatorStateInfo(0).IsName("run") && !c.Anim.IsInTransition(0);
                if (!fsmRun && !animRun) return;
                if (fsmRun) c.Movement.SendEvent("Animal_Idle");
                if (c.Anim != null) { c.Anim.speed = 1f; c.Anim.Play("idle", 0, 0f); }
                if (c.Rb != null) { var v = c.Rb.velocity; c.Rb.velocity = new Vector3(0f, v.y, 0f); }
                Log(c, "was running in place (" + (fsmRun ? "game's Movement in Run" : "run animation left on") + "), now stands");
            }
            catch (Exception e) { Plugin.Log.LogError("Idle anim: " + e.Message); c.NextAnimCheck = Time.time + 30f; }
        }

        // ---------- the body ----------
        private static void Go(Ctl c, float speed)
        {
            c.Speed = speed;
            if (c.Anim != null)
            {
                if (!c.Moving) c.Anim.Play("run", 0, UnityEngine.Random.value);
                c.Anim.speed = speed / 5f;
            }
            c.Moving = true;
        }

        private static void Stop(Ctl c) { Stop(c, true); }
        private static void Stop(Ctl c, bool playIdle)
        {
            if (c.Moving)
            {
                if (c.Anim != null) { c.Anim.speed = 1f; if (playIdle) c.Anim.Play("idle", 0, 0f); }
                if (c.Rb != null) { var v = c.Rb.velocity; c.Rb.velocity = new Vector3(0f, v.y, 0f); }
            }
            c.Moving = false; c.Leg = Leg.None; c.Turning = false;
        }

        private static readonly float[] Fan = { 0f, -35f, 35f, -70f, 70f };
        private static void Drive(Ctl c, float dt)
        {
            if (c.Rb == null) { Stop(c); return; }
            var t = c.A.T;
            if (c.Anim != null && !c.Anim.IsInTransition(0) && !c.Anim.GetCurrentAnimatorStateInfo(0).IsName("run")) { c.Anim.Play("run", 0, 0f); c.Anim.speed = c.Speed / 5f; }
            Vector3 to = c.Steer - t.position; to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) return;
            float want = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            // three-way body feelers (only off a map route): the first free heading nearest the wanted one
            float chosen = want;
            Vector3 low = t.position + Vector3.up * 0.6f, high = t.position + Vector3.up * 1.3f;
            for (int i = 0; i < Fan.Length; i++)
            {
                Vector3 d = Quaternion.Euler(0f, want + Fan[i], 0f) * Vector3.forward;
                if (!Physics.CapsuleCast(low, high, 0.3f, d, 1.2f, FeelMask, QueryTriggerInteraction.Ignore)) { chosen = want + Fan[i]; break; }
            }
            float yaw = Mathf.MoveTowardsAngle(t.eulerAngles.y, chosen, Mathf.Max(10f, Plugin.TurnRate.Value) * dt);
            t.rotation = Quaternion.Euler(0f, yaw, 0f);
            Vector3 v = t.forward * (c.Speed * Brain.TurnSpeedFactor(Mathf.Abs(Mathf.DeltaAngle(yaw, chosen))));   // slows down while turning: no arcs
            v.y = c.Rb.velocity.y;
            c.Rb.velocity = v;
        }
        private const int FeelMask = (1 << 0) | (1 << 8) | (1 << 11);     // Default (walls, rock, props), Car, Door - not Ground (slopes)

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }

        private static void Log(Ctl c, string msg)
        {
            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Idle: " + (c.A.Owner != null ? c.A.Owner.name : "?") + " " + msg);
        }

        // [Debug] ShowNavigation: home (green; grey = forgotten), patrol points (lime), search points (violet) and a label above the head
        internal static void DrawDebug()
        {
            if (!Plugin.ShowNav.Value || _ctl.Count == 0) return;
            var cam = Camera.main; if (cam == null) return;
            float now = Time.time;
            Vector3 cp = cam.transform.position;
            foreach (var c in _ctl.Values)
            {
                var a = c.A;
                if (c.Excluded || a.Owner == null || a.T.parent != null || (a.T.position - cp).sqrMagnitude > 200f * 200f) continue;
                if (c.HasHome)
                {
                    DebugOverlay.Mark(c.Home + Vector3.up * 0.2f, c.Forgotten ? Color.gray : Color.green, 10f);
                    if (c.Points != null) foreach (var p in c.Points) DebugOverlay.Mark(p + Vector3.up * 0.2f, new Color(0.6f, 1f, 0.2f), 7f);
                }
                if (c.InSearch && c.SPts != null) foreach (var p in c.SPts) DebugOverlay.Mark(p + Vector3.up * 0.2f, new Color(0.75f, 0.6f, 1f), 7f);
                string s = null; Color col = new Color(0.6f, 1f, 0.6f);
                if (c.InSearch)
                {
                    s = c.Leg == Leg.SearchOut ? "search round: out to point " + c.SLast : c.Leg == Leg.SearchBack ? "search round: back" : c.SPts == null || c.SPts.Count == 0 ? "search: no clear round, stands" : "search round: looks";
                    col = new Color(0.75f, 0.6f, 1f);
                }
                else if (a.State == Senses.State.Idle)
                {
                    if (c.Leg == Leg.Home) { s = "RETURNING " + Flat(a.T.position - c.Home).ToString("0") + " m (try " + (c.Tries + 1) + "/" + Plugin.IdleReturnTries.Value + ")"; col = Color.yellow; }
                    else if (c.Forgotten) { s = "IDLE - home forgotten"; col = Color.gray; }
                    else if (!c.HasHome) s = "IDLE - no home";
                    else if (c.Leg == Leg.ToPoint) s = "IDLE walk -> point " + c.PointIdx;
                    else if (c.Leg == Leg.Back) s = "IDLE walk -> home";
                    else if (c.PendingBack) s = "IDLE at point " + c.PointIdx + ", looks";
                    else if (c.Tries > 0 && now < c.WaitUntil) { s = "WAITS to return (" + c.Tries + "/" + Plugin.IdleReturnTries.Value + " failed)"; col = new Color(1f, 0.7f, 0.2f); }
                    else if (!c.LastWasSearch && now - c.BusyAt < Plugin.IdleReturnDelay.Value && Flat(a.T.position - c.Home) > 2.5f) { s = "IDLE - returns in " + (Plugin.IdleReturnDelay.Value - (now - c.BusyAt)).ToString("0") + " s"; col = new Color(1f, 0.9f, 0.4f); }
                    else s = "IDLE at home" + (c.PointsState == 1 ? " (" + (c.Points != null ? c.Points.Count : 0) + " patrol pts)" : c.PointsState == -1 ? " (no map yet)" : "");
                }
                if (s == null) continue;
                Vector3 top = a.Col != null ? new Vector3(a.Col.bounds.center.x, a.Col.bounds.max.y, a.Col.bounds.center.z) : a.T.position + Vector3.up * 2f;
                DebugOverlay.Label(top + Vector3.up * (c.InSearch ? 0.65f : 0.35f), s, col);   // the senses' own label (SEARCH ...) sits at +0.35
            }
        }
    }
}
