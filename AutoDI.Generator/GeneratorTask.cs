﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AutoDI.Fody;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using ILogger = AutoDI.Fody.ILogger;

namespace AutoDI.Generator
{
    public class GeneratorTask : Task, ICancelableTask
    {
        [Required]
        public string ProjectPath { get; set; }

        [Required]
        public string OutputPath { get; set; }

        [Required]
        public string GeneratedFilePath { get; set; }

        private ITaskItem[] _generatedCodeFiles;
        /// <summary>Gets or sets the list of generated managed code files.</summary>
        /// <returns>The list of generated managed code files.</returns>
        [Output]
        public ITaskItem[] GeneratedCodeFiles
        {
            get => _generatedCodeFiles ?? new ITaskItem[0];
            set => _generatedCodeFiles = value;
        }

        public override bool Execute()
        {
            
            XElement configElement = GetConfigElement(ProjectPath);
            var settings = Settings.Parse(new Settings(), configElement);
            var logger = new TaskLogger(BuildEngine, settings.DebugLogLevel);
            if (settings.GenerateRegistrations)
            {
                var assemblyResolver = new DefaultAssemblyResolver();
                assemblyResolver.AddSearchDirectory(Path.GetDirectoryName(OutputPath));

                var compiledAssembly = AssemblyDefinition.ReadAssembly(OutputPath);
                var typeResolver = new TypeResolver(compiledAssembly.MainModule, assemblyResolver, logger);
                ICollection<TypeDefinition> allTypes =
                    typeResolver.GetAllTypes(settings, out AssemblyDefinition _);
                Mapping mapping = Mapping.GetMapping(settings, allTypes, logger);

                Directory.CreateDirectory(Path.GetDirectoryName(GeneratedFilePath));
                using (var file = File.Open(GeneratedFilePath, FileMode.Create))
                {
                    WriteClass(mapping, settings, SetupMethod.Find(compiledAssembly.MainModule, logger), file);
                    GeneratedCodeFiles = new ITaskItem[] { new TaskItem(GeneratedFilePath) };
                }
            }
            return true;
        }

        private static XElement GetConfigElement(string projectPath)
        {
            var projectDir = Path.GetDirectoryName(projectPath);
            if (projectDir == null) return null;
            var configFile = Path.Combine(projectDir, "FodyWeavers.xml");
            if (File.Exists(configFile))
            {
                var xElement = XElement.Load(configFile);
                return xElement.Elements("AutoDI").FirstOrDefault();
            }
            return null;
        }

