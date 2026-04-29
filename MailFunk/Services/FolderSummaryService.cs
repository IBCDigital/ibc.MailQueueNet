// <copyright file="FolderSummaryService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using MailFunk.Models;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Default implementation of <see cref="IFolderSummaryService"/>.
    /// </summary>
    public class FolderSummaryService : IFolderSummaryService
    {
        private const int DefaultListTake = 50;
        private const int MaxListTake = 500;

        private readonly MailGrpcService.MailGrpcServiceClient grpcClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="FolderSummaryService"/> class.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="grpcClient">The gRPC client to the Mail service.</param>
        public FolderSummaryService(MailGrpcService.MailGrpcServiceClient grpcClient)
        {
            this.grpcClient = grpcClient;
        }

        /// <inheritdoc />
        public MailFolderSummary GetSummary()
        {
            try
            {
                var reply = this.grpcClient.GetFolderSummary(new GetFolderSummaryRequest());
                return new MailFolderSummary(
                    reply.QueueCount,
                    reply.FailedCount,
                    reply.QueueFolder,
                    reply.FailedFolder,
                    IsRemoteAccessible: true,
                    RemoteStatusMessage: string.Empty);
            }
            catch (Exception ex)
            {
                var message = string.IsNullOrWhiteSpace(ex.Message)
                    ? "Remote queue service is unreachable. Remote mail folders are not accessible."
                    : ex.Message;

                return new MailFolderSummary(
                    QueueCount: 0,
                    FailedCount: 0,
                    QueuePath: "Remote folder not accessible.",
                    FailedPath: "Remote folder not accessible.",
                    IsRemoteAccessible: false,
                    RemoteStatusMessage: message);
            }
        }

        /// <inheritdoc />
        public IEnumerable<MailFileEntry> ListFiles(MailFolderKind folderKind, int skip, int take)
        {
            take = take <= 0 ? DefaultListTake : take;
            if (take > MaxListTake)
            {
                take = MaxListTake;
            }

            if (folderKind == MailFolderKind.Unspecified)
            {
                return Array.Empty<MailFileEntry>();
            }

            try
            {
                var reply = this.grpcClient.ListMailFiles(new ListMailFilesRequest
                {
                    Folder = folderKind,
                    Skip = Math.Max(0, skip),
                    Take = take,
                });

                var serverFolder = reply.Folder ?? string.Empty;
                return reply.Files.Select(f => new MailFileEntry(
                    folderKind,
                    f.Name,
                    // Prefer the server-provided full path for display/diagnostics. This value is not
                    // used for subsequent operations (those should use FileRef).
                    !string.IsNullOrWhiteSpace(f.FullPath)
                        ? f.FullPath
                        : JoinServerPath(serverFolder, f.Name),
                    f.Size,
                    ParseUtc(f.CreatedUtc),
                    ParseUtc(f.ModifiedUtc),
                    string.IsNullOrWhiteSpace(f.ClientId) ? null : f.ClientId,
                    f.AttemptCount,
                    EnsureFileRef(f.FileRef, folderKind, f.Name)));
            }
            catch
            {
                return Array.Empty<MailFileEntry>();
            }
        }

        /// <summary>
        /// Parses an ISO-8601 UTC timestamp, returning <see cref="DateTime.MinValue"/> when parsing fails.
        /// </summary>
        /// <param name="raw">The raw timestamp string.</param>
        /// <returns>The parsed UTC time, or <see cref="DateTime.MinValue"/>.</returns>
        private static DateTime ParseUtc(string raw)
        {
            if (DateTime.TryParse(raw, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }

            return DateTime.MinValue;
        }

        /// <summary>
        /// Combines a server folder path and a file name using the server folder's path separator.
        /// </summary>
        /// <param name="folder">The folder path as returned by the server.</param>
        /// <param name="name">The file name (no directory separators).</param>
        /// <returns>The combined path for display purposes.</returns>
        private static string JoinServerPath(string folder, string name)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return name ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return folder;
            }

            // Prefer the separator already used by the server path.
            var separator = folder.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
            var trimmed = folder.TrimEnd('\\', '/');
            return trimmed + separator + name;
        }

        /// <summary>
        /// Ensures a usable file reference exists for subsequent read/delete/retry calls.
        /// </summary>
        /// <param name="fileRef">The reference provided by the server (if any).</param>
        /// <param name="folderKind">The folder kind used for the list request.</param>
        /// <param name="name">The file name.</param>
        /// <returns>A non-null <see cref="MailFileRef"/>.</returns>
        private static MailFileRef EnsureFileRef(MailFileRef? fileRef, MailFolderKind folderKind, string name)
        {
            if (fileRef != null && !string.IsNullOrWhiteSpace(fileRef.Name))
            {
                return fileRef;
            }

            return new MailFileRef
            {
                Folder = folderKind,
                Name = name ?? string.Empty,
            };
        }

    }
}
