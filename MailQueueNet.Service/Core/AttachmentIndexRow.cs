//-----------------------------------------------------------------------
// <copyright file="AttachmentIndexRow.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Represents a row in the attachment index database.
    /// </summary>
    internal sealed class AttachmentIndexRow
    {
        /// <summary>
        /// Gets or sets the attachment token.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the UTC timestamp when the attachment was uploaded.
        /// </summary>
        public DateTimeOffset UploadedUtc { get; set; }

        /// <summary>
        /// Gets or sets the stored length in bytes.
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        /// Gets or sets the reference count.
        /// </summary>
        public int RefCount { get; set; }

        /// <summary>
        /// Gets or sets the client identifier.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the merge owner identifier.
        /// </summary>
        public string MergeOwnerId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the original file name.
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the content type.
        /// </summary>
        public string ContentType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the SHA-256 in base64.
        /// </summary>
        public string Sha256Base64 { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the attachment is ready.
        /// </summary>
        public bool Ready { get; set; }
    }
}
