using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class LanguageHelperVerification
    {
        private static string[] directories;
        public static void Verify()
        {
            // Helper creates a DontDestroyOnLoad language source, so exercise its
            // real lifecycle in an empty play-mode scene rather than editor mode.
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            SessionState.SetBool("DVSeasons.VerifyLanguageHelper", true);
            EditorApplication.isPlaying = true;
        }

        [InitializeOnLoadMethod]
        private static void RegisterPlayModeCheck()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode ||
                    !SessionState.GetBool("DVSeasons.VerifyLanguageHelper", false)) return;
                SessionState.SetBool("DVSeasons.VerifyLanguageHelper", false);
                EditorApplication.delayCall += Run;
            };
        }

        private static void Run()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                var index = Array.IndexOf(args, "-dvInstallDir");
                if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Pass -dvInstallDir.");
                var game = args[index + 1];
                var managed = Path.Combine(game, "DerailValley_Data/Managed");
                var mod = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/build/DVSeasons"));
                directories = new[] { mod, Path.Combine(game, "Mods/DVLangHelper"), Path.Combine(managed, "UnityModManager"), managed };
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                var umm = Assembly.LoadFrom(Path.Combine(managed, "UnityModManager/UnityModManager.dll"));
                var infoType = umm.GetType("UnityModManagerNet.UnityModManager+ModInfo", true);
                var info = Activator.CreateInstance(infoType);
                infoType.GetField("Id").SetValue(info, "DVLangHelper");
                infoType.GetField("Version").SetValue(info, "1.2.1");
                infoType.GetField("ManagerVersion").SetValue(info, "0.32.4");
                var entryType = umm.GetType("UnityModManagerNet.UnityModManager+ModEntry", true);
                var entry = Activator.CreateInstance(entryType, new[] { info, directories[1] });
                var helper = Assembly.LoadFrom(Path.Combine(directories[1], "DVLangHelper.Runtime.dll"));
                helper.GetType("DVLangHelper.Runtime.LangHelperMain", true).GetField("Instance").SetValue(null, entry);
                var injectorType = helper.GetType("DVLangHelper.Runtime.TranslationInjector", true);
                var localizer = Assembly.LoadFrom(Path.Combine(mod, "DVSeasons.dll")).GetType("DVSeasons.Mod.ModLocalization", true);
                var initialize = localizer.GetMethod("Initialize");
                var text = localizer.GetMethod("Text");
                var format = localizer.GetMethod("Format");
                initialize.Invoke(null, new object[] { mod });
                var sources = ((IEnumerable)injectorType.GetProperty("Instances").GetValue(null, null)).Cast<object>().ToList();
                var source = sources.Single(value => (string)injectorType.GetField("Id").GetValue(value) == "DVSeasons");
                Require(((IEnumerable)injectorType.GetProperty("Terms").GetValue(source, null)).Cast<object>().Count() == 33,
                    "CSV did not import all 33 terms.");
                injectorType.GetMethod("PerformInjection").Invoke(source, null);
                var manager = Assembly.LoadFrom(Path.Combine(managed, "I2.Localization.dll")).GetType("I2.Loc.LocalizationManager", true);
                var language = manager.GetProperty("CurrentLanguage");
                language.SetValue(null, "Russian", null);
                Require((string)text.Invoke(null, new object[] { "Settings.NewSnow" }) == "Новая система снега", "Russian lookup failed.");
                Require(((string)format.Invoke(null, new object[] { "Status.Snow", new object[] { 50f, 12.5f } })).Contains("12,5"),
                    "Russian format did not use the selected language.");
                language.SetValue(null, "English", null);
                Require((string)text.Invoke(null, new object[] { "Settings.NewSnow" }) == "New snow system", "English switch failed.");
                Require(((string)format.Invoke(null, new object[] { "Status.Snow", new object[] { 50f, 12.5f } })).Contains("12.5"),
                    "English format failed.");
                language.SetValue(null, "German", null);
                Require((string)text.Invoke(null, new object[] { "Settings.NewSnow" }) == "New snow system", "English fallback failed.");
                var fixture = Path.GetFullPath(Path.Combine(mod, "../../verification/langhelper-override.csv"));
                Directory.CreateDirectory(Path.GetDirectoryName(fixture));
                File.WriteAllText(fixture, "Key,Description,English,Russian,French\nDVSeasons/Settings.NewSnow,,New snow system,Проверка перевода,Neige de test\n");
                injectorType.GetMethod("AddTranslationsFromCsv").Invoke(source, new object[] { fixture, true });
                language.SetValue(null, "Russian", null);
                Require((string)text.Invoke(null, new object[] { "Settings.NewSnow" }) == "Проверка перевода", "Helper override was ignored.");
                initialize.Invoke(null, new object[] { mod });
                Require((string)text.Invoke(null, new object[] { "Settings.NewSnow" }) == "Проверка перевода", "Reinitialization erased override.");
                Require(((IEnumerable)injectorType.GetProperty("Instances").GetValue(null, null)).Cast<object>().Count() == sources.Count,
                    "Reinitialization duplicated the source.");
                language.SetValue(null, "French", null);
                Require((string)text.Invoke(null, new object[] { "Settings.NewSnow" }) == "Neige de test", "Third-party language was ignored.");
                injectorType.GetMethod("Reload").Invoke(source, null);
                language.SetValue(null, "Russian", null);
                Require((string)text.Invoke(null, new object[] { "Settings.NewSnow" }) == "Новая система снега", "Base CSV reload failed.");
                Debug.Log("DVSEASONS_LANGUAGE_HELPER_OK: actual Helper 1.2.1 and I2; 33 terms, RU/EN switching, number formats, English fallback, RU/FR CSV overrides, reload and duplicate-source protection.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name.EndsWith(".resources", StringComparison.Ordinal)) return null;
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
