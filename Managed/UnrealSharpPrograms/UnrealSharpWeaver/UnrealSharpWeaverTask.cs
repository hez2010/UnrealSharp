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
        ICollection<AssemblyDefinition> orderedUserAssemblies = OrderInputAssembliesByReferences(assembliesToProcess);
        WeaverImporter.Instance.AllProjectAssemblies = assembliesToProcess;
        WriteUnrealSharpMetadataFile(orderedUserAssemblies, outputDirInfo);
        ICollection<string> outputFiles = ProcessOrderedAssemblies(orderedUserAssemblies, outputDirInfo);

        foreach (string file in outputFiles)
        {
            File.Copy(file, Path.Combine(OutputPath, Path.GetFileName(file)), true);
        }

        OutputFiles = outputFiles.Select(x => new Microsoft.Build.Utilities.TaskItem(x, true)).ToArray();
    }

    private static void WriteUnrealSharpMetadataFile(ICollection<AssemblyDefinition> orderedAssemblies, DirectoryInfo outputDirectory)
    {
        UnrealSharpMetadata unrealSharpMetadata = new UnrealSharpMetadata
        {
            AssemblyLoadingOrder = orderedAssemblies
                .Select(x => Path.GetFileNameWithoutExtension(x.MainModule.FileName)).ToList(),
        };

        string metaDataContent = JsonSerializer.Serialize(unrealSharpMetadata, new JsonSerializerOptions
        {
            WriteIndented = false,
        });

        string fileName = Path.Combine(outputDirectory.FullName, "UnrealSharp.assemblyloadorder.json");
        File.WriteAllText(fileName, metaDataContent);
    }

    private ICollection<string> ProcessOrderedAssemblies(ICollection<AssemblyDefinition> assemblies, DirectoryInfo outputDirectory)
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

    private static ICollection<AssemblyDefinition> OrderInputAssembliesByReferences(ICollection<AssemblyDefinition> assemblies)
    {
        HashSet<string> assemblyNames = new HashSet<string>();
        
        foreach (AssemblyDefinition assembly in assemblies)
        {
            assemblyNames.Add(assembly.FullName);
        }

        List<AssemblyDefinition> result = new List<AssemblyDefinition>(assemblies.Count);
        HashSet<AssemblyDefinition> remaining = new HashSet<AssemblyDefinition>(assemblies);

        // Add assemblies with no references first between the user assemblies.
        foreach (AssemblyDefinition assembly in assemblies)
        {
            bool hasReferenceToUserAssembly = false;
            foreach (AssemblyNameReference? reference in assembly.MainModule.AssemblyReferences)
            {
                if (!assemblyNames.Contains(reference.FullName))
                {
                    continue;
                }
                
                hasReferenceToUserAssembly = true;
                break;
            }

            if (hasReferenceToUserAssembly)
            {
                continue;
            }
            
            result.Add(assembly);
            remaining.Remove(assembly);
        }
        
        do
        {
            bool added = false;

            foreach (AssemblyDefinition assembly in assemblies)
            {
                if (!remaining.Contains(assembly))
                {
                    continue;
                }
                
                bool allResolved = true;
                foreach (AssemblyNameReference? reference in assembly.MainModule.AssemblyReferences)
                {
                    if (assemblyNames.Contains(reference.FullName))
                    {
                        bool found = false;
                        foreach (AssemblyDefinition addedAssembly in result)
                        {
                            if (addedAssembly.FullName != reference.FullName)
                            {
                                continue;
                            }
                            
                            found = true;
                            break;
                        }

                        if (found)
                        {
                            continue;
                        }
                        
                        allResolved = false;
                        break;
                    }
                }

                if (!allResolved)
                {
                    continue;
                }
                
                result.Add(assembly);
                remaining.Remove(assembly);
                added = true;
            }
            
            if (added || remaining.Count <= 0)
            {
                continue;
            }
            
            foreach (AssemblyDefinition asm in remaining)
            {
                result.Add(asm);
            }
            
            break;

        } while (remaining.Count > 0);

        return result;
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

    static void StartWeavingAssembly(AssemblyDefinition assembly, string assemblyOutputPath)
    {
        WeaverImporter.Instance.ImportCommonTypes(assembly);

        ApiMetaData assemblyMetaData = new ApiMetaData(assembly.Name.Name);
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
