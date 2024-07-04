using System;
using System.Collections.Generic;
using System.IO;
using Colossal;
using Game.SceneFlow;

namespace Traffic
{
    public partial class Localization
    {
        internal static readonly Dictionary<string, Tuple<string, string, IDictionarySource>> LocaleSources = new Dictionary<string, Tuple<string, string, IDictionarySource>>();
        internal static int languageSourceVersion = 0;

        public static string GetToolTooltipLocaleID(string tool, string value)
        {
            return $"{Mod.MOD_NAME}.Tooltip.Tools[{tool}][{value}]";
        }
        
        public static string GetLanguageNameLocaleID()
        {
            return $"{Mod.MOD_NAME}.Language.DisplayName";
        }

        internal static void LoadLocales(Mod mod, float refTranslationCount)
        {
            if (GameManager.instance.modManager.TryGetExecutableAsset(mod, out var asset))
            {
                string directory = Path.Combine(Path.GetDirectoryName(asset.path) ?? "", "Localization");
                if (Directory.Exists(directory))
                {
                    foreach (string localeFile in Directory.EnumerateFiles(directory, "*.json"))
                    {
                        string localeId = Path.GetFileNameWithoutExtension(localeFile);
                        Logger.DebugLocale($"Loading locale {localeId} from: {localeFile}");
                        ModLocale locale = new ModLocale(localeId, localeFile).Load(refTranslationCount);
                        GameManager.instance.localizationManager.AddSource(localeId, locale);
                    }
                }
                else
                {
                    Logger.Warning("Locale directory not found!");
                }
            }
        }

#if LOCALIZATION_EXPORT
        internal static void LocalizationExport(Mod mod, ModSettings settings)
        {
            if (GameManager.instance.modManager.TryGetExecutableAsset(mod, out var asset))
            {
                var keyValuePairs = new Localization.LocaleEN(settings).Load(true);
                var entries = System.Linq.Enumerable.ToDictionary(keyValuePairs, p => p.Key, p => p.Value);
                string directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(asset.path), "Localization");
                string filePath = System.IO.Path.Combine(directory, "TranslationSource.json");
                if (!System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
                System.IO.File.WriteAllText(filePath,Colossal.Json.JSON.Dump(entries), System.Text.Encoding.UTF8);
                Game.PSI.NotificationSystem.Push("Traffic.LocalizationExport", "Traffic - Export Locale Source Strings", $"Source Strings Exported Successfully to: \n{filePath}", onClicked: () => {
                    Game.PSI.NotificationSystem.Pop("Traffic.LocalizationExport");
                });
            }
        }
#endif 
    }
}
