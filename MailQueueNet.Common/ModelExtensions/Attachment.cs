// <copyright file="Attachment.cs" company="IBC Digital">
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

namespace MailQueueNet.Grpc
{
    using System;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Partial protobuf Attachment extensions.
    /// </summary>
    public partial class Attachment
    {
        /// <summary>
        /// Creates a protobuf Attachment from a <see cref="System.Net.Mail.Attachment"/>.
        /// Returns null if the attachment is not file-backed.
        /// </summary>
        /// <param name="attachment">The source attachment.</param>
        /// <returns>Attachment instance or null.</returns>
        public static Attachment? From(System.Net.Mail.Attachment attachment)
        {
            var stream = attachment.ContentStream as FileStream;
            if (stream == null)
            {
                return null;
            }

            var proto = new Attachment
            {
                FileName = stream.Name,
                Name = attachment.Name,
                NameEncoding = attachment.NameEncoding?.WebName,
                ContentId = attachment.ContentId,
                ContentType = attachment.ContentType.MediaType,
                TransferEncoding = attachment.TransferEncoding.ToString(),
            };

            if (attachment.ContentDisposition != null)
            {
                proto.ContentDisposition = new ContentDisposition
                {
                    DispositionType = attachment.ContentDisposition.DispositionType,
                    Inline = attachment.ContentDisposition.Inline,
                    FileName = attachment.ContentDisposition.FileName,
                    Size = attachment.ContentDisposition.Size,
                };

                if (attachment.ContentDisposition.ReadDate != DateTime.MinValue)
                {
                    proto.ContentDisposition.CreationDate = attachment.ContentDisposition.CreationDate.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
                }

                if (attachment.ContentDisposition.ModificationDate != DateTime.MinValue)
                {
                    proto.ContentDisposition.ModificationDate = attachment.ContentDisposition.ModificationDate.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
                }

                if (attachment.ContentDisposition.ReadDate != DateTime.MinValue)
                {
                    proto.ContentDisposition.ReadDate = attachment.ContentDisposition.ReadDate.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
                }

                foreach (string key in attachment.ContentDisposition.Parameters.Keys)
                {
                    attachment.ContentDisposition.Parameters[key] = attachment.ContentDisposition.Parameters[key];
                }
            }

            return proto;
        }

        /// <summary>
        /// Converts the protobuf Attachment back to a <see cref="System.Net.Mail.Attachment"/>.
        /// </summary>
        /// <returns>System.Net.Mail.Attachment.</returns>
        public System.Net.Mail.Attachment ToSystemType()
        {
            var attachment = this.ShouldDelete
                ? new AttachmentEx(this.FileName) { ShouldDeleteFile = true }
                : new System.Net.Mail.Attachment(this.FileName);

            if (this.Name != null)
            {
                attachment.Name = this.Name;
            }

            if (this.NameEncoding != null)
            {
                attachment.NameEncoding = Encoding.GetEncoding(this.NameEncoding);
            }

            if (this.ContentId != null)
            {
                attachment.ContentId = this.ContentId;
            }

            if (this.ContentType != null)
            {
                attachment.ContentType = new System.Net.Mime.ContentType(this.ContentType);
            }

            if (this.TransferEncoding != null)
            {
                attachment.TransferEncoding = (System.Net.Mime.TransferEncoding)Enum.Parse(typeof(System.Net.Mime.TransferEncoding), this.TransferEncoding);
            }

            if (this.ContentDisposition != null)
            {
                if (this.ContentDisposition.DispositionType != null)
                {
                    attachment.ContentDisposition.DispositionType = this.ContentDisposition.DispositionType;
                }

                attachment.ContentDisposition.Inline = this.ContentDisposition.Inline;

                if (this.ContentDisposition.FileName != null)
                {
                    attachment.ContentDisposition.FileName = this.ContentDisposition.FileName;
                }

                if (this.ContentDisposition.CreationDate != null)
                {
                    attachment.ContentDisposition.CreationDate = DateTime.ParseExact(this.ContentDisposition.CreationDate, "yyyy-MM-ddTHH:mm:ss.fffzzz", null);
                }

                if (this.ContentDisposition.ModificationDate != null)
                {
                    attachment.ContentDisposition.ModificationDate = DateTime.ParseExact(this.ContentDisposition.ModificationDate, "yyyy-MM-ddTHH:mm:ss.fffzzz", null);
                }

                if (this.ContentDisposition.ReadDate != null)
                {
                    attachment.ContentDisposition.ReadDate = DateTime.ParseExact(this.ContentDisposition.ReadDate, "yyyy-MM-ddTHH:mm:ss.fffzzz", null);
                }

                attachment.ContentDisposition.Size = Convert.ToInt64(this.ContentDisposition.Size);

                if (this.ContentDisposition.Params != null)
                {
                    foreach (var param in this.ContentDisposition.Params)
                    {
                        try
                        {
                            attachment.ContentDisposition.Parameters[param.Key] = param.Value;
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return attachment;
        }
    }
}
