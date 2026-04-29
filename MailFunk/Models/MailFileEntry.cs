// <copyright file="MailFileEntry.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Models
{
    using System;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Represents a single mail file entry in a queue or failed folder.
    /// </summary>
    /// <param name="FolderKind">The configured folder kind that contains this file.</param>
    /// <param name="Name">The file name (no directory separators).</param>
    /// <param name="FullPath">The full file path on the server (display/diagnostics only).</param>
    /// <param name="SizeBytes">The size of the file in bytes.</param>
    /// <param name="CreatedUtc">The file creation time (UTC).</param>
    /// <param name="ModifiedUtc">The file last modified time (UTC).</param>
    /// <param name="ClientId">The client identifier stamped into the queued mail file (if present).</param>
    /// <param name="AttemptCount">The attempt count stamped into the queued mail file (if present).</param>
    /// <param name="FileRef">The preferred server-side reference for this file, used for subsequent read/delete/retry calls.</param>
    public sealed record MailFileEntry(
        MailFolderKind FolderKind,
        string Name,
        string FullPath,
        long SizeBytes,
        DateTime CreatedUtc,
        DateTime ModifiedUtc,
        string? ClientId = null,
        int AttemptCount = 0,
        MailFileRef? FileRef = null);
}
