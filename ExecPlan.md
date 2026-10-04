# Apocaraider split ExecPlan

## Objective and constraints
Extract the read-only Apocaraider into WomenOfWasteland, GunplayHUD, Gunplay and NPCAI. Only these four directories may be written. No game deployment during builds. Preserve all relevant original behavior, independent loading, optional range integration, and ApocaSetter settings discovery. Runtime claims require actual observed gameplay.

## Progress
- [x] Inspect repository rules, projects and initial plugin/configuration implementation.
- [x] Delegate read-only female/HUD, gunplay/build, and AI/shared-coupling investigations.
- [x] Baseline build with all generated paths redirected into NPCAI verification artifacts.
- [x] Complete architecture/config/game analysis and freeze responsibility map.
- [x] Create four independently buildable projects and narrow plugin/config lifecycles.
- [x] Implement female/HUD/gunplay/AI extraction; build each milestone.
- [x] Verify range parity, optional contracts, patch inventory, and all 15 combinations.
- [x] Independent regression review, fixes, rebuilds, and final source integrity check.

## Initial findings
Original: .NET Framework 4.7.2, BepInEx, Harmony, Unity/PlayMaker/SensorToolkit/game references. SDK 10.0.401, MSBuild 18.9.11. Original project automatically deploys after Build, so baseline PluginDir, OutDir and intermediate paths must be overridden. ApocaSetter Catalog.All enumerates Chainloader.PluginInfos and plugin.Instance.Config; any boolean config key named Apocasetter opts in. No original GUID requirement for ordinary settings discovery.

## Design and ownership (frozen)
Women: model/texture/voice/corpse replacement and model asset loaders.
HUD: hit marker, damage numbers, crosshair controls; optional gunplay hit notifications.
Gunplay: projectile simulation, tracer visuals, damage/range implementation and stable public range API.
NPCAI: perception/sound origins, steering/nav/idle/combat decisions and compatible range fallback.
No fifth runtime assembly. Namespace and Harmony owner IDs must be unique.

Gunplay also owns boss HP scaling, projectile impact/corpse corrections, vehicle part/wheel damage and knife sound. Women owns all model loaders and female voice/loudness processing. Wav decoding is a small pure helper duplicated only for its two uses. NPCAI retains Aim/Brain/Senses/Nav/Idle/Passthrough/Storm together because they coordinate movement ownership through Brain.IsWalk and ghost walks. DebugOverlay drawing moves to NPCAI independently of HUD. Each owner installs its own narrowly scoped original hooks.

Dependency graph: all four depend only on BepInEx/Harmony/current game assemblies. NPCAI has a soft ordering dependency on Gunplay, never an assembly reference. Gunplay.Api version 1 exposes EffectiveRange(int), TryGetWeaponKind(GameObject,out int), ProjectilesEnabled. Primitive weapon codes 0..5 preserve original enum order. NPCAI.Api receives Shot/Hurt and provides SpreadFactor. GunplayHUD.Api receives PlayerHit/PartHit. Calls cross boundaries through dynamically discovered cached reflection/delegates using Unity/BCL parameter types. Standalone observers supply vanilla shot/hit paths and avoid reporting simulated projectiles twice.

ApocaSetter investigation: Catalog.All reads Chainloader plugin metadata and instance.Config; a boolean key named Apocasetter opts in regardless of GUID/section. SettingsUI enumerates typed config entries and acceptable ranges/lists. Preserve original public section/key semantics and register opt-in in each plugin. New GUIDs require no settings-menu change; online index/update distribution entries are outside this local extraction. First-run migration reads matching entries from legacy cfg; existing per-mod configs win. Original hidden controls remain hidden except deliberately exposed model/range choices.

Reference HTML describes version 1.6.1; current source is 1.7.0 and wins over stale map/README defaults (including ShooterPathing=true). Installed FSM references confirm original action names and player Bodypart damage paths. No attached-document instructions are treated as authority.

