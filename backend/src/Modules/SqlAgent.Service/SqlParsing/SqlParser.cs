using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using System.Text;

namespace SqlAgent.Service.SqlParsing;

public class SqlParser(Token[] tokens)
{
    private int _pos;
    private readonly Dictionary<string, string> _tableAliases = [];

    public QueryDefinition Parse()
    {
        var qd = ParseSelectStatement();
        if (Peek().Type == TokenType.Semicolon)
            Advance();
        if (Peek().Type != TokenType.EOF)
        {
            var token = Peek();
            throw new SqlParseException(
                $"Unexpected token '{token.Value}' at position {token.Pos}; the complete statement was not consumed.");
        }
        return qd;
    }

    private QueryDefinition ParseSelectStatement()
    {
        var qd = new QueryDefinition();

        if (PeekKeyword("WITH"))
            qd.CteConditions = ParseCte();

        ExpectKeyword("SELECT");

        if (PeekKeyword("DISTINCT"))
        {
            qd.Distinct = true;
            Advance();
        }
        else if (PeekKeyword("ALL"))
            Advance();

        qd.SelectColumns = ParseColumns();

        if (PeekKeyword("FROM"))
            ParseFromInto(qd);

        if (PeekKeyword("WHERE"))
        {
            Advance();
            qd.WhereColumnsAndValues = ParseWhereExpressionList();
        }

        if (PeekKeyword("GROUP"))
        {
            Advance();
            ExpectKeyword("BY");
            qd.GroupByConditions = ParseGroupBy();
        }

        if (PeekKeyword("HAVING"))
        {
            Advance();
            qd.HavingConditions = ParseHavingExpressionList();
        }

        if (PeekKeyword("ORDER"))
        {
            Advance();
            ExpectKeyword("BY");
            qd.OrderByColumns = ParseOrderBy();
        }

        if (PeekKeyword("LIMIT"))
        {
            Advance();
            ParseLimit(qd);
        }

        if (PeekKeyword("UNION") || PeekKeyword("INTERSECT") || PeekKeyword("EXCEPT"))
            qd.CombineConditions = ParseCombine(qd);

        return qd;
    }

    private List<CteCondition> ParseCte()
    {
        ExpectKeyword("WITH");
        var ctes = new List<CteCondition>();

        if (PeekKeyword("RECURSIVE")) Advance();

        ctes.Add(ParseSingleCte());
        while (Peek().Type == TokenType.Comma)
        {
            Advance();
            ctes.Add(ParseSingleCte());
        }
        return ctes;
    }

    private CteCondition ParseSingleCte()
    {
        var name = Expect(TokenType.Identifier).Value;
        var cte = new CteCondition { CteAliasName = name };

        if (Peek().Type == TokenType.LParen)
        {
            Advance();
            while (Peek().Type != TokenType.RParen) Advance();
            Expect(TokenType.RParen);
        }

        ExpectKeyword("AS");
        Expect(TokenType.LParen);
        var savedAliases = new Dictionary<string, string>(_tableAliases);
        _tableAliases.Clear();
        cte.Query = ParseSelectStatement();
        foreach (var kv in savedAliases) _tableAliases[kv.Key] = kv.Value;
        Expect(TokenType.RParen);
        return cte;
    }

    private List<SelectCondition> ParseColumns()
    {
        var columns = new List<SelectCondition>();

        if (Peek().Type == TokenType.Operator && Peek().Value == "*")
        {
            columns.Add(new FieldSelectCondition { FieldName = "*" });
            Advance();
        }
        else if (PeekKeyword("ALL"))
        {
            columns.Add(new FieldSelectCondition { FieldName = "*" });
            Advance();
        }
        else
        {
            columns.Add(ParseSingleColumn());
            while (Peek().Type == TokenType.Comma)
            {
                Advance();
                columns.Add(ParseSingleColumn());
            }
        }
        return columns;
    }

    private SelectCondition ParseSingleColumn()
    {
        var cond = ParseExprWithAlias(out var alias);
        if (alias != null) cond.Alias = alias;
        return cond;
    }

    private void ParseFromInto(QueryDefinition qd)
    {
        Advance();
        var tableToken = Peek();

        if (tableToken.Type == TokenType.LParen)
        {
            Advance();
            var savedAliases = new Dictionary<string, string>(_tableAliases);
            _tableAliases.Clear();
            qd.FromQuery = ParseSelectStatement();
            foreach (var kv in savedAliases) _tableAliases[kv.Key] = kv.Value;
            Expect(TokenType.RParen);
            if (PeekKeyword("AS")) { Advance(); qd.Alias = Expect(TokenType.Identifier).Value; }
            else if (Peek().Type == TokenType.Identifier) { qd.Alias = Expect(TokenType.Identifier).Value; }
        }
        else
        {
            var name = ParseTableName();
            qd.TableName = name;
            if (PeekKeyword("AS")) { Advance(); qd.Alias = Expect(TokenType.Identifier).Value; }
            else if (Peek().Type == TokenType.Identifier && !IsKeyword(Peek().Value) && !PeekKeyword("ON") && !PeekKeyword("JOIN") && !PeekKeyword("LEFT") && !PeekKeyword("RIGHT") && !PeekKeyword("INNER") && !PeekKeyword("CROSS") && !PeekKeyword("FULL") && !PeekKeyword("WHERE") && !PeekKeyword("GROUP") && !PeekKeyword("ORDER") && !PeekKeyword("HAVING") && !PeekKeyword("LIMIT") && !PeekKeyword("UNION") && !PeekKeyword("INTERSECT") && !PeekKeyword("EXCEPT") && Peek().Type != TokenType.Comma && Peek().Type != TokenType.EOF && !PeekKeyword("SET") && !PeekKeyword("NATURAL") && !PeekKeyword("LATERAL"))
            {
                qd.Alias = Expect(TokenType.Identifier).Value;
            }

            if (qd.Alias != null && !_tableAliases.ContainsKey(qd.TableName))
            {
                var tblName = qd.TableName;
                var lastDot = tblName.LastIndexOf('.');
                var shortName = lastDot >= 0 ? tblName[(lastDot + 1)..] : tblName;
                _tableAliases[shortName] = qd.Alias;
                _tableAliases[qd.Alias] = qd.Alias;
            }
        }

        while (PeekKeyword("JOIN") || PeekKeyword("LEFT") || PeekKeyword("RIGHT") || PeekKeyword("INNER") || PeekKeyword("CROSS") || PeekKeyword("FULL") || PeekKeyword("NATURAL"))
        {
            qd.Joins ??= [];
            qd.Joins.Add(ParseJoin());
        }

        if (Peek().Type == TokenType.Comma)
        {
            Advance();
            ParseFromInto(qd);
        }
    }

