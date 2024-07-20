using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Input;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Widgets;
using Traffic.Components;
using Traffic.Rendering;
using Traffic.Tools;
using Unity.Entities;
using UnityEngine.Device;

namespace Traffic
{
    [FileLocation("Traffic")]
    [SettingsUITabOrder(GeneralTab, KeybindingsTab)]
    [SettingsUIGroupOrder(MainSection, LaneConnectorSection, PrioritiesSection, OverlaysSection, AboutSection, ToolsSection, PriorityToolSection, SelectedNodeSection, OtherSection)]
    [SettingsUIShowGroupName( MainSection, LaneConnectorSection, PrioritiesSection, OverlaysSection, AboutSection, ToolsSection, PriorityToolSection, SelectedNodeSection, OtherSection)]
    public partial class ModSettings : ModSetting
    {
        internal const string SETTINGS_ASSET_NAME = "Traffic General Settings";
        internal static ModSettings Instance { get; private set; }
        
        internal const string GeneralTab = "General";  
        internal const string KeybindingsTab = "Keybindings";  
        internal const string MainSection = "General";
        internal const string LaneConnectorSection = "LaneConnections";
        internal const string PrioritiesSection = "Priorities";
        internal const string OverlaysSection = "Overlays";
        internal const string AboutSection = "About";

        private Dictionary<string, ProxyBinding.Watcher> _vanillaBindingWatchers;
        private Localization.LocaleManager _localeManager;

        [SettingsUISection(GeneralTab, MainSection)]
        [SettingsUISetter(typeof(ModSettings), nameof(OnUseGameLanguageSet))]
        public bool UseGameLanguage { get; set; }
        
