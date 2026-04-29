//-----------------------------------------------------------------------
// <copyright file="TestMail.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Grpc.Core;
    using MailForge.Grpc;
    using MailQueueNet.Common.Templating;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Components;
    using Microsoft.AspNetCore.Components.Forms;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Test page for sending standard and merge mails through the MailQueueNet queue server.
    /// </summary>
    public partial class TestMail : ComponentBase
    {
        private const long MaxUploadBytes = 25L * 1024L * 1024L;

        private const string DefaultClientId = "Funk Tester";
        private const string DefaultClientPass = "k/xK/xxQ2Gh/QwZrdvYGePqJMtwIAHEZxtk05EYRBKY=";

        private static readonly TimeSpan DefaultGrpcTimeout = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan AttachmentGrpcTimeout = TimeSpan.FromMinutes(2);

        private bool busy;
        private string? error;
        private string? status;

        private string mailServiceBaseAddress = string.Empty;
        private string clientId = string.Empty;
        private string clientPass = string.Empty;

        private string from = string.Empty;
        private string to = string.Empty;
        private string cc = string.Empty;
        private string bcc = string.Empty;
        private string subject = "Test message";
        private string body = "Hello from MailFunk";
        private bool isBodyHtml;

        private readonly List<IBrowserFile> attachments = new();
        private readonly List<string> attachmentNames = new();

        private string mergeId = string.Empty;
        private TemplateEngine mergeEngine = TemplateEngine.Liquid;
        private MergeTemplateKind mergeTemplateKind = MergeTemplateKind.SubjectBody;
        private string mergeTemplateFrom = string.Empty;
        private string mergeTemplateSubject = "Hello {{Name}}";
        private string mergeTemplateBody = "Hello {{Name}}";
        private string mergeJsonLines = "{\"Email\":\"to@example.com\",\"Name\":\"Test\"}";

        private readonly List<IBrowserFile> mergeAttachments = new();
        private readonly List<string> mergeAttachmentNames = new();

        private readonly List<string> mergeTemplateConversionWarnings = new();

        private TemplateEngine MergeEngine
        {
            get => this.mergeEngine;
            set
            {
                if (this.mergeEngine == value)
                {
                    return;
                }

                var previousEngine = this.mergeEngine;
                this.mergeEngine = value;
                this.ConvertMergeTemplateSyntax(previousEngine, value);
            }
        }

        /// <summary>
        /// Defines the supported merge template types for the test UI.
        /// </summary>
        public enum MergeTemplateKind
        {
            /// <summary>
            /// Uses plain text subject and body.
            /// </summary>
            SubjectBody = 0,

            /// <summary>
            /// Uses plain text subject and HTML body.
            /// </summary>
            HtmlBody = 1,
        }

        /// <summary>
        /// Gets or sets the configuration.
        /// </summary>
        [Inject]
        public IConfiguration Configuration { get; set; } = default!;

        /// <summary>
        /// Gets or sets the generated gRPC client used to communicate with the MailQueue server.
        /// This page creates its own configured channel for test purposes.
        /// </summary>
        [Inject]
        public MailGrpcService.MailGrpcServiceClient GrpcClient { get; set; } = default!;

        /// <summary>
        /// Gets or sets the logger instance.
        /// </summary>
        [Inject]
        public ILogger<TestMail> Logger { get; set; } = default!;

        /// <inheritdoc />
        protected override void OnInitialized()
        {
            var baseAddress = this.Configuration["MailService:BaseAddress"];
            this.mailServiceBaseAddress = string.IsNullOrWhiteSpace(baseAddress) ? "https://localhost:5001" : baseAddress;

            this.Logger.LogInformation(
                "[MailFunk TestMail] Initialised. MailServiceBaseAddress={BaseAddress} ClientId={ClientId}",
                this.mailServiceBaseAddress,
                string.IsNullOrWhiteSpace(this.clientId) ? "(not set)" : this.clientId);

            if (string.IsNullOrWhiteSpace(this.clientId))
            {
                this.clientId = DefaultClientId;
            }

            if (string.IsNullOrWhiteSpace(this.clientPass))
            {
                this.clientPass = DefaultClientPass;
            }

            if (string.IsNullOrWhiteSpace(this.from))
            {
                this.from = "from@example.com";
            }

            if (string.IsNullOrWhiteSpace(this.to))
            {
                this.to = "to@example.com";
            }

            this.mergeTemplateFrom = this.from;
            this.ApplyMergeTemplateDefaults();
        }

        private void ClearStandard()
        {
            this.error = null;
            this.status = null;

            this.from = string.Empty;
            this.to = string.Empty;
            this.cc = string.Empty;
            this.bcc = string.Empty;
            this.subject = string.Empty;
            this.body = string.Empty;
            this.isBodyHtml = false;

            this.attachments.Clear();
            this.attachmentNames.Clear();
        }

        private void ClearMerge()
        {
            this.error = null;
            this.status = null;

            this.mergeId = string.Empty;
            this.mergeTemplateFrom = this.from;
            this.ApplyMergeTemplateDefaults();
            this.mergeJsonLines = "{\"Email\":\"to@example.com\",\"Name\":\"Test\"}";

            this.mergeAttachments.Clear();
            this.mergeAttachmentNames.Clear();

            this.mergeTemplateConversionWarnings.Clear();
        }

        private void OnAttachmentsSelected(InputFileChangeEventArgs args)
        {
            this.attachments.Clear();
            this.attachmentNames.Clear();

            foreach (var f in args.GetMultipleFiles())
            {
                this.attachments.Add(f);
                this.attachmentNames.Add(f.Name);
            }
        }

        private void OnMergeAttachmentsSelected(InputFileChangeEventArgs args)
        {
            this.mergeAttachments.Clear();
            this.mergeAttachmentNames.Clear();

            foreach (var f in args.GetMultipleFiles())
            {
                this.mergeAttachments.Add(f);
                this.mergeAttachmentNames.Add(f.Name);
            }
        }

        private async Task OnMergeFileSelected(InputFileChangeEventArgs args)
        {
            this.error = null;
            this.status = null;

            var file = args.File;
            if (file == null)
            {
                return;
            }

            this.Logger.LogInformation(
                "[MailFunk TestMail] Merge JSONL file selected. Name={FileName} SizeBytes={SizeBytes}",
                file.Name,
                file.Size);

            try
            {
                await using var stream = file.OpenReadStream(MaxUploadBytes);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
                this.mergeJsonLines = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
        }

        private async Task SendStandardAsync()
        {
            this.error = null;
            this.status = null;
            this.busy = true;

            await this.TryRefreshUiAsync();

            try
            {
                using var cts = new CancellationTokenSource(DefaultGrpcTimeout);
                var deadline = DateTime.UtcNow.Add(DefaultGrpcTimeout);

                var headers = this.BuildClientAuthHeaders();

                this.Logger.LogInformation(
                    "[MailFunk TestMail] QueueMail starting. From={From} To={To} SubjectLength={SubjectLength} BodyLength={BodyLength} Attachments={Attachments}",
                    this.from,
                    this.to,
                    (this.subject ?? string.Empty).Length,
                    (this.body ?? string.Empty).Length,
                    this.attachments.Count);

                using var message = new System.Net.Mail.MailMessage();

                message.From = this.ParseSingleAddressRequired(this.from, "From");
                this.AddAddresses(message.To, this.to);
                this.AddAddresses(message.CC, this.cc);
                this.AddAddresses(message.Bcc, this.bcc);

                message.Subject = this.subject ?? string.Empty;
                message.Body = this.body ?? string.Empty;
                message.IsBodyHtml = this.isBodyHtml;

                await this.AddAttachmentsToMessageAsync(message, this.attachments, cts.Token).ConfigureAwait(false);

                var reply = await this.GrpcClient.QueueMailReplyAsync(message, headers, deadline, cts.Token).ConfigureAwait(false);

                if (reply == null)
                {
                    this.error = "No reply returned from server.";
                    return;
                }

                if (!reply.Success)
                {
                    this.error = "Server returned Success=false.";

                    this.Logger.LogWarning("[MailFunk TestMail] QueueMail returned Success=false.");
                    return;
                }

                this.status = "Queued successfully.";
                this.Logger.LogInformation("[MailFunk TestMail] QueueMail succeeded.");
            }
            catch (RpcException ex)
            {
                this.error = $"gRPC error: {ex.StatusCode} {ex.Status.Detail}";
                this.Logger.LogWarning(ex, "[MailFunk TestMail] QueueMail gRPC failed. StatusCode={StatusCode} Detail={Detail}", ex.StatusCode, ex.Status.Detail);
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
                this.Logger.LogError(ex, "[MailFunk TestMail] QueueMail failed.");
            }
            finally
            {
                this.busy = false;
                await this.TryRefreshUiAsync();
            }
        }

        private async Task SendMergeAsync()
        {
            this.error = null;
            this.status = null;
            this.busy = true;

            await this.TryRefreshUiAsync();

            try
            {
                var headers = this.BuildClientAuthHeaders();

                var effectiveMergeId = this.ResolveMergeIdForRequest(this.mergeId, this.mergeAttachments);

                using var template = new System.Net.Mail.MailMessage();

                template.From = this.ParseSingleAddressRequired(this.mergeTemplateFrom, "Template from");

                this.ApplyMergeHeaders(template, effectiveMergeId);

                template.Subject = this.mergeTemplateSubject ?? string.Empty;
                template.Body = this.mergeTemplateBody ?? string.Empty;
                template.IsBodyHtml = this.mergeTemplateKind == MergeTemplateKind.HtmlBody;

                using var attachmentCts = new CancellationTokenSource(AttachmentGrpcTimeout);
                var attachmentDeadline = DateTime.UtcNow.Add(AttachmentGrpcTimeout);

                await this.AddAttachmentsToMessageAsync(template, this.mergeAttachments, attachmentCts.Token).ConfigureAwait(false);

                if (template.Attachments.Count > 0)
                {
                    _ = await this.GrpcClient.UploadMergeAttachmentsAndApplyTokensAsync(
                        template,
                        effectiveMergeId,
                        removeOriginalAttachments: true,
                        headers: headers,
                        deadline: attachmentDeadline,
                        cancellationToken: attachmentCts.Token).ConfigureAwait(false);
                }

                using var cts = new CancellationTokenSource(DefaultGrpcTimeout);
                var deadline = DateTime.UtcNow.Add(DefaultGrpcTimeout);

                var lines = this.SplitJsonLines(this.mergeJsonLines);

                this.Logger.LogInformation(
                    "[MailFunk TestMail] QueueMailMerge starting. MergeId={MergeId} TemplateKind={TemplateKind} Lines={Lines} SubjectLength={SubjectLength} BodyLength={BodyLength}",
                    string.IsNullOrWhiteSpace(this.mergeId) ? "(server-generated)" : this.mergeId,
                    this.mergeTemplateKind,
                    lines.Length,
                    (this.mergeTemplateSubject ?? string.Empty).Length,
                    (this.mergeTemplateBody ?? string.Empty).Length);

                var req = new QueueMailMergeRequest
                {
                    MergeId = effectiveMergeId,
                    Message = MailQueueNet.Grpc.MailMessage.FromMessage(template),
                };

                req.JsonLines.AddRange(lines);

                var reply = await this.GrpcClient.QueueMailMergeAsync(req, headers, deadline, cts.Token).ResponseAsync.ConfigureAwait(false);

                if (reply == null)
                {
                    this.error = "No reply returned from server.";
                    return;
                }

                if (!reply.Success)
                {
                    this.error = "Server returned Success=false (merge may be closed or rejected).";

                    this.Logger.LogWarning(
                        "[MailFunk TestMail] QueueMailMerge returned Success=false. MergeId={MergeId}",
                        reply.MergeId ?? this.mergeId);
                    return;
                }

                this.mergeId = reply.MergeId ?? effectiveMergeId;
                this.status = $"Merge queued successfully. MergeId={reply.MergeId}; Template={reply.TemplateFileName}; BatchId={reply.BatchId}.";

                this.Logger.LogInformation(
                    "[MailFunk TestMail] QueueMailMerge succeeded. MergeId={MergeId} TemplateFileName={TemplateFileName} BatchId={BatchId}",
                    reply.MergeId,
                    reply.TemplateFileName,
                    reply.BatchId);
            }
            catch (RpcException ex)
            {
                this.error = $"gRPC error: {ex.StatusCode} {ex.Status.Detail}";
                this.Logger.LogWarning(ex, "[MailFunk TestMail] QueueMailMerge gRPC failed. StatusCode={StatusCode} Detail={Detail}", ex.StatusCode, ex.Status.Detail);
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
                this.Logger.LogError(ex, "[MailFunk TestMail] QueueMailMerge failed.");
            }
            finally
            {
                this.busy = false;
                await this.TryRefreshUiAsync();
            }
        }

        private async Task TryRefreshUiAsync()
        {
            try
            {
                await this.InvokeAsync(this.StateHasChanged);
            }
            catch
            {
                // Ignore render errors (for example, if the circuit disconnected mid-send).
            }
        }

        private void ApplyMergeTemplateDefaults()
        {
            this.mergeTemplateConversionWarnings.Clear();

            if (this.mergeEngine == TemplateEngine.Handlebars)
            {
                this.mergeTemplateSubject = "Hello {{Name}}";
                this.mergeTemplateBody = "Hello {{Name}}";
                return;
            }

            this.mergeTemplateSubject = "Hello {{ Name }}";
            this.mergeTemplateBody = "Hello {{ Name }}";
        }

        /// <summary>
        /// Converts the merge template subject and body between Liquid/Fluid and Handlebars syntax when the
        /// user changes the selected engine.
        /// </summary>
        /// <param name="previous">The previously selected template engine.</param>
        /// <param name="current">The newly selected template engine.</param>
        private void ConvertMergeTemplateSyntax(TemplateEngine previous, TemplateEngine current)
        {
            this.mergeTemplateConversionWarnings.Clear();

            if (string.IsNullOrWhiteSpace(this.mergeTemplateSubject) && string.IsNullOrWhiteSpace(this.mergeTemplateBody))
            {
                this.ApplyMergeTemplateDefaults();
                return;
            }

            if (TemplateSyntaxConverter.TryConvert(this.mergeTemplateSubject, previous, current, out var convertedSubject, out var subjectMessage))
            {
                this.mergeTemplateSubject = convertedSubject;
                if (!string.IsNullOrWhiteSpace(subjectMessage))
                {
                    this.mergeTemplateConversionWarnings.Add("Subject: " + subjectMessage);
                    this.Logger.LogInformation("[MailFunk TestMail] Subject template converted ({From} -> {To}). Note={Note}", previous, current, subjectMessage);
                }
            }
            else
            {
                this.Logger.LogWarning("[MailFunk TestMail] Subject template could not be converted ({From} -> {To}).", previous, current);
                this.mergeTemplateConversionWarnings.Add($"Subject could not be converted ({previous} -> {current}).");
            }

            if (TemplateSyntaxConverter.TryConvert(this.mergeTemplateBody, previous, current, out var convertedBody, out var bodyMessage))
            {
                this.mergeTemplateBody = convertedBody;
                if (!string.IsNullOrWhiteSpace(bodyMessage))
                {
                    this.mergeTemplateConversionWarnings.Add("Body: " + bodyMessage);
                    this.Logger.LogInformation("[MailFunk TestMail] Body template converted ({From} -> {To}). Note={Note}", previous, current, bodyMessage);
                }
            }
            else
            {
                this.Logger.LogWarning("[MailFunk TestMail] Body template could not be converted ({From} -> {To}).", previous, current);
                this.mergeTemplateConversionWarnings.Add($"Body could not be converted ({previous} -> {current}).");
            }
        }

        private void ApplyMergeHeaders(System.Net.Mail.MailMessage template, string mergeIdValue)
        {
            if (template == null)
            {
                throw new ArgumentNullException(nameof(template));
            }

            if (!string.IsNullOrWhiteSpace(mergeIdValue))
            {
                try
                {
                    template.Headers["X-MailMerge-Id"] = mergeIdValue;
                }
                catch
                {
                }
            }

            try
            {
                template.Headers["X-MailMerge-Engine"] = this.mergeEngine.ToString();
            }
            catch
            {
            }
        }

        private string ResolveMergeIdForRequest(string? mergeIdValue, IReadOnlyList<IBrowserFile> files)
        {
            if (!string.IsNullOrWhiteSpace(mergeIdValue))
            {
                return mergeIdValue.Trim();
            }

            if (files == null || files.Count == 0)
            {
                return string.Empty;
            }

            return Guid.NewGuid().ToString("N");
        }

        private bool ShouldUploadAttachments()
        {
            var target = this.mailServiceBaseAddress;
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

        private Metadata? BuildClientAuthHeaders()
        {
            if (string.IsNullOrWhiteSpace(this.clientId) || string.IsNullOrWhiteSpace(this.clientPass))
            {
                return null;
            }

            var md = new Metadata
            {
                { "x-client-id", this.clientId },
                { "x-client-pass", this.clientPass },
            };

            if (MailClientConfiguration.Current == null)
            {
                MailClientConfiguration.Current = new MailClientConfiguration();
            }

            MailClientConfiguration.Current.MailQueueNetServiceChannelAddress = this.mailServiceBaseAddress;

            return md;
        }

        private async Task AddAttachmentsToMessageAsync(System.Net.Mail.MailMessage message, IReadOnlyList<IBrowserFile> files, CancellationToken cancellationToken)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (files == null || files.Count == 0)
            {
                return;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tempPath = Path.Combine(Path.GetTempPath(), "MailFunk_TestMail_" + Guid.NewGuid().ToString("N") + "_" + file.Name);

                await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var upload = file.OpenReadStream(MaxUploadBytes, cancellationToken))
                {
                    await upload.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }

                var attachment = new System.Net.Mail.Attachment(tempPath);
                attachment.Name = file.Name;

                message.Attachments.Add(attachment);
            }
        }

        private System.Net.Mail.MailAddress ParseSingleAddressRequired(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(fieldName + " is required.");
            }

            try
            {
                return new System.Net.Mail.MailAddress(value);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(fieldName + " is not a valid email address: " + ex.Message);
            }
        }

        private void AddAddresses(System.Net.Mail.MailAddressCollection target, string? raw)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var parts = raw.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var trimmed = p.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                target.Add(new System.Net.Mail.MailAddress(trimmed));
            }
        }

        private string[] SplitJsonLines(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            return raw
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
        }
    }
}
