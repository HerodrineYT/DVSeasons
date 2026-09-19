using System;
using DVSeasons.Mod;
using UnityModManagerNet;

namespace OptimizerTest
{
    internal static class Program
    {
        private static int Main()
        {
            DVSeasonsOptimizer.Main.Load(new UnityModManager.ModEntry());
            var result = new SnowVehicleRegistry().Verify();
            Console.WriteLine("optimizer-fixture=" + result);

            if (!string.Equals(result, "3:4:False:False:True:4", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Expected active/full/scheduler/flag/restored/final = 3:4:False:False:True:4");
                return 1;
            }

            return 0;
        }
    }
}
