// <copyright file="StagingMailRoutingOptions.cs" company="IBC Digital">
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
    /// <summary>
    /// Configuration options controlling staging-only mail routing.
    /// </summary>
    public sealed class StagingMailRoutingOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether staging mail routing is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether all mail should be forced to Mailpit only.
        /// </summary>
        public bool ForceMailpitOnly { get; set; }

        /// <summary>
        /// Gets or sets the subject prefix applied to real SMTP deliveries in staging.
        /// </summary>
        public string SubjectPrefix { get; set; } = "[STAGING] ";

        /// <summary>
        /// Gets or sets Mailpit SMTP settings used for the default safe delivery path.
        /// </summary>
        public SmtpDeliveryOptions Mailpit { get; set; } = new SmtpDeliveryOptions();

        /// <summary>
        /// Gets or sets the real SMTP settings used for allow-listed staging deliveries.
        /// </summary>
        public SmtpDeliveryOptions RealSmtp { get; set; } = new SmtpDeliveryOptions();
    }
}
