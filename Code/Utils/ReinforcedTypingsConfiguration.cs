using System;
using Game.Tools;
using JetBrains.Annotations;
using Reinforced.Typings.Ast.TypeNames;
using Reinforced.Typings.Fluent;
using Traffic.Debug;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic.Utils
{
    [UsedImplicitly]
    public class ReinforcedTypingsConfiguration
    {
        [UsedImplicitly]
        public static void Configure(ConfigurationBuilder builder)
        {
            builder.Global(config => {
                config.UseModules(true, true);
                config.ExportPureTypings(false);
                config.RootNamespace("Traffic");
            });
            builder.AddImport("Entity", "cs2/utils");
            builder.Substitute(typeof(Entity), new RtSimpleTypeName("Entity"));
            builder.ExportAsEnum<TempFlags>();
            builder.ExportAsInterface<float3>()
                .WithPublicFields(exportBuilder => { if (exportBuilder.Member.IsStatic) { exportBuilder.Ignore(); } })
                .AutoI(false)
                .OverrideNamespace("Traffic");
            
            builder.ExportAsInterfaces(
                new[]
                {
                    typeof(NetworkDebugUISystem.DebugData),
                },
                exportBuilder => { exportBuilder.WithPublicFields().AutoI(false).OverrideNamespace("Traffic"); }
            );

        }
    }
}
