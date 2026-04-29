//-----------------------------------------------------------------------
// <copyright file="AttachmentViewKind.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Grpc
{
    /// <summary>
    /// Identifies common server-side attachment list views.
    /// </summary>
    public enum AttachmentViewKind
    {
        /// <summary>
        /// Lists all attachments matching the supplied filters.
        /// </summary>
        All = 0,

        /// <summary>
        /// Lists orphan attachments (ref_count == 0).
        /// </summary>
        Orphans = 1,

        /// <summary>
        /// Lists large attachments (length &gt; threshold).
        /// </summary>
        Large = 2,

        /// <summary>
        /// Lists attachments that are both orphaned and large.
        /// </summary>
        LargeOrphans = 3,
    }
}