    private JoinCondition ParseJoin()
    {
        var join = new JoinCondition();
        string? joinType = null;

        if (PeekKeyword("NATURAL")) { joinType = "NATURAL"; Advance(); }

        if (PeekKeyword("LEFT")) { joinType = "LEFT"; Advance(); if (PeekKeyword("OUTER")) Advance(); }
        else if (PeekKeyword("RIGHT")) { joinType = "RIGHT"; Advance(); if (PeekKeyword("OUTER")) Advance(); }
        else if (PeekKeyword("INNER")) { joinType = "INNER"; Advance(); }
        else if (PeekKeyword("CROSS")) { joinType = "CROSS"; Advance(); }
        else if (PeekKeyword("FULL")) { joinType = "FULL"; Advance(); if (PeekKeyword("OUTER")) Advance(); }

        if (joinType == "CROSS")
        {
            ExpectKeyword("JOIN");
            join.Type = JoinType.Cross;
        }
        else if (joinType != null)
        {
            ExpectKeyword("JOIN");
            join.Type = joinType switch
            {
                "LEFT" => JoinType.Left,
                "RIGHT" => JoinType.Right,
                "FULL" => JoinType.Full,
                "NATURAL" => JoinType.Inner,
                _ => JoinType.Inner
            };
        }
        else
            ExpectKeyword("JOIN");

        if (Peek().Type == TokenType.LParen)
        {
            Advance();
            var savedAliases = new Dictionary<string, string>(_tableAliases);
            _tableAliases.Clear();
            join.SubQuery = ParseSelectStatement();
            foreach (var kv in savedAliases) _tableAliases[kv.Key] = kv.Value;
            Expect(TokenType.RParen);
        }
        else
        {
            join.Table = ParseTableName();
        }

        if (PeekKeyword("AS")) { Advance(); join.Alias = Expect(TokenType.Identifier).Value; }
        else if (Peek().Type == TokenType.Identifier && !IsKeyword(Peek().Value) && !PeekKeyword("ON") && !PeekKeyword("WHERE") && !PeekKeyword("GROUP") && !PeekKeyword("ORDER") && !PeekKeyword("HAVING") && !PeekKeyword("LIMIT") && !PeekKeyword("UNION") && !PeekKeyword("INTERSECT") && !PeekKeyword("EXCEPT"))
        {
            join.Alias = Expect(TokenType.Identifier).Value;
        }

        if (join.Alias != null)
        {
            _tableAliases[join.Alias] = join.Alias;
            if (join.Table != null)
            {
                var lastDot = join.Table.LastIndexOf('.');
                var shortName = lastDot >= 0 ? join.Table[(lastDot + 1)..] : join.Table;
                if (!_tableAliases.ContainsKey(shortName))
                    _tableAliases[shortName] = join.Alias;
            }
        }

        if (PeekKeyword("ON"))
        {
            Advance();
            join.OnConditions = ParseOnConditions();
        }
        else if (PeekKeyword("USING"))
        {
            Advance();
            Expect(TokenType.LParen);
            while (Peek().Type != TokenType.RParen)
            {
                if (Peek().Type == TokenType.Comma) Advance();
                else
                {
                    var col = Expect(TokenType.Identifier).Value;
                    join.OnConditions.Add(new ColumnCompareWhereCondition
                    {
                        LeftFieldName = col,
                        Operator = "=",
                        RightFieldName = col,
                    });
                }
            }
            Expect(TokenType.RParen);
        }

        return join;
    }

    private List<WhereCondition> ParseOnConditions()
    {
        var conditions = new List<WhereCondition>
        {
            ParseSingleWhereExpr()
        };
        while (PeekKeyword("AND"))
        {
            Advance();
            conditions.Add(ParseSingleWhereExpr());
        }
        if (PeekKeyword("OR"))
        {
            var group = new GroupWhereCondition();
            foreach (var c in conditions) group.Groups.Add(c);
            group.IsOr = true;
            Advance();
            group.Groups.Add(ParseSingleWhereExpr());
            return [group];
        }
        return conditions;
    }

    private List<WhereCondition> ParseWhereExpressionList()
    {
        var conditions = new List<WhereCondition>
        {
            ParseSingleWhereExpr()
        };
        while (PeekKeyword("AND"))
        {
            Advance();
            conditions.Add(ParseSingleWhereExpr());
        }
        return conditions;
    }

    private WhereCondition ParseSingleWhereExpr()
    {
        return ParseWhereOrExpr();
    }

    private WhereCondition ParseWhereOrExpr()
    {
        var left = ParseWhereAndExpr();

        if (PeekKeyword("OR"))
        {
            var group = new GroupWhereCondition { IsOr = true };
            CollectWhereConditions(left, group.Groups);
            while (PeekKeyword("OR"))
            {
                Advance();
                var right = ParseWhereAndExpr();
                CollectWhereConditions(right, group.Groups);
            }
            return group.Groups.Count == 1 ? group.Groups[0] : group;
        }
        return left;
    }

    private WhereCondition ParseWhereAndExpr()
    {
        var left = ParseWherePrimary();

        if (PeekKeyword("AND"))
        {
            var group = new GroupWhereCondition();
            CollectWhereConditions(left, group.Groups);
            while (PeekKeyword("AND"))
            {
                Advance();
                var right = ParseWherePrimary();
                CollectWhereConditions(right, group.Groups);
            }
            return group.Groups.Count == 1 ? group.Groups[0] : group;
        }
        return left;
    }

    private WhereCondition ParseWherePrimary()
    {
        if (PeekKeyword("NOT"))
        {
            Advance();
            var cond = ParseWherePrimary();
            cond.IsNot = true;
            return cond;
        }

        if (PeekKeyword("EXISTS"))
        {
            Advance();
            Expect(TokenType.LParen);
            var sub = new SubQueryWhereCondition
            {
                FieldName = null,
                Operator = "EXISTS",
                SubQuery = ParseSelectStatement()
            };
            Expect(TokenType.RParen);
            return sub;
        }

        if (Peek().Type == TokenType.LParen)
        {
            var isParenthesizedValueExpression = ParenthesizedValueIsCompared();
            Advance();
            if (PeekKeyword("SELECT") || PeekKeyword("WITH"))
            {
                var saved = new Dictionary<string, string>(_tableAliases);
                _tableAliases.Clear();
                var subQd = ParseSelectStatement();
                foreach (var kv in saved) _tableAliases[kv.Key] = kv.Value;
                Expect(TokenType.RParen);
                var sub = new SubQueryWhereCondition { FieldName = null, Operator = "EXISTS", SubQuery = subQd };
                return sub;
            }
            if (!isParenthesizedValueExpression)
            {
                var nested = ParseWhereOrExpr();
                Expect(TokenType.RParen);
                return nested;
            }
            var expr = ParseExpr();
            Expect(TokenType.RParen);
            if (IsNextComparisonOp())
            {
                var opToken = Advance().Value;
                var rightExpr = ParseAdditiveExpr(null);
                if (expr is FieldSelectCondition lf && rightExpr is FieldSelectCondition rf)
                    return new ColumnCompareWhereCondition { LeftFieldName = lf.FieldName, Operator = opToken, RightFieldName = rf.FieldName };
                if (expr is FieldSelectCondition f2 && rightExpr is ConstantSelectCondition c)
                    return new BasicWhereCondition { FieldName = f2.FieldName, Operator = opToken, Value = c.Constant };
                return new ExpressionWhereCondition
                {
                    LeftExpression = expr,
                    Operator = opToken,
                    RightExpression = rightExpr,
                };
            }
            var whereCond = ParseExprToWhereCondition(expr);
            var group = new GroupWhereCondition();
            CollectWhereConditions(whereCond, group.Groups);
            return group;
        }

        return ParseWhereComparison();
    }

