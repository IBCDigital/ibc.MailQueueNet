// <copyright file="ServiceStatus.razor.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Components.Shared
{
    using System;
    using System.Threading.Tasks;
    using MailFunk.Services;
    using Microsoft.AspNetCore.Components;

    /// <summary>
    /// Displays the connectivity status to the MailQueueNet service.
    /// </summary>
    public partial class ServiceStatus : ComponentBase, IDisposable
    {
        /// <summary>Gets or sets the connectivity service.</summary>
        [Inject]
        public IConnectivityService Connectivity { get; set; } = default!;

        private string StatusText => this.Connectivity.IsOnline ? "Online" : "Offline";

        private string? CertificateSubject => this.Connectivity.ClientCertificateSubject;

        private string? CertificateThumbprint => this.Connectivity.ClientCertificateThumbprint;

        private bool IsSecure => this.Connectivity.IsOnline && !string.IsNullOrWhiteSpace(this.CertificateThumbprint);

        /// <inheritdoc />
        public void Dispose()
        {
            this.Connectivity.StatusChanged -= this.OnStatusChanged;
        }

        /// <inheritdoc />
        protected override async Task OnInitializedAsync()
        {
            this.Connectivity.StatusChanged += this.OnStatusChanged;
            this.Connectivity.StartMonitoring(TimeSpan.FromSeconds(10));
            await this.Connectivity.CheckNowAsync();
        }

        private async Task ReconnectAsync()
        {
            await this.Connectivity.CheckNowAsync();
        }

        private void OnStatusChanged()
        {
            this.InvokeAsync(this.StateHasChanged);
        }
    }
}
