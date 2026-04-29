//-----------------------------------------------------------------------
// <copyright file="TemplateRenderException.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Template
{
    using System;

    /// <summary>
    /// Represents an error encountered while rendering a template.
    /// </summary>
    public sealed class TemplateRenderException : Exception
    {
        /// <summary>
        /// Initialises a new instance of the <see cref="TemplateRenderException"/> class.
        /// </summary>
        /// <param name="message">Error message.</param>
        public TemplateRenderException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initialises a new instance of the <see cref="TemplateRenderException"/> class.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="innerException">Inner exception.</param>
        public TemplateRenderException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
