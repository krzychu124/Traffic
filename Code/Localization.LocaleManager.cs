using System;
using Colossal.Localization;
using Game.SceneFlow;

namespace Traffic
{
    public partial class Localization
    {
        internal class LocaleManager: IDisposable
        {
            private bool _disposed;
            private string _prevGameLocale;
            private LocalizationManager _vanillaLocalizationManager;
            private VanillaLocalizationObserver _localizationObserver;

            public LocaleManager()
            {
                _vanillaLocalizationManager = GameManager.instance.localizationManager;
                _prevGameLocale = _vanillaLocalizationManager.activeLocaleId;
                Logger.Info("Subscribing to LocalizationManager.onActiveDictionaryChanged");
            }

            public void RegisterVanillaLocalizationObserver(ModSettings settings)
            {
                _localizationObserver?.Dispose();
                _localizationObserver = new VanillaLocalizationObserver(this, settings);
            }

            public void UseVanillaLanguage(string currentLanguage)
            {
                Logger.Info($"(UseVanillaLanguage) current mod locale: {currentLanguage}, gameLocale {_vanillaLocalizationManager.activeLocaleId}");
                _localizationObserver.DisableObserver();
                var manager = GameManager.instance.localizationManager;
                string gameLocale = manager.activeLocaleId;
                Logger.DebugLocale($"(UseVanillaLanguage) Checkbox checked with {true}, current {currentLanguage}, vanilla {gameLocale}");
                Logger.DebugLocale($"(UseVanillaLanguage) Removing sources {currentLanguage}, {gameLocale} from current locale");
                //remove custom source
                manager.RemoveSource(gameLocale, LocaleSources[currentLanguage].Item3);
                manager.AddSource(currentLanguage, LocaleSources[currentLanguage].Item3);
                //remove original source
                manager.RemoveSource(gameLocale, LocaleSources[gameLocale].Item3);
                Logger.DebugLocale($"(UseVanillaLanguage) Add source {gameLocale} => {gameLocale}");
                //set modified source (might be original if mod language is matching with vanilla)
                manager.AddSource(gameLocale, LocaleSources[gameLocale].Item3);
                _localizationObserver.EnableObserver();
            }

            public void UseCustomLanguage(string customLanguage)
            {
                string gameLocale = _vanillaLocalizationManager.activeLocaleId;
                Logger.Info($"(UseCustomLanguage) mod locale: {customLanguage}, gameLocale {gameLocale}");
            }

            public void UseLocale(string locale, string currentLocale, bool useGameLocale)
            {
                if (useGameLocale)
                {
                    return;
                }

                if (!LocaleSources.ContainsKey(locale))
                {
                    Logger.Warning($"Unsupported locale: {locale}");
                    return;
                }
                
                _localizationObserver.DisableObserver();
                string gameLocale = _vanillaLocalizationManager.activeLocaleId;
                Logger.DebugLocale($"(UseLocale) Current Locale {currentLocale} changing to.. {locale} | Vanilla: {gameLocale}");
                Logger.DebugLocale($"(UseLocale) Removing sources {gameLocale}, {currentLocale}, {locale} from current locale");
                //remove original source
                _vanillaLocalizationManager.RemoveSource(gameLocale, LocaleSources[gameLocale].Item3);
                //remove modified source
                _vanillaLocalizationManager.RemoveSource(gameLocale, LocaleSources[currentLocale].Item3);
                //remove modified source (in case of swapping from the same)
                _vanillaLocalizationManager.RemoveSource(gameLocale, LocaleSources[locale].Item3);
                Logger.DebugLocale($"(UseLocale) Add source {locale} => {gameLocale}");
                //set modified source (might be original if mod language is matching with vanilla)
                _vanillaLocalizationManager.AddSource(gameLocale, LocaleSources[locale].Item3);
                Logger.DebugLocale($"(UseLocale) Done!");
                _localizationObserver.EnableObserver();
            }
            
            public void Dispose()
            {
                if (_disposed) return;
                
                _localizationObserver.Dispose();
                _localizationObserver = null;
                _vanillaLocalizationManager = null;
                
                _disposed = true;
            }

            private class VanillaLocalizationObserver: IDisposable
            {
                private LocaleManager _localeManager;
                private ModSettings _settings;
                private bool _disableChangeCallback;
                
                internal VanillaLocalizationObserver(LocaleManager localeManager, ModSettings settings)
                {
                    _localeManager = localeManager;
                    _settings = settings;
                    
                    GameManager.instance.localizationManager.onActiveDictionaryChanged += OnActiveDictionaryChanged;
                }

