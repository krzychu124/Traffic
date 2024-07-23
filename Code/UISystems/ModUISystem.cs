using System.Collections.Generic;
using System.Linq;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.Input;
using Game.Rendering;
using Game.Serialization;
using Game.Settings;
using Game.Tools;
using Game.UI;
using Traffic.CommonData;
using Traffic.Debug;
using Traffic.Tools;
using Unity.Entities;

namespace Traffic.UISystems
{
    public partial class ModUISystem : UISystemBase, IPreDeserialize
    {
        private EntityQuery _actionOverlayQuery;
        private GetterValueBinding<List<Entity>> _affectedIntersectionsBinding;
        private SelectedIntersectionData _selectedIntersectionData;
        private ModKeyBinds _keyBindings;
        private List<Entity> _affectedIntersections;
        private CameraUpdateSystem _cameraUpdateSystem;
        private ToolSystem _toolSystem;
        private DefaultToolSystem _defaultTool;
        private LaneConnectorToolSystem _laneConnectorTool;
        private ProxyAction _toggleLaneConnectorToolAction;

        public override GameMode gameMode
        {
            get { return GameMode.GameOrEditor; }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _affectedIntersections = new List<Entity>();
            _actionOverlayQuery = GetEntityQuery(ComponentType.ReadOnly<ActionOverlayData>(), ComponentType.Exclude<Deleted>());
            _selectedIntersectionData = new SelectedIntersectionData();
            _cameraUpdateSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _defaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            _laneConnectorTool = World.GetOrCreateSystemManaged<LaneConnectorToolSystem>();

            //keybindings
            _keyBindings = new ModKeyBinds();
            _toggleLaneConnectorToolAction = ModSettings.Instance.GetAction(ModSettings.KeyBindAction.ToggleLaneConnectorTool);

            //ui bindings
            AddUpdateBinding(new GetterValueBinding<SelectedIntersectionData>(Mod.MOD_NAME, UIBindingConstants.SELECTED_INTERSECTION, () => SelectedIntersection));
            AddUpdateBinding(new GetterValueBinding<bool>(Mod.MOD_NAME, UIBindingConstants.LOADING_ERRORS_PRESENT, () => HasLoadingErrors));
            AddUpdateBinding(new GetterValueBinding<ModKeyBinds>(Mod.MOD_NAME, UIBindingConstants.KEY_BINDINGS, () => CurrentKeyBindings));
            AddBinding(_affectedIntersectionsBinding = new GetterValueBinding<List<Entity>>(Mod.MOD_NAME, UIBindingConstants.ERROR_AFFECTED_INTERSECTIONS, () => AffectedIntersections, new ListWriter<Entity>()));
            AddBinding(new TriggerBinding<ActionOverlayPreview>(Mod.MOD_NAME, UIBindingConstants.SET_ACTION_OVERLAY_PREVIEW, SetActionOverlayPreviewState, new EnumReader<ActionOverlayPreview>()));
            AddBinding(new TriggerBinding(Mod.MOD_NAME, UIBindingConstants.APPLY_TOOL_ACTION_PREVIEW, ApplyActionOverlayPreview));
            AddBinding(new TriggerBinding<bool>(Mod.MOD_NAME, UIBindingConstants.TOGGLE_TOOL, ToggleTool));
            AddBinding(new TriggerBinding<Entity>(Mod.MOD_NAME, UIBindingConstants.NAVIGATE_TO_ENTITY, NavigateToEntity));
            AddBinding(new TriggerBinding<int>(Mod.MOD_NAME, UIBindingConstants.REMOVE_ENTITY_FROM_LIST, RemoveEntityFromList));
            EntityManager.CreateSingleton<ActionOverlayData>();
            ModSettings.Instance.onSettingsApplied += ModSettingsApplied;
        }

        public SelectedIntersectionData SelectedIntersection
        {
            get { return _selectedIntersectionData; }
            set {
                if (!value.entity.Equals(_selectedIntersectionData.entity))
                {
                    _selectedIntersectionData = value;
                    SetActionOverlayPreviewState(ActionOverlayPreview.None);
                }
            }
        }

        private bool HasLoadingErrors
        {
            get { return _affectedIntersections.Count > 0; }
        }

        private List<Entity> AffectedIntersections
        {
            get { return _affectedIntersections; }
        }

        private ModKeyBinds CurrentKeyBindings
        {
            get { return _keyBindings; }
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (_toggleLaneConnectorToolAction.WasPerformedThisFrame())
            {
                _toolSystem.activeTool = _toolSystem.activeTool == _laneConnectorTool ? _defaultTool : _laneConnectorTool;
            }
        }

        private void ModSettingsApplied(Setting setting)
        {
            Logger.Info($"Mod settings has been applied ({UnityEngine.Time.frameCount})");
            _keyBindings = new ModKeyBinds();
            if (setting is ModSettings ms)
            {
                _keyBindings.UpdateStrings(ms.GetActions());
            }
        }

        public void ApplyActionOverlayPreview()
        {
            if (_laneConnectorTool.Enabled)
            {
                _laneConnectorTool.ToolMode = LaneConnectorToolSystem.Mode.ApplyQuickModifications;
            }
        }

        public void SetActionOverlayPreviewState(ActionOverlayPreview state)
        {
            bool isValid = EntityManager.Exists(_selectedIntersectionData.entity);
            var actionOverlayData = _actionOverlayQuery.GetSingleton<ActionOverlayData>();
            actionOverlayData.entity = state != ActionOverlayPreview.None && isValid ? _selectedIntersectionData.entity : Entity.Null;
            actionOverlayData.mode = isValid ? state : ActionOverlayPreview.None;
            SystemAPI.SetSingleton(actionOverlayData);
        }

