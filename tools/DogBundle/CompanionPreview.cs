using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

public static partial class DogBuilder {
 public static void CompanionPreview() {
  Directory.CreateDirectory("Build/CompanionPreview");
  var data=JsonUtility.FromJson<Target>(File.ReadAllText("Assets/Targets/Wild_Hound.json"));
  RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;
  var light=new GameObject("PreviewLight").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(45,-30,0);
  foreach(string action in new[]{"Idle","Walk","Run","SitDown","SitIdle"}) {
   Transform[] nodes;var dog=Create(data,out nodes);var rig=nodes[data.animators[0].node];
   var marker=new GameObject("__NPCAI_PawSupport").transform;marker.SetParent(rig,false);
   var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Clips/Wild_Hound_"+action+".anim");clip.SampleAnimation(rig.gameObject,clip.length*(action=="SitDown"?.6f:.25f));
   float depth=-nodes.First(t=>t.name=="IKT_Leg_L").localPosition.y*rig.localScale.y;
   float paw=nodes.Where(t=>t.name.EndsWith("_toe")).Min(t=>dog.transform.InverseTransformPoint(t.position).y);
   rig.position+=Vector3.up*(-depth-paw-marker.localPosition.y);
   if(action.StartsWith("Sit")) {
    dog.transform.localScale=Vector3.one*.9f;dog.transform.position+=Vector3.forward*NPCAI.DogCompanionSeating.ForwardOffset;
    dog.transform.position+=Vector3.up*NPCAI.DogCompanionSeating.UpOffset;
   }
   foreach(var skin in dog.GetComponentsInChildren<SkinnedMeshRenderer>()) {
    var mesh=UnityEngine.Object.Instantiate(skin.sharedMesh);var vertices=mesh.vertices;var weights=mesh.boneWeights;var binds=mesh.bindposes;
    var matrices=skin.bones.Select((b,i)=>skin.transform.worldToLocalMatrix*b.localToWorldMatrix*binds[i]).ToArray();
    for(int i=0;i<vertices.Length;i++){var w=weights[i];var v=vertices[i];vertices[i]=matrices[w.boneIndex0].MultiplyPoint3x4(v)*w.weight0+matrices[w.boneIndex1].MultiplyPoint3x4(v)*w.weight1+matrices[w.boneIndex2].MultiplyPoint3x4(v)*w.weight2+matrices[w.boneIndex3].MultiplyPoint3x4(v)*w.weight3;}
    mesh.vertices=vertices;mesh.RecalculateNormals();mesh.RecalculateBounds();var visible=new GameObject("PosedMesh");visible.transform.SetParent(skin.transform,false);visible.AddComponent<MeshFilter>().sharedMesh=mesh;visible.AddComponent<MeshRenderer>().sharedMaterial=skin.sharedMaterial;skin.enabled=false;
   }
   var plane=GameObject.CreatePrimitive(PrimitiveType.Cube);plane.transform.position=new Vector3(0,-depth*dog.transform.localScale.y-.05f,0);plane.transform.localScale=action.StartsWith("Sit")?new Vector3(1.6f,.1f,1.6f):new Vector3(3,.1f,3);plane.GetComponent<Renderer>().sharedMaterial=new Material(Shader.Find("Standard")){color=new Color(.3f,.35f,.4f)};
   var camera=new GameObject("PreviewCamera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=1.3f;camera.nearClipPlane=.01f;camera.farClipPlane=20;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.14f,.17f);camera.transform.position=new Vector3(3,1.1f,2);camera.transform.LookAt(new Vector3(0,.1f,0));
   var render=new RenderTexture(768,768,24);camera.targetTexture=render;camera.Render();RenderTexture.active=render;var texture=new Texture2D(768,768,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,768,768),0,0);texture.Apply();File.WriteAllBytes("Build/CompanionPreview/"+action+".png",texture.EncodeToPNG());RenderTexture.active=null;
   UnityEngine.Object.DestroyImmediate(texture);UnityEngine.Object.DestroyImmediate(render);UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(plane);UnityEngine.Object.DestroyImmediate(dog);
  }
  UnityEngine.Object.DestroyImmediate(light.gameObject);
 }
}
