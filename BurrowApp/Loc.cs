using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Burrow
{
    /// <summary>
    /// Translations. The language follows Windows' display language unless the user picks one (title bar).
    /// Missing keys fall back to English, then to the key itself, so nothing ever shows blank.
    /// </summary>
    public static class Loc
    {
        public static readonly string[] Languages = { "en", "tr", "ru" };

        public static string Current { get; private set; } = "en";

        public static void Init(string setting) => Current = Resolve(setting);

        /// <summary>"auto" → Windows' display language if we have it, else English.</summary>
        public static string Resolve(string setting)
        {
            if (!string.IsNullOrEmpty(setting) && Languages.Contains(setting)) return setting;
            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            return Languages.Contains(two) ? two : "en";
        }

        public static string NativeName(string code) => code switch { "tr" => "Türkçe", "ru" => "Русский", _ => "English" };
        public static string ShortName(string code) => code.ToUpperInvariant();

        public static string T(string key, params object[] args)
        {
            var table = Current == "tr" ? Strings.Tr : Current == "ru" ? Strings.Ru : Strings.En;
            if (!table.TryGetValue(key, out var s) && !Strings.En.TryGetValue(key, out s)) s = key;
            return args.Length == 0 ? s : string.Format(CultureInfo.InvariantCulture, s, args);
        }

        /// <summary>Every key that exists in English but is missing in another language (used by tests).</summary>
        public static IEnumerable<string> Missing(string code)
        {
            var table = code == "tr" ? Strings.Tr : code == "ru" ? Strings.Ru : Strings.En;
            return Strings.En.Keys.Where(k => !table.ContainsKey(k));
        }
    }
}
