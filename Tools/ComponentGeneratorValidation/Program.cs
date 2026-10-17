using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpEngine.ComponentGeneration;
using SharpEngine.Components;

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Append(typeof(SharpComponent).Assembly.Location).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
int count = 0;
string Declaration(string body, string modifiers = "partial", string attributes = "") =>
    "using System; using System.Collections.Generic; using SharpEngine.Components; " +
    "[ComponentTypeId(\"58322a08-daf7-4342-b1d7-fc00ea751899\"), ComponentSchemaVersion(1)] " + attributes +
    " public " + modifiers + " class TestComponent : SharpComponent { " + body + " }";
void Compile(string source, bool success, string expected = "")
{
    var compilation = CSharpCompilation.Create("GeneratorFixture",
        [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12))], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    GeneratorDriver driver = CSharpGeneratorDriver.Create(new ComponentGenerator());
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
    var errors = diagnostics.Concat(output.GetDiagnostics()).Where(d => d.Severity == DiagnosticSeverity.Error).Distinct().ToArray();
    if (success && errors.Length != 0 || !success && !errors.Any(d => d.Id == "SECOMP001" && d.GetMessage().Contains(expected)))
        throw new Exception("Unexpected generator result: " + string.Join("\n", errors.Select(e => e.ToString())));
    count++;
}
Compile(Declaration("public float Speed=5; [SerializeField] private int _count=2;"), true);
Compile(Declaration("public TestComponent() {}"), false, "constructors");
Compile(Declaration("public object Cache = new object();"), false, "Unsupported");
Compile(Declaration("[NonSerialized] public object? Cache; public int Count=1;"), true);
Compile(Declaration("public int Count=1;", ""), false, "partial");
Compile(Declaration("public int RandomValue = new Random().Next();"), false, "deterministic");
Compile(Declaration("public Node? Root;") + "[Serializable] public partial class Node { public Node Next = new Node(); }", false, "deterministic");
Compile(Declaration("public Dictionary<string,int> Map = new();"), false, "Unsupported");
Compile(Declaration("public int[,] Grid = new int[2,2];"), false, "Unsupported");
Compile(Declaration("public SharpEntity Target = null!;"), false, "nullable");
Compile(Declaration("public SharpEntity? Target; public List<float> Values = [1,2,3];"), true);
Compile("using SharpEngine.Components; public partial class TestComponent : SharpComponent {}", false, "ComponentTypeId");
Compile(Declaration("public int Count=1;").Replace("ComponentSchemaVersion(1)", "ComponentSchemaVersion(0)"), false, "positive");
Compile(Declaration("public int Count=1;") + "[ComponentTypeId(\"58322a08-daf7-4342-b1d7-fc00ea751899\"), ComponentSchemaVersion(1)] public partial class Other : SharpComponent {}", false, "Duplicate");
Console.WriteLine($"Generator validation passed: {count} compile assertions");
