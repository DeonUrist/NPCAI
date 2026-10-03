using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace NPCAI
{
    // How ground NPCs move while they have a target ("the brain").
    //
    // Vanilla (asset dump 2026-10-01, every human and ground animal): while the Detection FSM has a detectedObj the Attack FSM sits in its
    // "trigger" state and the Movement FSM runs the NPC forward at 5-6 m/s (SetVelocity local z, every frame). Nothing turns the body toward
    // the target: the two LookAt actions in trigger aim the AttackRaycast children (the melee / burst sensors), the body's yaw comes only from
    // the "Rotate" FSM (random +-180 deg/s for 1-3 s every 1-3 s), the Attack FSM's run/rotate states (random again) and two 1.2 m bumper
    // rays that spin it +-120 deg/s. The "Unstuck" FSM (position check every 0.3 s) answers a blocked NPC with a random spin and a 40 N hop.
    // The NPC faces the target only during the melee swing (SmoothLookAt), the burst (an instant LookAt) and the run to a Hide point. So a
    // vanilla chase is a random walk that happens to reach you; the "headless chicken".
    //
    // Here, per NPC (humans and ground animals: a root object with the Movement/Attack/Detection/Unstuck FSMs and a Rigidbody with gravity;
    // flyers have gravity off and are left alone, as is anything parented under something else, which is how Apocapatrol seats a crew in a
    // car - a bailed-out crew is a fresh root object and gets a brain at once):
    // - the body turns at [Brain] TurnRate deg/s (never snaps), toward the steered heading while moving, toward the target while holding;
    // - feelers: FeelerCount sweeps over +-FeelerAngle around the direction to the target (melee: body-wide capsule sweeps, side commitment and
    //   wall following, see Steer; gunmen: plain rays unless ShooterPathing), FeelerLength / MeleeFeelerLength m; a blocked heading from a
    //   recent stuck is avoided for StuckMemorySeconds;
    // - a gunman (Tracers knows the gun) stops where it can shoot - line of sight and within [NpcAim] EngagePercent of the gun's reach - and
    //   holds there with the gun up (AimPose: the shooting animation frozen on its first frame), kneeling with CrouchChance % (no crouch
    //   animation exists: legs and hips re-posed by code in LateUpdate, capsule shortened), facing the target; the game's own burst logic
    //   and the Aim pacing do the shooting; every HoldRecheck it
    //   rolls AdvanceChance % to run at the target for AdvanceSeconds; it moves again when the line of sight is lost or the target walks out
    //   of the engage distance. Melee NPCs run at the target with the feelers.
    // - stuck (the Unstuck FSM's detector is kept, its spin + hop are not): a gunman with a line of sight and the target in reach holds and
    //   shoots from there; otherwise it backs up for StuckBackupSeconds, remembers the heading as blocked and leaves on the clearer side;
    //   StuckGiveUpCount stucks within 10 s -> it stands for a second (facing the target), then tries again. Every wait x [Brain] ReactionTime %.
    // - the random turns and bumper rays are switched off while a brain is in charge; Enabled = false gives the vanilla behaviour back.
    // Cost: one Think per NPC every 0.1 s (<= 5 brains) .. 0.5 s (> 20), staggered; a moving NPC casts FeelerCount (+1 drop check) rays
    // per Think, a holding gunman one line-of-sight ray; the per-frame work is one RotateTowards per NPC. NPCs beyond MaxDistance run vanilla.
    //
    // Hooks (Harmony prefixes; every one returns at once unless the FSM belongs to an NPC with an active brain):
    //   SetVelocity.DoSetVelocity (Movement FSM) - the pedal: forward, zero or backing;      Rotate.DoRotate (Rotate + Attack FSMs) - no random yaw;
    //   Raycast.DoRaycast (Attack, the 1.2 m bumper rays) - report clear;                 LookAt.DoLookAt (Attack, owner) - our turn rate instead;
    //   SendEvent.OnEnter (Attack "Animal_Run" while holding -> "Animal_Idle"; Unstuck "Animal_rotateRandom" -> our stuck handling);
    //   AddForce.DoAddForce (Unstuck) - no hop.
    internal static class Brain
    {
        internal enum Mode { Off, Chase, Hold, Advance, BackUp, Rest, Search, Walk }   // Walk (1.6.0): Idle walks it to a ghost, the brain stands aside

        private sealed class Npc
        {
            public GameObject Owner; public Transform T; public Rigidbody Rb; public Collider Col;
            public PlayMakerFSM Attack, Movement;
            public FsmGameObject Target;              // Detection.detectedObj
            public bool Ranged; public WeaponRanges.Kind Kind; public PlayMakerFSM MovementFsm; public float MadeAt, NextRangedCheck;
            public Animator Anim; public string AimState; public bool Frozen;   // the shooting animation held on its first frame = aiming
            public int CanCrouch;                     // 0 unknown, 1 has the leg bones, -1 no
            public bool Crouched, PoseCaptured; public float Drop;
            public Transform Hips, LUpLeg, LLeg, RUpLeg, RLeg;
            public Vector3 HipsLocal; public Quaternion LUpLegRot, LLegRot, RUpLegRot, RLegRot;   // the standing pose the kneel is built on
            public CapsuleCollider Capsule; public float CapHeight; public Vector3 CapCenter;
            public Mode Mode; public float ModeUntil;
            public float NextTick, NextRecheck, Stagger;
            public float Heading; public bool HasHeading;   // steered world yaw, degrees
            public float BlockedYaw, BlockedUntil;
            public int Side, ClearLooks; public float SideUntil; public bool Flipped;     // pathing: the committed way around an obstacle
            public float FreeLeft, FreeRight;                                             // the last fan's free lengths per half
            public Vector3 Waypoint; public bool HasWaypoint; public float WaypointUntil, NextScout;   // a scouted corner with a clear line to the target
            public string NavReason = "";
            public bool ToGhost, OnNav, WasOnNav; public float LookYaw, NextLookTurn, NavOffUntil;                       // Senses: going to a ghost / looking around at it
            public float BestDist = float.MaxValue, NoProgressSince;
            public int Stucks; public float FirstStuck, LastHop = -10f;
            public int NavStucks; public float NavStuckSince, LastStep = -10f;
            public float CmdSpeed, BlkSince, BlkDriven, BlkLastThink; public Vector3 BlkPos;     // blocked check: driven vs covered distance    // map-route walking: stucks in a row, the last step-over
            public float LosLostAt = -1f; public bool Los; public float Dist;
            public float LastLog;
            public int LosLooks;                       // consecutive thinks with line of sight (Hold needs 2: no flapping on a fence bar)
            public float CrouchRolledAt = -10f; public bool CrouchRoll;   // the kneel roll of this hold episode (kept 5 s across short Chase gaps)
            public float FaceTargetUntil;              // Aim wants a burst: face the target instead of the steered heading for a moment
            public float TickScale = 1f;               // thinks less often far from the camera
            public float SpeedScale = 1f;              // < 1: queues behind a friend going the same way
            public int AvoidSide; public float AvoidUntil;            // passing a friend: the side, kept a moment (no left-right dither)
            public int FriendBumps; public float FriendBumpsSince;
            public Vector3 MakeWayDir; public float MakeWayUntil;    // standing in a friend's way: a short step aside
            public readonly List<Vector3> Trail = new List<Vector3>(); public readonly List<byte> TrailKind = new List<byte>();   // [Debug] NavTrace
            public float NextTrail, NextTraceLog, NextTraceDump; public Vector3 TraceGoal; public string LegBlock = "";
        }

        private static readonly Dictionary<int, Npc> _npcs = new Dictionary<int, Npc>();
        private static readonly HashSet<int> _ignored = new HashSet<int>();
        private static readonly List<int> _dead = new List<int>();
        private static float _nextCount, _interval = 0.1f;
        private static int _active, _created;
        // layers (TagManager): 0 Default, 6 Player, 8 Car, 9 Item, 10 Actor, 11 Door, 14 Ground, 16 SeeTrough, 18 PhysicsLock
        private static readonly int Mask = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 16);   // the game's bumper-ray layers (buildings, cars, doors, fences)
        private static readonly int GroundMask = (1 << 0) | (1 << 8) | (1 << 11) | (1 << 14) | (1 << 16);   // + Ground: for the drop check
        private static readonly int LosMask = GroundMask;          // line of sight for the shooting decision: a hill crest hides the target too
        // pathing feelers: everything solid except creatures, the player, weapons and the non-world layers; Ground counts only where it is
        // steep (a rock, a prop placed on that layer), gentle terrain ahead is not an obstacle
        private static readonly int PathMask = ~((1 << 1) | (1 << 2) | (1 << 4) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 10) | (1 << 12) | (1 << 13) | (1 << 15) | (1 << 17) | (1 << 19) | (1 << 22));
        private static float[] _angles = new float[0];
        private static readonly float[] _scores = new float[32];
        private static readonly float[] _blocks = new float[32];

        public static void OnSceneLoaded() { _npcs.Clear(); _ignored.Clear(); _active = 0; }

        internal static bool On { get { return Plugin.BrainEnabled != null && Plugin.BrainEnabled.Value; } }
        // [Brain] ReactionTime %: every wait of the brain (think interval, back-up, rest, side lock, no-progress, memory, LOS tolerance,
        // hold rechecks) is multiplied by this; 100 = the defaults, 50 = twice as quick, 500 = five times slower
        internal static float R { get { return Mathf.Clamp(Plugin.ReactionTime.Value, 1f, 500f) / 100f; } }

        // ---------- per frame ----------
        public static void Tick()
        {
            if (_npcs.Count == 0) return;
            float now = Time.time, dt = Time.deltaTime;
            if (now >= _nextCount)
            {
                _nextCount = now + 1f;
                _dead.Clear();
                int n = 0;
                var cam = Camera.main; Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
                foreach (var kv in _npcs)
                {
                    var np = kv.Value;
                    if (np.Owner == null) _dead.Add(kv.Key);
                    else if (np.Mode != Mode.Off) n++;
                    if (np.Owner != null && cam != null)
                    {
                        // out of the player's sight the finer steering is wasted: think half / a quarter as often
                        float cd = (np.T.position - camPos).sqrMagnitude;
                        np.TickScale = cd > 80f * 80f ? 4f : cd > 40f * 40f ? 2f : 1f;
                    }
                }
                foreach (var k in _dead) _npcs.Remove(k);
                _active = n;
                _interval = (Plugin.ScaleWithActors.Value ? (n <= 5 ? 0.1f : n <= 10 ? 0.2f : n <= 20 ? 0.3f : 0.5f) : 0.1f) * R;
            }
            bool on = On;
            float turn = Mathf.Max(10f, Plugin.TurnRate.Value) * dt;
            foreach (var kv in _npcs)
            {
                var n = kv.Value;
                if (n.Owner == null) continue;
                if (!on)
                {
                    if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "brain off");
                    continue;
                }
                if (n.Crouched && n.T.parent != null) Crouch(n, false);     // seated by Apocapatrol after all: stand up
                if (now < n.MakeWayUntil && n.Rb != null && n.T.parent == null && n.Mode != Mode.Off)   // a friend needs past: a short step aside
                {
                    Vector3 v = n.MakeWayDir * 1.8f; v.y = n.Rb.velocity.y; n.Rb.velocity = v;
                }
                string state;
                if (!Engaged(n, out state))
                {
                    if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "target lost");
                    continue;
                }
                if (now >= n.NextTick)
                {
                    n.NextTick = now + _interval * n.TickScale;
                    try { Think(n, now); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Brain: " + e);
                        try { SetMode(n, Mode.Off, "error"); } catch (Exception) { n.Mode = Mode.Off; try { Unfreeze(n); Crouch(n, false); } catch (Exception) { } }
                        continue;
                    }
                }
                if (n.Mode == Mode.Off || n.Mode == Mode.BackUp || n.Mode == Mode.Walk) continue;     // Walk: Idle turns the body
                if (n.Frozen && state != "trigger" && state != "run") Unfreeze(n);      // the burst (or a melee swing, a hide run): let the animation play
                bool steering = (n.Mode == Mode.Chase || n.Mode == Mode.Advance) && n.HasHeading && state != "attack_melee" && state != "hide";
                if (state != "trigger" && state != "run" && state != "attack_ranged" && !steering) continue;   // melee swing, hide run ...: the game's own facing
                // (1.4.6) a moving NPC is turned to its steered heading in every Attack state but the swing / hide: the run velocity is ours in all
                // of them, and an unturned body used to run on along its old facing (an idle raider: toward home, away from the way out)
                var target = n.Target.Value;
                if (target == null) continue;
                Vector3 to;
                if (n.Mode == Mode.Search)
                {
                    if (Idle.SearchWalking(n.Owner)) continue;      // walking its search round ([Idle]): the round turns the body
                    if (now >= n.NextLookTurn)
                    {
                        n.NextLookTurn = now + UnityEngine.Random.Range(1f, 1.8f) * R;
                        n.LookYaw = n.T.eulerAngles.y + (UnityEngine.Random.value < 0.5f ? -1f : 1f) * UnityEngine.Random.Range(80f, 160f);
                    }
                    to = Quaternion.Euler(0f, n.LookYaw, 0f) * Vector3.forward;
                }
                else if (n.Mode == Mode.Hold || n.Mode == Mode.Rest || state == "attack_ranged" || now < n.FaceTargetUntil)
                    to = target.transform.position - n.T.position;
                else if (n.HasHeading) to = Quaternion.Euler(0f, n.Heading, 0f) * Vector3.forward;
                else to = target.transform.position - n.T.position;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f) continue;
                n.T.rotation = Quaternion.RotateTowards(n.T.rotation, Quaternion.LookRotation(to, Vector3.up), turn);
            }
        }

        // the brain is in charge only while the Attack FSM runs with a target, the NPC stands on its own (not seated in a car) and is near
        private static bool Engaged(Npc n, out string state)
        {
            state = null;
            if (n.T.parent != null || n.Attack == null || !n.Attack.enabled || n.Target == null) return false;
            var fsm = n.Attack.Fsm;
            if (fsm == null || !fsm.Initialized || fsm.Finished) return false;
            if (n.Target.Value == null) return false;
            state = fsm.ActiveStateName;
            return true;
        }

        private static void Think(Npc n, float now)
        {
            var target = n.Target.Value;
            if (target == null) { if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "no target"); return; }
            Vector3 tp = target.transform.position;
            Vector3 d3 = tp - n.T.position; d3.y = 0f;
            float d = d3.magnitude;
            n.Dist = d;
            // (1.6.0) a ghost to walk to: Idle walks the raider there the way it walks home (map to the exit, then straight); the brain only
            // stands aside (no pedal, no turning, no stuck handling) - at any distance. Idle hands it back after 3 failed tries.
            if (Senses.KindOf(n.Owner) == 2 && Idle.WalksToGhost(n.Owner))
            {
                n.ToGhost = true;
                if (n.Mode != Mode.Walk) SetMode(n, Mode.Walk, "Idle walks it to the ghost");
                return;
            }
            if (d > Plugin.MaxDistance.Value) { if (n.Mode != Mode.Off) SetMode(n, Mode.Off, "far"); return; }
            if (!n.Ranged && now < n.MadeAt + 10f && now >= n.NextRangedCheck)     // a gun that was not in the hand at the spawn frame
            {
                n.NextRangedCheck = now + 1f;
                WeaponRanges.Kind gk;
                if (WeaponRanges.GunKindOf(n.Owner, out gk)) { n.Ranged = true; n.Kind = gk; SetupRanged(n); }
            }
            int kind = Senses.KindOf(n.Owner);           // 0 vanilla / seen target, 1 seen target, 2 going to a ghost, 3 searching at it
            if (kind == 3) { if (n.Mode != Mode.Search) { n.NextLookTurn = 0f; SetMode(n, Mode.Search, "looks around"); } return; }
            n.ToGhost = kind == 2;
            if (n.Mode == Mode.Off || n.Mode == Mode.Search || n.Mode == Mode.Walk) SetMode(n, Mode.Chase, !Plugin.BrainLog.Value ? "" : (n.ToGhost ? "ghost" : "target") + " at " + d.ToString("0") + " m");

            if (Tracing && now >= n.NextTrail && n.Trail.Count < 1200)
            {
                n.NextTrail = now + 0.5f; n.TraceGoal = tp;
                n.Trail.Add(n.T.position); n.TrailKind.Add((byte)(n.Mode == Mode.BackUp || n.Mode == Mode.Rest ? 2 : n.OnNav ? 0 : 1));
            }
            if (n.Mode == Mode.BackUp) { if (now < n.ModeUntil) return; SetMode(n, Mode.Chase, "backed up"); }
            if (n.Mode == Mode.Rest) { if (now < n.ModeUntil) return; SetMode(n, Mode.Chase, "rested"); }

            if (n.ToGhost)
            {
                // a ghost is a place, not a target: go there (melee style, around things), then look around
                if (d <= Mathf.Max(0.5f, Plugin.ArriveDistance.Value)) { Senses.ArrivedAt(n.Owner); n.NextLookTurn = 0f; SetMode(n, Mode.Search, "at the ghost"); return; }
                if (n.Mode == Mode.Hold || n.Mode == Mode.Advance) SetMode(n, Mode.Chase, "ghost");
            }
            else if (n.Ranged)
            {
                bool los = LineOfSight(n, target, tp);
                if (los) { n.Los = true; n.LosLostAt = -1f; n.LosLooks++; }
                else { if (n.Los) { n.Los = false; n.LosLostAt = now; } n.LosLooks = 0; }
                float reach = WeaponRanges.RangeOf(n.Kind);
                float engage = reach * Mathf.Clamp(Plugin.EngagePercent.Value, 1f, 100f) / 100f;
                bool good = los && d <= engage;
                if (n.Mode == Mode.Hold)
                {
                    bool lost = (!los && now - n.LosLostAt > 0.3f * R) || d > engage * 1.1f;
                    if (lost) { SetMode(n, Mode.Chase, !Plugin.BrainLog.Value ? "" : los ? "target at " + d.ToString("0") + " m" : "no line of sight"); }
                    else if (now >= n.NextRecheck)
                    {
                        n.NextRecheck = now + UnityEngine.Random.Range(Mathf.Max(0.1f, Plugin.HoldRecheckMin.Value), Mathf.Max(0.1f, Plugin.HoldRecheckMax.Value)) * R;
                        if (UnityEngine.Random.Range(0f, 100f) < Plugin.AdvanceChance.Value && d > 3f)
                        {
                            n.ModeUntil = now + UnityEngine.Random.Range(Plugin.AdvanceMin.Value, Mathf.Max(Plugin.AdvanceMin.Value, Plugin.AdvanceMax.Value));
                            SetMode(n, Mode.Advance, "feels like it");
                        }
                        else return;
                    }
                    else return;
                }
                if (n.Mode == Mode.Advance)
                {
                    if (now >= n.ModeUntil || !los || d < 3f)
                    {
                        if (good) { SetMode(n, Mode.Hold, !Plugin.BrainLog.Value ? "" : "at " + d.ToString("0") + " m" + (now >= n.ModeUntil ? "" : ", enough")); return; }
                        SetMode(n, Mode.Chase, !Plugin.BrainLog.Value ? "" : los ? "target at " + d.ToString("0") + " m" : "no line of sight");
                    }
                }
                else if (n.Mode == Mode.Chase && good && n.LosLooks >= 2)        // two thinks in a row: a fence bar flickering across the line doesn't stop and start the hold
                {
                    n.NextRecheck = now + UnityEngine.Random.Range(Mathf.Max(0.1f, Plugin.HoldRecheckMin.Value), Mathf.Max(0.1f, Plugin.HoldRecheckMax.Value)) * R;
                    SetMode(n, Mode.Hold, !Plugin.BrainLog.Value ? "" : "line of sight at " + d.ToString("0") + " m (engages within " + engage.ToString("0") + ")");
                    return;
                }
            }
            // moving: Chase or Advance
            if (Blocked(n, now)) return;
            // inside a baked camp / building / cave the structure's own map says the way (out through the right exit, around its walls)
            Vector3 navNext = Vector3.zero; float pathLeft = 0f;
            bool hopLeg = false;
            bool asked = now >= n.NavOffUntil;
            n.OnNav = asked && Nav.Next(n.Owner, n.T.position, tp, out navNext, out pathLeft);
            string trace = null; bool vetoed = false;
            if (Tracing && now >= n.NextTraceLog) trace = asked ? Nav.TraceInfo() : "";
            if (n.OnNav)
            {
                hopLeg = Nav.HopAhead(n.T.position, navNext);         // the route climbs a low lip / kerb (a hop edge) right ahead
                if (!NavLegClear(n, navNext, hopLeg)) { n.OnNav = false; vetoed = true; }
                else if (hopLeg) TryHop(n, now, true);
            }
            if (trace != null)
            {
                n.NextTraceLog = now + 1f;
                Vector3 vel = n.Rb != null ? n.Rb.velocity : Vector3.zero; vel.y = 0f;
                Vector3 fp = n.Col != null ? n.Col.bounds.min : n.T.position;
                Plugin.Log.LogInfo("Trace: " + n.Owner.name + " " + n.Mode + " at " + n.T.position.x.ToString("0.0") + "," + n.T.position.z.ToString("0.0") + " feet y " + fp.y.ToString("0.0")
                    + " speed " + vel.magnitude.ToString("0.0") + " goal " + d.ToString("0") + " m | "
                    + (!asked ? "MAP OFF " + (n.NavOffUntil - now).ToString("0") + " s more"
                       : n.OnNav ? "MAP " + trace + (hopLeg ? " HOP" : "") + " wp " + Vector3.Distance(new Vector3(navNext.x, 0f, navNext.z), new Vector3(n.T.position.x, 0f, n.T.position.z)).ToString("0.0") + " m away"
                       : vetoed ? "MAP VETOED (" + n.LegBlock + ") " + trace
                       : "NO MAP: " + (Nav.LastReason.Length > 0 ? Nav.LastReason : "outside every map") + " | " + trace)
                    + (n.HasWaypoint ? " | corner " + Vector3.Distance(n.Waypoint, n.T.position).ToString("0.0") + " m" : "") + (n.Side != 0 ? " | side " + n.Side : ""));
            }
            if (Plugin.BrainLog.Value && !n.OnNav && Nav.LastReason != n.NavReason)
            {
                n.NavReason = Nav.LastReason;
                if (n.NavReason.Length > 0) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " no map route: " + n.NavReason + Nav.Probe(n.T.position));
            }
            if (n.OnNav != n.WasOnNav) { n.WasOnNav = n.OnNav; n.BestDist = float.MaxValue; n.NoProgressSince = now; }   // map path length and straight distance don't compare
            if (n.OnNav) NavProgress(n, pathLeft, now);         // may switch the map off for a moment (full feelers then)
            else if (!Progress(n, d, now)) return;
            Vector3 goal = d3; float gd = d;
            if (n.OnNav)
            {
                goal = navNext - n.T.position; goal.y = 0f; gd = Mathf.Max(0.5f, goal.magnitude);
                n.HasWaypoint = false;
            }
            else if (n.HasWaypoint)
            {
                Vector3 w = n.Waypoint - n.T.position; w.y = 0f;
                float wd = w.magnitude;
                if (wd < 1.2f || now > n.WaypointUntil) { n.HasWaypoint = false; if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + (wd < 1.2f ? " reached the corner" : " gives up the corner")); }
                else if (DirectClear(n, tp, d)) { n.HasWaypoint = false; if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " has a clear line, drops the corner"); }
                else { goal = w; gd = wd; }
            }
            if (n.OnNav) NavSteer(n, goal, now);                // on a map route: straight at the waypoint, short front feelers, steps over low stuff
            else Steer(n, goal, gd, now, d3);
            if (Tracing && n.HasHeading && now + 1f - n.NextTraceLog < 0.05f)      // right after a trace line: where the feelers actually point
            {
                float want = Mathf.Atan2(goal.x, goal.z) * Mathf.Rad2Deg;
                float off = Mathf.DeltaAngle(want, n.Heading);
                if (Mathf.Abs(off) > 30f) Plugin.Log.LogInfo("Trace: " + n.Owner.name + " feelers turn " + off.ToString("0") + " deg off the " + (n.OnNav ? "map waypoint" : n.HasWaypoint ? "corner" : "straight line"));
            }
        }

        // speed while turning: 1 within 30 deg of the wanted heading, down to 0.1 at 120 deg and beyond (also used by Idle)
        internal static float TurnSpeedFactor(float offDeg)
        {
            float a0 = Plugin.MoveFullSpeedAngle.Value, a1 = Mathf.Max(a0 + 1f, Plugin.MoveSlowestAngle.Value);
            if (offDeg <= a0) return 1f;
            return Mathf.Lerp(1f, Mathf.Clamp01(Plugin.MoveSlowestSpeed.Value), Mathf.Clamp01((offDeg - a0) / (a1 - a0)));
        }

        // (1.4.7) Blocked: over BlockedSeconds the body covered less than BlockedRatio of the distance it was driven (running in place against
        // something, grinding along a wall) -> the same answer as a stuck: hop, or a step back and another way (on a map route the blocked
        // heading is skipped for BlockedMemory s; 3 times in 8 s -> full feelers for 3 s). Never keeps pushing.
        private static bool Blocked(Npc n, float now)
        {
            float dt = now - n.BlkLastThink; n.BlkLastThink = now;
            bool moving = n.Mode == Mode.Chase || n.Mode == Mode.Advance;
            if (!moving || dt > 1f || Senses.Blown(n.Owner) || n.Rb == null) { BlkReset(n, now); return false; }
            n.BlkDriven += n.CmdSpeed * dt;
            if (now - n.BlkSince < Mathf.Max(0.2f, Plugin.BlockedSeconds.Value)) return false;
            Vector3 mv = n.T.position - n.BlkPos; mv.y = 0f;
            float driven = n.BlkDriven, covered = mv.magnitude;
            BlkReset(n, now);
            if (driven < 0.6f || covered >= driven * Plugin.BlockedRatio.Value) return false;
            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " blocked (covered " + covered.ToString("0.0") + " of " + driven.ToString("0.0") + " m)" + (n.OnNav ? " on its map route" : ""));
            OnStuck(n);
            return true;
        }
        private static void BlkReset(Npc n, float now) { n.BlkSince = now; n.BlkDriven = 0f; n.BlkPos = n.T.position; }

        // ---------- walking a map route (1.4.5) ----------
        // The map already knows the way around walls, so on a route the body walks it the way the idle return does: straight at the waypoint,
        // the first free of 5 headings (0, +-30, +-60) by a short body sweep from above the knee (1.2 m; low clutter is stepped over, not
        // steered around), no side commitment, no corner scouting, no left/right flips. Ankle-high things (a rock lip, debris, a kerb) ahead
        // are stepped over with a small lift.
        private static readonly float[] NavFan = { 0f, -30f, 30f, -60f, 60f };
        private static readonly int NavFeelMask = PathMask & ~(1 << 9);      // walls, rock, props, cars - not loose items
        private static void NavSteer(Npc n, Vector3 toWp, float now)
        {
            float want = Mathf.Atan2(toWp.x, toWp.z) * Mathf.Rad2Deg, chosen = want;
            n.Side = 0; n.Flipped = false; n.HasWaypoint = false;
            if (n.Col != null)
            {
                var b = n.Col.bounds;
                float r = Mathf.Clamp(Mathf.Min(b.extents.x, b.extents.z), 0.15f, 0.35f);
                float lo = b.min.y + 0.6f + r, hi = Mathf.Max(lo, b.min.y + b.size.y * 0.85f - r);
                Vector3 p1 = new Vector3(b.center.x, lo, b.center.z), p2 = new Vector3(b.center.x, hi, b.center.z);
                Transform troot = n.Target.Value != null ? n.Target.Value.transform.root : null;
                bool avoid = now < n.BlockedUntil;
                for (int i = 0; i < NavFan.Length; i++)
                {
                    if (avoid && Mathf.Abs(Mathf.DeltaAngle(want + NavFan[i], n.BlockedYaw)) < 25f) continue;   // blocked that way a moment ago
                    Vector3 dir = Quaternion.Euler(0f, want + NavFan[i], 0f) * Vector3.forward;
                    RaycastHit h;
                    if (!Physics.CapsuleCast(p1, p2, r, dir, out h, 1.2f, NavFeelMask, QueryTriggerInteraction.Ignore)
                        || h.collider.transform.root == n.T || (troot != null && h.collider.transform.root == troot)
                        || Nav.IsFloor(h.collider, h.normal, h.point.y, b.min.y))
                    { chosen = want + NavFan[i]; break; }
                }
            }
            n.Heading = chosen; n.HasHeading = true;
            StepOver(n, now);
        }

        // something ankle-high right ahead (a lip, debris, a kerb) and nothing at the knee: a small lift over it (at most every 0.6 s)
        private static void StepOver(Npc n, float now)
        {
            if (n.Rb == null || n.Col == null || now - n.LastStep < 0.6f || now - n.LastHop < 0.6f) return;
            var b = n.Col.bounds;
            Vector3 fwd = n.T.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) return; fwd.Normalize();
            float reach = Mathf.Min(b.extents.x, b.extents.z) + 0.35f;
            Vector3 low = new Vector3(b.center.x, b.min.y + 0.08f, b.center.z), knee = new Vector3(b.center.x, b.min.y + 0.55f, b.center.z);
            RaycastHit h;
            if (!Physics.Raycast(low, fwd, out h, reach, NavFeelMask, QueryTriggerInteraction.Ignore) || h.collider.transform.root == n.T) return;
            if (h.normal.y >= Nav.WalkNormal) return;                           // ground rising gently: the body walks up it
            if (Physics.Raycast(knee, fwd, reach + 0.25f, NavFeelMask, QueryTriggerInteraction.Ignore)) return;   // higher than a step: not this
            n.LastStep = now;
            Vector3 v = n.Rb.velocity;
            if (v.y < 2.4f) n.Rb.AddForce(Vector3.up * (2.4f - Mathf.Max(0f, v.y)) + fwd * 0.6f, ForceMode.VelocityChange);
            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " steps over " + h.collider.name + " (" + (h.point.y - b.min.y).ToString("0.00") + " m)");
        }

        // progress on a map route (path length left): 2.5 s without getting nearer -> a hop; 5 s -> full feelers for 3 s, then the map again
        private static void NavProgress(Npc n, float pathLeft, float now)
        {
            if (Senses.Blown(n.Owner) || pathLeft < n.BestDist - 0.5f) { n.BestDist = Mathf.Min(n.BestDist, pathLeft); n.NoProgressSince = now; return; }
            float stall = now - n.NoProgressSince;
            if (stall >= 5f * R)
            {
                n.NavOffUntil = now + 3f * R; n.OnNav = false; n.NoProgressSince = now;
                if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " gets nowhere on its map route, full feelers for " + (3f * R).ToString("0") + " s");
            }
            else if (stall >= 2.5f * R) TryHop(n, now, true);
        }

        // [Debug] NavTrace (NavDump + VerboseLog): a line per moving NPC per second and a picture of its trail when a chase ends / it rests
        private static bool Tracing { get { return Plugin.NavDump.Value && Plugin.BrainLog.Value; } }
        private static void TraceDump(Npc n, string why)
        {
            if (!Tracing || n.Trail.Count < 2 || Time.time < n.NextTraceDump) return;
            n.NextTraceDump = Time.time + 15f;
            Plugin.Log.LogInfo("Trace: " + n.Owner.name + " picture (" + why + ")");
            try { Nav.TraceDump(n.Owner.name, n.Trail, n.TrailKind, n.TraceGoal); } catch (Exception e) { Plugin.Log.LogError("Trace: " + e.Message); }
        }

        // ---------- feelers ----------
        // Pathing (melee NPCs always, gunmen with [Brain] ShooterPathing): FeelerCount capsule sweeps (radius ~0.3 m, knee to chest, so a
        // post, a tyre, a rock or a fence bar anywhere across the body counts) over +-FeelerAngle around the direction to the target, on the
        // bumper layers plus Item. Each direction is scored by the progress it makes toward the target over the feeler length (a long free
        // detour beats a short free straight line only when it pays), minus a penalty for ending near a wall. The moment the straight line is
        // blocked the NPC commits to a side (the freer half of the fan; a tie goes with the obstacle's facing) for SideLock seconds and until the
        // straight line has been clear twice in a row: the other half of the fan is penalised, so it can only choose how hard to turn, never
        // dither left-right into the thing. Boxed in (every feeler short), it follows the obstacle's tangent on the committed side. No progress
        // toward the target for 5 s flips the side once, then it rests (Rest mode) and starts over.
        // Gunmen without ShooterPathing keep the plain rays (one height, bumper layers, angle-scored) of 0.7.0.
        private static readonly float[] _free = new float[32];
        private static readonly Vector3[] _normals = new Vector3[32];
        private static readonly Collider[] _touch = new Collider[16];
        private static readonly Vector3[] _touchAway = new Vector3[16];   // per touching collider: the horizontal direction from it to the body (unit), or zero

        private static void Steer(Npc n, Vector3 toTarget, float dist, float now, Vector3 toReal)
        {
            int count = Mathf.Clamp(Plugin.FeelerCount.Value, 3, 31);
            if (count % 2 == 0) count++;
            float half = Mathf.Clamp(Plugin.FeelerAngle.Value, 10f, 170f);
            if (_angles.Length != count)
            {
                _angles = new float[count];
                for (int i = 0; i < count; i++) _angles[i] = -half + half * 2f * i / (count - 1);   // symmetric, 0 in the middle
            }
            bool adv = !n.Ranged || Plugin.ShooterPathing.Value || n.ToGhost || n.OnNav;   // on a map route everyone uses the body-wide feelers
            float len = Mathf.Max(0.5f, n.Ranged && !n.ToGhost ? Plugin.FeelerLength.Value : Plugin.MeleeFeelerLength.Value);
            if (dist < len && !n.OnNav) len = Mathf.Max(0.5f, dist);     // close to the target: don't "see" it as a wall (a map waypoint is no object)
            Vector3 origin = n.Col != null ? n.Col.bounds.center : n.T.position;
            float targetYaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            Transform troot = n.Target.Value != null ? n.Target.Value.transform.root : null;
            bool blockedMem = now < n.BlockedUntil;
            Vector3 right = Quaternion.Euler(0f, targetYaw, 0f) * Vector3.right;
            int mask = adv ? PathMask : Mask;
            float radius = 0.3f;
            Vector3 p1 = origin, p2 = origin;
            if (adv)
            {
                // the swept capsule: a little wider than the body, from ankle height (5 cm) to chest, so a brazier, a tyre or a fence bar that
                // the body would clip registers (small animals: whatever fits between their collider's top and bottom)
                if (n.Col != null)
                {
                    var b = n.Col.bounds;
                    radius = Mathf.Clamp(Mathf.Min(b.extents.x, b.extents.z) * 1.15f, 0.12f, 0.45f);
                    float lo = b.min.y + Mathf.Min(0.05f, b.size.y * 0.05f) + radius, hi = b.min.y + b.size.y * 0.7f - radius;   // from 5 cm above the feet: a brazier lip, a kerb
                    if (hi < lo) hi = lo;
                    p1 = new Vector3(b.center.x, lo, b.center.z); p2 = new Vector3(b.center.x, hi, b.center.z);
                }
            }
            RaycastHit hit;
            float feetY = n.Col != null ? n.Col.bounds.min.y : n.T.position.y - 1f;
            int centre = count / 2;
            int touching = 0;
            if (adv)
            {
                // a sweep ignores whatever overlaps its start, so the thing the body is pressed against would be invisible: block the
                // directions toward anything already touching the swept capsule
                touching = Physics.OverlapCapsuleNonAlloc(p1, p2, radius, _touch, mask, QueryTriggerInteraction.Ignore);
                for (int k = 0; k < touching; k++)
                {
                    var c = _touch[k]; _touchAway[k] = Vector3.zero;
                    if (c == null || c.transform.root == n.T || (troot != null && c.transform.root == troot)) { _touch[k] = null; continue; }
                    Vector3 away; float awayY;
                    if (!TouchDir(n, c, origin, out away, out awayY)) { _touch[k] = null; continue; }
                    if (Nav.IsFloorCollider(c) && awayY <= feetY + 0.3f) { _touch[k] = null; continue; }      // the ground under the feet (terrain, a cave floor)
                    _touchAway[k] = away;
                }
            }
            for (int i = 0; i < count; i++)
            {
                float yaw = targetYaw + _angles[i];
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                float free = len; Vector3 normal = Vector3.zero;
                for (int k = 0; k < touching; k++)
                {
                    if (_touch[k] == null) continue;
                    Vector3 away = _touchAway[k];
                    if (away.sqrMagnitude < 0.0001f) continue;
                    if (Vector3.Angle(dir, -away) < 70f) { free = 0f; normal = away; }
                }
                if (free <= 0f) { _free[i] = 0f; _normals[i] = normal; _blocks[i] = 1f; continue; }
                bool hitSomething = adv ? Physics.CapsuleCast(p1, p2, radius, dir, out hit, len, mask, QueryTriggerInteraction.Ignore)
                                        : Physics.Raycast(origin, dir, out hit, len, mask, QueryTriggerInteraction.Ignore);
                if (hitSomething && (troot == null || hit.collider.transform.root != troot) && hit.collider.transform.root != n.T
                    && !(adv && Nav.IsFloor(hit.collider, hit.normal, hit.point.y, feetY)))      // gentle ground ahead is not a wall (terrain or a cave floor rising)
                { free = Mathf.Max(0f, hit.distance); normal = hit.normal; }
                _free[i] = free; _normals[i] = normal; _blocks[i] = 1f - free / len;
            }

            if (adv)
            {
                bool straightBlocked = _free[centre] < len * 0.9f;
                { float fl0 = 0f, fr0 = 0f; for (int i = 0; i < count; i++) { if (_angles[i] < 0f) fl0 += _free[i]; else if (_angles[i] > 0f) fr0 += _free[i]; } n.FreeLeft = fl0; n.FreeRight = fr0; }
                if (straightBlocked && !n.HasWaypoint && !n.OnNav && now >= n.NextScout)
                {
                    if (Scout(n, origin, p1, p2, radius, mask, toReal, troot, now)) { n.Side = 0; n.Flipped = false; n.NextScout = now + 0.5f * R; }
                    else n.NextScout = now + 2.5f * R;        // nothing found: boxed in - don't burn 80 casts twice a second on it
                }
                if (n.HasWaypoint) { n.Side = 0; n.Flipped = false; }
                else if (n.Side == 0)
                {
                    if (straightBlocked)
                    {
                        float fl = 0f, fr = 0f;
                        for (int i = 0; i < count; i++) { if (_angles[i] < 0f) fl += _free[i]; else if (_angles[i] > 0f) fr += _free[i]; }
                        int side;
                        if (fr > fl * 1.15f) side = 1;
                        else if (fl > fr * 1.15f) side = -1;
                        else
                        {
                            float facing = Vector3.Dot(_normals[centre], right);     // the obstacle's face leans right -> go right
                            side = Mathf.Abs(facing) > 0.05f ? (facing > 0f ? 1 : -1) : (UnityEngine.Random.value < 0.5f ? -1 : 1);
                        }
                        n.Side = side; n.SideUntil = now + SideLock * R; n.ClearLooks = 0;
                        if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " goes around on the " + (side > 0 ? "right" : "left") + " (free L " + fl.ToString("0.0") + " / R " + fr.ToString("0.0") + ")");
                    }
                }
                else
                {
                    if (!straightBlocked) n.ClearLooks++; else n.ClearLooks = 0;
                    if (n.ClearLooks >= 2 && now >= n.SideUntil) { n.Side = 0; n.Flipped = false; }
                }
            }
            else n.Side = 0;

            int best = -1; float bestScore = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                float yaw = targetYaw + _angles[i];
                float score;
                if (adv)
                {
                    Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                    Vector3 reach = dir * _free[i];
                    float dAfter = (toTarget - reach).magnitude;
                    score = (dist - dAfter) / len - _blocks[i] * 1.0f;        // net progress toward the target, less for ending at a wall
                    if (n.Side != 0 && Mathf.Sign(_angles[i]) == -n.Side && _angles[i] != 0f) score -= 1.0f;
                }
                else score = Mathf.Cos(_angles[i] * Mathf.Deg2Rad) - _blocks[i] * 2.5f;
                if (blockedMem && Mathf.Abs(Mathf.DeltaAngle(yaw, n.BlockedYaw)) < 50f) score -= 2f;
                if (n.HasHeading && Mathf.Abs(Mathf.DeltaAngle(yaw, n.Heading)) < 12f) score += 0.1f;   // no flicker between near-equal rays
                _scores[i] = score;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (Plugin.DropCheck.Value && best >= 0)
            {
                // a short look for ground 1.5 m along the winner; a cliff or roof edge there -> the next best direction
                for (int tries = 0; tries < 3 && best >= 0; tries++)
                {
                    Vector3 dir = Quaternion.Euler(0f, targetYaw + _angles[best], 0f) * Vector3.forward;
                    Vector3 ahead = origin + dir * Mathf.Min(1.5f, len);
                    if (Physics.Raycast(ahead, Vector3.down, 4f, GroundMask, QueryTriggerInteraction.Ignore)) break;
                    _scores[best] = float.MinValue;
                    int nb = -1; float nbs = float.MinValue;
                    for (int i = 0; i < count; i++) if (_scores[i] > nbs) { nbs = _scores[i]; nb = i; }
                    best = nb;
                }
            }
            if (best < 0) best = centre;
            float heading = targetYaw + _angles[best];

            if (adv && n.Side != 0)
            {
                // boxed in (every feeler short): follow the obstacle's surface on the committed side, pushed off it a little when very close
                bool boxed = true;
                for (int i = 0; i < count; i++) if (_free[i] > len * 0.6f) { boxed = false; break; }
                if (boxed && _normals[centre] != Vector3.zero)
                {
                    Vector3 nrm = _normals[centre]; nrm.y = 0f;
                    if (nrm.sqrMagnitude > 0.001f)
                    {
                        nrm.Normalize();
                        Vector3 tangent = Vector3.Cross(Vector3.up, nrm);
                        if (Vector3.Dot(tangent, right * n.Side) < 0f) tangent = -tangent;
                        Vector3 follow = tangent + nrm * (_free[centre] < 1f ? 0.5f : 0.15f);
                        heading = Mathf.Atan2(follow.x, follow.z) * Mathf.Rad2Deg;
                        // a corner: the way along the wall is blocked too -> turn around now, don't wait for the no-progress timer
                        RaycastHit ch;
                        if (Physics.CapsuleCast(p1, p2, radius, tangent, out ch, 1.2f, mask, QueryTriggerInteraction.Ignore) && ch.collider.transform.root != n.T && (troot == null || ch.collider.transform.root != troot))
                        {
                            n.Side = -n.Side; n.SideUntil = now + SideLock * 2f * R; n.ClearLooks = 0; n.NextScout = 0f;
                            heading = Mathf.Atan2(-tangent.x, -tangent.z) * Mathf.Rad2Deg;
                            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " dead end, turns around");
                        }
                    }
                }
            }
            heading = AvoidFriends(n, heading, origin, p1, p2, radius, adv ? mask : PathMask, now);
            n.Heading = heading;
            n.HasHeading = true;
        }

        // Other NPCs are not in the feelers' mask (Actor layer): without this they ran into each other, the bump counted as "stuck" and
        // switched the map off for 10 s. Now, for every NPC within 3 m:
        // - ahead (+-60 deg) and going the same way (a brain NPC moving within 45 deg of this heading): queue behind it - slow down, never push;
        // - ahead and standing / coming the other way, within 2.5 m: pass it on one side (the side away from it; dead ahead: decided by
        //   instance id so two NPCs pick opposite sides), the side kept 1 s; if that way is blocked, the other side; neither: wait;
        //   a brain NPC standing still (hold / rest / search) within 1.6 m steps aside for 0.6 s;
        // - beside within 1 m: a small push apart.
        private static float AvoidFriends(Npc n, float heading, Vector3 origin, Vector3 p1, Vector3 p2, float radius, int mask, float now)
        {
            n.SpeedScale = 1f;
            Vector3 me = n.T.position;
            Vector3 fwd = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
            var tgt = n.Target.Value; Transform troot = tgt != null ? tgt.transform.root : null;
            float push = 0f; bool passing = false;
            string myTag = Plugin.FriendsPassThrough.Value ? Senses.TagOf(n.Owner) : null;
            foreach (var a in Senses.AllAgents)
            {
                if (a.Owner == null || a.T == n.T || a.T.parent != null || (troot != null && a.T == troot)) continue;
                if (myTag != null && a.Tag == myTag) continue;      // same faction: walks through them (Passthrough)
                Vector3 rel = a.T.position - me;
                if (Mathf.Abs(rel.y) > 2f) continue;
                rel.y = 0f;
                float d = rel.magnitude;
                if (d > 3f || d < 0.01f) continue;
                float ang = Vector3.SignedAngle(fwd, rel, Vector3.up);      // > 0: it is on the right
                if (Mathf.Abs(ang) > 60f)
                {
                    if (d < 1f) push += (ang > 0f ? -1f : 1f) * 12f * (1f - d);
                    continue;
                }
                Npc o; _npcs.TryGetValue(a.Owner.GetInstanceID(), out o);
                bool oMoving = o != null && o.Rb != null && o.Mode != Mode.Off && o.Mode != Mode.Hold && o.Mode != Mode.Rest && o.Mode != Mode.Search && o.Mode != Mode.Walk
                               && new Vector3(o.Rb.velocity.x, 0f, o.Rb.velocity.z).sqrMagnitude > 1f;
                if (oMoving && o.HasHeading && Mathf.Abs(Mathf.DeltaAngle(o.Heading, heading)) < 45f)
                {
                    float s = Mathf.Clamp((d - 0.9f) / 1.5f, 0.2f, 1f);      // queue: never push the one in front
                    if (s < n.SpeedScale) n.SpeedScale = s;
                    continue;
                }
                if (d > 2.5f) continue;
                int side;
                if (now < n.AvoidUntil && n.AvoidSide != 0) side = n.AvoidSide;
                else if (Mathf.Abs(ang) < 6f) side = n.Owner.GetInstanceID() < a.Owner.GetInstanceID() ? 1 : -1;
                else side = ang > 0f ? -1 : 1;
                n.AvoidSide = side; n.AvoidUntil = now + 1f; passing = true;
                push += side * Mathf.Lerp(55f, 20f, d / 2.5f);
                if (o != null && !oMoving && d < 1.6f && (o.Mode == Mode.Hold || o.Mode == Mode.Rest || o.Mode == Mode.Search) && !Idle.SearchWalking(o.Owner))
                {
                    o.MakeWayDir = Vector3.Cross(Vector3.up, fwd) * -side;     // away from the side the passer takes
                    o.MakeWayUntil = now + 0.6f;
                }
            }
            if (push == 0f) return heading;
            push = Mathf.Clamp(push, -70f, 70f);
            float h = heading + push;
            if (!passing || WayFree(n, h, origin, p1, p2, radius, mask)) return h;
            float other = heading - push;                                   // that side is a wall: the other side
            if (WayFree(n, other, origin, p1, p2, radius, mask)) { n.AvoidSide = -n.AvoidSide; return other; }
            n.SpeedScale = Mathf.Min(n.SpeedScale, 0.2f);                   // neither: wait for it to move
            return heading;
        }

        private static bool WayFree(Npc n, float yaw, Vector3 origin, Vector3 p1, Vector3 p2, float radius, int mask)
        {
            Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            RaycastHit h;
            if (!Physics.CapsuleCast(p1, p2, radius, dir, out h, 1.2f, mask, QueryTriggerInteraction.Ignore)) return true;
            if (h.collider.transform.root == n.T) return true;
            float feetY = n.Col != null ? n.Col.bounds.min.y : n.T.position.y - 1f;
            return Nav.IsFloor(h.collider, h.normal, h.point.y, feetY);
        }

        // a bump into another NPC is no reason to call itself stuck
        private static bool FriendInTheWay(Npc n)
        {
            Vector3 me = n.T.position, fwd = n.T.forward; fwd.y = 0f;
            string myTag = Plugin.FriendsPassThrough.Value ? Senses.TagOf(n.Owner) : null;
            foreach (var a in Senses.AllAgents)
            {
                if (a.Owner == null || a.T == n.T || a.T.parent != null) continue;
                if (myTag != null && a.Tag == myTag) continue;      // same faction: no bump possible (Passthrough)
                Vector3 rel = a.T.position - me;
                if (Mathf.Abs(rel.y) > 2f) continue;
                rel.y = 0f;
                if (rel.sqrMagnitude > 1.3f * 1.3f) continue;
                if (Vector3.Angle(fwd, rel) < 75f) return true;
            }
            return false;
        }

        // The L-corner problem: a 2.5 m fan cannot tell which end of a wall leads to the target, and the wrong side is a dead end. When the
        // straight line is blocked the NPC scouts: 12 long sweeps (ScoutLength m) around the direction to the target; the end of each free
        // stretch is a candidate corner, kept only if the line from there to the target is clear; the corner with the shortest path
        // (there + from there) becomes a waypoint the feelers steer to until it is reached, the straight line clears, or it times out.
        private static readonly float[] _scoutAngles = { -15f, 15f, -40f, 40f, -65f, 65f, -90f, 90f, -115f, 115f, -140f, 140f };
        private const float ScoutLength = 10f;

        private static bool Scout(Npc n, Vector3 origin, Vector3 p1, Vector3 p2, float radius, int mask, Vector3 toReal, Transform troot, float now)
        {
            float yaw0 = Mathf.Atan2(toReal.x, toReal.z) * Mathf.Rad2Deg;
            float dReal = toReal.magnitude;
            float feetY = n.Col != null ? n.Col.bounds.min.y : n.T.position.y - 1f;
            Vector3 chestOff = p2 - origin;        // scouting at chest height
            Vector3 targetChest = origin + toReal + Vector3.up * 0.3f;
            bool blockedMem = now < n.BlockedUntil;
            float bestCost = float.MaxValue; Vector3 bestEnd = Vector3.zero; float bestAngle = 0f;
            RaycastHit hit;
            for (int k = 0; k < _scoutAngles.Length; k++)
            {
                float yaw = yaw0 + _scoutAngles[k];
                if (blockedMem && Mathf.Abs(Mathf.DeltaAngle(yaw, n.BlockedYaw)) < 30f) continue;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                float free = ScoutLength;
                if (Physics.CapsuleCast(p1, p2, radius, dir, out hit, ScoutLength, mask, QueryTriggerInteraction.Ignore)
                    && (troot == null || hit.collider.transform.root != troot) && hit.collider.transform.root != n.T
                    && !Nav.IsFloor(hit.collider, hit.normal, hit.point.y, feetY))
                    free = hit.distance;
                // walk the free stretch from near to far: the first point with a clear line to the target is the corner
                float reach = free - radius - 0.3f;
                for (float along = 1.5f; along <= reach; along += 2.5f)
                {
                    Vector3 end = origin + dir * along;
                    Vector3 toT = targetChest - (end + chestOff);
                    if (Physics.Raycast(end + chestOff, toT.normalized, out hit, toT.magnitude, mask, QueryTriggerInteraction.Ignore)
                        && (troot == null || hit.collider.transform.root != troot) && hit.collider.transform.root != n.T
                        && !Nav.IsFloor(hit.collider, hit.normal, hit.point.y, feetY)) continue;
                    float cost = along + (origin + toReal - end).magnitude;
                    if (cost < bestCost) { bestCost = cost; bestEnd = end; bestAngle = _scoutAngles[k]; }
                    break;      // farther points on this ray only add path
                }
            }
            if (bestCost == float.MaxValue) return false;
            n.Waypoint = bestEnd; n.HasWaypoint = true; n.WaypointUntil = now + 8f * R;
            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " scouts a corner " + (bestAngle < 0f ? "left" : "right") + " " + Mathf.Abs(bestAngle) + " deg, " + (bestEnd - origin).magnitude.ToString("0.0") + " m away (path " + bestCost.ToString("0.0") + " m, straight " + dReal.ToString("0.0") + ")");
            return true;
        }

        // the straight line to the target is free for the body (one sweep)
        private static bool DirectClear(Npc n, Vector3 tp, float dist)
        {
            if (n.Col == null) return true;
            var b = n.Col.bounds;
            float radius = Mathf.Clamp(Mathf.Min(b.extents.x, b.extents.z) * 1.15f, 0.12f, 0.45f);
            float lo = b.min.y + Mathf.Min(0.05f, b.size.y * 0.05f) + radius, hi = b.min.y + b.size.y * 0.7f - radius;
            if (hi < lo) hi = lo;
            Vector3 p1 = new Vector3(b.center.x, lo, b.center.z), p2 = new Vector3(b.center.x, hi, b.center.z);
            Vector3 dir = tp - n.T.position; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return true;
            dir.Normalize();
            RaycastHit hit;
            Transform troot = n.Target.Value != null ? n.Target.Value.transform.root : null;
            if (!Physics.CapsuleCast(p1, p2, radius, dir, out hit, Mathf.Max(0.1f, dist - 0.5f), PathMask, QueryTriggerInteraction.Ignore)) return true;
            if (troot != null && hit.collider.transform.root == troot) return true;
            if (hit.collider.transform.root == n.T) return true;
            return Nav.IsFloor(hit.collider, hit.normal, hit.point.y, b.min.y);
        }

        // moving modes: is the NPC getting anywhere? (called from Think) - 5 s without coming nearer flips the side once, then rests
        private static bool Progress(Npc n, float d, float now)
        {
            if (Senses.Blown(n.Owner)) { n.NoProgressSince = now; n.BestDist = Mathf.Min(n.BestDist, d); return true; }   // pushed by a tornado: no judgement
            if (d < n.BestDist - 0.5f) { n.BestDist = d; n.NoProgressSince = now; return true; }
            if (now - n.NoProgressSince < NoProgressSeconds * R) return true;
            n.NoProgressSince = now;
            n.HasWaypoint = false; n.NextScout = 0f;
            if (!n.Flipped)
            {
                n.Flipped = true;
                n.Side = n.Side != 0 ? -n.Side : (UnityEngine.Random.value < 0.5f ? -1 : 1);
                n.SideUntil = now + SideLock * 2f * R; n.ClearLooks = 0;
                if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " gets nowhere, tries the " + (n.Side > 0 ? "right" : "left"));
                return true;
            }
            n.Flipped = false; n.Side = 0; n.BestDist = float.MaxValue;
            n.ModeUntil = now + RestSeconds * R;
            if (n.ToGhost && Senses.CannotReach(n.Owner)) { n.NextLookTurn = 0f; SetMode(n, Mode.Search, "gets nowhere toward the ghost"); return false; }
            SetMode(n, Mode.Rest, "gets nowhere, rests");
            return false;
        }

        private const float SideLock = 1.2f, NoProgressSeconds = 2.5f, RestSeconds = 1f, StuckWindow = 10f;

        private static bool LineOfSight(Npc n, GameObject target, Vector3 tp)
        {
            Vector3 eye = n.Col != null ? n.Col.bounds.center + Vector3.up * n.Col.bounds.extents.y * 0.6f : n.T.position + Vector3.up * 1.5f;
            Vector3 chest = tp + Vector3.up * 1f;
            RaycastHit hit;
            if (!Physics.Linecast(eye, chest, out hit, LosMask, QueryTriggerInteraction.Ignore)) return true;
            return hit.collider.transform.root == target.transform.root;
        }

        // ---------- modes ----------
        private static void SetMode(Npc n, Mode m, string why)
        {
            Mode was = n.Mode;
            n.Mode = m;
            if (Tracing && m != was)
            {
                if ((m == Mode.Chase || m == Mode.Advance) && (was == Mode.Off || was == Mode.Search)) { n.Trail.Clear(); n.TrailKind.Clear(); n.NextTrail = 0f; n.NextTraceDump = 0f; }
                else if ((m == Mode.Search || m == Mode.Off) && was != Mode.Hold) { n.NextTraceDump = 0f; TraceDump(n, m == Mode.Search ? "reached the goal" : "chase over"); }
                else if (m == Mode.Rest) TraceDump(n, "rests");
            }
            if (m == Mode.Chase || m == Mode.Advance) { n.HasHeading = false; if (was != Mode.Chase && was != Mode.Advance && was != Mode.BackUp) { n.BestDist = float.MaxValue; n.NoProgressSince = Time.time; n.Flipped = false; n.Side = 0; n.HasWaypoint = false; } }
            bool wasStill = was == Mode.Hold || was == Mode.Rest || was == Mode.Search || was == Mode.Walk, still = m == Mode.Hold || m == Mode.Rest || m == Mode.Search || m == Mode.Walk;
            if (m == Mode.Off) { if (wasStill) Move(n, n.Target != null && n.Target.Value != null); }   // (1.4.9) back to the game's run only with a target left - no target: idle, not a run on the spot
            else if (still && !wasStill) Move(n, false);
            else if (!still && wasStill) Move(n, true);
            if (still) n.OnNav = false;
            if (m == Mode.Hold)
            {
                Aim(n);
                if (was != Mode.Hold && n.Ranged && Plugin.CrouchChance.Value > 0f)
                {
                    float t = Time.time;
                    if (t - n.CrouchRolledAt > 5f) { n.CrouchRoll = UnityEngine.Random.Range(0f, 100f) < Plugin.CrouchChance.Value; n.CrouchRolledAt = t; }
                    if (n.CrouchRoll) Crouch(n, true);
                }
            }
            else { Unfreeze(n); Crouch(n, false); }
            if (Plugin.BrainLog.Value && (m != was))
                Plugin.Log.LogInfo("Brain: " + n.Owner.name + " " + was + " -> " + m + " (" + why + ", " + n.Dist.ToString("0") + " m" + (n.Ranged ? ", " + n.Kind : "") + ")");
        }

        // Movement FSM: Animal_Run (run animation + forward velocity) or Animal_Idle (idle animation, velocity zero), only while the Attack
        // FSM is in its chase state (during a burst / swing the Attack FSM re-sends Animal_Run on its way back; see BeforeSendEvent).
        private static void Move(Npc n, bool run)
        {
            if (n.Movement == null || n.Attack == null || n.Attack.Fsm == null) return;
            string s = n.Attack.Fsm.ActiveStateName;
            if (s != "trigger" && s != "run") return;
            n.Movement.SendEvent(run ? "Animal_Run" : "Animal_Idle");
        }

        // A holding gunman keeps the gun up: the Movement FSM's shooting animation (attack 2 / attack 4: a 0.2-0.6 s clip that starts with the gun
        // raised) is put on its first frame and the Animator frozen there. The burst plays it from the start again (Unfreeze in Tick), and after
        // the burst the Attack FSM's Animal_Run comes back as Animal_Idle (BeforeSendEvent) followed by this pose again.
        private static void Aim(Npc n)
        {
            if (!Plugin.AimPose.Value || n.Anim == null || string.IsNullOrEmpty(n.AimState)) return;
            try
            {
                n.Anim.Play(n.AimState, 0, 0f);
                n.Anim.speed = 0f;
                n.Frozen = true;
            }
            catch (Exception) { n.AimState = null; }
        }

        private static void Unfreeze(Npc n)
        {
            if (!n.Frozen) return;
            n.Frozen = false;
            if (n.Anim != null) n.Anim.speed = 1f;
        }

        // ---------- crouch ----------
        // There is no crouch animation for NPCs, so a holding gunman that rolled [Brain] CrouchChance kneels by code: every LateUpdate (after the
        // Animator wrote its pose) the hips are lowered and the legs re-posed on top of the animation - left leg forward with the shin vertical,
        // right knee on the ground with the shin folded back (the same bone-swing technique as Apocapatrol's seated pose). The root capsule is
        // shortened to the kneeling height so bullets aimed at the empty air above him miss; the head's own trigger collider follows the bone.
        private static bool FindLegs(Npc n)
        {
            if (n.CanCrouch != 0) return n.CanCrouch > 0;
            n.CanCrouch = -1;
            if (n.Owner == null) return false;
            var all = n.Owner.GetComponentsInChildren<Transform>(true);
            n.Hips = Bone(all, "Hips"); n.LUpLeg = Bone(all, "LeftUpLeg"); n.LLeg = Bone(all, "LeftLeg");
            n.RUpLeg = Bone(all, "RightUpLeg"); n.RLeg = Bone(all, "RightLeg");
            if (n.Hips == null || n.LUpLeg == null || n.LLeg == null || n.RUpLeg == null || n.RLeg == null) return false;
            n.Capsule = n.Owner.GetComponent<CapsuleCollider>();
            if (n.Capsule != null) { n.CapHeight = n.Capsule.height; n.CapCenter = n.Capsule.center; }
            n.CanCrouch = 1;
            return true;
        }

        private static Transform Bone(Transform[] all, string bone)
        {
            foreach (var t in all)
            {
                string s = t.name;
                int i = s.LastIndexOf(':'); if (i < 0) i = s.LastIndexOf('_');
                if ((i >= 0 ? s.Substring(i + 1) : s) == bone) return t;
            }
            return null;
        }

        private static void Crouch(Npc n, bool on)
        {
            if (on == n.Crouched) return;
            if (on)
            {
                if (!FindLegs(n)) return;
                // kneeling: the hips end up about one thigh length above the ground (the right thigh stands on its knee)
                float thigh = Vector3.Distance(n.LUpLeg.position, n.LLeg.position);
                float ground = n.Col != null ? n.Col.bounds.min.y : n.T.position.y - 1f;
                float hip = n.Hips.position.y - ground;
                n.Drop = Mathf.Clamp(hip - thigh * 0.95f, 0.2f, 0.8f);
                n.Crouched = true; n.PoseCaptured = false;
                if (n.Capsule != null && n.CapHeight > 0f)
                {
                    float bottom = n.CapCenter.y - n.CapHeight * 0.5f;
                    float h = Mathf.Max(n.Capsule.radius * 2f, n.CapHeight - n.Drop);
                    n.Capsule.height = h;
                    n.Capsule.center = new Vector3(n.CapCenter.x, bottom + h * 0.5f, n.CapCenter.z);
                }
                if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " kneels (hips down " + n.Drop.ToString("0.00") + " m)");
            }
            else
            {
                n.Crouched = false;
                if (n.Capsule != null && n.CapHeight > 0f) { n.Capsule.height = n.CapHeight; n.Capsule.center = n.CapCenter; }
            }
        }

        private static void Swing(Transform bone, float degrees, Vector3 axis)
        {
            if (bone != null) bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }

        // Runner.LateUpdate: re-pose the crouched gunmen on top of the animation. The standing leg pose is captured once (the first frame
        // after the kneel starts, i.e. the frozen aim pose) and the kneel is rebuilt from it every frame - absolute, so it neither drifts when
        // the Animator stops writing (culled off screen) nor fights the burst animation's upper body.
        public static void LateTick()
        {
            if (_npcs.Count == 0) return;
            foreach (var kv in _npcs)
            {
                var n = kv.Value;
                if (!n.Crouched || n.Owner == null) continue;
                if (n.Hips == null || n.LUpLeg == null || n.LLeg == null || n.RUpLeg == null || n.RLeg == null) { n.Crouched = false; continue; }
                if (!n.PoseCaptured)
                {
                    n.PoseCaptured = true;
                    n.HipsLocal = n.Hips.localPosition;
                    n.LUpLegRot = n.LUpLeg.localRotation; n.LLegRot = n.LLeg.localRotation;
                    n.RUpLegRot = n.RUpLeg.localRotation; n.RLegRot = n.RLeg.localRotation;
                }
                Vector3 right = n.T.right;
                var hp = n.Hips.parent;
                n.Hips.localPosition = n.HipsLocal + (hp != null ? hp.InverseTransformVector(Vector3.down * n.Drop) : Vector3.down * n.Drop);
                n.LUpLeg.localRotation = n.LUpLegRot; n.LLeg.localRotation = n.LLegRot;
                n.RUpLeg.localRotation = n.RUpLegRot; n.RLeg.localRotation = n.RLegRot;
                Swing(n.LUpLeg, -CrouchFrontThigh, right); Swing(n.LLeg, CrouchFrontKnee, right);
                Swing(n.RUpLeg, CrouchBackThigh, right); Swing(n.RLeg, CrouchBackKnee, right);
            }
        }

        // the kneeling pose, degrees about the body's right axis (minus = forward/up for a thigh; plus bends a knee back)
        private const float CrouchFrontThigh = 90f, CrouchFrontKnee = 90f, CrouchBackThigh = 15f, CrouchBackKnee = 100f;

        // [Debug] BrainLog, at a stuck: what is in front of the body (any layer, triggers too) - names the thing the feelers missed
        private static void LogAhead(Npc n)
        {
            try
            {
                Vector3 origin = n.Col != null ? new Vector3(n.Col.bounds.center.x, n.Col.bounds.min.y + 0.1f, n.Col.bounds.center.z) : n.T.position;
                var hits = Physics.RaycastAll(origin, n.T.forward, 1.5f, ~0, QueryTriggerInteraction.Collide);
                var sb = new System.Text.StringBuilder();
                foreach (var h in hits)
                {
                    if (h.collider == null || h.collider.transform.root == n.T) continue;
                    sb.Append(h.collider.name).Append(" [").Append(LayerMask.LayerToName(h.collider.gameObject.layer)).Append(h.collider.isTrigger ? ", trigger" : "")
                      .Append(", ").Append(h.distance.ToString("0.0")).Append(" m] ");
                }
                Plugin.Log.LogInfo("Brain: " + n.Owner.name + " ahead at ankle height: " + (sb.Length > 0 ? sb.ToString() : "nothing"));
                // what touches the body right now (the body capsule grown by 0.15 m, any layer, triggers too)
                if (n.Col != null)
                {
                    var b = n.Col.bounds;
                    float r = Mathf.Max(b.extents.x, b.extents.z) + 0.15f;
                    var touching = Physics.OverlapCapsule(new Vector3(b.center.x, b.min.y + r, b.center.z), new Vector3(b.center.x, b.max.y - r, b.center.z), r, ~0, QueryTriggerInteraction.Collide);
                    sb.Length = 0;
                    foreach (var c in touching)
                    {
                        if (c == null || c.transform.root == n.T) continue;
                        Vector3 cp = c.ClosestPoint(b.center);
                        Vector3 rel = n.T.InverseTransformPoint(cp);
                        sb.Append(c.name).Append(" [").Append(LayerMask.LayerToName(c.gameObject.layer)).Append(c.isTrigger ? ", trigger" : "").Append(", ")
                          .Append(c.GetType().Name).Append(", at ").Append(rel.x.ToString("0.0")).Append("/").Append(rel.y.ToString("0.0")).Append("/").Append(rel.z.ToString("0.0")).Append("] ");
                    }
                    Plugin.Log.LogInfo("Brain: " + n.Owner.name + " touching: " + (sb.Length > 0 ? sb.ToString() : "nothing") + " | vel " + (n.Rb != null ? n.Rb.velocity.magnitude.ToString("0.0") : "?") + " | fan free L " + n.FreeLeft.ToString("0.0") + " R " + n.FreeRight.ToString("0.0") + " side " + n.Side);
                }
            }
            catch (Exception) { }
        }

        // a map waypoint is taken only when the first metre toward it is free for the body (walls, rock, baked props - not loose items or cars,
        // the feelers steer round those); otherwise this look runs without the map, as before maps existed
        private static readonly int NavGateMask = PathMask & ~((1 << 8) | (1 << 9));
        private static bool NavLegClear(Npc n, Vector3 next) { return NavLegClear(n, next, false); }
        private static bool NavLegClear(Npc n, Vector3 next, bool hopLeg)
        {
            if (n.Col == null) return true;
            var b = n.Col.bounds;
            float r = Mathf.Clamp(Mathf.Min(b.extents.x, b.extents.z), 0.12f, 0.35f);
            float lo = b.min.y + 0.25f + r, hi = b.min.y + b.size.y * 0.7f - r; if (hi < lo) hi = lo;
            Vector3 p1 = new Vector3(b.center.x, lo, b.center.z), p2 = new Vector3(b.center.x, hi, b.center.z);
            Vector3 d = next - n.T.position; d.y = 0f;
            float len = Mathf.Min(1f, d.magnitude);
            if (len < 0.05f) return true;
            RaycastHit h;
            if (!Physics.CapsuleCast(p1, p2, r, d.normalized, out h, len, NavGateMask, QueryTriggerInteraction.Ignore)) return true;
            if (h.collider.transform.root == n.T) return true;
            if (Nav.IsFloor(h.collider, h.normal, h.point.y, b.min.y)) return true;
            if (hopLeg && h.point.y - b.min.y <= Nav.HopStep + 0.1f) return true;   // the lip the map hops over, not a wall
            if (h.point.y - b.min.y <= 0.6f && !Physics.Raycast(new Vector3(b.center.x, b.min.y + 0.65f, b.center.z), d.normalized, h.distance + r + 0.3f, NavGateMask, QueryTriggerInteraction.Ignore))
                return true;                                                          // ankle-high clutter with nothing at the knee: stepped over
            n.LegBlock = h.collider.name + " [" + LayerMask.LayerToName(h.collider.gameObject.layer) + "] " + (h.point.y - b.min.y).ToString("0.00") + " m above the feet, " + h.distance.ToString("0.00") + " m ahead" + (hopLeg ? ", hop leg" : "");
            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " map waypoint blocked by " + n.LegBlock + ", steers without the map");
            return false;
        }

        private static void OnStuck(Npc n)
        {
            float now = Time.time;
            if (n.Mode == Mode.Hold || n.Mode == Mode.Rest || n.Mode == Mode.Search || n.Mode == Mode.Walk || n.Mode == Mode.BackUp || n.Mode == Mode.Off) return;   // standing still (or Idle's walk): the Unstuck FSM's "not moving" is no stuck
            if (Senses.Blown(n.Owner)) return;          // pushed by a tornado: not stuck
            if (now - n.FriendBumpsSince > 6f) { n.FriendBumpsSince = now; n.FriendBumps = 0; }
            if (FriendInTheWay(n) && ++n.FriendBumps <= 3)   // bumped into another NPC: pass it, keep the map (3 times in 6 s at most: a wall next to a friend is still a wall)
            {
                if (n.AvoidSide == 0 || now >= n.AvoidUntil) n.AvoidSide = UnityEngine.Random.value < 0.5f ? -1 : 1;
                n.AvoidUntil = now + 1.2f; n.NoProgressSince = now;
                return;
            }
            if (n.OnNav)
            {
                // stuck on a map route: hop / step back and keep the route; 3 times within 8 s -> full feelers for 3 s, then the map again
                if (now - n.NavStuckSince > 8f) { n.NavStuckSince = now; n.NavStucks = 0; }
                n.NavStucks++;
                n.BlockedYaw = n.T.eulerAngles.y; n.BlockedUntil = now + Mathf.Max(0f, Plugin.BlockedMemory.Value) * R;
                if (Plugin.BrainLog.Value) LogAhead(n);
                if (n.NavStucks < 3)
                {
                    if (TryHop(n, now, true)) return;
                    n.ModeUntil = now + 0.4f * R;
                    SetMode(n, Mode.BackUp, Plugin.BrainLog.Value ? "stuck on a map route at " + n.T.position.x.ToString("0.0") + "," + n.T.position.z.ToString("0.0") + ", a step back, keeps the route" : "");
                    return;
                }
                n.NavStucks = 0; n.NavOffUntil = now + 3f * R; n.OnNav = false;
                if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " stuck 3x on a map route at " + n.T.position.x.ToString("0.0") + "," + n.T.position.z.ToString("0.0") + ", full feelers for " + (3f * R).ToString("0") + " s");
            }
            if (now - n.FirstStuck > StuckWindow * R) { n.FirstStuck = now; n.Stucks = 0; }
            n.Stucks++;
            var target = n.Target != null ? n.Target.Value : null;
            if (n.Ranged && !n.ToGhost && target != null && n.Dist <= WeaponRanges.RangeOf(n.Kind) && LineOfSight(n, target, target.transform.position))
            {
                n.NextRecheck = now + Mathf.Max(0.1f, Plugin.HoldRecheckMax.Value) * R;
                SetMode(n, Mode.Hold, "stuck, shoots from here");
                return;
            }
            if (n.Stucks >= Mathf.Max(1, Plugin.StuckGiveUpCount.Value))
            {
                n.Stucks = 0;
                n.ModeUntil = now + RestSeconds * R;
                if (n.ToGhost && Senses.CannotReach(n.Owner)) { n.NextLookTurn = 0f; SetMode(n, Mode.Search, "stuck on the way to the ghost"); return; }
                SetMode(n, Mode.Rest, "stuck for good, rests");
                return;
            }
            float yaw = n.T.eulerAngles.y;
            n.BlockedYaw = yaw; n.BlockedUntil = now + Mathf.Max(0f, Plugin.StuckMemorySeconds.Value) * R;
            n.HasWaypoint = false; n.NextScout = 0f;
            if (!n.Ranged || Plugin.ShooterPathing.Value)
            {
                // the body hit something the feelers did not see (or saw too late): commit to the freer side now and keep it through the back-up
                if (n.Side == 0) n.Side = n.FreeRight >= n.FreeLeft ? 1 : -1;
                n.SideUntil = now + SideLock * 2f * R; n.ClearLooks = 0;
            }
            if (Plugin.BrainLog.Value) LogAhead(n);
            if (TryHop(n, now, true)) return;       // stuck: a low edge is hopped even when its face is sloped (only gentle terrain is not)
            n.ModeUntil = now + Mathf.Max(0.1f, Plugin.StuckBackupSeconds.Value) * R;
            SetMode(n, Mode.BackUp, Plugin.BrainLog.Value ? "stuck " + n.Stucks + "x, backs up" : "");
        }

        // Stuck on something low (a rock lip, a kerb, a pipe on the floor) with nothing above it: the game's own Unstuck would hop here (we
        // block its AddForce while steering) - give one small hop forward instead of backing up. At most one hop per 2 s.
        private static bool TryHop(Npc n, float now) { return TryHop(n, now, false); }
        private static bool TryHop(Npc n, float now, bool lip)
        {
            if (n.Rb == null || n.Col == null || now - n.LastHop < 2f) return false;
            var b = n.Col.bounds;
            Vector3 fwd = n.T.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) return false; fwd.Normalize();
            Vector3 low = new Vector3(b.center.x, b.min.y + 0.1f, b.center.z), knee = new Vector3(b.center.x, b.min.y + 0.55f, b.center.z);
            float reach = Mathf.Min(b.extents.x, b.extents.z) + 0.45f;
            RaycastHit h;
            if (!Physics.Raycast(low, fwd, out h, reach, PathMask, QueryTriggerInteraction.Ignore) || h.collider.transform.root == n.T) return false;
            // a slope the body walks up is no edge - but a lip (stuck at it, or the map's route hops it) is hopped unless it is gentle terrain
            bool gentle = h.normal.y >= Nav.WalkNormal || Nav.IsFloor(h.collider, h.normal, h.point.y, b.min.y);
            if (gentle && (!lip || h.collider.gameObject.layer == 14)) return false;
            if (Physics.Raycast(knee, fwd, reach + 0.2f, PathMask, QueryTriggerInteraction.Ignore)) return false;      // something higher: not a hop
            n.LastHop = now;
            n.Rb.AddForce(Vector3.up * 3.2f + fwd * 1.5f, ForceMode.VelocityChange);
            if (Plugin.BrainLog.Value) Plugin.Log.LogInfo("Brain: " + n.Owner.name + " hops over a low edge (" + h.collider.name + ")");
            return true;
        }

        // The horizontal direction from a touching collider to the body. Collider.ClosestPoint only works for convex shapes: for a
        // non-convex MeshCollider (cave rock, building walls) and terrain it returns the query point itself, so those use ComputePenetration
        // against the NPC's own (convex) collider instead.
        private static bool TouchDir(Npc n, Collider c, Vector3 origin, out Vector3 away, out float contactY)
        {
            away = Vector3.zero; contactY = origin.y;
            var mc = c as MeshCollider;
            bool convex = mc != null ? mc.convex : (c is BoxCollider || c is SphereCollider || c is CapsuleCollider);   // terrain (and anything else) -> penetration
            if (convex)
            {
                Vector3 cp = c.ClosestPoint(origin);
                contactY = cp.y;
                away = origin - cp; away.y = 0f;
                if (away.sqrMagnitude < 0.0001f) return false;
                away.Normalize();
                return true;
            }
            if (n.Col == null) return false;
            Vector3 dir; float dist;
            if (!Physics.ComputePenetration(n.Col, n.T.position, n.T.rotation, c, c.transform.position, c.transform.rotation, out dir, out dist)) return false;
            contactY = origin.y - 0.5f;             // a wall pressed against the body: never counted as the floor under the feet
            if (Mathf.Abs(dir.y) > 0.8f) { contactY = dir.y > 0f ? n.Col.bounds.min.y : origin.y; }   // pushed up = it is under the feet
            away = dir; away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) return false;
            away.Normalize();
            return true;
        }

        // (1.6.0) Idle: the brain has stood aside for its ghost walk (Mode Walk) - only then does Idle move the body
        internal static bool IsWalk(GameObject owner)
        {
            if (!On) return false;
            var n = Get(owner);
            return n != null && n.Mode == Mode.Walk;
        }

        // Aim decided to fire but the body is turned away (steering around something): face the target for a moment
        internal static void FaceTarget(GameObject owner, float seconds)
        {
            var n = Get(owner);
            if (n != null) n.FaceTargetUntil = Time.time + seconds;
        }

        // ---------- NPC registry ----------
        private static Npc Get(GameObject owner)
        {
            Npc n;
            return owner != null && _npcs.TryGetValue(owner.GetInstanceID(), out n) ? n : null;
        }

        // the first Movement SetVelocity of an NPC registers it (every ground NPC with a target runs that action every frame)
        private static Npc Of(GameObject owner)
        {
            if (owner == null) return null;
            int id = owner.GetInstanceID();
            Npc n;
            if (_npcs.TryGetValue(id, out n)) return n;
            if (_ignored.Contains(id)) return null;
            n = Make(owner);
            if (n == null) { _ignored.Add(id); return null; }
            _npcs[id] = n;
            return n;
        }

        private static Npc Make(GameObject owner)
        {
            if (owner.transform.parent != null) return null;         // seated in a car (Apocapatrol), or a part of something
            var rb = owner.GetComponent<Rigidbody>();
            if (rb == null || !rb.useGravity) return null;         // flyers (bats, wasps, Terror of the Night) have gravity off
            PlayMakerFSM attack = null, movement = null, detection = null, unstuck = null;
            foreach (var f in owner.GetComponents<PlayMakerFSM>())
            {
                if (f == null) continue;
                switch (f.FsmName)
                {
                    case "Attack": attack = f; break;
                    case "Movement": movement = f; break;
                    case "Detection": detection = f; break;
                    case "Unstuck": unstuck = f; break;
                }
            }
            if (attack == null || movement == null || detection == null || unstuck == null) return null;
            if (detection.Fsm == null || !detection.Fsm.Initialized) return null;    // try again next frame (not cached as ignored)
            var target = detection.FsmVariables.FindFsmGameObject("detectedObj");
            if (target == null) return null;
            var n = new Npc { Owner = owner, T = owner.transform, Rb = rb, Col = owner.GetComponent<Collider>(), Attack = attack, Movement = movement, Target = target };
            WeaponRanges.Kind kind;
            n.Ranged = WeaponRanges.GunKindOf(owner, out kind);
            n.Kind = kind;
            n.MovementFsm = movement;
            n.MadeAt = Time.time;
            if (n.Ranged) SetupRanged(n);
            n.Stagger = (_created++ % 10) * 0.01f;
            n.NextTick = Time.time + n.Stagger;
            return n;
        }

        // a gunman's aim pose (the Movement FSM's AttackRanged animator state); also run later for one whose gun showed up after its spawn
        private static void SetupRanged(Npc n)
        {
            var owner = n.Owner; var movement = n.MovementFsm;
            if (movement == null) return;

            n.Anim = owner.GetComponentInChildren<Animator>(true);
            try
            {
                var mf = movement.Fsm;
                if (mf != null && mf.States != null)
                    foreach (var st in mf.States)
                    {
                        if (st == null || st.Name != "AttackRanged" || st.Actions == null) continue;
                        foreach (var a in st.Actions)
                        {
                            var ap = a as AnimatorPlay;
                            if (ap != null && ap.stateName != null && !string.IsNullOrEmpty(ap.stateName.Value)) { n.AimState = ap.stateName.Value; break; }
                        }
                    }
            }
            catch (Exception e) { Plugin.Verbose("Brain: no aim pose for " + owner.name + ": " + e.Message); }
        
        }

        private static Npc NpcOf(Fsm fsm, string fsmName)
        {
            if (fsm == null || fsm.Name != fsmName) return null;
            var n = Get(fsm.GameObject);
            return n != null && n.Mode != Mode.Off && On ? n : null;
        }

        // ---------- hooks ----------
        // SetVelocity.DoSetVelocity (Movement FSM): the pedal
        public static bool BeforeSetVelocity(SetVelocity __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Movement" || !On) return true;
                var n = Of(fsm.GameObject);
                if (n == null || n.Mode == Mode.Off) return true;
                if (n.Mode == Mode.Walk) { n.CmdSpeed = 0f; return false; }      // (1.6.0) Idle drives the body: the game's pedal (run or stop) is skipped
                float z = __instance.z != null && !__instance.z.IsNone ? __instance.z.Value : (__instance.vector != null && !__instance.vector.IsNone ? __instance.vector.Value.z : 0f);
                if (z <= 0f) return true;      // the Idle / attack states' "stop": vanilla
                if (n.Rb == null) return true;
                float speed = n.Mode == Mode.BackUp ? -Mathf.Min(z, 2.5f) : (n.Mode == Mode.Hold || n.Mode == Mode.Rest || n.Mode == Mode.Search) ? 0f : z * n.SpeedScale;
                if (n.Mode == Mode.Chase || n.Mode == Mode.Advance)
                {
                    // deliberate movement only: no heading yet (the first think after the alert) -> stand; facing away from the heading -> slow
                    // down while turning (full speed within 30 deg, 40 % at 90, 10 % from 120: a big turn is made nearly on the spot, not as an arc)
                    if (!n.HasHeading) speed = 0f;
                    else speed *= TurnSpeedFactor(Mathf.Abs(Mathf.DeltaAngle(n.T.eulerAngles.y, n.Heading)));
                }
                n.CmdSpeed = Mathf.Max(0f, speed);
                Vector3 v = n.T.forward * speed;
                v.y = n.Rb.velocity.y;
                n.Rb.velocity = v;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // Rotate.DoRotate: the Rotate FSM's random yaw and the Attack FSM's run / rotate / turn states
        public static bool BeforeRotate(Rotate __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null) return true;
                string name = fsm.Name;
                if (name != "Rotate" && name != "Attack") return true;
                return NpcOf(fsm, name) == null;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // Raycast.DoRaycast: the Attack FSM's two 1.2 m bumper rays report "clear" (the feelers see 3-4 m)
        public static bool BeforeRaycast(Raycast __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack" || __instance.repeatInterval == null || __instance.repeatInterval.Value == 0) return true;
                var n = NpcOf(fsm, "Attack");
                if (n == null) return true;
                if (__instance.storeDidHit != null && !__instance.storeDidHit.IsNone) __instance.storeDidHit.Value = false;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // LookAt.DoLookAt: the Attack FSM's instant snap of the body toward the target (burst state) -> our turn rate instead
        public static bool BeforeLookAt(LookAt __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack") return true;
                if (__instance.gameObject == null || __instance.gameObject.OwnerOption != OwnerDefaultOption.UseOwner) return true;   // the sensor children: vanilla
                return NpcOf(fsm, "Attack") == null;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // SmoothLookAt.DoSmoothLookAt: the melee Attack FSMs (Scraffa, Spanna, the animals) turn the body toward the target every frame in the
        // chase state itself (LateUpdate, after our rotation) - that is what kept a melee NPC running straight into the thing the feelers had
        // told it to go around. Skipped while the brain steers (chase states only; the swing's own facing stays).
        public static bool BeforeSmoothLookAt(SmoothLookAt __instance)
        {
            try
            {
                var fsm = __instance.Fsm;
                if (fsm == null || fsm.Name != "Attack") return true;
                if (__instance.gameObject == null || __instance.gameObject.OwnerOption != OwnerDefaultOption.UseOwner) return true;
                var n = NpcOf(fsm, "Attack");
                if (n == null) return true;
                string st = fsm.ActiveStateName;
                return st != "trigger" && st != "run";
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // SendEvent.OnEnter: Attack's Animal_Run while holding -> Animal_Idle; Unstuck's Animal_rotateRandom -> our stuck handling
        public static bool BeforeSendEvent(SendEvent __instance)
        {
            try
            {
                if (__instance.sendEvent == null) return true;
                string ev = __instance.sendEvent.Name;
                if (ev == "Animal_Run")
                {
                    var n = NpcOf(__instance.Fsm, "Attack");
                    if (n == null || (n.Mode != Mode.Hold && n.Mode != Mode.Rest && n.Mode != Mode.Search && n.Mode != Mode.Walk)) return true;
                    if (n.Movement != null) n.Movement.SendEvent("Animal_Idle");
                    if (n.Mode == Mode.Hold) Aim(n);
                    __instance.Finish();
                    return false;
                }
                if (ev == "Animal_rotateRandom")
                {
                    var n = NpcOf(__instance.Fsm, "Unstuck");
                    if (n == null) return true;
                    // the game's Unstuck ("moved < 0.2 m in 0.3 s" -> a random spin + a hop) is cancelled while the brain has the NPC; being stuck
                    // is judged by Blocked() (driven vs covered distance), which tells a deliberate turn on the spot from a real block
                    __instance.Finish();
                    return false;
                }
                return true;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // AddForce.DoAddForce (Unstuck): no hop
        public static bool BeforeAddForce(AddForce __instance)
        {
            try { return NpcOf(__instance.Fsm, "Unstuck") == null; }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); return true; }
        }

        // ---------- reaction time ----------
        // The NPCs' eyes are SensorToolkit sensors on the "Sensors" child (a RangeSensor feeding a LOSSensor; the Detection FSM polls their
        // results every frame). They pulse on a fixed interval, so an NPC notices you up to one interval (plus the LOS moving average) after
        // you walk into view. Harmony postfix on both sensors' OnEnable: on a creature with a Detection FSM the interval is capped at
        // [Brain] SensorInterval s (0 = the game's). The original is logged once per creature type with VerboseLog.
        private static readonly HashSet<string> _sensorLogged = new HashSet<string>();

        public static void AfterLosEnable(Micosmo.SensorToolkit.LOSSensor __instance)
        {
            try
            {
                float want = Plugin.SensorInterval.Value;
                if (want <= 0f || !On || __instance == null || !IsCreatureSensor(__instance.transform)) return;
                LogSensor(__instance.transform.root.name, "LOS", __instance.PulseMode.ToString(), __instance.PulseInterval,
                    " rays " + __instance.NumberOfRays + " minVis " + __instance.MinimumVisibility + " avg " + (__instance.MovingAverageEnabled ? __instance.MovingAverageWindowSize.ToString() : "off"));
                if (__instance.PulseMode == Micosmo.SensorToolkit.PulseRoutine.Modes.FixedInterval && __instance.PulseInterval > want) __instance.PulseInterval = want;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); }
        }

        public static void AfterRangeEnable(Micosmo.SensorToolkit.RangeSensor __instance)
        {
            try
            {
                float want = Plugin.SensorInterval.Value;
                if (want <= 0f || !On || __instance == null || !IsCreatureSensor(__instance.transform)) return;
                LogSensor(__instance.transform.root.name, "Range", __instance.PulseMode.ToString(), __instance.PulseInterval, "");
                if (__instance.PulseMode == Micosmo.SensorToolkit.PulseRoutine.Modes.FixedInterval && __instance.PulseInterval > want) __instance.PulseInterval = want;
            }
            catch (Exception e) { Plugin.Log.LogError("Brain: " + e); }
        }

        private static bool IsCreatureSensor(Transform t)
        {
            var root = t.root;
            foreach (var f in root.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == "Detection") return true;
            return false;
        }

        private static void LogSensor(string who, string kind, string mode, float interval, string extra)
        {
            if (!Plugin.VerboseLog.Value) return;
            int cut = who.IndexOf('(');
            string key = (cut > 0 ? who.Substring(0, cut) : who) + kind;
            if (_sensorLogged.Contains(key)) return;
            _sensorLogged.Add(key);
            Plugin.Log.LogInfo("Brain: " + key + " sensor " + mode + " every " + interval + " s" + extra);
        }

        // For Aim: how far the body still has to turn to face its target, degrees; -1 when the brain is not steering this NPC (the game
        // snaps it to the target itself then). A burst is held until this is within [NpcAim] FacingTolerance.
        internal static float FacingError(GameObject owner)
        {
            if (!On) return -1f;
            var n = Get(owner);
            if (n == null || n.Mode == Mode.Off || n.Target == null) return -1f;
            var t = n.Target.Value;
            if (t == null) return -1f;
            Vector3 to = t.transform.position - n.T.position; to.y = 0f;
            if (to.sqrMagnitude < 0.01f) return 0f;
            return Vector3.Angle(n.T.forward, to);
        }

        internal static string Status() { return _npcs.Count + " NPCs, " + _active + " engaged, tick " + _interval + " s"; }
    }
}
