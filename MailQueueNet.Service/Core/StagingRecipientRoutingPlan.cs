// <copyright file="StagingRecipientRoutingPlan.cs" company="IBC Digital">
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
