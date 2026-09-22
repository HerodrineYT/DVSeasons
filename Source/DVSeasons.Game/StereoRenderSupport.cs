using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace DVSeasons.Mod
{
    // Screen-sized snow buffers must match the headset target, not its desktop
    // mirror. Keep XR's packed-eye layout while changing only the color format.
    internal static class StereoRenderSupport
    {
        internal static RenderTextureDescriptor ColorDescriptor(RenderTextureDescriptor source,
            RenderTextureFormat format, int downsample = 1)
        {
            source.width = Mathf.Max(1, source.width / downsample);
            source.height = Mathf.Max(1, source.height / downsample);
            // Keep the boundary between double-wide eyes on a pixel boundary.
            if (source.vrUsage == VRTextureUsage.TwoEyes && source.dimension == TextureDimension.Tex2D &&
                downsample > 1) source.width = Mathf.Max(2, (source.width + 1) & ~1);
            source.colorFormat = format;
            source.depthBufferBits = 0;
            source.msaaSamples = 1;
            source.bindMS = false;
            source.sRGB = false;
            source.useMipMap = false;
            source.autoGenerateMips = false;
            source.enableRandomWrite = false;
            source.memoryless = RenderTextureMemoryless.None;
            return source;
        }

        internal static void GetTemporaryRT(CommandBuffer buffer, int id, Camera camera,
            RenderTextureFormat format, FilterMode filter, int downsample = 1)
        {
            if (camera.stereoEnabled && XRSettings.enabled)
            {
                buffer.GetTemporaryRT(id, ColorDescriptor(XRSettings.eyeTextureDesc, format, downsample), filter);
                return;
            }
            buffer.GetTemporaryRT(id, downsample == 1 ? -1 : Mathf.Max(1, camera.pixelWidth / downsample),
                downsample == 1 ? -1 : Mathf.Max(1, camera.pixelHeight / downsample), 0, filter,
                format, RenderTextureReadWrite.Linear);
        }

        internal static Matrix4x4 InverseViewProjection(Camera camera)
        {
            // Multi-pass VR renders this callback separately for each eye.
            if (camera.stereoEnabled && camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Mono)
            {
                var eye = camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left
                    ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
                return InverseViewProjection(camera, eye);
            }
            return (GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix).inverse;
        }

        internal static Matrix4x4 InverseViewProjection(Camera camera, Camera.StereoscopicEye eye)
        {
            return (GL.GetGPUProjectionMatrix(camera.GetStereoProjectionMatrix(eye), true) *
                camera.GetStereoViewMatrix(eye)).inverse;
        }
    }
}
