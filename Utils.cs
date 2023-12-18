using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Entities;

namespace Traffic
{
    public class Utils
    {
        internal static void RegisterCustomComponents() {
            var addAllComponents = typeof(TypeManager).GetMethod("AddAllComponentTypes",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly | BindingFlags.GetField | BindingFlags.GetProperty);
            IEnumerable<Type> newComponents = GetStructForInterfaceImplementations(typeof(IComponentData), new[] { Assembly.GetExecutingAssembly() })
                .Concat(GetStructForInterfaceImplementations(typeof(IBufferElementData), new[] { Assembly.GetExecutingAssembly() }))
                .ToArray();
            int startTypeIndex = TypeManager.GetTypeCount();
            Dictionary<int, HashSet<TypeIndex>> writeGroupByType = new Dictionary<int, HashSet<TypeIndex>>();
            Dictionary<Type, int> descendantCountByType = newComponents.Select(x => (x, 0)).ToDictionary(x => x.x, x => x.Item2);
            addAllComponents.Invoke(null, new object[] { newComponents, startTypeIndex, writeGroupByType, descendantCountByType });
        }

        public static IEnumerable<Type> GetStructForInterfaceImplementations(Type interfaceType, IEnumerable<Assembly> assembly = null) {
            if (assembly == null)
            {
                throw new NotSupportedException("GetStructForInterfaceImplementations assembly == null");
            }

            IEnumerable<Type> classes = assembly.SelectMany(x => {
                    try
                    {
                        return x?.GetTypes();
                    }
                    catch
                    {
                        return Type.EmptyTypes;
                    }
                })
                .Select(t => new
                {
                    t,
                    y = t.GetInterfaces()
                })
                .Where(t1 => t1.t.IsValueType && (t1.y.Contains(interfaceType) || interfaceType.IsAssignableFrom(t1.t)))
                .Select(t1 => t1.t);

            return classes;
        }
    }
}
