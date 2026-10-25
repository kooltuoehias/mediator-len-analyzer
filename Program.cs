using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


var projectPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath("../sample/sample.csproj");

MSBuildLocator.RegisterDefaults();
var (diRegistrations, requestEntries) = await AnalyzeAsync(projectPath);
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
    new { DIRegistrations = diRegistrations, MediatRHandlers = requestEntries },
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

static async Task<(Dictionary<string, List<Registration>>, Dictionary<string, RequestEntry>)> AnalyzeAsync(string path)
{
    var diRegistrations = new Dictionary<string, List<Registration>>();
    var requestEntries = new Dictionary<string, RequestEntry>();
    Console.Error.WriteLine($"Loading: {path}");

    using var workspace = MSBuildWorkspace.Create();

    if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
    {
        var solution = await workspace.OpenSolutionAsync(path);
        foreach (var diagnostic in workspace.Diagnostics)
        {
            Console.Error.WriteLine($"Diagnostic: {diagnostic}");
        }

        Console.Error.WriteLine($"Loaded solution: {solution.Projects.Count()} project(s)");

        foreach (var project in solution.Projects)
        {
            await AnalyzeProject(project, diRegistrations, requestEntries);
        }
    }
    else
    {
        var project = await workspace.OpenProjectAsync(path);
        foreach (var diagnostic in workspace.Diagnostics)
        {
            Console.Error.WriteLine($"Diagnostic: {diagnostic}");
        }

        await AnalyzeProject(project, diRegistrations, requestEntries);
    }

    return (diRegistrations, requestEntries);
}

static async Task AnalyzeProject(
    Project project,
    Dictionary<string, List<Registration>> diRegistrations,
    Dictionary<string, RequestEntry> requestEntries)
{
    Console.Error.WriteLine($"Loaded project: {project.Name}, {project.Documents.Count()} document(s)");

    foreach (var document in project.Documents)
    {
        var documentTree = await document.GetSyntaxTreeAsync();
        var documentModel = await document.GetSemanticModelAsync();
        if (documentTree is null || documentModel is null) continue;

        var documentRoot = documentTree.GetRoot();

        foreach (var invocation in documentRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member) continue;
            if (member.Name is not GenericNameSyntax generic) continue;
            var method = generic.Identifier.Text;
            if (method is not ("AddScoped" or "AddSingleton" or "AddTransient")) continue;
            var taargs = generic.TypeArgumentList.Arguments;
            if (taargs.Count != 2) continue;
            var implSymbol = documentModel.GetSymbolInfo(taargs[1]).Symbol as ITypeSymbol;
            if (implSymbol is not null && ContainsTypeParameter(implSymbol)) continue;

            var location1 = implSymbol?.Locations.FirstOrDefault();
            string? file = null;
            int? line = null;
            if (location1 is { IsInSource: true })
            {
                var span1 = location1.GetLineSpan();
                file = span1.Path;
                line = span1.StartLinePosition.Line;
            }

            if (!diRegistrations.TryGetValue(taargs[0].ToString(), out var list))
            {
                list = new List<Registration>();
                diRegistrations[taargs[0].ToString()] = list;
            }
            list.Add(new Registration(taargs[1].ToString(), file, line));
        }

        foreach (var classDecl in documentRoot.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (documentModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol) continue;

            foreach (var iface in classSymbol.AllInterfaces)
            {
                if (iface.Name != "IRequestHandler" || iface.TypeArguments.Length < 1) continue;
                if (classSymbol.IsAbstract) continue;

                var requestType = iface.TypeArguments[0].ToDisplayString();
                var location = classSymbol.Locations.FirstOrDefault();
                string? file = null;
                int? line = null;
                if (location is { IsInSource: true })
                {
                    var span = location.GetLineSpan();
                    file = span.Path;
                    line = span.StartLinePosition.Line;
                }

                if (!requestEntries.TryGetValue(requestType, out var entry))
                {
                    entry = new RequestEntry(ResolveNamedTypeChain(iface.TypeArguments[0]), new List<HandlerRegistration>());
                    requestEntries[requestType] = entry;
                }
                entry.Handlers.Add(new HandlerRegistration(classSymbol.Name, file, line));
            }
        }
    }
}

static bool ContainsTypeParameter(ITypeSymbol type)
{
    if (type is ITypeParameterSymbol) return true;
    if (type is INamedTypeSymbol { IsGenericType: true } named)
    {
        return named.TypeArguments.Any(ContainsTypeParameter);
    }
    return false;
}

static List<NamedTypeLocation> ResolveNamedTypeChain(ITypeSymbol type)
{
    var chain = new List<NamedTypeLocation>();

    void Visit(ITypeSymbol current)
    {
        if (current is not INamedTypeSymbol named) return;

        var location = named.Locations.FirstOrDefault();
        string? file = null;
        int? line = null;
        if (location is { IsInSource: true })
        {
            var span = location.GetLineSpan();
            file = span.Path;
            line = span.StartLinePosition.Line;
        }

        chain.Add(new NamedTypeLocation(named.ToDisplayString(), file, line));

        foreach (var typeArgument in named.TypeArguments)
        {
            Visit(typeArgument);
        }
    }

    Visit(type);
    return chain;
}

record Registration(string Implementation, string? File, int? Line);
record HandlerRegistration(string Handler, string? File, int? Line);
record NamedTypeLocation(string Name, string? File, int? Line);
record RequestEntry(List<NamedTypeLocation> RequestChain, List<HandlerRegistration> Handlers);
