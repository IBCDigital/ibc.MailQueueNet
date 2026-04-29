//-----------------------------------------------------------------------
// <copyright file="LiquidTemplateRenderer.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Template
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Fluid;
    using Fluid.Values;

    /// <summary>
    /// Renders Liquid templates using the Fluid engine.
    /// </summary>
    public sealed class LiquidTemplateRenderer : IMailTemplateRenderer
    {
        private static readonly FluidParser Parser = new FluidParser();
        private static readonly TemplateOptions Options = new TemplateOptions();

        /// <inheritdoc/>
        public Task<TemplateRenderResult> RenderAsync(string subjectTemplate, string bodyTemplate, string jsonData, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Parser.TryParse(subjectTemplate ?? string.Empty, out var subject, out var subjectError))
            {
                throw new TemplateRenderException("Liquid subject template parse failed: " + (subjectError ?? "Unknown"));
            }

            if (!Parser.TryParse(bodyTemplate ?? string.Empty, out var body, out var bodyError))
            {
                throw new TemplateRenderException("Liquid body template parse failed: " + (bodyError ?? "Unknown"));
            }

            var model = BuildModel(jsonData);

            var context = new TemplateContext(model, Options);
            var subjectRendered = subject.Render(context);
            var bodyRendered = body.Render(context);

            return Task.FromResult(new TemplateRenderResult
            {
                Subject = subjectRendered ?? string.Empty,
                Body = bodyRendered ?? string.Empty,
            });
        }

        private static FluidValue BuildModel(string jsonData)
        {
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                return new ObjectValue(new object());
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                return ConvertElement(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new TemplateRenderException("Invalid JSON payload", ex);
            }
        }

        private static FluidValue ConvertElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    {
                        var dict = new System.Collections.Generic.Dictionary<string, object?>();
                        foreach (var prop in element.EnumerateObject())
                        {
                            dict[prop.Name] = ConvertElement(prop.Value).ToObjectValue();
                        }

                        return FluidValue.Create(dict, Options);
                    }

                case JsonValueKind.Array:
                    {
                        var list = new System.Collections.Generic.List<object?>();
                        foreach (var item in element.EnumerateArray())
                        {
                            list.Add(ConvertElement(item).ToObjectValue());
                        }

                        return FluidValue.Create(list, Options);
                    }

                case JsonValueKind.String:
                    return FluidValue.Create(element.GetString() ?? string.Empty, Options);

                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var i))
                    {
                        return FluidValue.Create(i, Options);
                    }

                    if (element.TryGetDouble(out var d))
                    {
                        return FluidValue.Create(d, Options);
                    }

                    return FluidValue.Create(element.GetRawText(), Options);

                case JsonValueKind.True:
                    return FluidValue.Create(true, Options);

                case JsonValueKind.False:
                    return FluidValue.Create(false, Options);

                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return NilValue.Instance;

                default:
                    return FluidValue.Create(element.GetRawText(), Options);
            }
        }
    }
}
