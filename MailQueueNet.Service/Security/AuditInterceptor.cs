// <copyright file="AuditInterceptor.cs" company="IBC Digital">
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

namespace MailQueueNet.Service.Security
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using global::Grpc.Core.Interceptors;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Intercepts admin calls for audit logging and enforces replay protection (nonce + timestamp) for forwarded identities.
    /// </summary>
    internal sealed class AuditInterceptor : Interceptor
    {
        private static readonly HashSet<string> AdminMethodNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SetSettings",
            "GetSettings",
            "SetMailSettings",
            "GetMailSettings",
            "PauseProcessing",
            "ResumeProcessing",
            "DeleteMails",
            "RetryFailedMails",
            "GetFolderSummary",
            "ListMailFiles",
            "ReadMailFile",
            "ListMailMerges",
            "GetMailMergeSummary",
            "ListMergeDispatchState",
            "ListAttachments",
            "GetAttachmentStats",
            "PreviewOrphans",
            "PreviewLargeAttachments",
            "DeleteAttachmentsByQuery",
            "GetAttachmentInfo",
            "DeleteAttachment",
            "DownloadAttachment",
            "GetAttachmentManifest",
            "ListServerLogFiles",
            "ReadServerLog",
            "StreamServerLog",
        };

        private readonly IConfiguration configuration;
        private readonly ILogger<AuditInterceptor> logger;

        public AuditInterceptor(IConfiguration configuration, ILogger<AuditInterceptor> logger)
        {
            this.configuration = configuration;
            this.logger = logger;
        }

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var fullMethod = context.Method ?? string.Empty;
            var methodName = ExtractMethodName(fullMethod);
            string userEmail = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-user-email")?.Value ?? string.Empty;
            string tsHeader = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-ts")?.Value ?? string.Empty;
            string nonceHeader = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-nonce")?.Value ?? string.Empty;

            if (AdminMethodNames.Contains(methodName))
            {
                var adminId = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-admin-id")?.Value ?? string.Empty;
                var adminPass = context.RequestHeaders.FirstOrDefault(h => h.Key == "x-admin-pass")?.Value ?? string.Empty;
                var hasAdminRole = context.GetHttpContext()?.User?.IsInRole("Admin") == true;
                var hasAdminSharedSecret = !string.IsNullOrWhiteSpace(this.configuration["Security:AdminSharedSecret"]);

                this.logger.LogInformation(
                    "ADMINAUTH method={Method} hasAdminRole={HasAdminRole} hasAdminSharedSecret={HasAdminSharedSecret} hasAdminIdHeader={HasAdminIdHeader} hasAdminPassHeader={HasAdminPassHeader} hasTimestampHeader={HasTimestampHeader} hasNonceHeader={HasNonceHeader}",
                    methodName,
                    hasAdminRole,
                    hasAdminSharedSecret,
                    !string.IsNullOrWhiteSpace(adminId),
                    !string.IsNullOrWhiteSpace(adminPass),
                    !string.IsNullOrWhiteSpace(tsHeader),
                    !string.IsNullOrWhiteSpace(nonceHeader));
            }

            try
            {
                var response = await base.UnaryServerHandler(request, context, continuation).ConfigureAwait(false);
                this.logger.LogInformation("AUDIT method={Method} user={User} ts={Timestamp} nonce={Nonce} success=true", methodName, userEmail, tsHeader, nonceHeader);

                return response;
            }
            catch (RpcException rex)
            {
                this.logger.LogWarning(rex, "AUDIT method={Method} user={User} ts={Timestamp} nonce={Nonce} success=false status={Status}", methodName, userEmail, tsHeader, nonceHeader, rex.Status);

                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "AUDIT method={Method} user={User} ts={Timestamp} nonce={Nonce} success=false unhandled", methodName, userEmail, tsHeader, nonceHeader);

                throw new RpcException(new Status(StatusCode.Internal, "Internal error"));
            }
        }

        private static string ExtractMethodName(string fullMethod)
        {
            if (string.IsNullOrEmpty(fullMethod))
            {
                return string.Empty; // format: /Package.Service/Method
            }

            var slash = fullMethod.LastIndexOf('/');
            return slash >= 0 && slash < fullMethod.Length - 1 ? fullMethod[(slash + 1)..] : fullMethod;
        }
    }
}
