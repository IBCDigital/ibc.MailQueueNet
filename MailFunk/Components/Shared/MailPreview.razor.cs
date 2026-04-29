//-----------------------------------------------------------------------
// <copyright file="MailPreview.razor.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailFunk.Components.Shared
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using MailFunk.Models;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Components;
    using MudBlazor;

    public partial class MailPreview : ComponentBase
    {
        private bool busy;
        private string? failedFolder;
        private string? lastLoadedKey;
        private CancellationTokenSource? loadCts;

        /// <summary>
        /// Gets a value indicating whether the current preview is read-only.
        /// </summary>
        protected bool IsReadOnlyPreview => this.Entry?.FolderKind == MailFolderKind.MailMergeQueue;

        [Inject]
        public MailGrpcService.MailGrpcServiceClient GrpcClient { get; set; } = default!;

        [Inject]
        public IDialogService DialogService { get; set; } = default!;

        [Parameter]
        public MailFileEntry? Entry { get; set; }

        [Parameter]
        public EventCallback OnFileDeleted { get; set; }

        [Parameter]
        public EventCallback OnFileRetried { get; set; }

        protected MailQueueNet.Grpc.MailMessageWithSettings? Message { get; private set; }

        protected string? LoadError { get; private set; }

        protected string ServiceName => this.Message?.Settings?.Smtp is not null ? "SMTP" : this.Message?.Settings?.Mailgun is not null ? "Mailgun" : "Unknown";

        protected static string Addr(MailQueueNet.Grpc.MailAddress? a) => a is null ? string.Empty : string.IsNullOrWhiteSpace(a.DisplayName) ? a.Address ?? string.Empty : $"{a.DisplayName} <{a.Address}>";

        protected static string JoinAddrs(System.Collections.Generic.IList<MailQueueNet.Grpc.MailAddress>? list) => list is null || list.Count == 0 ? string.Empty : string.Join(", ", System.Linq.Enumerable.Select(list, a => Addr(a)));

        private bool CanRetry
        {
            get
            {
                if (this.Entry == null)
                {
                    return false;
                }

                if (this.Entry.FolderKind == MailFolderKind.Failed)
                {
                    return true;
                }

                if (this.Entry.FullPath == null || this.failedFolder == null)
                {
                    return false;
                }

                var entryNorm = NormalizePath(this.Entry.FullPath);
                var failedNorm = NormalizePath(this.failedFolder);
                if (failedNorm.Length == 0)
                {
                    return false;
                }

                return entryNorm.Equals(failedNorm, StringComparison.OrdinalIgnoreCase) ||
                       entryNorm.StartsWith(failedNorm + "/", StringComparison.OrdinalIgnoreCase);
            }
        }

        protected override async Task OnInitializedAsync()
        {
            try
            {
                var cfg = await this.GrpcClient.GetServiceConfigAsync(new GetServiceConfigRequest());
                this.failedFolder = cfg.FailedFolder;
            }
            catch
            {
            }
        }

        protected override Task OnParametersSetAsync()
        {
            var currentKey = this.GetEntryKey(this.Entry);

            if (string.IsNullOrWhiteSpace(currentKey))
            {
                this.lastLoadedKey = null;
                this.Message = null;
                this.LoadError = null;
                this.CancelLoad();
                return Task.CompletedTask;
            }

            if (string.Equals(this.lastLoadedKey, currentKey, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            this.lastLoadedKey = currentKey;
            this.Message = null;
            this.LoadError = null;

            return this.LoadMessageAsync(this.Entry);
        }

        private string GetEntryKey(MailFileEntry? entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            if (entry.FileRef != null && !string.IsNullOrWhiteSpace(entry.FileRef.Name))
            {
                return entry.FileRef.Folder.ToString() + ":" + entry.FileRef.Name;
            }

            if (!string.IsNullOrWhiteSpace(entry.Name) && entry.FolderKind != MailFolderKind.Unspecified)
            {
                return entry.FolderKind.ToString() + ":" + entry.Name;
            }

            return entry.FullPath ?? string.Empty;
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').TrimEnd('/');
        }

        private void CancelLoad()
        {
            try
            {
                this.loadCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                this.loadCts?.Dispose();
            }
            catch
            {
            }

            this.loadCts = null;
        }

        private async Task LoadMessageAsync(MailFileEntry? entry)
        {
            this.CancelLoad();
            this.loadCts = new CancellationTokenSource();
            var token = this.loadCts.Token;

            if (entry == null)
            {
                this.Message = null;
                return;
            }

            try
            {
                var request = new ReadMailFileRequest();

                // Prefer a name-only request so callers do not need to send raw filesystem paths and do not need
                // to echo the folder selection back to the server. The server will resolve the file name against
                // its configured mail folders.
                var effectiveName = entry.FileRef != null && !string.IsNullOrWhiteSpace(entry.FileRef.Name)
                    ? entry.FileRef.Name
                    : !string.IsNullOrWhiteSpace(entry.Name)
                        ? entry.Name
                        : Path.GetFileName(entry.FullPath ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(effectiveName))
                {
                    request.Name = effectiveName;
                }

                var reply = await this.GrpcClient.ReadMailFileAsync(request, cancellationToken: token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                this.Message = reply.Mail;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    this.LoadError = ex.Message;
                }
            }
        }

        private async void ShowDeleteConfirm()
        {
            if (this.Entry == null)
            {
                return;
            }

            var effectiveName = !string.IsNullOrWhiteSpace(this.Entry.Name)
                ? this.Entry.Name
                : Path.GetFileName(this.Entry.FullPath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(effectiveName))
            {
                return;
            }

            bool? confirm = await this.DialogService.ShowMessageBox(
                title: "Delete message file?",
                markupMessage: (MarkupString)"This will permanently remove the queued email file. This cannot be undone.",
                yesText: "Delete",
                cancelText: "Cancel",
                options: new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true });
            if (confirm == true)
            {
                await this.DeleteAsync();
            }
        }

        private async Task DeleteAsync()
        {
            if (this.Entry == null)
            {
                return;
            }

            var entry = this.Entry;
            var effectiveName = !string.IsNullOrWhiteSpace(entry.Name)
                ? entry.Name
                : Path.GetFileName(entry.FullPath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(effectiveName))
            {
                return;
            }

            this.busy = true;
            try
            {
                var req = new ModifyFilesRequest
                {
                };

                // Name-only ref avoids sending raw server paths and avoids requiring folder metadata.
                req.FileRefs.Add(new MailFileRef
                {
                    Name = effectiveName,
                });

                await this.GrpcClient.DeleteMailsAsync(req).ConfigureAwait(false);
                if (this.OnFileDeleted.HasDelegate)
                {
                    await this.OnFileDeleted.InvokeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.LoadError = ex.Message;
            }
            finally
            {
                this.busy = false;
                this.StateHasChanged();
            }
        }

        private async Task RetryAsync()
        {
            if (!this.CanRetry || this.Entry == null)
            {
                return;
            }

            var entry = this.Entry;
            var effectiveName = !string.IsNullOrWhiteSpace(entry.Name)
                ? entry.Name
                : Path.GetFileName(entry.FullPath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(effectiveName))
            {
                return;
            }

            this.busy = true;
            try
            {
                var req = new ModifyFilesRequest
                {
                };

                // Name-only ref avoids sending raw server paths and avoids requiring folder metadata.
                req.FileRefs.Add(new MailFileRef
                {
                    Name = effectiveName,
                });

                await this.GrpcClient.RetryFailedMailsAsync(req).ConfigureAwait(false);
                if (this.OnFileRetried.HasDelegate)
                {
                    await this.OnFileRetried.InvokeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                this.LoadError = ex.Message;
            }
            finally
            {
                this.busy = false;
                this.StateHasChanged();
            }
        }

        // Folder matching prefers the `MailFolderKind` value stamped onto the list entry. As a fallback (older
        // entries without a kind), we compare the full path value against the server's configured failed folder.
    }
}
