using System.Collections.Generic;
using UnityEngine;

namespace NPCAI
{
    internal static class SimpleBandits
    {
        private static readonly Dictionary<int,GameObject> Members=new Dictionary<int,GameObject>();
        private static readonly HashSet<int> Combat=new HashSet<int>();
        private static readonly Dictionary<int,GameObject> Targets=new Dictionary<int,GameObject>();
        internal static void Target(GameObject npc,GameObject target){if(Has(npc))Targets[npc.GetInstanceID()]=target;}
        internal static GameObject Goal(GameObject npc){if(npc!=null&&Targets.TryGetValue(npc.GetInstanceID(),out var target))return target;return Senses.PlayerObj;}
        internal static void Set(GameObject npc,bool enabled)
        {Set(npc,enabled,false);}
        internal static void Set(GameObject npc,bool enabled,bool combat)
        {if(npc==null)return;int id=npc.GetInstanceID();if(enabled)Members[id]=npc;else{Members.Remove(id);Targets.Remove(id);}if(enabled&&combat)Combat.Add(id);else Combat.Remove(id);}
        internal static bool Has(GameObject npc)
        {return npc!=null&&Members.TryGetValue(npc.GetInstanceID(),out var member)&&member==npc;}
        internal static bool Fighting(GameObject npc){return Has(npc)&&npc.transform.parent==null&&Combat.Contains(npc.GetInstanceID());}
        internal static void Clear(){Members.Clear();Combat.Clear();Targets.Clear();}
        internal static void Remove(GameObject npc){if(ReferenceEquals(npc,null))return;int id=npc.GetInstanceID();Members.Remove(id);Combat.Remove(id);Targets.Remove(id);}
    }

    internal static partial class Senses
    {
        private static void SimpleLook(Agent a,float now)
        {
            a.NextLook=now+0.8f+a.Stagger;
            if(!SimpleBandits.Fighting(a.Owner))return;
            var target=SimpleBandits.Goal(a.Owner);
            if(target==null){Release(a);a.State=State.Idle;a.Target=null;if(a.DetectedVar!=null)a.DetectedVar.Value=null;return;}
            // Dismount is the combat handoff. Keep the real player; no sight acquisition, ghosts, search or forgetting.
            if(a.Ghost!=null||a.State!=State.Combat||a.Target!=target)Release(a);
            a.State=State.Combat;a.Target=target;a.UnseenFor=0f;a.LastSeen=target.transform.position;
            if(a.DetectedVar!=null)a.DetectedVar.Value=target;
        }
        internal static bool SimpleSight(Transform from,GameObject owner,GameObject target)
        {
            var eye=from.position+Vector3.up*1.3f;var to=target.transform.position+Vector3.up-eye;
            int n=Physics.RaycastNonAlloc(eye,to.normalized,SimpleHits,to.magnitude,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
            if(n==SimpleHits.Length)return false;
            for(int i=0;i<n;i++){var c=SimpleHits[i].collider;if(c!=null&&!c.transform.IsChildOf(owner.transform)&&!c.transform.IsChildOf(target.transform))return false;}
            return true;
        }
        private static readonly RaycastHit[] SimpleHits=new RaycastHit[16];
    }
    internal static partial class Brain
    {
        private static void SimpleThink(Npc n,float now)
        {
            n.NextTick=now+0.6f*n.TickScale;
            if(n.Mode==Mode.BackUp&&now<n.ModeUntil)return;
            var target=n.Target.Value;if(target==null)return;
            var delta=target.transform.position-n.T.position;delta.y=0f;n.Dist=delta.magnitude;n.ToGhost=false;
            bool hold=n.Ranged&&n.Dist<WeaponRanges.RangeOf(n.Kind)*0.7f&&Senses.SimpleSight(n.T,n.Owner,target);
            n.Los=hold;
            var desired=hold?Mode.Hold:Mode.Chase;
            if(n.Mode!=desired)SetMode(n,desired,"simple convoy bandit");
            if(hold){n.OnNav=false;return;}
            Vector3 next;float left;
            n.OnNav=Nav.Next(n.Owner,n.T.position,target.transform.position,out next,out left);
            if(n.OnNav){delta=next-n.T.position;delta.y=0f;if(Nav.HopAhead(n.T.position,next))TryHop(n,now,true);}
            // One short obstacle sweep, no friend fan, cover search, engine checks or sound investigation.
            var heading=delta.normalized;RaycastHit hit;
            if(Physics.SphereCast(n.T.position+Vector3.up,0.25f,heading,out hit,1.5f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)
                &&hit.collider!=null&&!hit.collider.transform.IsChildOf(n.T)&&!hit.collider.transform.IsChildOf(target.transform))
                heading=Quaternion.Euler(0f,((n.Owner.GetInstanceID()&1)==0?1f:-1f)*45f,0f)*heading;
            n.Heading=Mathf.Atan2(heading.x,heading.z)*Mathf.Rad2Deg;n.HasHeading=true;n.SpeedScale=1f;
        }
    }
}
