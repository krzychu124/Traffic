using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal;
using Colossal.Json;

namespace Traffic
{
    public partial class Localization
    {
        public class ModLocale : IDictionarySource
        {
            private string _localeId;
            private string _localePath;
            private Dictionary<string, string> _translations;

            public string LocaleId => _localeId;

            public ModLocale(string localeId, string localePath)
            {
                _localeId = localeId;
                _localePath = localePath;
                _translations = new Dictionary<string, string>();
            }

            public ModLocale Load(float refTranslationCount)
            {
                LocaleSources.Remove(_localeId);
                
                if (File.Exists(_localePath))
                {
                    try { 
                        Variant variant = JSON.Load(File.ReadAllText(_localePath));
                        _translations.Clear();
                        _translations = variant.Make<Dictionary<string, string>>();
#if DEBUG_LOCALE
                        Logger.DebugLocale($"Loaded {_translations.Keys.Count} keys for {_localeId} || {variant.Count}");
                        StringBuilder sb = new StringBuilder();
                        foreach (KeyValuePair<string, string> keyValuePair in _translations)
                        {
                            sb.Append(keyValuePair.Key).Append(" | ").AppendLine(keyValuePair.Value);
                        }
                        Logger.Debug($"Strings:\n{sb}");
#endif
                        string coverageKey = ModSettings.Instance.GetOptionLabelLocaleID(nameof(ModSettings.TranslationCoverageStatus));
                        string coverage = $"{Convert.ToInt32((_translations.Count / refTranslationCount) * 100) }%";
                        // fill missing translation keys
                        var fallback = LocaleSources["en-US"].Item3.ReadEntries(null, null).ToDictionary(k => k.Key, k => k.Value);
                        foreach (KeyValuePair<string,string> keyValuePair in fallback)
                        {
                            _translations.TryAdd(keyValuePair.Key, keyValuePair.Value);
                        }
                        _translations[coverageKey] = $"{(_translations.TryGetValue(coverageKey, out string title) && string.IsNullOrEmpty(title) ? $"Translation status: {coverage}": $"{title} {coverage}")}";
                        
                        LocaleSources[_localeId] = new Tuple<string, string, IDictionarySource>(
                            $"{_translations.GetValueOrDefault(GetLanguageNameLocaleID(), _localeId)}", 
                            $"{coverage}",
                            this);
                        languageSourceVersion++;
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Something went wrong while loading locale {_localeId} from {_localePath} \n{e}");
                    }
                }

                return this;
            }

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            {
                return _translations;
            }

            public void Unload()
            {
                Logger.Debug($"Unloading locale {_localeId}");
                // LocaleSources?.Remove(_localeId);
            }

            public override string ToString()
            {
                return $"Traffic.Locale.{_localeId}";
            }
        }
    }
}
