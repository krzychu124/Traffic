using Game.Input;
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
            builder.AddImport("{ WidgetIdentifier }", "cs2/bindings");
            builder.Substitute(typeof(Entity), new RtSimpleTypeName("Entity"));
            builder.Substitute(typeof(ProxyBinding), new RtSimpleTypeName("WidgetIdentifier"));
            builder.ExportAsClass<UIBindingConstants>().DontIncludeToNamespace().WithPublicFields();
            builder.ExportAsClass<Localization.UIKeys>().DontIncludeToNamespace().WithPublicFields();
            builder.ExportAsInterface<ModUISystem.ModKeyBinds>().DontIncludeToNamespace().WithPublicFields().AutoI(false).OverrideNamespace("Traffic");;
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