        private static void WriteClass(Mapping mapping, Settings settings, MethodDefinition setupMethod, Stream output)
        {
            const string @namespace = "AutoDI.Generated";
            const string className = "AutoDI";
            using (var sw = new StreamWriter(output))
            {
                sw.WriteLine(0, "using System;");
                sw.WriteLine(0, "using AutoDI;");
                sw.WriteLine(0, "using Microsoft.Extensions.DependencyInjection;");
                sw.WriteLine(0, $"namespace {@namespace}");
                sw.WriteLine(0, "{");
                sw.WriteLine(1, $"public static partial class {className}");
                sw.WriteLine(1, "{");

                int index = 0;
                foreach (TypeMap typeMap in mapping)
                {
                    if (!typeMap.TargetType.CanMapType()) continue;
                    MethodDefinition ctor = typeMap.TargetType.GetMappingConstructor();
                    if (ctor == null) continue;

                    sw.WriteLine(2, $"private static global::{typeMap.TargetType.FullNameCSharp()} generated_{index++}(IServiceProvider serviceProvider)");
                    sw.WriteLine(2, "{");
                    sw.Write(3, $"return new global::{typeMap.TargetType.FullNameCSharp()}(");

                    sw.Write(string.Join(", ", ctor.Parameters.Select(p => $"serviceProvider.GetService<global::{p.ParameterType.FullNameCSharp()}>()")));

                    sw.WriteLine(");");
                    sw.WriteLine(2, "}");
                }

                sw.WriteLine(2, "private static void AddServices(IServiceCollection collection)");
                sw.WriteLine(2, "{");
                if (settings.DebugExceptions)
                {
                    sw.WriteLine(3, "List<Exception> list = new List<Exception>();");
                }
                index = 0;
                foreach (TypeMap typeMap in mapping)
                {
                    if (!typeMap.TargetType.CanMapType() || typeMap.TargetType.GetMappingConstructor() == null) continue;
                    
                    foreach (TypeLifetime lifetime in typeMap.Lifetimes)
                    {
                        int indent = 3;
                        if (settings.DebugExceptions)
                        {
                            sw.WriteLine(indent, "try");
                            sw.WriteLine(indent++, "{");
                        }

                        sw.WriteLine(indent, $"collection.AddAutoDIService<global::{typeMap.TargetType.FullNameCSharp()}>(generated_{index}, new System.Type[{lifetime.Keys.Count}]");
                        sw.WriteLine(indent, "{");
                        sw.WriteLine(indent + 1, string.Join(", ", lifetime.Keys.Select(t => $"typeof(global::{t.FullNameCSharp()})")));
                        sw.WriteLine(indent, $"}}, Lifetime.{lifetime.Lifetime});");

                        if (settings.DebugExceptions)
                        {
                            sw.WriteLine(--indent, "}");
                            sw.WriteLine(indent, "catch(Exception innerException)");
                            sw.WriteLine(indent, "{");
                            sw.WriteLine(indent + 1, $"list.Add(new AutoDIException(\"Error adding type '{typeMap.TargetType.FullNameCSharp()}' with key(s) '{string.Join(",", lifetime.Keys.Select(x => x.FullName))}'\", innerException));");
                            sw.WriteLine(indent, "}");
                        }
                    }
                    
                    index++;
                }

                if (settings.DebugExceptions)
                {
                    sw.WriteLine(3, "if (list.Count > 0)");
                    sw.WriteLine(3, "{");
                    sw.WriteLine(4, $"throw new AggregateException(\"Error in {@namespace}.{className}.AddServices() generated method\", list);");
                    sw.WriteLine(3, "}");
                }
                sw.WriteLine(2, "}");

                sw.WriteLine(2, "static partial void DoInit(Action<IApplicationBuilder> configure)");
                sw.WriteLine(2, "{");
                sw.WriteLine(3, "if (_globalServiceProvider != null)");
                sw.WriteLine(3, "{");
                sw.WriteLine(4, "throw new AlreadyInitializedException();");
                sw.WriteLine(3, "}");
                sw.WriteLine(3, "IApplicationBuilder applicationBuilder = new ApplicationBuilder();");
                sw.WriteLine(3, "applicationBuilder.ConfigureServices(AddServices);");
                if (setupMethod != null)
                {
                    sw.WriteLine(3, $"global::{setupMethod.DeclaringType.FullNameCSharp()}.{setupMethod.Name}(applicationBuilder);");
                }
                sw.WriteLine(3, "if (configure != null)");
                sw.WriteLine(3, "{");
                sw.WriteLine(4, "configure(applicationBuilder);");
                sw.WriteLine(3, "}");
                sw.WriteLine(3, "_globalServiceProvider = applicationBuilder.Build();");
                sw.WriteLine(3, "GlobalDI.Register(_globalServiceProvider);");
                sw.WriteLine(2, "}");

                sw.WriteLine(2, "static partial void DoDispose()");
                sw.WriteLine(2, "{");
                sw.WriteLine(3, "IDisposable disposable;");
                sw.WriteLine(3, "if ((disposable = (_globalServiceProvider as IDisposable)) != null)");
                sw.WriteLine(3, "{");
                sw.WriteLine(4, "disposable.Dispose();");
                sw.WriteLine(3, "}");
                sw.WriteLine(3, "GlobalDI.Unregister(_globalServiceProvider);");
                sw.WriteLine(3, "_globalServiceProvider = null;");
                sw.WriteLine(2, "}");


                sw.WriteLine(2, "private static IServiceProvider _globalServiceProvider;");

                sw.WriteLine(1, "}");
                sw.WriteLine(0, "}");
            }
        }

        public void Cancel()
        {

        }

        private class TaskLogger : ILogger
        {
            private const string Sender = "AutoDI";
            private readonly IBuildEngine _BuildEngine;
            private readonly DebugLogLevel _DebugLogLevel;

            public TaskLogger(IBuildEngine buildEngine, DebugLogLevel debugLogLevel)
            {
                _BuildEngine = buildEngine;
                _DebugLogLevel = debugLogLevel;
            }

            public void Debug(string message, DebugLogLevel debugLevel)
            {
                if (debugLevel <= _DebugLogLevel)
                {
                    _BuildEngine.LogMessageEvent(new BuildMessageEventArgs(message, "", "AutoDI", MessageImportance.Normal));
                }
            }

            public void Info(string message)
            {
                _BuildEngine.LogMessageEvent(new BuildMessageEventArgs(message, "", "AutoDI", MessageImportance.High));
            }

            public void Warning(string message)
            {
                _BuildEngine.LogWarningEvent(new BuildWarningEventArgs("", "", null, 0, 0, 0, 0, message, "", Sender));
            }

            public void Error(string message)
            {
                _BuildEngine.LogErrorEvent(new BuildErrorEventArgs("", "", null, 0, 0, 0, 0, message, "", Sender));
            }
        }
    }
}
