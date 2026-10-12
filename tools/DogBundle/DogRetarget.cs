using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public static partial class DogBuilder {
 [Serializable] public class Target { public string prefab; public Node[] nodes; public Skin[] models; public An[] animators; }
 [Serializable] public class Node { public string path,name; public int parent; public Vector3 position,scale; public Quaternion rotation; }
 [Serializable] public class An { public int node; public string name; public string[] clips; }
 [Serializable] public class FloatArray { public float[] values; }
 [Serializable] public class IntArray { public int[] values; }
 [Serializable] public class Skin { public int node; public string name,texture,material; public Vector3[] vertices,normals; public Vector2[] uv; public int[] triangles,bones; public FloatArray[] weights,bindposes; public IntArray[] indices; }
 class Link { public Transform target, source; public Quaternion rest; }
 static readonly Dictionary<string,string> Actions = new Dictionary<string,string> {
  {"Idle","Idle_1"},{"Walk","Walk_F_IP"},{"Run","Run_F_IP"},{"LieDown","Lie_start"},{"LieIdle","Lie_loop_1"},{"GetUp","Lie_end"},{"Bite","Attack_Bite"},{"MovingBite","Attack_Bite_IP"},{"JumpAttack","Attack_J"},
  {"SitDown","Sitting_start"},{"SitIdle","Sitting_loop_1"},
  {"SneakIdle","Crouch_Idle_loop_1"},{"SneakWalk","Crouch_F_IP"}
 };
 static readonly Dictionary<string,string> Common = new Dictionary<string,string> {
  {"root","Root_bone"},{"body","Spine_base"},{"body_bot","Spine_base"},{"pelvis","Spine_base"},{"body_top0","Spine_03"},{"body_top1","Spine_05"},
  {"neck0","neck"},{"neck1","head"},{"head0","head"},
  {"tail0","Tail_01"},{"tail1","Tail_02"},{"tail2","Tail_03"},{"tail3","Tail_04"},{"tail4","Tail_05"},{"tail5","Tail_05"}
 };
 static Dictionary<string,string> Mapping(bool grim) {
  var map=grim?new Dictionary<string,string> { {"Spine1","Spine_base"},{"Spine2","Spine_05"},{"Spine3","neck"},{"Head","head"},{"Head.001","mouth"} }:new Dictionary<string,string>(Common);
  foreach(string side in new[]{"left","right"}) {
   string s=side=="left"?"L":"R",gs=side=="left"?"Left":"Right";
   if(grim) {
    map["legFront_0"+gs]="thigh_f."+s;map["legFront_1"+gs]="leg_f."+s;map["legFront_IK"+gs]="foot_f."+s;
    map["legRear_0"+gs]="thigh_b."+s;map["legRear_1"+gs]="leg_b."+s;map["legRear_2"+gs]="shin_b."+s;map["legRear_IK"+gs]="foot_b."+s;
   } else foreach(string limb in new[]{"front","hind"}) {
    string suf=(limb=="front"?"f":"b")+"."+s,p="leg_"+limb+"_"+side+"_";
    map[p+"top0"]="hip_"+suf;map[p+"top1"]="thigh_"+suf;map[p+"bot0"]="leg_"+suf;map[p+"ankle"]="shin_"+suf;map[p+"toe"]="foot_"+suf;
   }
  }
  return map;
 }
 static Matrix4x4 Matrix(float[] v) { var m=new Matrix4x4();for(int r=0;r<4;r++)for(int c=0;c<4;c++)m[r,c]=v[r*4+c];return m; }
 static GameObject Create(Target data, out Transform[] nodes) {
  nodes=new Transform[data.nodes.Length];
  for(int i=0;i<nodes.Length;i++) {
   var n=data.nodes[i];var t=new GameObject(n.name).transform;nodes[i]=t;
   if(n.parent>=0)t.SetParent(nodes[n.parent],false);
   t.localPosition=i==0?Vector3.zero:n.position;t.localRotation=i==0?Quaternion.identity:n.rotation;t.localScale=n.scale;
  }
  var nodeArray=nodes;
  foreach(var s in data.models) {
   var mesh=new Mesh { name=s.name };mesh.vertices=s.vertices;mesh.normals=s.normals;mesh.uv=s.uv;mesh.triangles=s.triangles;
   mesh.bindposes=s.bindposes.Select(b=>Matrix(b.values)).ToArray();
   var weights=new BoneWeight[s.vertices.Length];
   for(int i=0;i<weights.Length;i++) {var w=s.weights[i].values;var b=s.indices[i].values;weights[i]=new BoneWeight {weight0=w[0],weight1=w[1],weight2=w.Length>2?w[2]:0,weight3=w.Length>3?w[3]:0,boneIndex0=b[0],boneIndex1=b[1],boneIndex2=b.Length>2?b[2]:0,boneIndex3=b.Length>3?b[3]:0};}
   mesh.boneWeights=weights;mesh.RecalculateBounds();
   var smr=nodes[s.node].gameObject.AddComponent<SkinnedMeshRenderer>();smr.sharedMesh=mesh;smr.bones=s.bones.Select(i=>nodeArray[i]).ToArray();smr.rootBone=smr.bones[0];smr.updateWhenOffscreen=true;
   var mat=new Material(Shader.Find("Standard"));mat.color=new Color(.7f,.63f,.52f);
   if(!string.IsNullOrEmpty(s.texture)) {var tex=AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Targets/"+s.texture);if(tex!=null){mat.mainTexture=tex;mat.color=Color.white;}}
   smr.sharedMaterial=mat;
  }
  return nodes[0].gameObject;
 }
 static void Restore(Transform[] nodes, Vector3[] pos, Quaternion[] rot) {for(int i=0;i<nodes.Length;i++){nodes[i].localPosition=pos[i];nodes[i].localRotation=rot[i];}}
 static void SneakHindFeet(GameObject target, Dictionary<string,Transform> bones, Dictionary<string,Transform> donor, float scale) {
  float floor=Mathf.Min(bones["leg_front_left_toe"].position.y,bones["leg_front_right_toe"].position.y);
  float sourceFloor=new[]{"foot_f.L","foot_f.R","foot_b.L","foot_b.R"}.Min(n=>donor[n].position.y);
  foreach(string side in new[]{"left","right"}) {
   string prefix="leg_hind_"+side+"_";var paw=bones[prefix+"toe"];
   float lift=Mathf.Clamp((donor["foot_b."+(side=="left"?"L":"R")].position.y-sourceFloor)*scale,0,.12f);
   Vector3 goal=paw.position;goal.y=floor+lift;
   var chain=new[]{bones[prefix+"ankle"],bones[prefix+"bot0"],bones[prefix+"top1"],bones[prefix+"top0"]};
   // Rotate existing joints; never translate bones or stretch the torso/legs.
   for(int pass=0;pass<16;pass++) {foreach(var joint in chain){Vector3 from=paw.position-joint.position,to=goal-joint.position;if(from.sqrMagnitude>1e-8f&&to.sqrMagnitude>1e-8f)joint.rotation=Quaternion.FromToRotation(from,to)*joint.rotation;}if((paw.position-goal).sqrMagnitude<.000001f)break;}
  }
 }
 public static void Bake() {
  var source=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Source));
  var sourceNodes=source.GetComponentsInChildren<Transform>(true);var sourcePos=sourceNodes.Select(t=>t.localPosition).ToArray();var sourceRot=sourceNodes.Select(t=>t.localRotation).ToArray();
  var sourceByName=sourceNodes.GroupBy(t=>t.name).ToDictionary(g=>g.Key,g=>g.First());var sourceRest=sourceByName.ToDictionary(p=>p.Key,p=>p.Value.rotation);
  var clips=AssetDatabase.LoadAllAssetsAtPath(Source).OfType<AnimationClip>().Where(c=>!c.name.StartsWith("__preview__")).ToDictionary(c=>c.name.Split('|').Last());
  Directory.CreateDirectory("Assets/Clips");var assets=new List<string>();var reports=new List<string>();
  foreach(string file in Directory.GetFiles("Assets/Targets","*.json")) {
   // The previous creature may finish sitting/crouching. Reference heights must always use the donor rest pose.
   Restore(sourceNodes,sourcePos,sourceRot);
   var data=JsonUtility.FromJson<Target>(File.ReadAllText(file));Transform[] targetNodes;var target=Create(data,out targetNodes);
   Transform animator=targetNodes[data.animators[0].node];var byName=targetNodes.GroupBy(t=>t.name).ToDictionary(g=>g.Key,g=>g.First());
   var map=Mapping(data.prefab=="Grimhound");var links=new List<Link>();
   foreach(var t in targetNodes) {string sn;if(map.TryGetValue(t.name,out sn)) {if(!sourceByName.ContainsKey(sn))throw new Exception("Missing source bone "+sn);links.Add(new Link {target=t,source=sourceByName[sn],rest=t.rotation});}}
   var pos=targetNodes.Select(t=>t.localPosition).ToArray();var rot=targetNodes.Select(t=>t.localRotation).ToArray();
   Transform sourceHip=sourceByName["Spine_base"],targetHip=byName[data.prefab=="Grimhound"?"Spine1":"body"];
   Vector3 sourceHipRest=sourceHip.position,targetHipRest=targetHip.position;float sourceFloor=sourceByName.Where(p=>p.Key.StartsWith("foot_")).Min(p=>p.Value.position.y);
   float targetFloor=links.Where(l=>l.source.name.StartsWith("foot_")).Min(l=>l.target.position.y);
   float scale=(targetHipRest.y-targetFloor)/Mathf.Max(.1f,sourceHipRest.y-sourceFloor);
   scale=Mathf.Clamp(scale,.5f,3f);
   foreach(var entry in Actions) {
    if(entry.Key.StartsWith("Sneak") && data.prefab!="Wild Hound")continue;
    var input=clips[entry.Value];var output=new AnimationClip {name=data.prefab.Replace(' ','_')+"_"+entry.Key,frameRate=30,legacy=false};
    int samples=Mathf.CeilToInt(input.length*30);var curves=new Dictionary<Transform,AnimationCurve[]>();var previous=new Dictionary<Transform,Quaternion>();
    var support=new AnimationCurve();
    foreach(var l in links)curves[l.target]=Enumerable.Range(0,7).Select(_=>new AnimationCurve()).ToArray();
    for(int f=0;f<=samples;f++) {
     float time=Mathf.Min(input.length,f/30f);Restore(sourceNodes,sourcePos,sourceRot);Restore(targetNodes,pos,rot);input.SampleAnimation(source,time);
     foreach(var l in links)l.target.rotation=l.source.rotation*Quaternion.Inverse(sourceRest[l.source.name])*l.rest;
     // Generic root displacement is baked into the body's local pose only; Rigidbody movement remains owned by NPCAI.
     Vector3 delta=sourceHip.position-sourceHipRest;delta.x=0;delta.z=Mathf.Clamp(delta.z,-.4f,.4f);
     targetHip.position=targetHipRest+delta*scale;
     if(data.prefab=="Wild Hound" && entry.Key=="SneakWalk") SneakHindFeet(target,byName,sourceByName,scale);
     if(data.prefab=="Wild Hound") {
      float pawFloor=links.Where(l=>l.target.name.EndsWith("_toe")).Min(l=>target.transform.InverseTransformPoint(l.target.position).y);
      float skinFloor=float.MaxValue;
      foreach(var skin in data.models) {
       var matrices=skin.bones.Select((index,j)=>target.transform.worldToLocalMatrix*targetNodes[index].localToWorldMatrix*Matrix(skin.bindposes[j].values)).ToArray();
       for(int v=0;v<skin.vertices.Length;v++) {
        var weights=skin.weights[v].values;var indices=skin.indices[v].values;Vector3 point=Vector3.zero;float tail=0;
        for(int j=0;j<Math.Min(4,weights.Length);j++) {if(weights[j]<=0)continue;point+=matrices[indices[j]].MultiplyPoint3x4(skin.vertices[v])*weights[j];if(targetNodes[skin.bones[indices[j]]].name.StartsWith("tail"))tail+=weights[j];}
        if(tail<.5f)skinFloor=Mathf.Min(skinFloor,point.y);
       }
      }
      support.AddKey(time,skinFloor-pawFloor);
     }
     foreach(var l in links) {
      var t=l.target;var q=t.localRotation;Quaternion last;if(previous.TryGetValue(t,out last)&&Quaternion.Dot(last,q)<0)q=new Quaternion(-q.x,-q.y,-q.z,-q.w);previous[t]=q;
      float[] values={t.localPosition.x,t.localPosition.y,t.localPosition.z,q.x,q.y,q.z,q.w};
      for(int c=0;c<7;c++)curves[t][c].AddKey(time,values[c]);
     }
    }
    foreach(var p in curves) {
     string path=AnimationUtility.CalculateTransformPath(p.Key,animator);
     string[] properties={"m_LocalPosition.x","m_LocalPosition.y","m_LocalPosition.z","m_LocalRotation.x","m_LocalRotation.y","m_LocalRotation.z","m_LocalRotation.w"};
     for(int c=0;c<7;c++){for(int k=0;k<p.Value[c].length;k++){AnimationUtility.SetKeyLeftTangentMode(p.Value[c],k,AnimationUtility.TangentMode.Linear);AnimationUtility.SetKeyRightTangentMode(p.Value[c],k,AnimationUtility.TangentMode.Linear);}output.SetCurve(path,typeof(Transform),properties[c],p.Value[c]);}
    }
    if(data.prefab=="Wild Hound") {for(int k=0;k<support.length;k++){AnimationUtility.SetKeyLeftTangentMode(support,k,AnimationUtility.TangentMode.Linear);AnimationUtility.SetKeyRightTangentMode(support,k,AnimationUtility.TangentMode.Linear);}output.SetCurve("__NPCAI_PawSupport",typeof(Transform),"m_LocalPosition.y",support);}
    bool loop=entry.Key=="Idle"||entry.Key=="Walk"||entry.Key=="Run"||entry.Key=="LieIdle"||entry.Key=="SitIdle"||entry.Key.StartsWith("Sneak");var settings=AnimationUtility.GetAnimationClipSettings(output);settings.loopTime=loop;settings.stopTime=input.length;AnimationUtility.SetAnimationClipSettings(output,settings);output.wrapMode=loop?WrapMode.Loop:WrapMode.ClampForever;
    string pathOut="Assets/Clips/"+output.name+".anim";AssetDatabase.DeleteAsset(pathOut);AssetDatabase.CreateAsset(output,pathOut);assets.Add(pathOut);
    reports.Add(output.name+" length="+output.length+" bindings="+AnimationUtility.GetCurveBindings(output).Length+" loop="+loop);
   }
   reports.Add(data.prefab+" mapped="+links.Count+" bodyScale="+scale);
   UnityEngine.Object.DestroyImmediate(target);
  }
  AssetDatabase.SaveAssets();Directory.CreateDirectory("Build");
  var manifest=BuildPipeline.BuildAssetBundles("Build",new[]{new AssetBundleBuild {assetBundleName="npcai_dogs.bundle",assetNames=assets.ToArray()}},BuildAssetBundleOptions.ChunkBasedCompression|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneWindows64);
  if(manifest==null)throw new Exception("AssetBundle build did not produce a manifest; check enabled Unity modules and build errors");
  File.WriteAllLines("Build/retarget-report.txt",reports);UnityEngine.Object.DestroyImmediate(source);Debug.Log("DOG BAKE: "+assets.Count+" retargeted clips");
 }
}
