using SqlAgent.Service.Enums;

namespace SqlAgent.Service.SqlTranslation.Templates.Ast;

public abstract record TemplateExpression;

public sealed record TemplateArgumentReferenceExpression(
    int Index,
    string? Modifier,
    IReadOnlyList<TemplateExpression> ModifierArguments) : TemplateExpression;

public sealed record TemplateSqlTokenExpression(string Token) : TemplateExpression;
public sealed record TemplateConstantExpression(object Value) : TemplateExpression;
public sealed record TemplateIntervalExpression(string Literal) : TemplateExpression;

public sealed record TemplateOperationExpression(
    TemplateExpression Left,
    ArithmeticOperator Operator,
    TemplateExpression Right) : TemplateExpression;

public sealed record TemplateFunctionExpression(
    string Name,
    IReadOnlyList<TemplateExpression> Arguments) : TemplateExpression;

public sealed record TemplateCastExpression(
    TemplateExpression Expression,
    string TypeName) : TemplateExpression;

public sealed record TemplateExtractExpression(
    TemplateExpression Unit,
    TemplateExpression Expression) : TemplateExpression;

public sealed record TemplateCaseBranch(
    TemplateExpression Condition,
    TemplateExpression Value);

public sealed record TemplateCaseExpression(
    IReadOnlyList<TemplateCaseBranch> Cases,
    TemplateExpression? ElseExpression) : TemplateExpression;
