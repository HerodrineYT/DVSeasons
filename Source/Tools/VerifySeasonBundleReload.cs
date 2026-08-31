#if UNITY_EDITOR
using System;
using DVSeasons.Core;
using DVSeasons.Mod;
using UnityEditor;
using UnityEngine;

// Run only in a fresh verification project containing this file, the production
// FixedTexturePackRepository.cs and SeasonKind.cs. Never run in a live game.
public static class VerifySeasonBundleReload
{
    public static void Run()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var flag = Array.IndexOf(args, "-dvseasonsModPath");
            if (flag < 0 || flag + 1 >= args.Length) throw new ArgumentException("Missing -dvseasonsModPath");
            using (var repository = new SeasonAssetBundleRepository(args[flag + 1]))
            {
                Require(repository.Bundle == null, "Bundle should load lazily, not in the menu.");
                for (var session = 1; session <= 3; session++)
                {
                    Texture2DArray winter;
                    Require(repository.TryGetTerrainArray(SeasonKind.Winter, out winter), "Winter array did not load.");
                    Require(winter.depth == 16 && winter.width == 512, "Unexpected winter array dimensions.");
                    Texture2D layer;
                    Require(repository.TryGetTerrainLayer(SeasonKind.Winter, 13, out layer), "Winter terrain layer did not load.");
                    Require(repository.HasCompleteSet("T_Maple_01_Cross_A_T"), "Tree atlas index was not restored.");
                    var bundle = repository.Bundle;
                    // Simulate DV's between-session cleanup in this isolated process.
                    bundle.Unload(true);
                    Require(winter == null && layer == null && repository.Bundle == null,
                        "The test did not actually invalidate Unity's cached objects.");
                    repository.ResetForSession();
                    Debug.Log("DVSEASONS_BUNDLE_RELOAD_SESSION_OK " + session);
                }
                Texture2DArray finalWinter;
                Require(repository.TryGetTerrainArray(SeasonKind.Winter, out finalWinter), "Final reload failed.");
            }
            Debug.Log("DVSEASONS_BUNDLE_RELOAD_OK");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
