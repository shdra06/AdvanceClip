using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Lightweight math expression evaluator using the Shunting-yard algorithm.
    /// Supports: +, -, *, /, ^, parentheses, and functions (sqrt, sin, cos, tan, log, abs).
    /// No external dependencies.
    /// </summary>
    public static class MathSolver
    {
        // ═══ Public API ═══

        /// <summary>
        /// Try to evaluate a math expression. Returns true if successfully solved.
        /// </summary>
        public static bool TrySolveExpression(string input, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            try
            {
                // Clean up the input
                string expr = NormalizeExpression(input);
                if (string.IsNullOrEmpty(expr)) return false;

                // Don't evaluate if it contains 'x' (that's a plottable equation)
                if (ContainsVariable(expr)) return false;

                // Must contain at least one operator or function to be a "math expression"
                if (!Regex.IsMatch(expr, @"[\+\-\*\/\^\(\)]|sqrt|sin|cos|tan|log|abs"))
                    return false;

                // Must not be just a plain number
                if (double.TryParse(expr, out _)) return false;

                var tokens = Tokenize(expr);
                var rpn = ShuntingYard(tokens);
                result = EvaluateRPN(rpn);

                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the expression contains variable 'x' (plottable).
        /// </summary>
        public static bool IsPlottableEquation(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            string expr = NormalizeExpression(input);
            return ContainsVariable(expr) && Regex.IsMatch(expr, @"[\+\-\*\/\^\(\)]|sqrt|sin|cos|tan|log|abs|\d");
        }

        /// <summary>
        /// Evaluate an expression at a given x value (for graph plotting).
        /// </summary>
        public static double EvaluateAtX(string input, double x)
        {
            try
            {
                string expr = NormalizeExpression(input);
                // Replace 'x' with the value (handle implicit multiplication: 3x → 3*x)
                expr = Regex.Replace(expr, @"(\d)(x)", "$1*" + x.ToString("R"));
                expr = Regex.Replace(expr, @"(x)(\d)", x.ToString("R") + "*$2");
                expr = expr.Replace("x", x.ToString("R"));

                var tokens = Tokenize(expr);
                var rpn = ShuntingYard(tokens);
                return EvaluateRPN(rpn);
            }
            catch
            {
                return double.NaN;
            }
        }

        // ═══ Expression Normalization ═══

        private static string NormalizeExpression(string input)
        {
            string expr = input.Trim().ToLower();

            // Remove common non-math prefixes
            expr = Regex.Replace(expr, @"^(solve|calculate|eval|compute|what is|find)\s*:?\s*", "", RegexOptions.IgnoreCase);

            // Replace × with *, ÷ with /
            expr = expr.Replace("×", "*").Replace("÷", "/").Replace("**", "^");

            // Remove spaces (except around functions)
            expr = Regex.Replace(expr, @"\s+", "");

            // Handle implicit multiplication: 2(3) → 2*(3), (3)(4) → (3)*(4), 2x → 2*x
            expr = Regex.Replace(expr, @"(\d)\(", "$1*(");
            expr = Regex.Replace(expr, @"\)\(", ")*(");
            expr = Regex.Replace(expr, @"\)(\d)", ")*$1");

            return expr;
        }

        private static bool ContainsVariable(string expr)
        {
            // Check for 'x' that's not part of a function name (like 'exp')
            return Regex.IsMatch(expr, @"(?<![a-z])x(?![a-z])");
        }

        // ═══ Tokenizer ═══

        private enum TokenType { Number, Operator, LeftParen, RightParen, Function }

        private struct Token
        {
            public TokenType Type;
            public string Value;
            public double NumValue;
        }

        private static List<Token> Tokenize(string expr)
        {
            var tokens = new List<Token>();
            int i = 0;

            while (i < expr.Length)
            {
                char c = expr[i];

                if (char.IsDigit(c) || c == '.')
                {
                    int start = i;
                    while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.')) i++;
                    string numStr = expr.Substring(start, i - start);
                    tokens.Add(new Token { Type = TokenType.Number, Value = numStr, NumValue = double.Parse(numStr) });
                    continue;
                }

                if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < expr.Length && char.IsLetter(expr[i])) i++;
                    string word = expr.Substring(start, i - start);

                    // Known constants
                    if (word == "pi") { tokens.Add(new Token { Type = TokenType.Number, Value = "pi", NumValue = Math.PI }); continue; }
                    if (word == "e") { tokens.Add(new Token { Type = TokenType.Number, Value = "e", NumValue = Math.E }); continue; }

                    // Functions
                    tokens.Add(new Token { Type = TokenType.Function, Value = word });
                    continue;
                }

                if (c == '(') { tokens.Add(new Token { Type = TokenType.LeftParen, Value = "(" }); i++; continue; }
                if (c == ')') { tokens.Add(new Token { Type = TokenType.RightParen, Value = ")" }); i++; continue; }

                if ("+-*/^%".Contains(c))
                {
                    // Handle unary minus: at start, after '(', or after another operator
                    if (c == '-' && (tokens.Count == 0 || tokens[tokens.Count - 1].Type == TokenType.LeftParen || tokens[tokens.Count - 1].Type == TokenType.Operator))
                    {
                        // Unary minus → multiply by -1
                        tokens.Add(new Token { Type = TokenType.Number, Value = "-1", NumValue = -1 });
                        tokens.Add(new Token { Type = TokenType.Operator, Value = "*" });
                    }
                    else
                    {
                        tokens.Add(new Token { Type = TokenType.Operator, Value = c.ToString() });
                    }
                    i++;
                    continue;
                }

                i++; // Skip unknown characters
            }

            return tokens;
        }

        // ═══ Shunting-Yard Algorithm ═══

        private static int Precedence(string op)
        {
            return op switch
            {
                "+" or "-" => 1,
                "*" or "/" or "%" => 2,
                "^" => 3,
                _ => 0
            };
        }

        private static bool IsRightAssociative(string op) => op == "^";

        private static List<Token> ShuntingYard(List<Token> tokens)
        {
            var output = new List<Token>();
            var stack = new Stack<Token>();

            foreach (var token in tokens)
            {
                switch (token.Type)
                {
                    case TokenType.Number:
                        output.Add(token);
                        break;

                    case TokenType.Function:
                        stack.Push(token);
                        break;

                    case TokenType.Operator:
                        while (stack.Count > 0 && stack.Peek().Type == TokenType.Operator)
                        {
                            var top = stack.Peek();
                            if ((IsRightAssociative(token.Value) && Precedence(top.Value) > Precedence(token.Value)) ||
                                (!IsRightAssociative(token.Value) && Precedence(top.Value) >= Precedence(token.Value)))
                            {
                                output.Add(stack.Pop());
                            }
                            else break;
                        }
                        stack.Push(token);
                        break;

                    case TokenType.LeftParen:
                        stack.Push(token);
                        break;

                    case TokenType.RightParen:
                        while (stack.Count > 0 && stack.Peek().Type != TokenType.LeftParen)
                            output.Add(stack.Pop());
                        if (stack.Count > 0) stack.Pop(); // Remove '('
                        if (stack.Count > 0 && stack.Peek().Type == TokenType.Function)
                            output.Add(stack.Pop());
                        break;
                }
            }

            while (stack.Count > 0)
                output.Add(stack.Pop());

            return output;
        }

        // ═══ RPN Evaluator ═══

        private static double EvaluateRPN(List<Token> rpn)
        {
            var stack = new Stack<double>();

            foreach (var token in rpn)
            {
                if (token.Type == TokenType.Number)
                {
                    stack.Push(token.NumValue);
                }
                else if (token.Type == TokenType.Operator)
                {
                    if (stack.Count < 2) throw new InvalidOperationException("Invalid expression");
                    double b = stack.Pop();
                    double a = stack.Pop();
                    stack.Push(token.Value switch
                    {
                        "+" => a + b,
                        "-" => a - b,
                        "*" => a * b,
                        "/" => b != 0 ? a / b : double.NaN,
                        "^" => Math.Pow(a, b),
                        "%" => b != 0 ? a % b : double.NaN,
                        _ => throw new InvalidOperationException($"Unknown operator: {token.Value}")
                    });
                }
                else if (token.Type == TokenType.Function)
                {
                    if (stack.Count < 1) throw new InvalidOperationException("Missing argument for function");
                    double a = stack.Pop();
                    stack.Push(token.Value switch
                    {
                        "sqrt" => Math.Sqrt(a),
                        "sin" => Math.Sin(a),
                        "cos" => Math.Cos(a),
                        "tan" => Math.Tan(a),
                        "log" => Math.Log10(a),
                        "ln" => Math.Log(a),
                        "abs" => Math.Abs(a),
                        "floor" => Math.Floor(a),
                        "ceil" => Math.Ceiling(a),
                        "round" => Math.Round(a),
                        "exp" => Math.Exp(a),
                        _ => throw new InvalidOperationException($"Unknown function: {token.Value}")
                    });
                }
            }

            return stack.Count == 1 ? stack.Pop() : throw new InvalidOperationException("Invalid expression");
        }
    }
}
