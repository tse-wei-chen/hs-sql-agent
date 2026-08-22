using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlTranslation.Context;
using SqlAgent.Service.SqlTranslation.Diagnostics;
using SqlAgent.Service.SqlTranslation.Functions;
using SqlAgent.Service.SqlTranslation.Normalization;
using Xunit;

namespace SqlAgent.Test.Services;

public class SqlSemanticNormalizerTests
{
    [Fact]
    public void Normalize_ShouldResolveSemanticFunctionWithoutMutatingSource()
    {
        var normalizer = CreateNormalizer();
        var source = new FunctionSelectCondition
        {
            FunctionName = "LEN",
            Arguments = [new FieldSelectCondition { FieldName = "name" }]
        };

        var result = normalizer.Normalize(source, new(
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres));

        var translated = Assert.IsType<FunctionSelectCondition>(result.Expression);
        Assert.Equal("LENGTH", translated.FunctionName);
        Assert.Equal("LEN", source.FunctionName);
        Assert.NotSame(source, translated);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Normalize_ShouldWarnAndPreserveUnknownFunctionWhenExplicitlyRequested()
    {
        var normalizer = CreateNormalizer();
        var source = new FunctionSelectCondition { FunctionName = "MY_UDF", Arguments = [] };

        var result = normalizer.Normalize(source, new(
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres,
            UnknownFunctionPolicy.WarnAndPassthrough));

        var translated = Assert.IsType<FunctionSelectCondition>(result.Expression);
        Assert.Equal("MY_UDF", translated.FunctionName);
        Assert.NotSame(source, translated);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("SQLFUNC001", diagnostic.Code);
        Assert.Equal(FunctionPortability.Unknown, diagnostic.Portability);
    }

    [Fact]
    public void Normalize_ShouldRejectUnknownFunctionByDefault()
    {
        var normalizer = CreateNormalizer();
        var source = new FunctionSelectCondition { FunctionName = "MY_UDF", Arguments = [] };

        Assert.Throws<InvalidOperationException>(() => normalizer.Normalize(source, new(
            SqlAgentToolType.MsSqlServer, SqlAgentToolType.Postgres)));
    }

    [Fact]
    public void Normalize_ShouldEnforceArgumentArity()
    {
        var normalizer = CreateNormalizer();
        var source = new FunctionSelectCondition { FunctionName = "LEN", Arguments = [] };

        Assert.Throws<InvalidOperationException>(() => normalizer.Normalize(source, new(
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres,
            UnknownFunctionPolicy.Throw)));
    }

    private static SqlSemanticNormalizer CreateNormalizer() => new(new FunctionRegistry(
    [
        new()
        {
            Dialect = SqlAgentToolType.MsSqlServer,
            Name = "LEN",
            Semantic = SemanticFunction.StringLength,
            MinArguments = 1,
            MaxArguments = 1,
            TranslationKind = FunctionTranslationKind.Semantic
        },
        new()
        {
            Dialect = SqlAgentToolType.Postgres,
            Name = "LENGTH",
            Semantic = SemanticFunction.StringLength,
            MinArguments = 1,
            MaxArguments = 1,
            TranslationKind = FunctionTranslationKind.Rename
        }
    ]));

    [Fact]
    public void EmbeddedDefinitions_ShouldProvideStringLengthForEveryDialect()
    {
        var registry = new FunctionRegistry(FunctionDefinitionLoader.LoadEmbedded());

        foreach (var dialect in Enum.GetValues<SqlAgentToolType>())
        {
            var definition = registry.Find(dialect, SemanticFunction.StringLength, 1);
            Assert.NotNull(definition);
            Assert.True(definition.AcceptsArgumentCount(1));
            Assert.False(definition.AcceptsArgumentCount(0));
            Assert.NotNull(registry.Find(dialect, SemanticFunction.Ceiling, 1));
        }
    }

    [Fact]
    public void Registry_ShouldRejectOverlappingArityDefinitions()
    {
        var definitions = new FunctionDefinition[]
        {
            new() { Dialect = SqlAgentToolType.Postgres, Name = "F", MinArguments = 1, MaxArguments = 2, TranslationKind = FunctionTranslationKind.Identity },
            new() { Dialect = SqlAgentToolType.Postgres, Name = "F", MinArguments = 2, MaxArguments = 3, TranslationKind = FunctionTranslationKind.Identity }
        };

        Assert.Throws<InvalidOperationException>(() => new FunctionRegistry(definitions));
    }

    [Fact]
    public void Registry_ShouldCompileAndValidateTemplatesAtConstruction()
    {
        var invalidReference = new FunctionDefinition
        {
            Dialect = SqlAgentToolType.Postgres,
            Name = "F",
            MinArguments = 1,
            MaxArguments = 1,
            TranslationKind = FunctionTranslationKind.Template,
            Template = "$2"
        };
        var unknownModifier = invalidReference with { Template = "$1:not_registered" };

        Assert.Throws<InvalidOperationException>(() => new FunctionRegistry([invalidReference]));
        Assert.Throws<FormatException>(() => new FunctionRegistry([unknownModifier]));
    }
}
