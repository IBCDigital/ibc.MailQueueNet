// <copyright file="AttachmentEx.cs" company="IBC Digital">
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
// <license>
// MIT Licence – see the repository root LICENCE file for full text.
// </license>

namespace MailQueueNet
{
    using System.IO;
    using System.Net.Mail;
    using System.Net.Mime;

    /// <summary>
    /// A thin wrapper around <see cref="Attachment"/> that exposes an extra flag
    /// (<see cref="ShouldDeleteFile"/>) indicating whether the source file
    /// should be deleted after the message is sent.
    /// </summary>
    public class AttachmentEx : Attachment
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentEx"/> class
        /// from a file on disk.
        /// </summary>
        /// <param name="fileName">The full path of the file to attach.</param>
        public AttachmentEx(string fileName)
            : base(fileName)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentEx"/> class
        /// from a file on disk and an explicit media type.
        /// </summary>
        /// <param name="fileName">The full path of the file to attach.</param>
        /// <param name="mediaType">
        /// The MIME media type (e.g. <c>image/png</c>) of the attachment.
        /// </param>
        public AttachmentEx(string fileName, string mediaType)
            : base(fileName, mediaType)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentEx"/> class
        /// from a file on disk and an explicit <see cref="ContentType"/>.
        /// </summary>
        /// <param name="fileName">The full path of the file to attach.</param>
        /// <param name="contentType">The MIME content type.</param>
        public AttachmentEx(string fileName, ContentType contentType)
            : base(fileName, contentType)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentEx"/> class
        /// from a <see cref="Stream"/>.
        /// </summary>
        /// <param name="contentStream">The stream that contains the attachment.</param>
        /// <param name="name">The suggested file name for the attachment.</param>
        public AttachmentEx(Stream contentStream, string name)
            : base(contentStream, name)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentEx"/> class
        /// from a <see cref="Stream"/> and explicit <see cref="ContentType"/>.
        /// </summary>
        /// <param name="contentStream">The stream that contains the attachment.</param>
        /// <param name="contentType">The MIME content type.</param>
        public AttachmentEx(Stream contentStream, ContentType contentType)
            : base(contentStream, contentType)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentEx"/> class
        /// from a <see cref="Stream"/>, a file name, and a media type string.
        /// </summary>
        /// <param name="contentStream">The stream that contains the attachment.</param>
        /// <param name="name">The suggested file name for the attachment.</param>
        /// <param name="mediaType">The MIME media type.</param>
        public AttachmentEx(Stream contentStream, string name, string mediaType)
            : base(contentStream, name, mediaType)
        {
        }

        /// <summary>
        /// Gets or sets a value indicating whether the file that backs this
        /// attachment should be deleted from disk after the e-mail has been sent.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if the file should be deleted; otherwise
        /// <see langword="false"/>. The default is <c>false</c>.
        /// </value>
        public bool ShouldDeleteFile { get; set; } = false;
    }
}
