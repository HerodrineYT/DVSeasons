using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug=UnityEngine.Debug;

namespace DVSeasons.AssetBundleBuild
{
    // The reference is a renamed, otherwise unmodified pre-optimization DLL.
    // Compare actual Texture2D pixels/mips from both runtime implementations.
    public static class SeasonalTextureParityVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        static readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        static Assembly current,baseline,core;
        static AssetBundle main,winter,tracks;
        static int comparisons,mips;
        static object Get(object target,string field) {return target.GetType().GetField(field,All).GetValue(target);}
        static void Set(object target,string field,object value) {target.GetType().GetField(field,All).SetValue(target,value);}
        static object Call(object target,string name,params object[] args) {return target.GetType().GetMethod(name,All).Invoke(target,args);}
        static void Require(bool condition,string message) {if(!condition)throw new Exception(message);}
        public static void Run()
        {
            int code=0;
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var mod=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=> {
                foreach(var directory in new[]{mod,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var path=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
            var overrides=Path.Combine(root,"artifacts/verification/seasonal-parity-overrides");
            var missing=Path.Combine(root,"artifacts/verification/seasonal-parity-no-pack");
            var repositories=new List<object>();
            try
            {
                current=Assembly.LoadFrom(Path.Combine(mod,"DVSeasons.dll"));
                baseline=Assembly.LoadFrom(Path.Combine(root,"artifacts/backups/cpu-yard20260918/DVSeasons.SeasonalBaseline.dll"));
                core=Assembly.LoadFrom(Path.Combine(mod,"DVSeasons.Core.dll"));
                main=AssetBundle.LoadFromFile(Path.Combine(mod,"AssetBundles/dvseasons_dv99"));
                winter=AssetBundle.LoadFromFile(Path.Combine(mod,"AssetBundles/dvseasons_winter"));
                tracks=AssetBundle.LoadFromFile(Path.Combine(mod,"AssetBundles/dvseasons_tracks"));
                Require(main!=null && winter!=null && tracks!=null,"Seasonal parity bundles missing");
                WriteOverrides(overrides);
                foreach(bool useOverrides in new[]{false,true})
                {
                    var legacy=Repository(baseline,useOverrides?overrides:missing,true);
                    var updated=Repository(current,useOverrides?overrides:missing,true);
                    repositories.Add(legacy);repositories.Add(updated);
                    foreach(var item in new[]{
                        new Case("Foliage","T_beech_atlas_BC v2",64,64,false),
                        new Case("Foliage","parity_pine_leaves",64,64,true),
                        new Case("Bark","T_beech_forest_stumps_01_BC_SM",64,64,false),
                        new Case("Billboard","billboard_parity_deciduous",256,32,false),
                        new Case("Billboard","billboard_parity_pine",256,32,true),
                        new Case("Ballast","BallastNew_d",64,64,false),
                        new Case("Sleeper","SleeperNew_d",64,64,false),
                        new Case("Rail","RailMed_d",64,64,false),
                        new Case("RoadSurface","MB_rooftile_red_01d",64,64,false)})
                        CompareCase(legacy,updated,item,useOverrides);
                    VerifyLazyPreparation(updated);
                VerifyAtlasCache(updated);
                VerifyCancellation(updated);
                }
                var fallbackLegacy=Repository(baseline,missing,false);
                var fallbackUpdated=Repository(current,missing,false);
                repositories.Add(fallbackLegacy);repositories.Add(fallbackUpdated);
                CompareCase(fallbackLegacy,fallbackUpdated,new Case("Billboard","billboard_fallback",256,32,false),false);
                CompareCase(fallbackLegacy,fallbackUpdated,new Case("Foliage","fallback_leaves",64,64,false),false);
                var partial=Path.Combine(root,"artifacts/verification/seasonal-parity-partial-pack");
                WritePng(partial,"winter_track/early/PartialBallast.png",88);
                WritePng(partial,"winter_track/middle/PartialBallast.png",133);
                WritePng(partial,"winter/PartialBallast.png",207);
                WritePng(partial,"winter_track/early/MalformedBallast.png",88);
                WritePng(partial,"winter_track/late/MalformedBallast.png",233);
                var malformed=Path.Combine(partial,"Overrides/Seasonal/winter_track/middle/MalformedBallast.png");
                Directory.CreateDirectory(Path.GetDirectoryName(malformed));File.WriteAllBytes(malformed,new byte[]{1,2,3,4,5});
                var partialLegacy=Repository(baseline,partial,false);var partialUpdated=Repository(current,partial,false);
                repositories.Add(partialLegacy);repositories.Add(partialUpdated);
                CompareCase(partialLegacy,partialUpdated,new Case("Ballast","PartialBallast",64,64,false),true);
                CompareMalformed(partialLegacy,partialUpdated);
                var lockedLegacy=Repository(baseline,partial,false);var lockedUpdated=Repository(current,partial,false);
                repositories.Add(lockedLegacy);repositories.Add(lockedUpdated);
                // Unity's decoder can accept unidentified bytes as a placeholder.
                // An unreadable file exercises the definitive failed-load branch.
                using(var locked=new FileStream(malformed,FileMode.Open,FileAccess.Read,FileShare.None))
                    CompareMalformed(lockedLegacy,lockedUpdated,true);
                VerifyTiming(repositories[0],repositories[1]);
                Debug.Log("SEASONAL_TEXTURE_PARITY_OK: comparisons="+comparisons+" mip_checks="+mips+" exact_RGBA_bytes=true; all_categories/seasons/transitions/strengths/snow_stages; authored/overrides/fallback; lazy_preparation/cache/cancel/dispose");
            }
            catch(Exception error){Debug.LogException(error);code=1;}
            finally
            {
                AsyncGPUReadback.WaitAllRequests();
                foreach(var repository in repositories)
                {
                    repository.GetType().GetProperty("Bundle",All).SetValue(repository,null,null);
                    Set(repository,"winterBundle",null);Set(repository,"tracksBundle",null);
                    var textures=(IList)Get(repository,"ownedTextures");
                    foreach(UnityEngine.Object texture in textures)if(texture!=null)UnityEngine.Object.DestroyImmediate(texture);
                    textures.Clear();
                    ((IDisposable)repository).Dispose();
                }
                foreach(var value in owned)if(value!=null)UnityEngine.Object.DestroyImmediate(value);
                owned.Clear();if(main!=null)main.Unload(true);if(winter!=null)winter.Unload(true);if(tracks!=null)tracks.Unload(true);
            }
            EditorApplication.Exit(code);
        }
        sealed class Case
        {
            public string Category,Name;public int Width,Height;public bool Evergreen;
            public Case(string category,string name,int width,int height,bool evergreen)
            {Category=category;Name=name;Width=width;Height=height;Evergreen=evergreen;}
        }
        static object Repository(Assembly assembly,string path,bool bundled)
        {
            var repository=Activator.CreateInstance(assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true),new object[]{path});
            Set(repository,"bundleLoadFinished",true);Set(repository,"winterBundleLoadFinished",true);Set(repository,"tracksBundleLoadFinished",true);
            Set(repository,"nextBundleLoadAttempt",float.MaxValue);
            if(bundled)
            {
                repository.GetType().GetProperty("Bundle",All).SetValue(repository,main,null);
                Set(repository,"winterBundle",winter);Set(repository,"tracksBundle",tracks);
                foreach(var bundle in new[]{main,winter,tracks})foreach(var asset in bundle.GetAllAssetNames())Call(repository,"IndexAsset",asset);
            }
            return repository;
        }
        static Texture2D Source(Case item)
        {
            var source=new Texture2D(item.Width,item.Height,TextureFormat.RGBA32,true) {name=item.Name,filterMode=FilterMode.Trilinear,anisoLevel=3,wrapMode=TextureWrapMode.Repeat};
            var pixels=new Color32[item.Width*item.Height];
            for(int y=0;y<item.Height;y++)for(int x=0;x<item.Width;x++)
                pixels[y*item.Width+x]=new Color32((byte)(20+x*43%220),(byte)(30+y*37%220),(byte)(10+(x+y)*13%190),(byte)((x*11+y*17)%256));
            source.SetPixels32(pixels);source.Apply(true,false);owned.Add(source);return source;
        }
        static object NewSet(object repository,Texture2D source,Case item)
        {
            var assembly=repository.GetType().Assembly;
            return Activator.CreateInstance(assembly.GetType("DVSeasons.Mod.SeasonalTextureController+SeasonalTextureSet",true),All,null,
                new[]{(object)source,Enum.Parse(assembly.GetType("DVSeasons.Mod.SeasonalTextureController+TextureCategory",true),item.Category),64,repository,item.Evergreen},null);
        }
        static object State(int season,float transition,float snow)
        {
            var kind=core.GetType("DVSeasons.Core.SeasonKind",true);
            return Activator.CreateInstance(core.GetType("DVSeasons.Core.SeasonState",true),new object[]{(double)season,Enum.ToObject(kind,season),Enum.ToObject(kind,(season+1)%4),transition,snow,-10f,0f});
        }
        static Texture2D Finish(object set,object state,float strength,int key,int budget=997)
        {
            int calls=0;
            do
            {
                Call(set,"UpdateChunk",state,strength,key,budget);
                // Test completion only: runtime never waits for a readback.
                AsyncGPUReadback.WaitAllRequests();
                Require(!(bool)set.GetType().GetProperty("IsFailed").GetValue(set,null),"Seasonal set failed during parity");
                Require(++calls<10000,"Seasonal set did not complete");
            }while((bool)Call(set,"NeedsUpdate",key));
            return (Texture2D)set.GetType().GetProperty("Output").GetValue(set,null);
        }
        static void CompareCase(object legacyRepository,object currentRepository,Case item,bool overrides)
        {
            var source=Source(item);var legacy=NewSet(legacyRepository,source,item);var updated=NewSet(currentRepository,source,item);
            try
            {
                int key=0;
                // Begin with full winter so the first frame cannot rely on caches
                // incidentally filled by prior warm-season test cases.
                foreach(int season in new[]{3,0,1,2,3})
                foreach(float transition in new[]{0f,.37f,1f})
                foreach(float strength in new[]{0f,.63f,1f})
                {
                    float snow=season==3?1-transition:season==2?transition:0;
                    var state=State(season,transition,snow);
                    Compare(Finish(legacy,state,strength,++key),Finish(updated,state,strength,key),item.Name);
                }
                foreach(float snow in new[]{0f,.28f,.62f,1f,.81f,.46f,.13f,0f})
                {
                    var state=State(2,.55f,snow);
                    Compare(Finish(legacy,state,1,++key),Finish(updated,state,1,key),item.Name+" snow="+snow);
                }
                // An update already in progress finishes its captured style;
                // the following style then replaces it without mixed pixels.
                int interrupted=++key;
                var first=State(2,.4f,.43f);
                foreach(var set in new[]{legacy,updated})
                {
                    int guard=0;
                    do {Call(set,"UpdateChunk",first,.63f,interrupted,1);AsyncGPUReadback.WaitAllRequests();Require(++guard<100,"Preparation did not reach blend");}
                    while(!(bool)set.GetType().GetProperty("HasPendingUpdate").GetValue(set,null));
                }
                var replacement=State(0,.71f,0);
                Compare(Finish(legacy,replacement,1,++key),Finish(updated,replacement,1,key),item.Name+" interrupted-style");
                Debug.Log("SEASONAL_TEXTURE_PARITY_CASE: "+item.Category+" "+item.Name+" overrides="+overrides+" states="+key);
            }
            finally{DisposeSet(legacy);DisposeSet(updated);}
        }
        static void CompareMalformed(object legacyRepository,object currentRepository,bool requireFallback=false)
        {
            var item=new Case("Ballast","MalformedBallast",64,64,false);var source=Source(item);
            var legacy=NewSet(legacyRepository,source,item);var updated=NewSet(currentRepository,source,item);
            try
            {
                // Even an unused malformed middle stage must reject the set at
                // full winter, matching the old eager-stage validation.
                int key=0;
                foreach(float snow in new[]{1f,.46f,.13f,.62f,0f})
                {
                    var state=State(2,.5f,snow);
                    Compare(Finish(legacy,state,1,++key),Finish(updated,state,1,key),"corrupt-track-stage snow="+snow);
                }
                bool originalFallback=Get(legacy,"winterTrackProfiles")==null;
                Require((Get(updated,"winterTrackProfiles")==null)==originalFallback,"Malformed stage changed legacy fallback mode");
                Require(!requireFallback || originalFallback,"Locked required stage did not exercise load failure");
            }
            finally {DisposeSet(legacy);DisposeSet(updated);}
        }
        static void DisposeSet(object set)
        {
            // Runtime uses deferred Object.Destroy. Edit mode requires immediate
            // destruction; detach only the GPU output and exercise real buffer /
            // pending-readback cleanup without expected editor misuse errors.
            var property=set.GetType().GetProperty("Output",All);
            var output=(Texture2D)property.GetValue(set,null);
            property.SetValue(set,null,null);
            Call(set,"Dispose");
            if(output!=null)UnityEngine.Object.DestroyImmediate(output);
        }
        static void Compare(Texture2D expected,Texture2D actual,string context)
        {
            Require(expected!=null && actual!=null && expected.width==actual.width && expected.height==actual.height && expected.mipmapCount==actual.mipmapCount,"Output layout differs: "+context);
            Require(expected.filterMode==actual.filterMode && expected.wrapMode==actual.wrapMode && expected.anisoLevel==actual.anisoLevel && expected.mipMapBias==actual.mipMapBias,"Sampler differs: "+context);
            for(int mip=0;mip<expected.mipmapCount;mip++)
            {
                var a=expected.GetPixels32(mip);var b=actual.GetPixels32(mip);
                Require(a.Length==b.Length,"Mip length differs: "+context);
                for(int p=0;p<a.Length;p++)if(!a[p].Equals(b[p]))
                    throw new Exception("Pixel differs: "+context+" mip="+mip+" pixel="+p+" old="+a[p]+" new="+b[p]);
                mips++;
            }
            comparisons++;
        }
        static void VerifyLazyPreparation(object repository)
        {
            var item=new Case("Foliage","lazy_leaf_fixture",64,64,false);var set=NewSet(repository,Source(item),item);
            try
            {
                Finish(set,State(1,0,0),1,1);
                var profiles=(Color32[][])Get(set,"profiles");
                Require(profiles[0]==null && profiles[2]==null && profiles[3]==null,"Summer eagerly generated unused foliage profiles");
                Finish(set,State(0,0,0),1,2);
                Require(profiles[0]!=null && profiles[2]==null && profiles[3]==null,"Spring generated unrelated profiles");
            }
            finally{DisposeSet(set);}
            item=new Case("Ballast","BallastNew_d",64,64,false);set=NewSet(repository,Source(item),item);
            try
            {
                Finish(set,State(3,0,1),1,1);
                var stages=(Color32[][])Get(set,"winterTrackProfiles");
                Require(stages!=null && stages[0]==null && stages[1]==null && stages[2]!=null,"Full winter eagerly loaded unused early/middle track stages");
            }
            finally{DisposeSet(set);}
        }
        static void VerifyAtlasCache(object repository)
        {
            var cache=(IDictionary)Get(repository,"loadedPixelProfiles");int before=cache.Count;
            var unique=new HashSet<Color32[]>();
            for(int i=0;i<40;i++)
            {
                var args=new object[]{"billboard_cache_"+i,256,32,null};
                Require((bool)Call(repository,"TryLoadBareTreeBillboardPixels",args),"Bare atlas missing during cache check");
                unique.Add((Color32[])args[3]);
            }
            Require(unique.Count<=4 && cache.Count-before<=5,"Bare atlas cache scales per tree name");
        }
        static void VerifyCancellation(object repository)
        {
            var item=new Case("Foliage","cancel_leaf",256,256,false);var source=Source(item);var set=NewSet(repository,source,item);
            Call(set,"UpdateChunk",State(3,0,1),1f,11,1);
            DisposeSet(set);AsyncGPUReadback.WaitAllRequests();
            Require(set.GetType().GetProperty("Output").GetValue(set,null)==null,"Cancelled readback created output");
            var readback=current.GetType("DVSeasons.Mod.SeasonTextureReadback",true);
            Require((int)readback.GetProperty("ActiveRequests",All).GetValue(null,null)==0,"Cancelled readback leaked active request");
            set=NewSet(repository,source,item);
            try
            {
                Finish(set,State(1,0,0),1,12);
                Call(set,"UpdateChunk",State(0,.5f,0),1f,13,1);
                DisposeSet(set);
                Require(Get(set,"profiles")==null && Get(set,"outputPixels")==null && set.GetType().GetProperty("Output").GetValue(set,null)==null,"Disposed preparation retained buffers");
            }
            finally{DisposeSet(set);}
        }
        static void VerifyTiming(object legacyRepository,object currentRepository)
        {
            foreach(var item in new[]{new Case("Billboard","billboard_timing_fir",1024,128,true),new Case("Foliage","timing_foliage",256,256,false)})
            {
                var source=Source(item);
                foreach(var repository in new[]{legacyRepository,currentRepository})
                {
                    var set=NewSet(repository,source,item);var state=State(3,0,1);
                    double total=0,maximum=0;int calls=0;var clock=new System.Diagnostics.Stopwatch();
                    try
                    {
                        do
                        {
                            clock.Restart();Call(set,"UpdateChunk",state,1f,901,32768);clock.Stop();
                            total+=clock.Elapsed.TotalMilliseconds;maximum=Math.Max(maximum,clock.Elapsed.TotalMilliseconds);
                            AsyncGPUReadback.WaitAllRequests();Require(++calls<10000,"Timing texture never completed");
                        }while((bool)Call(set,"NeedsUpdate",901));
                        Debug.Log("SEASONAL_TEXTURE_CPU_SAMPLE: implementation="+(repository==legacyRepository?"baseline":"lazy")+" category="+item.Category+" calls="+calls+" update_total_ms="+total.ToString("F3")+" max_slice_ms="+maximum.ToString("F3")+" gpu_wait_excluded=true cache=warm");
                    }
                    finally{DisposeSet(set);}
                }
            }
        }
        static void WriteOverrides(string root)
        {
            foreach(var name in new[]{"T_beech_atlas_BC v2","T_beech_forest_stumps_01_BC_SM","SleeperNew_d","MB_rooftile_red_01d"})
            foreach(var season in new[]{"spring","autumn","winter"})WritePng(root,season+"/"+name+".png",season=="winter"?219:87);
            foreach(var stage in new[]{"early","middle","late"})
                WritePng(root,"winter_track/"+stage+"/BallastNew_d.png",stage=="early"?91:stage=="middle"?153:227);
        }
        static void WritePng(string root,string relative,int value)
        {
            var path=Path.Combine(root,"Overrides/Seasonal",relative);Directory.CreateDirectory(Path.GetDirectoryName(path));
            var texture=new Texture2D(64,64,TextureFormat.RGBA32,false);var pixels=new Color32[4096];
            for(int p=0;p<pixels.Length;p++)pixels[p]=new Color32((byte)(value+p%23),(byte)(value/2+p%49),(byte)(value/3+p%67),(byte)(p%256));
            texture.SetPixels32(pixels);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
