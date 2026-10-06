using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NPCAIProbe
{
    internal static class Report
    {
        private sealed class HookRow { public bool Ours; public string All, Why; }
        private static readonly SortedDictionary<string, HookRow> _hooks = new SortedDictionary<string, HookRow>(StringComparer.Ordinal);
        internal static void Hook(string id, bool ours, string all, string why) { _hooks[id] = new HookRow { Ours = ours, All = all, Why = why }; }

        // what the traces must show once the matching situation was played (camp fight, car, save + load ...)
        private sealed class Ev { public string Desc, Group, Line; public bool Bad; }
        private static readonly Ev[] Evidence =
        {
            new Ev { Desc = "NPC Detection reaches searchRange (Senses skips it)", Group = "[Detection]", Line = "enter  searchRange" },
            new Ev { Desc = "RangedAttackWait sends Activate and Attack handles it (Aim holds/releases bursts)", Group = "[Attack]", Line = "event  in ", Bad = false },
            new Ev { Desc = "Attack enters attack_ranged (a burst)", Group = "[Attack]", Line = "enter  attack_ranged" },
            new Ev { Desc = "Attack enters trigger (chase state the brain steers)", Group = "[Attack]", Line = "enter  trigger" },
            new Ev { Desc = "Sound enters 'attack' (combat shout -> taunt)", Group = "[Sound]", Line = "enter  attack" },
            new Ev { Desc = "Movement enters Run / Idle", Group = "[Movement]", Line = "enter  Run" },
            new Ev { Desc = "SaveLoadGame enters SaveGame (senses saved)", Group = "[SaveLoadGame]", Line = "enter  SaveGame" },
            new Ev { Desc = "SaveLoadGame enters LoadGame then isPlay (senses restored)", Group = "[SaveLoadGame]", Line = "enter  LoadGame" },
            new Ev { Desc = "Player InCar enters InCar (engine noise)", Group = "[InCar]", Line = "enter  InCar" },
            new Ev { Desc = "GrabItem enters Throw (thrown item noise)", Group = "[GrabItem]", Line = "enter  Throw" },
            new Ev { Desc = "INPUT_Horn enters on (horn noise)", Group = "[INPUT_Horn]", Line = "enter  on" },
            new Ev { Desc = "PlayerIsEnemy switches state (Coyotes turn hostile / make peace)", Group = "[PlayerIsEnemy]", Line = "trans  " },
        };
        private static readonly string[] Sent = { "Animal_Run", "Animal_Idle", "Activate", "Animal_rotateRandom" };

        internal static void Write()
        {
            var lines = Trace.Lines().ToList();
            var sb = new StringBuilder();
            sb.AppendLine("# NPCAIProbe " + Plugin.VERSION + " report, " + DateTime.Now + "  (rewritten every " + Plugin.WriteSeconds.Value + " s and on quit)");
            sb.AppendLine("# OK = the game matches what NPCAI assumes; FAIL = it does not (or something is missing); INFO = facts to read; n/a = does not apply.");

            int fails = Checks.All.Values.Sum(c => c.Rows.Values.Count(r => r.S == Checks.St.FAIL));
            sb.AppendLine();
            sb.AppendLine("== SUMMARY: " + Checks.All.Count + " checks, " + fails + " FAIL row(s), " + _hooks.Values.Count(h => !h.Ours) + " missing hook(s), " + Health.Problems.Count + " runtime problem(s)");

            sb.AppendLine();
            sb.AppendLine("== CHECKS (subjects: NPC prefabs, player weapons, 'game')");
            foreach (var c in Checks.All.Values)
            {
                sb.AppendLine();
                var byS = c.Rows.GroupBy(kv => kv.Value.S).ToDictionary(g => g.Key, g => g.ToList());
                sb.AppendLine(c.Id + "  " + c.Desc);
                foreach (var st in new[] { Checks.St.FAIL, Checks.St.OK, Checks.St.INFO, Checks.St.NA })
                {
                    List<KeyValuePair<string, Checks.Row>> rows;
                    if (!byS.TryGetValue(st, out rows)) continue;
                    if (st == Checks.St.NA) { sb.AppendLine("   n/a   " + string.Join(", ", rows.Select(r => r.Key).ToArray())); continue; }
                    foreach (var r in rows) sb.AppendLine("   " + st.ToString().PadRight(5) + " " + r.Key.PadRight(22) + " " + r.Value.Detail);
                }
            }

            sb.AppendLine();
            sb.AppendLine("== NPCAI HOOKS (ok = an npcai patch of the expected kind is installed; other owners patch the same method)");
            foreach (var kv in _hooks) sb.AppendLine("   " + (kv.Value.Ours ? "ok     " : "MISSING") + " " + kv.Key.PadRight(52) + " " + kv.Value.Why.PadRight(38) + " " + kv.Value.All);

            sb.AppendLine();
            sb.AppendLine("== RUNTIME EVIDENCE (seen = happened at least once this session; 'not yet' = play that situation)");
            foreach (var e in Evidence)
            {
                var hit = lines.Where(l => l.Key.EndsWith(e.Group, StringComparison.Ordinal) && l.Value.StartsWith(e.Line, StringComparison.Ordinal)).Select(l => l.Key.Replace(" " + e.Group, "")).Distinct().ToList();
                if (e.Desc.StartsWith("RangedAttackWait", StringComparison.Ordinal))
                    hit = lines.Where(l => l.Key.EndsWith("[Attack]", StringComparison.Ordinal) && l.Value.Contains("<- Activate") && !l.Value.Contains("ignored")).Select(l => l.Key.Replace(" [Attack]", "")).Distinct().ToList();
                sb.AppendLine("   " + (hit.Count > 0 ? "seen   " : "not yet") + " " + e.Desc + (hit.Count > 0 ? "  (" + string.Join(", ", hit.Take(12).ToArray()) + (hit.Count > 12 ? ", ..." : "") + ")" : ""));
            }
            var ignored = lines.Where(l => l.Value.Contains("(no transition: ignored)") && Sent.Any(s => l.Value.Contains("<- " + s + " "))).ToList();
            ignored.AddRange(lines.Where(l => l.Value.Contains("(no transition: ignored)") && Sent.Any(s => l.Value.EndsWith("<- " + s + "  (no transition: ignored)", StringComparison.Ordinal))));
            sb.AppendLine("   events NPCAI relies on that reached a state with no transition for them (harmless if the state is meant to ignore them):");
            foreach (var l in ignored.Distinct()) sb.AppendLine("      " + l.Key + "  " + l.Value);

            sb.AppendLine();
            sb.AppendLine("== NPCAI HEALTH (latest; history in health.log)");
            sb.AppendLine("   " + Health.Last);
            sb.AppendLine("   problems seen this session:" + (Health.Problems.Count == 0 ? " none" : ""));
            foreach (var p in Health.Problems.Take(200)) sb.AppendLine("      " + p);
            Health.WriteSizes(sb);
            File.WriteAllText(Path.Combine(Plugin.OutDir, "report.txt"), sb.ToString());
        }
    }
}
