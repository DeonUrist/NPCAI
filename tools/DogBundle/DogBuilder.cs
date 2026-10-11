
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
public static partial class DogBuilder {
 const string Source = "Assets/Source/Labrador.fbx";
 [Serializable] public class Bone { public string path, name, parent; public Vector3 position,scale; public Quaternion rotation; public Vector3 worldPosition; public Quaternion worldRotation; }
 [Serializable] public class ModelInfo { public Bone[] bones; public string[] clips; }
 public static void Inspect() {
  var imp = (ModelImporter)AssetImporter.GetAtPath(Source);
  imp.animationType = ModelImporterAnimationType.Generic;
  imp.importAnimation = true; imp.animationCompression = ModelImporterAnimationCompression.Off;
  imp.materialImportMode = ModelImporterMaterialImportMode.None;
  imp.SaveAndReimport();
  var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Source);
  var obj=UnityEngine.Object.Instantiate(prefab);
  var report=new List<string>(); var bones=new List<Bone>();
  foreach(var t in obj.GetComponentsInChildren<Transform>(true)) {
   string path=AnimationUtility.CalculateTransformPath(t,obj.transform);
   report.Add("BONE "+path+" pos="+t.localPosition.ToString("F4")+" rot="+t.localRotation.ToString("F4")+" scale="+t.localScale.ToString("F4"));
   bones.Add(new Bone {path=path,name=t.name,parent=t.parent==null?"":AnimationUtility.CalculateTransformPath(t.parent,obj.transform),position=t.localPosition,rotation=t.localRotation,scale=t.localScale,worldPosition=t.position,worldRotation=t.rotation});
  }
  var clips=AssetDatabase.LoadAllAssetsAtPath(Source).OfType<AnimationClip>().Where(c=>!c.name.StartsWith("__preview__")).ToArray();
  foreach(var c in clips) report.Add("CLIP "+c.name+" length="+c.length+" fps="+c.frameRate+" loop="+c.isLooping+" bindings="+AnimationUtility.GetCurveBindings(c).Length);
  foreach(var c in imp.defaultClipAnimations) report.Add("TAKE "+c.name+" "+c.firstFrame+".."+c.lastFrame);
  foreach(var r in obj.GetComponentsInChildren<SkinnedMeshRenderer>()) report.Add("MESH "+r.name+" verts="+r.sharedMesh.vertexCount+" bones="+r.bones.Length+" blendshapes="+r.sharedMesh.blendShapeCount+" bounds="+r.bounds);
  Directory.CreateDirectory("Build");File.WriteAllLines("Build/donor-inspection.txt",report);
  File.WriteAllText("Build/donor-inspection.json",JsonUtility.ToJson(new ModelInfo {bones=bones.ToArray(),clips=clips.Select(c=>c.name).ToArray()},true));
  Debug.Log("DOG INSPECTION: "+clips.Length+" clips, "+bones.Count+" transforms");
  UnityEngine.Object.DestroyImmediate(obj);
 }
}
