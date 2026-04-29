
//-----------------------------------------------------------------------
// <copyright file="HandlebarsTemplateRenderer.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Template
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using HandlebarsDotNet;

    /// <summary>
    /// Renders Handlebars templates using Handlebars.Net.
    /// </summary>
    public sealed class HandlebarsTemplateRenderer : IMailTemplateRenderer
    {
        private static readonly IHandlebars Handlebars = HandlebarsDotNet.Handlebars.Create();

        /// <inheritdoc/>
        public Task<TemplateRenderResult> RenderAsync(string subjectTemplate, string bodyTemplate, string jsonData, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var model = BuildModel(jsonData);

                var subjectCompiled = Handlebars.Compile(subjectTemplate ?? string.Empty);
                var bodyCompiled = Handlebars.Compile(bodyTemplate ?? string.Empty);

                return Task.FromResult(new TemplateRenderResult
                {
                    Subject = subjectCompiled(model) ?? string.Empty,
                    Body = bodyCompiled(model) ?? string.Empty,
                });
            }
            catch (Exception ex) when (ex is JsonException || ex is HandlebarsException)
            {
                throw new TemplateRenderException("Handlebars render failed", ex);
            }
        }

        /// <summary>
        /// Builds a Handlebars render model from an incoming JSON line.
        /// </summary>
        /// <param name="jsonData">The JSON payload (one merge row) to convert.</param>
        /// <returns>
        /// A dictionary-backed object graph (dictionaries, lists, and primitives) suitable for Handlebars
        /// property resolution.
        /// </returns>
        private static object BuildModel(string jsonData)
        {
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                var root = ConvertElement(doc.RootElement);
                if (root is Dictionary<string, object?> dict)
                {
                    return dict;
                }

                return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["value"] = root,
                };
            }
            catch (JsonException ex)
            {
                throw new TemplateRenderException("Invalid JSON payload", ex);
            }
        }

        /// <summary>
        /// Converts a <see cref="JsonElement"/> into a dictionary/list/primitives object graph.
        /// </summary>
        /// <param name="element">The JSON element to convert.</param>
        /// <returns>The converted object graph.</returns>
        private static object? ConvertElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    {
                        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in element.EnumerateObject())
                        {
                            dict[prop.Name] = ConvertElement(prop.Value);
                        }

                        return dict;
                    }

                case JsonValueKind.Array:
                    {
                        var list = new List<object?>();
                        foreach (var item in element.EnumerateArray())
                        {
                            list.Add(ConvertElement(item));
                        }

                        return list;
                    }

                case JsonValueKind.String:
                    return element.GetString() ?? string.Empty;

                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var i))
                    {
                        return i;
                    }

                    if (element.TryGetDouble(out var d))
                    {
                        return d;
                    }

                    return element.GetRawText();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;

                default:
                    return element.GetRawText();
            }
        }
    }
}
