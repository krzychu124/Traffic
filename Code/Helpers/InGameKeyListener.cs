using System;
using System.Collections.Generic;
using UnityEngine;

namespace Traffic.Helpers
{
    public class InGameKeyListener : MonoBehaviour
    {
        private readonly float _clickInterval = 0.3f;
        private bool _hit;
        private float _lastClicked;
        private KeyCode _code;
        public HashSet<KeyCode> _codes;

        public event Action<EventModifiers, KeyCode> keyHitEvent = delegate { };

        public void Awake() {
            _codes = new HashSet<KeyCode>
                { KeyCode.T, KeyCode.R };
            Logger.Info("InGameKeyListener awaken");
        }

        public void OnGUI() {
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.control && _codes.Contains(Event.current.keyCode) && Time.time - _lastClicked > _clickInterval)
                {
                    _code = Event.current.keyCode;
                    _lastClicked = Time.time;
                    _hit = true;
                }
            }
        }

        public void Update() {
            if (_hit)
            {
                _hit = false;
                keyHitEvent(EventModifiers.Control, _code);
            }
        }

        public void OnDestroy() {
            keyHitEvent = null;
        }
    }
}
