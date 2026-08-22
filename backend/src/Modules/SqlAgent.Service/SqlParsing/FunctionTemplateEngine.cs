using SqlAgent.Service.Models;
using SqlAgent.Service.SqlTranslation.Context;
using SqlAgent.Service.SqlTranslation.Templates.Ast;
using SqlAgent.Service.SqlTranslation.Templates.Modifiers;
using SqlAgent.Service.SqlTranslation.Templates.Parsing;
using SqlAgent.Service.SqlTranslation.Templates.Resolution;

namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Facade for parsing and resolving lightweight function templates.
/// </summary>
public sealed class FunctionTemplateEngine
{
    private readonly TemplateParser _parser;
    private readonly TemplateArgumentResolver _resolver;

    public FunctionTemplateEngine(string template, ITemplateModifierRegistry? modifierRegistry = null)
    {
        _parser = new TemplateParser(template);
        _resolver = new TemplateArgumentResolver(
            modifierRegistry ?? TemplateModifierRegistry.CreateDefault());
    }

    public TemplateExpression? Parse() => _parser.Parse();

    public SelectCondition? Translate(IList<SelectCondition>? sourceArgs) =>
        Translate(sourceArgs, null);

    public SelectCondition? Translate(
        IList<SelectCondition>? sourceArgs,
        TranslationContext? context)
    {
        var template = Parse();
        return template is null ? null : _resolver.Resolve(template, sourceArgs, context);
    }
}
