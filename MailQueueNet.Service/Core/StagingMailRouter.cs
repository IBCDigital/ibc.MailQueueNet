//-----------------------------------------------------------------------
// <copyright file="StagingMailRouter.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;
    using MailQueueNet.Senders;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    /// <summary>
    /// Applies staging-only mail routing rules.
    /// </summary>
    public sealed class StagingMailRouter : IStagingMailRouter
    {
        private readonly IHostEnvironment hostEnvironment;
        private readonly ILogger<StagingMailRouter> logger;
        private readonly IOptionsMonitor<StagingMailRoutingOptions> optionsMonitor;
        private readonly IStagingRecipientAllowListStore allowListStore;

        /// <summary>
        /// Initialises a new instance of the <see cref="StagingMailRouter"/> class.
        /// </summary>
        /// <param name="hostEnvironment">Host environment.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="optionsMonitor">Routing options monitor.</param>
        /// <param name="allowListStore">Allow-list store.</param>
        public StagingMailRouter(
            IHostEnvironment hostEnvironment,
            ILogger<StagingMailRouter> logger,
            IOptionsMonitor<StagingMailRoutingOptions> optionsMonitor,
            IStagingRecipientAllowListStore allowListStore)
        {
            this.hostEnvironment = hostEnvironment;
            this.logger = logger;
            this.optionsMonitor = optionsMonitor;
            this.allowListStore = allowListStore;
        }

        /// <inheritdoc />
        public bool ShouldRoute(string clientId, MailSettings settings)
        {
            var options = this.optionsMonitor.CurrentValue;
            if (!this.hostEnvironment.IsStaging())
            {
                return false;
            }

            if (options == null || !options.Enabled)
            {
                return false;
            }

            if (options.Mailpit == null || string.IsNullOrWhiteSpace(options.Mailpit.Host))
            {
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public async Task<bool> SendAsync(System.Net.Mail.MailMessage message, string clientId, MailSettings effectiveSettings, CancellationToken cancellationToken)
        {
            var options = this.optionsMonitor.CurrentValue;
            if (options == null)
            {
                return false;
            }

            var normalisedClientId = (clientId ?? string.Empty).Trim();
            var allowList = await this.allowListStore.ListAsync(normalisedClientId, cancellationToken).ConfigureAwait(false);
            var allowSet = new HashSet<string>(allowList, StringComparer.OrdinalIgnoreCase);
            var plan = BuildPlan(message, normalisedClientId, allowSet, options);

            var delivered = false;
            if (plan.SendMailpitCopy)
            {
                using var mailpitCopy = CloneMessage(message);
                StampRoutingHeaders(mailpitCopy, "mailpit", normalisedClientId);
                var mailpitSettings = BuildSmtpSettings(options.Mailpit);
                delivered = await new SMTP().SendMailAsync(mailpitCopy, mailpitSettings).ConfigureAwait(false) || delivered;
            }

            if (plan.SendRealSmtpCopy)
            {
                using var realCopy = CloneMessage(message);
                ApplyFilteredRecipients(realCopy, allowSet);
                if (!string.IsNullOrWhiteSpace(options.SubjectPrefix))
                {
                    realCopy.Subject = (options.SubjectPrefix ?? string.Empty) + (realCopy.Subject ?? string.Empty);
                }

                StampRoutingHeaders(realCopy, "real-smtp", normalisedClientId);
                var realSettings = BuildSmtpSettings(options.RealSmtp);
                delivered = await new SMTP().SendMailAsync(realCopy, realSettings).ConfigureAwait(false) || delivered;
            }

            this.logger.LogInformation(
                "Staging mail routing applied (ClientId={ClientId}; Mailpit={Mailpit}; RealSmtp={RealSmtp}; AllowedRecipients={AllowedRecipients}; ToCount={ToCount}; CcCount={CcCount}; BccCount={BccCount})",
                normalisedClientId,
                plan.SendMailpitCopy,
                plan.SendRealSmtpCopy,
                plan.RealRecipients.Count,
                message.To.Count,
                message.CC.Count,
                message.Bcc.Count);

            return delivered;
        }

        private static StagingRecipientRoutingPlan BuildPlan(System.Net.Mail.MailMessage message, string clientId, HashSet<string> allowSet, StagingMailRoutingOptions options)
        {
            var realRecipients = new List<System.Net.Mail.MailAddress>();
            AddAllowed(message.To, allowSet, realRecipients);
            AddAllowed(message.CC, allowSet, realRecipients);
            AddAllowed(message.Bcc, allowSet, realRecipients);

            return new StagingRecipientRoutingPlan
            {
                ClientId = clientId,
                RealRecipients = realRecipients,
                SendMailpitCopy = true,
                SendRealSmtpCopy = !options.ForceMailpitOnly &&
                    !string.IsNullOrWhiteSpace(options.RealSmtp?.Host) &&
                    realRecipients.Count > 0,
            };
        }

        private static void AddAllowed(System.Net.Mail.MailAddressCollection source, HashSet<string> allowSet, List<System.Net.Mail.MailAddress> result)
        {
            foreach (var address in source.Cast<System.Net.Mail.MailAddress>())
            {
                var key = (address?.Address ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (allowSet.Contains(key))
                {
                    result.Add(address);
                }
            }
        }

        private static void ApplyFilteredRecipients(System.Net.Mail.MailMessage message, HashSet<string> allowSet)
        {
            FilterCollection(message.To, allowSet);
            FilterCollection(message.CC, allowSet);
            FilterCollection(message.Bcc, allowSet);
        }

        private static void FilterCollection(System.Net.Mail.MailAddressCollection collection, HashSet<string> allowSet)
        {
            var toKeep = collection.Cast<System.Net.Mail.MailAddress>()
                .Where(a => allowSet.Contains((a?.Address ?? string.Empty).Trim().ToLowerInvariant()))
                .ToArray();

            collection.Clear();
            foreach (var address in toKeep)
            {
                collection.Add(address);
            }
        }

        private static void StampRoutingHeaders(System.Net.Mail.MailMessage message, string route, string clientId)
        {
            message.Headers["X-Staging-Routed"] = route;
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                message.Headers["X-Staging-ClientId"] = clientId;
            }
        }

        private static MailSettings BuildSmtpSettings(SmtpDeliveryOptions options)
        {
            return new MailSettings
            {
                Smtp = new SmtpMailSettings
                {
                    Host = options?.Host ?? string.Empty,
                    Port = options?.Port ?? 0,
                    RequiresSsl = options?.RequiresSsl ?? false,
                    RequiresAuthentication = options?.RequiresAuthentication ?? false,
                    Username = options?.Username ?? string.Empty,
                    Password = options?.Password ?? string.Empty,
                    ConnectionTimeout = options?.ConnectionTimeout ?? 100000,
                },
            };
        }

        private static System.Net.Mail.MailMessage CloneMessage(System.Net.Mail.MailMessage source)
        {
            var clone = new System.Net.Mail.MailMessage
            {
                Subject = source.Subject,
                Body = source.Body,
                IsBodyHtml = source.IsBodyHtml,
                Priority = source.Priority,
                DeliveryNotificationOptions = source.DeliveryNotificationOptions,
                BodyEncoding = source.BodyEncoding,
                SubjectEncoding = source.SubjectEncoding,
                HeadersEncoding = source.HeadersEncoding,
            };

            if (source.From != null)
            {
                clone.From = new System.Net.Mail.MailAddress(source.From.Address, source.From.DisplayName);
            }

            if (source.Sender != null)
            {
                clone.Sender = new System.Net.Mail.MailAddress(source.Sender.Address, source.Sender.DisplayName);
            }

            foreach (var item in source.To.Cast<System.Net.Mail.MailAddress>())
            {
                clone.To.Add(new System.Net.Mail.MailAddress(item.Address, item.DisplayName));
            }

            foreach (var item in source.CC.Cast<System.Net.Mail.MailAddress>())
            {
                clone.CC.Add(new System.Net.Mail.MailAddress(item.Address, item.DisplayName));
            }

            foreach (var item in source.Bcc.Cast<System.Net.Mail.MailAddress>())
            {
                clone.Bcc.Add(new System.Net.Mail.MailAddress(item.Address, item.DisplayName));
            }

            foreach (var item in source.ReplyToList.Cast<System.Net.Mail.MailAddress>())
            {
                clone.ReplyToList.Add(new System.Net.Mail.MailAddress(item.Address, item.DisplayName));
            }

            foreach (var key in source.Headers.AllKeys)
            {
                var values = source.Headers.GetValues(key) ?? Array.Empty<string>();
                foreach (var value in values)
                {
                    clone.Headers.Add(key, value);
                }
            }

            foreach (var attachment in source.Attachments)
            {
                if (attachment.ContentStream is not System.IO.FileStream fileStream)
                {
                    continue;
                }

                var clonedAttachment = new System.Net.Mail.Attachment(fileStream.Name, attachment.ContentType?.MediaType ?? string.Empty)
                {
                    Name = attachment.Name,
                    ContentId = attachment.ContentId,
                };

                clone.Attachments.Add(clonedAttachment);
            }

            return clone;
        }
    }
}
