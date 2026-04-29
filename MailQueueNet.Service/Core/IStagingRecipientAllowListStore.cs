// <copyright file="IStagingRecipientAllowListStore.cs" company="IBC Digital">
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
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Stores staging-only allow-listed recipient addresses per client id.
    /// </summary>
    public interface IStagingRecipientAllowListStore
    {
        /// <summary>
        /// Lists allow-listed recipient email addresses for a client id.
        /// </summary>
        /// <param name="clientId">The authenticated client id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The allow-listed email addresses.</returns>
        Task<IReadOnlyList<string>> ListAsync(string clientId, CancellationToken cancellationToken);

        /// <summary>
        /// Adds an allow-listed recipient email address for a client id.
        /// </summary>
        /// <param name="clientId">The authenticated client id.</param>
        /// <param name="emailAddress">The email address to allow.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when the record was stored.</returns>
        Task<bool> AddAsync(string clientId, string emailAddress, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes an allow-listed recipient email address for a client id.
        /// </summary>
        /// <param name="clientId">The authenticated client id.</param>
        /// <param name="emailAddress">The email address to remove.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when a record was removed.</returns>
        Task<bool> DeleteAsync(string clientId, string emailAddress, CancellationToken cancellationToken);
    }
}
