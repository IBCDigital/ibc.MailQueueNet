//-----------------------------------------------------------------------
// <copyright file="AttachmentInfo.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Represents attachment metadata used by administrative queries.
    /// </summary>
    internal sealed class AttachmentInfo
    {
        /// <summary>
        /// Gets or sets a value indicating whether the attachment exists on disk.
        /// </summary>
        public bool Exists { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the attachment has completed uploading.
        /// </summary>
        public bool Ready { get; set; }

        /// <summary>
        /// Gets or sets the attachment token.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the stored byte length.
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        /// Gets or sets the SHA-256 digest encoded as base64.
        /// </summary>
        public string Sha256Base64 { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the time the upload completed.
        /// </summary>
        public DateTimeOffset UploadedUtc { get; set; }

        /// <summary>
        /// Gets or sets the reference count.
        /// </summary>
        public int RefCount { get; set; }

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
        /// Gets or sets the merge owner identifier.
        /// </summary>
        public string MergeOwnerId { get; set; } = string.Empty;
    }
}
