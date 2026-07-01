using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Parses a lightweight template expression into a SelectCondition tree.
/// Template syntax: $1..$N (arg refs), @SQL_TOKEN (dialect grammar token), FUNC(args), expr OP expr, 'string', 123, (expr)
/// </summary>
public class FunctionTemplateEngine
{
    private readonly string _template;
    private int _pos;
    private const char EOF = '\0';

    public FunctionTemplateEngine(string template)
    {
        _template = template.Trim();
    }

    public SelectCondition? Translate(IList<SelectCondition>? sourceArgs)
    {
        if (string.IsNullOrWhiteSpace(_template))
            return null;
        _pos = 0;
        var result = ParseExpr();
        if (_pos < _template.Length)
            return null;
        return ResolveArgs(result, sourceArgs);
    }

    private SelectCondition ResolveArgs(SelectCondition node, IList<SelectCondition>? args)
    {
        switch (node)
        {
            case ArgRefSelectCondition arg:
                if (args == null || arg.Index < 0 || arg.Index >= args.Count)
                    return node;
                return args[arg.Index];

            case OperationSelectCondition op:
                op.Left = ResolveArgs(op.Left, args);
                op.Right = ResolveArgs(op.Right, args);
                return op;

            case FunctionSelectCondition fn:
                if (fn.Arguments != null)
                {
                    for (var i = 0; i < fn.Arguments.Count; i++)
                        fn.Arguments[i] = ResolveArgs(fn.Arguments[i], args);
                }
                return fn;

            default:
                return node;
        }
    }

    // expr = additive
    private SelectCondition ParseExpr()
    {
        return ParseAdditive();
    }

    // additive = multiplicative (('+' | '-') multiplicative)*
    private SelectCondition ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            SkipWs();
            if (Match('+'))
                left = new OperationSelectCondition { Left = left, Operator = ArithmeticOperator.Add, Right = ParseMultiplicative() };
            else if (Match('-'))
                left = new OperationSelectCondition { Left = left, Operator = ArithmeticOperator.Subtract, Right = ParseMultiplicative() };
            else
                break;
        }
        return left;
    }

    // multiplicative = primary (('*' | '/') primary)*
    private SelectCondition ParseMultiplicative()
    {
        var left = ParsePrimary();
        while (true)
        {
            SkipWs();
            if (Match('*'))
                left = new OperationSelectCondition { Left = left, Operator = ArithmeticOperator.Multiply, Right = ParsePrimary() };
            else if (Match('/'))
                left = new OperationSelectCondition { Left = left, Operator = ArithmeticOperator.Divide, Right = ParsePrimary() };
            else
                break;
        }
        return left;
    }

    // primary = '$' digit+ | '@' sql-token | FUNC '(' expr (',' expr)* ')' | '\'' string '\'' | number | '(' expr ')'
    private SelectCondition ParsePrimary()
    {
        SkipWs();
        var ch = Peek();

        // Argument reference: $1, $2, ...
        if (ch == '$')
        {
            Advance();
            var numStr = "";
            while (char.IsDigit(Peek()))
                numStr += Advance();
            if (numStr.Length == 0) throw new FormatException("Expected digit after $ in template");
            return new ArgRefSelectCondition { Index = int.Parse(numStr) - 1 };
        }

        // Dialect grammar token for keywords that must not be parameterized, e.g. @Day or @CurrentTimestamp.
        if (ch == '@')
        {
            Advance();
            var token = "";
            while (char.IsLetterOrDigit(Peek()) || Peek() is '_' or '.')
                token += Advance();
            if (token.Length == 0) throw new FormatException("Expected SQL token after @ in template");
            return new TemplateSqlTokenSelectCondition { Token = token };
        }

        // String constant
        if (ch == '\'')
        {
            Advance();
            var val = "";
            while (Peek() != '\'' && Peek() != EOF)
                val += Advance();
            if (Peek() == '\'') Advance();
            return new ConstantSelectCondition { Constant = val };
        }

        // Number constant
        if (ch == '-' || char.IsDigit(ch))
        {
            var numStr = "";
            if (Match('-')) numStr += "-";
            while (char.IsDigit(Peek()))
                numStr += Advance();
            if (Peek() == '.')
            {
                numStr += '.';
                Advance();
                while (char.IsDigit(Peek()))
                    numStr += Advance();
            }
            if (int.TryParse(numStr, out var intVal))
                return new ConstantSelectCondition { Constant = intVal };
            return new ConstantSelectCondition { Constant = numStr };
        }

        // Parenthesized expression
        if (ch == '(')
        {
            Advance();
            var expr = ParseExpr();
            SkipWs();
            if (Peek() == ')') Advance();
            return expr;
        }

        // Function call: NAME(...)
        if (char.IsLetter(ch) || ch == '_')
        {
            var name = "";
            while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
                name += Advance();
            SkipWs();
            if (Peek() == '(')
            {
                Advance();
                var args = new List<SelectCondition>();
                SkipWs();
                if (Peek() != ')')
                {
                    args.Add(ParseExpr());
                    while (Peek() == ',')
                    {
                        Advance();
                        SkipWs();
                        args.Add(ParseExpr());
                    }
                }
                SkipWs();
                if (Peek() == ')') Advance();
                return new FunctionSelectCondition
                {
                    FunctionName = name,
                    Arguments = args,
                };
            }
            // Bare identifier — treat as constant
            return new ConstantSelectCondition { Constant = name };
        }

        throw new FormatException($"Unexpected character '{ch}' at position {_pos} in template: {_template}");
    }

    private char Peek() => _pos < _template.Length ? _template[_pos] : EOF;
    private char Advance() => _pos < _template.Length ? _template[_pos++] : EOF;
    private void SkipWs() { while (char.IsWhiteSpace(Peek())) Advance(); }
    private bool Match(char c) { if (Peek() == c) { Advance(); return true; } return false; }

    /// <summary>
    /// Internal placeholder for argument references in templates.
    /// Resolved to actual args at translation time.
    /// </summary>
    private class ArgRefSelectCondition : SelectCondition
    {
        public int Index { get; set; }
    }
}
