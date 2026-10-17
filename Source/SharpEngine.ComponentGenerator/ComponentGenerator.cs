using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpEngine.ComponentGeneration;

[Generator]
public sealed class ComponentGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Invalid = new("SECOMP001", "Invalid component declaration",
        "{0}", "SharpEngine", DiagnosticSeverity.Error, true);
    private static string Full(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("?", "");
    private static string Quote(string text) => SymbolDisplay.FormatLiteral(text, true);
    private static AttributeData? Attr(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == name + "Attribute");
    private static bool Component(INamedTypeSymbol type) => type.BaseType is { } parent &&
        (parent.ToDisplayString() == "SharpEngine.Components.SharpComponent" || Component(parent));
    private static IEnumerable<IFieldSymbol> Fields(INamedTypeSymbol type)
    {
        if (type.BaseType is { } parent && parent.SpecialType != SpecialType.System_Object && parent.ToDisplayString() != "SharpEngine.Components.SharpComponent")
            foreach (var field in Fields(parent)) yield return field;
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            if (!field.IsStatic && !field.IsConst && !field.IsReadOnly && !field.IsImplicitlyDeclared &&
                Attr(field, "NonSerialized") is null && (field.DeclaredAccessibility == Accessibility.Public || Attr(field, "SerializeField") is not null)) yield return field;
    }
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider((node, _) => node is TypeDeclarationSyntax,
            (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node) as INamedTypeSymbol)
            .Where(type => type is not null).Collect();
        context.RegisterSourceOutput(context.CompilationProvider.Combine(types), (ctx, input) => Generate(ctx, input.Left, input.Right));
    }
    private static void Generate(SourceProductionContext context, Compilation compilation, ImmutableArray<INamedTypeSymbol?> symbols)
    {
        var types = symbols.OfType<INamedTypeSymbol>().Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray();
        var components = types.Where(Component).ToArray();
        var dataTypes = types.Where(t => !Component(t) && Attr(t, "Serializable") is not null).ToArray();
        var registrations = new StringBuilder();
        var allIds = new HashSet<string>();
        foreach (var type in components.Concat(dataTypes))
        {
            bool component = Component(type);
            bool failed = false;
            void Error(string message, ISymbol? at = null)
            { failed = true; context.ReportDiagnostic(Diagnostic.Create(Invalid, (at ?? type).Locations.FirstOrDefault(), message)); }
            if (type.IsGenericType || type.ContainingType is not null) { Error("Serializable component/data types must be non-generic top-level types"); continue; }
            if (type.DeclaredAccessibility != Accessibility.Public) Error("Component/data types must be public");
            bool partial = type.DeclaringSyntaxReferences.All(r => r.GetSyntax() is TypeDeclarationSyntax s && s.Modifiers.Any(SyntaxKind.PartialKeyword));
            if ((component || Fields(type).Any(f => f.DeclaredAccessibility != Accessibility.Public)) && !partial)
                Error("Add partial to allow generated access to serialized fields");
            if (component && type.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared)) Error("Explicit component constructors are forbidden; use OnInitialize");
            var id = Attr(type, "ComponentTypeId")?.ConstructorArguments.FirstOrDefault().Value as string;
            var version = Attr(type, "ComponentSchemaVersion")?.ConstructorArguments.FirstOrDefault().Value as int?;
            if (component && !type.IsAbstract && (id is null || (!id.StartsWith("engine.") && !Guid.TryParse(id, out _)))) Error("ComponentTypeId requires a generated UUID");
            if (component && id is not null && !allIds.Add(id)) Error($"Duplicate component type ID {id}");
            if (component && !type.IsAbstract && version is not > 0) Error("ComponentSchemaVersion must be positive");
            var fields = Fields(type).ToArray();
            if (fields.Select(f => f.Name).Distinct().Count() != fields.Length) Error("Serialized field names must be unique across inheritance");
            foreach (var field in fields)
            {
                if (!Supported(field.Type, [])) Error($"Unsupported serialized field type {field.Type}; use NonSerialized to exclude it", field);
                if ((field.Type.ToDisplayString() == "SharpEngine.Components.SharpEntity" || field.Type is INamedTypeSymbol n && Component(n)) &&
                    field.NullableAnnotation != NullableAnnotation.Annotated) Error("Entity and component references must be nullable", field);
            }
            if (failed) continue;
            var name = Full(type);
            var helper = partial ? name : "global::SharpEngine.Generated." + type.Name + "DataAdapter";
            var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
            foreach (var u in type.DeclaringSyntaxReferences.SelectMany(r => r.SyntaxTree.GetCompilationUnitRoot().Usings).Select(u => u.ToString()).Distinct()) source.AppendLine(u);
            if (partial)
            {
                if (!type.ContainingNamespace.IsGlobalNamespace) source.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).AppendLine(";");
                source.Append("public partial ").Append(type.TypeKind == TypeKind.Struct ? "struct " : "class ").Append(type.Name).AppendLine(" {");
            }
            else source.Append("namespace SharpEngine.Generated; public static class ").Append(type.Name).AppendLine("DataAdapter {");
            source.AppendLine("public static global::SharpEngine.Components.ComponentField[] __SharpFields() => new global::SharpEngine.Components.ComponentField[] {");
            foreach (var field in fields)
            {
                if (!SymbolEqualityComparer.Default.Equals(field.ContainingType, type))
                {
                    source.Append("global::System.Linq.Enumerable.Single(").Append(Full(field.ContainingType)).Append(".__SharpFields(), f => f.Name == ").Append(Quote(field.Name)).AppendLine("),");
                    continue;
                }
                source.Append("new(").Append(Quote(field.Name)).Append(", typeof(").Append(Full(field.Type)).Append("), o => ((").Append(name).Append(")o).@").Append(field.Name)
                    .Append(", (o,v) => ").Append(type.IsValueType ? "global::System.Runtime.CompilerServices.Unsafe.Unbox<" + name + ">(o)" : "((" + name + ")o)")
                    .Append(".@").Append(field.Name).Append(" = (").Append(Full(field.Type)).Append(")v!");
                foreach (var (attribute, parameter) in new[] { ("InspectorName", "DisplayName"), ("Tooltip", "Tooltip"), ("Header", "Header") })
                    if (Attr(field, attribute)?.ConstructorArguments.FirstOrDefault().Value is string text) source.Append(", ").Append(parameter).Append(": ").Append(Quote(text));
                if (Attr(field, "HideInInspector") is not null) source.Append(", Hidden: true");
                if (Attr(field, "Range") is { } range)
                {
                    source.Append(", Minimum: ").Append(Convert.ToDouble(range.ConstructorArguments[0].Value).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    source.Append(", Maximum: ").Append(Convert.ToDouble(range.ConstructorArguments[1].Value).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (Attr(field, "Multiline") is { } multiline) source.Append(", Lines: ").Append(multiline.ConstructorArguments[0].Value);
                source.AppendLine("),");
            }
            source.AppendLine("};");
            if (component)
            {
                source.AppendLine("public static global::System.Text.Json.Nodes.JsonObject __SharpDefaults(global::SharpEngine.Components.ComponentRegistry registry) => new() {");
                foreach (var field in fields)
                {
                    if (!SymbolEqualityComparer.Default.Equals(field.ContainingType, type))
                    {
                        source.Append('[').Append(Quote(field.Name)).Append("] = ").Append(Full(field.ContainingType)).Append(".__SharpDefaults(registry)[").Append(Quote(field.Name)).AppendLine("]?.DeepClone(),");
                        continue;
                    }
                    var variable = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
                    var expression = variable?.Initializer?.Value;
                    var initial = "default(" + Full(field.Type) + ")";
                    if (expression is not null)
                    {
                        var model = compilation.GetSemanticModel(expression.SyntaxTree);
                        if (!Deterministic(expression, model)) Error($"Default initializer for {field.Name} must be deterministic", field);
                        initial = expression.ToString();
                    }
                    source.Append('[').Append(Quote(field.Name)).Append("] = global::SharpEngine.Components.ComponentValue.Encode(((").Append(Full(field.Type)).Append(field.Type.IsReferenceType ? "?" : "").Append(")(").Append(initial)
                        .Append("))!, typeof(").Append(Full(field.Type)).AppendLine("), registry),");
                }
                source.AppendLine("};");
                source.AppendLine("public sealed class InitialState : global::SharpEngine.Components.IComponentInitialState {");
                source.Append("public global::System.Type ComponentType => typeof(").Append(name).AppendLine(");");
                source.AppendLine("private readonly global::System.Collections.Generic.Dictionary<string, object?> values = new();");
                foreach (var field in fields)
                    source.Append("public ").Append(Full(field.Type)).Append(field.Type.IsReferenceType ? "?" : "").Append(" @").Append(field.Name)
                        .Append(" { set => values[").Append(Quote(field.Name)).AppendLine("] = value; }");
                source.AppendLine("public global::System.Text.Json.Nodes.JsonObject ToState(global::SharpEngine.Components.ComponentRegistry registry) { var state = new global::System.Text.Json.Nodes.JsonObject();");
                foreach (var field in fields)
                    source.Append("if (values.TryGetValue(").Append(Quote(field.Name)).Append(", out var v_").Append(field.Name).Append(")) state[").Append(Quote(field.Name))
                        .Append("] = global::SharpEngine.Components.ComponentValue.Encode(v_").Append(field.Name).Append(", typeof(").Append(Full(field.Type)).AppendLine("), registry);");
                source.AppendLine("return state; } }");
                source.AppendLine("public readonly struct GeneratedState { private readonly global::System.Text.Json.Nodes.JsonObject state; private readonly global::SharpEngine.Components.ComponentRegistry registry; public GeneratedState(global::System.Text.Json.Nodes.JsonObject state, global::SharpEngine.Components.ComponentRegistry registry) { this.state = state; this.registry = registry; }");
                foreach (var field in fields) source.Append("public ").Append(Full(field.Type)).Append(" @").Append(field.Name).Append(" => (").Append(Full(field.Type))
                    .Append(")global::SharpEngine.Components.ComponentValue.Decode(state[").Append(Quote(field.Name)).Append("], typeof(").Append(Full(field.Type)).AppendLine("), registry)!;");
                source.AppendLine("}");
                var menu = Attr(type, "AddComponentMenu")?.ConstructorArguments[0].Value as string;
                var requires = type.GetAttributes().Where(a => a.AttributeClass?.Name == "RequireComponentAttribute")
                    .Select(a => "typeof(" + Full((ITypeSymbol)a.ConstructorArguments[0].Value!) + ")");
                if (!type.IsAbstract)
                {
                source.AppendLine("public static void __SharpRegister(global::SharpEngine.Components.ComponentRegistry registry, string module, string generation) {");
                source.Append("registry.Register(new(").Append(Quote(id!)).Append(", ").Append(version).Append(", typeof(").Append(name).Append("), ")
                    .Append(menu is null ? "\"Scripts/\" + module + \"/\" + " + Quote(type.Name) : Quote(menu)).Append(", ")
                    .Append(Attr(type, "DisallowMultipleComponent") is not null ? "true" : "false").Append(", new global::System.Type[] {").Append(string.Join(",", requires))
                    .Append("}, __SharpFields(), () => new ").Append(name).Append("(), __SharpDefaults");
                if (type.GetMembers("Validate").OfType<IMethodSymbol>().Any(m => m.IsStatic)) source.Append(", (state, diagnostics) => Validate(new GeneratedState(state, registry), diagnostics)");
                source.AppendLine("), module, generation); }");
                registrations.Append(helper).AppendLine(".__SharpRegister(registry, module, generation);");
                }
            }
            else
            {
                source.AppendLine("public static global::System.Text.Json.Nodes.JsonObject __SharpDataDefaults(global::SharpEngine.Components.ComponentRegistry registry) => new() {");
                foreach (var field in fields)
                {
                    var expression = (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax)?.Initializer?.Value;
                    var initial = expression?.ToString() ?? "default(" + Full(field.Type) + ")";
                    if (expression is not null && !Deterministic(expression, compilation.GetSemanticModel(expression.SyntaxTree))) Error($"Default initializer for {field.Name} must be deterministic", field);
                    source.Append('[').Append(Quote(field.Name)).Append("] = global::SharpEngine.Components.ComponentValue.Encode(((").Append(Full(field.Type)).Append(field.Type.IsReferenceType ? "?" : "").Append(")(").Append(initial).Append("))!, typeof(").Append(Full(field.Type)).AppendLine("), registry),");
                }
                source.AppendLine("};");
                registrations.Insert(0, "registry.RegisterData(new(typeof(" + name + "), () => new " + name + "(), " + helper + ".__SharpFields(), " + helper + ".__SharpDataDefaults));\n");
            }
            source.AppendLine("}");
            var generated = source.ToString();
            if (type.BaseType is { } baseType && Component(baseType)) generated = generated
                .Replace("public static global::", "public new static global::")
                .Replace("public sealed class InitialState", "public new sealed class InitialState")
                .Replace("public readonly struct GeneratedState", "public new readonly struct GeneratedState");
            if (!failed) context.AddSource(type.ToDisplayString().Replace('.', '_') + ".Components.g.cs", generated);
        }
        if (components.Length != 0 || dataTypes.Length != 0)
        {
            var catalog = "Catalog_" + string.Concat((compilation.AssemblyName ?? "Module").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            context.AddSource("SharpComponentCatalog.g.cs", "// <auto-generated/>\n[assembly: global::SharpEngine.Components.ComponentCatalog(typeof(SharpEngine.Generated." + catalog + "))]\nnamespace SharpEngine.Generated { public sealed class " + catalog + " : global::SharpEngine.Components.IComponentCatalog { public void Register(global::SharpEngine.Components.ComponentRegistry registry, string module, string generation) {\n" + registrations + "} } }");
        }
    }
    private static bool Supported(ITypeSymbol type, HashSet<ITypeSymbol> visiting)
    {
        if (type.TypeKind == TypeKind.Enum || type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String && type.SpecialType != SpecialType.System_IntPtr && type.SpecialType != SpecialType.System_UIntPtr) return true;
        if (type is IArrayTypeSymbol array) return array.Rank == 1 && Supported(array.ElementType, visiting);
        if (type is not INamedTypeSymbol named) return false;
        var name = named.OriginalDefinition.ToDisplayString();
        if (name is "SharpEngine.Components.SharpEntity" or "SharpEngine.Components.SharpComponent" or "System.Numerics.Vector2" or "System.Numerics.Vector3" or "System.Numerics.Vector4" or "System.Numerics.Quaternion" or "SharpEngine.Components.Color" || Component(named)) return true;
        if (name == "SharpEngine.Components.AssetRef<T>") return true;
        if (name == "System.Collections.Generic.List<T>") return Supported(named.TypeArguments[0], visiting);
        if (Attr(named, "Serializable") is null) return false;
        if (!visiting.Add(type)) return true;
        var supported = Fields(named).All(f => Supported(f.Type, visiting));
        visiting.Remove(type); return supported;
    }
    private static bool Deterministic(ExpressionSyntax expression, SemanticModel model, HashSet<ITypeSymbol>? constructing = null)
    {
        constructing ??= new(SymbolEqualityComparer.Default);
        if (model.GetConstantValue(expression).HasValue) return true;
        if (expression is LiteralExpressionSyntax or DefaultExpressionSyntax) return true;
        if (expression is CollectionExpressionSyntax collection) return collection.Elements.All(e => e is ExpressionElementSyntax item && Deterministic(item.Expression, model));
        if (expression is BaseObjectCreationExpressionSyntax creation)
        {
            var type = model.GetTypeInfo(creation).Type?.ToDisplayString();
            var createdType = model.GetTypeInfo(creation).Type as INamedTypeSymbol;
            if (createdType is not null && !constructing.Add(createdType)) return false;
            var plainData = createdType is not null && Attr(createdType, "Serializable") is not null &&
                createdType.InstanceConstructors.All(c => c.IsImplicitlyDeclared) && Fields(createdType).All(f =>
                f.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax v || v.Initializer is null ||
                Deterministic(v.Initializer.Value, model.Compilation.GetSemanticModel(v.SyntaxTree), constructing));
            if (createdType is not null) constructing.Remove(createdType);
            if (type is null || !(plainData || type.StartsWith("System.Numerics.") || type.StartsWith("SharpEngine.Components.Color") || type.StartsWith("SharpEngine.Components.AssetRef<") || type.StartsWith("System.Collections.Generic.List<"))) return false;
            return (creation.ArgumentList?.Arguments.All(a => Deterministic(a.Expression, model)) ?? true) &&
                (creation.Initializer?.Expressions.All(e => Deterministic(e is AssignmentExpressionSyntax assignment ? assignment.Right : e, model)) ?? true);
        }
        if (expression is MemberAccessExpressionSyntax member && model.GetSymbolInfo(member).Symbol is ISymbol symbol)
            return symbol.ContainingType?.ToDisplayString() is "System.Numerics.Vector2" or "System.Numerics.Vector3" or "System.Numerics.Vector4" or "System.Numerics.Quaternion" or "SharpEngine.Components.Color";
        if (expression is ArrayCreationExpressionSyntax array) return array.Initializer?.Expressions.All(e => Deterministic(e, model)) == true;
        if (expression is ImplicitArrayCreationExpressionSyntax implicitArray) return implicitArray.Initializer.Expressions.All(e => Deterministic(e, model));
        return false;
    }
}
