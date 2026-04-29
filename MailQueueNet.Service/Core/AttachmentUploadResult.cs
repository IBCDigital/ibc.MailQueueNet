// <copyright file="AttachmentUploadResult.cs" company="IBC Digital">
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
