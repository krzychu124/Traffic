using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.UI;
using Traffic.Common;
using Traffic.Debug;
using Traffic.Helpers;
using Traffic.Tools;
using Unity.Entities;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Traffic.UISystems
{
    public partial class ModUISystem : UISystemBase
    {
        private InGameKeyListener _keyListener;
        private SelectedIntersectionData _selectedIntersectionData;
        private EntityQuery _actionOverlayQuery;

        public override GameMode gameMode
        {
            get { return GameMode.GameOrEditor; }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            _actionOverlayQuery = GetEntityQuery(ComponentType.ReadOnly<ActionOverlayData>(), ComponentType.Exclude<Deleted>());
            _selectedIntersectionData = new SelectedIntersectionData();
            AddUpdateBinding(new GetterValueBinding<SelectedIntersectionData>(Mod.MOD_NAME, UIBindingConstants.SELECTED_INTERSECTION, () => SelectedIntersection));
            AddBinding(new TriggerBinding<ActionOverlayPreview>(Mod.MOD_NAME, UIBindingConstants.SET_ACTION_OVERLAY_PREVIEW, SetActionOverlayPreviewState, new EnumReader<ActionOverlayPreview>()));
            AddBinding(new TriggerBinding(Mod.MOD_NAME, UIBindingConstants.APPLY_TOOL_ACTION_PREVIEW, ApplyActionOverlayPreview));
            AddBinding(new TriggerBinding<bool>(Mod.MOD_NAME, UIBindingConstants.TOGGLE_TOOL, ToggleTool));
            EntityManager.CreateSingleton<ActionOverlayData>();
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

        public void ApplyActionOverlayPreview()
        {
            LaneConnectorToolSystem laneConnectorToolSystem = World.GetExistingSystemManaged<LaneConnectorToolSystem>();
            if (laneConnectorToolSystem.Enabled)
            {
                laneConnectorToolSystem.ToolMode = LaneConnectorToolSystem.Mode.ApplyPreviewModifications;
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
            LaneConnectorToolSystem laneConnectorToolSystem = World.GetExistingSystemManaged<LaneConnectorToolSystem>();
            laneConnectorToolSystem.ToggleTool(enable);
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            if ((mode == GameMode.Game || mode == GameMode.Editor) && !_keyListener)
            {
                _keyListener = new GameObject("Traffic-keyListener").AddComponent<InGameKeyListener>();
                // _keyListener.keyHitEvent += World.GetExistingSystemManaged<PriorityToolSystem>().OnKeyPressed;
                _keyListener.keyHitEvent += World.GetExistingSystemManaged<LaneConnectorToolSystem>().OnKeyPressed;
                
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
            if (_keyListener)
            {
                // _keyListener.keyHitEvent -= World.GetExistingSystemManaged<PriorityToolSystem>().OnKeyPressed;
                _keyListener.keyHitEvent -= World.GetExistingSystemManaged<LaneConnectorToolSystem>().OnKeyPressed;
                Object.Destroy(_keyListener.gameObject);
                _keyListener = null;
            }
            // Cleanup singleton data
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
                writer.TypeBegin(nameof(SelectedIntersectionData));
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
    }
}
