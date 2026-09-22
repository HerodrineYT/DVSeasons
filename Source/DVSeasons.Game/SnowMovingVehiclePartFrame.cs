using System;
using System.Reflection;
using UnityEngine;

namespace DVSeasons.Mod
{
    // A vehicle's world pose is not enough for a mesh animated within that
    // vehicle. Keep the mesh pose used by its height slice for snow sampling.
    internal sealed class SnowMovingVehiclePartFrame
    {
        private static Type handcarControllerType;
        private static FieldInfo visualHandlebarField;
        private readonly Transform vehicleRoot;
        private readonly Transform part;
        private Matrix4x4 meshToCapturedVehicle;

        private SnowMovingVehiclePartFrame(Transform vehicleRoot, Transform part)
        {
            this.vehicleRoot = vehicleRoot;
            this.part = part;
            CaptureReference();
        }

        public static Transform FindHandlebar(Transform vehicleRoot)
        {
            if (vehicleRoot == null) return null;
            if (handcarControllerType == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    handcarControllerType = assembly.GetType("DV.Simulation.Controllers.HandcarController", false);
                    if (handcarControllerType != null) break;
                }
                visualHandlebarField = handcarControllerType?.GetField("visualHandlebar", BindingFlags.Public | BindingFlags.Instance);
            }
            if (visualHandlebarField == null) return null;
            var controller = vehicleRoot.GetComponentInChildren(handcarControllerType, true);
            var handlebar = controller != null ? visualHandlebarField.GetValue(controller) as Transform : null;
            return handlebar != null && handlebar.IsChildOf(vehicleRoot) ? handlebar : null;
        }

        public static SnowMovingVehiclePartFrame TryCreate(Transform vehicleRoot, Transform handlebar, Renderer renderer)
        {
            if (vehicleRoot == null || handlebar == null || renderer == null ||
                !handlebar.IsChildOf(vehicleRoot) || !renderer.transform.IsChildOf(handlebar)) return null;
            return new SnowMovingVehiclePartFrame(vehicleRoot, renderer.transform);
        }

        public bool IsValid => vehicleRoot != null && part != null && part.IsChildOf(vehicleRoot);

        public Matrix4x4 WorldToCapturedVehicle => IsValid
            ? meshToCapturedVehicle * part.worldToLocalMatrix
            : vehicleRoot != null ? vehicleRoot.worldToLocalMatrix : Matrix4x4.identity;

        public void CaptureReference()
        {
            if (!IsValid) return;
            // Build in the small local frame so a distant car or an origin shift
            // cannot lose detail while subtracting two large world positions.
            var matrix = Matrix4x4.identity;
            for (var node = part; node != vehicleRoot; node = node.parent)
                matrix = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * matrix;
            meshToCapturedVehicle = matrix;
        }
    }
}
