// <copyright file="MailFolderPanel.razor.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Components.Shared
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using MailFunk.Models;
    using MailQueueNet.Grpc;
    using Microsoft.AspNetCore.Components;

    public partial class MailFolderPanel : ComponentBase
    {
        private MailFileEntry? selected;
        private HashSet<string> selectedSet = new(StringComparer.OrdinalIgnoreCase);
        private bool selectAll;
        private bool bulkActionBusy;

        [Inject]
        public MailGrpcService.MailGrpcServiceClient GrpcClient { get; set; } = default!;

        [Parameter]
        public string Title { get; set; } = string.Empty;

        [Parameter]
        public string? FolderPath { get; set; }

        [Parameter]
        public MailFolderKind FolderKind { get; set; } = MailFolderKind.Unspecified;

        [Parameter]
        public int TotalCount { get; set; }

        [Parameter]
        public IList<MailFileEntry> Files { get; set; } = new List<MailFileEntry>();

        [Parameter]
        public EventCallback<MailFileEntry?> OnSelectedChanged { get; set; }

        [Parameter]
        public EventCallback OnLoadMore { get; set; }

        [Parameter]
        public bool EnableBulkActions { get; set; } = false;

        [Parameter]
        public bool AllowRetry { get; set; } = false;

        [Parameter]
        public EventCallback<IReadOnlyCollection<string>> OnBulkDelete { get; set; }

        [Parameter]
        public EventCallback<IReadOnlyCollection<string>> OnBulkRetry { get; set; }

        private bool AnySelected => this.selectedSet.Count > 0;

        private bool SelectAll
        {
            get => this.selectAll;
            set
            {
                this.selectAll = value;
                if (this.selectAll)
                {
                    this.SyncSelectAll();
                }
                else
                {
                    this.selectedSet.Clear();
                }
            }
        }

        protected override void OnParametersSet()
        {
            if (this.selectAll)
            {
                this.SyncSelectAll();
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.##} {units[unit]}";
        }

        private void SyncSelectAll()
        {
            if (this.Files == null)
            {
                return;
            }

            this.selectedSet = new HashSet<string>(this.Files.Select(f => this.GetSelectionKey(f)), StringComparer.OrdinalIgnoreCase);
        }

        private void ToggleSelection(MailFileEntry entry, bool isChecked)
        {
            var key = this.GetSelectionKey(entry);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (isChecked)
            {
                this.selectedSet.Add(key);
            }
            else
            {
                this.selectedSet.Remove(key);
                this.selectAll = false;
            }
        }

        private async Task DeleteSelectedAsync()
        {
            if (this.bulkActionBusy || this.selectedSet.Count == 0)
            {
                return;
            }

            this.bulkActionBusy = true;

            try
            {
                var selectedNames = this.selectedSet.ToList();
                var req = new ModifyFilesRequest();

                foreach (var name in selectedNames)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var entry = this.Files?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    var fileRef = entry?.FileRef;

                    // Prefer the server-provided reference, but do not require callers to echo the folder selection
                    // back to the server. A name-only ref is sufficient when file names are unique.
                    req.FileRefs.Add(new MailFileRef
                    {
                        Name = fileRef?.Name ?? name,
                    });
                }

                await this.GrpcClient.DeleteMailsAsync(req).ConfigureAwait(false);

                if (this.OnBulkDelete.HasDelegate)
                {
                    await this.OnBulkDelete.InvokeAsync(selectedNames.AsReadOnly()).ConfigureAwait(false);
                }

                this.selectedSet.Clear();
                this.selectAll = false;
            }
            catch
            {
            }
            finally
            {
                this.bulkActionBusy = false;
            }

            this.StateHasChanged();
        }

        private async Task RetrySelectedAsync()
        {
            if (this.bulkActionBusy || this.selectedSet.Count == 0)
            {
                return;
            }

            this.bulkActionBusy = true;

            try
            {
                var selectedNames = this.selectedSet.ToList();
                var req = new ModifyFilesRequest();

                foreach (var name in selectedNames)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var entry = this.Files?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    var fileRef = entry?.FileRef;

                    // Prefer the server-provided reference, but do not require callers to echo the folder selection
                    // back to the server. A name-only ref is sufficient when file names are unique.
                    req.FileRefs.Add(new MailFileRef
                    {
                        Name = fileRef?.Name ?? name,
                    });
                }

                await this.GrpcClient.RetryFailedMailsAsync(req).ConfigureAwait(false);

                if (this.OnBulkRetry.HasDelegate)
                {
                    await this.OnBulkRetry.InvokeAsync(selectedNames.AsReadOnly()).ConfigureAwait(false);
                }

                this.selectedSet.Clear();
                this.selectAll = false;
            }
            catch
            {
            }
            finally
            {
                this.bulkActionBusy = false;
            }

            this.StateHasChanged();
        }

        private async Task OnSelectionChangedAsync(MailFileEntry? entry)
        {
            this.selected = entry;
            if (this.OnSelectedChanged.HasDelegate)
            {
                await this.OnSelectedChanged.InvokeAsync(entry);
            }
        }

        private string GetSelectionKey(MailFileEntry? entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            // Prefer names so bulk operations can build `MailFileRef` values without passing
            // raw filesystem paths.
            if (!string.IsNullOrWhiteSpace(entry.Name))
            {
                return entry.Name;
            }

            return string.Empty;
        }

        // Bulk actions prefer `MailFileRef` to avoid passing raw server paths back to the queue service.
    }
}
