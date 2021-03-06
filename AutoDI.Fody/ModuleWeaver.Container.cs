﻿
using AutoDI;
using AutoDI.Fody;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Linq;

// ReSharper disable once CheckNamespace
partial class ModuleWeaver
{
    //TODO: out parameters... yuck
    private TypeDefinition GenerateAutoDIClass(Mapping mapping, Settings settings,
        out MethodDefinition initMethod)
    {
        var containerType = new TypeDefinition(DI.Namespace, DI.TypeName,
            TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
            | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit)
        {
            BaseType = ModuleDefinition.Get<object>()
        };

        FieldDefinition globalServiceProvider =
            ModuleDefinition.CreateStaticReadonlyField(DI.GlobalServiceProviderName, false, Import.IServiceProvider);
        containerType.Fields.Add(globalServiceProvider);

        MethodDefinition configureMethod = GenerateAddServicesMethod(mapping, settings, containerType);
        containerType.Methods.Add(configureMethod);

        initMethod = GenerateInitMethod(configureMethod, globalServiceProvider);
        containerType.Methods.Add(initMethod);

        MethodDefinition disposeMethod = GenerateDisposeMethod(globalServiceProvider);
        containerType.Methods.Add(disposeMethod);

        return containerType;
    }

    private MethodDefinition GenerateAddServicesMethod(Mapping mapping, Settings settings, TypeDefinition containerType)
    {
        var method = new MethodDefinition(nameof(DI.AddServices),
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Static,
            ModuleDefinition.ImportReference(typeof(void)));

        var serviceCollection = new ParameterDefinition("collection", ParameterAttributes.None, ModuleDefinition.Get<IServiceCollection>());
        method.Parameters.Add(serviceCollection);

        ILProcessor processor = method.Body.GetILProcessor();

        VariableDefinition exceptionList = null;
        VariableDefinition exception = null;
        TypeDefinition listType = null;
        if (settings.DebugExceptions)
        {
            var genericType = ModuleDefinition.ImportReference(Import.List_Type.MakeGenericInstanceType(Import.System_Exception));
            listType = genericType.Resolve();
            exceptionList = new VariableDefinition(genericType);
            exception = new VariableDefinition(Import.System_Exception);

            method.Body.Variables.Add(exceptionList);
            method.Body.Variables.Add(exception);

            MethodReference listCtor = ModuleDefinition.ImportReference(Import.List_Type.GetConstructors().Single(c => c.IsPublic && c.Parameters.Count == 0));
            listCtor = listCtor.MakeGenericDeclaringType(Import.System_Exception);

            processor.Emit(OpCodes.Newobj, listCtor);
            processor.Emit(OpCodes.Stloc, exceptionList);
        }

        MethodDefinition funcCtor = ModuleDefinition.ResolveCoreConstructor(typeof(Func<,>));

        if (mapping != null)
        {
            int factoryIndex = 0;
            foreach (TypeMap map in mapping)
            {
                try
                {
                    Logger.Debug($"Processing map for {map.TargetType.FullName}", DebugLogLevel.Verbose);

                    MethodDefinition factoryMethod = GenerateFactoryMethod(map.TargetType, factoryIndex);
                    if (factoryMethod == null)
                    {
                        Logger.Debug($"No acceptable constructor for '{map.TargetType.FullName}', skipping map",
                            DebugLogLevel.Verbose);
                        continue;
                    }
                    containerType.Methods.Add(factoryMethod);
                    factoryIndex++;

                    foreach (TypeLifetime typeLifetime in map.Lifetimes)
                    {
                        var tryStart = Instruction.Create(OpCodes.Ldarg_0); //collection parameter
                        processor.Append(tryStart);

                        processor.Emit(OpCodes.Ldnull);
                        processor.Emit(OpCodes.Ldftn, factoryMethod);
                        processor.Emit(OpCodes.Newobj,
                            ModuleDefinition.ImportReference(
                                funcCtor.MakeGenericDeclaringType(Import.IServiceProvider,
                                    map.TargetType)));

                        processor.Emit(OpCodes.Ldc_I4, typeLifetime.Keys.Count);
                        processor.Emit(OpCodes.Newarr, Import.System_Type);

                        int arrayIndex = 0;
                        foreach (TypeDefinition key in typeLifetime.Keys)
                        {
                            TypeReference importedKey = ModuleDefinition.ImportReference(key);
                            Logger.Debug(
                                $"Mapping {importedKey.FullName} => {map.TargetType.FullName} ({typeLifetime.Lifetime})",
                                DebugLogLevel.Default);
                            processor.Emit(OpCodes.Dup);
                            processor.Emit(OpCodes.Ldc_I4, arrayIndex++);
                            processor.Emit(OpCodes.Ldtoken, importedKey);
                            processor.Emit(OpCodes.Call, Import.Type_GetTypeFromHandle);
                            processor.Emit(OpCodes.Stelem_Ref);
                        }

                        processor.Emit(OpCodes.Ldc_I4, (int)typeLifetime.Lifetime);

                        var genericAddMethod =
                            new GenericInstanceMethod(Import.ServiceCollectionMixins_AddAutoDIService);
                        genericAddMethod.GenericArguments.Add(ModuleDefinition.ImportReference(map.TargetType));
                        processor.Emit(OpCodes.Call, genericAddMethod);
                        processor.Emit(OpCodes.Pop);

                        if (settings.DebugExceptions)
                        {
                            Instruction afterCatch = Instruction.Create(OpCodes.Nop);
                            processor.Emit(OpCodes.Leave_S, afterCatch);

                            Instruction handlerStart = Instruction.Create(OpCodes.Stloc, exception);
                            processor.Append(handlerStart);
                            processor.Emit(OpCodes.Ldloc, exceptionList);
                            processor.Emit(OpCodes.Ldstr, $"Error adding type '{map.TargetType.FullName}' with key(s) '{string.Join(",", typeLifetime.Keys.Select(x => x.FullName))}'");
                            processor.Emit(OpCodes.Ldloc, exception);

                            processor.Emit(OpCodes.Newobj, Import.AutoDIException_Ctor);
                            var listAdd = ModuleDefinition.ImportReference(Import.List_Type.GetMethods().Single(m => m.Name == "Add" && m.IsPublic && m.Parameters.Count == 1));
                            listAdd = listAdd.MakeGenericDeclaringType(Import.System_Exception);

                            processor.Emit(OpCodes.Callvirt, listAdd);

                            Instruction handlerEnd = Instruction.Create(OpCodes.Leave_S, afterCatch);
                            processor.Append(handlerEnd);

                            var exceptionHandler =
                                new ExceptionHandler(ExceptionHandlerType.Catch)
                                {
                                    CatchType = Import.System_Exception,
                                    TryStart = tryStart,
                                    TryEnd = handlerStart,
                                    HandlerStart = handlerStart,
                                    HandlerEnd = afterCatch,

                                };

                            method.Body.ExceptionHandlers.Add(exceptionHandler);

                            processor.Append(afterCatch);
                        }
                    }
                }
                catch (MultipleConstructorException e)
                {
                    Logger.Error($"Failed to create map for {map}\r\n{e}");
                }
                catch (Exception e)
                {
                    Logger.Warning($"Failed to create map for {map}\r\n{e}");
                }
            }
        }

        Instruction @return = Instruction.Create(OpCodes.Ret);
        if (settings.DebugExceptions)
        {
            Instruction loadList = Instruction.Create(OpCodes.Ldloc, exceptionList);
            processor.Append(loadList);

            var listCount = ModuleDefinition.ImportReference(listType.GetMethods().Single(m => m.IsPublic && m.Name == "get_Count"));
            listCount = listCount.MakeGenericDeclaringType(Import.System_Exception);
            processor.Emit(OpCodes.Callvirt, listCount);
            processor.Emit(OpCodes.Ldc_I4_0);
            processor.Emit(OpCodes.Cgt);
            processor.Emit(OpCodes.Brfalse_S, @return);

            processor.Emit(OpCodes.Ldstr, $"Error in {DI.TypeName}.{nameof(DI.AddServices)}() generated method");
            processor.Emit(OpCodes.Ldloc, exceptionList);

            processor.Emit(OpCodes.Newobj, Import.System_AggregateException_Ctor);
            processor.Emit(OpCodes.Throw);
        }

        processor.Append(@return);

        return method;
    }

