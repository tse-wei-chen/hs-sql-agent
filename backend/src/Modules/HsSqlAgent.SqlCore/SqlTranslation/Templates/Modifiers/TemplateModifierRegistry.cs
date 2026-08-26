namespace HsSqlAgent.SqlCore.SqlTranslation.Templates.Modifiers;

public interface ITemplateModifier
{
    string Name { get; }
    SelectCondition Apply(
        SelectCondition node,
        IReadOnlyList<SelectCondition> arguments,
        TranslationContext context);
}

public interface ITemplateModifierRegistry
{
    ITemplateModifier Get(string name);
}

public sealed class TemplateModifierRegistry(IEnumerable<ITemplateModifier> modifiers) : ITemplateModifierRegistry
{
    private readonly IReadOnlyDictionary<string, ITemplateModifier> _modifiers = modifiers.ToDictionary(
        modifier => modifier.Name,
        StringComparer.OrdinalIgnoreCase);

    public ITemplateModifier Get(string name) =>
        _modifiers.TryGetValue(name, out var modifier)
            ? modifier
            : throw new FormatException($"Unknown function-template modifier '{name}'.");

    public static TemplateModifierRegistry CreateDefault() => new([new DateFormatModifier(new DateFormatTranslator())]);
}

public sealed class DateFormatModifier(DateFormatTranslator translator) : ITemplateModifier
{
    public string Name => "date_format";

    public SelectCondition Apply(
        SelectCondition node,
        IReadOnlyList<SelectCondition> arguments,
        TranslationContext context)
    {
        if (arguments.Count != 0)
            throw new FormatException("The date_format modifier gets its dialects from TranslationContext and accepts no arguments.");
        if (node is not ConstantSelectCondition { Constant: string value })
            throw new FormatException("The date_format modifier requires a string constant.");

        return new ConstantSelectCondition
        {
            Alias = node.Alias,
            Constant = translator.Translate(value, context.SourceDialect, context.TargetDialect)
        };
    }
}
