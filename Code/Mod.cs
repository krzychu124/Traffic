// #if DEBUG
//   #define LOCALIZATION_EXPORT
// #endif

namespace Traffic
{
    using System;
    using System.Linq;
    using System.Reflection;
    using Game;
    using Game.Modding;
    using Game.Net;
    using Game.Rendering;
    using Game.SceneFlow;
    using Game.Serialization;
    using Game.Tools;
    using JetBrains.Annotations;
    using Traffic.Debug;
    using Traffic.Rendering;
    using Traffic.Systems;
    using Traffic.Systems.DataMigration;
    using Traffic.Systems.ModCompatibility;
    using Traffic.Systems.PrioritySigns;
    using Traffic.Tools;
    using Traffic.UISystems;
    using Traffic.Utils;
    using Unity.Entities;
    using UnityEngine;
    using ApplyLaneConnectionsSystem = Traffic.Systems.LaneConnections.ApplyLaneConnectionsSystem;
    using GenerateConnectorsSystem = Traffic.Systems.LaneConnections.GenerateConnectorsSystem;
    using GenerateLaneConnectionsSystem = Traffic.Systems.LaneConnections.GenerateLaneConnectionsSystem;
    using SearchSystem = Traffic.Systems.LaneConnections.SearchSystem;
    using SyncCustomLaneConnectionsSystem = Traffic.Systems.LaneConnections.SyncCustomLaneConnectionsSystem;

    [UsedImplicitly]
    public class Mod : IMod
    {
        public const string MOD_NAME = "Traffic";
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(4);
        public static string InformationalVersion => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        public static bool IsTLEEnabled => _isTLEEnabled ??= GameManager.instance.modManager.ListModsEnabled().Any(x => x.StartsWith("C2VM.CommonLibraries.LaneSystem"));

        private static bool? _isTLEEnabled;

        internal ModSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Logger.Info($"{nameof(OnLoad)}, version: {InformationalVersion}");
            TrySearchingForIncompatibleTLEOnBepinEx();
            Settings = new ModSettings(this, false);
            Settings.RegisterKeyBindings();
            Settings.RegisterInOptionsUI();
            Colossal.IO.AssetDatabase.AssetDatabase.global.LoadSettings(ModSettings.SETTINGS_ASSET_NAME, Settings, new ModSettings(this, true));
            if (!GameManager.instance.localizationManager.activeDictionary.ContainsID(Settings.GetSettingsLocaleID()))
            {
                var source = new Localization.LocaleEN(Settings);
                GameManager.instance.localizationManager.AddSource("en-US", source);
                Localization.LoadLocales(this, source.ReadEntries(null, null).Count());
            }
            Settings.ApplyLoadedSettings();

            updateSystem.UpdateAt<ModUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateBefore<PreDeserialize<ModUISystem>>(SystemUpdatePhase.Deserialize);
            
#if DEBUG_GIZMO
            updateSystem.UpdateAt<LaneConnectorDebugSystem>(SystemUpdatePhase.DebugGizmos);
#endif
            updateSystem.UpdateAfter<ToolOverlaySystem, AreaRenderSystem>(SystemUpdatePhase.Rendering);

            // VanillaSystemHelpers.ModifyLaneSystemUpdateRequirements(updateSystem.World.GetOrCreateSystemManaged<LaneSystem>());
            updateSystem.World.GetOrCreateSystemManaged<LaneSystem>().Enabled = false;
            updateSystem.UpdateBefore<TrafficLaneSystem, LaneSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<SyncCustomLaneConnectionsSystem, TrafficLaneSystem>(SystemUpdatePhase.Modification4);
            updateSystem.UpdateBefore<SyncCustomPrioritiesSystem, TrafficLaneSystem>(SystemUpdatePhase.Modification4);
            
            /*data migration - requires NetCompositions to work correctly - not possible to run in SystemUpdatePhase.Deserialize */
            updateSystem.UpdateBefore<TrafficDataMigrationSystem, SyncCustomLaneConnectionsSystem>(SystemUpdatePhase.Modification4);
            
            updateSystem.UpdateAt<ModificationDataSyncSystem>(SystemUpdatePhase.Modification4B);
            updateSystem.UpdateAt<GenerateLaneConnectionsSystem>(SystemUpdatePhase.Modification3);
            updateSystem.UpdateAt<GenerateEdgePrioritiesSystem>(SystemUpdatePhase.Modification3);

            updateSystem.UpdateAt<ModRaycastSystem>(SystemUpdatePhase.Raycast);
            updateSystem.UpdateAfter<Traffic.Tools.ValidationSystem, Game.Tools.ValidationSystem>(SystemUpdatePhase.ModificationEnd);

            updateSystem.UpdateAt<PriorityToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<LaneConnectorToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<ApplyLaneConnectionsSystem, ApplyNetSystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateBefore<ApplyPrioritiesSystem, ApplyNetSystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateAt<GenerateConnectorsSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<GenerateHandles>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<SearchSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateAt<LaneConnectorToolTooltipSystem>(SystemUpdatePhase.UITooltip);

