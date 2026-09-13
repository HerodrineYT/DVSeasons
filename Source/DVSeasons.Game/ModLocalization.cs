using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DVLangHelper.Runtime;
using I2.Loc;

namespace DVSeasons.Mod
{
    internal static class ModLocalization
    {
        private const string Prefix = "DVSeasons/";
        private static readonly Dictionary<string, string> startupEnglish = new Dictionary<string, string>();

        public static void Initialize(string modPath)
        {
            var csvPath = Path.Combine(modPath, "Localization", "DVSeasons.csv");
            if (!File.Exists(csvPath)) throw new FileNotFoundException("DVSeasons translations are missing.", csvPath);
            // Helper owns this persistent source. Reuse it after UMM reloads;
            // world unloads must not create duplicate sources or erase overrides.
            var injector = TranslationInjector.Instances.FirstOrDefault(source => source.Id == "DVSeasons");
            if (injector == null)
            {
                injector = new TranslationInjector("DVSeasons");
                injector.AddTranslationsFromCsv(csvPath);
            }
            var englishIndex = injector.Languages.ToList().FindIndex(language => language.Name == "English");
            startupEnglish.Clear();
            foreach (var term in injector.Terms)
                if (englishIndex >= 0) startupEnglish[term.Term] = term.GetTranslation(englishIndex);
            if (!startupEnglish.ContainsKey(Prefix + "UI.Title"))
                throw new InvalidDataException("Language Helper could not read the DVSeasons translation table.");
        }

        public static string Text(string key)
        {
            var term = Prefix + key;
            var value = LocalizationManager.GetTranslation(term);
            if (!string.IsNullOrEmpty(value)) return value;
            value = LocalizationManager.GetTranslation(term, overrideLanguage: "English");
            if (!string.IsNullOrEmpty(value)) return value;
            // Settings may open before Helper injects sources after game startup.
            return startupEnglish.TryGetValue(term, out value) ? value : key;
        }

        public static string Format(string key, params object[] values)
        {
            var culture = CultureInfo.InvariantCulture;
            try
            {
                var code = LocalizationManager.CurrentLanguageCode;
                if (!string.IsNullOrEmpty(code)) culture = CultureInfo.GetCultureInfo(code);
            }
            catch (CultureNotFoundException) { }
            return string.Format(culture, Text(key), values);
        }
    }
}
