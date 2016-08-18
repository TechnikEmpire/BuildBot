using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using BuildBotCore;
using System.Runtime.Versioning;
using System.Linq;

namespace BuildBot
{

    public class Program
    {
        private static List<MemoryStream> GeneratedAssemblies = new List<MemoryStream>();

        public static void Main(string[] args)
        {
            foreach(var arg in args)
            {
                Console.WriteLine(arg);
            }

            /*
            var variables = Environment.GetEnvironmentVariables();

            Console.WriteLine(string.Format("Num Variables: {0}", variables.Count));

            foreach(var varName in variables.Keys)
            {
                Console.WriteLine(string.Format("Key: {0}\nValue:{1}", varName, variables[varName]));
            }
            */
            
            var currentDirectory = Directory.GetCurrentDirectory();

            var desktopDirectory = @"C:\Users\Furinax\Desktop";

            if(Directory.Exists(desktopDirectory + "/.buildbot"))
            {
                var buildFiles = Directory.GetFiles(desktopDirectory + Path.DirectorySeparatorChar + ".buildbot", "*.cs");

                foreach(var buildFilePath in buildFiles)
                {
                    Console.WriteLine(buildFilePath);

                    var compiledBuildTasks = LoadTaskFromScript(buildFilePath);

                    foreach(var buildTask in compiledBuildTasks)
                    {
                        Console.WriteLine(buildTask.Help);
                    }                    
                }
            }

            Cleanup();
        }

        private static void Cleanup()
        {
            foreach(var ms in GeneratedAssemblies)
            {
                ms.Dispose();
            }
        }

        /// <summary>
        /// This method attempts to load the build script at the given location,
        /// compile return the exported AbstractBuildTask from it. The script
        /// file must inherit from AbstractBuildTask.
        /// </summary>
        /// <param name="taskScriptPath">
        /// The path to the build script to run.
        /// </param>
        /// <returns>
        /// In the event of successful discovery, read and compilation of the
        /// script at the given path, a list of all exported AbstractBuildTasks
        /// from the compiled assembly is returned.
        /// </returns>
        private static List<AbstractBuildTask> LoadTaskFromScript(string taskScriptPath)
        {
            // Exhaustively verify that the file exists, is readable and is not
            // an empty file.
            Debug.Assert(!string.IsNullOrEmpty(taskScriptPath) && !string.IsNullOrWhiteSpace(taskScriptPath), "Build task script path is null, empty or whitespace.");

            if(string.IsNullOrEmpty(taskScriptPath) || string.IsNullOrWhiteSpace(taskScriptPath))
            {
                throw new ArgumentException( "Build task script path is null, empty or whitespace.", nameof(taskScriptPath));
            }

            Debug.Assert(File.Exists(taskScriptPath), "Build task script path points to non-existent file.");

            if(!File.Exists(taskScriptPath))
            {
                throw new ArgumentException("Build task script path points to non-existent file.");
            }

            Console.WriteLine(string.Format("Reading all text from file {0}", taskScriptPath));

            var scriptContents = File.ReadAllText(taskScriptPath);

            Debug.Assert(!string.IsNullOrEmpty(scriptContents) && !string.IsNullOrWhiteSpace(scriptContents), "Build task script contents are null, empty or whitespace.");

            if(string.IsNullOrEmpty(scriptContents) || string.IsNullOrWhiteSpace(scriptContents))
            {
                throw new ArgumentException( "Build task script contents are null, empty or whitespace.", nameof(taskScriptPath));
            }

            // Setup syntax parse options for C#.
            CSharpParseOptions parseOptions = CSharpParseOptions.Default;
            parseOptions = parseOptions.WithLanguageVersion(LanguageVersion.CSharp6);
            parseOptions = parseOptions.WithDocumentationMode(DocumentationMode.None);
            parseOptions = parseOptions.WithKind(SourceCodeKind.Regular);
            
            // Parse text into syntax tree.
            SyntaxTree jobSyntaxTree = CSharpSyntaxTree.ParseText(scriptContents, parseOptions);

            // Generate a random file name for the assembly we're about to produce.
            string generatedAssemblyName = Path.GetRandomFileName();
            
            // Get the directory of a core assembly. We need this directory to
            // build out our platform specific reference to mscorlib. mscorlib
            // and the private mscorlib must be supplied as references for
            // compilation to succeed. Of these two assemblies, only the private
            // mscorlib is discovered via enumerataing assemblies referenced by
            // this executing binary.
            var dd = typeof(Enumerable).GetTypeInfo().Assembly.Location;
            var coreDir = Directory.GetParent(dd);            
            
            List<MetadataReference> references = new List<MetadataReference>
            {   
                // Here we get the path to the mscorlib and private mscorlib
                // libraries that are required for compilation to succeed.
                MetadataReference.CreateFromFile(coreDir.FullName + Path.DirectorySeparatorChar + "mscorlib.dll"),
                MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location)           
            };

