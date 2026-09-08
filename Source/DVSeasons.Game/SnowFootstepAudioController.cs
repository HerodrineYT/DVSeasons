using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace DVSeasons.Mod
{
    /// <summary>Replaces a native footstep with the supplied snow recording on exposed snow.</summary>
    internal sealed class SnowFootstepAudioController : IDisposable
    {
        private const string HarmonyId="Herodrine.DVSeasons.SnowFootsteps";
        private static SnowFootstepAudioController active;
        private readonly Harmony harmony=new Harmony(HarmonyId);
        private readonly List<AudioClip> clips=new List<AudioClip>();
        private static readonly RaycastHit[] shelterHits=new RaycastHit[16];
        private Func<Collider,float> vehicleSnowRemaining;
        private Collider currentSurfaceCollider;
        private Vector3 currentSurfacePosition;
        private int currentSurfaceFrame=-1;
        private float coverage;
        private bool enabled;
        private int nextClip;
        private bool patched;
        private bool replacementConfirmed;
        private bool heatClearedConfirmed;

        public SnowFootstepAudioController(string modPath)
        {
            Load(Path.Combine(modPath??string.Empty,"Audio","snow_step_1.wav"));
            Load(Path.Combine(modPath??string.Empty,"Audio","snow_step_2.wav"));
            if(clips.Count==0)
            {
                Debug.LogWarning("[DVSeasons] Snow footstep recordings are missing; native footsteps remain active.");
                return;
            }
            var target=AccessTools.Method(typeof(FootstepsAudioPlayer),nameof(FootstepsAudioPlayer.PlayFootstepSound));
            var surfaceTarget=AccessTools.Method(typeof(FootstepsAudioPlayer),"GetFootstepSurface");
            if(target==null || surfaceTarget==null) return;
            harmony.Patch(surfaceTarget,postfix:new HarmonyMethod(
                typeof(SnowFootstepAudioController),nameof(SurfacePostfix)));
            harmony.Patch(target,new HarmonyMethod(typeof(SnowFootstepAudioController),nameof(Prefix)));
            active=this;patched=true;
            Debug.Log("[DVSeasons] Snow footsteps ready: two trimmed clips at native footstep volume.");
        }

        public void SetCoverage(float value,bool isEnabled)
        {coverage=Mathf.Clamp01(value);enabled=isEnabled;}

        public void SetVehicleSnowRemaining(Func<Collider,float> remaining)
        {vehicleSnowRemaining=remaining;}

        private static void SurfacePostfix(Vector3 footstepPosition,RaycastHit hit)
        {
            var controller=active;
            if(controller==null) return;
            controller.currentSurfaceCollider=hit.collider;
            controller.currentSurfacePosition=footstepPosition;
            controller.currentSurfaceFrame=Time.frameCount;
        }

        private static bool Prefix(FootstepsAudioPlayer __instance,
            FootstepsAudioScriptableObject.SurfaceType surface,Vector3 footstepPosition,
            float volume,Transform audioParent)
        {
            var controller=active;
            var provider=__instance!=null?__instance.provider:null;
            if(controller==null || provider==null || controller.clips.Count==0 ||
                !controller.ShouldUseSnow(surface,footstepPosition,__instance.transform)) return true;
            var clip=controller.clips[controller.nextClip++%controller.clips.Count];
            // RequestPlayFootstepSound has already selected the game's exact walk,
            // run or crouch volume. Reusing it makes this recording as loud as the
            // normal step rather than stacking a second full-volume sound over it.
            provider.Play(clip,footstepPosition,volume,UnityEngine.Random.Range(.965f,1.035f),audioParent);
            if(!controller.replacementConfirmed)
            {
                controller.replacementConfirmed=true;
                Debug.Log("[DVSeasons] Custom snow footstep playback confirmed on surface "+surface+".");
            }
            return false;
        }

        private bool ShouldUseSnow(FootstepsAudioScriptableObject.SurfaceType surface,
            Vector3 position,Transform player)
        {
            if(!enabled || clips.Count==0) return false;
            if(surface==FootstepsAudioScriptableObject.SurfaceType.Water ||
                surface==FootstepsAudioScriptableObject.SurfaceType.Liquid ||
                surface==FootstepsAudioScriptableObject.SurfaceType.Ladder) return false;
            if(surface!=FootstepsAudioScriptableObject.SurfaceType.Snow && coverage<.08f) return false;
            var surfaceCollider=GetCurrentSurface(position);
            var car=FindTrainCar(surfaceCollider);
            var vehicleRemaining=vehicleSnowRemaining!=null && surfaceCollider!=null
                ? Mathf.Clamp01(vehicleSnowRemaining(surfaceCollider)) : 1f;
            if(surfaceCollider!=null && vehicleSnowRemaining!=null &&
                coverage*vehicleRemaining<.08f)
            {
                if(!heatClearedConfirmed)
                {
                    heatClearedConfirmed=true;
                    var label=car!=null ? car.name+" (instance "+car.GetInstanceID()+")" :
                        "detached cab collider";
                    Debug.Log("[DVSeasons] Native footstep retained on heat-cleared locomotive "+label+".");
                }
                return false;
            }
            if(surface==FootstepsAudioScriptableObject.SurfaceType.Snow) return true;

            return !HasLowShelter(position,player);
        }

        private Collider GetCurrentSurface(Vector3 position)
        {
            if(currentSurfaceFrame!=Time.frameCount ||
                (currentSurfacePosition-position).sqrMagnitude>.04f) return null;
            return currentSurfaceCollider;
        }

        private static TrainCar FindTrainCar(Collider collider)
        {
            if(collider==null) return null;
            var car=collider.GetComponentInParent<TrainCar>();
            if(car==null && collider.attachedRigidbody!=null)
                car=collider.attachedRigidbody.GetComponentInParent<TrainCar>();
            return car;
        }

        private static bool HasLowShelter(Vector3 position,Transform player)
        {
            var count=Physics.RaycastNonAlloc(position+Vector3.up*.12f,Vector3.up,
                shelterHits,3.1f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
            for(var i=0;i<count;i++)
            {
                var collider=shelterHits[i].collider;
                if(collider==null || BelongsToPlayer(collider.transform,player)) continue;
                var extents=collider.bounds.extents;
                // A real cab/building ceiling has a broad footprint. This rejects
                // hands, tools, cables and tree branches crossing the vertical ray.
                if(extents.x<.55f || extents.z<.55f) continue;
                return true;
            }
            return false;
        }

        private static bool BelongsToPlayer(Transform candidate,Transform player)
        {
            if(candidate==null || player==null) return false;
            var playerRoot=player.root;
            return candidate==player || candidate.IsChildOf(player) || player.IsChildOf(candidate) ||
                (playerRoot!=null && candidate.root==playerRoot);
        }

        private void Load(string path)
        {
            try
            {
                if(!File.Exists(path)) return;
                var bytes=File.ReadAllBytes(path);
                int channels,frequency;float[] samples;
                if(!TryReadPcm16Wave(bytes,out channels,out frequency,out samples))
                {
                    Debug.LogWarning("[DVSeasons] Snow footstep is not PCM16 WAV: "+path);
                    return;
                }
                var clip=AudioClip.Create(Path.GetFileNameWithoutExtension(path),
                    samples.Length/channels,channels,frequency,false);
                clip.SetData(samples,0);clips.Add(clip);
            }
            catch(Exception exception)
            {Debug.LogWarning("[DVSeasons] Could not load snow footstep '"+path+"': "+exception.Message);}
        }

        private static bool TryReadPcm16Wave(byte[] bytes,out int channels,out int frequency,
            out float[] samples)
        {
            channels=0;frequency=0;samples=null;
            if(bytes==null || bytes.Length<44 || ReadAscii(bytes,0,4)!="RIFF" ||
                ReadAscii(bytes,8,4)!="WAVE") return false;
            int format=0,bits=0,dataOffset=0,dataLength=0;
            for(var offset=12;offset+8<=bytes.Length;)
            {
                var id=ReadAscii(bytes,offset,4);var length=ReadInt32(bytes,offset+4);
                var payload=offset+8;if(length<0 || payload+length>bytes.Length) return false;
                if(id=="fmt " && length>=16)
                {
                    format=ReadInt16(bytes,payload);channels=ReadInt16(bytes,payload+2);
                    frequency=ReadInt32(bytes,payload+4);bits=ReadInt16(bytes,payload+14);
                }
                else if(id=="data") {dataOffset=payload;dataLength=length;}
                offset=payload+length+(length&1);
            }
            if(format!=1 || bits!=16 || channels<1 || channels>2 || frequency<8000 ||
                dataOffset==0 || dataLength<2) return false;
            var count=dataLength/2;samples=new float[count];
            for(var i=0;i<count;i++) samples[i]=ReadInt16(bytes,dataOffset+i*2)/32768f;
            return true;
        }

        private static string ReadAscii(byte[] bytes,int offset,int count)
        {return System.Text.Encoding.ASCII.GetString(bytes,offset,count);}
        private static short ReadInt16(byte[] bytes,int offset)
        {return (short)(bytes[offset]|bytes[offset+1]<<8);}
        private static int ReadInt32(byte[] bytes,int offset)
        {return bytes[offset]|bytes[offset+1]<<8|bytes[offset+2]<<16|bytes[offset+3]<<24;}

        public void Dispose()
        {
            if(active==this) active=null;
            if(patched) harmony.UnpatchAll(HarmonyId);
            foreach(var clip in clips) if(clip!=null) UnityEngine.Object.Destroy(clip);
            clips.Clear();patched=false;coverage=0;enabled=false;replacementConfirmed=false;
            heatClearedConfirmed=false;vehicleSnowRemaining=null;currentSurfaceCollider=null;
            currentSurfaceFrame=-1;
        }
    }
}
