using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowShaderDiagnostics
    {
        public static void Run()
        {
            int code=0;Material material=null;
            try
            {
                var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowDust.shader");
                if(shader==null)throw new System.Exception("SNOW_SHADER_DIAGNOSTIC: shader missing");
                material=new Material(shader);
                for(int pass=0;pass<material.passCount;pass++)ShaderUtil.CompilePass(material,pass,true);
                Debug.Log("SNOW_SHADER_DIAGNOSTIC: passes="+material.passCount+", errors="+ShaderUtil.ShaderHasError(shader));
                foreach(var message in ShaderUtil.GetShaderMessages(shader))
                    Debug.Log("SNOW_SHADER_DIAGNOSTIC: "+message.message+" at "+message.file+":"+message.line+" platform="+message.platform+" severity="+message.severity);
                if(ShaderUtil.ShaderHasError(shader))throw new System.Exception("SNOW_SHADER_DIAGNOSTIC: shader compilation failed");
                SnowDustVerification.Verify(shader);
            }
            catch(System.Exception e){Debug.LogException(e);code=1;}
            finally{if(material!=null)Object.DestroyImmediate(material);}
            EditorApplication.Exit(code);
        }
    }
}
