//-----------------------------------------------------------------------
// <copyright file="AttachmentListItem.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Represents a single attachment entry returned by the attachment listing API.
    /// </summary>
    internal sealed class AttachmentListItem
    {
        /// <summary>
        /// Gets or sets the attachment token.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the attachment exists on disk.
        /// </summary>
        public bool Exists { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the attachment is ready.
        /// </summary>
        public bool Ready { get; set; }

        /// <summary>
        /// Gets or sets the reference count.
        /// </summary>
        public int RefCount { get; set; }

        /// <summary>
        /// Gets or sets the attachment length in bytes.
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        /// Gets or sets the SHA-256 digest in base64.
        /// </summary>
        public string Sha256Base64 { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the upload timestamp (UTC).
        /// </summary>
        public DateTimeOffset UploadedUtc { get; set; }

        /// <summary>
        /// Gets or sets the original file name.
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the content type.
        /// </summary>
        public string ContentType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the client identifier.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the merge-owner identifier.
        /// </summary>
        public string MergeOwnerId { get; set; } = string.Empty;
    }
}