## Baseline result
BUILD VERIFIED: SDK 10.0.401, MSBuild 18.9.11, net472, Release. Command: dotnet build apocaraider/Apocaraider.csproj -c Release -p:BaseIntermediateOutputPath=<npcai>/.verification/baseline/obj/ -p:MSBuildProjectExtensionsPath=<same> -p:OutputPath=<npcai>/.verification/baseline/bin/ -p:PluginDir=<npcai>/.verification/baseline/package/ -v:minimal. Output: .verification/baseline/bin/Apocaraider.dll. Zero errors; one pre-existing CS0649 warning Brain.Npc.LastLog. Log: .verification/baseline/build.log. No game deployment. Initial attempted DefaultItemExcludes argument had MSBuild semicolon parsing error before compilation; removed unnecessary argument and build succeeded. Source had no existing obj/bin to exclude.

## Milestones and verification
1. Baseline integrity snapshot + redirected build.
2. Each standalone extraction compiles before dependent integration work.
3. Integration builds and static dependency/config/patch checks for all combinations.
4. Independent source comparison and final build/integrity/status report.

## Decisions, failures and unresolved questions
Plan lives in NPCAI to comply with workspace write restrictions. Source hash snapshot and status are local verification data, excluded from commits. Additional architecture details, exact commands/results, and deviations will be recorded as discovered.

## Runtime verification
NOT RUNTIME VERIFIED. Game installation and dumps were reference-only. No game files deployed or changed by the builds. All acceptance combinations have BUILD/STATIC verification; actual Unity plugin initialization, Harmony dispatch and gameplay remain an explicit in-game acceptance step.

## Completed milestones and repairs
Women/HUD milestones compiled separately with zero warnings/errors. Gunplay milestones compiled with zero warnings/errors. NPCAI milestones compiled with zero errors and the same inherited CS0649 warning as baseline (incremental builds may omit the warning). Every implementation milestone compiled before proceeding.

Build repairs: HUD trimmed-reference build lacked Assembly-CSharp (the SetFsmFloat action lives there), restored reference; Gunplay generated binding initially omitted VehicleDamagePer1, restored original binding; NPCAI verifier's nested obj sources entered the shipping project's default glob, explicitly excluded verification/**/*.cs and .verification/**/*.cs. All were rebuilt successfully.

Root review caught lost persistent-runner lifetime in initial female/HUD extraction; all plugins now use DontDestroyOnLoad runners and EnsureRunner on scene changes without unpatching when game destroys the plugin GameObject. Optional range lookup must use ReferenceEquals on BepInEx plugin Instance because Unity destroyed-object equality otherwise disables integrations. Probe exceptions are guarded and absence is retried. Offline verifier originally lacked initialized BepInEx Paths; added managed test paths entirely under .verification.

Independent Gunplay regression review of NPCAI/HUD found vanilla NPC ray length 80 m could not reach AI rifle/sniper shooting positions (102/212.5 m at default engagement). NPCAI now temporarily pulses that ray at effective range only without authoritative Gunplay projectiles, when aim or movement AI is enabled, independently of the detection toggle. A Harmony finalizer restores its original length even if the action throws. Original stateful projectile damage code is not duplicated. Reviewer rechecked all toggle combinations. Fallback weapon cache cleanup restored every 30 seconds.

Independent female/HUD agent compared Gunplay to original: projectile simulation, penetration, falloff, headshots, vehicle/wheel/corpse paths and WAV asset preserved; callbacks preserve attacker identity. Independent Gunplay agent compared NPCAI/HUD with original: AI body-driving methods preserved, only domain boundaries and standalone observers/range adapter differ; feedback/crosshair methods preserved. No unresolved source regression findings remain.

## Intentional differences
- Four GUIDs/namespaces/config files and independent logs, runners and Harmony owners replace the combined plugin.
- Model/texture/hide-part selectors and six range controls are exposed with their original keys; original defaults retained. AI owns the original Gunplay/NpcAim toggle; unrelated toggles do not disable other mods.
- Matching exposed settings are imported from the old cfg on first launch, without writing it; existing new settings win.
- Female/boss registration uses owner-local spawn handling and polling; each works without detection AI.
- HUD and AI add narrow vanilla observers, since their original event sources were only Gunplay. HUD flush moves to its own LateUpdate.
- NPCAI sidecars write NPCAI/Saves and read legacy Apocaraider/Saves when needed; v1 format and enum values unchanged. NavDump uses NPCAI/NavDump.
- Gunplay patches Apocapatrol's private legacy wheel-ownership probe optionally (it previously required the Apocaraider assembly name). No external repository changed.
- All projects require GameDir or APOCALYPTER_GAME_DIR and package locally rather than auto-deploying. No machine-specific build paths are embedded in source.

