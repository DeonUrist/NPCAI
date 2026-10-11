using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using HutongGames.PlayMaker;

public sealed partial class Checks
{
    [Serializable] class Node { public string path,name; public int parent; public Vector3 position,scale; public Quaternion rotation; }
    [Serializable] class Floats { public float[] values; }
    [Serializable] class Ints { public int[] values; }
    [Serializable] class Skin { public int node; public Vector3[] vertices,normals; public Vector2[] uv; public int[] triangles,bones; public Floats[] weights,bindposes; public Ints[] indices; }
    [Serializable] class An { public int node; }
    [Serializable] class Model { public string prefab; public Node[] nodes; public Skin[] models; public An[] animators; }
    GameObject ExportedRig(string file, int offset)
    {
        var model = new Model();
        using(var r=new BinaryReader(File.OpenRead(file))) {
            model.prefab=r.ReadString();model.nodes=new Node[r.ReadInt32()];
            for(int i=0;i<model.nodes.Length;i++)model.nodes[i]=new Node {name=r.ReadString(),parent=r.ReadInt32(),position=ReadVector(r),rotation=new Quaternion(r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle()),scale=ReadVector(r)};
            model.animators=new[]{new An {node=r.ReadInt32()}};model.models=new Skin[r.ReadInt32()];
            for(int j=0;j<model.models.Length;j++) {
                var s=new Skin {node=r.ReadInt32()};int count=r.ReadInt32();s.vertices=new Vector3[count];s.normals=new Vector3[count];s.uv=new Vector2[count];s.weights=new Floats[count];s.indices=new Ints[count];
                for(int i=0;i<count;i++)s.vertices[i]=ReadVector(r);for(int i=0;i<count;i++)s.normals[i]=ReadVector(r);for(int i=0;i<count;i++)s.uv[i]=new Vector2(r.ReadSingle(),r.ReadSingle());
                for(int i=0;i<count;i++)s.weights[i]=new Floats {values=new[]{r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle()}};
                for(int i=0;i<count;i++)s.indices[i]=new Ints {values=new[]{r.ReadInt32(),r.ReadInt32(),r.ReadInt32(),r.ReadInt32()}};
                s.triangles=new int[r.ReadInt32()];for(int i=0;i<s.triangles.Length;i++)s.triangles[i]=r.ReadInt32();s.bones=new int[r.ReadInt32()];for(int i=0;i<s.bones.Length;i++)s.bones[i]=r.ReadInt32();
                s.bindposes=new Floats[s.bones.Length];for(int i=0;i<s.bindposes.Length;i++){s.bindposes[i]=new Floats {values=new float[16]};for(int k=0;k<16;k++)s.bindposes[i].values[k]=r.ReadSingle();}model.models[j]=s;
            }
        }
        var nodes = new Transform[model.nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            var n = model.nodes[i]; var t = new GameObject(n.name).transform; nodes[i] = t;
            if (n.parent >= 0) t.SetParent(nodes[n.parent], false);
            t.localPosition = i == 0 ? Vector3.zero : n.position; t.localRotation = i == 0 ? Quaternion.identity : n.rotation; t.localScale = n.scale;
        }
        var root = nodes[0].gameObject; root.name = model.prefab + "(Clone)"; root.layer=10;root.transform.position = new Vector3(10000 + offset * 5, 3, 10000); fixtures.Add(root);
        foreach (var s in model.models)
        {
            var mesh = new Mesh(); mesh.vertices = s.vertices; mesh.normals = s.normals; mesh.uv = s.uv; mesh.triangles = s.triangles;
            mesh.bindposes = s.bindposes.Select(v => { var m = new Matrix4x4(); for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) m[r,c] = v.values[r * 4 + c]; return m; }).ToArray();
            mesh.boneWeights = s.weights.Select((v, i) => { var w = v.values; var b = s.indices[i].values; return new BoneWeight { weight0 = w[0],weight1 = w[1],weight2 = w.Length > 2 ? w[2] : 0,weight3 = w.Length > 3 ? w[3] : 0,boneIndex0 = b[0],boneIndex1 = b[1],boneIndex2 = b.Length > 2 ? b[2] : 0,boneIndex3 = b.Length > 3 ? b[3] : 0 }; }).ToArray();
            var smr = nodes[s.node].gameObject.AddComponent<SkinnedMeshRenderer>(); smr.sharedMesh = mesh; smr.bones = s.bones.Select(i => nodes[i]).ToArray(); smr.rootBone = smr.bones[0]; smr.updateWhenOffscreen = true;
        }
        nodes[model.animators[0].node].gameObject.AddComponent<Animator>();
        var body = root.AddComponent<Rigidbody>(); body.useGravity = false; var capsule = root.AddComponent<CapsuleCollider>(); capsule.height = 1; capsule.radius = .2f;
        var floor = new GameObject("Verifier.Floor"); floor.AddComponent<BoxCollider>().size = new Vector3(4,.1f,4); floor.transform.position = root.transform.position + Vector3.down * .55f; fixtures.Add(floor);
        Physics.SyncTransforms(); return root;
    }
    static Vector3 ReadVector(BinaryReader r) {return new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());}
}
