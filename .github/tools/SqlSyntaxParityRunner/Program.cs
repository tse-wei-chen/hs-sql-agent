using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length == 0)
    return Usage();

return args[0] switch
{
    "run" when args.Length == 4 => RunAssembly(args[1], args[2], args[3]),
    "compare" when args.Length == 4 => Compare(args[1], args[2], args[3]),
    "verify-negative" when args.Length == 3 => VerifyNegative(args[1], args[2]),
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
    var dmlCompilerType = RequiredType(assembly, "HsSqlAgent.SqlCore.Core.Pipeline.CoreDmlCompiler");
    var validationType = RequiredType(assembly, "HsSqlAgent.SqlCore.Core.Pipeline.SqlPlanValidationContext");
    var policyType = RequiredType(assembly, "HsSqlAgent.SqlCore.Core.Pipeline.SqlExecutionPlanPolicy");
    var profileType = RequiredType(assembly, "HsSqlAgent.SqlCore.Models.SqlProviderCapabilityProfile");
    var conflictTargetAssuranceType = RequiredType(
        assembly,
        "HsSqlAgent.SqlCore.Core.Pipeline.DmlConflictTargetAssurance");

    var sourceDialects = corpus.ToDictionary(
        item => item.Name,
        item => Enum.Parse(dialectType, item.Dialect ?? "Postgres", ignoreCase: true),
        StringComparer.Ordinal);
    var targetDialects = corpus.ToDictionary(
        item => item.Name,
        item => Enum.Parse(
            dialectType,
            item.TargetDialect ?? item.Dialect ?? "Postgres",
            ignoreCase: true),
        StringComparer.Ordinal);

    var parseMethods = parserType.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == "ParseQuery")
        .Where(method => method.GetParameters().Length is 2 or 3)
        .ToArray();
    var parseDmlMethods = parserType.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == "ParseDml")
        .Where(method => method.GetParameters().Length is 2 or 3)
        .ToArray();

    var createCompiler = compilerType.GetMethod(
        "CreateDefault",
        BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(compilerType.FullName, "CreateDefault");

    var compiler = createCompiler.Invoke(null, null)
        ?? throw new InvalidOperationException("CoreSqlCompiler.CreateDefault returned null.");

    var createDmlCompiler = dmlCompilerType.GetMethod(
        "CreateDefault",
        BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(dmlCompilerType.FullName, "CreateDefault");
    var dmlCompiler = createDmlCompiler.Invoke(null, null)
        ?? throw new InvalidOperationException("CoreDmlCompiler.CreateDefault returned null.");

    var outcomes = new List<Outcome>(corpus.Count);
    foreach (var item in corpus)
    {
        var sourceDialect = sourceDialects[item.Name];
        var sourceProfile = CreateProviderProfile(
            profileType,
            sourceDialect,
            item.SourceVersion,
            item.SourceCompatibilityLevel,
            item.SourceSessionModes);
        object parsed;
        var parsedAsDml = false;
        try
        {
            var parse = sourceProfile is null
                ? parseMethods
                    .Where(method => method.GetParameters().Length is 2 or 3)
                    .Where(method => LeadingArgumentsFit(
                        method.GetParameters(),
                        [item.Sql, sourceDialect]))
                    .OrderBy(method => method.GetParameters().Length)
                    .FirstOrDefault()
                : parseMethods
                    .Where(method => method.GetParameters().Length >= 3)
                    .Where(method => LeadingArgumentsFit(
                        method.GetParameters(),
                        [item.Sql, sourceDialect, sourceProfile]))
                    .OrderBy(method => method.GetParameters().Length)
                    .FirstOrDefault();

            if (parse is null)
            {
                throw new MissingMethodException(
                    parserType.FullName,
                    sourceProfile is null
                        ? "ParseQuery(sql, dialect[, sourceProfile])"
                        : "ParseQuery(sql, dialect, sourceProfile)");
            }

            try
            {
                parsed = sourceProfile is null
                    ? InvokeWithOptionalTail(parse, null, item.Sql, sourceDialect)
                        ?? throw new InvalidOperationException("ParseQuery returned null.")
                    : InvokeWithOptionalTail(parse, null, item.Sql, sourceDialect, sourceProfile)
                        ?? throw new InvalidOperationException("ParseQuery returned null.");
            }
            catch (Exception queryException)
            {
                var queryActual = Unwrap(queryException);
                if (!queryActual.Message.Contains(
                        "ParseQuery requires a SELECT statement",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }

                var parseDml = sourceProfile is null
                    ? parseDmlMethods
                        .Where(method => LeadingArgumentsFit(
                            method.GetParameters(),
                            [item.Sql, sourceDialect]))
                        .OrderBy(method => method.GetParameters().Length)
                        .FirstOrDefault()
                    : parseDmlMethods
                        .Where(method => method.GetParameters().Length >= 3)
                        .Where(method => LeadingArgumentsFit(
                            method.GetParameters(),
                            [item.Sql, sourceDialect, sourceProfile]))
                        .OrderBy(method => method.GetParameters().Length)
                        .FirstOrDefault();

                if (parseDml is null)
                {
                    throw new MissingMethodException(
                        parserType.FullName,
                        sourceProfile is null
                            ? "ParseDml(sql, dialect[, sourceProfile])"
                            : "ParseDml(sql, dialect, sourceProfile)");
                }

                parsed = sourceProfile is null
                    ? InvokeWithOptionalTail(parseDml, null, item.Sql, sourceDialect)
                        ?? throw new InvalidOperationException("ParseDml returned null.")
                    : InvokeWithOptionalTail(parseDml, null, item.Sql, sourceDialect, sourceProfile)
                        ?? throw new InvalidOperationException("ParseDml returned null.");
                parsedAsDml = true;
            }
        }
        catch (Exception exception)
        {
            var actual = Unwrap(exception);
            outcomes.Add(FailureOutcome(item.Name, "parse", actual));
            continue;
        }

        try
        {
            var validation = CreateWithOptionalTail(validationType, "syntax-parity-main-floor-v3");
            var policy = CreateWithOptionalTail(policyType);

            var targetDialect = targetDialects[item.Name];
            var targetProfile = CreateProviderProfile(
                profileType,
                targetDialect,
                item.TargetVersion,
                item.TargetCompatibilityLevel,
                item.TargetSessionModes);

            object compiledCommand;

            if (parsedAsDml)
            {
                var compileMethods = dmlCompilerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => method.Name == "Compile")
                    .Where(method => method.GetParameters().Length >= 3)
                    .Where(method => method.GetParameters()[0].ParameterType.IsInstanceOfType(parsed));

                var conflictTargetAssurance = CreatePrimaryKeyAssurance(
                    conflictTargetAssuranceType,
                    item.ConflictTargetPrimaryKeyColumns);

                object?[] compileLeading =
                    targetProfile is null
                        ? conflictTargetAssurance is null
                            ? [parsed, targetDialect, validation]
                            : [parsed, targetDialect, validation, conflictTargetAssurance]
                        : conflictTargetAssurance is null
                            ? [parsed, targetDialect, validation, targetProfile]
                            : [parsed, targetDialect, validation, targetProfile, conflictTargetAssurance];

                var compile = compileMethods
                    .Where(method => method.GetParameters().Length >= compileLeading.Length)
                    .Where(method => LeadingArgumentsFit(
                        method.GetParameters(),
                        compileLeading))
                    .OrderBy(method => method.GetParameters().Length)
                    .FirstOrDefault();

                if (compile is null)
                {
                    throw new MissingMethodException(
                        dmlCompilerType.FullName,
                        "Compile(parsed, target, validation[, targetProfile][, conflictTargetAssurance])");
                }

                compiledCommand = InvokeWithOptionalTail(
                    compile,
                    dmlCompiler,
                    compileLeading)
                    ?? throw new InvalidOperationException("DML Compile returned null.");
            }
            else
            {
                var compileMethods = compilerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => method.Name == "Compile")
                    .Where(method => method.GetParameters().Length is 4 or 5)
                    .Where(method => method.GetParameters()[0].ParameterType.IsInstanceOfType(parsed));

                var compile = targetProfile is null
                    ? compileMethods
                        .Where(method => LeadingArgumentsFit(
                            method.GetParameters(),
                            [parsed, targetDialect, validation, policy]))
                        .OrderBy(method => method.GetParameters().Length)
                        .FirstOrDefault()
                    : compileMethods
                        .Where(method => method.GetParameters().Length >= 5)
                        .Where(method => LeadingArgumentsFit(
                            method.GetParameters(),
                            [parsed, targetDialect, validation, policy, targetProfile]))
                        .OrderBy(method => method.GetParameters().Length)
                        .FirstOrDefault();

                if (compile is null)
                {
                    throw new MissingMethodException(
                        compilerType.FullName,
                        targetProfile is null
                            ? "Compile(parsed, target, validation, policy[, targetProfile])"
                            : "Compile(parsed, target, validation, policy, targetProfile)");
                }

                compiledCommand = (targetProfile is null
                    ? InvokeWithOptionalTail(
                        compile,
                        compiler,
                        parsed,
                        targetDialect,
                        validation,
                        policy)
                    : InvokeWithOptionalTail(
                        compile,
                        compiler,
                        parsed,
                        targetDialect,
                        validation,
                        policy,
                        targetProfile))
                    ?? throw new InvalidOperationException("Query Compile returned null.");
            }

            outcomes.Add(new Outcome(
                item.Name,
                true,
                "success",
                null,
                null,
                Semantic: CaptureSemanticSignature(parsed, compiledCommand)));
        }
        catch (Exception exception)
        {
            var actual = Unwrap(exception);
            outcomes.Add(FailureOutcome(item.Name, "compile", actual));
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

    foreach (var outcome in outcomes.Where(outcome => !outcome.Success))
    {
        Console.WriteLine(
            $"  - {outcome.Name}: {outcome.ExceptionType}: {outcome.Message} " +
            $"[diagnostic code={outcome.DiagnosticCode ?? "<none>"}, " +
            $"stage={outcome.DiagnosticStage ?? "<none>"}, " +
            $"category={outcome.DiagnosticCategory ?? "<none>"}, " +
            $"span={outcome.DiagnosticSpanStart?.ToString() ?? "<none>"}+" +
            $"{outcome.DiagnosticSpanLength?.ToString() ?? "<none>"}]");
    }

    var harnessFailures = outcomes
        .Where(IsHarnessFailure)
        .ToArray();
    if (harnessFailures.Length > 0)
    {
        Console.Error.WriteLine(
            "Parity harness failures detected; refusing to reinterpret runner/reflection failures as SQL capability differences:");
        foreach (var failure in harnessFailures)
        {
            Console.Error.WriteLine(
                $"  - {failure.Name}: {failure.ExceptionType}: {failure.Message}");
        }

        loadContext.Unload();
        return 1;
    }

    loadContext.Unload();
    return 0;
}

static int VerifyNegative(string assemblyPath, string corpusPath)
{
    corpusPath = Path.GetFullPath(corpusPath);
    var corpus = JsonSerializer.Deserialize<List<CorpusCase>>(
        File.ReadAllText(corpusPath),
        JsonOptions()) ?? throw new InvalidOperationException("Negative syntax corpus is empty.");

    var temporary = Path.Combine(
        Path.GetTempPath(),
        "sql-negative-" + Guid.NewGuid().ToString("N") + ".json");

    try
    {
        var runResult = RunAssembly(assemblyPath, corpusPath, temporary);
        if (runResult != 0)
            return runResult;

        var outcomes = ReadOutcomes(temporary);
        var violations = new List<string>();

        foreach (var item in corpus)
        {
            if (!outcomes.TryGetValue(item.Name, out var outcome))
            {
                violations.Add($"{item.Name}: no outcome was captured");
                continue;
            }

            if (outcome.Success)
            {
                violations.Add($"{item.Name}: expected fail-closed behavior but compilation succeeded");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ExpectedStage))
            {
                violations.Add($"{item.Name}: negative corpus case does not declare expectedStage");
            }
            else if (!string.Equals(
                         outcome.Stage,
                         item.ExpectedStage,
                         StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{item.Name}: expected stage {item.ExpectedStage}, actual {outcome.Stage} " +
                    $"({outcome.ExceptionType}: {outcome.Message})");
            }

            if (!string.IsNullOrWhiteSpace(item.ExceptionTypeContains)
                && (outcome.ExceptionType?.Contains(
                        item.ExceptionTypeContains,
                        StringComparison.OrdinalIgnoreCase) != true))
            {
                violations.Add(
                    $"{item.Name}: expected exception type containing '{item.ExceptionTypeContains}', " +
                    $"actual '{outcome.ExceptionType}'");
            }

            foreach (var fragment in item.MessageContains ?? [])
            {
                if (outcome.Message?.Contains(fragment, StringComparison.OrdinalIgnoreCase) != true)
                {
                    violations.Add(
                        $"{item.Name}: expected diagnostic containing '{fragment}', " +
                        $"actual '{outcome.Message}'");
                }
            }

            if (item.RequireTypedDiagnostic)
            {
                var actualContract =
                    $"actual code={outcome.DiagnosticCode ?? "<none>"}, " +
                    $"stage={outcome.DiagnosticStage ?? "<none>"}, " +
                    $"category={outcome.DiagnosticCategory ?? "<none>"}, " +
                    $"span={outcome.DiagnosticSpanStart?.ToString() ?? "<none>"}+" +
                    $"{outcome.DiagnosticSpanLength?.ToString() ?? "<none>"}";

                if (string.IsNullOrWhiteSpace(item.ExpectedDiagnosticCode))
                    violations.Add($"{item.Name}: typed diagnostic contract does not declare expectedDiagnosticCode; {actualContract}");

                if (string.IsNullOrWhiteSpace(item.ExpectedDiagnosticStage))
                    violations.Add($"{item.Name}: typed diagnostic contract does not declare expectedDiagnosticStage; {actualContract}");

                if (string.IsNullOrWhiteSpace(item.ExpectedDiagnosticCategory))
                    violations.Add($"{item.Name}: typed diagnostic contract does not declare expectedDiagnosticCategory; {actualContract}");

                if (outcome.DiagnosticCode is null
                    || outcome.DiagnosticStage is null
                    || outcome.DiagnosticCategory is null)
                {
                    violations.Add($"{item.Name}: failure did not expose a complete typed diagnostic; {actualContract}");
                }

                if (outcome.DiagnosticSpanStart is null
                    || outcome.DiagnosticSpanLength is null
                    || outcome.DiagnosticSpanStart < 0
                    || outcome.DiagnosticSpanLength < 0)
                {
                    violations.Add($"{item.Name}: failure did not expose a concrete typed diagnostic span; {actualContract}");
                }
            }

            if (!string.IsNullOrWhiteSpace(item.ExpectedDiagnosticCode)
                && !string.Equals(outcome.DiagnosticCode, item.ExpectedDiagnosticCode, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{item.Name}: expected diagnostic code '{item.ExpectedDiagnosticCode}', " +
                    $"actual '{outcome.DiagnosticCode ?? "<none>"}'");
            }

            if (!string.IsNullOrWhiteSpace(item.ExpectedDiagnosticStage)
                && !string.Equals(outcome.DiagnosticStage, item.ExpectedDiagnosticStage, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{item.Name}: expected diagnostic stage '{item.ExpectedDiagnosticStage}', " +
                    $"actual '{outcome.DiagnosticStage ?? "<none>"}'");
            }

            if (!string.IsNullOrWhiteSpace(item.ExpectedDiagnosticCategory)
                && !string.Equals(outcome.DiagnosticCategory, item.ExpectedDiagnosticCategory, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(
                    $"{item.Name}: expected diagnostic category '{item.ExpectedDiagnosticCategory}', " +
                    $"actual '{outcome.DiagnosticCategory ?? "<none>"}'");
            }

            if (item.RequireDiagnosticSpan
                && (outcome.DiagnosticSpanStart is null
                    || outcome.DiagnosticSpanLength is null
                    || outcome.DiagnosticSpanStart < 0
                    || outcome.DiagnosticSpanLength < 0))
            {
                violations.Add(
                    $"{item.Name}: expected a concrete typed diagnostic source span, " +
                    $"actual start={outcome.DiagnosticSpanStart?.ToString() ?? "<none>"}, " +
                    $"length={outcome.DiagnosticSpanLength?.ToString() ?? "<none>"}");
            }
        }

        if (violations.Count == 0)
        {
            Console.WriteLine(
                $"Negative syntax contract: {corpus.Count} cases failed closed at their declared stages.");
            return 0;
        }

        Console.Error.WriteLine("Negative syntax contract violations detected:");
        foreach (var violation in violations)
            Console.Error.WriteLine($"  - {violation}");
        return 1;
    }
    finally
    {
        if (File.Exists(temporary))
            File.Delete(temporary);
    }
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
        else if (mainOutcome.Success && headOutcome.Success
                 && SemanticRegression(mainOutcome.Semantic, headOutcome.Semantic) is { } semanticRegression)
        {
            var detail = $"{name}: semantic compatibility floor regressed: {semanticRegression}";
            if (allowList.Contains(name))
                intentional.Add(detail);
            else
                regressions.Add(detail);
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

static string? SemanticRegression(
    SemanticSignature? main,
    SemanticSignature? head)
{
    if (main is null || head is null)
        return "successful outcome did not expose a semantic signature";

    if (!string.Equals(main.StatementFamily, head.StatementFamily, StringComparison.Ordinal))
        return $"statement family changed from {main.StatementFamily} to {head.StatementFamily}";

    if (head.CteCount < main.CteCount)
        return $"CTE count shrank from {main.CteCount} to {head.CteCount}";
    if (head.SetOperationCount < main.SetOperationCount)
        return $"set-operation count shrank from {main.SetOperationCount} to {head.SetOperationCount}";
    if (head.SubqueryCount < main.SubqueryCount)
        return $"subquery count shrank from {main.SubqueryCount} to {head.SubqueryCount}";

    var headTables = head.NamedTableReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var missingTables = main.NamedTableReferences
        .Where(table => !headTables.Contains(table))
        .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (missingTables.Length > 0)
        return "named table/source references were dropped: " + string.Join(", ", missingTables);

    if (!string.Equals(main.CommandKind, head.CommandKind, StringComparison.Ordinal))
        return $"compiled command kind changed from {main.CommandKind ?? "<none>"} to {head.CommandKind ?? "<none>"}";

    if (main.ReturnsRows.HasValue
        && head.ReturnsRows.HasValue
        && main.ReturnsRows.Value != head.ReturnsRows.Value)
    {
        return $"ReturnsRows changed from {main.ReturnsRows.Value} to {head.ReturnsRows.Value}";
    }

    return null;
}

static SemanticSignature CaptureSemanticSignature(object parsed, object command)
{
    var statement = parsed.GetType()
        .GetProperty("Statement", BindingFlags.Public | BindingFlags.Instance)
        ?.GetValue(parsed)
        ?? throw new InvalidOperationException("ParsedStatement.Statement was not available.");

    var state = new SemanticWalkState();
    WalkSemantic(statement, state, 0);

    var commandType = command.GetType();
    var commandKind = commandType
        .GetProperty("Kind", BindingFlags.Public | BindingFlags.Instance)
        ?.GetValue(command)
        ?.ToString();
    var returnsRowsValue = commandType
        .GetProperty("ReturnsRows", BindingFlags.Public | BindingFlags.Instance)
        ?.GetValue(command);
    bool? returnsRows = returnsRowsValue is bool value ? value : null;

    return new SemanticSignature(
        StatementFamily(statement.GetType().Name),
        state.CteCount,
        state.SetOperationCount,
        state.SubqueryCount,
        state.NamedTables.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
        commandKind,
        returnsRows);
}

static string StatementFamily(string typeName) =>
    typeName switch
    {
        "SelectStatement" or "QueryStatement" => "Query",
        "InsertStatement" => "Insert",
        "UpdateStatement" => "Update",
        "DeleteStatement" => "Delete",
        "MergeStatement" => "Merge",
        _ => typeName
    };

static void WalkSemantic(object? value, SemanticWalkState state, int depth)
{
    if (value is null || depth > 96 || value is string)
        return;

    if (value is System.Collections.IEnumerable sequence)
    {
        foreach (var item in sequence)
            WalkSemantic(item, state, depth + 1);
        return;
    }

    var type = value.GetType();
    if (type.IsPrimitive
        || type.IsEnum
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan)
        || type == typeof(Guid))
    {
        return;
    }

    var typeName = type.Name;
    if (typeName == "CteDefinition")
        state.CteCount++;
    if (typeName == "SetOperation")
        state.SetOperationCount++;
    if (typeName.Contains("Subquery", StringComparison.Ordinal)
        || typeName == "DerivedTableSource"
        || typeName == "InsertQuerySource")
    {
        state.SubqueryCount++;
    }

    if (typeName == "NamedTableSource")
    {
        var identifier = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
        var text = IdentifierText(identifier);
        if (!string.IsNullOrWhiteSpace(text))
            state.NamedTables.Add(text);
    }

    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
        if (!property.CanRead
            || property.GetIndexParameters().Length != 0
            || property.Name is "Span" or "RawSql" or "SourceProfile")
        {
            continue;
        }

        object? child;
        try
        {
            child = property.GetValue(value);
        }
        catch
        {
            continue;
        }

        WalkSemantic(child, state, depth + 1);
    }
}

static string? IdentifierText(object? identifier)
{
    if (identifier is null)
        return null;

    var parts = identifier.GetType()
        .GetProperty("Parts", BindingFlags.Public | BindingFlags.Instance)
        ?.GetValue(identifier) as System.Collections.IEnumerable;
    if (parts is null)
        return identifier.ToString();

    var values = new List<string>();
    foreach (var part in parts)
    {
        var text = part?.GetType()
            .GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(part)
            ?.ToString();
        if (!string.IsNullOrWhiteSpace(text))
            values.Add(text);
    }

    return values.Count == 0 ? null : string.Join(".", values);
}

static bool IsHarnessFailure(Outcome outcome)
{
    if (outcome.Success || string.IsNullOrWhiteSpace(outcome.ExceptionType))
        return false;

    return outcome.ExceptionType is
        "System.MissingMethodException"
        or "System.IndexOutOfRangeException"
        or "System.TypeLoadException"
        or "System.BadImageFormatException"
        or "System.IO.FileNotFoundException"
        or "System.IO.FileLoadException"
        or "System.Reflection.AmbiguousMatchException"
        or "System.Reflection.TargetParameterCountException";
}

static Dictionary<string, Outcome> ReadOutcomes(string path)
{
    var values = JsonSerializer.Deserialize<List<Outcome>>(
        File.ReadAllText(path),
        JsonOptions()) ?? throw new InvalidOperationException($"No outcomes in {path}.");

    return values.ToDictionary(value => value.Name, StringComparer.Ordinal);
}

static object? CreateProviderProfile(
    Type profileType,
    object provider,
    string? versionText,
    int? compatibilityLevel,
    string[]? sessionModes)
{
    var hasVersion = !string.IsNullOrWhiteSpace(versionText);
    var hasSessionModes = sessionModes is not null;
    if (!hasVersion && !compatibilityLevel.HasValue && !hasSessionModes)
        return null;

    Version? version = null;
    if (hasVersion)
    {
        try
        {
            version = Version.Parse(versionText!);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Invalid capability profile version '{versionText}'.",
                exception);
        }
    }

    var modes = sessionModes is null
        ? null
        : new HashSet<string>(
            sessionModes,
            StringComparer.OrdinalIgnoreCase);

    return CreateWithOptionalTail(
        profileType,
        provider,
        version,
        compatibilityLevel,
        modes,
        null);
}

static object? CreatePrimaryKeyAssurance(
    Type assuranceType,
    string[]? columns)
{
    if (columns is null)
        return null;
    if (columns.Length == 0)
        throw new InvalidOperationException(
            "conflictTargetPrimaryKeyColumns cannot be empty when declared.");

    var factory = assuranceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == "FromPrimaryKey")
        .Where(method => method.GetParameters().Length == 1)
        .Where(method => LeadingArgumentsFit(method.GetParameters(), [columns]))
        .SingleOrDefault()
        ?? throw new MissingMethodException(assuranceType.FullName, "FromPrimaryKey");

    return factory.Invoke(null, [columns])
        ?? throw new InvalidOperationException(
            "DmlConflictTargetAssurance.FromPrimaryKey returned null.");
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
        else
        {
            var parameterType = parameters[index].ParameterType;
            var nullableType = Nullable.GetUnderlyingType(parameterType);
            if (!parameterType.IsInstanceOfType(argument)
                && (nullableType is null
                    || !nullableType.IsInstanceOfType(argument)))
            {
                return false;
            }
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

static Outcome FailureOutcome(string name, string stage, Exception exception)
{
    var diagnostic = exception.GetType()
        .GetProperty("Diagnostic", BindingFlags.Public | BindingFlags.Instance)
        ?.GetValue(exception)
        ?? exception.Data["HsSqlAgent.SqlCore.Diagnostic"];

    string? code = null;
    string? diagnosticStage = null;
    string? category = null;
    int? spanStart = null;
    int? spanLength = null;

    if (diagnostic is not null)
    {
        var diagnosticType = diagnostic.GetType();
        code = diagnosticType.GetProperty("Code")?.GetValue(diagnostic)?.ToString();
        diagnosticStage = diagnosticType.GetProperty("Stage")?.GetValue(diagnostic)?.ToString();
        category = diagnosticType.GetProperty("Category")?.GetValue(diagnostic)?.ToString();

        var span = diagnosticType.GetProperty("Span")?.GetValue(diagnostic);
        if (span is not null)
        {
            var spanType = span.GetType();
            spanStart = spanType.GetProperty("Start")?.GetValue(span) as int?;
            spanLength = spanType.GetProperty("Length")?.GetValue(span) as int?;
        }
    }

    return new Outcome(
        name,
        false,
        stage,
        exception.GetType().FullName,
        exception.Message,
        code,
        diagnosticStage,
        category,
        spanStart,
        spanLength);
}

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
        "  SqlSyntaxParityRunner compare <main.json> <head.json> <allowlist.json>\n" +
        "  SqlSyntaxParityRunner verify-negative <HsSqlAgent.SqlCore.dll> <corpus.json>");
    return 2;
}

