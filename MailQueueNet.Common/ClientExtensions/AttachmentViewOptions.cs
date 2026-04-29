//-----------------------------------------------------------------------
// <copyright file="AttachmentViewOptions.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Grpc
{
    using System;

    /// <summary>
    /// Provides configuration for common attachment list "views".
    /// </summary>
    public sealed class AttachmentViewOptions
    {
        /// <summary>
        /// Gets or sets the page size requested from the server.
        /// </summary>
        public int Take { get; set; } = 50;

        /// <summary>
        /// Gets or sets the server-side sort selector.
        /// </summary>
        public AttachmentSortBy SortBy { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether sorting is descending.
        /// </summary>
        public bool SortDesc { get; set; } = true;

        /// <summary>
        /// Gets or sets the "only large" threshold in bytes.
        /// </summary>
        public long LargeThresholdBytes { get; set; } = 10L * 1024L * 1024L;

        /// <summary>
        /// Gets or sets an optional client_id filter.
        /// </summary>
        public string? ClientId { get; set; }

        /// <summary>
        /// Gets or sets an optional merge_owner_id filter.
        /// </summary>
        public string? MergeOwnerId { get; set; }

        /// <summary>
        /// Gets or sets an optional upper bound on uploaded timestamp (UTC).
        /// </summary>
        public DateTimeOffset? OlderThanUtc { get; set; }

        /// <summary>
        /// Gets or sets an optional lower bound on uploaded timestamp (UTC).
        /// </summary>
        public DateTimeOffset? NewerThanUtc { get; set; }
    }
}
