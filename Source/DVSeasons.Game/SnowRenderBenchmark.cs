using System;
using System.Globalization;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Explicit, local-only diagnostic. It never changes season, snow history,
    // settings, networking or simulation. Only render-command emission changes.
    internal static class SnowRenderBenchmark
    {
        private static readonly SnowRenderBenchmarkSession session=new SnowRenderBenchmarkSession();
        private static Camera camera;
        private static Vector3 position;
        private static Quaternion rotation;
        private static int width,height,limit,lastFrame=-1;
        private static float coverage,startCoverage,fieldOfView,orthoSize,nearClip,farClip;
        private static int cullingMask;
        private static bool hdr,stereo,orthographic;
        private static bool ready,poseKnown,previousRendered;
        private static SeasonKind season;
        private static bool seasonKnown;
        public static bool Active => session.Active;
        public static bool CanStart => ready && camera!=null && !session.Active;
        public static int SecondsRemaining => (int)Math.Ceiling(session.SecondsRemaining);
        public static bool SkipVehicles => session.SkipVehicleSurfaces;
        public static bool SkipShading => session.SkipShading;
        public static bool SkipAll => session.SkipAll;
        public static void SetSeason(SeasonKind current)
        {
            if(Active && seasonKnown && current!=season)Cancel("season changed");
            season=current;seasonKnown=true;
        }

        public static void Ready(Camera current,float amount,int snowLimit)
        {
            if(Active && (camera!=current || Math.Abs(startCoverage-amount)>.01f || limit!=snowLimit))
                Cancel("camera, snow coverage or snow limit changed");
            camera=current;coverage=amount;limit=snowLimit;ready=current!=null;
        }
        public static void Unavailable()
        {Cancel("snow renderer unavailable");ready=false;camera=null;}
        public static void Start()
        {
            if(!CanStart)return;
            session.Start();startCoverage=coverage;lastFrame=-1;poseKnown=false;previousRendered=false;
            Debug.Log("[DVSeasons] Snow render A/B started. Close settings, stop the train and keep the camera still. " +
                "Only temporary rendering changes; snow history and weather are preserved. " +
                "CPU+GPU whole-frame comparison, not isolated GPU timestamps; snow-limit="+limit+
                "; resolution="+camera.pixelWidth+"x"+camera.pixelHeight+"; HDR="+camera.allowHDR+"; stereo="+camera.stereoEnabled);
        }
        public static void Cancel(string reason="canceled by user")
        {
            if(!Active)return;
            session.Cancel();previousRendered=false;poseKnown=false;
            Debug.Log("[DVSeasons] Snow render A/B canceled: "+reason+". Normal rendering restored.");
        }
        public static void BeforeRender(Camera current)
        {
            if(!Active || current!=camera || lastFrame==Time.frameCount)return;
            if(!session.Waiting && lastFrame>=0 && Time.frameCount>lastFrame+1)
            {Cancel("world camera stopped rendering");return;}
            lastFrame=Time.frameCount;
            if(Time.timeScale<=0f)
            {if(!session.Waiting)Cancel("game paused");previousRendered=false;return;}
            if(!session.Waiting)
            {
                if(!poseKnown)
                {
                    position=current.transform.position;rotation=current.transform.rotation;
                    width=current.pixelWidth;height=current.pixelHeight;fieldOfView=current.fieldOfView;
                    orthoSize=current.orthographicSize;nearClip=current.nearClipPlane;farClip=current.farClipPlane;
                    cullingMask=current.cullingMask;hdr=current.allowHDR;stereo=current.stereoEnabled;orthographic=current.orthographic;
                    poseKnown=true;
                }
                else if((current.transform.position-position).sqrMagnitude>.0625f ||
                    Quaternion.Angle(current.transform.rotation,rotation)>1f || current.pixelWidth!=width || current.pixelHeight!=height ||
                    current.fieldOfView!=fieldOfView || current.orthographicSize!=orthoSize || current.nearClipPlane!=nearClip ||
                    current.farClipPlane!=farClip || current.cullingMask!=cullingMask || current.allowHDR!=hdr ||
                    current.stereoEnabled!=stereo || current.orthographic!=orthographic)
                {Cancel("camera moved or camera settings changed");return;}
            }
            session.Tick(Time.unscaledDeltaTime,previousRendered);
            previousRendered=true;
            SnowRenderBenchmarkResult result;
            while(session.TryTakeCompletedResult(out result))
            {
                Debug.Log("[DVSeasons] Snow render A/B phase="+result.Phase+
                    "; frames="+result.FrameCount+"; FPS="+result.Fps.ToString("F2",CultureInfo.InvariantCulture)+
                    "; frame-ms="+(result.FrameCount>0?result.MeasuredSeconds*1000/result.FrameCount:0).ToString("F3",CultureInfo.InvariantCulture)+
                    "; max-ms="+result.MaximumFrameMilliseconds.ToString("F3",CultureInfo.InvariantCulture)+
                    "; missing-render-frames="+result.MissingRenderFrames+"; contaminated="+result.Contaminated+
                    "; whole-game CPU+GPU, not a GPU timestamp.");
            }
            if(!Active)Debug.Log("[DVSeasons] Snow render A/B complete. Normal rendering restored. Compare Baseline and RestoredBaseline before interpreting intermediate phases.");
        }
    }
}
