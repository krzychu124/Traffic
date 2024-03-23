using System;
using System.Linq;
using System.Reflection;
using Game.Net;
using Traffic.LaneConnections;
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
            EntityQueryDesc originalQueryDesc = originalQuery.GetEntityQueryDesc();
            // add ModifiedLaneConnections to force vanilla skip all entities with the buffer component
            originalQueryDesc.None = originalQueryDesc.None.Append(ComponentType.ReadOnly<ModifiedLaneConnections>()).ToArray();
            
            MethodInfo getQueryMethod = typeof(ComponentSystemBase).GetMethod("GetEntityQuery", BindingFlags.Instance | BindingFlags.NonPublic, null, CallingConventions.Any, new Type[]{typeof(EntityQueryDesc[])}, Array.Empty<ParameterModifier>());
            // generate EntityQuery using LaneSystem
            EntityQuery modifiedQuery = (EntityQuery)getQueryMethod.Invoke(laneSystem, new object[] { new EntityQueryDesc[] {originalQueryDesc} });
            // replace current LaneSystem query to use more restrictive
            queryField.SetValue(laneSystem, modifiedQuery);
            // add EntityQuery to LaneSystem update check
            laneSystem.RequireForUpdate(modifiedQuery);
        }
    }
}
