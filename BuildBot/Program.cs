/// The MIT License (MIT) Copyright (c) 2016 Jesse Nicholson 
/// 
/// Permission is hereby granted, free of charge, to any person obtaining a 
/// copy of this software and associated documentation files (the 
/// "Software"), to deal in the Software without restriction, including 
/// without limitation the rights to use, copy, modify, merge, publish, 
/// distribute, sublicense, and/or sell copies of the Software, and to 
/// permit persons to whom the Software is furnished to do so, subject to 
/// the following conditions: 
/// 
/// The above copyright notice and this permission notice shall be included 
/// in all copies or substantial portions of the Software. 
/// 
/// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
/// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF 
/// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
/// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY 
/// CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, 
/// TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
/// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE. 

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
using CommandLine;
using System.Text.RegularExpressions;
using BuildBot.Extensions;

namespace BuildBot
{

    /// <summary>
    /// Defines exit codes for the application. 
    /// </summary>
    public enum ExitCodes : int
    {
        /// <summary>
        /// No errors. Build scripts were found, and all were compiled and
        /// executed without issue.
        /// </summary>
        Success = 0,

        /// <summary>
        /// Invald arguments were supplied to BuildBot.
        /// </summary>
        InvalidArguments,

        /// <summary>
        /// Could not locate any build scripts.
        /// </summary>
        NoBuildScriptsFound,

        /// <summary>
        /// An error occurred when attempting to compile a build script.
        /// </summary>
        ScriptCompilationFailure,

        /// <summary>
        /// An error occurred when attempting to execute a build script.
        /// </summary>
        ScriptExecutionError,

        /// <summary>
        /// The supplied project directory does not exist.
        /// </summary>
        ProjectDirectoryDoesNotExist
    }

    /// <summary>
    /// Defines optional and required command line arguments for BuildBot.
    /// </summary>
    public class BuildBotOptions
    {

        [Option('C', "Config", Separator = ',', Required = true, SetName="Build",
        HelpText = "The build configuration. Valid values are \"Debug\" and \"Release\". Multiple values can be supplied in a list, separated by the ',' character.")]
        public IList<BuildConfiguration> Configuration
        {
            get;
            set;
        }

        [Option('A', "Arch", Separator = ',', Required = true, SetName="Build", 
        HelpText = "The target architecture. Valid values are \"x86\" and \"x64\". Multiple values can be supplied in a list, separated by the ',' character.")]
        public IList<Architecture> Arch
        {
            get;
            set;
        }

        [Option('D', "ProjectDir", Required = true,
        HelpText = "The project directory to initiate building from. This is the base directory that will be recursively scanned for .buildbot script directories.")]
        public string ProjectDirectory
        {
            get;
            set;
        }

        [Option('X', "CleanAll", Required = true, SetName="Clean",
        HelpText = "Perform the Clean operation on all of the project's build task. Be warned: clean operations delete things.")]        
        public bool CleanAll
        {
            get;
            set;
        }
    }

    /// <summary>
    /// BuildBot main program class.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Stores dynamically compiled, in-memory assemblies generated from
        /// successful build script compilation.
        /// </summary>
        private static List<MemoryStream> GeneratedAssemblies = new List<MemoryStream>();