    private bool ParenthesizedValueIsCompared()
    {
        var depth = 0;
        for (var offset = 0; ; offset++)
        {
            var token = Peek(offset);
            if (token.Type == TokenType.EOF) return false;
            if (token.Type == TokenType.LParen) depth++;
            else if (token.Type == TokenType.RParen && --depth == 0)
            {
                var next = Peek(offset + 1);
                return next.Type == TokenType.Operator && IsComparisonOp(next.Value);
            }
        }
    }

    private WhereCondition ParseWhereComparison()
    {
        var leftExpr = ParseAdditiveExpr(null);
        var opToken = Peek();

        if (opToken.Type == TokenType.Operator)
        {
            var op = opToken.Value;
            if (IsComparisonOp(op))
            {
                Advance();

                if (Peek().Type == TokenType.LParen && Peek(1).Type == TokenType.Keyword && Peek(1).Value.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    Advance();
                    var sub = new SubQueryWhereCondition
                    {
                        FieldName = leftExpr is FieldSelectCondition f ? f.FieldName : null,
                        Operator = op == "<>" || op == "!=" ? "NOT IN" : "IN",
                        SubQuery = ParseSelectStatement()
                    };
                    Expect(TokenType.RParen);
                    return sub;
                }

                var right = ParseAdditiveExpr(null);

                if (leftExpr is FieldSelectCondition lf && right is FieldSelectCondition rf)
                {
                    return new ColumnCompareWhereCondition
                    {
                        LeftFieldName = lf.FieldName,
                        Operator = op,
                        RightFieldName = rf.FieldName,
                    };
                }

                if (leftExpr is FieldSelectCondition f2 && right is ConstantSelectCondition c)
                {
                    return new BasicWhereCondition
                    {
                        FieldName = f2.FieldName,
                        Operator = op,
                        Value = c.Constant,
                    };
                }

                return new ExpressionWhereCondition
                {
                    LeftExpression = leftExpr,
                    Operator = op,
                    RightExpression = right,
                };
            }
        }

        if (PeekKeyword("IS"))
        {
            Advance();
            if (PeekKeyword("NOT")) { Advance(); ExpectKeyword("NULL"); return new BasicWhereCondition { FieldName = ExtractFieldName(leftExpr), Operator = "IS", Value = null, IsNot = true }; }
            ExpectKeyword("NULL");
            return new BasicWhereCondition { FieldName = ExtractFieldName(leftExpr), Operator = "IS", Value = null };
        }

        if (PeekKeyword("IN"))
        {
            Advance();
            Expect(TokenType.LParen);
            if (PeekKeyword("SELECT"))
            {
                var sub = new SubQueryWhereCondition
                {
                    FieldName = ExtractFieldName(leftExpr),
                    Operator = "IN",
                    SubQuery = ParseSelectStatement()
                };
                Expect(TokenType.RParen);
                return sub;
            }
            else
            {
                var basic = new BasicWhereCondition
                {
                    FieldName = ExtractFieldName(leftExpr),
                    Operator = "IN",
                };
                basic.Values.AddRange(ParseLiteralList());
                Expect(TokenType.RParen);
                return basic;
            }
        }

        if (PeekKeyword("NOT"))
        {
            Advance();
            if (PeekKeyword("IN"))
            {
                Advance();
                Expect(TokenType.LParen);
                var basic = new BasicWhereCondition
                {
                    FieldName = ExtractFieldName(leftExpr),
                    Operator = "IN",
                    IsNot = true,
                };
                basic.Values.AddRange(ParseLiteralList());
                Expect(TokenType.RParen);
                return basic;
            }
            if (PeekKeyword("LIKE"))
            {
                Advance();
                var right = ParseAdditiveExpr(null);
                return new BasicWhereCondition
                {
                    FieldName = ExtractFieldName(leftExpr),
                    Operator = "LIKE",
                    Value = right is ConstantSelectCondition c ? c.Constant : ExtractExprText(right),
                    IsNot = true,
                };
            }
            if (PeekKeyword("ILIKE"))
            {
                Advance();
                var right = ParseAdditiveExpr(null);
                return new BasicWhereCondition
                {
                    FieldName = ExtractFieldName(leftExpr),
                    Operator = "ILIKE",
                    Value = right is ConstantSelectCondition c ? c.Constant : ExtractExprText(right),
                    IsNot = true,
                };
            }
            if (PeekKeyword("BETWEEN"))
            {
                Advance();
                var v1 = ParseAdditiveExpr(null);
                ExpectKeyword("AND");
                var v2 = ParseAdditiveExpr(null);
                return new BasicWhereCondition
                {
                    FieldName = ExtractFieldName(leftExpr),
                    Operator = "BETWEEN",
                    Value = new List<object> { v1 is ConstantSelectCondition c1 ? c1.Constant : ExtractExprText(v1), v2 is ConstantSelectCondition c2 ? c2.Constant : ExtractExprText(v2) },
                    IsNot = true,
                };
            }
        }

        if (PeekKeyword("LIKE"))
        {
            Advance();
            var right = ParseAdditiveExpr(null);
            return new BasicWhereCondition
            {
                FieldName = ExtractFieldName(leftExpr),
                Operator = "LIKE",
                Value = right is ConstantSelectCondition c ? c.Constant : ExtractExprText(right),
            };
        }

        if (PeekKeyword("ILIKE"))
        {
            Advance();
            var right = ParseAdditiveExpr(null);
            return new BasicWhereCondition
            {
                FieldName = ExtractFieldName(leftExpr),
                Operator = "ILIKE",
                Value = right is ConstantSelectCondition c ? c.Constant : ExtractExprText(right),
            };
        }

        if (PeekKeyword("BETWEEN"))
        {
            Advance();
            var v1 = ParseAdditiveExpr(null);
            ExpectKeyword("AND");
            var v2 = ParseAdditiveExpr(null);
            return new BasicWhereCondition
            {
                FieldName = ExtractFieldName(leftExpr),
                Operator = "BETWEEN",
                Value = new List<object> { v1 is ConstantSelectCondition c1 ? c1.Constant : ExtractExprText(v1), v2 is ConstantSelectCondition c2 ? c2.Constant : ExtractExprText(v2) },
            };
        }

        if (PeekKeyword("IN"))
        {
            Advance();
            Expect(TokenType.LParen);
            var basic = new BasicWhereCondition
            {
                FieldName = ExtractFieldName(leftExpr),
                Operator = "IN",
            };
            basic.Values.AddRange(ParseLiteralList());
            Expect(TokenType.RParen);
            return basic;
        }

        return new BasicWhereCondition
        {
            FieldName = ExtractFieldName(leftExpr),
            Operator = "=",
            Value = true,
        };
    }

    private List<HavingCondition> ParseHavingExpressionList()
    {
        var conditions = new List<HavingCondition>
        {
            ParseSingleHavingExpr()
        };
        while (PeekKeyword("AND") || PeekKeyword("OR"))
        {
            var isOr = PeekKeyword("OR");
            Advance();
            conditions.Add(ParseSingleHavingExpr());
            if (isOr && conditions.Count >= 2)
            {
                var last = conditions[^1];
                var prev = conditions[^2];
                var group = new GroupHavingCondition();
                group.Groups.Add(prev);
                group.Groups.Add(last);
                group.IsOr = true;
                conditions.RemoveRange(conditions.Count - 2, 2);
                conditions.Add(group);
            }
        }
        return conditions;
    }

