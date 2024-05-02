using Game.Rendering;
using Game.Tools;
using Traffic.CommonData;
using Traffic.Components.LaneConnections;
using Traffic.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Traffic.Rendering
{
    public partial class ToolOverlaySystem
    {
#if WITH_BURST
        [BurstCompile]
#endif
        private struct ConnectorsOverlayJob : IJob
        {
            [ReadOnly] public EntityTypeHandle entityTypeHandle;
            [ReadOnly] public NativeList<ArchetypeChunk> connectorDataChunks;
            [ReadOnly] public ComponentTypeHandle<Connector> connectorType;
            [ReadOnly] public ComponentLookup<Connector> connectorData;
            [ReadOnly] public LaneConnectorToolSystem.State state;
            [ReadOnly] public LaneConnectorToolSystem.StateModifier modifier;
            [ReadOnly] public ConnectorColorSet colorSet;
            [ReadOnly] public float connectorSize;
            [ReadOnly] public NativeList<ControlPoint> controlPoints;
            public OverlayRenderSystem.Buffer overlayBuffer;

            public void Execute() {
                bool renderSource = state == LaneConnectorToolSystem.State.SelectingSourceConnector;
                bool renderTarget = state == LaneConnectorToolSystem.State.SelectingTargetConnector;
                
                Entity source = Entity.Null;
                Entity target = Entity.Null;
                Connector sourceConnector = default;
                LaneConnectorToolSystem.StateModifier modifierIgnoreUnsafe = modifier & ~LaneConnectorToolSystem.StateModifier.MakeUnsafe;
                bool isUnsafe = (modifier & LaneConnectorToolSystem.StateModifier.MakeUnsafe) != 0;
                bool forceRoad = (modifierIgnoreUnsafe & (LaneConnectorToolSystem.StateModifier.Road | LaneConnectorToolSystem.StateModifier.FullMatch)) == (LaneConnectorToolSystem.StateModifier.Road | LaneConnectorToolSystem.StateModifier.FullMatch);
                bool forceTrack = (modifierIgnoreUnsafe & (LaneConnectorToolSystem.StateModifier.Track | LaneConnectorToolSystem.StateModifier.FullMatch)) == (LaneConnectorToolSystem.StateModifier.Track | LaneConnectorToolSystem.StateModifier.FullMatch);
                if (controlPoints.Length > 0)
                {
                    source = controlPoints[0].m_OriginalEntity;
                    if (connectorData.HasComponent(source))
                    {
                        sourceConnector = connectorData[source];
                    }
                    if (controlPoints.Length > 1)
                    { 
                        target = controlPoints[1].m_OriginalEntity;
                    }
                }
                for (int i = 0; i < connectorDataChunks.Length; i++)
                {
                    ArchetypeChunk chunk = connectorDataChunks[i];
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityTypeHandle);
                    NativeArray<Connector> connectors = chunk.GetNativeArray(ref connectorType);
                    for (int j = 0; j < connectors.Length; j++)
                    {
                        Entity entity = entities[j];
                        bool isSource = entity == source;
                        bool isTarget = entity == target && renderTarget;
                        float diameter = isSource || isTarget ? connectorSize : connectorSize * 1.1f;
                        float outline = isSource || isTarget ? diameter/2f : connectorSize * 0.3f;
                        Connector connector = connectors[j];
                        if (IsNotMatchingModifier(modifier, connector))
                        {
                            continue;
                        }
                        if ((isUnsafe && (connector.vehicleGroup & ~VehicleGroup.Car) > 0) ||
                            (forceRoad && (connector.vehicleGroup & ~VehicleGroup.Car) != 0) ||
                            (forceTrack && (connector.vehicleGroup & VehicleGroup.Car) != 0))
                        {
                            continue;
                        }
                        
                        if (renderTarget)
                        {
                            if (sourceConnector.vehicleGroup == VehicleGroup.Car) {
                                if ((connector.vehicleGroup & VehicleGroup.Car) == 0)
                                {
                                    continue;
                                }
                            }
                            else if (sourceConnector.vehicleGroup > VehicleGroup.Car && 
                                (sourceConnector.vehicleGroup & connector.vehicleGroup) == 0)
                            {
                                continue;
                            }
                        }
                        
                        float3 position = connector.position;
                        if ((connector.connectorType & ConnectorType.Source) != 0 && (renderSource || isSource))
                        {
                            overlayBuffer.DrawCircle(
                                isSource 
                                    ? colorSet.outlineActiveColor : connector.connectionType == ConnectionType.SharedCarTrack 
                                        ? colorSet.outlineSourceMixedColor : connector.connectionType == ConnectionType.Track 
                                            ? colorSet.outlineSourceTrackColor : colorSet.outlineSourceColor,
                                colorSet.fillSourceColor,
                                outline,
                                0,
                                new float2(0.0f, 1f),
                                position,
                                diameter);
                        }
                        else if ((connector.connectorType & ConnectorType.Target) != 0 && renderTarget)
                        {
                            overlayBuffer.DrawCircle(
                                connector.connectionType == ConnectionType.SharedCarTrack 
                                    ? colorSet.outlineTargetMixedColor : connector.connectionType == ConnectionType.Track 
                                        ? colorSet.outlineTargetTrackColor : colorSet.outlineTargetColor,
                                colorSet.fillTargetColor,
                                outline,
                                0,
                                new float2(0.0f, 1f),
                                position,
                                diameter);
                        } 
                        //TODO FIX SUPPORT BI-DIRECTIONAL
                        // else if ((connector.connectorType & ConnectorType.TwoWay) != 0)
                        // {
                        //     overlayBuffer.DrawCircle(
                        //         colorSet.outlineTwoWayColor,
                        //         colorSet.fillTwoWayColor,
                        //         outline,
                        //         0,
                        //         new float2(0.0f, 1f),
                        //         position,
                        //         diameter);
                        // }
                    }
                }  
            }

            private bool IsNotMatchingModifier(LaneConnectorToolSystem.StateModifier stateModifier, Connector connector) {
                return stateModifier == LaneConnectorToolSystem.StateModifier.Track && (connector.connectionType & (ConnectionType.Track)) == 0 ||
                    stateModifier == LaneConnectorToolSystem.StateModifier.Road && (connector.connectionType & (ConnectionType.Road)) == 0 ||
                    stateModifier == (LaneConnectorToolSystem.StateModifier.Road | LaneConnectorToolSystem.StateModifier.FullMatch)  && (connector.connectionType & (ConnectionType.Road)) != ConnectionType.Road ||
                    stateModifier == (LaneConnectorToolSystem.StateModifier.Track | LaneConnectorToolSystem.StateModifier.FullMatch)  && (connector.connectionType & (ConnectionType.Track)) != ConnectionType.Track ||
                    stateModifier == (LaneConnectorToolSystem.StateModifier.AnyConnector | LaneConnectorToolSystem.StateModifier.FullMatch) && (connector.connectionType & ConnectionType.SharedCarTrack) != ConnectionType.SharedCarTrack;
            }
        }
    }
}
