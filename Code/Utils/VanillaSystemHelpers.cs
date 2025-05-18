using System;
using System.Linq;
using System.Reflection;
using Game.Net;
using Traffic.Components.LaneConnections;
using Unity.Entities;

namespace Traffic.Utils
{
    public static class VanillaSystemHelpers
    {
        public static void ModifyLaneSystemUpdateRequirements(LaneSystem laneSystem)
        {
            // get original system's EntityQuery
            FieldInfo queryField = typeof(LaneSystem).GetField("m_OwnerQuery", BindingFlags.Instance | BindingFlags.NonPublic);
            EntityQuery originalQuery = (EntityQuery)queryField.GetValue(laneSystem);
            if (originalQuery.GetHashCode() == 0)
            {
                Logger.Warning("LaneSystem was not initialized!");
                string id = "Traffic_mod_initialization";
                Game.PSI.NotificationSystem.Push(id,
                    "Traffic Mod Initialization",
                    "Something went wrong. Please contact mod author.",
                    progressState: Colossal.PSI.Common.ProgressState.Warning,
                    onClicked: () => Game.PSI.NotificationSystem.Pop(id)
                );
                return;
            }
            EntityQueryDesc[] originalQueryDescs = originalQuery.GetEntityQueryDescs();
            // add ModifiedLaneConnections to force vanilla skip all entities with the buffer component
            ComponentType componentType = ComponentType.ReadOnly<ModifiedLaneConnections>();
            foreach (EntityQueryDesc originalQueryDesc in originalQueryDescs)
            {
                if (originalQueryDesc.None.Contains(componentType))
                {
                    continue;
                }
                originalQueryDesc.None = originalQueryDesc.None.Append(componentType).ToArray();
                MethodInfo getQueryMethod = typeof(ComponentSystemBase).GetMethod("GetEntityQuery", BindingFlags.Instance | BindingFlags.NonPublic, null, CallingConventions.Any, new Type[] { typeof(EntityQueryDesc[]) }, Array.Empty<ParameterModifier>());
                // generate EntityQuery using LaneSystem
                EntityQuery modifiedQuery = (EntityQuery)getQueryMethod.Invoke(laneSystem, new object[] { new EntityQueryDesc[] { originalQueryDesc } });
                // replace current LaneSystem query to use more restrictive
                queryField.SetValue(laneSystem, modifiedQuery);
                // add EntityQuery to LaneSystem update check
                laneSystem.RequireForUpdate(modifiedQuery);
            }
        }
    }
}
