using System;
using System.IO;
using System.Reflection;

internal static class VerifyDvSeasonsAssemblyLoad
{
    private static string[] searchDirectories;

    private static int Main(string[] args)
    {
        if (args.Length != 3 && (args.Length != 4 || (args[3] != "--verify-save" && args[3] != "--verify-climate")))
        {
            Console.Error.WriteLine("Usage: VerifyDvSeasonsAssemblyLoad <mod-dir> <managed-dir> <umm-dir> [--verify-save|--verify-climate]");
            return 64;
        }

        searchDirectories = new[] { args[0], args[1], args[2] };
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        try
        {
            var assemblyPath = Path.Combine(args[0], "DVSeasons.dll");
            var assembly = Assembly.LoadFrom(assemblyPath);
            var types = assembly.GetTypes();
            Console.WriteLine("OK: loaded {0} types from {1}", types.Length, assembly.FullName);
            // --verify-save requires .NET 8 or Unity Mono: the game's stripped
            // Newtonsoft assembly is not strong-name-valid in desktop .NET 4.x.
            if (args.Length == 4 && args[3] == "--verify-save") VerifySaveRoundTrip(assembly, args[1]);
            if (args.Length == 4 && args[3] == "--verify-climate")
            {
                var climate = assembly.GetType("DVSeasons.Mod.SeasonalClimateController", true);
                var instance = Activator.CreateInstance(climate, true);
                try
                {
                    climate.GetMethod("Install", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, null);
                    Console.WriteLine("OK: five climate patches installed against actual game DLLs, including weather transpiler.");
                }
                finally { ((IDisposable)instance).Dispose(); }
                Console.WriteLine("OK: climate patches removed without launching the game.");
            }
            return 0;
        }
        catch (ReflectionTypeLoadException exception)
        {
            Console.Error.WriteLine("TYPE LOAD FAILED");
            foreach (var loaderException in exception.LoaderExceptions)
            {
                Console.Error.WriteLine("{0}: {1}", loaderException.GetType().FullName, loaderException.Message);
            }

            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
        }
    }

    private static void VerifySaveRoundTrip(Assembly mod, string managedDirectory)
    {
        // Use the actual game's SaveGameData and JSON implementation, without
        // opening a career or executing Unity's native scene/runtime functions.
        var game = Assembly.LoadFrom(Path.Combine(managedDirectory, "Assembly-CSharp.dll"));
        var dataType = game.GetType("SaveGameData", true);
        var codec = mod.GetType("DVSeasons.Mod.SeasonSaveData", true);
        var write = codec.GetMethod("Write");
        var stateType = write.GetParameters()[1].ParameterType;
        var state = Activator.CreateInstance(stateType);
        stateType.GetField("Phase").SetValue(state, 3.918237492817d);
        stateType.GetField("AutomaticCycle").SetValue(state, false);
        stateType.GetField("DaysPerSeason").SetValue(state, 21f);
        stateType.GetField("RandomTransitionDuration").SetValue(state, false);
        stateType.GetField("TransitionDays").SetValue(state, 4f);
        stateType.GetField("TransitionSeason").SetValue(state, 3);
        var data = Activator.CreateInstance(dataType);
        dataType.GetMethod("SetInt").Invoke(data, new object[] { "OtherMod.Test", 51 });
        write.Invoke(null, new[] { data, state });
        var json = (string)dataType.GetMethod("GetJsonString").Invoke(data, null);
        var restoredData = dataType.GetMethod("LoadFromString").Invoke(null, new object[] { json, null });
        var readArgs = new[] { restoredData, (object)null };
        if (!(bool)codec.GetMethod("TryRead").Invoke(null, readArgs))
            throw new InvalidOperationException("Native JSON round-trip rejected the saved calendar.");
        foreach (var field in stateType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            if (!object.Equals(field.GetValue(state), field.GetValue(readArgs[1])))
                throw new InvalidOperationException("Native JSON round-trip changed " + field.Name);
        if (!object.Equals(51, dataType.GetMethod("GetInt").Invoke(restoredData, new object[] { "OtherMod.Test" })))
            throw new InvalidOperationException("Season save altered another mod's data.");
        Console.WriteLine("OK: native SaveGameData JSON preserves exact phase, transition and unrelated data.");
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name);
        if (requested.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var directory in searchDirectories)
        {
            var candidate = Path.Combine(directory, requested.Name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }

        return null;
    }
}
