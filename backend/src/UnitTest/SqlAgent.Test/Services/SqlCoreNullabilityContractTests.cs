using System.Linq;
using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class SqlCoreNullabilityContractTests
{
    private static readonly NullabilityInfoContext Nullability = new();

    [Fact]
    public void ConnectionDtos_PreserveV11010OptionalConnectionFields()
    {
        foreach (var propertyName in new[]
                 {
                     nameof(BuildDbConnectionModelBase.Host),
                     nameof(BuildDbConnectionModelBase.Port),
                     nameof(BuildDbConnectionModelBase.Username),
                     nameof(BuildDbConnectionModelBase.Password),
                     nameof(BuildDbConnectionModelBase.Database),
                     nameof(BuildDbConnectionModelBase.ExtraSettings)
                 })
        {
            var property = typeof(BuildDbConnectionModelBase).GetProperty(propertyName)!;
            Assert.Equal(NullabilityState.Nullable, Nullability.Create(property).WriteState);
        }

        var provider = typeof(BuildDbConnectionModel).GetProperty(nameof(BuildDbConnectionModel.Provider))!;
        Assert.Equal(NullabilityState.NotNull, Nullability.Create(provider).WriteState);
    }

    [Fact]
    public void LegacyDtoOptionalMembers_PreserveV11010NullableMetadata()
    {
        AssertNullableProperty<QueryDefinition>(nameof(QueryDefinition.FromQuery));
        AssertNullableProperty<QueryDefinition>(nameof(QueryDefinition.Alias));
        AssertNullableProperty<QueryDefinition>(nameof(QueryDefinition.SelectColumns));
        AssertNullableProperty<DmlDefinition>(nameof(DmlDefinition.WhereConditions));
        AssertNullableProperty<DmlDefinition>(nameof(DmlDefinition.ConfirmToken));
        AssertNullableProperty<SelectCondition>(nameof(SelectCondition.Alias));
        AssertNullableProperty<FunctionSelectCondition>(nameof(FunctionSelectCondition.Arguments));
        AssertNullableProperty<FunctionSelectCondition>(nameof(FunctionSelectCondition.Window));
        AssertNullableProperty<ExpressionWhereCondition>(nameof(ExpressionWhereCondition.RightExpression));
        AssertNullableProperty<SubQueryWhereCondition>(nameof(SubQueryWhereCondition.FieldName));
        AssertNullableProperty<WindowFrameDefinition>(nameof(WindowFrameDefinition.End));
        AssertNullableProperty<JoinCondition>(nameof(JoinCondition.SubQuery));
        AssertNullableProperty<JoinCondition>(nameof(JoinCondition.Alias));
        AssertNullableProperty<NameValuePair>(nameof(NameValuePair.Value));
        AssertNullableProperty<TestDbConnectionVM>(nameof(TestDbConnectionVM.ErrorMessage));
    }

    [Fact]
    public void CompatibilityAst_DoesNotMakeEveryExpressionOrIdentifierNullable()
    {
        var identifierCtor = typeof(SqlIdentifier)
            .GetConstructors()
            .Single(c => c.GetParameters().Length == 2);
        var parts = Nullability.Create(identifierCtor.GetParameters()[0]);
        Assert.Equal(NullabilityState.NotNull, parts.ReadState);
        Assert.Equal(NullabilityState.NotNull, Assert.Single(parts.GenericTypeArguments).ReadState);

        var selectCtor = typeof(SelectStatement).GetConstructors().Single();
        var parameters = selectCtor.GetParameters();
        Assert.Equal(NullabilityState.Nullable, Nullability.Create(parameters.Single(p => p.Name == "fromSource")).ReadState);
        Assert.Equal(NullabilityState.Nullable, Nullability.Create(parameters.Single(p => p.Name == "whereExpr")).ReadState);
        Assert.Equal(NullabilityState.Nullable, Nullability.Create(parameters.Single(p => p.Name == "having")).ReadState);

        var groupBy = Nullability.Create(parameters.Single(p => p.Name == "groupBy"));
        Assert.Equal(NullabilityState.NotNull, groupBy.ReadState);
        Assert.Equal(NullabilityState.NotNull, Assert.Single(groupBy.GenericTypeArguments).ReadState);
    }

    [Fact]
    public void SqlNullValues_AreNullableButCompilerResultsAreNot()
    {
        var parameterValue = typeof(SqlParameterValue).GetProperty(nameof(SqlParameterValue.Value))!;
        Assert.Equal(NullabilityState.Nullable, Nullability.Create(parameterValue).ReadState);

        var rows = typeof(QueryExecutionResult).GetProperty(nameof(QueryExecutionResult.Rows))!;
        var rowsInfo = Nullability.Create(rows);
        Assert.Equal(NullabilityState.NotNull, rowsInfo.ReadState);
        var dictionary = Assert.Single(rowsInfo.GenericTypeArguments);
        Assert.Equal(NullabilityState.NotNull, dictionary.ReadState);
        Assert.Equal(NullabilityState.Nullable, dictionary.GenericTypeArguments[1].ReadState);

        var compile = typeof(SqlCoreFacade)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(SqlCoreFacade.CompileQuery)
                && method.GetParameters() is var p
                && p.Length == 5
                && p[0].ParameterType == typeof(string)
                && p[3].ParameterType == typeof(SqlPlanValidationContext));
        Assert.Equal(NullabilityState.NotNull, Nullability.Create(compile.ReturnParameter).ReadState);
    }

    private static void AssertNullableProperty<T>(string propertyName)
    {
        var property = typeof(T).GetProperty(propertyName)!;
        var info = Nullability.Create(property);
        Assert.Equal(NullabilityState.Nullable, info.ReadState);
        Assert.Equal(NullabilityState.Nullable, info.WriteState);
    }
}