    private HavingCondition ParseSingleHavingExpr()
    {
        var leftExpr = ParseAdditiveExpr(null);
        var opToken = Peek();

        if (PeekKeyword("IS"))
        {
            Advance();
            if (PeekKeyword("NOT")) { Advance(); ExpectKeyword("NULL"); return MakeHaving(leftExpr, "IS", null, true); }
            ExpectKeyword("NULL");
            return MakeHaving(leftExpr, "IS", null, false);
        }

        if (opToken.Type == TokenType.Operator && IsComparisonOp(opToken.Value))
        {
            Advance();
            var rightExpr = ParseAdditiveExpr(null);
            if (rightExpr is ConstantSelectCondition rightConst)
                return MakeHaving(leftExpr, opToken.Value, rightConst.Constant, false);
            return new ExpressionHavingCondition
            {
                LeftExpression = leftExpr,
                Operator = opToken.Value,
                RightExpression = rightExpr,
            };
        }

        return new BasicHavingCondition
        {
            FieldName = ExtractFieldName(leftExpr),
            Operator = "=",
            Value = true
        };
    }

    private static HavingCondition MakeHaving(SelectCondition leftExpr, string op, object? value, bool isNot)
    {
        if (leftExpr is FunctionSelectCondition fn)
        {
            return new FunctionHavingCondition
            {
                LeftFunction = new SqlFunctionCondition
                {
                    FunctionName = fn.FunctionName,
                    Arguments = fn.Arguments,
                    IsDistinct = fn.IsDistinct,
                },
                Operator = op,
                Value = value,
                IsNot = isNot,
            };
        }

        if (leftExpr is OperationSelectCondition)
        {
            return new ExpressionHavingCondition
            {
                LeftExpression = leftExpr,
                Operator = op,
                RightExpression = value == null ? null : new ConstantSelectCondition { Constant = value },
                IsNot = isNot,
            };
        }

        return new BasicHavingCondition
        {
            FieldName = ExtractFieldName(leftExpr),
            Operator = op,
            Value = value,
            IsNot = isNot,
        };
    }

    private List<GroupByCondition> ParseGroupBy()
    {
        var groups = new List<GroupByCondition>
        {
            ParseSingleGroupBy()
        };
        while (Peek().Type == TokenType.Comma)
        {
            Advance();
            groups.Add(ParseSingleGroupBy());
        }
        return groups;
    }

    private GroupByCondition ParseSingleGroupBy()
    {
        var expr = ParseAdditiveExpr(null);
        if (expr is FieldSelectCondition f)
            return new FieldGroupByCondition { FieldName = f.FieldName };
        if (expr is FunctionSelectCondition fn)
            return new FunctionGroupByCondition
            {
                FunctionName = fn.FunctionName,
                Arguments = fn.Arguments,
                IsDistinct = fn.IsDistinct,
            };
        return new FieldGroupByCondition { FieldName = ExtractExprText(expr) };
    }

    private List<OrderByCondition> ParseOrderBy()
    {
        var orders = new List<OrderByCondition>
        {
            ParseSingleOrderBy()
        };
        while (Peek().Type == TokenType.Comma)
        {
            Advance();
            orders.Add(ParseSingleOrderBy());
        }
        return orders;
    }

    private OrderByCondition ParseSingleOrderBy()
    {
        var expr = ParseAdditiveExpr(null);
        var dir = SortDirection.Asc;
        var nullOrdering = NullOrdering.Default;
        if (PeekKeyword("ASC")) { Advance(); }
        else if (PeekKeyword("DESC")) { dir = SortDirection.Desc; Advance(); }

        if (PeekKeyword("NULLS"))
        {
            Advance();
            if (PeekKeyword("FIRST"))
            {
                nullOrdering = NullOrdering.First;
                Advance();
            }
            else if (PeekKeyword("LAST"))
            {
                nullOrdering = NullOrdering.Last;
                Advance();
            }
            else
            {
                var token = Peek();
                throw new SqlParseException($"Expected FIRST or LAST after NULLS at position {token.Pos}.");
            }
        }

        if (expr is FieldSelectCondition f)
            return new FieldOrderByCondition { FieldName = f.FieldName, Direction = dir, NullOrdering = nullOrdering };
        if (expr is FunctionSelectCondition fn)
            return new FunctionOrderByCondition
            {
                FunctionName = fn.FunctionName,
                Arguments = fn.Arguments,
                IsDistinct = fn.IsDistinct,
                Direction = dir,
                NullOrdering = nullOrdering
            };
        if (nullOrdering != NullOrdering.Default)
            throw CapabilityError("NULL ordering on non-field/function expressions", Peek(-1));
        return new FieldOrderByCondition { FieldName = ExtractExprText(expr), Direction = dir };
    }

    private void ParseLimit(QueryDefinition qd)
    {
        if (Peek().Type == TokenType.Number)
        {
            qd.Limit = int.Parse(Peek().Value);
            Advance();
            if (PeekKeyword("OFFSET"))
            {
                Advance();
                qd.Offset = int.Parse(Expect(TokenType.Number).Value);
            }
        }
    }

    private List<CombineCondition> ParseCombine(QueryDefinition qd)
    {
        var combines = new List<CombineCondition>();

        while (PeekKeyword("UNION") || PeekKeyword("INTERSECT") || PeekKeyword("EXCEPT"))
        {
            var kw = Peek().Value;
            Advance();
            var combineType = kw.ToUpper() switch
            {
                "UNION" => CombineType.Union,
                "INTERSECT" => CombineType.Intersect,
                "EXCEPT" => CombineType.Except,
                _ => CombineType.Union
            };
            if (PeekKeyword("ALL")) { combineType = CombineType.UnionAll; Advance(); }
            else if (PeekKeyword("DISTINCT")) Advance();

            var savedAliases = new Dictionary<string, string>(_tableAliases);
            _tableAliases.Clear();
            var subQd = ParseSelectStatement();
            foreach (var kv in savedAliases) _tableAliases[kv.Key] = kv.Value;

            combines.Add(new CombineCondition { Type = combineType, Query = subQd });
        }
        return combines;
    }

    private SelectCondition ParseExprWithAlias(out string? alias)
    {
        alias = null;
        var expr = ParseExpr();

        if (PeekKeyword("AS"))
        {
            Advance();
            alias = Expect(TokenType.Identifier).Value;
        }
        else if (Peek().Type == TokenType.Identifier && !IsKeyword(Peek().Value))
        {
            alias = Expect(TokenType.Identifier).Value;
        }

        return expr;
    }

    private SelectCondition ParseExpr() => ParseOrExpr();

    private SelectCondition ParseOrExpr()
    {
        var left = ParseAndExpr();
        while (PeekKeyword("OR"))
        {
            Advance();
            var right = ParseAndExpr();
            left = new OperationSelectCondition
            {
                Left = left,
                Operator = ArithmeticOperator.Or,
                Right = right
            };
        }
        return left;
    }

    private SelectCondition ParseAndExpr()
    {
        var left = ParseUnaryExpr();
        while (PeekKeyword("AND"))
        {
            Advance();
            var right = ParseUnaryExpr();
            left = new OperationSelectCondition
            {
                Left = left,
                Operator = ArithmeticOperator.And,
                Right = right
            };
        }
        return left;
    }

