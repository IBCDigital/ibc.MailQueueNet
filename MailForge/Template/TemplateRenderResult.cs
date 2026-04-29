//-----------------------------------------------------------------------
// <copyright file="TemplateRenderResult.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Template
{
    /// <summary>
    /// Represents the output of a template render operation.
    /// </summary>
    public sealed class TemplateRenderResult
    {
        /// <summary>
        /// Gets or sets the rendered subject.
        /// </summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the rendered body.
        /// </summary>
        public string Body { get; set; } = string.Empty;
    }
}
