using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length == 0)
    return Usage();

return args[0] switch
{
    "run" when args.Length == 4 => RunAssembly(args[1], args[2], args[3]),
    "compare" when args.Length == 4 => Compare(args[1], args[2], args[3]),
    _ => Usage()
};

static int RunAssembly(string assemblyPath, string corpusPath, string outputPath)
{
    assemblyPath = Path.GetFullPath(assemblyPath);
    corpusPath = Path.GetFullPath(corpusPath);
    outputPath = Path.GetFullPath(outputPath);

    var corpus = JsonSerializer.Deserialize<List<CorpusCase>>(
        File.ReadAllText(corpusPath),
        JsonOptions()) ?? throw new InvalidOperationException("Syntax corpus is empty.");

    var loadContext = new SqlCoreLoadContext(assemblyPath);
    var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

    var dialectType = RequiredType(assembly, "HsSqlAgent.SqlCore.Enums.SqlAgentToolType");
    var parserType = RequiredType(assembly, "HsSqlAgent.SqlCore.SqlParsing.CoreSqlTextParser");
    var compilerType = RequiredType(assembly, "HsSqlAgent.SqlCore.Core.Pipeline.CoreSqlCompiler");
    var validationType = RequiredType(assembly, "HsSqlAgent.SqlCore.Core.Pipeline.SqlPlanValidationContext");
    var policyType = RequiredType(assembly, "HsSqlAgent.SqlCore.Core.Pipeline.SqlExecutionPlanPolicy");

    var dialects = corpus.ToDictionary(
        item => item.Name,
        item => Enum.Parse(dialectType, item.Dialect ?? "Postgres", ignoreCase: true),
        StringComparer.Ordinal);

    var parse = parserType.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == "ParseQuery")
        .Where(method => method.GetParameters().Length is 2 or 3)
        .OrderBy(method => method.GetParameters().Length)
        .FirstOrDefault()
        ?? throw new MissingMethodException(parserType.FullName, "ParseQuery");

    var createCompiler = compilerType.GetMethod(
        "CreateDefault",
        BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(compilerType.FullName, "CreateDefault");

    var compiler = createCompiler.Invoke(null, null)
        ?? throw new InvalidOperationException("CoreSqlCompiler.CreateDefault returned null.");

    var outcomes = new List<Outcome>(corpus.Count);
    foreach (var item in corpus)
    {
        try
        {
            var dialect = dialects[item.Name];
            var parsed = InvokeWithOptionalTail(parse, null, item.Sql, dialect)
                ?? throw new InvalidOperationException("ParseQuery returned null.");
            var validation = CreateWithOptionalTail(validationType, "syntax-parity-main-floor-v2");
            var policy = CreateWithOptionalTail(policyType);

            var compile = compilerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.Name == "Compile")
                .Where(method => method.GetParameters().Length is 4 or 5)
                .Where(method => method.GetParameters()[0].ParameterType.IsInstanceOfType(parsed))
                .OrderBy(method => method.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new MissingMethodException(compilerType.FullName, "Compile");

            _ = InvokeWithOptionalTail(
                compile,
                compiler,
                parsed,
                dialect,
                validation,
                policy);

            outcomes.Add(new Outcome(item.Name, true, null, null));
        }
        catch (Exception exception)
        {
            var actual = Unwrap(exception);
            outcomes.Add(new Outcome(
                item.Name,
                false,
                actual.GetType().FullName,
                actual.Message));
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(outcomes, JsonOptions(writeIndented: true)));

    Console.WriteLine(
        $"Captured {outcomes.Count} cases from {Path.GetFileName(assemblyPath)}: " +
        $"{outcomes.Count(outcome => outcome.Success)} success, " +
        $"{outcomes.Count(outcome => !outcome.Success)} failure.");

    loadContext.Unload();
    return 0;
}

static int Compare(string mainPath, string headPath, string allowListPath)
{
    var main = ReadOutcomes(mainPath);
    var head = ReadOutcomes(headPath);
    var allowList = JsonSerializer.Deserialize<HashSet<string>>(
        File.ReadAllText(allowListPath),
        JsonOptions()) ?? [];

    var regressions = new List<string>();
    var intentional = new List<string>();
    var expansions = new List<string>();

    foreach (var (name, mainOutcome) in main.OrderBy(pair => pair.Key, StringComparer.Ordinal))
    {
        if (!head.TryGetValue(name, out var headOutcome))
        {
            regressions.Add($"{name}: missing from PR result set");
            continue;
        }

        if (mainOutcome.Success && !headOutcome.Success)
        {
            var detail =
                $"{name}: main=success, PR=failure " +
                $"({headOutcome.ExceptionType}: {headOutcome.Message})";

            if (allowList.Contains(name))
                intentional.Add(detail);
            else
                regressions.Add(detail);
        }
        else if (!mainOutcome.Success && headOutcome.Success)
        {
            expansions.Add(name);
        }
    }

    var staleAllowList = allowList
        .Where(name => !intentional.Any(entry => entry.StartsWith(name + ":", StringComparison.Ordinal)))
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    Console.WriteLine($"Compatibility floor: {main.Count} cases.");
    Console.WriteLine($"Capability expansions over main: {expansions.Count}.");
    foreach (var name in expansions)
        Console.WriteLine($"  + {name}");

    if (intentional.Count > 0)
    {
        Console.WriteLine($"Explicit intentional breaks: {intentional.Count}.");
        foreach (var item in intentional)
            Console.WriteLine($"  ! {item}");
    }

    if (staleAllowList.Length > 0)
    {
        Console.Error.WriteLine("Stale intentional-breaking allowlist entries are not permitted:");
        foreach (var name in staleAllowList)
            Console.Error.WriteLine($"  - {name}");
        return 1;
    }

    if (regressions.Count == 0)
    {
        Console.WriteLine("No main -> PR syntax capability regressions detected.");
        return 0;
    }

    Console.Error.WriteLine("Syntax capability regressions detected:");
    foreach (var regression in regressions)
        Console.Error.WriteLine($"  - {regression}");
    return 1;
}

static Dictionary<string, Outcome> ReadOutcomes(string path)
{
    var values = JsonSerializer.Deserialize<List<Outcome>>(
        File.ReadAllText(path),
        JsonOptions()) ?? throw new InvalidOperationException($"No outcomes in {path}.");

    return values.ToDictionary(value => value.Name, StringComparer.Ordinal);
}

static object CreateWithOptionalTail(Type type, params object?[] leading)
{
    foreach (var constructor in type.GetConstructors().OrderBy(value => value.GetParameters().Length))
    {
        var parameters = constructor.GetParameters();
        if (parameters.Length < leading.Length)
            continue;
        if (!LeadingArgumentsFit(parameters, leading))
            continue;

        var arguments = FillArguments(parameters, leading);
        return constructor.Invoke(arguments);
    }

    throw new MissingMethodException(type.FullName, ".ctor");
}

static object? InvokeWithOptionalTail(
    MethodInfo method,
    object? instance,
    params object?[] leading)
{
    var parameters = method.GetParameters();
    if (parameters.Length < leading.Length || !LeadingArgumentsFit(parameters, leading))
        throw new MissingMethodException(method.DeclaringType?.FullName, method.Name);

    return method.Invoke(instance, FillArguments(parameters, leading));
}

static bool LeadingArgumentsFit(ParameterInfo[] parameters, object?[] leading)
{
    for (var index = 0; index < leading.Length; index++)
    {
        var argument = leading[index];
        if (argument is null)
        {
            if (parameters[index].ParameterType.IsValueType
                && Nullable.GetUnderlyingType(parameters[index].ParameterType) is null)
                return false;
        }
        else if (!parameters[index].ParameterType.IsInstanceOfType(argument))
        {
            return false;
        }
    }

    return true;
}

static object?[] FillArguments(ParameterInfo[] parameters, object?[] leading)
{
    var arguments = new object?[parameters.Length];
    Array.Copy(leading, arguments, leading.Length);

    for (var index = leading.Length; index < parameters.Length; index++)
    {
        var parameter = parameters[index];
        if (parameter.HasDefaultValue)
        {
            arguments[index] = parameter.DefaultValue;
            continue;
        }

        var type = parameter.ParameterType;
        arguments[index] = type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? Activator.CreateInstance(type)
            : null;
    }

    return arguments;
}

static Type RequiredType(Assembly assembly, string name) =>
    assembly.GetType(name, throwOnError: true)
    ?? throw new TypeLoadException(name);

static Exception Unwrap(Exception exception)
{
    while (exception is TargetInvocationException invocation
           && invocation.InnerException is Exception inner)
    {
        exception = inner;
    }

    return exception;
}

static JsonSerializerOptions JsonOptions(bool writeIndented = false) =>
    new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = writeIndented
    };

static int Usage()
{
    Console.Error.WriteLine(
        "Usage:\n" +
        "  SqlSyntaxParityRunner run <HsSqlAgent.SqlCore.dll> <corpus.json> <output.json>\n" +
        "  SqlSyntaxParityRunner compare <main.json> <head.json> <allowlist.json>");
    return 2;
}

sealed record CorpusCase(string Name, string Sql, string? Dialect = null);
sealed record Outcome(string Name, bool Success, string? ExceptionType, string? Message);

sealed class SqlCoreLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly string _directory = Path.GetDirectoryName(assemblyPath)
        ?? throw new ArgumentException("Assembly path has no directory.", nameof(assemblyPath));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(candidate)
            ? LoadFromAssemblyPath(candidate)
            : null;
    }
}
