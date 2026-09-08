using System;
using HarmonyLib;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Multiplayer currently logs one warning per unsupported cab control and can
    // repeat the same interior-load error for a streamed car. Keep the first useful
    // signal in Player.log and suppress only subsequent messages of those exact
    // categories. Other Multiplayer warnings and errors remain untouched.
    internal sealed class MultiplayerLogSpamFilter : IDisposable
    {
        private const string HarmonyId="Herodrine.DVSeasons.MultiplayerLogSpam";
        private static bool controlWarningSeen,interiorErrorSeen;
        private readonly Harmony harmony=new Harmony(HarmonyId);
        private bool installed;
        private bool disposed;

        public MultiplayerLogSpamFilter()
        {
        }

        public void Enable()
        {
            if(disposed || installed) return;
            var type=AccessTools.TypeByName("Multiplayer.Multiplayer");
            if(type==null) return;
            controlWarningSeen=interiorErrorSeen=false;
            var prefix=new HarmonyMethod(typeof(MultiplayerLogSpamFilter),nameof(Prefix));
            foreach(var name in new[]{"LogWarning","LogError"})
            {
                var method=AccessTools.Method(type,name,new[]{typeof(object)});
                if(method!=null) harmony.Patch(method,prefix:prefix);
            }
            var unloadFinalizer=new HarmonyMethod(typeof(MultiplayerLogSpamFilter),nameof(UnloadFinalizer));
            PatchUnloadMethod("Multiplayer.Components.Networking.World.NetworkedPitStopStation","OnDisable",unloadFinalizer);
            PatchUnloadMethod("Multiplayer.Components.Networking.World.NetworkedPitStopStation","OnDestroy",unloadFinalizer);
            PatchUnloadMethod("Multiplayer.Components.Networking.World.NetworkedPluggableObject","OnDisable",unloadFinalizer);
            PatchUnloadMethod("Multiplayer.Components.Networking.World.NetworkedPluggableObject","OnDestroy",unloadFinalizer);
            PatchUnloadMethod("Multiplayer.Patches.World.CashRegisterWithModulesPatch","OnDisable",unloadFinalizer);
            installed=true;
            Debug.Log("[DVSeasons] Multiplayer repeated cab-control/interior log filter active.");
        }

        private void PatchUnloadMethod(string typeName,string methodName,HarmonyMethod finalizer)
        {
            var type=AccessTools.TypeByName(typeName);
            var method=type==null?null:AccessTools.Method(type,methodName);
            if(method!=null) harmony.Patch(method,finalizer:finalizer);
        }

        private static Exception UnloadFinalizer(Exception __exception)
        {
            if(__exception is NullReferenceException && UnloadWatcher.isUnloading) return null;
            return __exception;
        }

        public void Disable()
        {
            if(installed) harmony.UnpatchAll(HarmonyId);
            installed=false;
            controlWarningSeen=interiorErrorSeen=false;
        }

        private static bool Prefix(object msg)
        {
            var text=msg==null?string.Empty:msg.ToString();
            if(text.StartsWith("Unable to hook control",StringComparison.Ordinal))
            {
                if(controlWarningSeen) return false;
                controlWarningSeen=true;
            }
            else if(text.StartsWith("TrainCar ",StringComparison.Ordinal) &&
                text.EndsWith(" failed to load an interior",StringComparison.Ordinal))
            {
                if(interiorErrorSeen) return false;
                interiorErrorSeen=true;
            }
            return true;
        }

        public void Dispose()
        {
            if(disposed) return;
            Disable();
            disposed=true;
        }
    }
}
