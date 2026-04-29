// <copyright file="AttachmentStoreManifest.cs" company="IBC Digital">
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
    using System;

    /// <summary>
    /// Describes attachment metadata persisted alongside a stored attachment.
    /// </summary>
    internal sealed class AttachmentStoreManifest
    {
        /// <summary>
        /// Gets or sets the token identifying this attachment.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the upload client identifier, when available.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the original file name.
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the uploaded content type.
        /// </summary>
        public string ContentType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the expected content ID for inline attachments.
        /// </summary>
        public string ContentId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the value indicating whether the attachment should be treated as inline.
        /// </summary>
        public bool Inline { get; set; }

        /// <summary>
        /// Gets or sets the number of bytes stored.
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        /// Gets or sets the SHA-256 digest of the stored bytes encoded as base64.
        /// </summary>
        public string Sha256Base64 { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the UTC timestamp when the upload completed.
        /// </summary>
        public DateTimeOffset UploadedUtc { get; set; }

        /// <summary>
        /// Gets or sets the number of queued items currently referencing this attachment.
        /// </summary>
        public int RefCount { get; set; }

        /// <summary>
        /// Gets or sets the merge job identifier that owns the attachments.
        /// When set, attachments are treated as shared across the merge job.
        /// </summary>
        public string MergeOwnerId { get; set; } = string.Empty;
    }
}