        private void ToggleTool(bool enable)
        {
            if (enable && _toolSystem.activeTool != _laneConnectorTool)
            {
                _toolSystem.selected = Entity.Null;
                _toolSystem.activeTool = _laneConnectorTool;
            } 
            else if (!enable && _toolSystem.activeTool == _laneConnectorTool)
            {
                _toolSystem.selected = Entity.Null;
                _toolSystem.activeTool = _defaultTool;
            }
        }

        private void NavigateToEntity(Entity entity)
        {
            if (_cameraUpdateSystem.orbitCameraController != null && entity != Entity.Null)
            {
                _cameraUpdateSystem.orbitCameraController.followedEntity = entity;
                _cameraUpdateSystem.orbitCameraController.TryMatchPosition(_cameraUpdateSystem.activeCameraController);
                _cameraUpdateSystem.activeCameraController = _cameraUpdateSystem.orbitCameraController;
            }
        }

        private void RemoveEntityFromList(int entityIndex)
        {
            if (entityIndex < 0)
            {
                _affectedIntersections.Clear();
                _affectedIntersectionsBinding?.TriggerUpdate();
            }
            else if (_affectedIntersections.Count > entityIndex)
            {
                _affectedIntersections.RemoveAt(entityIndex);
                _affectedIntersectionsBinding?.TriggerUpdate();
            }
        }

        internal void AddToAffectedIntersections(Entity e)
        {
            if (!_affectedIntersections.Contains(e))
            {
                _affectedIntersections.Add(e);
            }
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            bool isGameOrEditor = mode == GameMode.Game || mode == GameMode.Editor;
            _toggleLaneConnectorToolAction.shouldBeEnabled = isGameOrEditor;
            Logger.Info($"OnGamePreload: {purpose} | {mode}");
            if (isGameOrEditor)
            {
                ModSettingsApplied(ModSettings.Instance);
            }
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
#if DEBUG_GIZMO
            World.GetExistingSystemManaged<LaneConnectorDebugSystem>().RefreshGizmoDebug();
#endif
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            ModSettings.Instance.onSettingsApplied -= ModSettingsApplied;
            if (SystemAPI.TryGetSingletonEntity<ActionOverlayData>(out Entity actionOverlayEntity))
            {
                EntityManager.DestroyEntity(actionOverlayEntity);
            }
        }

        public struct SelectedIntersectionData : IJsonWritable
        {
            public Entity entity;

            public void Write(IJsonWriter writer)
            {
                writer.TypeBegin(GetType().FullName);
                writer.PropertyName(nameof(entity));
                writer.Write(entity);
                writer.TypeEnd();
            }
        }

        public enum ActionOverlayPreview
        {
            None,
            RemoveAllConnections = 1,
            RemoveUTurns = 2,
            RemoveUnsafe = 3,
            ResetToVanilla = 4,
        }

        public class ModKeyBinds : IJsonWritable
        {
            //TODO Gamepad support (pass multiple or choose based on current input type)
            public ProxyBinding laneConnectorTool;
            public ProxyBinding removeAllConnections;
            public ProxyBinding removeUTurns;
            public ProxyBinding removeUnsafe;
            public ProxyBinding resetDefaults;

            public void Write(IJsonWriter writer)
            {
                writer.TypeBegin(GetType().FullName);
                writer.PropertyName(nameof(laneConnectorTool));
                writer.Write(laneConnectorTool);
                writer.PropertyName(nameof(removeAllConnections));
                writer.Write(removeAllConnections);
                writer.PropertyName(nameof(removeUTurns));
                writer.Write(removeUTurns);
                writer.PropertyName(nameof(removeUnsafe));
                writer.Write(removeUnsafe);
                writer.PropertyName(nameof(resetDefaults));
                writer.Write(resetDefaults);
                writer.TypeEnd();
            }

            public void UpdateStrings(IEnumerable<ProxyAction> keybinds)
            {
                foreach (ProxyAction proxyAction in keybinds)
                {
                    switch (proxyAction.name)
                    {
                        case ModSettings.KeyBindAction.ApplyTool:
                        case ModSettings.KeyBindAction.CancelTool:
                            break;
                        case ModSettings.KeyBindAction.ToggleLaneConnectorTool:
                            laneConnectorTool = proxyAction.bindings.FirstOrDefault();
                            break;
                        case ModSettings.KeyBindAction.RemoveAllConnections:
                            removeAllConnections = proxyAction.bindings.FirstOrDefault();
                            break;
                        case ModSettings.KeyBindAction.RemoveUTurns:
                            removeUTurns = proxyAction.bindings.FirstOrDefault();
                            break;
                        case ModSettings.KeyBindAction.RemoveUnsafe:
                            removeUnsafe = proxyAction.bindings.FirstOrDefault();
                            break;
                        case ModSettings.KeyBindAction.ResetIntersectionToDefaults:
                            resetDefaults = proxyAction.bindings.FirstOrDefault();
                            break;
                        default:
                            Logger.DebugError($"Not supported mod key binding action: {proxyAction.name}");
                            break;
                    }
                }
            }
        }

        public void PreDeserialize(Context context)
        {
            _selectedIntersectionData = new SelectedIntersectionData();
            _affectedIntersections.Clear();
            _affectedIntersectionsBinding?.TriggerUpdate();
        }
    }
}
