using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

public sealed partial class Checks
{
    float SoleHeight(GameObject root)
    {
        var mesh = new Mesh(); float min = float.MaxValue;
        foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var vertices = skin.sharedMesh.vertices; var weights = skin.sharedMesh.boneWeights; var binds = skin.sharedMesh.bindposes;
            var matrices = skin.bones.Select((b,i) => root.transform.worldToLocalMatrix * b.localToWorldMatrix * binds[i]).ToArray();
            for (int i = 0; i < vertices.Length; i++)
            {
                var w = weights[i]; var v = vertices[i];
                var point = matrices[w.boneIndex0].MultiplyPoint3x4(v) * w.weight0 + matrices[w.boneIndex1].MultiplyPoint3x4(v) * w.weight1 + matrices[w.boneIndex2].MultiplyPoint3x4(v) * w.weight2 + matrices[w.boneIndex3].MultiplyPoint3x4(v) * w.weight3;
                // The tail may hang below a seat; classify it by skin weights,
                // since sitting can curl it forward into the body's footprint.
                float tail = (skin.bones[w.boneIndex0].name.StartsWith("tail") ? w.weight0 : 0f)
                    + (skin.bones[w.boneIndex1].name.StartsWith("tail") ? w.weight1 : 0f)
                    + (skin.bones[w.boneIndex2].name.StartsWith("tail") ? w.weight2 : 0f)
                    + (skin.bones[w.boneIndex3].name.StartsWith("tail") ? w.weight3 : 0f);
                if (tail < .5f) min = Mathf.Min(min, point.y);
            }
        }
        UnityEngine.Object.DestroyImmediate(mesh); return min;
    }
    void CheckSupport(GameObject root, object dog, object driver, string label)
    {
        var dogType = dog.GetType();
        var position = root.transform.localPosition; var rotation = root.transform.localRotation;
        var clips = (Dictionary<string, AnimationClip>)Get(dogType, dog, "Clips");
        float depth = (float)driver.GetType().GetProperty("PawHeight", Flags).GetValue(driver, null);
        foreach (string action in new[] { "Idle", "Walk", "Run", "SitDown", "SitIdle" })
        {
            var poseName = action == "SitDown" ? "SitDown" : action == "SitIdle" ? "Sitting" : "Locomotion";
            Set(dogType, dog, "Pose", Enum.Parse(dogType.GetField("Pose", Flags).FieldType, poseName));
            Set(dogType, dog, "Playing", action); Set(dogType, dog, "Fade", 1f);
            float greatestGap = float.MinValue, deepest = float.MaxValue;
            foreach (float phase in new[] { 0f, .25f, .5f, .75f, .99f })
            {
                Call(DogType, null, "RestoreCompanion", root);
                Set(dogType, dog, "Time", clips[action].length * phase);
                Call(DogType, null, "LateCompanion", root);
                float gap = SoleHeight(root) - depth;
                greatestGap = Mathf.Max(greatestGap, gap); deepest = Mathf.Min(deepest, gap);
            }
            report.Add("SUPPORT " + label + " " + action + " actual skin gap " + deepest.ToString("F3") + ".." + greatestGap.ToString("F3") + " root units");
            Check(greatestGap < .01f && deepest > -.01f, label + " " + action + " geometry stays within 1 cm of the support surface through the clip");
        }
        Call(DogType, null, "RestoreCompanion", root);
        Check(root.transform.localPosition == position && root.transform.localRotation == rotation, label + " visual alignment preserves the navigation/seat pivot");
        Set(dogType, dog, "Pose", Enum.Parse(dogType.GetField("Pose", Flags).FieldType, "Locomotion")); Set(dogType, dog, "Playing", "Idle");
    }
    void HeightDiagnostics(string source)
    {
        var root = ExportedRig(Path.Combine(source, "Wild_Hound.bin"), fixtures.Count);
        var animator = root.GetComponentInChildren<Animator>();
        var mesh = new Mesh();
        foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>()) report.Add("SKIN " + skin.name + " scale=" + skin.transform.localScale + " lossy=" + skin.transform.lossyScale);
        var clips = ((Dictionary<string, AnimationClip>)((System.Collections.IDictionary)Get(DogType, null, "Clips"))["Wild_Hound"]);
        foreach (string action in new[] { "Idle", "Walk", "Run", "SitDown", "SitIdle" })
        {
            foreach (float phase in new[] { 0f, .25f, .5f, .75f, .99f })
            {
                clips[action].SampleAnimation(animator.gameObject, clips[action].length * phase);
                float min = float.MaxValue;
                foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    skin.BakeMesh(mesh, false);
                    foreach (var v in mesh.vertices) min = Mathf.Min(min, root.transform.InverseTransformPoint(skin.transform.TransformPoint(v)).y);
                }
                var paws = root.GetComponentsInChildren<Transform>().Where(t => t.name.EndsWith("_toe")).Select(t => t.name + "=" + root.transform.InverseTransformPoint(t.position).y.ToString("F3"));
                report.Add("HEIGHT " + action + " " + phase + " meshBottom=" + min.ToString("F3") + " paws=" + string.Join(",", paws));
            }
        }
        UnityEngine.Object.DestroyImmediate(mesh);
    }
}
