﻿using Colossal.Serialization.Entities;
using Game;
using Game.UI;
using Traffic.Debug;
using Traffic.Helpers;
using Traffic.Tools;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Traffic.UISystems
{
    public partial class ModUISystem : UISystemBase
    {
        private InGameKeyListener _keyListener;

        public override GameMode gameMode
        {
            get { return GameMode.GameOrEditor; }
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode) {
            if ((mode == GameMode.Game || mode == GameMode.Editor) && !_keyListener)
            {
                _keyListener = new GameObject("Traffic-keyListener").AddComponent<InGameKeyListener>();
                // _keyListener.keyHitEvent += World.GetExistingSystemManaged<PriorityToolSystem>().OnKeyPressed;
                _keyListener.keyHitEvent += World.GetExistingSystemManaged<LaneConnectorToolSystem>().OnKeyPressed;
                
            }
        }

        protected override void OnGameLoaded(Context serializationContext) {
            base.OnGameLoaded(serializationContext);
#if DEBUG_GIZMO
            World.GetExistingSystemManaged<LaneConnectorDebugSystem>().RefreshGizmoDebug();
#endif
        }

        protected override void OnDestroy() {
            base.OnDestroy();
            if (_keyListener)
            {
                // _keyListener.keyHitEvent -= World.GetExistingSystemManaged<PriorityToolSystem>().OnKeyPressed;
                _keyListener.keyHitEvent -= World.GetExistingSystemManaged<LaneConnectorToolSystem>().OnKeyPressed;
                Object.Destroy(_keyListener.gameObject);
                _keyListener = null;
            }
        }
    }
}