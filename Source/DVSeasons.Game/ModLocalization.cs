using System;
using System.Globalization;
using I2.Loc;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal static class ModLocalization
    {
        public static bool IsRussian
        {
            get
            {
                try
                {
                    var language = LocalizationManager.CurrentLanguage ?? string.Empty;
                    if (!string.IsNullOrEmpty(language))
                        return language.Equals("Russian", StringComparison.OrdinalIgnoreCase) ||
                               language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ||
                               language.IndexOf("рус", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch
                {
                    // Localization can be unavailable briefly while the main menu loads.
                }
                return Application.systemLanguage == SystemLanguage.Russian;
            }
        }

        public static string Text(bool russian, string ru, string en) { return russian ? ru : en; }

        public static string Number(bool russian, float value, string format)
        {
            return value.ToString(format, russian ? CultureInfo.CurrentCulture : CultureInfo.InvariantCulture);
        }
    }
}
