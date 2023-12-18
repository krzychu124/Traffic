using Game;
using Game.Modding;
using JetBrains.Annotations;

namespace Traffic
{
    [UsedImplicitly]
    public class Mod : IMod
    {

        public void OnCreateWorld(UpdateSystem updateSystem) {
            Logger.Info(nameof(OnCreateWorld));
        }

        public void OnDispose() {
            Logger.Info(nameof(OnDispose));
        }

        public void OnLoad() {
            Logger.Info(nameof(OnLoad));
        }
    }
}
