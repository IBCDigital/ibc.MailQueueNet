//-----------------------------------------------------------------------
// <copyright file="AttachmentStoreOptions.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Provides configuration options for the on-disk attachment store.
    /// </summary>
    internal sealed class AttachmentStoreOptions
    {
        /// <summary>
        /// Gets the default maximum attachment size in bytes.
        /// </summary>
        public const long DefaultMaxUploadBytes = 10L * 1024L * 1024L;

        /// <summary>
        /// Gets the default maximum attachment age before an unreferenced item can be deleted.
        /// </summary>
        public static readonly TimeSpan DefaultUnreferencedTtl = TimeSpan.FromHours(24);

        /// <summary>
        /// Gets the default maximum age for incomplete uploads before they are reaped.
        /// </summary>
        public static readonly TimeSpan DefaultIncompleteUploadTtl = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Gets or sets the absolute folder path where attachments are stored.
        /// </summary>
        public string BaseFolder { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the maximum upload size in bytes.
        /// </summary>
        public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;

        /// <summary>
        /// Gets or sets the TTL applied to unreferenced, completed attachments.
        /// </summary>
        public TimeSpan UnreferencedTtl { get; set; } = DefaultUnreferencedTtl;

        /// <summary>
        /// Gets or sets the TTL applied to incomplete uploads.
        /// </summary>
        public TimeSpan IncompleteUploadTtl { get; set; } = DefaultIncompleteUploadTtl;
    }
}
