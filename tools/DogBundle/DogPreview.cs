using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
public static partial class DogBuilder {
 public static void Preview() {
  Directory.CreateDirectory("Build/Preview");var report=new List<string>();
  RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=new Color(.65f,.65f,.65f);
  var sun=new GameObject("Preview sun").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.2f;sun.transform.rotation=Quaternion.Euler(40,-30,0);
  var camera=new GameObject("Preview camera").AddComponent<Camera>();camera.orthographic=true;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.17f,.19f,.22f);camera.nearClipPlane=.01f;camera.farClipPlane=100;
  var render=new RenderTexture(512,512,24);camera.targetTexture=render;
  foreach(string file in Directory.GetFiles("Assets/Targets","*.json")) {
   var data=JsonUtility.FromJson<Target>(File.ReadAllText(file));Transform[] nodes;var target=Create(data,out nodes);var anim=nodes[data.animators[0].node];
   var rests=nodes.Select(t=>t.localPosition).ToArray();var rots=nodes.Select(t=>t.localRotation).ToArray();
   foreach(string action in new[]{"Idle","LieIdle","Bite","Run","GetUp","JumpAttack","SitDown","SitIdle"}) {
    Restore(nodes,rests,rots);
    string name=data.prefab.Replace(' ','_')+"_"+action;var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Clips/"+name+".anim");if(clip==null)throw new Exception("Missing "+name);
    clip.SampleAnimation(anim.gameObject,clip.length*(action=="LieIdle"?.2f:action=="Bite"?.45f:action=="GetUp"?.5f:action=="JumpAttack"?.4f:.25f));
    Bounds bounds=new Bounds();bool first=true;var staticMeshes=new List<GameObject>();
    foreach(var smr in target.GetComponentsInChildren<SkinnedMeshRenderer>()) {
     var baked=new Mesh();smr.BakeMesh(baked);
     var visible=new GameObject("BakedPreview");visible.transform.SetParent(smr.transform,false);visible.AddComponent<MeshFilter>().sharedMesh=baked;visible.AddComponent<MeshRenderer>().sharedMaterial=smr.sharedMaterial;staticMeshes.Add(visible);smr.enabled=false;
     foreach(var v in baked.vertices){var p=smr.transform.TransformPoint(v);if(first){bounds=new Bounds(p,Vector3.zero);first=false;}else bounds.Encapsulate(p);}
    }
    if(float.IsNaN(bounds.size.magnitude)||float.IsInfinity(bounds.size.magnitude)||bounds.size.magnitude>10||bounds.size.magnitude<.2f)throw new Exception("Invalid deformed bounds "+name+" "+bounds);
    report.Add(name+" bounds="+bounds);
    camera.orthographicSize=Mathf.Max(bounds.size.y,bounds.size.z)*.7f;
    camera.transform.position=bounds.center+new Vector3(3f,1.2f,1.5f);camera.transform.LookAt(bounds.center);
    camera.Render();RenderTexture.active=render;var texture=new Texture2D(512,512,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,512,512),0,0);texture.Apply();File.WriteAllBytes("Build/Preview/"+name+".png",texture.EncodeToPNG());UnityEngine.Object.DestroyImmediate(texture);
    foreach(var visible in staticMeshes){UnityEngine.Object.DestroyImmediate(visible.GetComponent<MeshFilter>().sharedMesh);UnityEngine.Object.DestroyImmediate(visible);}foreach(var smr in target.GetComponentsInChildren<SkinnedMeshRenderer>())smr.enabled=true;
   }
   // Validate that the explicit rest transitions join without an unrelated pose.
   string stem=data.prefab.Replace(' ','_');var enter=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Clips/"+stem+"_LieDown.anim");var idle=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Clips/"+stem+"_LieIdle.anim");var exit=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Clips/"+stem+"_GetUp.anim");
   enter.SampleAnimation(anim.gameObject,enter.length);var end=nodes.Select(t=>t.localRotation).ToArray();idle.SampleAnimation(anim.gameObject,0f);float enterDifference=nodes.Select((t,i)=>Quaternion.Angle(t.localRotation,end[i])).Max();
   exit.SampleAnimation(anim.gameObject,0f);float exitDifference=nodes.Select((t,i)=>Quaternion.Angle(t.localRotation,end[i])).Max();report.Add(stem+" transition angle enter="+enterDifference+" exit="+exitDifference);
   UnityEngine.Object.DestroyImmediate(target);
  }
  File.WriteAllLines("Build/preview-validation.txt",report);RenderTexture.active=null;UnityEngine.Object.DestroyImmediate(render);UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(sun.gameObject);
 }
}
