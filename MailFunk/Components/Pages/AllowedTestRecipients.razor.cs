//-----------------------------------------------------------------------
// <copyright file="AllowedTestRecipients.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using MailFunk.Services;
    using Microsoft.AspNetCore.Components;

    /// <summary>
    /// Displays and manages the authenticated client's staging allow-listed recipients.
    /// </summary>
    public sealed partial class AllowedTestRecipients
    {
        private readonly List<string> items = new();
        private bool busy;
        private bool isStaging;
        private string error = string.Empty;
        private string status = string.Empty;
        private string clientId = string.Empty;
        private string newEmailAddress = string.Empty;

        [Inject]
        private AllowedTestRecipientsService Service { get; set; } = default!;

        /// <inheritdoc />
        protected override async Task OnInitializedAsync()
        {
            this.isStaging = this.Service.IsStagingEnabled;
            if (!this.isStaging)
            {
                return;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task RefreshAsync()
        {
            if (!this.isStaging)
            {
                return;
            }

            this.busy = true;
            this.error = string.Empty;
            this.status = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(this.clientId))
                {
                    this.error = "Client ID is required.";
                    this.items.Clear();
                    return;
                }

                var values = await this.Service.ListAsync(this.clientId, CancellationToken.None).ConfigureAwait(false);
                this.items.Clear();
                this.items.AddRange(values);
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
            finally
            {
                this.busy = false;
            }
        }

        private async Task AddAsync()
        {
            if (this.busy || string.IsNullOrWhiteSpace(this.newEmailAddress))
            {
                return;
            }

            this.busy = true;
            this.error = string.Empty;
            this.status = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(this.clientId))
                {
                    this.error = "Client ID is required.";
                    return;
                }

                var reply = await this.Service.AddAsync(this.clientId, this.newEmailAddress, CancellationToken.None).ConfigureAwait(false);
                if (!reply.Success)
                {
                    this.error = string.IsNullOrWhiteSpace(reply.Message) ? "Recipient was not added." : reply.Message;
                    return;
                }

                this.newEmailAddress = string.Empty;
                this.status = "Recipient added.";
                await this.RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
            finally
            {
                this.busy = false;
            }
        }

        private async Task DeleteAsync(string emailAddress)
        {
            if (this.busy || string.IsNullOrWhiteSpace(emailAddress))
            {
                return;
            }

            this.busy = true;
            this.error = string.Empty;
            this.status = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(this.clientId))
                {
                    this.error = "Client ID is required.";
                    return;
                }

                var reply = await this.Service.DeleteAsync(this.clientId, emailAddress, CancellationToken.None).ConfigureAwait(false);
                if (!reply.Success)
                {
                    this.error = string.IsNullOrWhiteSpace(reply.Message) ? "Recipient was not removed." : reply.Message;
                    return;
                }

                this.status = "Recipient removed.";
                await this.RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
            finally
            {
                this.busy = false;
            }
        }
    }
}
