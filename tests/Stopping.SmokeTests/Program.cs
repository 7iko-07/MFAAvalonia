using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Compile the actual stop/command methods against controllable UI and native
// boundaries, so races and asynchronous failures need no emulator or application exit.
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string Read(string path) => File.ReadAllText(Path.Combine(root, path));
string Methods(string path, params string[] names)
{
    var methods = CSharpSyntaxTree.ParseText(Read(path)).GetRoot().DescendantNodes()
        .OfType<MethodDeclarationSyntax>().Where(method => names.Contains(method.Identifier.Text)).ToArray();
    if (methods.Length != names.Length) throw new Exception("Missing or ambiguous production method: " + path);
    return string.Join("\n", methods.Select(method => method.WithoutLeadingTrivia().WithoutTrailingTrivia().ToFullString()));
}
var processor = Methods("MFAAvalonia/Extensions/MaaFW/MaaProcessor.cs",
    "EnsureCommandThread", "CommandLoop", "EnqueueCommand", "StopCommandThread", "Stop", "StopInternal",
    "CancelOperations", "StopAgents", "ShouldProcessStop", "ExecuteStopCore", "AbortCurrentTasker");
var agents = Methods("MFAAvalonia/Extensions/MaaFW/AgentHelper.cs", "KillAllAgents", "KillAgentProcesses");
using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Stopping.SmokeTests.Harness.cs")!;
var harness = new StreamReader(resource).ReadToEnd()
    .Replace("// PROCESSOR_METHODS", processor).Replace("// AGENT_METHODS", agents);
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(path => MetadataReference.CreateFromFile(path));
var compilation = CSharpCompilation.Create("StoppingRegressionHarness",
    [CSharpSyntaxTree.ParseText(harness), CSharpSyntaxTree.ParseText(Read("MFAAvalonia/Helper/TaskManager.cs"))],
    references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var output = new MemoryStream();
var result = compilation.Emit(output);
if (!result.Success) throw new Exception(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
var assembly = Assembly.Load(output.ToArray());
await (Task)assembly.GetType("RegressionCases")!.GetMethod("Run")!.Invoke(null, null)!;
