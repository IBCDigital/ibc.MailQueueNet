// <copyright file="MailMessage.cs" company="IBC Digital">
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
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Protobuf MailMessage helpers for conversion to/from System.Net.Mail.MailMessage.
    /// </summary>
    public partial class MailMessage
    {
        /// <summary>
        /// Creates a protobuf MailMessage from a System.Net.Mail.MailMessage.
        /// </summary>
        public static MailMessage FromMessage(System.Net.Mail.MailMessage message)
        {
            var proto = new MailMessage
            {
                Priority = message.Priority.ToString(),
                BodyEncoding = message.BodyEncoding?.WebName,
                HeadersEncoding = message.HeadersEncoding?.WebName,
                SubjectEncoding = message.SubjectEncoding?.WebName,
                Subject = message.Subject,
                Body = message.Body,
                IsBodyHtml = message.IsBodyHtml,
                DeliveryNotificationOptions = message.DeliveryNotificationOptions.ToString(),
                From = message.From == null ? null : MailAddress.From(message.From),
                Sender = message.Sender == null ? null : MailAddress.From(message.Sender),
            };

            // Extract merge correlation fields from headers if present.
            try
            {
                var mergeId = message.Headers["X-MailMerge-Id"];
                if (!string.IsNullOrWhiteSpace(mergeId))
                {
                    proto.MergeId = mergeId;
                }

                var batchId = message.Headers["X-MailMerge-BatchId"];
                if (!string.IsNullOrWhiteSpace(batchId))
                {
                    proto.BatchId = batchId;
                }
            }
            catch
            {
            }

            foreach (var address in message.To)
            {
                proto.To.Add(MailAddress.From(address));
            }

            foreach (var address in message.CC)
            {
                proto.Cc.Add(MailAddress.From(address));
            }

            foreach (var address in message.Bcc)
            {
                proto.Bcc.Add(MailAddress.From(address));
            }

            foreach (var address in message.ReplyToList)
            {
                proto.ReplyTo.Add(MailAddress.From(address));
            }

            if (message.Headers.Count > 0)
            {
                foreach (var key in message.Headers.AllKeys)
                {
                    foreach (var value in message.Headers.GetValues(key))
                    {
                        proto.Headers.Add(new Header { Name = key, Value = value });
                    }
                }
            }

            foreach (var attachment in message.Attachments)
            {
                if (!(attachment.ContentStream is FileStream))
                {
                    continue;
                }

                var converted = Attachment.From(attachment);
                if (converted != null)
                {
                    proto.Attachments.Add(converted);
                }
            }

            // Attachment tokens (added by client-side upload helper).
            // These allow callers to send attachments when the queue service is remote.
            try
            {
                var tokens = message.Headers.GetValues("X-Attachment-Token") ?? Array.Empty<string>();
                var fileNames = message.Headers.GetValues("X-Attachment-Token-FileName") ?? Array.Empty<string>();
                var contentTypes = message.Headers.GetValues("X-Attachment-Token-ContentType") ?? Array.Empty<string>();
                var contentIds = message.Headers.GetValues("X-Attachment-Token-ContentId") ?? Array.Empty<string>();
                var inlines = message.Headers.GetValues("X-Attachment-Token-Inline") ?? Array.Empty<string>();

                for (var i = 0; i < tokens.Length; i++)
                {
                    var token = tokens[i];
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    var tokenRef = new AttachmentTokenRef
                    {
                        Token = token,
                        FileName = i < fileNames.Length ? (fileNames[i] ?? string.Empty) : string.Empty,
                        ContentType = i < contentTypes.Length ? (contentTypes[i] ?? string.Empty) : string.Empty,
                        ContentId = i < contentIds.Length ? (contentIds[i] ?? string.Empty) : string.Empty,
                        Inline = i < inlines.Length && string.Equals(inlines[i], "1", StringComparison.OrdinalIgnoreCase),
                    };

                    proto.AttachmentTokens.Add(tokenRef);
                }
            }
            catch
            {
            }

            return proto;
        }

        /// <summary>
        /// Converts protobuf MailMessage to System.Net.Mail.MailMessage.
        /// </summary>
        public System.Net.Mail.MailMessage ToSystemType()
        {
            var message = new System.Net.Mail.MailMessage
            {
                Priority = TryParseMailPriority(this.Priority),
                BodyEncoding = this.BodyEncoding == null ? null : Encoding.GetEncoding(this.BodyEncoding),
                HeadersEncoding = this.HeadersEncoding == null ? null : Encoding.GetEncoding(this.HeadersEncoding),
                SubjectEncoding = this.SubjectEncoding == null ? null : Encoding.GetEncoding(this.SubjectEncoding),
                Subject = this.Subject,
                Body = this.Body,
                IsBodyHtml = this.IsBodyHtml,
                DeliveryNotificationOptions = TryParseDeliveryNotificationOptions(this.DeliveryNotificationOptions),
                From = this.From?.ToSystemType(),
                Sender = this.Sender?.ToSystemType(),
            };

            foreach (var address in this.To)
            {
                message.To.Add(address.ToSystemType());
            }

            foreach (var address in this.Cc)
            {
                message.CC.Add(address.ToSystemType());
            }

            foreach (var address in this.Bcc)
            {
                message.Bcc.Add(address.ToSystemType());
            }

            foreach (var address in this.ReplyTo)
            {
                message.ReplyToList.Add(address.ToSystemType());
            }

            if (this.Headers.Count > 0)
            {
                foreach (var header in this.Headers)
                {
                    message.Headers.Add(header.Name, header.Value);
                }
            }

            // Reinstate merge correlation fields as headers for downstream systems.
            if (!string.IsNullOrWhiteSpace(this.MergeId))
            {
                message.Headers["X-MailMerge-Id"] = this.MergeId;
            }

            if (!string.IsNullOrWhiteSpace(this.BatchId))
            {
                message.Headers["X-MailMerge-BatchId"] = this.BatchId;
            }

            foreach (var attachment in this.Attachments)
            {
                message.Attachments.Add(attachment.ToSystemType());
            }

            return message;
        }

        private static System.Net.Mail.MailPriority TryParseMailPriority(string value)
        {
            if (Enum.TryParse<System.Net.Mail.MailPriority>(value, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            return System.Net.Mail.MailPriority.Normal;
        }

        private static System.Net.Mail.DeliveryNotificationOptions TryParseDeliveryNotificationOptions(string value)
        {
            if (Enum.TryParse<System.Net.Mail.DeliveryNotificationOptions>(value, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            return System.Net.Mail.DeliveryNotificationOptions.None;
        }
    }
}
