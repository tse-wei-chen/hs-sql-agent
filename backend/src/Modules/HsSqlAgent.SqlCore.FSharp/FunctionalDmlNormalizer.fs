namespace HsSqlAgent.SqlCore.Internal

open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Normalization
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums

/// DML canonicalization specialization implemented in F#.
///
/// INSERT keeps its explicit source shape while value/query expressions are
/// normalized through the common query normalizer. UPDATE/DELETE reuse the
/// same common canonicalizer directly.
module internal FunctionalDmlNormalizer =

    let private normalizeValues
        (parent: BoundStatement)
        (values: InsertValuesSource)
        targetProvider =

        let carrier =
            CoreInsertValuesCarrier.CreateExpressionCarrier(
                values)

        let normalized =
            CoreSqlNormalizer
                .CreateDefault()
                .Normalize(
                    BoundStatement(
                        carrier,
                        parent.Facts,
                        parent.SourceDialect),
                    targetProvider)

        CoreInsertValuesCarrier.RestoreFromExpressionCarrier(
            values,
            normalized.Statement)
        :> InsertSource

    let private normalizeQuerySource
        (parent: BoundStatement)
        (source: InsertQuerySource)
        targetProvider =

        let normalized =
            CoreSqlNormalizer
                .CreateDefault()
                .Normalize(
                    BoundStatement(
                        source.Query,
                        parent.Facts,
                        parent.SourceDialect),
                    targetProvider)

        CoreBindingAstClone.InsertQuery(
            source,
            normalized.Statement)
        :> InsertSource

    let normalize
        (statement: BoundStatement)
        targetProvider
        : CanonicalStatement =

        match statement.Statement with
        | :? InsertStatement as insert ->
            let normalizedSource =
                match insert.Source with
                | :? InsertValuesSource as values ->
                    normalizeValues
                        statement
                        values
                        targetProvider

                | :? InsertQuerySource as querySource ->
                    normalizeQuerySource
                        statement
                        querySource
                        targetProvider

                | other ->
                    raise (SqlCompilationException(
                        $"Unsupported INSERT source during normalization: {other.GetType().Name}"))

            CanonicalStatement(
                CoreBindingAstClone.Insert(
                    insert,
                    normalizedSource),
                statement.Facts,
                statement.SourceDialect,
                targetProvider)

        | _ ->
            CoreSqlNormalizer
                .CreateDefault()
                .Normalize(
                    statement,
                    targetProvider)
