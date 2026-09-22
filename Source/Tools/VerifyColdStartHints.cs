using System;
using System.Collections;
using System.IO;
using System.Reflection;
using DV.UIFramework;
using DVSeasons.Core;
using TMPro;
using UnityEngine;

// Exercise the game's notification implementation with a minimal UI prefab.
// The live canvas/VR placement remains owned by Derail Valley.
public sealed class ColdHintProviderFixture : ANotificationManagerProvider
{
    public RectTransform Root;
    public int Added;
    public override RectTransform ContentRoot { get { return Root; } }
    public override void AddWorldSpacePointer(GameObject notification,Transform to,bool targetIsUI,GameObject owner) { }
    public override void ClearWorldSpacePointer(GameObject notification) { }
    public override void OnNotificationAdded(GameObject notification) { Added++; }
}

public static class VerifyColdStartHints
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
    public static void Run(string modPath)
    {
        var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
        mod.GetType("DVSeasons.Mod.ModLocalization",true).GetMethod("Initialize",All).Invoke(null,new object[]{modPath});
        var type=mod.GetType("DVSeasons.Mod.ColdStartHintController",true);
        var hints=Activator.CreateInstance(type,true);
        var present=type.GetMethod("Present",All);
        var root=new GameObject("cold hint test canvas",typeof(RectTransform),typeof(Canvas));
        var prefab=new GameObject("cold hint test prefab",typeof(RectTransform));
        try
        {
            var provider=root.AddComponent<ColdHintProviderFixture>();provider.Root=(RectTransform)root.transform;
            var manager=root.AddComponent<NotificationManager>();manager.provider=provider;
            var content=prefab.AddComponent<NotificationController>();
            var text=new GameObject("text",typeof(RectTransform));text.transform.SetParent(prefab.transform,false);
            content.textContent=text.AddComponent<TextMeshProUGUI>();
            typeof(NotificationManager).GetField("notificationPrefab",All).SetValue(manager,prefab);
            var notifications=(IList)typeof(NotificationManager).GetField("notifications",All).GetValue(manager);
            var tutorial=manager.ShowNotification("Unrelated tutorial",localize:false);
            var hint=new ColdStartHintState{CarId="fixture",Stage=ColdStartHintStage.Starter,RemainingSeconds=5.2f,De6=true};
            present.Invoke(hints,new object[]{hint,manager});
            var owned=(GameObject)type.GetField("notification",All).GetValue(hints);
            var label=owned.GetComponent<NotificationController>().textContent;
            Require(notifications.Count==2 && provider.Added==2,"Cold hint replaced tutorial or duplicated notifications");
            Require(label.text.Contains("6") && label.text.Contains("DE6"),"Countdown rounding/advance DE6 primer instruction missing: "+label.text);
            Require(label.text.Contains("5") && (label.text.Contains("maximum") || label.text.Contains("максимум")),"Five-second window or maximum-throttle alternative missing: "+label.text);
            hint.RemainingSeconds=4;
            present.Invoke(hints,new object[]{hint,manager});
            Require((GameObject)type.GetField("notification",All).GetValue(hints)==owned && provider.Added==2,"Countdown recreated native notification");
            Require(label.text.Contains("4"),"Countdown label did not update");
            hint.Stage=ColdStartHintStage.Primer;hint.RemainingSeconds=3;
            present.Invoke(hints,new object[]{hint,manager});
            Require(label.text.Contains("3") && !label.text.Contains("\n"),"Primer transition retained starter instruction");
            present.Invoke(hints,new object[]{default(ColdStartHintState),manager});
            Require(notifications.Count==1 && tutorial!=null,"Ending hint cleared an unrelated tutorial");
            present.Invoke(hints,new object[]{hint,manager});
            type.GetMethod("Update",All).Invoke(hints,new object[]{false,true,null});
            Require(notifications.Count==1,"Disabling hints retained notification");
            present.Invoke(hints,new object[]{hint,manager});
            ((IDisposable)hints).Dispose();
            Require(notifications.Count==1,"Session reset retained notification");
            manager.ClearAllNotifications();
            Debug.Log("DVSeasons cold start UI verified: native notification, countdown reuse, DE6 transition, local disable, tutorial preservation and cleanup.");
        }
        finally
        {
            ((IDisposable)hints).Dispose();
            UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(prefab);
        }
    }
}
