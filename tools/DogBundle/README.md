# Canine bundle builder

Unity 2020.3.49f1 editor scripts used to build `Models/npcai_dogs.bundle`.
Copy these scripts into a Unity project's `Assets/Editor` directory. The local
working project is `_staging/npcai-dog-animations-20261009/UnityDogs` beside this
repository. It contains the original exported game target skeletons/meshes in
`Assets/Targets` and the purchased Labrador donor in `Assets/Source/Labrador.fbx`.
Those source assets are not part of this tool or the mod package.

Run `DogBuilder.Bake` in Unity batch mode to retarget and build for Windows 64-bit,
then `DogBuilder.Preview` with graphics enabled to render the pose previews. The
1.9.5 map adds `SitDown` from `Sitting_start` and looping `SitIdle` from
`Sitting_loop_1`; all nine prior actions remain. Output: 55 clips across five rigs.
The current bundle adds `SneakIdle` and `SneakWalk` for the Wild Hound, for a
total of 57 clips.

1.9.6 also bakes a `__NPCAI_PawSupport` height curve into every Wild Hound clip.
It records the skin's lowest non-tail point relative to the lowest paw bone.
Only the companion creates this invisible marker and uses its sampled value to
align the visual rig with its support surface. Native NPC rigs ignore the curve.

`DogBuilder.CompanionPreview` also renders the corrected poses on a support
surface. Copy `DogCompanionSeating.cs` from the repository root into the editor
directory with it to preview the current passenger position. Version 1.9.10
retains the original seated neck/torso pose and lowers the whole dog instead.

The Wild Hound moving crouch clip uses a hind-leg joint correction during baking
to match the donor's grounded/raised paw phases to the target's front support
height. It rotates existing joints without translating or stretching the bones.

Copy `Build/npcai_dogs.bundle` to `Models/npcai_dogs.bundle` after a successful
build. The companion requires both sit clips along with locomotion and bite clips;
an older bundle keeps its original controller rather than taking partial ownership.


Native Wild Hound offsets are deliberately separate from the companion's clips.
`Models/npcai_wildhound.bundle` contains the nine original pre-companion Wild Hound
clips. Rebuilding the main canine bundle does not replace this native offset set;
keep both bundles when packaging or deploying. The companion continues to use
`Wild_Hound_*` from the main bundle and its own support-height placement.
The original offset baseline is kept locally at
`_staging/npcai-dogcompanion-compat/backup/npcai_dogs.bundle`.
