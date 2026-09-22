using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class StereoReleaseVerification
    {
        public static void Run()
        {
            int result=0;
            AssetBundle bundle=null;
            try
            {
                var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
                var runtime=Path.Combine(root,"artifacts/build/DVSeasons");
                bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));
                if(bundle==null) throw new InvalidOperationException("Release bundle is unavailable");
                StereoSnowVerification.Verify(bundle);
                StereoPostEffectsVerification.Verify(bundle);
                var verify=typeof(SnowStereoCullingVerification).GetMethod("Verify",BindingFlags.NonPublic|BindingFlags.Static);
                verify.Invoke(null,new object[]{Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"))});
                Debug.Log("STEREO_RELEASE_OK: packaged mono shaders and current-source stereo device adapters; descriptor/culling checks; actual headset submission not tested. Bytecode presence is checked separately by Tools/verify_stereo_bundle.py.");
            }
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {if(bundle!=null)bundle.Unload(true);}
            EditorApplication.Exit(result);
        }
    }
}
