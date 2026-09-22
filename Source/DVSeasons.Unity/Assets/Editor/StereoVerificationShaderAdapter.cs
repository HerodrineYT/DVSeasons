using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Without an active XR device Unity 2019 overwrites its built-in stereo
    // constant buffers, ignoring material/command-buffer overrides. This copies
    // current production source verbatim except device-uniform bindings. It
    // retains all production keyword branches, UV call sites and reconstruction.
    // The packed original is separately compiled; this cannot validate the XR
    // runtime's actual constant-buffer upload or submission to a physical HMD.
    public sealed class StereoVerificationShaderAdapter:IDisposable
    {
        readonly string path;
        public Shader Shader {get;private set;}
        public StereoVerificationShaderAdapter(string productionPath,bool explicitImageVertex=false)
        {
            string source=File.ReadAllText(productionPath);
            string directory=Path.GetDirectoryName(productionPath).Replace('\\','/');
            source=Regex.Replace(source,"#include \\\"([^\\\"]+)\\\"",match=> {
                string include=match.Groups[1].Value;
                string candidate=directory+"/"+include;
                return File.Exists(candidate)?"#include \""+candidate+"\"":match.Value;
            });
            string adapter=@"
            int _DVPSVerifyEye;
            float4 _DVPSVerifyScaleOffset[2];
            #if defined(UNITY_SINGLE_PASS_STEREO)
            #define unity_StereoEyeIndex _DVPSVerifyEye
            #define UnityStereoTransformScreenSpaceTex(uv) (saturate(uv)*_DVPSVerifyScaleOffset[_DVPSVerifyEye].xy+_DVPSVerifyScaleOffset[_DVPSVerifyEye].zw)
            #endif
";
            const string includeMarker="#include \"UnityCG.cginc\"";
            if(!source.Contains(includeMarker))throw new InvalidOperationException("Expected UnityCG include for stereo adapter: "+productionPath);
            source=source.Replace(includeMarker,includeMarker+adapter);
            if(explicitImageVertex)
            {
                // vert_img normally obtains clip coordinates from the native
                // XR view/projection constant buffer. A supplied clip-space
                // quad replaces only that unavailable device submission step.
                const string imageVertex=@"
                v2f_img VerifyImageVertex(appdata_img v)
                {
                    v2f_img o;
                    UNITY_INITIALIZE_OUTPUT(v2f_img,o);
                    UNITY_SETUP_INSTANCE_ID(v);
                    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                    o.pos=v.vertex; o.uv=v.texcoord;
                    // CommandBuffer.SetViewProjectionMatrices converts the
                    // fixture's identity projection for a render texture.
                    // Reproduce its D3D Y inversion for native vert_img parity.
                    #if UNITY_UV_STARTS_AT_TOP
                    o.pos.y=-o.pos.y;
                    #endif
                    return o;
                }
";
                source=source.Replace("#pragma vertex vert_img","#pragma vertex VerifyImageVertex");
                source=source.Replace(includeMarker+adapter,includeMarker+adapter+imageVertex);
            }
            source=Regex.Replace(source,"Shader \\\"[^\\\"]+\\\"","Shader \"Hidden/DVSeasons/Verification/Adapted"+Path.GetFileNameWithoutExtension(productionPath)+"\"",RegexOptions.None);
            path="Assets/Editor/__StereoVerification_"+Path.GetFileName(productionPath);
            File.WriteAllText(path,source);AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
            Shader=AssetDatabase.LoadAssetAtPath<Shader>(path);
            if(Shader==null || !Shader.isSupported)throw new InvalidOperationException("Adapted stereo shader unavailable: "+productionPath);
        }
        public void Dispose() {AssetDatabase.DeleteAsset(path);}
    }
}
