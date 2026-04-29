// <copyright file="ConnectivityService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Default implementation of <see cref="IConnectivityService"/> using MailGrpcService.
    /// </summary>
    public sealed class ConnectivityService : IConnectivityService, IDisposable
    {
        private readonly MailGrpcService.MailGrpcServiceClient client;
        private PeriodicTimer? timer;
        private CancellationTokenSource? cts;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectivityService"/> class.
        /// </summary>
        /// <param name="client">The gRPC client.</param>
        public ConnectivityService(MailGrpcService.MailGrpcServiceClient client)
        {
            this.client = client;
        }

        /// <inheritdoc />
        public event Action? StatusChanged;

        /// <inheritdoc />
        public bool IsOnline { get; private set; }

        /// <inheritdoc />
        public bool IsChecking { get; private set; }

        /// <inheritdoc />
        public string? LastError { get; private set; }

        /// <summary>
        /// Gets the subject of the client certificate, if available.
        /// </summary>
        public string? ClientCertificateSubject { get; private set; }

        /// <summary>
        /// Gets the thumbprint of the client certificate, if available.
        /// </summary>
        public string? ClientCertificateThumbprint { get; private set; }

        /// <inheritdoc />
        public void StartMonitoring(TimeSpan interval)
        {
            this.StopMonitoring();
            this.cts = new CancellationTokenSource();
            this.timer = new PeriodicTimer(interval);

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (await this.timer.WaitForNextTickAsync(this.cts.Token))
                        {
                            await this.CheckNowAsync(this.cts.Token);
                        }
                    }
                    catch
                    {
                        // shutting down
                    }
                },
                this.cts.Token);
        }

        /// <inheritdoc />
        public void StopMonitoring()
        {
            try
            {
                this.timer?.Dispose();
            }
            catch
            {
            }

            try
            {
                this.cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                this.cts?.Dispose();
            }
            catch
            {
            }

            this.timer = null;
            this.cts = null;
        }

        /// <inheritdoc />
        public async Task CheckNowAsync(CancellationToken token = default)
        {
            this.IsChecking = true;
            this.LastError = null;
            this.RaiseChanged();

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(2);
                var reply = await this.client.GetServiceConfigAsync(new GetServiceConfigRequest(), deadline: deadline, cancellationToken: token).ResponseAsync;
                this.IsOnline = !string.IsNullOrWhiteSpace(reply?.QueueFolder);

                try
                {
                    // Attempt to read cert info via underlying handler (only once)
                    if (this.ClientCertificateThumbprint is null)
                    {
                        var handlerField = this.client.GetType().GetField("__ClientCertificate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (handlerField != null)
                        {
                            if (handlerField.GetValue(this.client) is System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
                            {
                                this.ClientCertificateThumbprint = cert.Thumbprint;
                                this.ClientCertificateSubject = cert.Subject;
                            }
                        }
                    }
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                this.IsOnline = false;
                this.LastError = ex.Message;
            }
            finally
            {
                this.IsChecking = false;
                this.RaiseChanged();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.StopMonitoring();
        }

        private void RaiseChanged()
        {
            this.StatusChanged?.Invoke();
        }
    }
}
