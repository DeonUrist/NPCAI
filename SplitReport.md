# Apocaraider split engineering report

Four new local Git repositories are implemented. All build successfully. Apocaraider remains unchanged; ApocaSetter and the game installation were not edited or deployed to.

| Repository | Feature ownership | Release output |
|---|---|---|
| womenofwasteland | Female spawn/model/texture selection, voice, corpses, saved appearance, model/audio loaders | womenofwasteland/bin/Release/WomenOfWasteland.dll + Models/ + Sounds/ |
| gunplayhud | Hit markers, floating/list damage numbers, vehicle condition feedback, crosshair controls, standalone vanilla hit observer | gunplayhud/bin/Release/GunplayHUD.dll |
| gunplay | Player/NPC projectiles and tracers, effective ranges/falloff, leading, headshots, impact/corpse effects, vehicle/wheel damage, boss HP | gunplay/bin/Release/Gunplay.dll + Sounds/knifestab.wav |
| npcai | Aim pacing, perception, sound/ghost origins, search/pursuit, combat movement, baked navigation, idle behavior, friendly passthrough, storms, AI debug overlays | npcai/bin/Release/NPCAI.dll |

Paths above are relative to the workspace containing the four repositories. Each repository includes its own plugin, portable project, config/logging, README, .gitignore and Git metadata. New files are uncommitted; generated outputs are ignored.

```text
WomenOfWasteland ──> BepInEx / Unity / game
GunplayHUD      ──> BepInEx / Harmony / Unity / game
Gunplay         ──> BepInEx / Harmony / Unity / game
NPCAI           ──> BepInEx / Harmony / Unity / game

NPCAI --optional version-1 reflection API--> Gunplay ranges/classification
Gunplay --optional reflection callbacks--> NPCAI shot/hurt/spread
Gunplay --optional reflection callbacks--> GunplayHUD feedback
```

There is no fifth runtime mod and no sibling assembly reference. NPCAI declares one soft BepInEx dependency on Gunplay for ordering. Gunplay discovers callbacks dynamically and tolerates absence. Primitive weapon codes and game/BCL argument types prevent shared-contract type loading failures. Pure WAV decoding and legacy-config import helpers are local copies; stateful projectile/AI/HUD hooks retain a single owning mod. Shared action targets have narrowly gated callbacks, preserving original coexistence.

Gunplay.Api supplies live EffectiveRange(int), TryGetWeaponKind(GameObject,out int), and ProjectilesEnabled. NPCAI uses the available provider as authoritative, including when projectile simulation is disabled. Otherwise its independent discovery and classifier preserve the original active weapon/muzzle/crossbow hierarchy rules and retry/cache behavior, with fixed internal ranges: pistol 60, SMG 70, rifle 120, sniper 250, shotgun 35, crossbow 90 metres. NPCAI no longer exposes range settings; only Gunplay offers player-adjustable ranges. Tests compare original/Gunplay configurable calculations and NPCAI fixed defaults, and verify provider precedence/failure fallback. Without Gunplay projectiles, NPCAI temporarily extends or shortens the vanilla NPC shot ray to effective reach and restores it through a Harmony finalizer; AI range positioning consequently has a usable hitscan path.

ApocaSetter Catalog discovers Chainloader plugin metadata and managed Instance.Config entries; a boolean Apocasetter key opts in. All four preserve General/Apocasetter=true and relevant original section/key semantics, types and acceptable values. Matching exposed entries migrate from the old cfg on first run; old files remain unchanged and existing new configs win. Hidden fixed controls remain hidden except deliberately exposed model/range choices. No ApocaSetter source change is needed for discovery or configuration. External index entries for online summaries, installation and release/update listings were not added by this local split.

BUILD VERIFIED: .NET SDK 10.0.401 / MSBuild 18.9.11, net472 Release. WomenOfWasteland, GunplayHUD and Gunplay compile with zero warnings/errors. NPCAI compiles with zero errors and one inherited CS0649 unused Brain.Npc.LastLog warning on full compilation; the original has the same warning. All builds package locally and never deploy. Each README documents `dotnet build <project>.csproj -c Release -p:GameDir="<installed game directory>"` or APOCALYPTER_GAME_DIR.

