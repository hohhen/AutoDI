﻿using Mono.Cecil;
using Mono.Cecil.Rocks;
using System.Linq;

namespace AutoDI.Fody
{
    public static class SetupMethod
    {
        public static MethodDefinition Find(ModuleDefinition module, ILogger logger)
        {
            foreach (var method in module.GetAllTypes().SelectMany(t => t.GetMethods())
                .Where(m => m.CustomAttributes.Any(a => a.AttributeType.IsType<SetupMethodAttribute>())))
            {
                if (!method.IsStatic)
                {
                    logger?.Warning($"Setup method '{method.FullName}' must be static");
                    return null;
                }
                if (!method.IsPublic && !method.IsAssembly)
                {
                    logger?.Warning($"Setup method '{method.FullName}' must be public or internal");
                    return null;
                }
                if (method.Parameters.Count != 1 || !method.Parameters[0].ParameterType.IsType<IApplicationBuilder>())
                {
                    logger?.Warning($"Setup method '{method.FullName}' must take a single parameter of type '{typeof(IApplicationBuilder).FullName}'");
                    return null;
                }
                return method;
            }
            return null;
        }
    }
}