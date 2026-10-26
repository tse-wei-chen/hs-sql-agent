using System;
using System.Linq;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreFunctionParameterBindingTests
{
    private static SqlScriptAnalysisResult Analyze(string sql) =>
        SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres, SqlCatalogState.KnownEmpty());

    [Theory]
    [InlineData("amount integer", "integer", "amount", SqlFunctionReturnType.Integer)]
    [InlineData("counter bigint", "bigint", "counter", SqlFunctionReturnType.BigInteger)]
    [InlineData("enabled boolean", "boolean", "enabled", SqlFunctionReturnType.Boolean)]
    [InlineData("message text", "text", "message", SqlFunctionReturnType.Text)]
    public void NamedSymbolsBindToExactReturnAst(
        string argument, string declared, string name, SqlFunctionReturnType expected)
    {
        var sql = "CREATE FUNCTION app.identity(" + argument + ") RETURNS " + declared +
            " LANGUAGE SQL AS $body$ SELECT " + name + "; $body$;";
        var report = Analyze(sql);
        var definition = Assert.Single(report.ProceduralDefinitions);
        var symbol = Assert.Single(definition.Parameters);
        Assert.Equal(1, symbol.Ordinal);
        Assert.Equal(name, symbol.Name);
        Assert.Equal(expected, symbol.DeclaredType);
        Assert.Equal(SqlCatalogKnowledge.Unknown, symbol.RuntimeCertainty);
        Assert.Equal(SqlFunctionReturnEvidence.ParameterSymbolMatch, definition.FunctionReturn!.Evidence);
        Assert.Equal(expected, definition.FunctionReturn.DeclaredType);
        Assert.False(definition.FunctionReturn.IsRuntimeReturnProven);
        Assert.Equal(SqlStatementEffectOperation.DefineFunction, Assert.Single(report.DetailedEffects).Operation);
        Assert.Empty(report.Diagnostics);
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Fact]
    public void MultipleParametersHaveOrderedImmutableSymbols()
    {
        var report = Analyze("CREATE OR REPLACE FUNCTION app.choose(first_arg integer, second_arg boolean) " +
            "RETURNS boolean AS $b$ SELECT second_arg; $b$ LANGUAGE SQL;");
        var d = Assert.Single(report.ProceduralDefinitions);
        Assert.Equal(2, d.Parameters.Count);
        Assert.Equal("first_arg", d.Parameters[0].Name);
        Assert.Equal(1, d.Parameters[0].Ordinal);
        Assert.Equal(SqlFunctionReturnType.Integer, d.Parameters[0].DeclaredType);
        Assert.Equal("second_arg", d.Parameters[1].Name);
        Assert.Equal(2, d.Parameters[1].Ordinal);
        Assert.Equal(SqlFunctionReturnType.Boolean, d.Parameters[1].DeclaredType);
        Assert.Equal(SqlFunctionReturnEvidence.ParameterSymbolMatch, d.FunctionReturn!.Evidence);
    }

    [Fact]
    public void DefinedButUnusedParameterStillCanReturnConstant()
    {
        var report = Analyze("CREATE FUNCTION app.constant(ignored_value text) RETURNS integer " +
            "LANGUAGE SQL AS $b$SELECT 7;$b$;");
        Assert.Single(Assert.Single(report.ProceduralDefinitions).Parameters);
        Assert.Equal(SqlFunctionReturnEvidence.LiteralMatch,
            Assert.Single(report.ProceduralDefinitions).FunctionReturn!.Evidence);
    }

    [Theory]
    [InlineData("arg integer", "text", "SELECT arg;")]
    [InlineData("arg integer", "integer", "SELECT missing;")]
    [InlineData("arg integer", "integer", "SELECT arg FROM app.orders;")]
    [InlineData("arg integer", "integer", "SELECT arg, 1;")]
    [InlineData("arg integer", "integer", "UPDATE app.orders SET id=1; SELECT arg;")]
    [InlineData("arg integer", "integer", "SELECT arg WHERE true;")]
    public void AmbiguousOrUnprovenParameterUsageFailsClosed(string args, string resultType, string body)
    {
        var report = Analyze("CREATE FUNCTION app.bad(" + args + ") RETURNS " + resultType +
            " LANGUAGE SQL AS $b$" + body + "$b$;");
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
        Assert.Contains(report.Diagnostics, d => d.Code == "SQL_PROCEDURAL_RETURN_UNPROVEN"
            || d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
        Assert.False(report.FinalCatalog.IsClosedWorld);
    }

    [Theory]
    [InlineData("arg integer, arg integer")]
    [InlineData("arg INTEGER, ARG integer")]
    [InlineData("arg integer,")]
    [InlineData("integer")]
    [InlineData("arg void")]
    [InlineData("arg numeric")]
    [InlineData("OUT arg integer")]
    [InlineData("INOUT arg integer")]
    [InlineData("arg integer DEFAULT 1")]
    [InlineData("arg integer = 1")]
    [InlineData("\"arg\" integer")]
    [InlineData("a integer,b integer,c integer,d integer,e integer,f integer,g integer,h integer,i integer")]
    public void UnsupportedArgumentDeclarationsDoNotEmitSymbols(string args)
    {
        var report = Analyze("CREATE FUNCTION app.bad(" + args +
            ") RETURNS integer LANGUAGE SQL AS $b$SELECT 1;$b$;");
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
        Assert.Contains(report.Diagnostics, d => d.Code == "SQL_PROCEDURAL_UNSUPPORTED");
    }

    [Fact]
    public void PositionalParametersNeverBypassStrictSourceLexer()
    {
        var report = Analyze("CREATE FUNCTION app.pos(arg integer) RETURNS integer LANGUAGE SQL AS $b$SELECT $1;$b$;");
        Assert.Empty(report.ProceduralDefinitions);
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported, Assert.Single(report.Statements).Status);
        Assert.Contains(report.Diagnostics, d => d.Code == "SQL_PROCEDURAL_BODY_UNSUPPORTED");
    }

    [Fact]
    public void SymbolsAreIsolatedBetweenDefinitions()
    {
        var report = Analyze(
            "CREATE FUNCTION app.primary_fn(arg integer) RETURNS integer LANGUAGE SQL AS $a$SELECT arg;$a$;" +
            "CREATE FUNCTION app.second(value_name text) RETURNS text LANGUAGE SQL AS $b$SELECT value_name;$b$;");
        Assert.Equal(2, report.ProceduralDefinitions.Count);
        Assert.Equal("arg", Assert.Single(report.ProceduralDefinitions[0].Parameters).Name);
        Assert.Equal("value_name", Assert.Single(report.ProceduralDefinitions[1].Parameters).Name);
    }

    [Fact]
    public void ExistingZeroParameterDefinitionsRemainCompatible()
    {
        var report = Analyze(
            "CREATE PROCEDURE app.p() LANGUAGE SQL AS $a$SELECT 1;$a$;" +
            "CREATE FUNCTION app.f() RETURNS integer LANGUAGE SQL AS $b$SELECT 1;$b$;");
        Assert.Equal(2, report.ProceduralDefinitions.Count);
        Assert.All(report.ProceduralDefinitions, d => Assert.Empty(d.Parameters));
        Assert.Null(report.ProceduralDefinitions[0].FunctionReturn);
        Assert.Equal(SqlFunctionReturnEvidence.LiteralMatch,
            report.ProceduralDefinitions[1].FunctionReturn!.Evidence);
    }
}
