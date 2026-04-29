// <copyright file="TemplateSyntaxConverter.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
//
//  Derived from “MailQueueNet” by Daniel Cohen Gindi
//  (https://github.com/danielgindi/MailQueueNet).
//
//  Original portions:
//    © 2014 Daniel Cohen Gindi (danielgindi@gmail.com)
//    Licensed under the MIT Licence.
//  Modifications and additions:
//    © 2025 IBC Digital Pty Ltd
//    Distributed under the same MIT Licence.
//
//  The above notice and this permission notice shall be included in
//  all copies or substantial portions of this file.
// </copyright>

namespace MailQueueNet.Common.Templating
{
    using System;
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using MailForge.Grpc;

    /// <summary>
    /// Provides best-effort conversion between supported template syntaxes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This converter is intentionally conservative and focuses on common, simple variable interpolation
    /// patterns used in mail subjects and bodies (for example <c>{{ Name }}</c> and <c>{{Name}}</c>).
    /// More advanced syntax such as conditionals, loops, helpers, partials, and filters may not be
    /// convertible and will be left unchanged.
    /// </para>
    /// <para>
    /// In this repository, "Fluid" templates are treated as Liquid-style templates, represented by
    /// <see cref="TemplateEngine.Liquid" />.
    /// </para>
    /// </remarks>
    public static class TemplateSyntaxConverter
    {
        private static readonly Regex HandlebarsTripleRegex = new Regex("\\{\\{\\{\\s*(?<expr>[^{}]+?)\\s*\\}\\}\\}", RegexOptions.Compiled);
        private static readonly Regex HandlebarsDoubleRegex = new Regex("\\{\\{(?!\\{)\\s*(?<expr>[^{}]+?)\\s*\\}\\}(?!\\})", RegexOptions.Compiled);

        private static readonly Regex LiquidDoubleRegex = new Regex("\\{\\{\\s*(?<expr>[^{}]+?)\\s*\\}\\}", RegexOptions.Compiled);

        /// <summary>
        /// Attempts to convert a template from one supported engine syntax to another.
        /// </summary>
        /// <param name="template">The template text to convert.</param>
        /// <param name="fromEngine">The engine syntax to convert from.</param>
        /// <param name="toEngine">The engine syntax to convert to.</param>
        /// <param name="converted">On success, receives the converted template.</param>
        /// <param name="message">On success, receives a conversion note or warning message (if any).</param>
        /// <returns>
        /// <c>true</c> when conversion was performed (or no conversion was required); otherwise <c>false</c>.
        /// </returns>
        public static bool TryConvert(string? template, TemplateEngine fromEngine, TemplateEngine toEngine, out string converted, out string message)
        {
            template ??= string.Empty;

            if (fromEngine == toEngine)
            {
                converted = template;
                message = string.Empty;
                return true;
            }

            if (IsLiquid(fromEngine) && IsHandlebars(toEngine))
            {
                return TryConvertLiquidToHandlebars(template, out converted, out message);
            }

            if (IsHandlebars(fromEngine) && IsLiquid(toEngine))
            {
                return TryConvertHandlebarsToLiquid(template, out converted, out message);
            }

            converted = template;
            message = $"Unsupported conversion: {fromEngine} to {toEngine}.";
            return false;
        }

        /// <summary>
        /// Determines whether the specified engine represents a Liquid/Fluid template.
        /// </summary>
        /// <param name="engine">The engine value to test.</param>
        /// <returns><c>true</c> when the engine is Liquid/Fluid; otherwise <c>false</c>.</returns>
        private static bool IsLiquid(TemplateEngine engine)
        {
            return engine == TemplateEngine.Liquid;
        }

        /// <summary>
        /// Determines whether the specified engine represents a Handlebars template.
        /// </summary>
        /// <param name="engine">The engine value to test.</param>
        /// <returns><c>true</c> when the engine is Handlebars; otherwise <c>false</c>.</returns>
        private static bool IsHandlebars(TemplateEngine engine)
        {
            return engine == TemplateEngine.Handlebars;
        }

