// <copyright file="NullAttachmentIndexNotifier.cs" company="IBC Digital">
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
    /// A no-op implementation of <see cref="IAttachmentIndexNotifier"/>.
    /// </summary>
    public sealed class NullAttachmentIndexNotifier : IAttachmentIndexNotifier
    {
        private NullAttachmentIndexNotifier()
        {
        }

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
    }
}
