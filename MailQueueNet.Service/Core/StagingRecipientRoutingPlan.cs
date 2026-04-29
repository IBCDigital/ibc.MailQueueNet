//-----------------------------------------------------------------------
// <copyright file="StagingRecipientRoutingPlan.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System.Collections.Generic;
    using System.Net.Mail;

    /// <summary>
    /// Describes the recipients selected for Mailpit and real SMTP delivery in staging.
    /// </summary>
    public sealed class StagingRecipientRoutingPlan
    {
        /// <summary>
        /// Gets or sets the original client id associated with the message.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the allow-listed real SMTP recipients.
        /// </summary>
        public IReadOnlyList<MailAddress> RealRecipients { get; set; } = new List<MailAddress>();

        /// <summary>
        /// Gets or sets a value indicating whether a Mailpit copy should be sent.
        /// </summary>
        public bool SendMailpitCopy { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether a real SMTP copy should be sent.
        /// </summary>
        public bool SendRealSmtpCopy { get; set; }
    }
}