        [SettingsUISection(GeneralTab, MainSection)]
        [SettingsUIDropdown(typeof(ModSettings), nameof(GetLanguageOptions))]
        [SettingsUIValueVersion(typeof(Localization), nameof(Localization.languageSourceVersion))]
        [SettingsUISetter(typeof(ModSettings), nameof(ChangeModLanguage))]        
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(UseGameLanguage))]
        public string CurrentLocale { get; set; } = "en-US";

        [SettingsUISection(GeneralTab, MainSection)]
        [SettingsUIMultilineText("coui://ui-mods/traffic-images/crowdin-icon-white.svg")]
        public string TranslationCoverageStatus => string.Empty;
        
        [SettingsUISection(GeneralTab, LaneConnectorSection)]
        [SettingsUIButton()]
        [SettingsUIConfirmation()]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsNotGameOrEditor))]
        [SettingsUIHideByCondition(typeof(ModSettings), nameof(IsNotGameOrEditor))]
        public bool ResetLaneConnections
        {
            set {
                World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LaneConnectorToolSystem>().ResetAllConnections();
            }
        }
        
        [SettingsUISection(GeneralTab, PrioritiesSection)]
        [SettingsUIButton()]
        [SettingsUIConfirmation()]
        [SettingsUIDisableByCondition(typeof(ModSettings), nameof(IsNotGameOrEditor))]
        [SettingsUIHideByCondition(typeof(ModSettings), nameof(IsNotGameOrEditor))]
        public bool ResetPriorities
        {
            set {
                World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<PriorityToolSystem>().ResetAllPriorities();
            }
        }
        
        [SettingsUISlider(min = 0.2f, max = 2f, step = 0.1f, unit = Game.UI.Unit.kFloatSingleFraction)]
        [SettingsUISection(GeneralTab, OverlaysSection)]
        public float ConnectionLaneWidth { get; set; }
        
        [SettingsUISlider(min = 0.5f, max = 2f, step = 0.1f, unit = Game.UI.Unit.kFloatSingleFraction)]
        [SettingsUISection(GeneralTab, OverlaysSection)]
        public float ConnectorSize { get; set; }
        
        [SettingsUISlider(min = 0.1f, max = 1f, step = 0.1f, unit = Game.UI.Unit.kFloatSingleFraction)]
        [SettingsUISection(GeneralTab, OverlaysSection)]
        public float FeedbackOutlineWidth { get; set; }

        [SettingsUIButton()]
        [SettingsUISection(GeneralTab, OverlaysSection)]
        public bool ResetStyle
        {
            set {
                ToolOverlaySystem toolOverlaySystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<ToolOverlaySystem>();
                toolOverlaySystem.SetDefautlOverlayParams(out ToolOverlayParameterData data);
                ConnectorSize = data.laneConnectorSize;
                ConnectionLaneWidth = data.laneConnectorLineWidth;
                FeedbackOutlineWidth = data.feedbackLinesWidth;
                ApplyAndSave();
            }
        }
        
        public bool IsNotGameOrEditor()
        {
            return (GameManager.instance.gameMode & GameMode.GameOrEditor) == 0;
        }

        [SettingsUISection(GeneralTab, AboutSection)]
        public string ModVersion => Mod.Version;
        
        [SettingsUISection(GeneralTab, AboutSection)]
        public string InformationalVersion => Mod.InformationalVersion;

        [SettingsUISection(GeneralTab, AboutSection)]
        public bool OpenRepositoryAtVersion
        {
            set {
                try
                {
                    Application.OpenURL($"https://github.com/krzychu124/Traffic/commit/{Mod.InformationalVersion.Split('+')[1]}");
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }

        public ModSettings(IMod mod, bool asDefault) : base(mod)
        {
            Instance = this;
            _vanillaBindingWatchers = new Dictionary<string, ProxyBinding.Watcher>();
            if (!asDefault)
            {
                _localeManager = new Localization.LocaleManager();
                _localeManager.RegisterVanillaLocalizationObserver(this);
            }
            SetDefaults();
        }
        
        public sealed override void SetDefaults()
        {
            ConnectorSize = 1f;
            ConnectionLaneWidth = 0.4f;
            FeedbackOutlineWidth = 0.3f;
            UseGameLanguage = true;
            UseVanillaToolActions = true;
        }

        public void OnUseGameLanguageSet(bool value)
        {
            if (UseGameLanguage == value)
            {
                Logger.Warning($"(OnUseGameLanguageSet) No state changed {value}");
                return;
            }
            
            if (value)
            {
                _localeManager.UseVanillaLanguage(CurrentLocale);
                CurrentLocale = GameManager.instance.localizationManager.activeLocaleId;
            }
            else
            {
                _localeManager.UseCustomLanguage(CurrentLocale);
            }
        }
        
        private void ChangeModLanguage(string value)
        {
            _localeManager.UseLocale(value, CurrentLocale, UseGameLanguage);
        }

        public override void Apply()
        {
            ToolOverlaySystem toolOverlaySystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<ToolOverlaySystem>();
            toolOverlaySystem.ApplyOverlayParams(new ToolOverlayParameterData()
            {
                feedbackLinesWidth = FeedbackOutlineWidth,
                laneConnectorSize = ConnectorSize,
                laneConnectorLineWidth = ConnectionLaneWidth,
            });
            base.Apply();
        }

        private ProxyBinding.Watcher MimicVanillaAction(ProxyAction vanillaAction, ProxyAction customAction, string actionGroup)
        {
            ProxyBinding customActionBinding = customAction.bindings.FirstOrDefault(b => b.group == actionGroup);
            ProxyBinding vanillaActionBinding = vanillaAction.bindings.FirstOrDefault(b => b.group == actionGroup);
            ProxyBinding.Watcher actionWatcher = new ProxyBinding.Watcher(vanillaActionBinding, binding => SetMimic(customActionBinding, binding));
            SetMimic(customActionBinding, actionWatcher.binding);
            return actionWatcher;
        }

        private void SetMimic(ProxyBinding mimic, ProxyBinding buildIn)
        {
            var newMimicBinding = mimic.Copy();
            newMimicBinding.path = buildIn.path;
            newMimicBinding.modifiers = buildIn.modifiers;
            InputManager.instance.SetBinding(newMimicBinding, out _);
        }

        internal void ApplyLoadedSettings()
        {
            if (UseVanillaToolActions)
            {
                RegisterToolActionWatchers();
            }
            
            var manager = GameManager.instance.localizationManager;
            string gameLocale = manager.activeLocaleId;
            (CurrentLocale, UseGameLanguage) = Localization.LocaleManager.ApplySettings(gameLocale, UseGameLanguage, CurrentLocale);
        }

        private DropdownItem<string>[] GetLanguageOptions()
        {
            return Localization.LocaleSources.Select(pair => new DropdownItem<string>()
            {
                value = pair.Key,
                displayName = pair.Value.Item1
            }).ToArray();
        }

        internal void Unload()
        {
            _localeManager?.Dispose();
            _localeManager = null;
            DisposeToolActionWatchers();
        }
    }
}
