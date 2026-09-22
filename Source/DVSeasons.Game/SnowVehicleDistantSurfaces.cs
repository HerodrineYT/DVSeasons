using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    internal sealed class SnowVehicleDistantSurfaces : IDisposable
    {
        private const int Capacity=128;
        private readonly Matrix4x4[] matrices=new Matrix4x4[Capacity],local=new Matrix4x4[Capacity];
        private readonly float[] ids=new float[Capacity];
        private readonly MaterialPropertyBlock properties=new MaterialPropertyBlock();
        private Mesh box;
        private int count;
        public int Cars {get;private set;}
        public int Commands {get;private set;}
        public void Begin(){count=0;Cars=Commands=0;}
        public static float Distance(SnowVehicleRegistry.Vehicle vehicle,Camera camera)
        {
            var root=vehicle.Root;var bounds=vehicle.Topology.Local;
            var nearest=root.TransformPoint(bounds.ClosestPoint(root.InverseTransformPoint(camera.transform.position)));
            return Vector3.Distance(nearest,camera.transform.position);
        }
        public bool Eligible(SnowVehicleRegistry.Vehicle vehicle,Camera camera)
        {
            return !camera.stereoEnabled && !vehicle.HasIndependentSnowTexture && vehicle.RollingStock && vehicle.Ready && vehicle.SnowReady &&
                vehicle.Topology.Initialized && !vehicle.PartsPending && !vehicle.InteriorPending && !vehicle.PartsExploded &&
                vehicle.Root.gameObject.activeInHierarchy && vehicle.Root.localToWorldMatrix.determinant>0 &&
                Distance(vehicle,camera)>=80f &&
                Vector3.Distance(vehicle.Root.TransformPoint(vehicle.Topology.Local.center),camera.transform.position)+
                    vehicle.Topology.Local.extents.magnitude*vehicle.Root.lossyScale.magnitude<camera.farClipPlane;
        }
        public void Add(CommandBuffer buffer,Material material,SnowVehicleRegistry.Vehicle vehicle,Camera camera)
        {
            var bounds=vehicle.Topology.Local;bounds.Expand(new Vector3(.12f,.02f,.12f));
            matrices[count]=vehicle.Root.localToWorldMatrix*Matrix4x4.TRS(bounds.center,Quaternion.identity,bounds.size);
            local[count]=vehicle.Root.worldToLocalMatrix;ids[count]=vehicle.Slot+1;count++;Cars++;
            if(count==Capacity)Flush(buffer,material,camera);
        }
        public void Flush(CommandBuffer buffer,Material material,Camera camera)
        {
            if(count==0)return;
            if(box==null)
            {
                box=new Mesh{name="DVSeasons far-car snow volume",hideFlags=HideFlags.HideAndDontSave};
                var vertices=new Vector3[8];for(int i=0;i<8;i++)vertices[i]=new Vector3((i&1)==0?-.5f:.5f,(i&2)==0?-.5f:.5f,(i&4)==0?-.5f:.5f);
                box.vertices=vertices;box.triangles=new[]{0,2,1,1,2,3,4,5,6,5,7,6,0,4,2,2,4,6,1,3,5,3,7,5,0,1,4,1,5,4,2,6,3,3,6,7};box.UploadMeshData(true);
            }
            properties.SetMatrixArray("_DVPSInstanceWorldToLocal",local);properties.SetFloatArray("_DVPSInstanceVehicleIndex",ids);
            buffer.SetGlobalMatrix("_DVPSExclusionInverseVP",StereoRenderSupport.InverseViewProjection(camera));
            buffer.DrawMeshInstanced(box,0,material,8,matrices,count,properties);Commands++;count=0;
        }
        public void Dispose(){if(box!=null){if(Application.isPlaying)UnityEngine.Object.Destroy(box);else UnityEngine.Object.DestroyImmediate(box);}box=null;Begin();}
    }
}
