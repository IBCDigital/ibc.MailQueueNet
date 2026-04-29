//-----------------------------------------------------------------------
// <copyright file="AdminSharedSecretInterceptor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.GrpcInterceptors
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Globalization;
    using global::Grpc.Core;
    using global::Grpc.Core.Interceptors;
    using Microsoft.Extensions.Configuration;

    /// <summary>
    /// Adds admin shared-secret headers to internal gRPC calls so MailFunk can call
    /// admin endpoints over HTTP within the stack.
    /// </summary>
    public sealed class AdminSharedSecretInterceptor : Interceptor
    {
        private const string AdminIdHeaderName = "x-admin-id";
        private const string AdminPassHeaderName = "x-admin-pass";
        private const string AdminTimestampHeaderName = "x-ts";
        private const string AdminNonceHeaderName = "x-nonce";

        private readonly IConfiguration configuration;

        /// <summary>
        /// Initialises a new instance of the <see cref="AdminSharedSecretInterceptor"/> class.
        /// </summary>
        /// <param name="configuration">Application configuration.</param>
        public AdminSharedSecretInterceptor(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        /// <inheritdoc />
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            var updated = this.TryAddAdminHeaders(context);
            return base.AsyncUnaryCall(request, updated, continuation);
        }

        /// <inheritdoc />
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            var updated = this.TryAddAdminHeaders(context);
            return base.AsyncServerStreamingCall(request, updated, continuation);
        }

        /// <inheritdoc />
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
            where TRequest : class
            where TResponse : class
        {
            var updated = this.TryAddAdminHeaders(context);
            return base.BlockingUnaryCall(request, updated, continuation);
        }

        private ClientInterceptorContext<TRequest, TResponse> TryAddAdminHeaders<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
            where TRequest : class
            where TResponse : class
        {
            var secret = this.configuration["Security:AdminSharedSecret"];
            if (string.IsNullOrWhiteSpace(secret))
            {
                return context;
            }

            var adminId = this.configuration["Security:AdminClientId"];
            if (string.IsNullOrWhiteSpace(adminId))
            {
                adminId = "MailFunk";
            }

            var pass = ComputePassword(adminId, secret);
            var timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var nonce = Guid.NewGuid().ToString("N");

            var existingHeaders = context.Options.Headers;
            var headers = new Metadata();
            if (existingHeaders != null)
            {
                foreach (var entry in existingHeaders)
                {
                    headers.Add(entry);
                }
            }

            // Avoid duplicates.
            if (!HasHeader(headers, AdminIdHeaderName))
            {
                headers.Add(AdminIdHeaderName, adminId);
            }

            if (!HasHeader(headers, AdminPassHeaderName))
            {
                headers.Add(AdminPassHeaderName, pass);
            }

            if (!HasHeader(headers, AdminTimestampHeaderName))
            {
                headers.Add(AdminTimestampHeaderName, timestamp);
            }

            if (!HasHeader(headers, AdminNonceHeaderName))
            {
                headers.Add(AdminNonceHeaderName, nonce);
            }

            var options = context.Options.WithHeaders(headers);
            return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, options);
        }

        private static string ComputePassword(string clientId, string sharedSecret)
        {
            using var sha = SHA256.Create();
            var data = Encoding.UTF8.GetBytes(clientId + ":" + sharedSecret);
            return Convert.ToBase64String(sha.ComputeHash(data));
        }

        private static bool HasHeader(Metadata headers, string name)
        {
            foreach (var entry in headers)
            {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
