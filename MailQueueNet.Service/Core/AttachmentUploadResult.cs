//-----------------------------------------------------------------------
// <copyright file="AttachmentUploadResult.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Represents the outcome of an attachment upload operation.
    /// </summary>
    internal sealed class AttachmentUploadResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether the upload succeeded.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Gets or sets the server-allocated token that identifies the uploaded attachment.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of bytes received by the server.
        /// </summary>
        public long ReceivedBytes { get; set; }

        /// <summary>
        /// Gets or sets an optional diagnostic message.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
