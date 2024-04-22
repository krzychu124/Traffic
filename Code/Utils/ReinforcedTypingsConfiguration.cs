using System;
using Game.Tools;
using JetBrains.Annotations;
using Reinforced.Typings.Ast.TypeNames;
using Reinforced.Typings.Fluent;
using Traffic.CommonData;
using Traffic.Debug;
using Traffic.UISystems;
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
            builder.AddImport("{ Entity }", "cs2/utils");
            builder.Substitute(typeof(Entity), new RtSimpleTypeName("Entity"));
            builder.ExportAsClass<UIBindingConstants>().DontIncludeToNamespace().WithPublicFields();
            builder.ExportAsEnum<TempFlags>();
            builder.ExportAsEnum<ModUISystem.ActionOverlayPreview>();
            builder.ExportAsInterface<float3>()
                .WithPublicFields(exportBuilder => { if (exportBuilder.Member.IsStatic) { exportBuilder.Ignore(); } })
                .AutoI(false)
                .OverrideNamespace("Traffic");
            
            builder.ExportAsInterfaces(
                new[] { typeof(NetworkDebugUISystem.DebugData), },
                exportBuilder => { exportBuilder.WithPublicFields().AutoI(false).OverrideNamespace("Traffic"); }
            );
            builder.ExportAsInterfaces(
                new[] { typeof(ModUISystem.SelectedIntersectionData), },
                exportBuilder => { exportBuilder.WithPublicFields().AutoI(false).OverrideNamespace("Traffic"); }
            );
        }
    }
}