            // Enumerate all assemblies referenced by this executing assembly
            // and provide them as references to the build script we're about to
            // compile.
            var referencedAssemblies = Assembly.GetEntryAssembly().GetReferencedAssemblies();
            foreach(var referencedAssembly in referencedAssemblies)
            {
                var loadedAssembly = Assembly.Load(referencedAssembly);   

                references.Add(MetadataReference.CreateFromFile(loadedAssembly.Location)); 
                
                /* For debugging, to try to list explicit platform compat. Flaky.
                try
                {
                    var customAttributes = loadedAssembly.GetCustomAttributes();
                    var attribute = customAttributes.OfType<TargetFrameworkAttribute>().First();

                    Console.WriteLine(attribute.FrameworkName);
                    Console.WriteLine(attribute.FrameworkDisplayName);
                }
                catch{}
                */                
            }
            
            // Initialize compilation arguments for the build script we're about
            // to compile.
            var op = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            op = op.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
            op = op.WithGeneralDiagnosticOption(ReportDiagnostic.Warn);            
            
            // Initialize the compilation with our options, references and the
            // already parsed syntax tree of the build script.
            CSharpCompilation compilation = CSharpCompilation.Create(
                generatedAssemblyName,
                syntaxTrees: new[] { jobSyntaxTree },
                references: references,
                options: op);           
            
            // Compile and emit new assembly into memory.
            var ms = new MemoryStream();
            EmitResult result = compilation.Emit(ms);

            if(result.Success)
            {
                // Store the in-memory assembly until this program exits.
                GeneratedAssemblies.Add(ms);

                // Get an Assembly structure from the data in memory.
                ms.Seek(0, SeekOrigin.Begin);                    
                AssemblyLoadContext loadCtx = AssemblyLoadContext.Default;                
                Assembly assembly = loadCtx.LoadFromStream(ms);

                // Enumerate types exported from the assembly. Presently not used.
                var exportedTypes = assembly.ExportedTypes;                
                foreach(var xp in exportedTypes)
                {
                    Console.WriteLine(string.Format("Build script exports type: {0}" ,xp.Name));
                }

                // Filter exported types so we only pull types extending from AbstractBuildTask.                
                var filteredExports = exportedTypes.Where(x => x.Name != typeof(AbstractBuildTask).Name);                
                Console.WriteLine(string.Format("Number of exported build objects: {0}", filteredExports.Count()));

                // Ensure that we have at least one exported build task.
                Debug.Assert(filteredExports.Count() > 0, "Script either does not export any AbstractBuildTask objects. Build scripts should export one or more AbstractBuildTask objects.");
                if(filteredExports.Count() <= 0)
                {
                    throw new ArgumentException("Script either does not export any AbstractBuildTask objects. Build scripts should export one or more AbstractBuildTask objects.", nameof(taskScriptPath));
                }

                var filteredExportsList = filteredExports.ToList();
                List<AbstractBuildTask> buildTasks = new List<AbstractBuildTask>();

                foreach(var entry in filteredExportsList)
                {
                    AbstractBuildTask typedExport = (AbstractBuildTask)Activator.CreateInstance(entry);
                    buildTasks.Add(typedExport);
                }

                return buildTasks;
            }
            else
            {
                ms.Dispose();

                Console.WriteLine("Failed");

                IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic => 
                        diagnostic.IsWarningAsError || 
                        diagnostic.Severity == DiagnosticSeverity.Error);

                foreach (Diagnostic diagnostic in failures)
                {
                    Console.Error.WriteLine("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                }
            }

            return null;
        }
    }
}