    private MethodDefinition GenerateFactoryMethod(TypeDefinition targetType, int index)
    {
        if (!targetType.CanMapType()) return null;
        
        MethodDefinition targetTypeCtor = targetType.GetMappingConstructor();
        if (targetTypeCtor == null) return null;

        var factory = new MethodDefinition($"<{targetType.Name}>_generated_{index}",
            MethodAttributes.Private | MethodAttributes.HideBySig | MethodAttributes.Static,
            ModuleDefinition.ImportReference(targetType));
        factory.Parameters.Add(new ParameterDefinition("serviceProvider", ParameterAttributes.None, Import.IServiceProvider));

        ILProcessor factoryProcessor = factory.Body.GetILProcessor();

        MethodReference getServiceMethod = ModuleDefinition.GetMethod(typeof(ServiceProviderServiceExtensions),
            nameof(ServiceProviderServiceExtensions.GetService));

        foreach (ParameterDefinition parameter in targetTypeCtor.Parameters)
        {
            factoryProcessor.Emit(OpCodes.Ldarg_0);
            var genericGetService = new GenericInstanceMethod(getServiceMethod);
            genericGetService.GenericArguments.Add(ModuleDefinition.ImportReference(parameter.ParameterType));
            factoryProcessor.Emit(OpCodes.Call, genericGetService);
        }

        factoryProcessor.Emit(OpCodes.Newobj, ModuleDefinition.ImportReference(targetTypeCtor));
        factoryProcessor.Emit(OpCodes.Ret);
        return factory;
    }

