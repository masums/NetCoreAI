using System.ComponentModel;
using System.Globalization;

namespace NetCoreAI.Tools.BuiltIn;

/// <summary>Today's date and the time, which a model otherwise guesses at from its training data.</summary>
public sealed class DateTimeTool
{
    [AITool("current_date_time", Description = "The current date and time in UTC. Use this whenever the answer depends on today's date rather than guessing.")]
    public static string Now() =>
        DateTimeOffset.UtcNow.ToString("dddd d MMMM yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Arithmetic, which language models are famously unreliable at.
/// </summary>
/// <remarks>
/// A hand-written parser rather than <c>DataTable.Compute</c> or anything that evaluates a string as code.
/// The input comes from a model, which means in practice it comes from whoever is talking to the model, and
/// an expression evaluator that can reach a type system is a way to run code by asking nicely.
/// </remarks>
public sealed class CalculatorTool
{
    [AITool("calculate", Description = "Work out an arithmetic expression, such as (1200 * 1.2) / 3. Supports + - * / % ^ and parentheses.")]
    public static string Calculate([Description("The expression to evaluate.")] string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            // Returned, not thrown. A tool that throws ends the turn; one that answers lets the model ask
            // again with something usable.
            return "That is not an expression I can work out: give something like (1200 * 1.2) / 3.";
        }

        try
        {
            var value = Expression.Evaluate(expression);
            return double.IsFinite(value)
                ? value.ToString("0.############", CultureInfo.InvariantCulture)

                // Saying so beats returning "∞", which a model will happily put in a sentence.
                : "That does not have a finite answer (a division by zero, or a number too large to represent).";
        }
        catch (FormatException ex)
        {
            return $"That is not an expression I can work out: {ex.Message}";
        }
    }

    /// <summary>A recursive-descent parser over the four operations, powers, and parentheses.</summary>
    internal static class Expression
    {
        public static double Evaluate(string input)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input);

            var position = 0;
            var value = ParseAdditive(input, ref position);
            SkipSpace(input, ref position);

            return position < input.Length
                ? throw new FormatException($"unexpected '{input[position]}' at position {position}")
                : value;
        }

        private static double ParseAdditive(string s, ref int i)
        {
            var left = ParseMultiplicative(s, ref i);
            while (true)
            {
                SkipSpace(s, ref i);
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    var op = s[i++];
                    var right = ParseMultiplicative(s, ref i);
                    left = op == '+' ? left + right : left - right;
                }
                else
                {
                    return left;
                }
            }
        }

        private static double ParseMultiplicative(string s, ref int i)
        {
            var left = ParsePower(s, ref i);
            while (true)
            {
                SkipSpace(s, ref i);
                if (i < s.Length && (s[i] == '*' || s[i] == '/' || s[i] == '%'))
                {
                    var op = s[i++];
                    var right = ParsePower(s, ref i);
                    left = op switch
                    {
                        '*' => left * right,
                        '/' => left / right,
                        _ => left % right,
                    };
                }
                else
                {
                    return left;
                }
            }
        }

        private static double ParsePower(string s, ref int i)
        {
            var left = ParseUnary(s, ref i);
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '^')
            {
                i++;

                // Right-associative: 2^3^2 is 2^(3^2), which is what a person writing it means.
                return Math.Pow(left, ParsePower(s, ref i));
            }

            return left;
        }

        private static double ParseUnary(string s, ref int i)
        {
            SkipSpace(s, ref i);
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            {
                var sign = s[i++] == '-' ? -1 : 1;
                return sign * ParseUnary(s, ref i);
            }

            return ParsePrimary(s, ref i);
        }

        private static double ParsePrimary(string s, ref int i)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length)
            {
                throw new FormatException("it ends before it says what to work out");
            }

            if (s[i] == '(')
            {
                i++;
                var inner = ParseAdditive(s, ref i);
                SkipSpace(s, ref i);
                if (i >= s.Length || s[i] != ')')
                {
                    throw new FormatException("a bracket is opened and never closed");
                }

                i++;
                return inner;
            }

            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.' || s[i] == '_' || s[i] == ','))
            {
                // Thousands separators are dropped: a model writing 1,200 means one thousand two hundred.
                i++;
            }

            if (i == start)
            {
                throw new FormatException($"unexpected '{s[i]}' at position {i}");
            }

            var number = s[start..i].Replace(",", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"'{number}' is not a number");
        }

        private static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }
    }
}