    private SelectCondition ParseUnaryExpr()
    {
        if (Peek().Type == TokenType.Operator && (Peek().Value == "+" || Peek().Value == "-"))
        {
            var op = Advance().Value;
            var expr = ParsePostfixExpr();
            if (op == "-" && expr is ConstantSelectCondition c)
                return new ConstantSelectCondition { Constant = -(Convert.ToDouble(c.Constant)) };
            return expr;
        }
        return ParseComparisonExpr();
    }

    private SelectCondition ParseComparisonExpr()
    {
        var left = ParseAdditiveExpr(null);
        var opToken = Peek();

        if (opToken.Type == TokenType.Operator && IsComparisonOp(opToken.Value))
        {
            Advance();
            var right = ParseAdditiveExpr(null);
            return new OperationSelectCondition
            {
                Left = left,
                Operator = ComparisonOperator(opToken.Value),
                Right = right
            };
        }
        return left;
    }

    private SelectCondition ParseAdditiveExpr(SelectCondition? left)
    {
        left ??= ParseMultiplicativeExpr();

        while (Peek().Type == TokenType.Operator && (Peek().Value == "+" || Peek().Value == "-" || Peek().Value == "||"))
        {
            var op = Advance().Value;
            var right = ParseMultiplicativeExpr();
            left = new OperationSelectCondition
            {
                Left = left,
                Operator = op == "+" ? ArithmeticOperator.Add : op == "-" ? ArithmeticOperator.Subtract : ArithmeticOperator.Concat,
                Right = right,
            };
        }
        return left;
    }

    private SelectCondition ParseMultiplicativeExpr()
    {
        var left = ParsePostfixExpr();

        while (Peek().Type == TokenType.Operator && (Peek().Value == "*" || Peek().Value == "/" || Peek().Value == "%"))
        {
            var op = Advance().Value;
            var right = ParsePostfixExpr();
            left = new OperationSelectCondition
            {
                Left = left,
                Operator = op == "*" ? ArithmeticOperator.Multiply
                    : op == "/" ? ArithmeticOperator.Divide
                    : ArithmeticOperator.Modulo,
                Right = right,
            };
        }
        return left;
    }

    private SelectCondition ParsePostfixExpr()
    {
        var expr = ParsePrimary();
        while (Peek().Type == TokenType.Operator && Peek().Value == "::")
        {
            Advance();
            expr = new CastSelectCondition
            {
                Expression = expr,
                TypeName = ParseCastTypeName()
            };
        }
        return expr;
    }

