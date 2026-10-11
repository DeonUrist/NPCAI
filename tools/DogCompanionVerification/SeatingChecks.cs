using System;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

public sealed partial class Checks
{
    float HeadTop(GameObject root)
    {
        float top = float.MinValue;
        foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var vertices = skin.sharedMesh.vertices; var weights = skin.sharedMesh.boneWeights; var binds = skin.sharedMesh.bindposes;
            var matrices = skin.bones.Select((b, i) => root.transform.worldToLocalMatrix * b.localToWorldMatrix * binds[i]).ToArray();
            var head = skin.bones.Select(b => b.name == "head0" || b.name == "neck0" || b.name == "neck1").ToArray();
            for (int i = 0; i < vertices.Length; i++)
            {
                var w = weights[i];
                float h = (head[w.boneIndex0] ? w.weight0 : 0f) + (head[w.boneIndex1] ? w.weight1 : 0f) + (head[w.boneIndex2] ? w.weight2 : 0f) + (head[w.boneIndex3] ? w.weight3 : 0f);
                if (h < .5f) continue;
                var v = vertices[i];
                var p = matrices[w.boneIndex0].MultiplyPoint3x4(v) * w.weight0 + matrices[w.boneIndex1].MultiplyPoint3x4(v) * w.weight1 + matrices[w.boneIndex2].MultiplyPoint3x4(v) * w.weight2 + matrices[w.boneIndex3].MultiplyPoint3x4(v) * w.weight3;
                top = Mathf.Max(top, p.y);
            }
        }
        return top;
    }
    void CheckSeating(GameObject root, MonoBehaviour component, object dog)
    {
        var componentType = component.GetType(); var dogType = dog.GetType(); var config = componentType.Assembly.GetType("DogCompanion.Plugin");
        var modeType = componentType.GetNestedType("Mode", Flags);
        Action<int> mode = value => Set(componentType, component, "_mode", Enum.ToObject(modeType, value));
        Action<string> pose = name => Set(dogType, dog, "Pose", Enum.Parse(dogType.GetField("Pose", Flags).FieldType, name));
        Func<string, float> cfg = key => ((ConfigEntry<float>)Get(config, null, key)).Value;
        Set(componentType, component, "_seat", new Vector3(1f, .2f, 2f)); mode(1);
        var original = new Vector3(1f + cfg("SeatRight"), .2f + cfg("SeatUp") + (float)Get(componentType, component, "_pawDepth") * cfg("RideScale"), 2f + cfg("SeatForward"));
        Call(componentType, component, "RideTick"); var first = root.transform.localPosition;
        Check(Vector3.Distance(first, original + new Vector3(0f, -.125f, .45f)) < .001f, "Vehicle placement adds 45 cm forward and lowers the configured seat position by 12.5 cm");
        Call(componentType, component, "RideTick"); Check(root.transform.localPosition == first, "Repeated vehicle updates do not accumulate the forward offset");
        pose("Sitting"); Set(dogType, dog, "Playing", "SitIdle"); Set(dogType, dog, "Time", .5f); Set(dogType, dog, "Fade", 1f);
        mode(0); Call(DogType, null, "RestoreCompanion", root); Call(DogType, null, "LateCompanion", root);
        float upright = HeadTop(root); float seatContact = SoleHeight(root);
        mode(1); Call(DogType, null, "RestoreCompanion", root); Call(DogType, null, "LateCompanion", root);
        float lowered = HeadTop(root);
        report.Add("SEATED HEAD top lowered by " + ((upright - lowered) * root.transform.lossyScale.y).ToString("F3") + " m at ride scale " + root.transform.lossyScale.y.ToString("F2"));
        Check(Mathf.Abs(upright - lowered) < .005f, "Car sitting leaves the original head/neck pose unchanged");
        Check(Mathf.Abs(SoleHeight(root) - seatContact) < .005f, "Car sitting preserves the original rig contact height relative to its lower pivot");
        mode(0); Call(DogType, null, "RestoreCompanion", root); Call(DogType, null, "LateCompanion", root);
        Check(Mathf.Abs(HeadTop(root) - upright) < .005f, "The lowered head pose is removed when the companion leaves the vehicle");
        var neck = root.GetComponentsInChildren<Transform>().First(t => t.name == "neck0");
        var upper = root.GetComponentsInChildren<Transform>().First(t => t.name == "neck1");
        var headBone = root.GetComponentsInChildren<Transform>().First(t => t.name == "head0");
        var clips = (Dictionary<string, AnimationClip>)Get(dogType, dog, "Clips");
        Call(DogType, null, "RestoreCompanion", root); clips["Walk"].SampleAnimation(root.GetComponentInChildren<Animator>().gameObject, .5f);
        var originalRotation = new[] { neck.localRotation, upper.localRotation, headBone.localRotation };
        pose("Locomotion"); Set(dogType, dog, "Playing", "Walk"); Set(dogType, dog, "Time", .5f); Set(dogType, dog, "Fade", 1f);
        Call(DogType, null, "LateCompanion", root);
        Check(new[] { neck, upper, headBone }.Select((b, i) => Quaternion.Angle(b.localRotation, originalRotation[i])).Max() < .05f, "Walking retains the original 1.9.6 neck and head animation rotations");
        Call(DogType, null, "RestoreCompanion", root); mode(1); pose("Locomotion"); Set(dogType, dog, "Playing", "Idle");
    }
}
