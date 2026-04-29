//-----------------------------------------------------------------------
// <copyright file="ITemplateEngineResolver.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Template
{
    using MailForge.Grpc;

    /// <summary>
    /// Resolves a concrete template renderer for a requested engine.
    /// </summary>
    public interface ITemplateEngineResolver
    {
        /// <summary>
        /// Retrieves a renderer for the requested engine.
        /// </summary>
        /// <param name="engine">The requested template engine.</param>
        /// <returns>A renderer capable of producing a subject and body.</returns>
        IMailTemplateRenderer Resolve(TemplateEngine engine);
    }
}
