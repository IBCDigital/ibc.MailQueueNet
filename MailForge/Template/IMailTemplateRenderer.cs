//-----------------------------------------------------------------------
// <copyright file="IMailTemplateRenderer.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Template
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Defines a renderer capable of producing a subject and body for an e-mail message
    /// from a template and a JSON data payload.
    /// </summary>
    public interface IMailTemplateRenderer
    {
        /// <summary>
        /// Renders the subject and body templates for a single message.
        /// </summary>
        /// <param name="subjectTemplate">Subject template text.</param>
        /// <param name="bodyTemplate">Body template text.</param>
        /// <param name="jsonData">JSON data payload for template variables.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A rendered result containing the resolved subject and body.</returns>
        Task<TemplateRenderResult> RenderAsync(string subjectTemplate, string bodyTemplate, string jsonData, CancellationToken cancellationToken);
    }
}
