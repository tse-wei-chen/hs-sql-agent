using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlTranslation.Diagnostics;

namespace HsSqlAgent.SqlCore.SqlTranslation.Context;

public sealed record TranslationContext(
    SqlAgentToolType SourceDialect,
    SqlAgentToolType TargetDialect,
    UnknownFunctionPolicy UnknownFunctionPolicy = UnknownFunctionPolicy.Throw);
