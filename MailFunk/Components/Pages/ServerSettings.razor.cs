//-----------------------------------------------------------------------
// <copyright file="ServerSettings.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Pages
{
    using System;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Grpc.Core;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Components;
    using Microsoft.JSInterop;

    /// <summary>
    /// Component for viewing and editing MailQueue server settings. All writes are applied immediately via gRPC.
    /// </summary>
    public partial class ServerSettings : ComponentBase
    {
        private Settings? settings;
        private MailSettings? mailSettings;
        private bool busy;
        private string? error;
        private bool editMode;
        private Settings editSettings = new();
        private string mailType = "smtp";
        private SmtpMailSettings editSmtp = new();
        private MailgunMailSettings editMailgun = new();

        /// <summary>
        /// Gets or sets the generated gRPC client used to communicate with the MailQueue server.
        /// </summary>
        [Inject]
        public MailGrpcService.MailGrpcServiceClient GrpcClient { get; set; } = default!;

        /// <summary>
        /// Gets or sets the JavaScript runtime used for clipboard operations.
        /// </summary>
        [Inject]
        public IJSRuntime JsRuntime { get; set; } = default!;

        /// <inheritdoc />
        protected override async Task OnInitializedAsync()
        {
            await this.RefreshAsync();
        }

        /// <summary>
        /// Refreshes settings from the server. Requires admin authorisation.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the asynchronous refresh.</returns>
        private async Task RefreshAsync()
        {
            this.busy = true;
            this.error = null;
            try
            {
                var s = await this.GrpcClient.GetSettingsAsync(new GetSettingsMessage());
                this.settings = s.Settings ?? new Settings();

                var ms = await this.GrpcClient.GetMailSettingsAsync(new GetMailSettingsMessage());
                this.mailSettings = ms.Settings ?? new MailSettings();
                this.editMode = false;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                this.error = "Permission denied. Admin certificate is required to view server settings.";
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
            finally
            {
                this.busy = false;
            }

            await this.InvokeAsync(this.StateHasChanged);
        }

        /// <summary>
        /// Enters edit mode and initialises the editable models from the currently loaded settings.
        /// </summary>
        private void BeginEdit()
        {
            if (this.settings is null)
            {
                return;
            }

            this.editMode = true;
            this.editSettings = new Settings
            {
                QueueFolder = this.settings.QueueFolder,
                FailedFolder = this.settings.FailedFolder,
                SecondsUntilFolderRefresh = this.settings.SecondsUntilFolderRefresh,
                MaximumConcurrentWorkers = this.settings.MaximumConcurrentWorkers,
                MaximumFailureRetries = this.settings.MaximumFailureRetries,
                MaximumPauseMinutes = this.settings.MaximumPauseMinutes,
            };

            if (this.mailSettings?.Smtp is not null)
            {
                this.mailType = "smtp";
                this.editSmtp = new SmtpMailSettings
                {
                    Host = this.mailSettings.Smtp.Host,
                    Port = this.mailSettings.Smtp.Port,
                    RequiresSsl = this.mailSettings.Smtp.RequiresSsl,
                    RequiresAuthentication = this.mailSettings.Smtp.RequiresAuthentication,
                    Username = this.mailSettings.Smtp.Username,
                    Password = this.mailSettings.Smtp.Password,
                    ConnectionTimeout = this.mailSettings.Smtp.ConnectionTimeout,
                };
            }
            else if (this.mailSettings?.Mailgun is not null)
            {
                this.mailType = "mailgun";
                this.editMailgun = new MailgunMailSettings
                {
                    Domain = this.mailSettings.Mailgun.Domain,
                    ApiKey = this.mailSettings.Mailgun.ApiKey,
                    ConnectionTimeout = this.mailSettings.Mailgun.ConnectionTimeout,
                };
            }
            else
            {
                this.mailType = "smtp";
                this.editSmtp = new SmtpMailSettings();
                this.editMailgun = new MailgunMailSettings();
            }
        }

        /// <summary>
        /// Cancels edit mode and discards pending changes.
        /// </summary>
        private void CancelEdit()
        {
            this.editMode = false;
        }

        /// <summary>
        /// Saves the edited settings to the server. Applies immediately.
        /// </summary>
        /// <returns>A <see cref="Task"/> that completes when the save operation finishes.</returns>
        private async Task SaveAsync()
        {
            if (this.settings is null)
            {
                return;
            }

            this.busy = true;
            this.error = null;
            try
            {
                await this.GrpcClient.SetSettingsAdminAsync(new SetSettingsMessage { Settings = this.editSettings });

                if (this.mailType == "smtp")
                {
                    await this.GrpcClient.SetMailSettingsAdminAsync(new SetMailSettingsMessage { Settings = new MailSettings { Smtp = this.editSmtp } });
                }
                else if (this.mailType == "mailgun")
                {
                    await this.GrpcClient.SetMailSettingsAdminAsync(new SetMailSettingsMessage { Settings = new MailSettings { Mailgun = this.editMailgun } });
                }

                await this.RefreshAsync();
            }
            catch (RpcException ex)
            {
                this.error = $"Save failed: {ex.StatusCode} {ex.Status.Detail}";
            }
            catch (Exception ex)
            {
                this.error = ex.Message;
            }
            finally
            {
                this.busy = false;
            }

            await this.InvokeAsync(this.StateHasChanged);
        }

        /// <summary>
        /// Copies the current settings snapshot as indented JSON to the system clipboard.
        /// </summary>
        /// <returns>A <see cref="Task"/> for the clipboard operation.</returns>
        private async Task CopyAsJsonAsync()
        {
            try
            {
                var payload = new { Settings = this.settings, MailSettings = this.mailSettings };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                await this.JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", json);
            }
            catch
            {
                // Swallow – clipboard may be blocked by the environment.
            }
        }
    }
}
