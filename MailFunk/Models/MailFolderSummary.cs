// <copyright file="MailFolderSummary.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Models
{
    /// <summary>
    /// Represents a snapshot of queue and failed mail folder states.
    /// </summary>
    /// <param name="QueueCount">Number of files in the queue folder.</param>
    /// <param name="FailedCount">Number of files in the failed folder.</param>
    /// <param name="QueuePath">Server path for the queue folder (display only).</param>
    /// <param name="FailedPath">Server path for the failed folder (display only).</param>
    /// <param name="IsRemoteAccessible">True when the remote queue service and folders were reachable.</param>
    /// <param name="RemoteStatusMessage">Status message suitable for displaying to administrators.</param>
    public sealed record MailFolderSummary(
        int QueueCount,
        int FailedCount,
        string QueuePath,
        string FailedPath,
        bool IsRemoteAccessible,
        string RemoteStatusMessage);
}
