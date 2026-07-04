using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using System.Text.RegularExpressions;

namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Parses a lightweight template expression into a SelectCondition tree.
/// Template syntax: $1..$N (arg refs), @SQL_TOKEN (dialect grammar token), FUNC(args), expr OP expr, 'string', 123, (expr)
/// </summary>
public partial class FunctionTemplateEngine(string template)
{
    private readonly string _template = template.Trim();
    private int _pos;
    private const char EOF = '\0';

    /// <summary>
    /// Supported date format style keys for the :date_format modifier.
    /// Each Strategy declares which style it targets (e.g. 'pg', 'sqlite', 'mssql').
    /// </summary>
    private enum DateStyle { Sqlite, Mssql, Pg }

    private static readonly Dictionary<string, DateStyle> DateStyleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqlite"] = DateStyle.Sqlite,
        ["mssql"]  = DateStyle.Mssql,
        ["pg"]     = DateStyle.Pg,
        ["oracle"] = DateStyle.Pg,     // Oracle uses same token style as Postgres (YYYY, MM, DD)
    };

    private static readonly Dictionary<string, Func<SelectCondition, IList<SelectCondition>?, SelectCondition>> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        // date_format: translates the $N node's date format string to the target dialect.
        // Usage: $2:date_format('pg')  — the modifier arg is a style key, NOT a format string.
        // The style key tells the engine which dialect's tokens to emit.
        // If the input is empty or the style key is unknown, the node is returned unchanged.
        ["date_format"] = (node, modifierArgs) =>
        {
            if (modifierArgs is { Count: > 0 }
                && modifierArgs[0] is ConstantSelectCondition fmtArg
                && fmtArg.Constant is string styleKey
                && DateStyleMap.TryGetValue(styleKey, out var targetStyle))
            {
                string inputFormat = "";
                if (node is ConstantSelectCondition constNode && constNode.Constant is string s)
                {
                    inputFormat = s;
                }

                string mappedFmt = TranslateDateFormat(inputFormat, targetStyle);

                if (node is ConstantSelectCondition targetNode)
                    targetNode.Constant = mappedFmt;
                else
                    return new ConstantSelectCondition { Constant = mappedFmt };
            }
            return node;
        }
    };

    /// <summary>
    /// Translates an input date format string (from any dialect) into the target dialect's token style.
    /// Recognises two input families:
    ///   1. %-style tokens (MySQL/SQLite): %Y, %m, %d, %H, %i, %S, etc.
    ///   2. Named tokens (MSSQL/Postgres/Oracle): yyyy, MM, dd, HH, mm, SS, YYYY, DD, MI, etc.
    /// Non-token characters (separators like - / : space) are preserved as-is.
    /// </summary>
    private static string TranslateDateFormat(string input, DateStyle target)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        // --- Branch 1: %-style input (MySQL/SQLite) ---
        if (input.Contains('%'))
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '%' && i + 1 < input.Length)
                {
                    char specifier = input[i + 1];
                    i++; // skip specifier
                    sb.Append(MapToken(specifier, target));
                }
                else
                {
                    sb.Append(input[i]);
                }
            }
            return sb.ToString();
        }

        // --- Branch 2: Named-token input (MSSQL/Postgres/Oracle) ---
        string result = input;

        // Normalize input tokens to abstract placeholders first (order matters: longest match first)
        result = Year4Regex().Replace(result, "{Y}");
        result = Year2Regex().Replace(result, "{y}");
        result = Hour24Regex().Replace(result, "{H}");   // Postgres 24h — before generic HH
        result = Hour12Regex().Replace(result, "{h}");   // Postgres 12h — before generic HH
        result = HourHRegex().Replace(result, "{H}");
        result = HourhRegex().Replace(result, "{h}");
        result = MinuteMIRegex().Replace(result, "{m}");      // Postgres/Oracle minute — before MM
        result = MonthRegex().Replace(result, "{M}");
        result = DayRegex().Replace(result, "{D}");
        result = MinuteMMRegex().Replace(result, "{m}");  // MSSQL minute
        result = SecondRegex().Replace(result, "{s}");

        // Map placeholders to target style
        result = result.Replace("{Y}", Emit('Y', target));
        result = result.Replace("{y}", Emit('y', target));
        result = result.Replace("{M}", Emit('M', target));
        result = result.Replace("{D}", Emit('D', target));
        result = result.Replace("{H}", Emit('H', target));
        result = result.Replace("{h}", Emit('h', target));
        result = result.Replace("{m}", Emit('m', target));
        result = result.Replace("{s}", Emit('s', target));

        return result;
    }

    [GeneratedRegex("yyyy|YYYY")]
    private static partial Regex Year4Regex();

    [GeneratedRegex(@"(?<!\{)yy(?!\})|(?<!\{)YY(?!\})")]
    private static partial Regex Year2Regex();

    [GeneratedRegex("HH24")]
    private static partial Regex Hour24Regex();

    [GeneratedRegex("HH12")]
    private static partial Regex Hour12Regex();

    [GeneratedRegex("HH")]
    private static partial Regex HourHRegex();

    [GeneratedRegex("hh")]
    private static partial Regex HourhRegex();

    [GeneratedRegex("MI")]
    private static partial Regex MinuteMIRegex();

    [GeneratedRegex("MM")]
    private static partial Regex MonthRegex();

    [GeneratedRegex("dd|DD")]
    private static partial Regex DayRegex();

    [GeneratedRegex(@"(?<!\{)mm(?!\})")]
    private static partial Regex MinuteMMRegex();

    [GeneratedRegex("ss|SS")]
    private static partial Regex SecondRegex();

    /// <summary>Maps a %-style specifier char to the target dialect's token.</summary>
    private static string MapToken(char specifier, DateStyle target) => specifier switch
    {
        'Y'      => Emit('Y', target),
        'y'      => Emit('y', target),
        'm'      => Emit('M', target),
        'd'      => Emit('D', target),
        'e'      => target switch { DateStyle.Sqlite => "%d", DateStyle.Mssql => "d", _ => "FMDD" },
        'H'      => Emit('H', target),
        'h' or 'I' => Emit('h', target),
        'i'      => Emit('m', target),
        's' or 'S' => Emit('s', target),
        _        => "%" + specifier
    };

    /// <summary>Emits the canonical token for the given abstract slot in the target dialect.</summary>
    private static string Emit(char slot, DateStyle target) => (slot, target) switch
    {
        ('Y', DateStyle.Sqlite) => "%Y",   ('Y', DateStyle.Mssql) => "yyyy", ('Y', _) => "YYYY",
        ('y', DateStyle.Sqlite) => "%y",   ('y', DateStyle.Mssql) => "yy",   ('y', _) => "YY",
        ('M', DateStyle.Sqlite) => "%m",   ('M', _)               => "MM",
        ('D', DateStyle.Sqlite) => "%d",   ('D', DateStyle.Mssql) => "dd",   ('D', _) => "DD",
        ('H', DateStyle.Sqlite) => "%H",   ('H', DateStyle.Mssql) => "HH",   ('H', _) => "HH24",
        ('h', DateStyle.Sqlite) => "%I",   ('h', DateStyle.Mssql) => "hh",   ('h', _) => "HH12",
        ('m', DateStyle.Sqlite) => "%i",   ('m', DateStyle.Mssql) => "mm",   ('m', _) => "MI",
        ('s', DateStyle.Sqlite) => "%S",   ('s', DateStyle.Mssql) => "ss",   ('s', _) => "SS",
        _ => ""
    };

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

    private static SelectCondition ResolveArgs(SelectCondition node, IList<SelectCondition>? args)
    {
        switch (node)
        {
            case ArgRefSelectCondition arg:
                if (args == null || arg.Index < 0 || arg.Index >= args.Count)
                    return node;
                var resolvedNode = args[arg.Index];
                if (!string.IsNullOrEmpty(arg.Modifier))
                {
                    if (Modifiers.TryGetValue(arg.Modifier, out var modifierFunc))
                    {
                        // Clone the node to prevent mutation of the original AST argument
                        if (resolvedNode is ConstantSelectCondition constNode)
                        {
                            resolvedNode = new ConstantSelectCondition { Constant = constNode.Constant };
                        }
                        // Resolve any modifier args (they may themselves reference $N)
                        var resolvedModifierArgs = arg.ModifierArgs?.Select(a => ResolveArgs(a, args)).ToList();
                        resolvedNode = modifierFunc(resolvedNode, resolvedModifierArgs);
                    }
                }
                return resolvedNode;

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

    // primary = '$' digit+ [ ':' modifier ] | @' sql-token | FUNC '(' expr (',' expr)* ')' | '\'' string '\'' | number | '(' expr ')'
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
            var index = int.Parse(numStr) - 1;

            string? modifier = null;
            List<SelectCondition>? modifierArgs = null;
            if (Peek() == ':')
            {
                Advance(); // Skip ':'
                modifier = "";
                while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
                    modifier += Advance();

                // Optional modifier arguments: $1:date_format('yyyy-MM-dd')
                if (Peek() == '(')
                {
                    Advance(); // consume '('
                    modifierArgs = [];
                    SkipWs();
                    if (Peek() != ')')
                    {
                        modifierArgs.Add(ParseExpr());
                        while (Peek() == ',')
                        {
                            Advance();
                            SkipWs();
                            modifierArgs.Add(ParseExpr());
                        }
                    }
                    SkipWs();
                    if (Peek() == ')') Advance();
                }
            }

            return new ArgRefSelectCondition { Index = index, Modifier = modifier, ModifierArgs = modifierArgs };
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
        public string? Modifier { get; set; }
        /// <summary>Parsed arguments to the modifier, e.g. the 'yyyy-MM-dd' in $1:date_format('yyyy-MM-dd').</summary>
        public List<SelectCondition>? ModifierArgs { get; set; }
    }
}
