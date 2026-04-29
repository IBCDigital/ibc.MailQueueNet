// <copyright file="MailForgeDispatcherOptions.cs" company="IBC Digital">
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
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Represents configuration options for dispatching merge jobs from the queue
    /// service to MailForge workers.
    /// </summary>
    public sealed class MailForgeDispatcherOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether MailForge dispatching is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the worker addresses (URLs) that the dispatcher may lease.
        /// </summary>
        public IList<string> WorkerAddresses { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the lease duration.
        /// </summary>
        public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets or sets the lease renewal interval.
        /// </summary>
        public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the default template engine used when creating a merge job
        /// dispatch request.
        /// </summary>
        public string DefaultEngine { get; set; } = "liquid";

        /// <summary>
        /// Gets or sets a value indicating whether invalid TLS certificates should be
        /// accepted when connecting to MailForge workers.
        /// </summary>
        /// <remarks>
        /// This setting is intended for local development environments where containers
        /// use self-signed certificates that may not include the worker hostname in the
        /// certificate subject or SAN list.
        /// </remarks>
        public bool AllowInvalidWorkerCertificates { get; set; }
    }
}
