using System;
using System.Collections.Generic;
using DV.UI;
using DV.UIFramework;
using DV.Utils;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Own a normal tutorial-style notification; never replace the tutorial's
    // notification or its input-blocking/manual-dismiss coroutine.
    internal sealed class ColdStartHintController : IDisposable
    {
        private readonly Dictionary<string,ColdStartHintState> remote=new Dictionary<string,ColdStartHintState>(StringComparer.OrdinalIgnoreCase);
        private float receivedAt;
        private GameObject notification;
        private NotificationManager manager;
        private NotificationController content;
        private ColdStartHintStage shownStage;
        private int shownSeconds=-1;
        private bool shownDe6;
        private string shownLanguage;
        public void Receive(ColdStartHintState[] values)
        {
            if(!ColdStartHintState.IsValid(values))return;
            remote.Clear();foreach(var value in values)remote[value.CarId]=value;
            receivedAt=Time.time;
        }
        public void Update(bool enabled,bool localAuthority,ColdPowertrainController powertrain)
        {
            var car=PlayerManager.Car;
            if(!enabled || car==null || Time.timeScale<=0){Hide();return;}
            ColdStartHintState hint;
            if(localAuthority)hint=powertrain.GetHint(car);
            else
            {
                if(string.IsNullOrEmpty(car.CarGUID) || Time.time-receivedAt>3 || !remote.TryGetValue(car.CarGUID,out hint)){Hide();return;}
                hint=hint.After(Time.time-receivedAt);
            }
            if(hint.Stage==ColdStartHintStage.None){Hide();return;}
            var canvas=SingletonBehaviour<ACanvasController<CanvasController.ElementType>>.Instance;
            if(canvas==null || canvas.NotificationManager==null){Hide();return;}
            Present(hint,canvas.NotificationManager);
        }
        private void Present(ColdStartHintState hint,NotificationManager nextManager)
        {
            if(hint.Stage==ColdStartHintStage.None || nextManager==null){Hide();return;}
            if(manager!=nextManager){Hide();manager=nextManager;}
            int seconds=Mathf.CeilToInt(hint.RemainingSeconds);
            string language=I2.Loc.LocalizationManager.CurrentLanguage;
            if(notification!=null && shownStage==hint.Stage && shownSeconds==seconds && shownDe6==hint.De6 && shownLanguage==language)return;
            string text=hint.Stage==ColdStartHintStage.Primer?ModLocalization.Format("ColdStart.Primer",Mathf.Max(1,seconds)):
                seconds>0?ModLocalization.Format("ColdStart.Starter",seconds):ModLocalization.Text("ColdStart.AwaitEngine");
            if(hint.Stage==ColdStartHintStage.Starter && hint.De6)text+="\n"+ModLocalization.Text("ColdStart.De6Next");
            if(notification==null)
            {
                notification=manager.ShowNotification(text,null,float.MaxValue,clearExisting:false,localize:false,
                    sizeOverrides:new NotificationManager.SizeOverrides{textScale=.85f,verticalMarginScale=.6f},ordering:NotificationManager.Ordering.Last);
                content=notification!=null?notification.GetComponent<NotificationController>():null;
            }
            else if(content!=null)content.textContent.text=text;
            shownStage=hint.Stage;shownSeconds=seconds;shownDe6=hint.De6;shownLanguage=language;
        }
        private void Hide()
        {
            if(notification!=null)
            {
                if(manager!=null)manager.ClearNotification(notification);
                else UnityEngine.Object.Destroy(notification);
            }
            notification=null;content=null;shownSeconds=-1;shownStage=ColdStartHintStage.None;
        }
        public void Dispose(){Hide();manager=null;remote.Clear();receivedAt=0;}
    }
}