## Final verification and reproducible commands
Run from npcai: `./verification/verify.ps1 -GameDir "<installed-game-directory>"`. It creates a redirected read-only baseline if needed, builds all four plus the nonshipping verifier, invokes separate managed processes for all 15 masks, range/integration/config checks, actual game method inventory, and source/asset integrity coverage. No sibling DLL resolution is allowed in the test assembly resolver. Current log: .verification/verification.log.

All 15 masks pass (568 total managed checks): assembly identities, plugin metadata, soft dependency flags, GetTypes signature/base dependency resolution, no sibling/Apocaraider AssemblyRefs, and public API signatures. 62 range/config checks pass: original/Gunplay/fallback defaults and altered/clamped values, 16 classification cases including precedence, authoritative provider over conflicting fallback, provider failure fallback, four first-run cfg imports and existing-config precedence/legacy nonmutation. 46 actual game hook type/method checks pass. Source audit accounts for all 162 original exposed/hidden config keys and literal defaults; every original Models/Sounds asset has one owner and identical SHA256; all original callback owners present exactly once; female loader/audio helpers unchanged except namespace. Original 43 non-Git files match initial SHA256 snapshot and original Git status is clean.

Full report: SplitReport.md. Exact local artifact paths and final Git status capture are also stored in ignored .verification/git-status.json for this run. Generated artifacts/snapshots/logs are excluded from Git.

## Remaining verification limits
Managed reflection tests are not BepInEx/Unity game startup tests. In-game checks remain: four-way behavior equivalence, every installation combination, vanilla/custom damage timing and HUD aggregation, configured/disabled range adapters, ghost attribution, save/load restoration, female models/voices/corpses, scene changes, navigation and patrol interoperation. ApocaSetter settings discovery is statically verified and import behavior is managed-tested; live menu interaction and online index summaries/releases have not been tested/updated. No compiler error or known unresolved implementation defect remains.

## Follow-up: fixed NPCAI ranges (2026-10-04)
User requested removing all six player-editable NPCAI range settings while retaining other behavior. Removed their ConfigEntry fields and bindings, replaced local fallback with the same original values (60/70/120/250/35/90). Optional Gunplay API lookup and precedence are unchanged, including when Gunplay projectile simulation is disabled. Startup selectively removes only those six retired Tracers orphan keys from NPCAI's config; unrelated bound/orphan entries are preserved and old combined/Gunplay config files are not written. This decision supersedes the initial public NPCAI fallback settings.

Milestone build: NPCAI Release succeeds, zero errors, one inherited CS0649 warning. Updated verifier succeeds: 84 checks covering original/Gunplay calculations, fixed NPCAI defaults despite altered settings, configurable provider precedence, provider failure fallback, classification, legacy migration, exact old-key cleanup, preserved unrelated settings and idempotence. Standalone NPCAI mask8 passes23 checks; NPCAI+Gunplay mask12 passes41. Source audit passes default/key/asset/patch coverage and confirms no editable NPCAI range bindings; original 43-file SHA256 snapshot remains identical. README and SplitReport updated. No game execution or deployment.

## Release 1.0.1 (2026-10-04)
User authorized a local ZIP in workspace _releases, a source commit/push, and a GitHub release with manual ZIP attachment. Existing origin is DeonUrist/NPCAI, matching the supplied credential's authenticated account; requested DeonDF/NPCAI endpoint was not found. Preserve existing 1.0.0 archive; package 1.0.1 with README.md and NPCAI/NPCAI.dll. Plugin metadata advanced to 1.0.1. No credential stored in repository/files or Git remotes. Do not upload release assets or deploy game files.
