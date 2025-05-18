using System;
using System.Linq;
using System.Reflection;
using Traffic.Components.LaneConnections;
using Unity.Entities;

namespace Traffic.Utils
{
    public static class ModsCompatibilityHelpers
    {
        public static void ModifyTLELaneSystemUpdateRequirements(ComponentSystemBase laneSystem)
        {
            // get original system's EntityQuery
            FieldInfo queryField = laneSystem.GetType().GetField("m_OwnerQuery", BindingFlags.Instance | BindingFlags.NonPublic);
            if (queryField == null)
            {
                Logger.Error("Could not find Traffic Light Enhancements m_OwnerQuery for compatibility patching");
                return;
            }
            EntityQuery originalQuery = (EntityQuery)queryField.GetValue(laneSystem);
            EntityQueryDesc[] originalQueryDescs = originalQuery.GetEntityQueryDescs();
            ComponentType componentType = ComponentType.ReadOnly<ModifiedLaneConnections>();
            foreach (EntityQueryDesc originalQueryDesc in originalQueryDescs)
            {
                if (originalQueryDesc.None.Contains(componentType))
                {
                    continue;
                }

                // add ModifiedLaneConnections to force vanilla skip all entities with the buffer component
                originalQueryDesc.None = originalQueryDesc.None.Append(ComponentType.ReadOnly<ModifiedLaneConnections>()).ToArray();

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