STATICALLY / MANAGED-TEST VERIFIED: 568 checks across separate processes for the 15 combinations; 62 range/classification/optional-provider/config migration checks; 46 checks that actual game patch target types/methods exist. Source audit preserves all 162 original public/hidden setting keys and literal defaults, every original asset byte-for-byte, original patch callback ownership, and unchanged female model/audio helpers apart from namespace. Initial source SHA256 snapshot confirms all 43 original non-Git files unchanged. Fresh independent agents reviewed Gunplay and NPCAI/HUD against original source and fixes were rebuilt.

| Combination | Build/static managed result |
|---|---|
| WomenOfWasteland | PASS |
| GunplayHUD | PASS |
| Gunplay | PASS |
| NPCAI | PASS |
| WomenOfWasteland + GunplayHUD | PASS |
| WomenOfWasteland + Gunplay | PASS |
| WomenOfWasteland + NPCAI | PASS |
| GunplayHUD + Gunplay | PASS |
| GunplayHUD + NPCAI | PASS |
| Gunplay + NPCAI | PASS |
| WomenOfWasteland + GunplayHUD + Gunplay | PASS |
| WomenOfWasteland + GunplayHUD + NPCAI | PASS |
| WomenOfWasteland + Gunplay + NPCAI | PASS |
| GunplayHUD + Gunplay + NPCAI | PASS |
| All four | PASS |

Intentional changes: independent plugin/config/runner ownership; one-time settings migration; public model and range controls; NPC aiming no longer gated by unrelated projectile toggle; owner-local female/boss scans; standalone vanilla hit/shot observations; temporary vanilla NPC range adaptation; HUD's own LateUpdate flush; NPCAI sidecar writes under NPCAI/Saves with legacy read fallback (same v1 format); optional Apocapatrol legacy wheel-ownership patch; build-time local packaging. Core original model, ballistic and AI algorithms were retained. See ExecPlan.md for decisions, intermediate compiler failures and repairs.

RUNTIME VERIFIED: none. The game was not launched. Managed type loading does not execute plugin Awake, install the Harmony patches in Unity, or observe gameplay. Actual in-game acceptance remains for all combinations, shared patch timing, ray repulsing/restoration, damage/HUD timing, shotgun/headshot aggregation, ghost attribution, save restoration, female corpses/voices, scene transitions, navigation and Apocapatrol compatibility. Vanilla HUD measurements may fall back to nominal damage if the health event is delayed beyond LateUpdate, as documented in its README. There are no known compiler errors or unresolved source review findings. The original combined plugin should be replaced when installing this split to avoid duplicate original hooks.

Reproduce checks from npcai with `./verification/verify.ps1 -GameDir "<installed game directory>"`. Local logs and exact Git-status/artifact metadata are in ignored .verification/. Source integrity snapshots and baseline artifacts are generated locally when absent. These checks distinguish build/static verification from gameplay and do not change the source repository or game.

Final Git status: apocaraider is clean. Each of womenofwasteland, gunplayhud, gunplay and npcai contains only the newly created, untracked source/project/documentation/assets; no commits were requested or made. The exact porcelain status is captured in .verification/git-status.json.

Follow-up, 2026-10-04: at the user's request NPCAI fallback ranges are now fixed in code. All six editable NPCAI range bindings/fields were removed; startup retires only their exact old config entries, preserving unrelated entries. Gunplay's optional live range API, AI algorithms and other settings remain unchanged. Updated verification passes 84 range/classification/provider/config checks (including old-key cleanup, unrelated-setting preservation and fixed fallback despite edited old values), plus standalone NPCAI and NPCAI+Gunplay managed type/dependency checks. NPCAI rebuilt with zero errors and the inherited CS0649 warning. No in-game verification claimed. This supersedes the initial extraction's configurable NPCAI fallback design.
