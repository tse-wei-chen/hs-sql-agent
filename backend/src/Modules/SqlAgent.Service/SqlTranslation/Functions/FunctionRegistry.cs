using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.SqlTranslation.Templates.Ast;
using SqlAgent.Service.SqlTranslation.Templates.Modifiers;

namespace SqlAgent.Service.SqlTranslation.Functions;

public interface IFunctionRegistry
{
    FunctionDefinition? Find(SqlAgentToolType dialect, string functionName, int argumentCount);
    FunctionDefinition? Find(SqlAgentToolType dialect, SemanticFunction semantic, int argumentCount);
}

public sealed class FunctionRegistry : IFunctionRegistry
{
    private readonly IReadOnlyList<FunctionDefinition> _definitions;
    private readonly Dictionary<(SqlAgentToolType Dialect, string Name), IReadOnlyList<FunctionDefinition>> _byName;

    public FunctionRegistry(IEnumerable<FunctionDefinition> definitions)
    {
        _definitions = definitions?.ToArray() ?? throw new ArgumentNullException(nameof(definitions));
        ValidateDefinitions(_definitions);
        _byName = _definitions
            .SelectMany(definition => definition.Aliases.Prepend(definition.Name)
                .Select(name => (definition, name: NormalizeName(name))))
            .GroupBy(item => (item.definition.Dialect, item.name))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<FunctionDefinition>)group.Select(item => item.definition).ToArray());
    }

    public FunctionDefinition? Find(SqlAgentToolType dialect, string functionName, int argumentCount)
    {
        if (!_byName.TryGetValue((dialect, NormalizeName(functionName)), out var candidates))
            return null;

        return candidates.FirstOrDefault(candidate => candidate.AcceptsArgumentCount(argumentCount));
    }

    public FunctionDefinition? Find(SqlAgentToolType dialect, SemanticFunction semantic, int argumentCount) =>
        _definitions.FirstOrDefault(definition =>
            definition.Dialect == dialect
            && definition.Semantic == semantic
            && definition.AcceptsArgumentCount(argumentCount));

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Function name cannot be empty.", nameof(name));

        return name.Trim().ToUpperInvariant();
    }

    private static void ValidateDefinitions(IReadOnlyList<FunctionDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            var name = NormalizeName(definition.Name);
            if (definition.MinArguments < 0)
                throw new InvalidOperationException($"Function '{name}' has a negative minimum arity.");
            if (definition.MaxArguments is { } max && max < definition.MinArguments)
                throw new InvalidOperationException($"Function '{name}' has an invalid arity range.");
            if (definition.Aliases.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException($"Function '{name}' has an empty alias.");
            var ownNames = definition.Aliases.Prepend(definition.Name).Select(NormalizeName).ToArray();
            if (ownNames.Distinct(StringComparer.Ordinal).Count() != ownNames.Length)
                throw new InvalidOperationException($"Function '{name}' declares a duplicate name or alias.");
            if (definition.TranslationKind == FunctionTranslationKind.Template)
            {
                if (string.IsNullOrWhiteSpace(definition.Template))
                    throw new InvalidOperationException($"Template function '{name}' has no template.");
                var ast = new FunctionTemplateEngine(definition.Template).Parse()
                    ?? throw new InvalidOperationException($"Template function '{name}' has an empty template.");
                ValidateTemplate(ast, definition, TemplateModifierRegistry.CreateDefault());
            }
            else if (definition.Template is not null)
                throw new InvalidOperationException($"Non-template function '{name}' must not declare a template.");
            if (definition.TranslationKind == FunctionTranslationKind.Specialized
                && string.IsNullOrWhiteSpace(definition.Translator))
                throw new InvalidOperationException($"Specialized function '{name}' has no translator.");
        }

        var entries = definitions.SelectMany(definition =>
            definition.Aliases.Prepend(definition.Name).Select(name => (definition, name: NormalizeName(name))));
        foreach (var group in entries.GroupBy(entry => (entry.definition.Dialect, entry.name)))
        {
            var candidates = group.Select(entry => entry.definition).ToArray();
            for (var i = 0; i < candidates.Length; i++)
            for (var j = i + 1; j < candidates.Length; j++)
                if (RangesOverlap(candidates[i], candidates[j]))
                    throw new InvalidOperationException(
                        $"Overlapping function definitions for {group.Key.Dialect} '{group.Key.name}'.");
        }

        foreach (var group in definitions.Where(d => d.Semantic is not null)
                     .GroupBy(d => (d.Dialect, d.Semantic)))
        {
            var candidates = group.ToArray();
            for (var i = 0; i < candidates.Length; i++)
            for (var j = i + 1; j < candidates.Length; j++)
                if (RangesOverlap(candidates[i], candidates[j]))
                    throw new InvalidOperationException(
                        $"Overlapping semantic definitions for {group.Key.Dialect} '{group.Key.Semantic}'.");
        }
    }

    private static bool RangesOverlap(FunctionDefinition left, FunctionDefinition right) =>
        left.MinArguments <= (right.MaxArguments ?? int.MaxValue)
        && right.MinArguments <= (left.MaxArguments ?? int.MaxValue);

    private static void ValidateTemplate(
        TemplateExpression expression,
        FunctionDefinition definition,
        ITemplateModifierRegistry modifiers)
    {
        switch (expression)
        {
            case TemplateArgumentReferenceExpression reference:
                // Template AST stores the user-facing $1 reference as zero-based index 0.
                if (reference.Index < 0 || definition.MaxArguments is { } max && reference.Index >= max)
                    throw new InvalidOperationException(
                        $"Template for '{definition.Name}' references unavailable argument ${reference.Index + 1}.");
                if (reference.Modifier is not null) _ = modifiers.Get(reference.Modifier);
                foreach (var argument in reference.ModifierArguments)
                    ValidateTemplate(argument, definition, modifiers);
                break;
            case TemplateOperationExpression operation:
                ValidateTemplate(operation.Left, definition, modifiers);
                ValidateTemplate(operation.Right, definition, modifiers);
                break;
            case TemplateFunctionExpression function:
                foreach (var argument in function.Arguments) ValidateTemplate(argument, definition, modifiers);
                break;
            case TemplateCastExpression cast:
                ValidateTemplate(cast.Expression, definition, modifiers);
                break;
            case TemplateExtractExpression extract:
                ValidateTemplate(extract.Unit, definition, modifiers);
                ValidateTemplate(extract.Expression, definition, modifiers);
                break;
            case TemplateCaseExpression @case:
                foreach (var branch in @case.Cases)
                {
                    ValidateTemplate(branch.Condition, definition, modifiers);
                    ValidateTemplate(branch.Value, definition, modifiers);
                }
                if (@case.ElseExpression is not null)
                    ValidateTemplate(@case.ElseExpression, definition, modifiers);
                break;
        }
    }
}
