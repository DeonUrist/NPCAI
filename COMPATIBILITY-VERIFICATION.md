# NPC Overhaul — Dog Companion compatibility

Version 1.9.10: release build and animation bundle build succeed. Focused native
checks pass for the moving crouch hind-leg correction, immediate combat sneak
release, preservation of the original sitting torso/head pose, suppression of
the companion's sticky detection redirect, cover selection near the dog,
combat-target preservation, four-second retreat expiry and repeat cooldown.
These checks explicitly advance clip clocks and use isolated cover fixtures;
actual terrain/vehicle visuals and retreat navigation need normal gameplay review.
The earlier checks below describe historical versions, including head-bending
adjustments that 1.9.10 removes.

Verified on 2026-10-12 using Unity 2020.3.49f1, BepInEx 5.4.23.5 and the
installed Dog Companion 1.2.0 DLL. The compatibility bridge is included in
NPCAI.dll; Dog Companion is optional and is not modified or bundled.

- Release build passes. Existing Brain unused-field warnings remain.
- The animation bundle loads 55 clips across the original five rigs.
- Native runtime checks pass with Dog Companion present and absent.
- The actual stripped companion model registers without physics components.
- Idle/walk/run, sit-down, sitting idle, parented car eligibility, smooth pose
  transitions and return to locomotion are checked.
- Both native NPC and companion attacks defer damage to contact. Companion
  damage uses its configured amount and is applied once; out-of-reach,
  obstructed, sidestepping and dead targets receive no hit.
- Running attacks retain their committed direction, and losing a target releases
  the attack. Disable, dismissal, recall and scene cleanup restore ownership.
- An older bundle without sitting clips leaves Dog Companion's original
  animation and attack controller working.
- Unity editor previews were rendered and visually inspected on the original
  Wild Hound model; the sitting pose is upright rather than lying down.
- 1.9.6 corrects the donor animation's support-height offset. Skin geometry is
  checked at five phases of idle, walk, run, sit-down and seated idle, at scales
  1 and 1.25 and on a tilted car at scale 0.9. Contact stays within 1 cm of the
  intended ground/seat surface; the root navigation/seat pivot is preserved.
- The support offset is baked from the original skinned model, so the runtime
  does not need to read the game's non-readable mesh or bake meshes every frame.
- 1.9.7 adds 0.30 m to the vehicle passenger placement without accumulating the
  offset each update. Its seated neck/head overlay lowers the actual head skin,
  keeps seat contact height, and releases on leaving the vehicle. The walking
  neck/head rotations are checked against the unchanged 1.9.6 animation clip.

These checks explicitly advance animation clocks in an isolated native game
process. They do not establish normal gameplay or vehicle-specific visual
alignment. Dog Companion's existing seat offsets and ride scale still apply;
fetch/tool props and particular vehicles should be reviewed in live play.

Verifier source and instructions: `tools/DogCompanionVerification`.
Bundle editor scripts and build instructions: `tools/DogBundle`.
