// <copyright file="MailGrpcServiceClient.cs" company="IBC Digital">
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

namespace MailQueueNet.Grpc
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Mail;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Grpc.Core;

    /// <summary>
    /// Partial extension methods for the gRPC-generated
    /// <see cref="MailGrpcService.MailGrpcServiceClient"/>.
    /// These helpers accept the familiar <see cref="System.Net.Mail.MailMessage"/>
    /// class and convert it to the protobuf <see cref="MailMessage"/> message
    /// so callers do not need to reference the generated types directly.
    /// </summary>
    public partial class MailGrpcService
    {
        /// <summary>
        /// Extends the generated <see cref="MailGrpcServiceClient"/> with overloads
        /// that accept <see cref="System.Net.Mail.MailMessage"/> and optional
        /// <see cref="MailSettings"/>.
        /// </summary>
        public partial class MailGrpcServiceClient
        {
            private static string? sharedSecret;
            private static string? clientId;

            private const string AttachmentTokenHeader = "X-Attachment-Token";
            private const string AttachmentTokenFileNameHeader = "X-Attachment-Token-FileName";
            private const string AttachmentTokenContentTypeHeader = "X-Attachment-Token-ContentType";
            private const string AttachmentTokenContentIdHeader = "X-Attachment-Token-ContentId";
            private const string AttachmentTokenInlineHeader = "X-Attachment-Token-Inline";

            /// <summary>
            /// Configures client authentication headers for subsequent calls.
            /// </summary>
            /// <param name="clientIdValue">Client identifier.</param>
            /// <param name="sharedSecretValue">Shared secret.</param>
            public static void ConfigureClientAuth(string clientIdValue, string sharedSecretValue)
            {
                clientId = clientIdValue;
                sharedSecret = sharedSecretValue;
            }

            /// <summary>
            /// Uploads an attachment to the queue service using client streaming and returns an attachment token.
            /// </summary>
            /// <param name="stream">Readable stream containing the attachment bytes.</param>
            /// <param name="fileName">Original file name.</param>
            /// <param name="contentType">Content type (MIME type).</param>
            /// <param name="headers">Optional metadata headers.</param>
            /// <param name="deadline">Optional deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>Upload reply containing the attachment token.</returns>
            public virtual Task<UploadAttachmentReply> UploadAttachmentAsync(Stream stream, string fileName, string contentType, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (stream == null)
                {
                    throw new ArgumentNullException(nameof(stream));
                }

                return this.UploadAttachmentTokenAsync(stream, fileName, contentType, headers, deadline, cancellationToken);
            }

            /// <summary>
            /// Uploads a file-backed <see cref="System.Net.Mail.Attachment"/> and returns an attachment token.
            /// </summary>
            /// <param name="attachment">The attachment to upload.</param>
            /// <param name="headers">Optional metadata headers.</param>
            /// <param name="deadline">Optional deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>Upload reply containing the attachment token.</returns>
            public virtual async Task<UploadAttachmentReply> UploadAttachmentAsync(System.Net.Mail.Attachment attachment, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (attachment == null)
                {
                    throw new ArgumentNullException(nameof(attachment));
                }

                if (!(attachment.ContentStream is FileStream fileStream))
                {
                    throw new NotSupportedException("Only file-backed attachments can be uploaded.");
                }

                var path = fileStream.Name;
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new IOException("Attachment file path is missing.");
                }

                var uploadFileName = attachment.Name;
                if (string.IsNullOrWhiteSpace(uploadFileName))
                {
                    uploadFileName = Path.GetFileName(path);
                }

                var uploadContentType = attachment.ContentType?.MediaType ?? string.Empty;

                await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                {
                    return await this.UploadAttachmentTokenAsync(fs, uploadFileName, uploadContentType, headers, deadline, cancellationToken).ConfigureAwait(false);
                }
            }

            /// <summary>
            /// Uploads a file attachment from disk and returns an attachment token.
            /// </summary>
            /// <param name="path">Path to the file to upload.</param>
            /// <param name="fileName">Optional original file name. Defaults to the file name from <paramref name="path"/>.</param>
            /// <param name="contentType">Optional MIME content type.</param>
            /// <param name="headers">Optional metadata headers.</param>
            /// <param name="deadline">Optional deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>Upload reply containing the attachment token.</returns>
            public virtual async Task<UploadAttachmentReply> UploadFileAttachmentAsync(string path, string? fileName = null, string? contentType = null, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("Path is null or empty.", nameof(path));
                }

                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("File not found.", path);
                }

                var uploadName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path) : fileName;
                var uploadContentType = contentType ?? string.Empty;

                await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                {
                    return await this.UploadAttachmentTokenAsync(fs, uploadName, uploadContentType, headers, deadline, cancellationToken).ConfigureAwait(false);
                }
            }

            /// <summary>
            /// Uploads file-backed attachments from a <see cref="System.Net.Mail.MailMessage"/>, writes the resulting
            /// token references into the message headers, and optionally removes the original attachments.
            /// </summary>
            /// <param name="mailMessage">Mail message containing attachments to upload.</param>
            /// <param name="removeOriginalAttachments">True to remove and dispose the uploaded attachments from the message.</param>
            /// <param name="headers">Optional metadata headers.</param>
            /// <param name="deadline">Optional deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A list of attachment token references that were uploaded and added to the message.</returns>
            public virtual async Task<IReadOnlyList<AttachmentTokenRef>> UploadAttachmentsAndApplyTokensAsync(System.Net.Mail.MailMessage mailMessage, bool removeOriginalAttachments = true, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (mailMessage == null)
                {
                    throw new ArgumentNullException(nameof(mailMessage));
                }

                if (mailMessage.Attachments == null || mailMessage.Attachments.Count == 0)
                {
                    return Array.Empty<AttachmentTokenRef>();
                }

                var existingTokens = this.TryGetExistingAttachmentTokenHeaders(mailMessage);
                if (existingTokens.Count > 0)
                {
                    return existingTokens;
                }

                var tokens = new List<AttachmentTokenRef>();
                var toRemove = new List<System.Net.Mail.Attachment>();

                foreach (var attachment in mailMessage.Attachments)
                {
                    if (attachment == null)
                    {
                        continue;
                    }

                    if (!(attachment.ContentStream is FileStream fileStream))
                    {
                        continue;
                    }

                    var path = fileStream.Name;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    var uploadFileName = attachment.Name;
                    if (string.IsNullOrWhiteSpace(uploadFileName))
                    {
                        uploadFileName = Path.GetFileName(path);
                    }

                    await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                    {
                        var upload = await this.UploadAttachmentTokenAsync(
                            fs,
                            uploadFileName,
                            attachment.ContentType?.MediaType ?? string.Empty,
                            headers,
                            deadline,
                            cancellationToken).ConfigureAwait(false);

                        if (!upload.Success || string.IsNullOrWhiteSpace(upload.Token))
                        {
                            throw new IOException("Attachment upload failed: " + (upload.Message ?? string.Empty));
                        }

                        tokens.Add(new AttachmentTokenRef
                        {
                            Token = upload.Token,
                            FileName = attachment.Name ?? string.Empty,
                            ContentType = attachment.ContentType?.MediaType ?? string.Empty,
                            ContentId = attachment.ContentId ?? string.Empty,
                            Inline = attachment.ContentDisposition != null && attachment.ContentDisposition.Inline,
                        });

                        if (removeOriginalAttachments)
                        {
                            toRemove.Add(attachment);
                        }
                    }
                }

                if (tokens.Count == 0)
                {
                    return Array.Empty<AttachmentTokenRef>();
                }

                if (removeOriginalAttachments)
                {
                    foreach (var attachment in toRemove)
                    {
                        try
                        {
                            mailMessage.Attachments.Remove(attachment);
                            attachment.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }

                this.ApplyAttachmentTokensToHeaders(mailMessage, tokens);

                return tokens;
            }

            /// <summary>
            /// Ensures file-backed attachments are uploaded and token headers are present on the message.
            /// If tokens are already present, no upload is performed.
            /// </summary>
            /// <param name="mailMessage">Mail message to process.</param>
            /// <param name="removeOriginalAttachments">True to remove and dispose the uploaded attachments from the message.</param>
            /// <param name="headers">Optional metadata headers.</param>
            /// <param name="deadline">Optional deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A list of attachment token references present after the operation.</returns>
            public virtual Task<IReadOnlyList<AttachmentTokenRef>> EnsureAttachmentTokensAsync(System.Net.Mail.MailMessage mailMessage, bool removeOriginalAttachments = true, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                return this.UploadAttachmentsAndApplyTokensAsync(mailMessage, removeOriginalAttachments, headers, deadline, cancellationToken);
            }

            /// <summary>
            /// Uploads attachments for a mail merge item, ensuring the merge identifier is persisted on the message.
            /// </summary>
            /// <param name="mailMessage">Mail message containing attachments to upload.</param>
            /// <param name="mergeId">Merge identifier that should own the attachment references.</param>
            /// <param name="removeOriginalAttachments">True to remove and dispose the uploaded attachments from the message.</param>
            /// <param name="headers">Optional metadata headers.</param>
            /// <param name="deadline">Optional deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A list of attachment token references that were uploaded and added to the message.</returns>
            public virtual Task<IReadOnlyList<AttachmentTokenRef>> UploadMergeAttachmentsAndApplyTokensAsync(System.Net.Mail.MailMessage mailMessage, string mergeId, bool removeOriginalAttachments = true, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (mailMessage == null)
                {
                    throw new ArgumentNullException(nameof(mailMessage));
                }

                if (string.IsNullOrWhiteSpace(mergeId))
                {
                    throw new ArgumentException("Merge id is null or empty.", nameof(mergeId));
                }

                try
                {
                    mailMessage.Headers["X-MailMerge-Id"] = mergeId;
                }
                catch
                {
                }

                return this.UploadAttachmentsAndApplyTokensAsync(mailMessage, removeOriginalAttachments, headers, deadline, cancellationToken);
            }

            private bool ShouldUploadAttachments()
            {
                var target = MailClientConfiguration.Current?.MailQueueNetServiceChannelAddress;
                if (string.IsNullOrWhiteSpace(target))
                {
                    return false;
                }

                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }

            private async Task<UploadAttachmentReply> UploadAttachmentTokenAsync(Stream stream, string fileName, string contentType, Metadata? headers, DateTime? deadline, CancellationToken cancellationToken)
            {
                // Reuse the public helper if present as a virtual method; otherwise use the generated streaming call.
                var call = this.UploadAttachment(EnsureAuthHeaders(headers), deadline, cancellationToken);

                var length = stream.CanSeek ? stream.Length : 0;
                var sha256Base64 = string.Empty;

                if (stream.CanSeek)
                {
                    var origPos = stream.Position;
                    stream.Position = 0;

                    using (var sha = SHA256.Create())
                    {
                        sha256Base64 = Convert.ToBase64String(sha.ComputeHash(stream));
                    }

                    stream.Position = origPos;
                }

                await call.RequestStream.WriteAsync(new UploadAttachmentRequest
                {
                    Start = new UploadAttachmentStart
                    {
                        Token = string.Empty,
                        FileName = fileName ?? string.Empty,
                        ContentType = contentType ?? string.Empty,
                        Length = length,
                        Sha256Base64 = sha256Base64,
                    },
                }).ConfigureAwait(false);

                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await call.RequestStream.WriteAsync(new UploadAttachmentRequest
                    {
                        Chunk = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read),
                    }).ConfigureAwait(false);
                }

                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                return await call.ResponseAsync.ConfigureAwait(false);
            }

            private async Task UploadAndMutateAttachmentsAsync(System.Net.Mail.MailMessage mailMessage, Metadata? headers, DateTime? deadline, CancellationToken cancellationToken)
            {
                if (mailMessage == null)
                {
                    throw new ArgumentNullException(nameof(mailMessage));
                }

                if (mailMessage.Attachments == null || mailMessage.Attachments.Count == 0)
                {
                    return;
                }

                var existingTokens = this.TryGetExistingAttachmentTokenHeaders(mailMessage);
                if (existingTokens.Count > 0)
                {
                    return;
                }

                var tokens = await this.UploadAttachmentsAndApplyTokensAsync(mailMessage, true, headers, deadline, cancellationToken).ConfigureAwait(false);
                _ = tokens;
            }

            private IReadOnlyList<AttachmentTokenRef> TryGetExistingAttachmentTokenHeaders(System.Net.Mail.MailMessage mailMessage)
            {
                var tokens = new List<AttachmentTokenRef>();

                try
                {
                    var existing = mailMessage.Headers.GetValues(AttachmentTokenHeader);
                    if (existing == null || existing.Length == 0)
                    {
                        return tokens;
                    }

                    var fileNames = mailMessage.Headers.GetValues(AttachmentTokenFileNameHeader) ?? Array.Empty<string>();
                    var contentTypes = mailMessage.Headers.GetValues(AttachmentTokenContentTypeHeader) ?? Array.Empty<string>();
                    var contentIds = mailMessage.Headers.GetValues(AttachmentTokenContentIdHeader) ?? Array.Empty<string>();
                    var inlines = mailMessage.Headers.GetValues(AttachmentTokenInlineHeader) ?? Array.Empty<string>();

                    for (var i = 0; i < existing.Length; i++)
                    {
                        var token = existing[i];
                        if (string.IsNullOrWhiteSpace(token))
                        {
                            continue;
                        }

                        tokens.Add(new AttachmentTokenRef
                        {
                            Token = token,
                            FileName = i < fileNames.Length ? (fileNames[i] ?? string.Empty) : string.Empty,
                            ContentType = i < contentTypes.Length ? (contentTypes[i] ?? string.Empty) : string.Empty,
                            ContentId = i < contentIds.Length ? (contentIds[i] ?? string.Empty) : string.Empty,
                            Inline = i < inlines.Length && string.Equals(inlines[i], "1", StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }
                catch
                {
                }

                return tokens;
            }

            private void ApplyAttachmentTokensToHeaders(System.Net.Mail.MailMessage mailMessage, IReadOnlyList<AttachmentTokenRef> tokens)
            {
                if (mailMessage == null)
                {
                    throw new ArgumentNullException(nameof(mailMessage));
                }

                if (tokens == null)
                {
                    throw new ArgumentNullException(nameof(tokens));
                }

                foreach (var token in tokens)
                {
                    mailMessage.Headers.Add(AttachmentTokenHeader, token.Token);
                    mailMessage.Headers.Add(AttachmentTokenFileNameHeader, token.FileName ?? string.Empty);
                    mailMessage.Headers.Add(AttachmentTokenContentTypeHeader, token.ContentType ?? string.Empty);
                    mailMessage.Headers.Add(AttachmentTokenContentIdHeader, token.ContentId ?? string.Empty);
                    mailMessage.Headers.Add(AttachmentTokenInlineHeader, token.Inline ? "1" : "0");
                }
            }

            /// <summary>
            /// Queues an e-mail message for sending, using the default call options.
            /// When the service address is not localhost, file-backed attachments will be uploaded
            /// and converted to attachment tokens.
            /// </summary>
            /// <param name="message">The SMTP-style mail message to queue.</param>
            /// <param name="headers">
            /// Optional metadata headers to include with the gRPC request.
            /// </param>
            /// <param name="deadline">
            /// Optional absolute deadline after which the call is cancelled.
            /// </param>
            /// <param name="cancellationToken">
            /// A token that can be used to cancel the operation.
            /// </param>
            /// <returns>
            /// A <see cref="MailMessageReply"/> indicating whether the message was
            /// successfully queued by the server.
            /// </returns>
            public virtual MailMessageReply QueueMail(System.Net.Mail.MailMessage message, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (message == null)
                {
                    throw new ArgumentNullException(nameof(message));
                }

                if (this.ShouldUploadAttachments())
                {
                    this.UploadAndMutateAttachmentsAsync(message, headers, deadline, cancellationToken).GetAwaiter().GetResult();
                }

                return this.QueueMail(MailMessage.FromMessage(message), EnsureAuthHeaders(headers), deadline, cancellationToken);
            }

            /// <summary>
            /// Asynchronously queues an e-mail message for sending using the default
            /// call options.
            /// When the service address is not localhost, file-backed attachments will be uploaded
            /// and converted to attachment tokens.
            /// </summary>
            /// <param name="message">The SMTP-style mail message to queue.</param>
            /// <param name="headers">
            /// Optional metadata headers to include with the gRPC request.
            /// </param>
            /// <param name="deadline">
            /// Optional absolute deadline after which the call is cancelled.
            /// </param>
            /// <param name="cancellationToken">
            /// A token that can be used to cancel the operation.
            /// </param>
            /// <returns>
            /// An <see cref="AsyncUnaryCall{TResponse}"/> that completes with the
            /// server reply.
            /// </returns>
            public virtual Task<MailMessageReply> QueueMailReplyAsync(System.Net.Mail.MailMessage message, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (message == null)
                {
                    throw new ArgumentNullException(nameof(message));
                }

                if (this.ShouldUploadAttachments())
                {
                    this.UploadAndMutateAttachmentsAsync(message, headers, deadline, cancellationToken).GetAwaiter().GetResult();
                }

                return this.QueueMailAsync(MailMessage.FromMessage(message), EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync;
            }

            /// <summary>
            /// Asynchronously queues a protobuf e-mail message while applying configured client authentication headers.
            /// This helper is intended for callers that already have a persisted protobuf message, such as disk-resilience resend workflows.
            /// </summary>
            /// <param name="message">The protobuf mail message to queue.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional absolute deadline after which the call is cancelled.</param>
            /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
            /// <returns>A task that completes with the server reply.</returns>
            public virtual Task<MailMessageReply> QueueMailMessageReplyAsync(MailMessage message, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (message == null)
                {
                    throw new ArgumentNullException(nameof(message));
                }

                return this.QueueMailAsync(message, EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync;
            }

            /// <summary>
            /// Asynchronously queues an e-mail message with <see cref="MailSettings"/> using default call options.
            /// When the service address is not localhost, file-backed attachments will be uploaded
            /// and converted to attachment tokens.
            /// </summary>
            /// <param name="message">The SMTP-style mail message to queue.</param>
            /// <param name="settings">Custom per-message settings.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the server reply.</returns>
            public virtual async Task<MailMessageReply> QueueMailWithSettingsReplyAsync(System.Net.Mail.MailMessage message, MailSettings settings, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (message == null)
                {
                    throw new ArgumentNullException(nameof(message));
                }

                if (this.ShouldUploadAttachments())
                {
                    await this.UploadAndMutateAttachmentsAsync(message, headers, deadline, cancellationToken).ConfigureAwait(false);
                }

                var req = new MailMessageWithSettings
                {
                    Message = MailMessage.FromMessage(message),
                    Settings = settings,
                };

                return await this.QueueMailWithSettingsAsync(req, EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync.ConfigureAwait(false);
            }

            /// <summary>
            /// Asynchronously queues multiple e-mail messages in a single request.
            /// When the service address is not localhost, file-backed attachments will be uploaded
            /// and converted to attachment tokens.
            /// </summary>
            /// <param name="messages">Messages to queue.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the bulk reply.</returns>
            public virtual Task<QueueMailBulkReply> QueueMailBulkReplyAsync(IEnumerable<System.Net.Mail.MailMessage> messages, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (messages == null)
                {
                    var empty = new QueueMailBulkRequest();
                    return this.QueueMailBulkAsync(empty, EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync;
                }

                var req = new QueueMailBulkRequest();
                foreach (var m in messages.Where(x => x != null))
                {
                    if (this.ShouldUploadAttachments())
                    {
                        this.UploadAndMutateAttachmentsAsync(m, headers, deadline, cancellationToken).GetAwaiter().GetResult();
                    }

                    req.Mails.Add(new MailMessageWithSettings
                    {
                        Message = MailMessage.FromMessage(m),
                    });
                }

                return this.QueueMailBulkAsync(req, EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync;
            }

            /// <summary>
            /// Asynchronously queues a mail merge item, optionally appending to an existing merge batch.
            /// When the service address is not localhost, file-backed attachments will be uploaded
            /// and converted to attachment tokens.
            /// </summary>
            /// <param name="message">The SMTP-style mail message to queue.</param>
            /// <param name="mergeId">Optional merge batch identifier to append to.</param>
            /// <param name="headers">Optional metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the server reply containing the effective merge id.</returns>
            public virtual Task<QueueMailMergeReply> QueueMailMergeReplyAsync(System.Net.Mail.MailMessage message, string? mergeId = null, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (message == null)
                {
                    throw new ArgumentNullException(nameof(message));
                }

                if (this.ShouldUploadAttachments())
                {
                    this.UploadAndMutateAttachmentsAsync(message, headers, deadline, cancellationToken).GetAwaiter().GetResult();
                }

                var req = new QueueMailMergeRequest
                {
                    MergeId = mergeId ?? string.Empty,
                    Message = MailMessage.FromMessage(message),
                };

                return this.QueueMailMergeAsync(req, EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync;
            }

            /// <summary>
            /// Asynchronously queues a protobuf mail merge request while applying configured client authentication headers.
            /// </summary>
            /// <param name="request">The mail merge request to queue.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the server reply containing the effective merge id.</returns>
            public virtual Task<QueueMailMergeReply> QueueMailMergeRequestReplyAsync(QueueMailMergeRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                return this.QueueMailMergeAsync(request, EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync;
            }

            /// <summary>
            /// Asynchronously queues a protobuf mail merge request with settings while applying configured client authentication headers.
            /// </summary>
            /// <param name="request">The mail merge request to queue.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the server reply containing the effective merge id.</returns>
            public virtual Task<QueueMailMergeReply> QueueMailMergeWithSettingsRequestReplyAsync(QueueMailMergeWithSettingsRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                return this.QueueMailMergeWithSettingsAsync(request, EnsureAuthHeaders(headers), deadline, cancellationToken).ResponseAsync;
            }

            /// <summary>
            /// Lists the allowed staging test recipient e-mail addresses for the authenticated client.
            /// Administrators can provide <paramref name="clientId"/> to manage another client's list.
            /// </summary>
            /// <param name="clientId">Optional client identifier used by administrators when managing a client's allow-list.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the allowed test recipient e-mail addresses.</returns>
            public virtual async Task<IReadOnlyList<string>> ListAllowedTestRecipientEmailAddressesAsync(string? clientId = null, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                var reply = await this.ListAllowedTestRecipientsAsync(
                    new ListAllowedTestRecipientsRequest
                    {
                        ClientId = clientId ?? string.Empty,
                    },
                    EnsureAllowListHeaders(headers, clientId),
                    deadline,
                    cancellationToken).ResponseAsync.ConfigureAwait(false);

                return reply.EmailAddresses.ToArray();
            }

            /// <summary>
            /// Adds an e-mail address to the staging test recipient allow-list for the authenticated client.
            /// Administrators can provide <paramref name="clientId"/> to manage another client's list.
            /// </summary>
            /// <param name="emailAddress">E-mail address to allow for real staging delivery.</param>
            /// <param name="clientId">Optional client identifier used by administrators when managing a client's allow-list.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the server reply.</returns>
            public virtual Task<AddAllowedTestRecipientReply> AddAllowedTestRecipientEmailAddressAsync(string emailAddress, string? clientId = null, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (string.IsNullOrWhiteSpace(emailAddress))
                {
                    throw new ArgumentException("E-mail address is required.", nameof(emailAddress));
                }

                return this.AddAllowedTestRecipientAsync(
                    new AddAllowedTestRecipientRequest
                    {
                        EmailAddress = emailAddress,
                        ClientId = clientId ?? string.Empty,
                    },
                    EnsureAllowListHeaders(headers, clientId),
                    deadline,
                    cancellationToken).ResponseAsync;
            }

            /// <summary>
            /// Removes an e-mail address from the staging test recipient allow-list for the authenticated client.
            /// Administrators can provide <paramref name="clientId"/> to manage another client's list.
            /// </summary>
            /// <param name="emailAddress">E-mail address to remove from real staging delivery.</param>
            /// <param name="clientId">Optional client identifier used by administrators when managing a client's allow-list.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>A task that completes with the server reply.</returns>
            public virtual Task<DeleteAllowedTestRecipientReply> RemoveAllowedTestRecipientEmailAddressAsync(string emailAddress, string? clientId = null, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (string.IsNullOrWhiteSpace(emailAddress))
                {
                    throw new ArgumentException("E-mail address is required.", nameof(emailAddress));
                }

                return this.DeleteAllowedTestRecipientAsync(
                    new DeleteAllowedTestRecipientRequest
                    {
                        EmailAddress = emailAddress,
                        ClientId = clientId ?? string.Empty,
                    },
                    EnsureAllowListHeaders(headers, clientId),
                    deadline,
                    cancellationToken).ResponseAsync;
            }

            // New helpers for settings that attach replay-protection headers
            public virtual AsyncUnaryCall<SetSettingsReply> SetSettingsAdminAsync(SetSettingsMessage request, Metadata? headers = null, DateTime? deadline = null, System.Threading.CancellationToken cancellationToken = default)
                => this.SetSettingsAsync(request, EnsureAdminMutationHeaders(headers), deadline, cancellationToken);

            public virtual AsyncUnaryCall<SetMailSettingsReply> SetMailSettingsAdminAsync(SetMailSettingsMessage request, Metadata? headers = null, DateTime? deadline = null, System.Threading.CancellationToken cancellationToken = default)
                => this.SetMailSettingsAsync(request, EnsureAdminMutationHeaders(headers), deadline, cancellationToken);

            /// <summary>
            /// Gets attachment information using the admin-authorised endpoint.
            /// This helper applies replay-protection metadata expected by the server.
            /// </summary>
            /// <param name="request">The attachment info request.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>
            /// An <see cref="AsyncUnaryCall{TResponse}"/> that completes with the
            /// server reply.
            /// </returns>
            public virtual AsyncUnaryCall<GetAttachmentInfoReply> GetAttachmentInfoAdminAsync(GetAttachmentInfoRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                return this.GetAttachmentInfoAsync(request, EnsureAdminMutationHeaders(headers), deadline, cancellationToken);
            }

            /// <summary>
            /// Deletes an attachment using the admin-authorised endpoint.
            /// This helper applies replay-protection metadata expected by the server.
            /// </summary>
            /// <param name="request">The delete attachment request.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>
            /// An <see cref="AsyncUnaryCall{TResponse}"/> that completes with the
            /// server reply.
            /// </returns>
            public virtual AsyncUnaryCall<DeleteAttachmentReply> DeleteAttachmentAdminAsync(DeleteAttachmentRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (request == null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                return this.DeleteAttachmentAsync(request, EnsureAdminMutationHeaders(headers), deadline, cancellationToken);
            }

            /// <summary>
            /// Polls attachment info until the attachment exists and is ready, or until the timeout elapses.
            /// This helper is intended for admin diagnostic workflows where uploads are followed by
            /// operations that require ready state.
            /// </summary>
            /// <param name="token">The attachment token to wait for.</param>
            /// <param name="timeout">The maximum amount of time to wait.</param>
            /// <param name="pollInterval">The delay between polling attempts.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>The latest <see cref="GetAttachmentInfoReply"/> observed before returning.</returns>
            /// <exception cref="TimeoutException">Thrown when the timeout elapses before the attachment is ready.</exception>
            public virtual async Task<GetAttachmentInfoReply> WaitForAttachmentReadyAdminAsync(string token, TimeSpan timeout, TimeSpan pollInterval, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ArgumentException("Token is null or empty.", nameof(token));
                }

                if (timeout <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");
                }

                if (pollInterval <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be greater than zero.");
                }

                var startedUtc = DateTimeOffset.UtcNow;
                GetAttachmentInfoReply? lastReply = null;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var reply = await this.GetAttachmentInfoAdminAsync(
                        new GetAttachmentInfoRequest { Token = token },
                        headers,
                        deadline,
                        cancellationToken).ResponseAsync.ConfigureAwait(false);

                    lastReply = reply;

                    if (reply != null && reply.Exists && reply.Ready)
                    {
                        return reply;
                    }

                    var elapsed = DateTimeOffset.UtcNow - startedUtc;
                    if (elapsed >= timeout)
                    {
                        throw new TimeoutException("Attachment did not become ready within the specified timeout.");
                    }

                    var remaining = timeout - elapsed;
                    var delay = pollInterval < remaining ? pollInterval : remaining;

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            /// <summary>
            /// Attempts to delete an attachment only when it is unreferenced.
            /// This helper performs an info check and then issues a non-forced delete.
            /// </summary>
            /// <param name="token">The attachment token to delete.</param>
            /// <param name="headers">Optional gRPC metadata headers.</param>
            /// <param name="deadline">Optional call deadline.</param>
            /// <param name="cancellationToken">Cancellation token.</param>
            /// <returns>
            /// A tuple describing the outcome:
            /// <list type="bullet">
            /// <item><description><c>Deleted</c>: True if the attachment was deleted.</description></item>
            /// <item><description><c>WasReferenced</c>: True if the attachment existed and had a non-zero reference count.</description></item>
            /// <item><description><c>Info</c>: The observed attachment info response (may indicate not-found).</description></item>
            /// <item><description><c>Delete</c>: The delete response when a delete was attempted; otherwise null.</description></item>
            /// </list>
            /// </returns>
            public virtual async Task<(bool Deleted, bool WasReferenced, GetAttachmentInfoReply Info, DeleteAttachmentReply? Delete)> TryDeleteAttachmentIfUnreferencedAdminAsync(string token, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ArgumentException("Token is null or empty.", nameof(token));
                }

                var info = await this.GetAttachmentInfoAdminAsync(
                    new GetAttachmentInfoRequest { Token = token },
                    headers,
                    deadline,
                    cancellationToken).ResponseAsync.ConfigureAwait(false);

                if (info == null)
                {
                    return (false, false, new GetAttachmentInfoReply { Exists = false, Ready = false }, null);
                }

                if (!info.Exists)
                {
                    return (false, false, info, null);
                }

                if (info.RefCount > 0)
                {
                    return (false, true, info, null);
                }

                var deleteReply = await this.DeleteAttachmentAdminAsync(
                    new DeleteAttachmentRequest { Token = token, Force = false },
                    headers,
                    deadline,
                    cancellationToken).ResponseAsync.ConfigureAwait(false);

                var deleted = deleteReply != null && deleteReply.Success;

                return (deleted, false, info, deleteReply);
            }

            private static Metadata EnsureAuthHeaders(Metadata? headers)
            {
                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(sharedSecret))
                {
                    return headers ?? new Metadata();
                }

                var md = new Metadata();
                if (headers != null)
                {
                    foreach (var entry in headers)
                    {
                        md.Add(entry.Key, entry.Value ?? string.Empty);
                    }
                }

                var hasId = false;
                var hasPass = false;
                foreach (var e in md)
                {
                    if (e.Key == "x-client-id")
                    {
                        hasId = true;
                    }
                    else if (e.Key == "x-client-pass")
                    {
                        hasPass = true;
                    }
                }

                if (!hasId)
                {
                    md.Add("x-client-id", clientId);
                }

                if (!hasPass)
                {
                    md.Add("x-client-pass", ComputePassword(clientId!, sharedSecret!));
                }

                return md;
            }

            private static Metadata EnsureAdminMutationHeaders(Metadata? headers)
            {
                var md = EnsureAuthHeaders(headers);

                // Add replay-protection headers expected by server AuditInterceptor
                bool hasTs = false, hasNonce = false;
                foreach (var e in md)
                {
                    if (e.Key == "x-ts")
                    {
                        hasTs = true;
                    }
                    else if (e.Key == "x-nonce")
                    {
                        hasNonce = true;
                    }
                }

                if (!hasTs)
                {
                    md.Add("x-ts", DateTimeOffset.UtcNow.ToString("o"));
                }

                if (!hasNonce)
                {
                    md.Add("x-nonce", Guid.NewGuid().ToString("N"));
                }

                return md;
            }

            private static Metadata EnsureAllowListHeaders(Metadata? headers, string? clientIdOverride)
            {
                if (string.IsNullOrWhiteSpace(clientIdOverride))
                {
                    return EnsureAuthHeaders(headers);
                }

                return EnsureAdminMutationHeaders(headers);
            }

            private static string ComputePassword(string clientIdValue, string secret)
            {
                using var sha = SHA256.Create();
                var bytes = Encoding.UTF8.GetBytes(clientIdValue + ":" + secret);
                return Convert.ToBase64String(sha.ComputeHash(bytes));
            }
        }
    }
}