            updateSystem.UpdateBefore<PreDeserialize<ModDefaultsSystem>>(SystemUpdatePhase.Deserialize);
#if DEBUG
            updateSystem.UpdateAt<NetworkDebugUISystem>(SystemUpdatePhase.UIUpdate);
#endif
#if DEBUG_TOOL
            // updateSystem.UpdateAt<CleanUp>(SystemUpdatePhase.Cleanup);
            // updateSystem.UpdateAt<ApplyTool>(SystemUpdatePhase.ApplyTool);
            // updateSystem.UpdateAt<ClearTool>(SystemUpdatePhase.ClearTool);
#endif
            Logger.Info($"Registering check TLE installed and enabled. RenderedFrame: {Time.renderedFrameCount}");
            GameManager.instance.RegisterUpdater(TLECompatibilityFix);
            GameManager.instance.RegisterUpdater(ListEnabledMods);
            // NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;

#if LOCALIZATION_EXPORT
            Localization.LocalizationExport(this, Settings);
#endif
        }

        public void OnDispose()
        {
            Logger.Info(nameof(OnDispose));
            Settings?.UnregisterInOptionsUI();
            Settings?.Unload();
            Settings = null;
        }

        private static void TLECompatibilityFix()
        {
            if (IsTLEEnabled)
            {
                Logger.Info($"Detected TLE installed and enabled!");
                try
                {
                    World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<UpdateSystem>().UpdateAfter<TLEDataMigrationSystem>(SystemUpdatePhase.Deserialize);
                    ComponentSystemBase tleLaneSystem = World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged(Type.GetType("Game.Net.C2VMPatchedLaneSystem, C2VM.CommonLibraries.LaneSystem"));
                    // ModsCompatibilityHelpers.ModifyTLELaneSystemUpdateRequirements(tleLaneSystem);
                    tleLaneSystem.Enabled = false;
                    World.DefaultGameObjectInjectionWorld.GetOrCreateSystemManaged<LaneSystem>().Enabled = true;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"**Traffic** mod exception!\nSomething went wrong while fixing compatibility with **Traffic Lights Enhancement** mod");
                    Logger.Error($"{e.Message}\n{e.StackTrace}\nInnerException:\n{e.InnerException?.Message}");
                }
            }
        }

        private static void TrySearchingForIncompatibleTLEOnBepinEx()
        {
            try
            {
                Type type = Type.GetType("C2VM.TrafficLightsEnhancement.Plugin, C2VM.TrafficLightsEnhancement", false);
                Type type2 = Type.GetType("C2VM.CommonLibraries.LaneSystem.Plugin, C2VM.CommonLibraries.LaneSystem", false);
                if (type != null || type2 != null)
                {
                    string path = System.IO.Directory.GetParent((type ?? type2).Assembly.Location)?.Parent?.FullName ?? string.Empty;
                    string id = "Traffic_Compatibility_Detector";
                    Game.PSI.NotificationSystem.Push(id, 
                        "Traffic Mod Compatibility Report", 
                        "Detected incompatible Traffic Lights Enhancement Mod!\nClick for more details.",
                        progressState: Colossal.PSI.Common.ProgressState.Failed,
                        onClicked: () => {
                            Game.UI.MessageDialogWithDetails dialog = new(
                                "Traffic Mod Compatibility Report",
                                "**Traffic** mod compatibility detector found incompatible version of **Traffic Lights Enhancement** mod",
                                $"Please remove **Traffic Lights Enhancement** from \n{path.Replace("\\", "\\\\")}\n\n" +
                                "If you want to continue using that mod, subscribe the latest version from the **PDX Mods** platform.\n" +
                                "If you don't have any other mods running on **BepinEx**, consider removing this plugin as well.",
                                copyButton: true,
                                Game.UI.Localization.LocalizedString.Id("Common.OK"));
                            GameManager.instance.userInterface.appBindings.ShowMessageDialog(dialog, _ => Game.PSI.NotificationSystem.Pop(id));
                        }
                    );
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        private static void ListEnabledMods()
        {
            Logger.Info("\n======= Enabled Mods =======\n\t"+string.Join("\n\t",GameManager.instance.modManager.ListModsEnabled()) + "\n============================");
        }
    }
#if DEBUG_TOOL
    // internal partial class CleanUp : GameSystemBase
    // {
    //     protected override void OnUpdate() {
    //         Logger.Info("CleanUp!");
    //     }
    // }
    //
    // internal partial class ApplyTool : GameSystemBase
    // {
    //     private ToolSystem _toolSystem;
    //     private LaneConnectorToolSystem _laneConnectorTool;
    //     protected override void OnCreate() {
    //         base.OnCreate();
    //         _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
    //         _laneConnectorTool = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
    //     }
    //
    //     protected override void OnUpdate() {
    //         if (_toolSystem.activeTool == _laneConnectorTool)
    //         {
    //             Logger.Info("ApplyTool!");
    //         }
    //     }
    // }
    // internal partial class ClearTool : GameSystemBase
    // {
    //     private ToolSystem _toolSystem;
    //     private LaneConnectorToolSystem _laneConnectorTool;
    //     protected override void OnCreate() {
    //         base.OnCreate();
    //         _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
    //         _laneConnectorTool = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();
    //     }
    //
    //     protected override void OnUpdate() {
    //         if (_toolSystem.activeTool == _laneConnectorTool)
    //         {
    //             Logger.Info("ClearTool!");
    //         }
    //     }
    // }
#endif
}
