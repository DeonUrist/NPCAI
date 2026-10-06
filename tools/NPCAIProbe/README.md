# NPCAI Probe (debug tool)

Checks, while you play, that the game matches everything NPCAI assumes, and records what NPCAI's FSM hooks really see.
Observation only: it changes nothing in the game. Remove it when done.

Install: `BepInEx\plugins\NPCAIProbe\NPCAIProbe.dll`. Config `com.denis.apocalypter.npcaiprobe.cfg` (Enabled, TraceTransitions, WriteSeconds).

Output in `BepInEx\NPCAIProbe\` (rewritten every 30 s and on quit):
- `report.txt` - checks per NPC prefab / player weapon / game (OK, FAIL, INFO, n/a), NPCAI's Harmony hooks and other mods patching
  the same methods, runtime evidence (which situations were seen), NPCAI health (inconsistent senses state, collection sizes).
- `transitions.txt` - entered states, transitions and received events per prefab and FSM; CreateObject spawns (spawners, explosions).
- `prefabs\<Prefab>.txt` - full FSM dump (every action field), sensors, animator clips, colliders of the first instance.
- `health.log` - one line of NPCAI state every 30 s.

Session to cover everything: walk into a Scrapyard camp, get seen, fight (gunmen and melee), break line of sight and hide, shout
(Alt+Q), throw an item, drive near NPCs and honk, visit a Coyotes town, save while NPCs are searching, load that save, quit.

Build: `MANAGED=... BEPCORE=... sh build.sh` (mcs against the game's Managed DLLs + BepInEx\core).
