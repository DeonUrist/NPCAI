using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Micosmo.SensorToolkit.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NPCAI
{
    // How NPCs notice things ("the senses"). Replaces the game's SensorToolkit detection for every NPC that stands on its own.
    //
    // Vanilla (asset read 2026-10-02): the "Sensors" child carries a RangeSensor (100 m sphere, Player + Actor layers, a tag filter per
    // faction) feeding a LOSSensor (1 ray from the sensor at chest height, 240 deg wide, blockers Default/Item/Door/Ground, visible when the
    // moving average of 10 pulses is >= 0.2, pulsing every 0.5 s). The Detection FSM polls it every frame: notDetected -> inLOS (Attack +
    // RangedAttackWait on, "Alert" sent) -> searchRange (range sensor, through walls, 30 s) -> checkLOS -> notDetected. A health change
    // also jumps to inLOS. Nothing hears anything; only Sprokka's Alert FSM warns neighbours.
    //
    // Here: the Detection FSM's SensorGetDetections actions are answered by the mod (Harmony prefix: we write detectedObj and fire the
    // state's own events), the vanilla sensors are switched off, and what an NPC "knows" is one of:
    // - a target it sees: a 100 deg cone from the head, a ray from the head to the target's head or body (blockers Default/Car/Item/Door/
    //   Ground), range SightRange in daylight shrinking to DarkSightRange in full darkness (Enviro's main light), full range for a player
    //   with the flashlight on; NoticeSeconds in view -> combat; LoseSeconds out of view -> a ghost at the last seen spot;
    // - a ghost: a hidden object at a position, fed to detectedObj so the game's own Attack FSM (and the brain) go there. Ghosts come from
    //   sight lost (VISION), being hit (HIT), a taunt (TAUNT: a human's combat shout tells same-faction humans within TauntRange where the
    //   shouter's target or ghost is, and tells its enemies where the shouter is), gunshots (GUNSHOT: every NPC within the gun's noise
    //   range gets a ghost at the shooter), a thrown item (THROWN, ThrowRange around where it lands) and the player's running engine
    //   (ENGINE, 50-150 m by horsepower, half while idling). A ghost is shared by everyone the same event alerted; an NPC only takes a
    //   ghost of equal or higher rank than the one it has (VISION > GUNSHOT (every sound) > ENGINE, newest wins at equal rank; newer news about the same subject always wins)
    //   and never while it sees its target. Arriving at the ghost (ArriveDistance) without seeing anything starts a SearchSeconds
    //   look-around (the brain turns the body), then the NPC drops to idle where it stands. A ghost nobody holds any more disappears.
    // Everything is saved per slot (ghosts, who holds which, combat targets, search time left) and restored after a load.
    // [Senses] Enabled = false restores the game's sensors and detection untouched.
    internal static class Senses
    {
        internal enum Src { None = 0, Engine = 1, Thrown = 2, Gunshot = 3, Shout = 4, Hit = 5, Vision = 6 }
        internal enum State { Idle, Combat, Investigate, Search }

        // Ranks (Denis): VISION > GUNSHOT > SHOUT > CAR. Sight (own, or passed on by a friend's shout) is vision; a gunshot, a bullet hitting,
        // an explosion or a thrown item rank as a gunshot; an enemy's shout (an NPC's, or the player's shout key) below that; the engine last.
        internal static int Rank(Src s)
        {
            switch (s) { case Src.None: return 0; case Src.Engine: return 1; case Src.Shout: return 2; case Src.Vision: return 4; default: return 3; }
        }

        internal sealed class Ghost
        {
            public int Id; public Vector3 Pos; public Src Src; public float Born, Moved; public GameObject Obj, Source; public string About = "";
            public GameObject Subject;               // who the ghost is about (the player, an NPC): newer news about the same subject always wins
            public readonly List<Agent> Holders = new List<Agent>();
            public bool Retired;                     // nobody is on the way any more (only searchers left): never handed out, merged into or relayed again
        }

        internal sealed class Agent
        {
            public GameObject Owner; public Transform T; public Collider Col; public Transform Head;
            public PlayMakerFSM Detection, Attack; public FsmGameObject DetectedVar;
            public string Tag; public HashSet<string> Hostile; public bool Human;
            public HashSet<string> BaseHostile; public PlayMakerFSM PlayerIsEnemy;   // the prefab's own enemies, and its faction-relation FSM (Coyotes)
            public State State; public GameObject Target; public Ghost Ghost; public Src GhostPrio;
            public float SeenFor, UnseenFor, SearchUntil, NextLook, Stagger, LastLog, InvestigateUntil, InvestigateSince; public Vector3 LastSeen;
            public GameObject Pursue; public int Pursuits;     // the target it lost from sight, and how many more times it will go to where that target really is
            public Behaviour[] Sensors; public bool SensorsOff;
            public bool InStorm;          // inside a sandstorm (refreshed once a second)
            public bool Blown;            // within a tornado funnel's shove radius (refreshed once a second)
            public readonly Dictionary<int, float> Done = new Dictionary<int, float>();   // ghosts it reached (id -> the ghost's Moved then): never given again unless the ghost has newer news
            public readonly HashSet<int> Heard = new HashSet<int>();   // ghosts it already got from a friend's shout, and enemy shouters (instance ids, negated) it already went to: a shout never re-sends them
        }

        private static readonly Dictionary<int, Agent> _agents = new Dictionary<int, Agent>();
        internal static Dictionary<int, Agent>.ValueCollection AllAgents { get { return _agents.Values; } }   // read-only use (Idle)
        internal static string TagOf(GameObject owner) { Agent a; return owner != null && _agents.TryGetValue(owner.GetInstanceID(), out a) ? a.Tag : null; }
        internal static bool Blown(GameObject owner) { Agent a; return owner != null && _agents.TryGetValue(owner.GetInstanceID(), out a) && a.Blown; }
        private static readonly HashSet<int> _ignored = new HashSet<int>();
        private static readonly List<Ghost> _ghosts = new List<Ghost>();
        private static readonly List<int> _dead = new List<int>();
        private static readonly List<Agent> _scratch = new List<Agent>();
        private static int _nextGhostId = 1, _created;
        private static float _nextIgnoredClear;
        private static readonly HashSet<string> _presentTags = new HashSet<string>();   // factions with a registered NPC (Look skips the NPC loop when none is hostile)
        private static readonly Dictionary<int, string> _prefabOf = new Dictionary<int, string>();
        private static float _nextMaint, _nextEngine, _nextLightLog, _lightCache, _lightAt = -10f;
        private static GameObject _player, _playerHead, _playerCar; private static Transform _flashlight; private static float _nextPlayerFind;
        private static bool _playerInStorm;
        private static PlayMakerFSM _inCarFsm, _grabFsm; private static FsmGameObject _inCarVar, _grabItemVar; private static string _grabState;
        private static Rigidbody _thrown; private static float _thrownRest, _thrownSince;
        private static Transform _ghostRoot;
        private const int Blockers = (1 << 0) | (1 << 8) | (1 << 9) | (1 << 11) | (1 << 14);   // Default, Car, Item, Door, Ground (the game's bullet obstruction set)

        internal static bool On { get { return Plugin.SensesEnabled != null && Plugin.SensesEnabled.Value; } }

        public static void OnSceneLoaded()
        {
            foreach (var g in _ghosts) if (g.Obj != null) UnityEngine.Object.Destroy(g.Obj);
            _ghosts.Clear(); _agents.Clear(); _ignored.Clear(); _hpOf.Clear(); _prefabOf.Clear(); _presentTags.Clear(); Nwh.Clear();
            _player = null; _playerHead = null; _playerCar = null; _flashlight = null; _inCarFsm = null; _grabFsm = null; _thrown = null;
            Storm.Reset();
            _hornFsm = null;
            Persist.ResetForScene();
        }

        // ---------- per frame ----------
        public static void Tick(MonoBehaviour runner)
        {
            float now = Time.time, dt = Time.deltaTime;
            bool on = On;
            if (now >= _nextMaint)
            {
                _nextMaint = now + 1f;
                _dead.Clear(); _presentTags.Clear();
                if (_player != null) Storm.Refresh(); else Storm.Reset();      // menu / loading: no storms, no lookups
                _playerInStorm = _player != null && Storm.In(_player.transform.position);
                foreach (var kv in _agents)
                {
                    var a = kv.Value;
                    if (a.Owner == null) { if (a.Ghost != null) { a.Ghost.Holders.Remove(a); } _dead.Add(kv.Key); }
                    else
                    {
                        if (!on && a.SensorsOff) Sensors(a, true);
                        else if (on && !a.SensorsOff) Sensors(a, false);        // switched back on: the game's sensors go quiet again
                        if (a.PlayerIsEnemy != null) Relation(a, true);
                        if (a.Done.Count > 16) PruneDone(a);
                        _presentTags.Add(a.Tag);
                        a.InStorm = Storm.Any && Storm.In(a.T.position);
                        bool blown = Storm.AnyFunnel && Storm.InBlast(a.T.position);
                        if (blown != a.Blown) { a.Blown = blown; if (blown) Log(a, "is shoved by a tornado"); }
                    }
                }
                foreach (var k in _dead) _agents.Remove(k);
                if (now >= _nextIgnoredClear) { _nextIgnoredClear = now + 120f; _ignored.Clear(); }   // ids of parented / unfinished NPCs: re-evaluated, never kept for the whole session
                if (_handover.Count > 0) { _dead.Clear(); foreach (var kv in _handover) if (now - kv.Value.Value > 30f) _dead.Add(kv.Key); foreach (var k in _dead) _handover.Remove(k); }
                for (int i = _ghosts.Count - 1; i >= 0; i--)
                {
                    var g = _ghosts[i];
                    g.Holders.RemoveAll(h => h.Owner == null);
                    if (g.Holders.Count == 0 || now - g.Moved > 600f) KillGhost(i);     // nobody wants it, or no news about it for 10 min
                }
                FindPlayer();
            }
            Persist.Tick(runner);
            if (!_patrolChecked && Time.unscaledTime > 2f) HookApocapatrol();
            if (on) { try { ShoutKey(); } catch (Exception e) { Plugin.Log.LogError("Senses shout: " + e); } }
            if (!on) return;
            if (_player == null) return;

            foreach (var kv in _agents)
            {
                var a = kv.Value;
                if (a.Owner == null || a.T.parent != null) continue;       // seated in a car (Apocapatrol): not ours
                if (now < a.NextLook) continue;
                float interval = Mathf.Max(0.05f, Plugin.LookInterval.Value);
                a.NextLook = now + interval + a.Stagger;
                try { Look(a, interval, now); }
                catch (Exception e) { Plugin.Log.LogError("Senses: " + e); }
            }
            if (now >= _nextEngine) { _nextEngine = now + 0.5f; try { Engine(now); } catch (Exception e) { Plugin.Log.LogError("Senses engine: " + e); } }
            try { Thrown(now, dt); } catch (Exception e) { Plugin.Log.LogError("Senses throw: " + e); }
        }

        // ---------- sight ----------
        private static void Look(Agent a, float interval, float now)
        {
            // shoved by a tornado: it forgets what it was doing unless it sees the player right now (sight still works, hearing doesn't)
            if (a.Blown && a.State != State.Idle && !(a.State == State.Combat && a.Target == _player && a.UnseenFor < 0.5f))
            {
                Release(a);
                a.State = State.Idle; a.Target = null; a.SeenFor = 0f; a.Pursue = null;
                Log(a, "blown about by a tornado, forgets what it was doing");
            }
            if (a.State == State.Search && now >= a.SearchUntil) { SearchedNothing(a, "nothing here"); }
            if (a.State == State.Investigate && a.Ghost != null)
            {
                Vector3 to = a.Ghost.Pos - a.T.position; to.y = 0f;
                if (to.magnitude <= Mathf.Max(0.5f, Plugin.ArriveDistance.Value)) Arrived(a, now);
                // no searching on the way: only a ghost nobody has refreshed for GhostTimeout seconds (since it was given to this NPC or last
                // moved/renewed) lets the NPC settle for searching where it got to
                else if (now >= Mathf.Max(a.InvestigateSince, a.Ghost.Moved) + Mathf.Max(5f, Plugin.GhostTimeout.Value))
                { Log(a, "ghost #" + a.Ghost.Id + " went stale (" + Plugin.GhostTimeout.Value.ToString("0") + " s without news, " + to.magnitude.ToString("0") + " m left), searches from here"); Arrived(a, now); }
            }
            if (a.State == State.Combat && a.Target == null) { a.State = State.Idle; Log(a, "target gone"); }

            // candidates: the player (if this faction hunts players) and every other NPC of a hostile faction
            float d2max = Plugin.SightRange.Value * Plugin.SightRange.Value;
            Vector3 eye = Eye(a);
            GameObject best = null; float bestD = float.MaxValue;
            bool currentVisible = false;
            if (a.Hostile.Contains("Player") && _player != null && (_player.transform.position - a.T.position).sqrMagnitude <= d2max)
            {
                if (Visible(a, eye, _player, true, _playerInStorm)) { best = _player; bestD = (_player.transform.position - a.T.position).sqrMagnitude; if (a.Target == _player) currentVisible = true; }
            }
            if (!currentVisible && !a.Blown && HostileNpcAround(a))
            {
                Vector3 ap = a.T.position;
                foreach (var kv in _agents)
                {
                    var b = kv.Value;
                    if (b == a || !a.Hostile.Contains(b.Tag) || b.Owner == null) continue;
                    float d2 = (b.T.position - ap).sqrMagnitude;
                    if (d2 > d2max || (d2 >= bestD && b.Owner != a.Target)) continue;
                    if (!Visible(a, eye, b.Owner, false, b.InStorm)) continue;
                    if (b.Owner == a.Target) { best = b.Owner; currentVisible = true; break; }
                    if (d2 < bestD) { best = b.Owner; bestD = d2; }
                }
            }
            if (best != null)
            {
                a.UnseenFor = 0f;
                a.SeenFor += interval;
                a.LastSeen = best.transform.position;
                if (a.State != State.Combat || a.Target != best)
                {
                    if (a.SeenFor >= Mathf.Max(0f, Plugin.NoticeSeconds.Value)) Engage(a, best);
                }
            }
            else
            {
                a.SeenFor = Mathf.Max(0f, a.SeenFor - interval);
                a.UnseenFor += interval;
                if (a.State == State.Combat && a.UnseenFor > Mathf.Max(0f, Plugin.LoseSeconds.Value)) LoseTarget(a, now);
            }
        }

        private static bool InStormOf(GameObject go)
        {
            if (go == null || !Storm.Any) return false;
            if (go == _player) return _playerInStorm;
            var b = Get(go.transform.root.gameObject);
            return b != null ? b.InStorm : Storm.In(go.transform.position);
        }

        private static readonly List<int> _pruneIds = new List<int>();
        private static void PruneDone(Agent a)
        {
            _pruneIds.Clear();
            foreach (var kv in a.Done)
            {
                bool alive = false;
                foreach (var g in _ghosts) if (g.Id == kv.Key) { alive = true; break; }
                if (!alive) _pruneIds.Add(kv.Key);
            }
            foreach (var k in _pruneIds) a.Done.Remove(k);
        }

        private static bool HostileNpcAround(Agent a)
        {
            foreach (var t in a.Hostile) if (t != "Player" && _presentTags.Contains(t)) return true;
            return false;
        }

        private static bool Visible(Agent a, Vector3 eye, GameObject target, bool isPlayer, bool targetInStorm)
        {
            // the game's God Mode (F11, Player [GODMODE]) moves the Player, camera holder and head to layer 5 (UI) - the vanilla sensors never
            // see that layer, so neither does this sight. Hearing, ghosts and hits are untouched.
            if (isPlayer && target.layer == 5) return false;
            Vector3 tp = target.transform.position;
            Vector3 flat = tp - a.T.position; flat.y = 0f;
            float d = flat.magnitude;
            float range = isPlayer && FlashlightOn() ? Plugin.SightRange.Value : SightRange();
            if (a.InStorm || targetInStorm) range *= Storm.SightFactor;     // sand in the air between them
            if (d > range) return false;
            if (Vector3.Angle(a.T.forward, flat) > Mathf.Clamp(Plugin.SightCone.Value, 10f, 360f) * 0.5f) return false;
            Transform troot = target.transform.root;
            Vector3 head, body;
            Points(target, isPlayer, out head, out body);
            return Clear(eye, head, troot) || Clear(eye, body, troot);
        }

        private static bool Clear(Vector3 from, Vector3 to, Transform targetRoot)
        {
            RaycastHit hit;
            Vector3 d = to - from; float len = d.magnitude;
            if (len < 0.05f) return true;
            if (!Physics.Raycast(from, d / len, out hit, len, Blockers, QueryTriggerInteraction.Ignore)) return true;
            var r = hit.collider.transform.root;
            if (r == targetRoot) return true;
            if (_playerCar != null && r == _playerCar.transform.root && targetRoot == _player.transform.root) return true;   // the player in a car: the car does not hide them
            return false;
        }

        private static Vector3 Eye(Agent a)
        {
            if (a.Head != null) return a.Head.position;
            if (a.Col != null) { var b = a.Col.bounds; return new Vector3(b.center.x, b.max.y - 0.15f, b.center.z); }
            return a.T.position + Vector3.up * 1.5f;
        }

        private static void Points(GameObject target, bool isPlayer, out Vector3 head, out Vector3 body)
        {
            if (isPlayer)
            {
                head = _playerHead != null ? _playerHead.transform.position : target.transform.position + Vector3.up * 1.6f;
                body = target.transform.position + Vector3.up * 0.9f;
                return;
            }
            Agent b;
            if (_agents.TryGetValue(target.GetInstanceID(), out b))
            {
                head = b.Head != null ? b.Head.position : (b.Col != null ? new Vector3(b.Col.bounds.center.x, b.Col.bounds.max.y - 0.1f, b.Col.bounds.center.z) : target.transform.position + Vector3.up * 1.5f);
                body = b.Col != null ? b.Col.bounds.center : target.transform.position + Vector3.up * 0.8f;
                return;
            }
            head = target.transform.position + Vector3.up * 1.5f; body = target.transform.position + Vector3.up * 0.8f;
        }

        // sight range for the light right now: DarkSightRange in full darkness, SightRange in daylight
        internal static float SightRange()
        {
            float L = LightLevel();
            float dark = Mathf.Max(0.5f, Plugin.DarkSightRange.Value), max = Mathf.Max(dark, Plugin.SightRange.Value);
            return dark + (max - dark) * L;
        }

        // 0 = full night, 1 = full day, from Enviro's main light (EnviroSkyLite.instance.MainLight.intensity between the moon and sun
        // settings); the reference is tunable ([Senses] DaylightIntensity, 0 = Enviro's own sun setting). Cached for 0.5 s.
        private static Type _enviroType; private static PropertyInfo _enviroInst; private static FieldInfo _mainLight, _lightSettings, _sunI, _moonI; private static bool _enviroTried;
        internal static float LightLevel()
        {
            if (Time.time - _lightAt < 0.5f) return _lightCache;
            _lightAt = Time.time;
            float level = 1f, intensity = -1f, sun = 1f, moon = 0f;
            try
            {
                if (!_enviroTried)
                {
                    _enviroTried = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _enviroType = asm.GetType("EnviroSkyLite");
                        if (_enviroType != null) break;
                    }
                    if (_enviroType != null)
                    {
                        _enviroInst = _enviroType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                        _mainLight = Field(_enviroType, "MainLight"); _lightSettings = Field(_enviroType, "lightSettings");
                        if (_lightSettings != null) { _sunI = Field(_lightSettings.FieldType, "directLightSunIntensity"); _moonI = Field(_lightSettings.FieldType, "directLightMoonIntensity"); }
                    }
                }
                object inst = _enviroInst != null ? _enviroInst.GetValue(null, null) : null;
                var light = inst != null && _mainLight != null ? _mainLight.GetValue(inst) as Light : null;
                if (light == null) light = RenderSettings.sun;
                if (light != null)
                {
                    intensity = light.intensity;
                    object ls = inst != null && _lightSettings != null ? _lightSettings.GetValue(inst) : null;
                    if (ls != null) { if (_sunI != null) sun = Convert.ToSingle(_sunI.GetValue(ls)); if (_moonI != null) moon = Convert.ToSingle(_moonI.GetValue(ls)); }
                    if (Plugin.DaylightIntensity.Value > 0f) sun = Plugin.DaylightIntensity.Value;
                    float elev = Mathf.Clamp01(Vector3.Dot(-light.transform.forward, Vector3.up) * 3f);   // the light below the horizon counts for nothing
                    level = Mathf.Clamp01((intensity - moon) / Mathf.Max(0.01f, sun - moon)) * elev;
                }
            }
            catch (Exception e) { Plugin.Verbose("Senses: light read failed: " + e.Message); }
            _lightCache = level;
            if (Plugin.VerboseLog.Value && Time.time >= _nextLightLog)
            {
                _nextLightLog = Time.time + 60f;
                Plugin.Log.LogInfo("Senses: light " + level.ToString("0.00") + " (main light " + intensity.ToString("0.00") + ", sun " + sun + ", moon " + moon + ") -> sight " + SightRange().ToString("0") + " m");
            }
            return level;
        }

        private static FieldInfo Field(Type t, string name)
        {
            for (var c = t; c != null; c = c.BaseType)
            {
                var f = c.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f;
            }
            return null;
        }

        private static bool FlashlightOn()
        {
            if (_flashlight == null)
            {
                var cam = Camera.main;
                if (cam != null) _flashlight = cam.transform.Find("Flashlight");
                if (_flashlight == null) return false;
            }
            return _flashlight.gameObject.activeInHierarchy;
        }

        // ---------- knowledge ----------
        private static void Engage(Agent a, GameObject target)
        {
            if (a.State == State.Combat && a.Target == target) return;
            Release(a);
            a.State = State.Combat; a.Target = target; a.UnseenFor = 0f; a.Pursuits = 0; a.Pursue = null; a.Heard.RemoveWhere(x => x < 0);
            Log(a, "sees " + Name(target) + " at " + Vector3.Distance(a.T.position, target.transform.position).ToString("0") + " m");
        }

        private static void LoseTarget(Agent a, float now)
        {
            var target = a.Target;
            a.Target = null;
            var g = GetOrMake(Src.Vision, a.LastSeen, target, "last seen " + Name(target), 4f, 3f, now);
            g.Subject = target;
            a.State = State.Idle;
            Assign(a, g, Src.Vision, now);
            a.Pursue = target;
            a.Pursuits = UnityEngine.Random.Range(Mathf.Max(0, Plugin.PursuitMin.Value), Mathf.Max(Plugin.PursuitMin.Value, Plugin.PursuitMax.Value) + 1);
            Log(a, "lost sight of " + Name(target) + ", goes where it was (ghost #" + g.Id + ", will follow " + a.Pursuits + " more time(s))");
        }

        private static void Arrived(Agent a, float now)
        {
            // it lost its target from sight a moment ago: before searching it goes to where that target really is now, a few times -
            // stepping behind a barrel does not shake off a raider who was right behind you
            if (a.Ghost != null) a.Done[a.Ghost.Id] = a.Ghost.Moved;
            if (a.Pursuits > 0 && a.Pursue != null && a.Ghost != null && a.Ghost.Subject == a.Pursue)
            {
                a.Pursuits--;
                Vector3 real = a.Pursue.transform.position;
                var g = GetOrMake(Src.Vision, real, a.Pursue, "where " + Name(a.Pursue) + " really is", 2f, 1f, now);
                g.Subject = a.Pursue;
                a.State = State.Idle;
                if (Assign(a, g, Src.Vision, now)) { Log(a, "reached ghost without seeing " + Name(a.Pursue) + ", follows to where it is now (ghost #" + g.Id + ", " + a.Pursuits + " left)"); return; }
            }
            a.State = State.Search; a.SearchUntil = now + Mathf.Max(0f, Plugin.SearchSeconds.Value);
            Log(a, "reached ghost #" + (a.Ghost != null ? a.Ghost.Id.ToString() : "?") + ", looks around for " + Plugin.SearchSeconds.Value.ToString("0") + " s");
            if (a.Ghost != null) RetireIfSearched(a.Ghost);
        }

        // (1.4.10) a search that found nothing: the NPC gives up and tells its friends (same faction, humans, within AllClearRange) who are on
        // the way to the same spot - or searching it - "there's nothing": they give up too and head back (never those fighting something they see)
        private static void SearchedNothing(Agent a, string why)
        {
            var g = a.Ghost;
            Vector3 spot = g != null ? g.Pos : a.T.position;
            GiveUp(a, why);
            float range = Plugin.AllClearRange.Value;
            if (!a.Human || g == null || range <= 0f) return;
            int told = 0;
            foreach (var kv in _agents)
            {
                var f = kv.Value;
                if (f == a || f.Owner == null || !f.Human || f.Tag != a.Tag || f.T.parent != null) continue;
                if (f.State != State.Investigate && f.State != State.Search) continue;
                var fg = f.Ghost;
                if (fg == null || (fg != g && (fg.Pos - spot).sqrMagnitude > 8f * 8f)) continue;
                if ((f.T.position - a.T.position).sqrMagnitude > range * range) continue;
                f.Done[fg.Id] = fg.Moved;           // been told about it: not taken again unless there is news (the ghost moves)
                GiveUp(f, "told by " + Name(a.Owner) + " there's nothing at ghost #" + fg.Id);
                told++;
            }
            if (told > 0 && Plugin.SensesLog.Value) Plugin.Log.LogInfo("Senses: " + Name(a.Owner) + " found nothing at ghost #" + g.Id + ", tells " + told + " friend(s) to go back");
        }

        // Idle: the search round is walked (or there was none and it looked around) - the search ends now, not when SearchSeconds run out
        internal static void EndSearch(GameObject owner, string why)
        {
            var a = Get(owner);
            if (a != null && a.State == State.Search) SearchedNothing(a, why);
        }

        private static void GiveUp(Agent a, string why)
        {
            Release(a);
            a.State = State.Idle; a.Target = null; a.SeenFor = 0f;
            a.Heard.RemoveWhere(x => x < 0);    // the alert is over: a new shout from the same enemy is news again
            Log(a, "gives up (" + why + "), idle");
        }

        // An NPC takes a ghost of equal or higher rank than the one it has, or any ghost with newer news about the same subject (it went to
        // check a shout about the player; the player now shoots elsewhere: that is where the player is). Never while it sees its target.
        private static bool Assign(Agent a, Ghost g, Src prio, float now)
        {
            if (g == null || a.State == State.Combat || g.Retired) return false;
            float doneAt;
            if (a.Done.TryGetValue(g.Id, out doneAt) && g.Moved <= doneAt + 0.01f) return false;   // been there, nothing new about it
            if (a.Ghost == g)
            {
                if (Rank(prio) > Rank(a.GhostPrio)) a.GhostPrio = prio;
                if (a.State != State.Investigate) { a.State = State.Investigate; Budget(a, g); }   // Search, or Idle after Arrived() re-made the ghost it holds
                return true;
            }
            if (a.Ghost != null)
            {
                bool newerSameSubject = g.Subject != null && a.Ghost.Subject == g.Subject && g.Moved >= a.Ghost.Moved;
                if (!newerSameSubject && Rank(prio) < Rank(a.GhostPrio)) return false;
            }
            Release(a);
            a.Ghost = g; a.GhostPrio = prio; g.Holders.Add(a);
            a.State = State.Investigate; a.SeenFor = 0f;
            Budget(a, g);
            return true;
        }

        // time allowed to reach a ghost before searching from wherever the NPC got to (unreachable spots, a cave with no way out)
        private static void Budget(Agent a, Ghost g)
        {
            a.InvestigateUntil = Time.time + Mathf.Max(5f, Plugin.GhostTimeout.Value);
            a.InvestigateSince = Time.time;
        }

        // the brain gets nowhere on the way: the NPC does NOT start searching (only arrival or a stale ghost, GhostTimeout, ends the walk) -
        // the brain rests a moment and tries again
        internal static bool CannotReach(GameObject owner)
        {
            var a = Get(owner);
            if (a == null || a.State != State.Investigate || a.Ghost == null) return false;
            if (Time.time - a.LastLog > 5f) { a.LastLog = Time.time; Log(a, "gets nowhere toward ghost #" + a.Ghost.Id + " for now, keeps trying"); }
            return false;
        }

        private static void Release(Agent a)
        {
            var g = a.Ghost;
            a.Ghost = null; a.GhostPrio = Src.None;
            if (g == null) return;
            g.Holders.Remove(a);
            if (g.Holders.Count == 0) { int i = _ghosts.IndexOf(g); if (i >= 0) KillGhost(i); }
            else RetireIfSearched(g);
        }

        // nobody is on the way to it any more, only searchers are left: the ghost is retired - never handed out, merged into or relayed again,
        // and it dies with its last searcher (as every ghost dies with its last holder)
        private static void RetireIfSearched(Ghost g)
        {
            if (g.Retired) return;
            foreach (var h in g.Holders) if (h.State == State.Investigate) return;
            g.Retired = true;
            if (Plugin.SensesLog.Value) Plugin.Log.LogInfo("Senses: ghost #" + g.Id + " searched by " + g.Holders.Count + ", retired");
        }

        private static Ghost GetOrMake(Src src, Vector3 pos, GameObject source, string about, float mergeRadius, float mergeSeconds, float now)
        {
            foreach (var g in _ghosts)
            {
                if (g.Src != src || g.Retired) continue;          // a searched spot: new news makes a new ghost
                bool sameSource = source != null && g.Source == source && now - g.Moved <= mergeSeconds;
                bool samePlace = mergeRadius > 0f && now - g.Moved <= mergeSeconds && (g.Pos - pos).sqrMagnitude <= mergeRadius * mergeRadius;
                if (sameSource || samePlace)
                {
                    g.Pos = pos; g.Moved = now; if (g.Obj != null) g.Obj.transform.position = pos;
                    return g;
                }
            }
            var n = new Ghost { Id = _nextGhostId++, Pos = pos, Src = src, Born = now, Moved = now, Source = source, About = about ?? "" };
            if (_ghostRoot == null) { var r = new GameObject("NPCAI.Ghosts") { hideFlags = HideFlags.HideAndDontSave }; UnityEngine.Object.DontDestroyOnLoad(r); _ghostRoot = r.transform; }
            n.Obj = new GameObject("NPCAI.Ghost#" + n.Id) { hideFlags = HideFlags.HideAndDontSave };
            n.Obj.transform.SetParent(_ghostRoot, false);
            n.Obj.transform.position = pos;
            _ghosts.Add(n);
            if (Plugin.SensesLog.Value && (src == Src.Vision || src == Src.Hit)) Plugin.Log.LogInfo("Senses: ghost #" + n.Id + " " + src + " (" + n.About + ") at " + pos);
            return n;
        }

        private static void KillGhost(int i)
        {
            var g = _ghosts[i];
            foreach (var h in g.Holders)
            {
                if (h.Ghost != g) continue;
                h.Ghost = null; h.GhostPrio = Src.None;
                if (h.State == State.Investigate || h.State == State.Search) { h.State = State.Idle; h.SeenFor = 0f; h.Heard.RemoveWhere(x => x < 0); }
            }
            g.Holders.Clear();
            if (g.Obj != null) UnityEngine.Object.Destroy(g.Obj);
            _ghosts.RemoveAt(i);
        }

        // ---------- sounds ----------
        // one ghost per event, offered to every NPC within the radius (walls halve the radius with [Senses] MuffleSounds)
        private static void Noise(GameObject source, Vector3 pos, float radius, Src src, string about, Func<Agent, bool> filter, Func<Agent, Vector3> at, GameObject subject = null, Action<Agent> told1 = null)
        {
            if (radius <= 0f || !On) return;
            float now = Time.time;
            Transform sroot = source != null ? source.transform.root : null;
            // An event about the player (the player's shots, shouts, engine, thrown items; an NPC shooting or shouting at the player) means
            // nothing to an NPC whose faction is at peace with the player (Coyotes towns): it only alerts NPCs hostile to the player. Hits
            // still count (being shot makes the game turn the faction hostile anyway) and so do fights between NPCs.
            bool aboutPlayer = src != Src.Hit && PlayerRelated(source, subject);
            Ghost shared = at == null ? GetOrMake(src, pos, source, about, 0f, 1f, now) : null;
            if (shared != null) shared.Subject = subject;
            int told = 0;
            float r2 = radius * radius;
            // a sandstorm swallows gunfire, explosions, shouts and engines: the range shrinks once (never twice) when the sound starts in a
            // storm or the listener stands in one. No storm loaded: nothing is tested.
            bool stormy = (src == Src.Gunshot || src == Src.Shout || src == Src.Engine) && Storm.Any;
            bool srcStorm = stormy && Storm.In(pos);
            float hf = Storm.HearingFactor, rs2 = r2 * hf * hf;
            foreach (var kv in _agents)
            {
                var a = kv.Value;
                if (a.Owner == null || a.T.parent != null || (sroot != null && a.T == sroot)) continue;
                float d2 = (a.T.position - pos).sqrMagnitude;
                if (d2 > r2) continue;
                if (stormy && (srcStorm || a.InStorm) && d2 > rs2) continue;
                if (filter != null && !filter(a)) continue;
                if (aboutPlayer && !a.Hostile.Contains("Player")) continue;
                if (a.Blown) continue;                                      // the tornado's roar: hears nothing
                if (Plugin.MuffleSounds.Value && !Clear(pos + Vector3.up, Eye(a), a.T))
                {
                    float m = radius * Mathf.Clamp01(Plugin.MuffleFactor.Value / 100f);
                    if (d2 > m * m) continue;
                }
                Ghost g = shared;
                if (g == null) { g = GetOrMake(src, at(a), source, about, 0f, 1f, now); g.Subject = subject; }
                if (Assign(a, g, src, now)) { told++; if (told1 != null) told1(a); }
            }
            if (shared != null && shared.Holders.Count == 0) { int i = _ghosts.IndexOf(shared); if (i >= 0) KillGhost(i); }
            if (Plugin.SensesLog.Value && told > 0) Plugin.Log.LogInfo("Senses: " + src + " (" + about + ") within " + radius.ToString("0") + " m alerts " + told + (shared != null ? " -> ghost #" + shared.Id : ""));
        }

        private static bool IsPlayer(GameObject go)
        {
            if (go == null || _player == null) return false;
            return go == _player || go.transform.IsChildOf(_player.transform);
        }

        // the event is about the player: the player made it, it is about the player, or an NPC made it while fighting / hunting the player
        private static bool PlayerRelated(GameObject source, GameObject subject)
        {
            FindPlayer();
            if (_player == null) return false;
            if (IsPlayer(subject) || IsPlayer(source)) return true;
            if (_playerCar != null && source != null && source.transform.root == _playerCar.transform.root) return true;
            if (source == null) return false;
            var sa = Get(source.transform.root.gameObject);
            if (sa != null)
            {
                if (sa.State == State.Combat && IsPlayer(sa.Target)) return true;
                if (sa.Ghost != null && IsPlayer(sa.Ghost.Subject)) return true;
                return false;
            }
            // not one of ours (a raider seated in an Apocapatrol car): its own Detection target
            var t = Aim.TargetOf(source.transform.root.gameObject);
            return IsPlayer(t);
        }

        // Tracers: a gun fired (the player's or an NPC's), once per shot (pellets of one blast merge through the 1 s source window)
        internal static void Shot(GameObject shooter, Vector3 pos, WeaponRanges.Kind kind, bool player)
        {
            if (!On) return;
            float radius = player ? PlayerShotRange(kind) : NpcShotRange(shooter, kind);
            string about = Plugin.SensesLog.Value || Plugin.ShowGhosts.Value ? (player ? "player" : Name(shooter)) + " " + kind.ToString().ToLowerInvariant() : "gunshot";
            Noise(shooter, pos, radius, Src.Gunshot, about, null, null, shooter);
        }

        private static float PlayerShotRange(WeaponRanges.Kind k)
        {
            switch (k)
            {
                case WeaponRanges.Kind.Pistol: return Plugin.ShotRangePistol.Value;
                case WeaponRanges.Kind.Smg: return Plugin.ShotRangeSmg.Value;
                case WeaponRanges.Kind.Sniper: return Plugin.ShotRangeSniper.Value;
                case WeaponRanges.Kind.Shotgun: return Plugin.ShotRangeShotgun.Value;
                case WeaponRanges.Kind.Crossbow: return Plugin.ShotRangeCrossbow.Value;
                default: return Plugin.ShotRangeRifle.Value;
            }
        }

        private static Dictionary<string, float> _npcShot; private static string _npcShotSrc;
        private static float NpcShotRange(GameObject shooter, WeaponRanges.Kind k)
        {
            string cfg = Plugin.NpcShotRanges.Value ?? "";
            if (_npcShot == null || _npcShotSrc != cfg)
            {
                _npcShotSrc = cfg; _npcShot = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOfAny(new[] { '=', ':' });
                    float v;
                    if (eq > 0 && float.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) _npcShot[part.Substring(0, eq).Trim()] = v;
                }
            }
            float r;
            if (shooter != null && _npcShot.TryGetValue(PrefabOf(shooter), out r)) return r;
            return PlayerShotRange(k);
        }

        // ---------- explosions ----------
        // Grenades and blast lance 1 (Explosion_Grenade), blast lance 2 (Explosion_Can), Blast Rat and Blast Zombie deaths (Explosion_BlastRat /
        // _BlastZombie): each item/creature FSM spawns the blast prefab with a CreateObject action (explode state, Health FSM). Harmony postfix on
        // CreateObject.OnEnter: a spawned prefab named in [Senses] BlastPrefabs is a GUNSHOT-ranked noise within BlastRange at the blast.
        // Car explosions are not CreateObject spawns (Apocapatrol instantiates its blast itself) - they come only through AfterBlast.
        private static HashSet<string> _blastNames; private static string _blastSrc;
        public static void AfterCreateObject(CreateObject __instance)
        {
            try
            {
                if (__instance.gameObject == null) return;
                var prefab = __instance.gameObject.Value;
                if (prefab == null) return;
                string pname = PrefabOf(prefab);        // prefab assets are few: cached, so this hook costs no string per spawn
                var made = __instance.storeObject != null ? __instance.storeObject.Value : null;
                if (made != null && Nav.On && pname.Length > 5 && (pname[0] == 'C' || pname[0] == 'B')) Nav.Spawned(made);   // Camp_N / Cave_N / Building_N
                if (!On || Plugin.BlastRange.Value <= 0f) return;
                string cfg = Plugin.BlastPrefabs.Value ?? "";
                if (_blastNames == null || _blastSrc != cfg)
                {
                    _blastSrc = cfg; _blastNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var part in cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) _blastNames.Add(part.Trim());
                }
                if (!_blastNames.Contains(pname)) return;
                Vector3 pos;
                if (made != null && Prefab(made.name) == pname) pos = made.transform.position;
                else
                {
                    var sp = __instance.spawnPoint != null ? __instance.spawnPoint.Value : null;
                    pos = sp != null ? sp.transform.position : (__instance.Fsm != null && __instance.Fsm.GameObject != null ? __instance.Fsm.GameObject.transform.position : Vector3.zero);
                    if (__instance.position != null && !__instance.position.IsNone) pos += __instance.position.Value;
                }
                var owner = __instance.Fsm != null ? __instance.Fsm.GameObject : null;
                Noise(owner, pos, Plugin.BlastRange.Value, Src.Gunshot, (owner != null ? Prefab(owner.name) : "something") + " exploding", null, null);
            }
            catch (Exception e) { Plugin.Log.LogError("Senses: " + e); }
        }

        // the human Sound FSM's combat shout (Harmony prefix on AudioPlay.OnEnter, FSM "Sound", state "attack")
        public static bool BeforeAudioPlay(AudioPlay __instance)
        {
            try
            {
                if (!On) return true;
                var fsm = __instance.Fsm;
                if (fsm == null) return true;
                if (fsm.Name == "INPUT_Horn") { _hornFsm = fsm; _hornAt = Time.time; return true; }    // the player's car horn starts (Engine() picks it up)
                if (fsm.Name != "Sound" || fsm.ActiveStateName != "attack") return true;
                var a = Get(fsm.GameObject);
                if (a == null || !a.Human || Plugin.TauntRange.Value <= 0f) return true;
                Taunt(a);
            }
            catch (Exception e) { Plugin.Log.LogError("Senses: " + e); }
            return true;
        }

        private static void Taunt(Agent t)
        {
            float now = Time.time, range = Plugin.TauntRange.Value;
            // friends: where the shouter's target is (or the ghost the shouter is going to)
            Vector3 where; bool known = false; Ghost tg = null;
            if (t.State == State.Combat && t.Target != null) { where = t.Target.transform.position; known = true; }
            else if (t.Ghost != null && t.State == State.Investigate && !t.Ghost.Retired) { where = t.Ghost.Pos; tg = t.Ghost; known = true; }   // a searcher found nothing: nothing to pass on
            else where = t.T.position;
            if (known)
            {
                string tag = t.Tag;
                Ghost shared = tg;
                // a shout is not a ghost of its own: it passes on what the shouter knows - what it sees (a VISION ghost at the target) or the
                // very ghost it is going to, at that ghost's rank (a gunshot it is checking stays a GUNSHOT)
                if (shared == null) { shared = GetOrMake(Src.Vision, where, t.Target, Name(t.Owner) + " saw " + Name(t.Target), 4f, 15f, now); shared.Subject = t.Target; }   // one ghost per sighting: shouts (every few s) refresh it instead of minting new ones
                Src relay = tg == null ? Src.Vision : t.GhostPrio;
                int told = 0;
                foreach (var kv in _agents)
                {
                    var f = kv.Value;
                    if (f == t || f.Owner == null || !f.Human || f.Tag != tag || f.T.parent != null) continue;
                    if ((f.T.position - t.T.position).sqrMagnitude > range * range) continue;
                    // a ghost acquired once (from a shout or any other way) is never re-sent by a shout: it would wake a searching NPC
                    // back to investigating the same spot again and again while its friends keep shouting
                    if (f.Ghost == shared || f.Heard.Contains(shared.Id)) continue;
                    if (f.Ghost != null && f.Ghost.Subject != null && f.Ghost.Subject == shared.Subject && (f.Ghost.Pos - shared.Pos).sqrMagnitude < 16f) continue;   // already knows the same thing
                    if (Assign(f, shared, relay, now)) { told++; f.Heard.Add(shared.Id); }
                }
                if (shared != tg && shared.Holders.Count == 0) { int i = _ghosts.IndexOf(shared); if (i >= 0) KillGhost(i); }
                if (Plugin.SensesLog.Value && told > 0) Plugin.Log.LogInfo("Senses: " + Name(t.Owner) + "'s shout sends " + told + " " + tag + " to " + (tg != null ? "ghost #" + tg.Id : "its target"));
            }
            // enemies: the shouter gave itself away
            string ttag = t.Tag;
            int key = -t.Owner.GetInstanceID();
            Noise(t.Owner, t.T.position, range, Src.Shout, Name(t.Owner) + " shouting", a => a.Hostile.Contains(ttag) && !a.Heard.Contains(key), null, t.Owner,
                a => a.Heard.Add(key));
        }

        // ---------- the player's shout ([Senses] ShoutKey) ----------
        // A human raider's voice (the enemy_human_single clips of a Scraffa/Flexa's Sound FSM) from the player, and a SHOUT ghost at the player
        // for every NPC hostile to the player within PlayerShoutRange. Only in play (not paused, not in a menu that stops time), once per
        // ShoutCooldown.
        private static float _nextShout;
        private static AudioClip[] _voice;
        private static void ShoutKey()
        {
            var key = Plugin.ShoutKey.Value;
            if (key == UnityEngine.InputSystem.Key.None) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || !kb[key].wasPressedThisFrame || !ModifierHeld()) return;
            if (Time.timeScale <= 0f || Time.unscaledTime < _nextShout) return;
            FindPlayer();
            if (_player == null) return;
            _nextShout = Time.unscaledTime + Mathf.Max(0f, Plugin.ShoutCooldown.Value);
            var clip = Voice();
            if (clip != null)
            {
                // your own voice: a 2D source on the camera, so it stays with you (PlayClipAtPoint left it hanging in the air where you shouted)
                var src = VoiceSource();
                if (src != null) src.PlayOneShot(clip, Mathf.Clamp01(Plugin.ShoutVolume.Value));
            }
            Noise(_player, _player.transform.position, Plugin.PlayerShoutRange.Value, Src.Shout, "the player shouting", a => a.Hostile.Contains("Player"), null, _player);
            if (Plugin.SensesLog.Value) Plugin.Log.LogInfo("Senses: the player shouts (" + Plugin.PlayerShoutRange.Value.ToString("0") + " m)" + (clip == null ? ", no voice clip found" : ""));
        }

        private static AudioSource _voiceSrc;
        private static AudioSource VoiceSource()
        {
            var cam = Camera.main;
            if (cam == null) return null;
            if (_voiceSrc != null && _voiceSrc.transform.parent == cam.transform) return _voiceSrc;
            var go = new GameObject("NPCAI.Voice");
            go.transform.SetParent(cam.transform, false);
            _voiceSrc = go.AddComponent<AudioSource>();
            _voiceSrc.spatialBlend = 0f; _voiceSrc.playOnAwake = false; _voiceSrc.loop = false;
            return _voiceSrc;
        }

        // ShoutModifier held (an Alt key stands for either Alt)
        private static bool ModifierHeld()
        {
            var m = Plugin.ShoutModifier.Value;
            if (m == UnityEngine.InputSystem.Key.None) return true;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return false;
            if (m == UnityEngine.InputSystem.Key.LeftAlt || m == UnityEngine.InputSystem.Key.RightAlt) return kb.altKey.isPressed;
            if (m == UnityEngine.InputSystem.Key.LeftCtrl || m == UnityEngine.InputSystem.Key.RightCtrl) return kb.ctrlKey.isPressed;
            if (m == UnityEngine.InputSystem.Key.LeftShift || m == UnityEngine.InputSystem.Key.RightShift) return kb.shiftKey.isPressed;
            return kb[m].isPressed;
        }

        // the game's own buttons on the shout key do nothing while the modifier is held (Alt+Q must not kick)
        private static HashSet<string> _blocked; private static string _blockedSrc;
        private static bool Blocked(FsmString name)
        {
            if (name == null || Plugin.ShoutModifier.Value == UnityEngine.InputSystem.Key.None || Plugin.ShoutKey.Value == UnityEngine.InputSystem.Key.None) return false;
            string cfg = Plugin.ShoutBlocksButtons.Value ?? "";
            if (_blocked == null || _blockedSrc != cfg)
            {
                _blockedSrc = cfg; _blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in cfg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) _blocked.Add(part.Trim());
            }
            return _blocked.Contains(name.Value ?? "") && ModifierHeld();
        }
        public static bool BeforeGetButton(GetButton __instance)
        {
            try { if (Blocked(__instance.buttonName)) { if (__instance.storeResult != null) __instance.storeResult.Value = false; return false; } }
            catch (Exception) { }
            return true;
        }
        public static bool BeforeGetButtonDown(GetButtonDown __instance)
        {
            try { if (Blocked(__instance.buttonName)) { if (__instance.storeResult != null) __instance.storeResult.Value = false; return false; } }
            catch (Exception) { }
            return true;
        }
        public static bool BeforeGetButtonUp(GetButtonUp __instance)
        {
            try { if (Blocked(__instance.buttonName)) { if (__instance.storeResult != null) __instance.storeResult.Value = false; return false; } }
            catch (Exception) { }
            return true;
        }

        private static AudioClip Voice()
        {
            if (_voice == null || _voice.Length == 0 || _voice[0] == null)
            {
                // Flexa's own shouts, read from the Flexa prefab asset (Gungirl's voice swap only ever edits spawned instances, so the
                // prefab always carries the game's original clips); else from a live vanilla Flexa. Gungirls are skipped, and nothing is
                // collected by clip name: the mod's Gungirl voice files carry the same names (enemy_human_single_N).
                _voice = null;
                GameObject prefab = null;
                if (Time.unscaledTime >= _nextFlexaScan)
                {
                    _nextFlexaScan = Time.unscaledTime + 30f;       // Gungirl keeps the Flexa prefab once found; looked up at most every 30 s until then
                    prefab = FlexaPrefab();
                }
                if (prefab != null) _voice = FlexaClips(prefab);
                if (_voice == null)
                    foreach (var kv in _agents)
                    {
                        var o = kv.Value.Owner;
                        if (o == null || !(o.name == "Flexa" || o.name.StartsWith("Flexa(", StringComparison.Ordinal))) continue;
                        _voice = FlexaClips(o);
                        if (_voice != null) break;
                    }
                if (_voice == null) return null;      // no Flexa anywhere yet: a silent shout, tried again next time
            }
            return _voice[UnityEngine.Random.Range(0, _voice.Length)];
        }

        private static GameObject _flexaPrefab;
        private static GameObject FlexaPrefab()
        {
            if (_flexaPrefab != null) return _flexaPrefab;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go != null && go.name == "Flexa" && !go.scene.IsValid() && go.transform.parent == null && go.GetComponent<Rigidbody>() != null)
                { _flexaPrefab = go; break; }
            return _flexaPrefab;
        }

        private static float _nextFlexaScan;
        private static AudioClip[] FlexaClips(GameObject flexa)
        {
            foreach (var f in flexa.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.FsmName != "Sound") continue;
                var arr = f.FsmVariables.FindFsmArray("soundArray");
                if (arr == null || arr.Values == null) continue;
                var list = new List<AudioClip>();
                foreach (var v in arr.Values) { var c = v as AudioClip; if (c != null) list.Add(c); }
                if (list.Count > 0) return list.ToArray();
            }
            return null;
        }

        // Tracers: a bullet hit a creature - it knows where that came from
        internal static void Hurt(GameObject victim, GameObject attacker)
        {
            if (!On || victim == null || attacker == null) return;
            var a = Get(victim.transform.root.gameObject);
            if (a == null || a.State == State.Combat || a.Blown) return;
            float now = Time.time;
            var g = GetOrMake(Src.Hit, attacker.transform.root.position, attacker.transform.root.gameObject, "hit by " + Name(attacker), 0f, 1f, now);
            g.Subject = attacker.transform.root.gameObject;
            bool had = a.Ghost == g;
            if (Assign(a, g, Src.Hit, now)) { if (!had) Log(a, "is hit by " + Name(attacker) + ", goes for ghost #" + g.Id); }   // once per shot, not per pellet
            else if (g.Holders.Count == 0) { int i = _ghosts.IndexOf(g); if (i >= 0) KillGhost(i); }
        }

        // the player's car: a running engine is heard EngineMinRange..EngineMaxRange m by horsepower, half of it idling, nothing switched off
        private static Dictionary<int, float> _hpOf = new Dictionary<int, float>();
        // The car horn (the car's DriveTrigger/INPUT [INPUT_Horn] FSM: Horn button down -> loop the horn clip, up -> stop): heard like the
        // engine (same rank, peaceful factions, sandstorm) by everyone within the engine's longest range + 50 m (every horn the same), every
        // engine tick while it sounds (a short tap counts too: the AudioPlay hook stamps the start).
        private static Fsm _hornFsm; private static float _hornAt = -10f;

        private static void Engine(float now)
        {
            var car = PlayerCar();
            if (car == null) return;
            bool engineOn = Apocapatrol_EngineRunning(car);
            bool hornOn = _hornFsm != null && _hornFsm.GameObject != null && _hornFsm.GameObject.transform.root == car.transform.root
                          && (now - _hornAt < 0.6f || _hornFsm.ActiveStateName == "on");
            if (!engineOn && !hornOn) return;
            Vector3 cpos = car.transform.position;
            float er = 0f;
            if (engineOn)
            {
                string about;
                er = EngineRadius(car, out about);
                Noise(car, cpos, er, Src.Engine, about, null, null, _player);
                ReleaseEngineHolders(car, cpos, er, now);
            }
            if (hornOn)
            {
                // its own ghost (source = the horn object): it follows the car while the horn sounds and then stays where it last sounded,
                // so NPCs it reached beyond the engine's range walk there. NPCs within the engine's range keep the engine ghost.
                float er2 = er * er;
                Noise(_hornFsm.GameObject, cpos, Plugin.EngineMaxRange.Value + 50f, Src.Engine, "horn", engineOn ? (Func<Agent, bool>)(a => (a.T.position - cpos).sqrMagnitude > er2) : null, null, _player);
            }
        }

        private static float EngineRadius(GameObject car, out string about)
        {
            float hp;
            if (!_hpOf.TryGetValue(car.GetInstanceID(), out hp))
            {
                hp = 0f;
                foreach (var t in car.GetComponentsInChildren<Transform>(true))
                {
                    int i = t.name.IndexOf("HP", StringComparison.OrdinalIgnoreCase);
                    if (i <= 0) continue;
                    int j = i - 1; while (j >= 0 && (char.IsDigit(t.name[j]) || t.name[j] == '.')) j--;
                    float v;
                    if (float.TryParse(t.name.Substring(j + 1, i - j - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0f) { hp = v; break; }
                }
                _hpOf[car.GetInstanceID()] = hp;
                Plugin.Verbose("Senses: " + car.name + " engine " + (hp > 0f ? hp + " HP" : "unknown"));
            }
            float lo = Plugin.EngineMinHp.Value, hi = Mathf.Max(lo + 1f, Plugin.EngineMaxHp.Value);
            float k = hp > 0f ? Mathf.Clamp01((hp - lo) / (hi - lo)) : 0.5f;
            float radius = Mathf.Lerp(Plugin.EngineMinRange.Value, Plugin.EngineMaxRange.Value, k);
            var rb = car.GetComponent<Rigidbody>();
            float speed = rb != null ? rb.velocity.magnitude : 0f;
            float thr = Nwh.Throttle(car);
            if (speed < 1.5f && thr < 0.05f) radius *= Mathf.Clamp01(Plugin.EngineIdleFactor.Value / 100f);
            about = "engine " + (hp > 0f ? hp + " HP" : "");
            return radius;
        }

        private static void ReleaseEngineHolders(GameObject car, Vector3 cpos, float radius, float now)
        {
            // the engine ghost is one shared ghost that moves with the car and is refreshed every tick: an NPC that drove out of earshot
            // would follow it for ever - let go of it where the NPC stands
            foreach (var g in _ghosts)
            {
                if (g.Src != Src.Engine || g.Source != car || g.Retired) continue;
                _scratch.Clear();
                foreach (var h in g.Holders) if (h.Owner == null || (h.T.position - cpos).sqrMagnitude > radius * radius * 1.3f) _scratch.Add(h);
                foreach (var h in _scratch)
                {
                    if (h.Owner == null) { g.Holders.Remove(h); continue; }
                    Log(h, "lost the engine sound, searches here");
                    h.Ghost = null; h.GhostPrio = Src.None; g.Holders.Remove(h);
                    h.State = State.Search; h.SearchUntil = now + Mathf.Max(0f, Plugin.SearchSeconds.Value);
                }
                if (g.Holders.Count == 0) { int i = _ghosts.IndexOf(g); if (i >= 0) KillGhost(i); }
                break;
            }
        }

        private static bool Apocapatrol_EngineRunning(GameObject car) { return Nwh.EngineRunning(car); }

        // the player throws a held item (GrabItem FSM state Throw): the landing spot attracts NPCs within ThrowRange
        private static void Thrown(float now, float dt)
        {
            if (_grabFsm == null)
            {
                var cam = Camera.main;
                if (cam != null) foreach (var f in cam.GetComponents<PlayMakerFSM>()) if (f.FsmName == "GrabItem") { _grabFsm = f; _grabItemVar = f.FsmVariables.GetFsmGameObject("Item"); break; }
                if (_grabFsm == null) return;
            }
            if (_grabFsm.Fsm == null || !_grabFsm.Fsm.Initialized) return;
            string st = _grabFsm.ActiveStateName;
            if (st != _grabState)
            {
                _grabState = st;
                if (st == "Throw" && _grabItemVar != null && _grabItemVar.Value != null)
                {
                    _thrown = _grabItemVar.Value.GetComponent<Rigidbody>();
                    _thrownSince = now; _thrownRest = 0f;
                    if (Plugin.SensesLog.Value) Plugin.Log.LogInfo("Senses: " + Name(_grabItemVar.Value) + " thrown");
                }
            }
            if (_thrown == null) return;
            if (_thrown.velocity.magnitude < 0.5f) _thrownRest += dt; else _thrownRest = 0f;
            if (_thrownRest > 0.3f || now - _thrownSince > 6f)
            {
                Noise(_thrown.gameObject, _thrown.position, Plugin.ThrowRange.Value, Src.Thrown, "thrown " + Prefab(_thrown.name), null, null, _player);
                _thrown = null;
            }
        }

        // ---------- the Detection FSM ----------
        // Harmony prefix on SensorGetDetections.DoAction: for our NPCs the result is what the NPC knows (its seen target, or its ghost),
        // written to the action's own variable and announced with the state's own events.
        public static bool BeforeGetDetections(SensorGetDetections __instance)
        {
            try
            {
                if (!On) return true;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Detection") return true;
                var a = Of(fsm.GameObject);
                if (a == null) return true;
                GameObject want = Known(a);
                if (__instance.storeDetected != null && !__instance.storeDetected.IsNone) __instance.storeDetected.Value = want;
                if (__instance.storeDetectionCount != null && !__instance.storeDetectionCount.IsNone) __instance.storeDetectionCount.Value = want != null ? 1 : 0;
                string st = fsm.ActiveStateName;
                if (st == "searchRange") { if (__instance.noneDetectedEvent != null) fsm.Event(__instance.noneDetectedEvent); }   // straight on to checkLOS, no 30 s hunt through walls
                else if (want != null) { if (__instance.detectedEvent != null) fsm.Event(__instance.detectedEvent); }
                else if (__instance.noneDetectedEvent != null) fsm.Event(__instance.noneDetectedEvent);
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError("Senses: " + e); return true; }
        }

        // the burst's line-of-sight check (Attack FSM "ranged" state): a target the NPC sees right now, never a ghost
        public static bool BeforeLosResult(SensorGetLineOfSightResult __instance)
        {
            try
            {
                if (!On) return true;
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack") return true;
                var a = Get(fsm.GameObject);
                if (a == null) return true;
                bool vis = a.State == State.Combat && a.Target != null && a.UnseenFor <= Mathf.Max(0f, Plugin.LoseSeconds.Value);
                if (__instance.storeVisibility != null && !__instance.storeVisibility.IsNone) __instance.storeVisibility.Value = vis ? 1f : 0f;
                if (__instance.storeIsVisible != null && !__instance.storeIsVisible.IsNone) __instance.storeIsVisible.Value = vis;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError("Senses: " + e); return true; }
        }

        private static GameObject Known(Agent a)
        {
            switch (a.State)
            {
                case State.Combat: if (a.Target == null) { a.State = State.Idle; return null; } return a.Target;
                case State.Investigate:
                case State.Search: return a.Ghost != null ? a.Ghost.Obj : null;
                default: return null;
            }
        }

        // for the brain and Aim
        internal static int KindOf(GameObject owner)
        {
            if (!On) return 0;
            var a = Get(owner);
            if (a == null) return 0;
            switch (a.State) { case State.Combat: return 1; case State.Investigate: return 2; case State.Search: return 3; default: return 0; }
        }
        internal static bool IsGhostTarget(GameObject owner) { int k = KindOf(owner); return k == 2 || k == 3; }
        internal static void ArrivedAt(GameObject owner) { var a = Get(owner); if (a != null && a.State == State.Investigate) Arrived(a, Time.time); }

        // ---------- Apocapatrol (only when that mod is loaded) ----------
        // A crew that bails out is a fresh NPC spawned beside the car (Patrol.BailOut instantiates the prefab and deletes the seated one), so
        // it would start with no idea where the player is. A Harmony postfix on Patrol.BailOut hands the new NPC the crew's knowledge: the
        // seated NPC's own target, or the player when within BailOutAwareRange of the car (the crews hunt the player). When the new NPC
        // registers it fights at once if it sees that target, otherwise it gets a sight-ranked ghost at the target's position and searches.
        // A postfix on Explode.Blast makes a car explosion a gunshot-ranked noise (ExplosionRange) at the wreck.
        private static bool _patrolChecked;
        private static readonly Dictionary<int, KeyValuePair<GameObject, float>> _handover = new Dictionary<int, KeyValuePair<GameObject, float>>();

        private static void HookApocapatrol()
        {
            _patrolChecked = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.denis.apocalypter.apocapatrol")) { Plugin.Verbose("Senses: Apocapatrol not loaded, no crew rules"); return; }
                var patrol = HarmonyLib.AccessTools.TypeByName("Apocapatrol.Patrol");
                var explode = HarmonyLib.AccessTools.TypeByName("Apocapatrol.Explode");
                var h = new HarmonyLib.Harmony(Plugin.GUID + ".senses.patrol");
                int n = 0;
                var bail = patrol != null ? HarmonyLib.AccessTools.Method(patrol, "BailOut", new[] { typeof(GameObject), typeof(GameObject), typeof(string), typeof(float) }) : null;
                if (bail != null) { h.Patch(bail, postfix: new HarmonyLib.HarmonyMethod(typeof(Senses), nameof(AfterBailOut))); n++; }
                var blast = explode != null ? HarmonyLib.AccessTools.Method(explode, "Blast", new[] { typeof(GameObject) }) : null;
                if (blast != null) { h.Patch(blast, postfix: new HarmonyLib.HarmonyMethod(typeof(Senses), nameof(AfterBlast))); n++; }
                Plugin.Log.LogInfo("Senses: Apocapatrol found, " + n + "/2 crew rules hooked" + (bail == null ? " (no Patrol.BailOut)" : "") + (blast == null ? " (no Explode.Blast)" : ""));
            }
            catch (Exception e) { Plugin.Log.LogError("Senses: Apocapatrol hooks failed: " + e); }
        }

        public static void AfterBailOut(GameObject car, GameObject pax, GameObject __result)
        {
            try
            {
                if (!On || !Plugin.BailOutAware.Value || __result == null) return;
                GameObject target = null;
                if (pax != null)
                    foreach (var f in pax.GetComponents<PlayMakerFSM>())
                        if (f != null && f.FsmName == "Detection" && f.Fsm != null && f.Fsm.Initialized)
                        {
                            var v = f.FsmVariables.FindFsmGameObject("detectedObj");
                            if (v != null && v.Value != null && v.Value.name.IndexOf("NPCAI.Ghost", StringComparison.Ordinal) < 0) target = v.Value.transform.root.gameObject;
                            break;
                        }
                if (target == null || (_playerCar != null && target == _playerCar)) { FindPlayer(); target = _player; }
                if (target == null) return;
                Vector3 from = car != null ? car.transform.position : __result.transform.position;
                if (target == _player && (target.transform.position - from).magnitude > Plugin.BailOutAwareRange.Value) return;
                Inform(__result, target);
            }
            catch (Exception e) { Plugin.Log.LogError("Senses: bail-out handover: " + e); }
        }

        public static void AfterBlast(GameObject car)
        {
            try
            {
                if (!On || car == null || Plugin.ExplosionRange.Value <= 0f) return;
                Noise(car, car.transform.position, Plugin.ExplosionRange.Value, Src.Gunshot, Prefab(car.name) + " exploding", null, null);
            }
            catch (Exception e) { Plugin.Log.LogError("Senses: explosion noise: " + e); }
        }

        // tell an NPC (now, or the moment it registers) that it knows about target
        internal static void Inform(GameObject npc, GameObject target)
        {
            if (npc == null || target == null) return;
            var a = Get(npc);
            _handover[npc.GetInstanceID()] = new KeyValuePair<GameObject, float>(target, Time.time);
            if (a != null) Handover(a);
        }

        private static void Handover(Agent a)
        {
            if (_handover.Count == 0) return;
            KeyValuePair<GameObject, float> h;
            int id = a.Owner.GetInstanceID();
            if (!_handover.TryGetValue(id, out h)) return;
            _handover.Remove(id);
            if (h.Key == null || Time.time - h.Value > 30f) return;
            if (Visible(a, Eye(a), h.Key, h.Key == _player, InStormOf(h.Key))) { Engage(a, h.Key); Log(a, "bailed out and sees " + Name(h.Key)); return; }
            float now = Time.time;
            a.LastSeen = h.Key.transform.position;
            var g = GetOrMake(Src.Vision, a.LastSeen, h.Key, "crew knew where " + Name(h.Key) + " was", 6f, 2f, now);
            g.Subject = h.Key;
            if (Assign(a, g, Src.Vision, now)) Log(a, "bailed out, goes where its crew last had " + Name(h.Key) + " (ghost #" + g.Id + ")");
        }

        // ---------- registry ----------
        private static Agent Get(GameObject owner)
        {
            Agent a;
            return owner != null && _agents.TryGetValue(owner.GetInstanceID(), out a) ? a : null;
        }

        private static Agent Of(GameObject owner)
        {
            if (owner == null) return null;
            int id = owner.GetInstanceID();
            Agent a;
            if (_agents.TryGetValue(id, out a)) return a;
            if (_ignored.Contains(id)) return null;
            a = Make(owner);
            if (a == null) { _ignored.Add(id); return null; }
            _agents[id] = a;
            Handover(a);
            return a;
        }

        private static Agent Make(GameObject owner)
        {
            if (owner.transform.parent != null) return null;
            PlayMakerFSM det = null, att = null;
            foreach (var f in owner.GetComponents<PlayMakerFSM>())
            {
                if (f == null) continue;
                if (f.FsmName == "Detection") det = f; else if (f.FsmName == "Attack") att = f;
            }
            if (det == null || att == null || det.Fsm == null || !det.Fsm.Initialized) return null;
            var a = new Agent { Owner = owner, T = owner.transform, Col = owner.GetComponent<Collider>(), Detection = det, Attack = att };
            a.DetectedVar = det.FsmVariables.FindFsmGameObject("detectedObj");
            a.Tag = owner.tag;
            a.BaseHostile = HostileTags(owner);
            a.Hostile = new HashSet<string>(a.BaseHostile);
            foreach (var f in owner.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "PlayerIsEnemy") { a.PlayerIsEnemy = f; break; }
            Relation(a, false);
            a.Human = IsHuman(a.Tag);
            foreach (var t in owner.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name; int i = n.LastIndexOf(':');
                if ((i >= 0 ? n.Substring(i + 1) : n) == "Head") { a.Head = t; break; }
            }
            var sensors = new List<Behaviour>();
            foreach (var b in owner.GetComponentsInChildren<Behaviour>(true))
            {
                if (b == null) continue;
                string tn = b.GetType().Name;
                if (tn == "LOSSensor" || tn == "RangeSensor") sensors.Add(b);
            }
            a.Sensors = sensors.ToArray();
            Sensors(a, false);
            a.Stagger = (_created++ % 10) * 0.012f;
            a.NextLook = Time.time + a.Stagger;
            if (Plugin.SensesLog.Value) Plugin.Log.LogInfo("Senses: " + owner.name + " (" + a.Tag + (a.Human ? ", human" : "") + ") hunts " + string.Join("/", new List<string>(a.Hostile).ToArray()) + (a.Head != null ? ", eyes at the head" : ""));
            return a;
        }

        // The game turns a faction against the player at run time: hitting a Coyote sends HitFriendly -> CheckFriendly -> FactionCoyotesEnemy,
        // and every Coyote's PlayerIsEnemy FSM goes to state PlayerEnemy (and back to PlayerFriendly when the game makes peace). Our copy of the
        // NPC's enemies follows that state (checked every second and when the NPC registers); the game's own sensors are off, so their
        // tag lists can't be relied on.
        private static void Relation(Agent a, bool log)
        {
            bool hostile;
            var f = a.PlayerIsEnemy;
            if (f != null && f.Fsm != null && f.Fsm.Initialized) hostile = f.ActiveStateName == "PlayerEnemy";
            else hostile = a.BaseHostile.Contains("Player");
            if (hostile == a.Hostile.Contains("Player")) return;
            if (hostile) a.Hostile.Add("Player"); else a.Hostile.Remove("Player");
            if (log) Log(a, hostile ? "turns hostile to the player" : "makes peace with the player");
        }

        private static void Sensors(Agent a, bool on)
        {
            if (a.Sensors == null) return;
            foreach (var b in a.Sensors) if (b != null) b.enabled = on;
            a.SensorsOff = !on;
        }

        private static bool IsHuman(string tag)
        {
            foreach (var p in (Plugin.HumanFactions.Value ?? "").Split(',')) if (p.Trim() == tag) return true;
            return false;
        }

        // the faction's enemies: the RangeSensor's tag filter (SignalFilter.AllowedTags), as the game has it per prefab
        private static HashSet<string> HostileTags(GameObject owner)
        {
            var set = new HashSet<string>();
            try
            {
                foreach (var b in owner.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null || b.GetType().Name != "RangeSensor") continue;
                    object filter = null;
                    var f = Field(b.GetType(), "SignalFilter") ?? Field(b.GetType(), "signalFilter");
                    if (f != null) filter = f.GetValue(b);
                    if (filter == null) continue;
                    var tagsF = Field(filter.GetType(), "AllowedTags");
                    var tags = tagsF != null ? tagsF.GetValue(filter) as string[] : null;
                    if (tags != null) foreach (var t in tags) if (!string.IsNullOrEmpty(t)) set.Add(t);
                    break;
                }
            }
            catch (Exception e) { Plugin.Verbose("Senses: tag filter of " + owner.name + ": " + e.Message); }
            if (set.Count == 0)
            {
                // the game's own table, for a prefab without a readable filter
                switch (owner.tag)
                {
                    case "Coyotes": set.Add("Scrapyard"); set.Add("Mutant"); set.Add("Carnivore"); break;
                    case "Carnivore": set.Add("Player"); set.Add("Mutant"); set.Add("Scrapyard"); set.Add("Coyotes"); break;
                    case "Mutant": set.Add("Player"); set.Add("Scrapyard"); set.Add("Carnivore"); set.Add("Coyotes"); set.Add("Herbivore"); break;
                    default: set.Add("Player"); set.Add("Mutant"); set.Add("Carnivore"); set.Add("Coyotes"); break;
                }
            }
            return set;
        }

        private static void FindPlayer()
        {
            if (_player == null && Time.time >= _nextPlayerFind)
            {
                _nextPlayerFind = Time.time + 2f;
                _player = GameObject.Find("Player");
                _playerHead = null; _inCarFsm = null;
                if (_player != null)
                    foreach (var t in _player.GetComponentsInChildren<Transform>(true)) if (t.name == "head") { _playerHead = t.gameObject; break; }
            }
            _playerCar = PlayerCar();
        }

        // the car the player drives: the Player's InCar FSM (state InCar, variable Car)
        private static GameObject PlayerCar()
        {
            if (_player == null) return null;
            if (_inCarFsm == null)
            {
                foreach (var f in _player.GetComponents<PlayMakerFSM>()) if (f.FsmName == "InCar") { _inCarFsm = f; break; }
                if (_inCarFsm == null) return null;
                _inCarVar = _inCarFsm.FsmVariables.GetFsmGameObject("Car");
            }
            if (_inCarFsm.Fsm == null || !_inCarFsm.Fsm.Initialized || _inCarFsm.ActiveStateName != "InCar") return null;
            return _inCarVar != null ? _inCarVar.Value : null;
        }

        private static string Name(GameObject go) { return go == null ? "?" : (go == _player ? "the player" : Prefab(go.name)); }
        private static string Prefab(string n) { int c = n.IndexOf('('); return c > 0 ? n.Substring(0, c) : n; }
        // the prefab name of an object, read once (GameObject.name is a native call + a new string every time)
        internal static string PrefabOf(GameObject go)
        {
            int id = go.GetInstanceID(); string n;
            if (_prefabOf.TryGetValue(id, out n)) return n;
            n = Prefab(go.name);
            if (_prefabOf.Count > 4096) _prefabOf.Clear();
            _prefabOf[id] = n;
            return n;
        }

        private static void Log(Agent a, string s)
        {
            if (Plugin.SensesLog.Value) Plugin.Log.LogInfo("Senses: " + a.Owner.name + " " + s);
        }

        internal static string Status() { return _agents.Count + " NPCs, " + _ghosts.Count + " ghosts"; }

        // ---------- debug overlay ([Debug] ShowGhosts) ----------
        private static readonly Color[] SrcColor = { Color.gray, new Color(0.4f, 0.6f, 1f), new Color(0.4f, 0.9f, 0.4f), new Color(1f, 0.9f, 0.2f), new Color(1f, 0.55f, 0.15f), new Color(1f, 0.3f, 0.9f), new Color(1f, 0.25f, 0.2f) };
        internal static void DrawDebug()
        {
            if (!On || !Plugin.ShowGhosts.Value) return;
            float now = Time.time;
            foreach (var g in _ghosts)
            {
                var c = g.Retired ? Color.gray : SrcColor[(int)g.Src];
                DebugOverlay.Mark(g.Pos + Vector3.up * 1.2f, c, 14f);
                DebugOverlay.Label(g.Pos + Vector3.up * 1.6f, "#" + g.Id + " " + g.Src.ToString().ToUpperInvariant() + (g.Retired ? " (searched)" : "") + " " + g.About + "  (" + g.Holders.Count + ", " + (now - g.Born).ToString("0") + " s)", c);
            }
            foreach (var kv in _agents)
            {
                var a = kv.Value;
                if (a.Owner == null || a.State == State.Idle) continue;
                string s; Color c;
                switch (a.State)
                {
                    case State.Combat: s = "COMBAT " + Name(a.Target); c = SrcColor[(int)Src.Vision]; break;
                    case State.Investigate: s = "-> ghost #" + (a.Ghost != null ? a.Ghost.Id.ToString() : "?") + " (" + (Rank(a.GhostPrio) == 4 ? "vision" : Rank(a.GhostPrio) == 3 ? "gunshot" : Rank(a.GhostPrio) == 2 ? "shout" : "engine") + ")"; c = a.Ghost != null ? SrcColor[(int)a.Ghost.Src] : Color.white; break;
                    default: s = "SEARCH " + Mathf.Max(0f, a.SearchUntil - now).ToString("0") + " s"; c = new Color(0.75f, 0.6f, 1f); break;
                }
                Vector3 top = a.Col != null ? new Vector3(a.Col.bounds.center.x, a.Col.bounds.max.y, a.Col.bounds.center.z) : a.T.position + Vector3.up * 2f;
                DebugOverlay.Label(top + Vector3.up * 0.35f, Prefab(a.Owner.name) + ": " + s, c);
            }
        }

        // ---------- NWH (reflection, like Apocapatrol's) ----------
        private static class Nwh
        {
            private static readonly Dictionary<int, object[]> _h = new Dictionary<int, object[]>();   // car id -> { engine, input }
            internal static void Clear() { _h.Clear(); }
            private static object[] Of(GameObject car)
            {
                object[] h;
                if (_h.TryGetValue(car.GetInstanceID(), out h)) return h;
                object vc = null;
                foreach (var c in car.GetComponents<Component>()) if (c != null && c.GetType().Name == "VehicleController") { vc = c; break; }
                if (vc == null) return null;
                object pt = Read(vc, "powertrain");
                h = new object[] { Read(pt, "engine"), Read(vc, "input") };
                _h[car.GetInstanceID()] = h;
                return h;
            }
            private static object Read(object o, string name)
            {
                if (o == null) return null;
                var t = o.GetType();
                var f = Field(t, name); if (f != null) return f.GetValue(o);
                for (var c = t; c != null; c = c.BaseType) { var p = c.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); if (p != null) return p.GetValue(o, null); }
                return null;
            }
            internal static bool EngineRunning(GameObject car)
            {
                try { var h = Of(car); var r = h != null ? Read(h[0], "IsRunning") : null; return r is bool && (bool)r; } catch (Exception) { return false; }
            }
            internal static float Throttle(GameObject car)
            {
                try { var h = Of(car); var r = h != null ? Read(h[1], "Throttle") : null; return r is float ? (float)r : 0f; } catch (Exception) { return 0f; }
            }
        }

        // ---------- persistence ----------
        // A sidecar per save slot (BepInEx/config/Apocaraider/Saves/<SaveGameN.es3>.senses.txt) written when the game saves, read
        // after a load once the NPCs (found by their saved names) are back. NPC names are unique per world (the Health FSM numbers them).
        private static class Persist
        {
            private static PlayMakerFSM _saveLoad; private static string _lastState, _loadSlot; private static float _nextScan; private static bool _restoring, _loadPending;
            private static int _gen;        // bumped by every reset / clear / new load: a running Restore sees it and stops

            internal static void ResetForScene() { _saveLoad = null; _lastState = null; _loadSlot = null; _restoring = false; _loadPending = false; _nextScan = 0f; _gen++; }

            internal static void Tick(MonoBehaviour runner)
            {
                if (Time.unscaledTime >= _nextScan && (_saveLoad == null || _saveLoad.gameObject == null))
                {
                    _nextScan = Time.unscaledTime + 1f;
                    var go = GameObject.Find("SaveLoadGame");
                    if (go != null) foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f.FsmName == "SaveLoadGame") { _saveLoad = f; break; }
                }
                if (_saveLoad == null || _saveLoad.Fsm == null || !_saveLoad.Fsm.Initialized) return;
                string state = _saveLoad.ActiveStateName ?? "";
                if (state == _lastState) return;
                _lastState = state;
                try
                {
                    if (state == "SaveGame") Save(Slot());
                    if (state == "LoadGame") { _loadSlot = Slot(); _loadPending = true; _gen++; _restoring = false; }   // a new load cancels a restore in progress
                    if (state == "setSeed") Clear();
                    if (state == "isPlay" && _loadPending && !_restoring)
                    {
                        _loadPending = false;
                        // the slot read while loading can be stale (SaveFile may still name the previous slot): read it again now that the game is in play
                        string s = Slot(); if (string.IsNullOrEmpty(s)) s = _loadSlot;
                        _loadSlot = null;
                        if (!string.IsNullOrEmpty(s)) runner.StartCoroutine(Restore(s));
                    }
                }
                catch (Exception e) { Plugin.Log.LogError("Senses persistence: " + e); }
            }

            private static string Slot()
            {
                try { string p = ES3Settings.defaultSettings.path; if (!string.IsNullOrEmpty(p)) return Path.GetFileName(p); } catch (Exception) { }
                var v = _saveLoad != null ? _saveLoad.FsmVariables.GetFsmString("SaveFile") : null;
                return v != null ? v.Value : null;
            }

            private static bool TryPath(string slot, out string path)
            {
                path = null;
                string name = Path.GetFileName(slot ?? "");
                if (string.IsNullOrEmpty(name) || !name.StartsWith("SaveGame", StringComparison.OrdinalIgnoreCase)) return false;
                foreach (char c in name) if (!(char.IsLetterOrDigit(c) || c == '.')) return false;
                path = Path.Combine(Path.Combine(Path.Combine(Paths.ConfigPath, "NPCAI"), "Saves"), name + ".senses.txt");
                return true;
            }

            private static void Clear()
            {
                _gen++;
                foreach (var kv in _agents) { var a = kv.Value; a.State = State.Idle; a.Target = null; a.Ghost = null; a.GhostPrio = Src.None; a.Heard.Clear(); a.Done.Clear(); }
                for (int i = _ghosts.Count - 1; i >= 0; i--) KillGhost(i);
            }

            private static void Save(string slot)
            {
                string path;
                if (!TryPath(slot, out path)) { Plugin.Log.LogWarning("Senses: no save slot to write the senses for (" + slot + ")"); return; }
                var sb = new StringBuilder();
                var ci = CultureInfo.InvariantCulture;
                float now = Time.time;
                sb.Append("v1 ").Append(Path.GetFileName(slot)).Append('\n');
                foreach (var g in _ghosts)
                    sb.Append("ghost ").Append(g.Id).Append(' ').Append(g.Pos.x.ToString("0.00", ci)).Append(' ').Append(g.Pos.y.ToString("0.00", ci)).Append(' ').Append(g.Pos.z.ToString("0.00", ci))
                      .Append(' ').Append((int)g.Src).Append(' ').Append((now - g.Born).ToString("0", ci)).Append(' ').Append(g.About.Replace('\n', ' ')).Append('\n');
                int n = 0;
                foreach (var kv in _agents)
                {
                    var a = kv.Value;
                    if (a.Owner == null || a.State == State.Idle) continue;
                    string target = a.Target == null ? "-" : (a.Target == _player ? "Player" : a.Target.name);
                    sb.Append("agent ").Append(a.Owner.name.Replace(' ', '_')).Append(' ').Append((int)a.State).Append(' ').Append(target.Replace(' ', '_')).Append(' ')
                      .Append(a.Ghost != null ? a.Ghost.Id : -1).Append(' ').Append((int)a.GhostPrio).Append(' ').Append(Mathf.Max(0f, a.SearchUntil - now).ToString("0.0", ci)).Append(' ')
                      .Append(a.LastSeen.x.ToString("0.00", ci)).Append(' ').Append(a.LastSeen.y.ToString("0.00", ci)).Append(' ').Append(a.LastSeen.z.ToString("0.00", ci)).Append('\n');
                    n++;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path + ".tmp", sb.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
                Plugin.Verbose("Senses: saved " + _ghosts.Count + " ghost(s), " + n + " alert NPC(s) for " + Path.GetFileName(slot));
            }

            private static IEnumerator Restore(string slot)
            {
                _restoring = true;
                string path;
                if (!TryPath(slot, out path)) { _restoring = false; yield break; }
                if (!File.Exists(path)) path = Path.Combine(Path.Combine(Path.Combine(Paths.ConfigPath, "Apocaraider"), "Saves"), Path.GetFileName(slot) + ".senses.txt");
                if (!File.Exists(path)) { _restoring = false; yield break; }
                string[] lines;
                try { lines = File.ReadAllLines(path); } catch (Exception e) { Plugin.Log.LogWarning("Senses: cannot read " + path + ": " + e.Message); _restoring = false; yield break; }
                if (lines.Length == 0 || !lines[0].StartsWith("v1 ")) { _restoring = false; yield break; }
                if (!string.Equals(lines[0].Substring(3).Trim(), Path.GetFileName(slot), StringComparison.OrdinalIgnoreCase))
                { Plugin.Log.LogWarning("Senses: " + path + " was written for another slot, not restored"); _restoring = false; yield break; }
                Clear();
                int gen = _gen;
                _nextPlayerFind = 0f; FindPlayer();
                var ci = CultureInfo.InvariantCulture;
                var ghostById = new Dictionary<int, Ghost>();
                var pending = new List<string[]>();
                float now = Time.time;
                foreach (var line in lines)
                {
                    var p = line.Split(' ');
                    try
                    {
                        if (p[0] == "ghost" && p.Length >= 7)
                        {
                            var g = new Ghost { Id = int.Parse(p[1]), Pos = new Vector3(float.Parse(p[2], ci), float.Parse(p[3], ci), float.Parse(p[4], ci)), Src = (Src)int.Parse(p[5]), Born = now - float.Parse(p[6], ci), Moved = now, About = p.Length > 7 ? string.Join(" ", p, 7, p.Length - 7) : "" };
                            if (_ghostRoot == null) { var r = new GameObject("NPCAI.Ghosts") { hideFlags = HideFlags.HideAndDontSave }; UnityEngine.Object.DontDestroyOnLoad(r); _ghostRoot = r.transform; }
                            g.Obj = new GameObject("NPCAI.Ghost#" + g.Id) { hideFlags = HideFlags.HideAndDontSave };
                            g.Obj.transform.SetParent(_ghostRoot, false); g.Obj.transform.position = g.Pos;
                            if (g.About.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0) g.Subject = _player;
                            _ghosts.Add(g); ghostById[g.Id] = g;
                            if (g.Id >= _nextGhostId) _nextGhostId = g.Id + 1;
                        }
                        else if (p[0] == "agent" && p.Length >= 10) pending.Add(p);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Senses: bad line in " + path + ": " + e.Message); }
                }
                float until = Time.realtimeSinceStartup + 20f;
                var byName = new Dictionary<string, Agent>();
                int restored = 0;
                while (pending.Count > 0 && Time.realtimeSinceStartup < until)
                {
                    byName.Clear();
                    foreach (var kv in _agents) if (kv.Value.Owner != null) byName[kv.Value.Owner.name.Replace(' ', '_')] = kv.Value;
                    for (int i = pending.Count - 1; i >= 0; i--)
                    {
                        var p = pending[i];
                        Agent a;
                        if (!byName.TryGetValue(p[1], out a)) continue;
                        pending.RemoveAt(i);
                        try
                        {
                            var st = (State)int.Parse(p[2]);
                            a.LastSeen = new Vector3(float.Parse(p[7], ci), float.Parse(p[8], ci), float.Parse(p[9], ci));
                            GameObject target = p[3] == "Player" ? _player : (p[3] == "-" ? null : (byName.ContainsKey(p[3]) ? byName[p[3]].Owner : null));
                            int gid = int.Parse(p[4]); Ghost g; ghostById.TryGetValue(gid, out g);
                            if (st == State.Combat)
                            {
                                if (target != null) { a.State = State.Combat; a.Target = target; }
                                else { g = GetOrMake(Src.Vision, a.LastSeen, null, "last seen (before the load)", 2f, 9999f, now); a.State = State.Idle; Assign(a, g, Src.Vision, now); }
                            }
                            else if (g != null)
                            {
                                a.State = State.Idle; Assign(a, g, (Src)int.Parse(p[5]), now);
                                if (st == State.Search) { a.State = State.Search; a.SearchUntil = now + float.Parse(p[6], ci); }
                            }
                            restored++;
                        }
                        catch (Exception e) { Plugin.Log.LogWarning("Senses: restore of " + p[1] + " failed: " + e.Message); }
                    }
                    if (pending.Count > 0) yield return new WaitForSecondsRealtime(0.5f);
                    if (gen != _gen) { Plugin.Verbose("Senses: restore of " + Path.GetFileName(slot) + " cancelled"); _restoring = false; yield break; }   // a new load / reset happened meanwhile
                }
                for (int i = _ghosts.Count - 1; i >= 0; i--) if (_ghosts[i].Holders.Count == 0) KillGhost(i);
                Plugin.Verbose("Senses: restored " + restored + " alert NPC(s), " + _ghosts.Count + " ghost(s) from " + Path.GetFileName(slot) + (pending.Count > 0 ? "; " + pending.Count + " NPC(s) not found" : ""));
                _restoring = false;
            }
        }
    }
}
