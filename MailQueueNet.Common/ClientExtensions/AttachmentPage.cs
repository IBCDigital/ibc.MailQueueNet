//-----------------------------------------------------------------------
// <copyright file="AttachmentPage.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Grpc
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Represents a single page of attachment query results.
    /// </summary>
    public sealed class AttachmentPage
    {
        /// <summary>
        /// Gets or sets the total number of rows matched by the query.
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Gets or sets the items returned for this page.
        /// </summary>
        public IReadOnlyList<AttachmentListItem> Items { get; set; } = Array.Empty<AttachmentListItem>();

        /// <summary>
        /// Gets or sets an opaque token used to request the next page.
        /// </summary>
        public string NextPageToken { get; set; } = string.Empty;

        /// <summary>
        /// Gets a value indicating whether the server reported there are additional pages.
        /// </summary>
        public bool HasMore => !string.IsNullOrWhiteSpace(this.NextPageToken);
    }
}