sealed record CorpusCase(
    string Name,
    string Sql,
    string? Dialect = null,
    string? TargetDialect = null,
    string? SourceVersion = null,
    string? TargetVersion = null,
    int? SourceCompatibilityLevel = null,
    int? TargetCompatibilityLevel = null,
    string[]? SourceSessionModes = null,
    string[]? TargetSessionModes = null,
    string[]? ConflictTargetPrimaryKeyColumns = null,
    string? ExpectedStage = null,
    string? ExceptionTypeContains = null,
    string[]? MessageContains = null,
    string? ExpectedDiagnosticCode = null,
    string? ExpectedDiagnosticStage = null,
    string? ExpectedDiagnosticCategory = null,
    bool RequireDiagnosticSpan = false,
    bool RequireTypedDiagnostic = false);
sealed record SemanticSignature(
    string StatementFamily,
    int CteCount,
    int SetOperationCount,
    int SubqueryCount,
    string[] NamedTableReferences,
    string? CommandKind,
    bool? ReturnsRows);

sealed class SemanticWalkState
{
    public int CteCount { get; set; }
    public int SetOperationCount { get; set; }
    public int SubqueryCount { get; set; }
    public HashSet<string> NamedTables { get; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed record Outcome(
    string Name,
    bool Success,
    string Stage,
    string? ExceptionType,
    string? Message,
    string? DiagnosticCode = null,
    string? DiagnosticStage = null,
    string? DiagnosticCategory = null,
    int? DiagnosticSpanStart = null,
    int? DiagnosticSpanLength = null,
    SemanticSignature? Semantic = null);

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
