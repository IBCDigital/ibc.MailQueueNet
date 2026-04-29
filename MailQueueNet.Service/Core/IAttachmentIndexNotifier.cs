//-----------------------------------------------------------------------
// <copyright file="IAttachmentIndexNotifier.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Describes an optional callback used by <see cref="DiskAttachmentStore"/> to notify an
    /// external indexing system of attachment state changes.
    /// </summary>
    public interface IAttachmentIndexNotifier
    {
        /// <summary>
        /// Notifies the index that the reference count for an attachment token has changed.
        /// </summary>
        /// <param name="token">The attachment token.</param>
        /// <param name="refCount">The updated reference count.</param>
        /// <param name="mergeOwnerId">The merge owner id when known; otherwise an empty string.</param>
        void OnRefCountChanged(string token, int refCount, string mergeOwnerId);

        /// <summary>
        /// Notifies the index that an attachment token has been removed from disk.
        /// </summary>
        /// <param name="token">The attachment token.</param>
        void OnDeleted(string token);

        /// <summary>
        /// Notifies the index that an attachment token has been upserted (created or updated).
        /// </summary>
        /// <param name="token">The attachment token.</param>
        void OnUpserted(string token);
    }
}
