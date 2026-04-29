//-----------------------------------------------------------------------
// <copyright file="IStagingMailRouter.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Routes staging mail between Mailpit and the real SMTP service.
    /// </summary>
    public interface IStagingMailRouter
    {
        /// <summary>
        /// Gets a value indicating whether staging routing should be applied to the current message.
        /// </summary>
        /// <param name="clientId">The client id associated with the message.</param>
        /// <param name="settings">The effective outbound mail settings.</param>
        /// <returns>True when staging routing should be applied.</returns>
        bool ShouldRoute(string clientId, MailSettings settings);

        /// <summary>
        /// Sends a message using staging routing rules.
        /// </summary>
        /// <param name="message">The mail message to route.</param>
        /// <param name="clientId">The client id associated with the message.</param>
        /// <param name="effectiveSettings">The effective original outbound settings.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when at least one routed delivery succeeded; otherwise false.</returns>
        Task<bool> SendAsync(System.Net.Mail.MailMessage message, string clientId, MailSettings effectiveSettings, CancellationToken cancellationToken);
    }
}
