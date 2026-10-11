# Dog Companion compatibility verification

This BepInEx test plugin runs controlled checks inside the game's actual Unity
and PlayMaker runtime, using the installed Dog Companion assembly and exported
original game skeleton fixtures. It blocks ordinary world/save FSMs and exits
before normal gameplay. Use only a separate isolated runtime directory.

Build `Verifier.csproj` with `-p:GameDir=<game-directory>` after building NPCAI.
Put the verifier DLL, NPCAI DLL/bundle and optional Dog Companion DLL into the
isolated runtime's `BepInEx/plugins`, then launch its copied game executable with:

```
-batchmode -nographics -npcai-dogs-direct -npcai-dogs-report=<report-directory> -npcai-dogs-targets=<exported-fixtures-directory>
```

Fixtures are the five `.bin` exports in the local staging Unity project's
`Assets/Targets`. They are not included in a distributed mod package.
The verifier writes `report.txt` and exits nonzero if any check fails.

For the focused 1.9.10 checks, add `-npcai-companion-fixes` with the current bundle
and Dog Companion 1.2.0. This covers moving sneak hind-leg placement, combat
sneak release, unmodified seated torso/head bones, aggro override and temporary
cover expiry/cooldown. The release report is included with the packaged mod.

The broader historical checks run with the current bundle and Dog Companion 1.2.0; repeat with Dog Companion absent,
and repeat with the 1.9.4 bundle and `-npcai-companion-fallback` to check graceful
fallback when sit clips are missing. Current checks cover native NPC rest/attack,
companion movement clips, sitting while parented, blend continuity, exactly-once
contact damage, missed/obstructed/dead targets, snapshot running attacks, disable,
dismiss/recall and scene cleanup.

Clip clocks are explicitly advanced. The checks do not replace normal gameplay
or vehicle-specific visual seating, fetch and tool-prop review.
