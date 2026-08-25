using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlTranslation.Diagnostics;

namespace SqlAgent.Service.SqlTranslation.Context;

public sealed record TranslationContext(
    SqlAgentToolType SourceDialect,
    SqlAgentToolType TargetDialect,
    UnknownFunctionPolicy UnknownFunctionPolicy = UnknownFunctionPolicy.Throw);