    private SelectCondition ParsePrimary()
    {
        if (PeekKeyword("CASE"))
            return ParseCaseExpr();

        if (PeekKeyword("CAST"))
            return ParseCastExpr();

        if (Peek().Type == TokenType.Parameter)
        {
            var parameter = Peek();
            throw new SqlParseException(
                $"Unbound SQL parameter '{parameter.Value}' at position {parameter.Pos}. " +
                "Runtime SQL parameters are not accepted by execute_query_sql; use a declared Custom Tool parameter.");
        }

        if (PeekKeyword("NULL"))
        {
            Advance();
            return new ConstantSelectCondition { Constant = null! };
        }

        if (PeekKeyword("TRUE"))
        {
            Advance();
            return new ConstantSelectCondition { Constant = true };
        }

        if (PeekKeyword("FALSE"))
        {
            Advance();
            return new ConstantSelectCondition { Constant = false };
        }

        if (PeekKeyword("EXISTS"))
        {
            Advance();
            Expect(TokenType.LParen);
            var sub = new SubQuerySelectCondition();
            sub.SelectColumns = ParseColumns();
            if (PeekKeyword("FROM"))
            {
                Advance();
                var qdSub = new QueryDefinition();
                ParseFromInto(qdSub);
                sub.TableName = qdSub.TableName;
                sub.FromQuery = qdSub.FromQuery;
                sub.Alias = qdSub.Alias;
                sub.Joins = qdSub.Joins;
                if (PeekKeyword("WHERE")) { Advance(); sub.WhereColumnsAndValues = ParseWhereExpressionList(); }
                if (PeekKeyword("GROUP")) { Advance(); ExpectKeyword("BY"); sub.GroupByConditions = ParseGroupBy(); }
                if (PeekKeyword("HAVING")) { Advance(); sub.HavingConditions = ParseHavingExpressionList(); }
                if (PeekKeyword("ORDER")) { Advance(); ExpectKeyword("BY"); sub.OrderByColumns = ParseOrderBy(); }
                if (PeekKeyword("LIMIT")) { Advance(); var limit = int.Parse(Expect(TokenType.Number).Value); sub.Limit = limit; }
            }
            Expect(TokenType.RParen);
            return sub;
        }

        if (Peek().Type == TokenType.LParen)
        {
            Advance();

            if (PeekKeyword("SELECT"))
            {
                var savedAliases = new Dictionary<string, string>(_tableAliases);
                _tableAliases.Clear();
                var sub = new SubQuerySelectCondition();
                var subQd = ParseSelectStatement();
                subQd.TableName = subQd.TableName ?? "";
                sub.TableName = subQd.TableName;
                sub.FromQuery = subQd.FromQuery;
                sub.Alias = subQd.Alias;
                sub.Distinct = subQd.Distinct;
                sub.SelectColumns = subQd.SelectColumns;
                sub.WhereColumnsAndValues = subQd.WhereColumnsAndValues;
                sub.OrderByColumns = subQd.OrderByColumns;
                sub.GroupByConditions = subQd.GroupByConditions;
                sub.HavingConditions = subQd.HavingConditions;
                sub.Joins = subQd.Joins;
                sub.CombineConditions = subQd.CombineConditions;
                sub.CteConditions = subQd.CteConditions;
                sub.Limit = subQd.Limit;
                sub.Offset = subQd.Offset;
                foreach (var kv in savedAliases) _tableAliases[kv.Key] = kv.Value;
                Expect(TokenType.RParen);
                return sub;
            }

            var innerExpr = ParseExpr();
            Expect(TokenType.RParen);
            return innerExpr;
        }

        if (Peek().Type == TokenType.Number)
        {
            var val = Advance().Value;
            return new ConstantSelectCondition
            {
                Constant = ParseNumericLiteral(val)
            };
        }

        if (Peek().Type == TokenType.String)
        {
            var val = Advance().Value;
            return new ConstantSelectCondition { Constant = DecodeStringLiteral(val) };
        }

        if (Peek().Type == TokenType.Operator && Peek().Value == "*")
        {
            Advance();
            return new FieldSelectCondition { FieldName = "*" };
        }

        if (PeekKeyword("TIMESTAMP")
            && (Peek(1).Value.Equals("WITH", StringComparison.OrdinalIgnoreCase)
                || Peek(1).Value.Equals("WITHOUT", StringComparison.OrdinalIgnoreCase)))
        {
            Advance();
            var withTimeZone = Advance().Value.Equals("WITH", StringComparison.OrdinalIgnoreCase);
            ExpectKeyword("TIME");
            ExpectKeyword("ZONE");
            var literalToken = Expect(TokenType.String);
            var raw = DecodeStringLiteral(literalToken.Value);
            if (!SqlTemporalLiteralParser.TryParseTimestamp(raw, out var timestamp))
                throw new SqlParseException($"Invalid TIMESTAMP literal '{raw}' at position {literalToken.Pos}.");
            if (withTimeZone && timestamp is not SqlOffsetDateTimeValue)
                throw new SqlParseException("TIMESTAMP WITH TIME ZONE requires an explicit UTC offset or Z suffix.");
            if (!withTimeZone && timestamp is SqlOffsetDateTimeValue)
                throw new SqlParseException("TIMESTAMP WITHOUT TIME ZONE must not include a UTC offset.");
            return new ConstantSelectCondition { Constant = timestamp };
        }

        if ((Peek().Type == TokenType.Keyword || Peek().Type == TokenType.Identifier) && Peek(1).Type == TokenType.String &&
            (Peek().Value.Equals("DATE", StringComparison.OrdinalIgnoreCase) ||
             Peek().Value.Equals("TIME", StringComparison.OrdinalIgnoreCase) ||
             Peek().Value.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase)))
        {
            var typeKw = Advance().Value;
            var literalToken = Advance();
            var raw = DecodeStringLiteral(literalToken.Value);
            if (typeKw.Equals("DATE", StringComparison.OrdinalIgnoreCase))
            {
                if (!SqlTemporalLiteralParser.TryParseDate(raw, out var date))
                    throw new SqlParseException(
                        $"Invalid DATE literal '{raw}'. Expected YYYY-MM-DD at position {literalToken.Pos}.");
                return new ConstantSelectCondition { Constant = date };
            }

            if (typeKw.Equals("TIME", StringComparison.OrdinalIgnoreCase)
                && SqlTemporalLiteralParser.TryParseTime(raw, out var time))
                return new ConstantSelectCondition { Constant = time };
            if (typeKw.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase)
                && SqlTemporalLiteralParser.TryParseTimestamp(raw, out var timestamp))
                return new ConstantSelectCondition { Constant = timestamp };
            throw new SqlParseException($"Invalid {typeKw.ToUpperInvariant()} literal '{raw}' at position {literalToken.Pos}.");
        }

        if ((PeekKeyword("INTERVAL") || (Peek().Type == TokenType.Identifier && Peek().Value.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase)))
            && Peek(1).Type == TokenType.String)
        {
            Advance();
            var literal = DecodeStringLiteral(Advance().Value);
            return new IntervalSelectCondition { Literal = literal };
        }

        if (PeekKeyword("CURRENT_DATE")
            || PeekKeyword("CURRENT_TIME")
            || PeekKeyword("CURRENT_TIMESTAMP"))
        {
            var token = Advance().Value.ToUpperInvariant();
            if (Peek().Type == TokenType.LParen)
            {
                Advance();
                Expect(TokenType.RParen);
            }
            return new TemplateSqlTokenSelectCondition { Token = token };
        }

        if ((Peek().Type == TokenType.Keyword || Peek().Type == TokenType.Identifier) && Peek(1).Type == TokenType.LParen)
        {
            return ParseFunction();
        }

        if (PeekKeyword("ROW") || PeekKeyword("RANGE") || PeekKeyword("UNBOUNDED") || PeekKeyword("PRECEDING") || PeekKeyword("FOLLOWING"))
        {
            return new FieldSelectCondition { FieldName = Advance().Value };
        }

        return ParseColumnRef();
    }

    private FunctionSelectCondition ParseFunction()
    {
        var fnName = Advance().Value.ToUpper();
        Expect(TokenType.LParen);
        var isDistinct = PeekKeyword("DISTINCT");
        if (isDistinct) Advance();

        var args = new List<SelectCondition>();
        var window = (WindowDefinition?)null;
        var filterConditions = (List<WhereCondition>?)null;

        if (fnName == "COUNT" && Peek().Type == TokenType.Operator && Peek().Value == "*")
        {
            Advance();
            args.Add(new FieldSelectCondition { FieldName = "*" });
        }
        else if (Peek().Type != TokenType.RParen)
        {
            if (!isDistinct && PeekKeyword("ALL")) Advance();
            while (Peek().Type != TokenType.RParen && Peek().Type != TokenType.EOF)
            {
                if (Peek().Type == TokenType.Comma) { Advance(); continue; }
                if (PeekKeyword("DISTINCT")) { isDistinct = true; Advance(); continue; }
                args.Add(ParseExpr());
                if (Peek().Type == TokenType.Comma) Advance();
            }
        }

        Expect(TokenType.RParen);

        if (PeekKeyword("FILTER"))
        {
            Advance();
            Expect(TokenType.LParen);
            ExpectKeyword("WHERE");
            filterConditions = ParseWhereExpressionList();
            Expect(TokenType.RParen);
        }

        if (PeekKeyword("OVER"))
        {
            Advance();
            window = ParseWindowSpec();
        }

        if (args.Count == 0 && !isDistinct)
        {
            args = null;
        }

        return new FunctionSelectCondition
        {
            FunctionName = fnName,
            Arguments = args,
            IsDistinct = isDistinct,
            FilterWhereConditions = filterConditions,
            Window = window,
        };
    }

    private WindowDefinition ParseWindowSpec()
    {
        Expect(TokenType.LParen);
        var window = new WindowDefinition();

        if (PeekKeyword("PARTITION"))
        {
            Advance();
            ExpectKeyword("BY");
            window.PartitionBy = [ParseSingleGroupBy()];
            while (Peek().Type == TokenType.Comma)
            {
                Advance();
                window.PartitionBy.Add(ParseSingleGroupBy());
            }
        }

        if (PeekKeyword("ORDER"))
        {
            Advance();
            ExpectKeyword("BY");
            window.OrderBy = ParseOrderBy();
        }

        if (PeekKeyword("ROWS") || PeekKeyword("RANGE"))
            window.Frame = ParseWindowFrame();

        Expect(TokenType.RParen);
        return window;
    }

    private CaseWhenSelectCondition ParseCaseExpr()
    {
        Advance();
        var caseExpr = Peek().Type == TokenType.Keyword && PeekKeyword("WHEN") ? null : ParseExpr();
        var cases = new List<CaseWhenClause>();

        while (PeekKeyword("WHEN"))
        {
            Advance();
            WhereCondition condition;
            if (caseExpr != null)
            {
                var whenExpr = ParseExpr();
                condition = new ExpressionWhereCondition
                {
                    LeftExpression = caseExpr,
                    Operator = "=",
                    RightExpression = whenExpr,
                };
            }
            else
            {
                condition = ParseWhereOrExpr();
            }
            ExpectKeyword("THEN");
            var thenExpr = ParseExprWithAlias(out _);
            cases.Add(new CaseWhenClause
            {
                Condition = condition,
                Value = thenExpr,
            });
        }

        object? elseValue = null;
        if (PeekKeyword("ELSE"))
        {
            Advance();
            elseValue = ParseExprWithAlias(out _);
        }

        ExpectKeyword("END");
        if (PeekKeyword("CASE")) Advance();

        return new CaseWhenSelectCondition
        {
            CaseWhen = cases,
            ElseValue = elseValue,
        };
    }

    private SelectCondition ParseCastExpr()
    {
        Advance();
        Expect(TokenType.LParen);
        var expr = ParseExpr();
        ExpectKeyword("AS");
        var castType = ParseCastTypeName();
        Expect(TokenType.RParen);
        return new CastSelectCondition { Expression = expr, TypeName = castType };
    }

    private string ParseCastTypeName()
    {
        var parts = new List<string>();
        if (Peek().Type is not (TokenType.Identifier or TokenType.Keyword))
        {
            var token = Peek();
            throw new SqlParseException($"Expected cast type at position {token.Pos}.");
        }

        parts.Add(Advance().Value);
        while (Peek().Type == TokenType.Dot)
        {
            Advance();
            if (Peek().Type is not (TokenType.Identifier or TokenType.Keyword))
                throw new SqlParseException($"Expected cast type component at position {Peek().Pos}.");
            parts[^1] += "." + Advance().Value;
        }

        while (Peek().Type is TokenType.Identifier or TokenType.Keyword
               && IsCastTypeQualifier(Peek().Value))
            parts.Add(Advance().Value);

        if (Peek().Type == TokenType.LParen)
        {
            var suffix = new StringBuilder("(");
            Advance();
            var first = Expect(TokenType.Number);
            if (!int.TryParse(first.Value, out _))
                throw new SqlParseException($"Cast type precision must be an integer at position {first.Pos}.");
            suffix.Append(first.Value);
            if (Peek().Type == TokenType.Comma)
            {
                Advance();
                var second = Expect(TokenType.Number);
                if (!int.TryParse(second.Value, out _))
                    throw new SqlParseException($"Cast type scale must be an integer at position {second.Pos}.");
                suffix.Append(',').Append(second.Value);
            }
            Expect(TokenType.RParen);
            suffix.Append(')');
            parts[^1] += suffix.ToString();
        }

        return string.Join(' ', parts);
    }

    private static bool IsCastTypeQualifier(string value) => value.Equals("PRECISION", StringComparison.OrdinalIgnoreCase)
        || value.Equals("VARYING", StringComparison.OrdinalIgnoreCase)
        || value.Equals("WITH", StringComparison.OrdinalIgnoreCase)
        || value.Equals("WITHOUT", StringComparison.OrdinalIgnoreCase)
        || value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ZONE", StringComparison.OrdinalIgnoreCase)
        || value.Equals("SIGNED", StringComparison.OrdinalIgnoreCase)
        || value.Equals("UNSIGNED", StringComparison.OrdinalIgnoreCase);

    private WindowFrameDefinition ParseWindowFrame()
    {
        var unitToken = Advance();
        var frame = new WindowFrameDefinition
        {
            Unit = unitToken.Value.Equals("ROWS", StringComparison.OrdinalIgnoreCase)
                ? WindowFrameUnit.Rows
                : WindowFrameUnit.Range
        };

        if (PeekKeyword("BETWEEN"))
        {
            Advance();
            frame.Start = ParseWindowFrameBound();
            ExpectKeyword("AND");
            frame.End = ParseWindowFrameBound();
        }
        else
        {
            frame.Start = ParseWindowFrameBound();
        }

        return frame;
    }

    private WindowFrameBound ParseWindowFrameBound()
    {
        if (PeekKeyword("UNBOUNDED"))
        {
            Advance();
            if (PeekKeyword("PRECEDING"))
            {
                Advance();
                return new WindowFrameBound { Kind = WindowFrameBoundKind.UnboundedPreceding };
            }
            ExpectKeyword("FOLLOWING");
            return new WindowFrameBound { Kind = WindowFrameBoundKind.UnboundedFollowing };
        }

        if (PeekKeyword("CURRENT"))
        {
            Advance();
            ExpectKeyword("ROW");
            return new WindowFrameBound { Kind = WindowFrameBoundKind.CurrentRow };
        }

        var offsetToken = Expect(TokenType.Number);
        if (!int.TryParse(offsetToken.Value, out var offset) || offset < 0)
            throw new SqlParseException($"Window frame offset must be a non-negative integer at position {offsetToken.Pos}.");
        if (PeekKeyword("PRECEDING"))
        {
            Advance();
            return new WindowFrameBound { Kind = WindowFrameBoundKind.Preceding, Offset = offset };
        }
        ExpectKeyword("FOLLOWING");
        return new WindowFrameBound { Kind = WindowFrameBoundKind.Following, Offset = offset };
    }

    private SelectCondition ParseColumnRef()
    {
        if (Peek().Type != TokenType.Identifier && Peek().Type != TokenType.Operator)
        {
            if (Peek().Type == TokenType.EOF)
                return new FieldSelectCondition { FieldName = "" };
            return new ConstantSelectCondition { Constant = Advance().Value };
        }

        var token = Advance().Value;
        var parts = new List<string> { token };

        while (Peek().Type == TokenType.Dot)
        {
            Advance();
            if (Peek().Type == TokenType.Operator && Peek().Value == "*")
            {
                parts.Add("*");
                Advance();
                break;
            }
            parts.Add(Expect(TokenType.Identifier).Value);
        }

        var fieldName = string.Join(".", parts);

        if (parts.Count >= 2 && parts[^1] == "*")
            return new FieldSelectCondition { FieldName = fieldName };

        return new FieldSelectCondition { FieldName = fieldName };
    }

    private string ParseTableName()
    {
        var token = Expect(TokenType.Identifier).Value;
        var parts = new List<string> { token };

        while (Peek().Type == TokenType.Dot)
        {
            Advance();
            parts.Add(Expect(TokenType.Identifier).Value);
        }
        return string.Join(".", parts);
    }

    private List<object> ParseLiteralList()
    {
        if (Peek().Type == TokenType.RParen)
            throw new SqlParseException($"IN literal list cannot be empty at position {Peek().Pos}.");

        var values = new List<object> { ParseLiteralValue() };
        while (Peek().Type == TokenType.Comma)
        {
            Advance();
            if (Peek().Type == TokenType.RParen)
                throw new SqlParseException($"IN literal list cannot end with a comma at position {Peek().Pos}.");
            values.Add(ParseLiteralValue());
        }

        if (Peek().Type != TokenType.RParen)
        {
            var token = Peek();
            throw new SqlParseException(
                $"Expected ',' or ')' after IN literal but got {token.Type} ('{token.Value}') at position {token.Pos}.");
        }

        return values;
    }

    private object ParseLiteralValue()
    {
        if (Peek().Type == TokenType.Number)
        {
            var val = Advance().Value;
            return ParseNumericLiteral(val);
        }
        if (Peek().Type == TokenType.String)
        {
            var val = Advance().Value;
            return DecodeStringLiteral(val);
        }
        if (PeekKeyword("NULL")) { Advance(); return null!; }
        if (PeekKeyword("TRUE")) { Advance(); return true; }
        if (PeekKeyword("FALSE")) { Advance(); return false; }

        if (Peek().Type == TokenType.Operator && Peek().Value is "+" or "-")
        {
            var sign = Advance();
            var number = Peek();
            if (number.Type != TokenType.Number)
                throw new SqlParseException(
                    $"Expected numeric literal after unary '{sign.Value}' at position {number.Pos}.");

            var value = ParseNumericLiteral(Advance().Value);
            if (sign.Value == "+") return value;
            return value switch
            {
                int integer => (object)-integer,
                decimal decimalValue => (object)-decimalValue,
                _ => throw new SqlParseException($"Unsupported signed numeric literal at position {sign.Pos}.")
            };
        }

        var token = Peek();
        throw new SqlParseException(
            $"Expected literal value but got {token.Type} ('{token.Value}') at position {token.Pos}.");
    }

    private static object ParseNumericLiteral(string value)
    {
        if (!value.Contains('.') && !value.Contains('e', StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var integer))
            return integer;

        return decimal.Parse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string DecodeStringLiteral(string token) =>
        token[1..^1].Replace("''", "'", StringComparison.Ordinal);

    private static WhereCondition ConvertSelectToWhereCondition(SelectCondition expr)
    {
        if (expr is FieldSelectCondition f)
            return new BasicWhereCondition { FieldName = f.FieldName, Operator = "=", Value = true };
        if (expr is ConstantSelectCondition)
            return new BasicWhereCondition { FieldName = "1", Operator = "=", Value = 1 };
        if (expr is OperationSelectCondition op)
        {
            if (op.Operator is ArithmeticOperator.And or ArithmeticOperator.Or)
                return new GroupWhereCondition
                {
                    Groups = [ConvertSelectToWhereCondition(op.Left), ConvertSelectToWhereCondition(op.Right)],
                    IsOr = op.Operator == ArithmeticOperator.Or
                };
            return new BasicWhereCondition { FieldName = ExtractExprText(expr), Operator = "=", Value = true };
        }
        return new BasicWhereCondition { FieldName = ExtractExprText(expr), Operator = "=", Value = true };
    }

    private static void CollectWhereConditions(WhereCondition cond, List<WhereCondition> target)
    {
        if (cond is GroupWhereCondition g && g.Groups.Count > 0 && !g.IsOr)
        {
            foreach (var c in g.Groups) target.Add(c);
        }
        else if (cond is GroupWhereCondition g2 && g2.Groups.Count == 1)
        {
            target.Add(g2.Groups[0]);
        }
        else
            target.Add(cond);
    }

    private static string ExtractFieldName(SelectCondition expr)
    {
        if (expr is FieldSelectCondition f) return f.FieldName;
        return ExtractExprText(expr);
    }

    private static string ExtractExprText(SelectCondition expr)
    {
        if (expr is FieldSelectCondition f) return f.FieldName;
        if (expr is ConstantSelectCondition c) return c.Constant?.ToString() ?? "NULL";
        if (expr is OperationSelectCondition o)
            return $"({ExtractExprText(o.Left)} {OpToStr(o.Operator)} {ExtractExprText(o.Right)})";
        if (expr is FunctionSelectCondition fn)
            return $"{fn.FunctionName}(...)";
        if (expr is CastSelectCondition cast)
            return $"CAST({ExtractExprText(cast.Expression)} AS {cast.TypeName})";
        if (expr is IntervalSelectCondition interval)
            return $"INTERVAL '{interval.Literal.Replace("'", "''", StringComparison.Ordinal)}'";
        return "";
    }

    private static string OpToStr(ArithmeticOperator op) => op switch
    {
        ArithmeticOperator.Add => "+",
        ArithmeticOperator.Subtract => "-",
        ArithmeticOperator.Multiply => "*",
        ArithmeticOperator.Divide => "/",
        ArithmeticOperator.Modulo => "%",
        ArithmeticOperator.Concat => "||",
        ArithmeticOperator.Equal => "=",
        ArithmeticOperator.NotEqual => "<>",
        ArithmeticOperator.GreaterThan => ">",
        ArithmeticOperator.LessThan => "<",
        ArithmeticOperator.GreaterThanOrEqual => ">=",
        ArithmeticOperator.LessThanOrEqual => "<=",
        ArithmeticOperator.And => "AND",
        ArithmeticOperator.Or => "OR",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown expression operator.")
    };

    private static ArithmeticOperator ComparisonOperator(string op) => op switch
    {
        "=" => ArithmeticOperator.Equal,
        "<>" or "!=" => ArithmeticOperator.NotEqual,
        ">" => ArithmeticOperator.GreaterThan,
        "<" => ArithmeticOperator.LessThan,
        ">=" => ArithmeticOperator.GreaterThanOrEqual,
        "<=" => ArithmeticOperator.LessThanOrEqual,
        _ => throw new SqlParseException($"Unsupported comparison operator '{op}'.")
    };

    private static bool IsKeyword(string value) =>
        Keywords.Contains(value.ToUpperInvariant());

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "LIKE", "ILIKE",
        "BETWEEN", "IS", "NULL", "AS", "ON", "JOIN", "LEFT", "RIGHT", "INNER",
        "CROSS", "FULL", "OUTER", "ORDER", "BY", "GROUP", "HAVING", "LIMIT",
        "OFFSET", "ASC", "DESC", "DISTINCT", "ALL", "UNION", "INTERSECT",
        "EXCEPT", "WITH", "RECURSIVE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "TRUE", "FALSE", "EXISTS", "OVER", "PARTITION", "FILTER", "SET",
        "ROW", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING", "FOLLOWING", "CURRENT",
        "LATERAL", "USING", "NATURAL", "SOME", "ANY",
        "NULLS", "FIRST", "LAST", "INTERVAL"
    };

    private static bool IsComparisonOp(string op) => op switch
    {
        "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" => true,
        _ => false
    };

    private bool IsNextComparisonOp()
    {
        var t = Peek();
        return t.Type == TokenType.Operator && IsComparisonOp(t.Value);
    }

    private static WhereCondition ParseExprToWhereCondition(SelectCondition expr)
    {
        if (expr is FieldSelectCondition f)
            return new BasicWhereCondition { FieldName = f.FieldName, Operator = "=", Value = true };
        if (expr is OperationSelectCondition)
            return new GroupWhereCondition { Groups = [new BasicWhereCondition { FieldName = ExtractExprText(expr), Operator = "=", Value = true }] };
        if (expr is FunctionSelectCondition)
            return new GroupWhereCondition { Groups = [new BasicWhereCondition { FieldName = ExtractExprText(expr), Operator = "=", Value = true }] };
        return new BasicWhereCondition { FieldName = ExtractExprText(expr), Operator = "=", Value = true };
    }

    private bool PeekKeyword(string keyword)
    {
        return Peek().Type == TokenType.Keyword && Peek().Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private bool PeekTypeKeyword(string keyword)
    {
        var t = Peek();
        return (t.Type == TokenType.Keyword || t.Type == TokenType.Identifier)
            && t.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlParseException CapabilityError(string capability, Token token) =>
        new($"Unsupported SQL capability '{capability}' at position {token.Pos}; the statement was rejected to preserve semantics.");

    private Token Peek(int offset = 0)
    {
        var idx = _pos + offset;
        if (idx < 0) return tokens[0];
        return idx < tokens.Length ? tokens[idx] : tokens[^1];
    }

    private Token Advance()
    {
        return tokens[_pos++];
    }

    private Token Expect(TokenType type)
    {
        var token = Advance();
        if (token.Type != type)
            throw new SqlParseException($"Expected {type} but got {token.Type} ('{token.Value}') at position {token.Pos}");
        return token;
    }

    private void ExpectKeyword(string keyword)
    {
        var token = Advance();
        if (token.Type != TokenType.Keyword || !token.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            throw new SqlParseException($"Expected keyword '{keyword}' but got '{token.Value}' at position {token.Pos}");
    }

    private void ExpectOperator(string op)
    {
        var token = Advance();
        if (token.Type != TokenType.Operator || token.Value != op)
            throw new SqlParseException($"Expected operator '{op}' but got '{token.Value}' at position {token.Pos}");
    }
}

public class SqlParseException(string message) : Exception(message) { }