        /// <summary>
        /// Attempts to convert a Liquid/Fluid template to Handlebars syntax.
        /// </summary>
        /// <param name="template">The Liquid/Fluid template text.</param>
        /// <param name="converted">On success, receives the converted text.</param>
        /// <param name="message">On success, receives a note about any unconverted expressions.</param>
        /// <returns><c>true</c> when conversion was performed.</returns>
        private static bool TryConvertLiquidToHandlebars(string template, out string converted, out string message)
        {
            var warnings = new List<string>();

            converted = LiquidDoubleRegex.Replace(template, m =>
            {
                var expr = (m.Groups["expr"].Value ?? string.Empty).Trim();
                expr = TrimThisPrefix(expr);

                if (string.IsNullOrWhiteSpace(expr))
                {
                    return m.Value;
                }

                if (LooksLikeLiquidExpressionThatIsNotSimpleVariable(expr))
                {
                    warnings.Add("Some Liquid expressions were not converted.");
                    return m.Value;
                }

                return "{{" + expr + "}}";
            });

            message = BuildMessage(warnings);
            return true;
        }

        /// <summary>
        /// Attempts to convert a Handlebars template to Liquid/Fluid syntax.
        /// </summary>
        /// <param name="template">The Handlebars template text.</param>
        /// <param name="converted">On success, receives the converted text.</param>
        /// <param name="message">On success, receives a note about any unconverted expressions.</param>
        /// <returns><c>true</c> when conversion was performed.</returns>
        private static bool TryConvertHandlebarsToLiquid(string template, out string converted, out string message)
        {
            var warnings = new List<string>();

            converted = HandlebarsTripleRegex.Replace(template, m =>
            {
                var expr = (m.Groups["expr"].Value ?? string.Empty).Trim();
                expr = TrimThisPrefix(expr);

                if (string.IsNullOrWhiteSpace(expr))
                {
                    return m.Value;
                }

                warnings.Add("Triple-stash Handlebars expressions were converted to Liquid and may change HTML escaping behaviour.");
                return "{{ " + expr + " }}";
            });

            converted = HandlebarsDoubleRegex.Replace(converted, m =>
            {
                var expr = (m.Groups["expr"].Value ?? string.Empty).Trim();
                expr = TrimThisPrefix(expr);

                if (string.IsNullOrWhiteSpace(expr))
                {
                    return m.Value;
                }

                if (LooksLikeHandlebarsControlExpression(expr))
                {
                    warnings.Add("Some Handlebars block/control expressions were not converted.");
                    return m.Value;
                }

                return "{{ " + expr + " }}";
            });

            message = BuildMessage(warnings);
            return true;
        }

        /// <summary>
        /// Removes a leading <c>this.</c> prefix from a template expression, when present.
        /// </summary>
        /// <param name="expr">The expression to normalise.</param>
        /// <returns>The normalised expression.</returns>
        private static string TrimThisPrefix(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return string.Empty;
            }

            if (expr.StartsWith("this.", StringComparison.OrdinalIgnoreCase))
            {
                return expr.Substring("this.".Length);
            }

            return expr;
        }

        /// <summary>
        /// Determines whether a Handlebars expression appears to be a block or control tag that cannot
        /// be safely converted to Liquid/Fluid.
        /// </summary>
        /// <param name="expr">The expression text.</param>
        /// <returns><c>true</c> when the expression looks like a control expression; otherwise <c>false</c>.</returns>
        private static bool LooksLikeHandlebarsControlExpression(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return false;
            }

            var first = expr[0];
            return first == '#' || first == '/' || first == '^' || first == '>' || first == '!';
        }

        /// <summary>
        /// Determines whether a Liquid/Fluid interpolation appears to include filters or operators that
        /// cannot be represented in a simple Handlebars expression.
        /// </summary>
        /// <param name="expr">The expression text.</param>
        /// <returns><c>true</c> when the expression appears complex; otherwise <c>false</c>.</returns>
        private static bool LooksLikeLiquidExpressionThatIsNotSimpleVariable(string expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return false;
            }

            if (expr.Contains('|'))
            {
                return true;
            }

            if (expr.Contains(':'))
            {
                return true;
            }

            if (expr.Contains("==", StringComparison.Ordinal))
            {
                return true;
            }

            if (expr.Contains("!=", StringComparison.Ordinal))
            {
                return true;
            }

            if (expr.Contains("<=", StringComparison.Ordinal))
            {
                return true;
            }

            if (expr.Contains(">=", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Builds a single-line note message from a set of warnings.
        /// </summary>
        /// <param name="warnings">The warnings to include.</param>
        /// <returns>A message suitable for display, or an empty string when no warnings exist.</returns>
        private static string BuildMessage(IReadOnlyList<string> warnings)
        {
            if (warnings == null || warnings.Count == 0)
            {
                return string.Empty;
            }

            var distinct = new HashSet<string>(warnings, StringComparer.Ordinal);
            return string.Join(" ", distinct);
        }
    }
}
