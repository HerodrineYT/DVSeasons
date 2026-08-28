using System;
using System.IO;
using System.Reflection;

internal static class VerifyDvSeasonsAssemblyLoad
{
    private static string[] searchDirectories;

    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: VerifyDvSeasonsAssemblyLoad <mod-dir> <managed-dir> <umm-dir>");
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
