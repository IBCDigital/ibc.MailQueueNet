//-----------------------------------------------------------------------
// <copyright file="TemplateEngineResolver.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Template
{
    using System;
    using MailForge.Grpc;

    /// <summary>
    /// Resolves the template renderer for a requested engine.
    /// </summary>
    public sealed class TemplateEngineResolver : ITemplateEngineResolver
    {
        private readonly LiquidTemplateRenderer liquid;
        private readonly HandlebarsTemplateRenderer handlebars;

        /// <summary>
        /// Initialises a new instance of the <see cref="TemplateEngineResolver"/> class.
        /// </summary>
        /// <param name="liquid">Liquid renderer.</param>
        /// <param name="handlebars">Handlebars renderer.</param>
        public TemplateEngineResolver(LiquidTemplateRenderer liquid, HandlebarsTemplateRenderer handlebars)
        {
            this.liquid = liquid;
            this.handlebars = handlebars;
        }

        /// <inheritdoc/>
        public IMailTemplateRenderer Resolve(TemplateEngine engine)
        {
            return engine switch
            {
                TemplateEngine.Liquid => this.liquid,
                TemplateEngine.Handlebars => this.handlebars,
                _ => throw new ArgumentOutOfRangeException(nameof(engine), "Unsupported template engine"),
            };
        }
    }
}
