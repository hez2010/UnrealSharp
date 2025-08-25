using Microsoft.Build.Framework;
using Mono.Cecil;
using Mono.Cecil.Pdb;
using System.Text.Json;
using UnrealSharpWeaver.MetaData;
using UnrealSharpWeaver.TypeProcessors;
using UnrealSharpWeaver.Utilities;

namespace UnrealSharpWeaver;

public sealed class UnrealSharpWeaverTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public required ITaskItem[] References { get; set; }
    [Required]
    public required ITaskItem[] Assemblies { get; set; }
    [Required]
    public required string OutputPath { get; set; }
    [Output]
    public ITaskItem[]? OutputFiles { get; set; }

    private void LoadBindingsAssembly()
    {
        DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();

        HashSet<string> searchPaths = new();
        foreach (string assemblyPath in References.Select(p => p.ItemSpec))
        {
            string? directory = Path.GetDirectoryName(StripQuotes(assemblyPath));

            if (string.IsNullOrEmpty(directory) || !searchPaths.Add(directory))
            {
                continue;
            }

            if (!Directory.Exists(directory))
            {
                throw new InvalidOperationException("Could not determine directory for assembly path.");
            }

            resolver.AddSearchDirectory(directory);
        }
        
        WeaverImporter.Instance.AssemblyResolver = resolver;
    }

    private void ProcessUserAssemblies()
    {
        DirectoryInfo outputDirInfo = new DirectoryInfo(Path.Combine(StripQuotes(OutputPath), "weaver"));

        if (!outputDirInfo.Exists)
        {
            outputDirInfo.Create();
        }

        DefaultAssemblyResolver resolver = GetAssemblyResolver();
        List<AssemblyDefinition> assembliesToProcess = LoadInputAssemblies(resolver);
        WeaverImporter.Instance.AllProjectAssemblies = assembliesToProcess;
        ICollection<string> outputFiles = ProcessAssemblies(assembliesToProcess, outputDirInfo);

        foreach (string file in outputFiles)
        {
            File.Copy(file, Path.Combine(OutputPath, Path.GetFileName(file)), true);
        }

        outputDirInfo.Delete(true);

        OutputFiles = outputFiles.Select(x =>
            new Microsoft.Build.Utilities.TaskItem(Path.Combine(OutputPath, Path.GetFileName(x)))).ToArray();
    }

    private ICollection<string> ProcessAssemblies(ICollection<AssemblyDefinition> assemblies, DirectoryInfo outputDirectory)
    {
        Exception? exception = null;
        List<string> outputFiles = new List<string>(assemblies.Count);
        foreach (AssemblyDefinition assembly in assemblies)
        {
            if (assembly.Name.Name.EndsWith(".Glue"))
            {
                continue;
            }

            try
            {
                string outputPath = Path.Combine(outputDirectory.FullName, Path.GetFileName(assembly.MainModule.FileName));
                StartWeavingAssembly(assembly, outputPath);
                outputFiles.Add(outputPath);
                outputFiles.Add(Path.ChangeExtension(outputPath, "metadata.json"));
            }
            catch (Exception ex)
            {
                exception = ex;
                break;
            }
        }

        foreach (AssemblyDefinition assembly in assemblies)
        {
            assembly.Dispose();
        }

        if (exception != null)
        {
            throw new AggregateException("Assembly processing failed", exception);
        }

        return outputFiles;
    }

    private static DefaultAssemblyResolver GetAssemblyResolver()
    {
        return WeaverImporter.Instance.AssemblyResolver;
    }

    private List<AssemblyDefinition> LoadInputAssemblies(IAssemblyResolver resolver)
    {
        ReaderParameters readerParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = true,
            SymbolReaderProvider = new PdbReaderProvider(),
        };

        List<AssemblyDefinition> result = new List<AssemblyDefinition>();

        foreach (var assemblyPath in Assemblies.Select(p => StripQuotes(p.ItemSpec)))
        {
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Could not find assembly at: {assemblyPath}");
            }

            AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);
            result.Add(assembly);
        }

        return result;
    }

    private static string StripQuotes(string value)
    {
        if (value.StartsWith("\"") && value.EndsWith("\""))
        {
            return value.Substring(1, value.Length - 2);
        }

        return value;
    }

    private void StartWeavingAssembly(AssemblyDefinition assembly, string assemblyOutputPath)
    {
        WeaverImporter.Instance.ImportCommonTypes(assembly);

        ApiMetaData assemblyMetaData = new ApiMetaData(assembly.Name.Name);
        assemblyMetaData.References.AddRange(References.Select(r => r.ItemSpec));
        StartProcessingAssembly(assembly, assemblyMetaData);

        assembly.Write(assemblyOutputPath, new WriterParameters
        {
            SymbolWriterProvider = new PdbWriterProvider(),
        });
        WriteAssemblyMetaDataFile(assemblyMetaData, assemblyOutputPath);
    }

    private static void WriteAssemblyMetaDataFile(ApiMetaData metadata, string outputPath)
    {
        string metaDataContent = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = false,
        });

        string metadataFilePath = Path.ChangeExtension(outputPath, "metadata.json");
        File.WriteAllText(metadataFilePath, metaDataContent);
    }

    private static void StartProcessingAssembly(AssemblyDefinition userAssembly, ApiMetaData metadata)
    {
        try
        {
            List<TypeDefinition> classes = [];
            List<TypeDefinition> structs = [];
            List<TypeDefinition> enums = [];
            List<TypeDefinition> interfaces = [];
            List<TypeDefinition> multicastDelegates = [];
            List<TypeDefinition> delegates = [];

            try
            {
                void RegisterType(List<TypeDefinition> typeDefinitions, TypeDefinition typeDefinition)
                {
                    typeDefinitions.Add(typeDefinition);
                    typeDefinition.AddGeneratedTypeAttribute();
                }

                foreach (ModuleDefinition? module in userAssembly.Modules)
                {
                    foreach (TypeDefinition? type in module.Types)
                    {
                        if (type.IsUClass())
                        {
                            RegisterType(classes, type);
                        }
                        else if (type.IsUEnum())
                        {
                            RegisterType(enums, type);
                        }
                        else if (type.IsUStruct())
                        {
                            RegisterType(structs, type);
                        }
                        else if (type.IsUInterface())
                        {
                            RegisterType(interfaces, type);
                        }
                        else if (type.BaseType != null && type.BaseType.FullName.Contains("UnrealSharp.MulticastDelegate"))
                        {
                            RegisterType(multicastDelegates, type);
                        }
                        else if (type.BaseType != null && type.BaseType.FullName.Contains("UnrealSharp.Delegate"))
                        {
                            RegisterType(delegates, type);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error enumerating types: {ex.Message}");
                throw;
            }

            UnrealEnumProcessor.ProcessEnums(enums, metadata);
            UnrealInterfaceProcessor.ProcessInterfaces(interfaces, metadata);
            UnrealStructProcessor.ProcessStructs(structs, metadata, userAssembly);
            UnrealClassProcessor.ProcessClasses(classes, metadata);
            UnrealDelegateProcessor.ProcessDelegates(delegates, multicastDelegates, userAssembly, metadata.DelegateMetaData);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during assembly processing: {ex.Message}");
            throw;
        }
    }

    public override bool Execute()
    {
        try
        {
            LoadBindingsAssembly();
            ProcessUserAssemblies();
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError(ex.ToString());
            return false;
        }
    }
}