                internal void EnableObserver()
                {
                    _disableChangeCallback = false;
                }

                internal void DisableObserver()
                {
                    _disableChangeCallback = true;
                }
                
                private void OnActiveDictionaryChanged()
                {
                    if (_disableChangeCallback)
                    {
                        Logger.DebugLocale($"(OnActiveDictionaryChanged) Skip Callback! UseGameLanguage {_settings.UseGameLanguage}, mod locale: {_settings.CurrentLocale}");
                        return;
                    }

                    var vanillaLocalizationManager = GameManager.instance.localizationManager;
                    string lastLocale = _localeManager._prevGameLocale;
                    string newLocale = _localeManager._vanillaLocalizationManager.activeLocaleId;
                    Logger.DebugLocale("(OnActiveDictionaryChanged) Dictionary changed!" +
                        $" Game locale: (last){lastLocale}, (new){vanillaLocalizationManager.activeLocaleId}," +
                        $" Current mod locale: {_settings.CurrentLocale}," +
                        $" Loaded mod locales: {LocaleSources.Count}");

                    if (_settings.UseGameLanguage)
                    {
                        Logger.DebugLocale($"Use Default locale {lastLocale} -> {newLocale}, mod locale: {_settings.CurrentLocale}");
                        //update Language dropdown value only
                        _settings.CurrentLocale = newLocale;
                        if (!vanillaLocalizationManager.activeDictionary.ContainsID(GetLanguageNameLocaleID()))
                        {
                            Logger.Warning($"(Use Default locale) Missing LocaleID in {newLocale}");
                        }
                    }
                    else
                    {
                        Logger.DebugLocale($"Use Custom locale {lastLocale} -> {newLocale}, mod locale: {_settings.CurrentLocale}");
                        string currentLocale = _settings.CurrentLocale;
                        if (LocaleSources.ContainsKey(newLocale) &&
                            LocaleSources.ContainsKey(currentLocale))
                        {
                            DisableObserver();
                            Logger.DebugLocale($"Matching Custom locale {currentLocale} ({lastLocale} -> {newLocale})");
                            vanillaLocalizationManager.RemoveSource(lastLocale, LocaleSources[currentLocale].Item3);
                            if (lastLocale != newLocale)
                            {
                                //make sure previous locale was reset to original
                                vanillaLocalizationManager.AddSource(lastLocale, LocaleSources[lastLocale].Item3);
                            }
                            // remove and add again current source dictionary
                            vanillaLocalizationManager.RemoveSource(newLocale, LocaleSources[currentLocale].Item3);
                            vanillaLocalizationManager.AddSource(newLocale, LocaleSources[currentLocale].Item3);
                            EnableObserver();
                        }
                    }

                    _localeManager._prevGameLocale = newLocale;
                }

                public void Dispose()
                {
                    Logger.Info("Unsubscribing from LocalizationManager.onActiveDictionaryChanged");
                    GameManager.instance.localizationManager.onActiveDictionaryChanged -= OnActiveDictionaryChanged;
                    _localeManager = null;
                    _settings = null;
                }
            }

            public static (string, bool) ApplySettings(string gameLocale, bool useGameLanguage, string currentLanguage)
            {
                Logger.Warning($"(ApplySettings) game locale {gameLocale} UseGameLocale: {useGameLanguage} mod locale: {currentLanguage}");
                if (!useGameLanguage)
                {
                    Logger.Warning($"Applying custom mod locale {currentLanguage} | current game locale: {gameLocale}");
                    LocalizationManager manager = GameManager.instance.localizationManager;
                    if (!LocaleSources.ContainsKey(currentLanguage))
                    {
                        Logger.Warning($"Custom mod locale {currentLanguage} not found, fallback to English, useGameLanguage ");
                        manager.RemoveSource(gameLocale, LocaleSources["en-US"].Item3);
                        manager.AddSource(gameLocale, LocaleSources["en-US"].Item3);
                        return (currentLanguage, true);
                    }
                        
                    //remove original source
                    manager.RemoveSource(gameLocale, LocaleSources[gameLocale].Item3);
                    //remove modified source
                    manager.RemoveSource(gameLocale, LocaleSources[currentLanguage].Item3);
                    //add modified source
                    manager.AddSource(gameLocale, LocaleSources[currentLanguage].Item3);
                    return (currentLanguage, false);
                }
                
                if (currentLanguage != gameLocale)
                {
                    Logger.Warning($"Use Game Language - detected different mod locale ({currentLanguage}) than currently set in the game settings ({gameLocale}). Falling back to the game locale");
                    return (gameLocale, true);
                }
                
                return (currentLanguage, true);
            }
        }
    }
}