        /// <summary>
        /// Main.
        /// </summary>
        /// <param name="args">
        /// Supplied BuildBot arguments.
        /// </param>
        public static void Main(string[] args)
        {
            // Attempt to build out options. If this fails, just return. By
            // using the default parser, help/usage will automatically get
            // printed to the console.

            BuildBotOptions options = null;

            var parser = CommandLine.Parser.Default;
            var result = parser.ParseArguments<BuildBotOptions>(args).WithParsed(
               (opts =>
               {
                   options = opts;
               })
            );

            // Full project dir. To be expanded out of options.
            string fullProjectDirectory = string.Empty;

            if (options != null)
            {
                WriteTitleToConsole("Specified Build Settings");

                foreach (var entry in options.Configuration)
                {
                    Console.WriteLine(string.Format("Requested build configuration: {0}", entry));
                }

                foreach (var entry in options.Arch)
                {
                    Console.WriteLine(string.Format("Requested target arch: {0}", entry));
                }

                // Sanitize input project dir
                options.ProjectDirectory = options.ProjectDirectory.ConvertToHostOsPath();

                // Build correct base project directory.
                var fullProjectPath = Path.GetFullPath(options.ProjectDirectory).ConvertToHostOsPath();

                if (!fullProjectPath.Equals(options.ProjectDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Unrooted project path supplied.");
                    fullProjectDirectory = (Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + options.ProjectDirectory);
                }
                else
                {
                    Console.WriteLine("Rooted project path supplied.");
                    fullProjectDirectory = options.ProjectDirectory.ConvertToHostOsPath();
                }

                // Ensure path is full/abs.
                fullProjectDirectory = Path.GetFullPath(options.ProjectDirectory).ConvertToHostOsPath();

                Console.WriteLine(string.Format("Base project directory: {0}", fullProjectDirectory));
            }
            else
            {
                // Failed to parse options.
                CleanExit(ExitCodes.InvalidArguments);
            }

            if (Directory.Exists(fullProjectDirectory))
            {
                // Find all build bot directories, recursively.
                var buildBotDirs = Directory.GetDirectories(fullProjectDirectory, ".buildbot", SearchOption.AllDirectories);

                var pendingTasks = new List<AbstractBuildTask>();
                //var completedTasks = new HashSet<string>();

                foreach (var buildDirPath in buildBotDirs)
                {
                    Console.WriteLine(buildDirPath);
                    foreach (var buildScriptPath in Directory.GetFiles(buildDirPath, "*.cs", SearchOption.AllDirectories))
                    {
                        Console.WriteLine(buildScriptPath);

                        var compiledBuildTasks = LoadTaskFromScript(buildScriptPath);

                        if (compiledBuildTasks == null || compiledBuildTasks.Count <= 0)
                        {
                            // We get a null collection when nothing succeeded.
                            CleanExit(ExitCodes.ScriptCompilationFailure);
                        }

                        foreach (var buildTask in compiledBuildTasks)
                        {
                            pendingTasks.Add(buildTask);
                        }
                    }
                }

                if(pendingTasks.Count <= 0)
                {
                    CleanExit(ExitCodes.NoBuildScriptsFound);
                }                

                WriteTitleToConsole("Resolving Task Dependency Order");

                // Sort by lease dependencies to greatest.
                //pendingTasks.Sort((x, y) => x.TaskDependencies.Count.CompareTo(y.TaskDependencies.Count));

                pendingTasks.Sort(delegate (AbstractBuildTask x, AbstractBuildTask y)
                {
                    if (x.TaskDependencies.Contains(y.GUID))
                    {
                        Console.WriteLine(string.Format("Task {0} depends on task {1}.", x.TaskFriendlyName, y.TaskFriendlyName));
                        return 1;
                    }
                    else if (y.TaskDependencies.Contains(x.GUID))
                    {
                        Console.WriteLine(string.Format("Task {0} depends on task {1}.", y.TaskFriendlyName, x.TaskFriendlyName));
                        return -1;
                    }

                    return 0;
                });

                if(options.CleanAll)
                {
                    // First try to clean all.
                    foreach (var buildTask in pendingTasks)
                    {
                        try
                        {
                            WriteTitleToConsole(string.Format("Running Build Task Clean: {0}", buildTask.TaskFriendlyName));

                            if (!buildTask.Clean())
                            {
                                Console.WriteLine("Failed to execute build task clean process.");

                                foreach (var err in buildTask.Errors)
                                {
                                    Console.WriteLine(err.Message);
                                }

                                Cleanup();
                                CleanExit(ExitCodes.ScriptExecutionError);
                            }
                            else
                            {
                                Console.WriteLine("Clean process successful.");
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e.Message);
                            Console.WriteLine(e.StackTrace);

                            if (e.InnerException != null)
                            {
                                Console.WriteLine(e.InnerException.Message);
                                Console.WriteLine(e.InnerException.StackTrace);
                            }

                            Cleanup();
                            CleanExit(ExitCodes.ScriptExecutionError);
                        }
                    }

                    CleanExit(ExitCodes.Success);
                }                

                // Rebuild the arch and config flags from the options.
                var archStringList = options.Arch.Select(x => x.ToString()).ToList();
                Architecture archFlag = (Architecture)Enum.Parse(typeof(Architecture), string.Join(",", archStringList));

                var configStringList = options.Configuration.Select(x => x.ToString()).ToList();
                BuildConfiguration configFlag = (BuildConfiguration)Enum.Parse(typeof(BuildConfiguration), string.Join(",", configStringList));
                //

                // FYI, no need to iterate over config and arch flags when
                // building. Each build task is expected to do this internally.

                foreach (var buildTask in pendingTasks)
                {
                    try
                    {
                        WriteTitleToConsole(string.Format("Running Build Task: {0}", buildTask.TaskFriendlyName));

                        if (!buildTask.Run(configFlag, archFlag))
                        {
                            Console.WriteLine("Failed to execute build task.");

                            foreach (var err in buildTask.Errors)
                            {
                                Console.WriteLine(err.Message);
                            }

                            Cleanup();
                            CleanExit(ExitCodes.ScriptExecutionError);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        Console.WriteLine(e.StackTrace);

                        if (e.InnerException != null)
                        {
                            Console.WriteLine(e.InnerException.Message);
                            Console.WriteLine(e.InnerException.StackTrace);
                        }

                        Cleanup();
                        CleanExit(ExitCodes.ScriptExecutionError);
                    }
                }
            }
            else
            {
                CleanExit(ExitCodes.ProjectDirectoryDoesNotExist);
            }

            Cleanup();
        }

        private static void CleanExit(ExitCodes code)
        {
            Cleanup();

            // Clear a line for neatness.
            Console.WriteLine();

            if(code != ExitCodes.Success)
            {
                // Write out our reson.
                Console.WriteLine(string.Format("Exiting due to error: {0}", Regex.Replace(code.ToString(), @"(\B[A-Z]+?(?=[A-Z][^A-Z])|\B[A-Z]+?(?=[^A-Z]))", " $1")));
            }            

            // Exit.
            Environment.Exit((int)code);
        }

        private static void Cleanup()
        {
            foreach (var ms in GeneratedAssemblies)
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
            WriteTitleToConsole("Loading & Parsing Build Script");

            // Exhaustively verify that the file exists, is readable and is not
            // an empty file.
            Debug.Assert(!string.IsNullOrEmpty(taskScriptPath) && !string.IsNullOrWhiteSpace(taskScriptPath), "Build task script path is null, empty or whitespace.");

            if (string.IsNullOrEmpty(taskScriptPath) || string.IsNullOrWhiteSpace(taskScriptPath))
            {
                throw new ArgumentException("Build task script path is null, empty or whitespace.", nameof(taskScriptPath));
            }

            Debug.Assert(File.Exists(taskScriptPath), "Build task script path points to non-existent file.");

            if (!File.Exists(taskScriptPath))
            {
                throw new ArgumentException("Build task script path points to non-existent file.");
            }

            Console.WriteLine(string.Format("Reading all text from file {0}", taskScriptPath));

            var scriptContents = File.ReadAllText(taskScriptPath);

            Debug.Assert(!string.IsNullOrEmpty(scriptContents) && !string.IsNullOrWhiteSpace(scriptContents), "Build task script contents are null, empty or whitespace.");

            if (string.IsNullOrEmpty(scriptContents) || string.IsNullOrWhiteSpace(scriptContents))
            {
                throw new ArgumentException("Build task script contents are null, empty or whitespace.", nameof(taskScriptPath));
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
            foreach (var referencedAssembly in referencedAssemblies)
            {
                var loadedAssembly = Assembly.Load(referencedAssembly);

                var mref = MetadataReference.CreateFromFile(loadedAssembly.Location);

                if (loadedAssembly.FullName.Contains("System.Runtime.Extension"))
                {
                    // Have to do this to avoid collisions with duplicate type
                    // definitions between private mscorlib and this assembly.
                    // XXX TODO - Needs to be solved in a better way?
                    mref = mref.WithAliases(new List<string>(new[] { "CorPrivate" }));
                }

                references.Add(mref);

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

            if (result.Success)
            {
                // Store the in-memory assembly until this program exits.
                GeneratedAssemblies.Add(ms);

                // Get an Assembly structure from the data in memory.
                ms.Seek(0, SeekOrigin.Begin);
                AssemblyLoadContext loadCtx = AssemblyLoadContext.Default;
                Assembly assembly = loadCtx.LoadFromStream(ms);

                // Enumerate types exported from the assembly. Presently not used.
                var exportedTypes = assembly.ExportedTypes;
                foreach (var xp in exportedTypes)
                {
                    Console.WriteLine(string.Format("Build script exports type: {0}", xp.Name));
                }

                // Filter exported types so we only pull types extending from AbstractBuildTask.                
                var filteredExports = exportedTypes.Where(x => x.Name != typeof(AbstractBuildTask).Name);
                Console.WriteLine(string.Format("Number of exported build objects: {0}", filteredExports.Count()));

                // Ensure that we have at least one exported build task.
                Debug.Assert(filteredExports.Count() > 0, "Script either does not export any AbstractBuildTask objects. Build scripts should export one or more AbstractBuildTask objects.");
                if (filteredExports.Count() <= 0)
                {
                    throw new ArgumentException("Script either does not export any AbstractBuildTask objects. Build scripts should export one or more AbstractBuildTask objects.", nameof(taskScriptPath));
                }

                var filteredExportsList = filteredExports.ToList();
                List<AbstractBuildTask> buildTasks = new List<AbstractBuildTask>();

                foreach (var entry in filteredExportsList)
                {
                    AbstractBuildTask typedExport = (AbstractBuildTask)Activator.CreateInstance(entry, new[] { taskScriptPath.ConvertToHostOsPath() });
                    buildTasks.Add(typedExport);
                }

                return buildTasks;
            }
            else
            {
                ms.Dispose();

                Console.WriteLine(string.Format("Failed to compile build script: {0}.", taskScriptPath));

                IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                        diagnostic.IsWarningAsError ||
                        diagnostic.Severity == DiagnosticSeverity.Error);

                foreach (Diagnostic diagnostic in failures)
                {
                    Console.Error.WriteLine("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                }

                CleanExit(ExitCodes.ScriptCompilationFailure);
            }

            Console.WriteLine("null");

            return null;
        }

        /// <summary>
        /// Writes the given string as a centered title to the console.
        /// </summary>
        /// <param name="title">
        /// The title to write.
        /// </param>
        private static void WriteTitleToConsole(string title)
        {
            // Write empty line just for neatness;
            Console.WriteLine();

            if (string.IsNullOrEmpty(title) || string.IsNullOrWhiteSpace(title))
            {
                title = string.Empty;
            }

            FillConsoleWidth('~');

            Console.SetCursorPosition((Console.WindowWidth - title.Length) / 2, Console.CursorTop);

            Console.WriteLine(title);

            FillConsoleWidth('~');
        }

        /// <summary>
        /// Fills the width of the console window with the given character.
        /// </summary>
        /// <param name="c">
        /// The char to fill the width of the console with.
        /// </param>
        private static void FillConsoleWidth(char c)
        {
            var s = new string(c, Console.WindowWidth);
            Console.WriteLine(s);
        }
    }
}
