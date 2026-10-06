using System.Collections.Generic;
using UnityEngine;

namespace NPCAI
{
    // [Pathfinding] FriendsPassThrough: NPCs of the same faction walk through each other while they are busy - fighting, going to a spot
    // they heard or saw something, searching, or walking home (Idle). Their solid colliders ignore each other (Physics.IgnoreCollision,
    // pairwise); once BOTH are idle again (back home / standing) and no longer overlapping, the collisions are switched back on.
    // Every 0.25 s over the registered NPCs; only pairs within 30 m are looked at (farther ones can't touch). Unity drops an ignore when a
    // collider is deactivated, so the ignored pairs are re-applied every 3 s.
    // (1.1.0) Only in corridors: a pair is made when at least one of the two stands on a map cell next to a wall / obstacle (Nav.NearWall)
    // - a doorway, a passage between huts, a cave tunnel. In the open they bump as before (the brain's AvoidFriends passes them).
    internal static class Passthrough
    {
        private static readonly Dictionary<int, Collider[]> _cols = new Dictionary<int, Collider[]>();
        private static readonly Dictionary<long, KeyValuePair<Senses.Agent, Senses.Agent>> _pairs = new Dictionary<long, KeyValuePair<Senses.Agent, Senses.Agent>>();
        private static readonly List<Senses.Agent> _list = new List<Senses.Agent>();
        private static readonly List<bool> _narrow = new List<bool>();
        private static readonly List<long> _drop = new List<long>();
        private static float _next, _nextReapply;

        public static void OnSceneLoaded() { _cols.Clear(); _pairs.Clear(); }

        private static bool Busy(Senses.Agent a)
        {
            return a.State != Senses.State.Idle || a.Target != null || a.Ghost != null || Idle.Busy(a.Owner);
        }

        private static Collider[] Cols(Senses.Agent a)
        {
            Collider[] c;
            int id = a.Owner.GetInstanceID();
            if (_cols.TryGetValue(id, out c) && c.Length > 0 && c[0] != null) return c;
            var l = new List<Collider>();
            foreach (var col in a.Owner.GetComponentsInChildren<Collider>(true)) if (col != null && !col.isTrigger) l.Add(col);
            c = l.ToArray();
            _cols[id] = c;
            return c;
        }

        private static void Set(Senses.Agent a, Senses.Agent b, bool ignore)
        {
            var ca = Cols(a); var cb = Cols(b);
            foreach (var x in ca) foreach (var y in cb) if (x != null && y != null) Physics.IgnoreCollision(x, y, ignore);
        }

        private static long Key(int a, int b) { if (a > b) { int t = a; a = b; b = t; } return ((long)a << 32) ^ (uint)b; }

        public static void Tick()
        {
            float now = Time.time;
            if (now < _next) return;
            _next = now + 0.25f;
            bool on = Plugin.FriendsPassThrough.Value && Senses.On;
            if (!on)
            {
                if (_pairs.Count > 0) { foreach (var kv in _pairs) if (kv.Value.Key.Owner != null && kv.Value.Value.Owner != null) Set(kv.Value.Key, kv.Value.Value, false); _pairs.Clear(); }
                return;
            }
            _list.Clear(); _narrow.Clear();
            foreach (var a in Senses.AllAgents) if (a.Owner != null && a.T.parent == null) { _list.Add(a); _narrow.Add(Nav.NearWall(a.T.position)); }
            bool reapply = now >= _nextReapply;
            if (reapply) _nextReapply = now + 3f;
            // existing pairs: dead ones go, re-enable when both idle and apart, re-apply the ignore now and then
            _drop.Clear();
            foreach (var kv in _pairs)
            {
                var a = kv.Value.Key; var b = kv.Value.Value;
                if (a.Owner == null || b.Owner == null) { _drop.Add(kv.Key); continue; }
                if (!Busy(a) && !Busy(b))
                {
                    Vector3 d = a.T.position - b.T.position; d.y = 0f;
                    if (d.sqrMagnitude > 1.2f * 1.2f) { Set(a, b, false); _drop.Add(kv.Key); }
                    continue;
                }
                if (reapply) Set(a, b, true);
            }
            foreach (var k in _drop) _pairs.Remove(k);
            // new pairs: same faction, close, at least one busy
            for (int i = 0; i < _list.Count; i++)
            {
                var a = _list[i];
                bool ba = Busy(a);
                Vector3 pa = a.T.position;
                for (int j = i + 1; j < _list.Count; j++)
                {
                    var b = _list[j];
                    if (a.Tag != b.Tag) continue;
                    if ((b.T.position - pa).sqrMagnitude > 30f * 30f) continue;
                    if (!ba && !Busy(b)) continue;
                    if (!_narrow[i] && !_narrow[j]) continue;      // open ground: no walking through each other
                    long key = Key(a.Owner.GetInstanceID(), b.Owner.GetInstanceID());
                    if (_pairs.ContainsKey(key)) continue;
                    Set(a, b, true);
                    _pairs[key] = new KeyValuePair<Senses.Agent, Senses.Agent>(a, b);
                }
            }
        }
    }
}
