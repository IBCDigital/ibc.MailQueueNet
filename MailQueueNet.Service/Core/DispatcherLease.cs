// <copyright file="DispatcherLease.cs" company="IBC Digital">
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

    /// <summary>
    /// Represents the active dispatcher lease for a MailForge worker.
    /// </summary>
    public sealed class DispatcherLease
    {
        /// <summary>
        /// Gets or sets the leased worker address.
        /// </summary>
        public string WorkerAddress { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the current fence token.
        /// </summary>
        public long FenceToken { get; set; }

        /// <summary>
        /// Gets or sets the UTC expiry for the lease.
        /// </summary>
        public DateTimeOffset ExpiresUtc { get; set; }

        /// <summary>
        /// Gets a value indicating whether the lease is still valid.
        /// </summary>
        public bool IsValid
        {
            get
            {
                if (string.IsNullOrWhiteSpace(this.WorkerAddress))
                {
                    return false;
                }

                return this.ExpiresUtc > DateTimeOffset.UtcNow;
            }
        }
    }
}
