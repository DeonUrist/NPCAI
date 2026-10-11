using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using UnityEngine;
using HutongGames.PlayMaker;

public sealed partial class Checks
{
    void RunCompanionFixes(string source)
    {
        var assembly = Chainloader.PluginInfos["apocalypter.dogcompanion"].Instance.GetType().Assembly;
        var dogComponentType = assembly.GetType("DogCompanion.Dog"); var npcType = assembly.GetType("DogCompanion.Npc");
        var root = ExportedRig(Path.Combine(source, "Wild_Hound.bin"), fixtures.Count);
        Call(assembly.GetType("DogCompanion.Manager"), null, "Strip", root);
        var component = (MonoBehaviour)root.AddComponent(dogComponentType); Call(dogComponentType, component, "Update");
        var dog = Registered()[root.GetInstanceID()]; var type = dog.GetType(); var driver = Get(type, dog, "Companion");
        var driverType = driver.GetType(); var clips = (Dictionary<string,AnimationClip>)Get(type, dog, "Clips");
        Check(clips.ContainsKey("SneakWalk"), "Updated bundle and optional compatibility hooks load in native Unity");
        var paws = root.GetComponentsInChildren<Transform>().Where(t => t.name.EndsWith("_toe")).ToArray();
        float worst = 0f;
        for(int i=0;i<16;i++)
        {
            clips["SneakWalk"].SampleAnimation(root.GetComponentInChildren<Animator>().gameObject, clips["SneakWalk"].length*i/16f);
            float front = paws.Where(p=>p.name.Contains("front")).Min(p=>p.position.y);
            foreach(var hind in paws.Where(p=>p.name.Contains("hind"))) worst=Mathf.Max(worst,hind.position.y-front);
        }
        report.Add("SNEAK maximum hind paw lift over front support: " + worst.ToString("F3") + " m");
        Check(worst < .16f, "Moving sneak hind paws follow the grounded gait rather than floating above it");
        Call(DogType, null, "RestoreCompanion", root); Set(driverType,driver,"WantsSneak",true); Set(driverType,driver,"Speed",1f);
        Call(DogType,null,"TickDog",dog,.016f); Check((string)Get(type,dog,"Playing")=="SneakWalk", "Quiet follow movement selects the low walk");
        var enemy = new GameObject("Verifier.Enemy"); fixtures.Add(enemy); enemy.transform.position=root.transform.position+Vector3.forward*.6f;
        var npc=Activator.CreateInstance(npcType,true); Set(npcType,npc,"Go",enemy);Set(npcType,npc,"Transform",enemy.transform);Set(npcType,npc,"Health",new FsmFloat {Value=100f});
        Set(dogComponentType,component,"_target",npc);
        var args = new object[]{component,6f}; Call(Mod.GetType("NPCAI.DogCompanionCompat"),null,"BeforeStep",args);
        Check((float)args[1]==6f, "A newly acquired live target immediately bypasses the sneak movement cap");
        Call(driverType,driver,"SyncTarget"); Call(DogType,null,"TickDog",dog,.016f);
        Check(!((string)Get(type,dog,"Playing")).StartsWith("Sneak"), "Attack/chase leaves the sneak animation before contact");
        var player = new GameObject("Verifier.Player"); fixtures.Add(player);
        var agent = Agent(enemy); var agentType=agent.GetType();
        Set(agentType,agent,"Human",true);Set(agentType,agent,"Hostile",new HashSet<string>{"Player"});Set(agentType,agent,"Target",player);
        Set(agentType,agent,"State",Enum.Parse(Mod.GetType("NPCAI.Senses+State"),"Combat"));
        ((IDictionary)Get(SensesType,null,"_agents"))[enemy.GetInstanceID()]=agent;
        var action = new CountContact(); var fsm=Damage(enemy);action.Init(fsm.Fsm.States[0]);
        Check(!(bool)Call(Mod.GetType("NPCAI.DogCompanionCompat"),null,"BeforeDistraction",action), "Overhaul-managed enemies bypass Dog Companion's sticky detection redirect");
        var brain=Mod.GetType("NPCAI.Brain");var brainNpc=Mod.GetType("NPCAI.Brain+Npc");var n=Activator.CreateInstance(brainNpc,true);
        Set(brainNpc,n,"Owner",enemy);Set(brainNpc,n,"T",enemy.transform);Set(brainNpc,n,"Target",new FsmGameObject{Value=player});
        var hide=new GameObject("Verifier.Cover");fixtures.Add(hide);hide.transform.position=root.transform.position+Vector3.forward*8f;
        Set(brain,null,"_hides",new[]{hide});Set(brain,null,"_nextHides",Time.time+100f);
        Call(brain,null,"CompanionCoverCheck",n,Time.time);
        Check(Get(brainNpc,n,"Mode").ToString()=="Cover" && (GameObject)Get(brainNpc,n,"DogCoverThreat")==root, "Nearby companion triggers temporary cover when suitable cover exists");
        Check(((FsmGameObject)Get(brainNpc,n,"Target")).Value==player, "Cover retreat keeps the existing combat target rather than permanently redirecting it to the dog");
        Set(brainNpc,n,"Dist",1f);Set(brainNpc,n,"CoverArrived",true);
        Call(brain,null,"CoverThink",n,Time.time+.2f,"trigger",1,player.transform.position);
        Check(Get(brainNpc,n,"Mode").ToString()=="Cover", "Nearby combat target does not immediately cancel the dog-triggered retreat");
        Call(brain,null,"CoverThink",n,Time.time+5f,"trigger",1,player.transform.position);
        Check(Get(brainNpc,n,"Mode").ToString()=="Chase", "Dog cover expires after four seconds and resumes combat");
        Call(brain,null,"CompanionCoverCheck",n,Time.time+5.1f);
        Check(Get(brainNpc,n,"Mode").ToString()=="Chase", "Cooldown prevents permanent repeat retreats while the dog stays close");
        Set(dogComponentType,component,"_target",null);Call(driverType,driver,"SyncTarget");
        Call(DogType,null,"RestoreCompanion",root);Set(type,dog,"Pose",Enum.Parse(type.GetField("Pose",Flags).FieldType,"Sitting"));Set(type,dog,"Playing","SitIdle");Set(type,dog,"Time",.5f);Set(type,dog,"Fade",1f);
        Set(dogComponentType,component,"_mode",Enum.ToObject(dogComponentType.GetNestedType("Mode",Flags),1));
        var bones=(Transform[])Get(type,dog,"Bones");clips["SitIdle"].SampleAnimation(root.GetComponentInChildren<Animator>().gameObject,.5f);
        var rotations=bones.Select(b=>b.localRotation).ToArray();var positions=bones.Select(b=>b.localPosition).ToArray();
        Call(DogType,null,"LateCompanion",root);
        Check(bones.Select((b,i)=>Quaternion.Angle(b.localRotation,rotations[i])).Max()<.05f && bones.Select((b,i)=>Vector3.Distance(b.localPosition,positions[i])).Max()<.005f, "Car sitting keeps the original torso and head bone pose without deformation or neck bending");
        report.Add("Focused checks only; clip clocks are advanced explicitly. Actual terrain/vehicle visuals and retreat navigation still need normal gameplay review.");
    }
}
