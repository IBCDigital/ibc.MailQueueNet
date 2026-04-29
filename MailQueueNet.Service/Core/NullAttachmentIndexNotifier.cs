//-----------------------------------------------------------------------
// <copyright file="NullAttachmentIndexNotifier.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    /// <summary>
    /// A no-op implementation of <see cref="IAttachmentIndexNotifier"/>.
    /// </summary>
    public sealed class NullAttachmentIndexNotifier : IAttachmentIndexNotifier
    {
        /// <summary>
        /// Gets a shared singleton instance.
        /// </summary>
        public static NullAttachmentIndexNotifier Instance { get; } = new NullAttachmentIndexNotifier();

        /// <inheritdoc />
        public void OnRefCountChanged(string token, int refCount, string mergeOwnerId)
        {
        }

        /// <inheritdoc />
        public void OnDeleted(string token)
        {
        }

        /// <inheritdoc />
        public void OnUpserted(string token)
        {
        }

        private NullAttachmentIndexNotifier()
        {
        }
    }
}