    private MethodDefinition GenerateInitMethod(MethodDefinition configureMethod, FieldDefinition globalServiceProvider)
    {
        var initMethod = new MethodDefinition(nameof(DI.Init),
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Static,
            ModuleDefinition.ImportReference(typeof(void)));
        var configureAction = new ParameterDefinition("configure", ParameterAttributes.None, ModuleDefinition.Get<Action<IApplicationBuilder>>());
        initMethod.Parameters.Add(configureAction);

        var applicationBuilder = new VariableDefinition(ModuleDefinition.Get<IApplicationBuilder>());
        initMethod.Body.Variables.Add(applicationBuilder);
        ILProcessor initProcessor = initMethod.Body.GetILProcessor();

        Instruction createApplicationbuilder = Instruction.Create(OpCodes.Newobj, ModuleDefinition.GetDefaultConstructor<ApplicationBuilder>());

        initProcessor.Emit(OpCodes.Ldsfld, globalServiceProvider);
        initProcessor.Emit(OpCodes.Brfalse_S, createApplicationbuilder);
        //Compare
        initProcessor.Emit(OpCodes.Newobj, ModuleDefinition.GetConstructor<AlreadyInitializedException>());
        initProcessor.Emit(OpCodes.Throw);

        initProcessor.Append(createApplicationbuilder);
        initProcessor.Emit(OpCodes.Stloc_0);

        initProcessor.Emit(OpCodes.Ldloc_0); //applicationBuilder
        initProcessor.Emit(OpCodes.Ldnull);
        initProcessor.Emit(OpCodes.Ldftn, configureMethod);
        initProcessor.Emit(OpCodes.Newobj, ModuleDefinition.GetConstructor<Action<IServiceCollection>>());
        initProcessor.Emit(OpCodes.Callvirt, ModuleDefinition.GetMethod<IApplicationBuilder>(nameof(IApplicationBuilder.ConfigureServices)));
        initProcessor.Emit(OpCodes.Pop);

        MethodDefinition setupMethod = SetupMethod.Find(ModuleDefinition, Logger);
        if (setupMethod != null)
        {
            Logger.Debug($"Found setup method '{setupMethod.FullName}'", DebugLogLevel.Default);
            initProcessor.Emit(OpCodes.Ldloc_0); //applicationBuilder
            initProcessor.Emit(OpCodes.Call, setupMethod);
            initProcessor.Emit(OpCodes.Nop);
        }
        else
        {
            Logger.Debug("No setup method found", DebugLogLevel.Default);
        }

        Instruction loadForBuild = Instruction.Create(OpCodes.Ldloc_0);

        initProcessor.Emit(OpCodes.Ldarg_0);
        initProcessor.Emit(OpCodes.Brfalse_S, loadForBuild);
        initProcessor.Emit(OpCodes.Ldarg_0);
        initProcessor.Emit(OpCodes.Ldloc_0);
        initProcessor.Emit(OpCodes.Callvirt, ModuleDefinition.GetMethod<Action<IApplicationBuilder>>(nameof(Action<IApplicationBuilder>.Invoke)));


        initProcessor.Append(loadForBuild);
        var buildMethod = ModuleDefinition.GetMethod<IApplicationBuilder>(nameof(IApplicationBuilder.Build));
        buildMethod.ReturnType = Import.IServiceProvider; //Must update the return type to handle .net core apps
        initProcessor.Emit(OpCodes.Callvirt, buildMethod);
        initProcessor.Emit(OpCodes.Stsfld, globalServiceProvider);

        initProcessor.Emit(OpCodes.Ldsfld, globalServiceProvider);
        initProcessor.Emit(OpCodes.Call, Import.GlobalDI_Register);


        initProcessor.Emit(OpCodes.Ret);

        return initMethod;
    }

    private MethodDefinition GenerateDisposeMethod(FieldDefinition globalServiceProvider)
    {
        var disposeMethod = new MethodDefinition(nameof(DI.Dispose),
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Static,
            ModuleDefinition.ImportReference(typeof(void)));

        VariableDefinition disposable = new VariableDefinition(ModuleDefinition.Get<IDisposable>());
        disposeMethod.Body.Variables.Add(disposable);

        ILProcessor processor = disposeMethod.Body.GetILProcessor();
        Instruction afterDispose = Instruction.Create(OpCodes.Nop);

        processor.Emit(OpCodes.Ldsfld, globalServiceProvider);
        processor.Emit(OpCodes.Isinst, ModuleDefinition.Get<IDisposable>());
        processor.Emit(OpCodes.Dup);
        processor.Emit(OpCodes.Stloc_0); //disposable
        processor.Emit(OpCodes.Brfalse_S, afterDispose);
        processor.Emit(OpCodes.Ldloc_0); //disposable
        processor.Emit(OpCodes.Callvirt, ModuleDefinition.GetMethod<IDisposable>(nameof(IDisposable.Dispose)));

        processor.Append(afterDispose);

        processor.Emit(OpCodes.Ldsfld, globalServiceProvider);
        processor.Emit(OpCodes.Call, Import.GlobalDI_Unregister);
        processor.Emit(OpCodes.Pop);

        processor.Emit(OpCodes.Ldnull);
        processor.Emit(OpCodes.Stsfld, globalServiceProvider);

        processor.Emit(OpCodes.Ret);
        return disposeMethod;
    }

    
}