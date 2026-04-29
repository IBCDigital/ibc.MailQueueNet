// <copyright file="IConnectivityService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Provides connectivity monitoring to the MailQueueNet gRPC service.
    /// </summary>
    public interface IConnectivityService
    {
        /// <summary>Raised when connectivity state changes.</summary>
        event Action? StatusChanged;

        /// <summary>Gets a value indicating whether the service is reachable.</summary>
        bool IsOnline { get; }

        /// <summary>Gets a value indicating whether a connectivity check is in progress.</summary>
        bool IsChecking { get; }

        /// <summary>Gets the last error message from a failed check.</summary>
        string? LastError { get; }

        /// <summary>Gets the subject of the client certificate used (if any).</summary>
        string? ClientCertificateSubject { get; }

        /// <summary>Gets the thumbprint of the client certificate used (if any).</summary>
        string? ClientCertificateThumbprint { get; }

        /// <summary>Starts periodic connectivity monitoring.</summary>
        /// <param name="interval">Polling interval.</param>
        void StartMonitoring(TimeSpan interval);

        /// <summary>Stops periodic connectivity monitoring.</summary>
        void StopMonitoring();

        /// <summary>Performs a one-shot connectivity check now.</summary>
        /// <param name="token">Cancellation token.</param>
        Task CheckNowAsync(CancellationToken token = default);
    }
}
