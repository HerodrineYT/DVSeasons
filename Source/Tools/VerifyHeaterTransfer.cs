using System;
using System.IO;
using System.Reflection;

internal static class VerifyHeaterTransfer
{
    private static int Main(string[] paths)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s,e) =>
        {
            foreach(var path in paths)
            {
                string file=Path.Combine(path,new AssemblyName(e.Name).Name+".dll");
                if(File.Exists(file)) return Assembly.LoadFrom(file);
            }
            return null;
        };
        try
        {
            var seasons=Assembly.LoadFrom(Path.Combine(paths[0],"DVSeasons.dll"));
            var survival=Assembly.LoadFrom(Path.Combine(paths[1],"DVSurvival.dll"));
            var car=Assembly.LoadFrom(Path.Combine(paths[2],"Assembly-CSharp.dll")).GetType("TrainCar",true);
            var api=seasons.GetType("DVSeasons.Mod.CabHeating",true);
            if((bool)api.GetProperty("IsActive").GetValue(null,null)) throw new Exception("Heater service active outside a session");
            foreach(string name in new[]{"Ensure","GetLevel","GetLevelById","GetCabinTemperature"})
            {
                var method=api.GetMethod(name);
                Type input=name=="GetLevelById"?typeof(string):car;
                Type output=name=="Ensure"?typeof(bool):typeof(float);
                Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(input,output),method);
            }
            if((float)api.GetMethod("GetLevel").Invoke(null,new object[]{null})!=0) throw new Exception("Inactive heater isn't off");
            var adapter=survival.GetType("DVSurvival.Mod.SeasonsCabHeating",true);
            if(adapter.GetProperty("Active")==null) throw new Exception("Survival bridge missing");
            var runtime=seasons.GetType("DVSeasons.Mod.Main",true).GetField("runtime",BindingFlags.NonPublic|BindingFlags.Static);
            var state=runtime.FieldType.GetProperty("CurrentState").PropertyType;
            if(state.GetProperty("TemperatureCelsius").PropertyType!=typeof(float) || state.GetProperty("Current")==null)
                throw new Exception("Temperature adapter contract changed");
            Console.WriteLine("PASS: compiled heater delegates, inactive state, Survival bridge and existing temperature API.");
            return 0;
        }
        catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
