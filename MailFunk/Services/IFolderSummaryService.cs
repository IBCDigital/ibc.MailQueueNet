// <copyright file="IFolderSummaryService.cs" company="IBC Digital">
// Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Services
{
    using System.Collections.Generic;
    using MailFunk.Models;
    using MailQueueNet.Grpc;

    /// <summary>
    /// Provides a summary of the mail queue and failed folders.
    /// </summary>
    public interface IFolderSummaryService
    {
        /// <summary>
        /// Gets a snapshot of the counts and paths for the queue and failed folders.
        /// </summary>
        /// <returns>A populated summary.</returns>
        MailFolderSummary GetSummary();

        /// <summary>
        /// Lists mail file entries from a folder.
        /// </summary>
        /// <param name="folderKind">Folder kind to enumerate.</param>
        /// <param name="skip">Number of items to skip.</param>
        /// <param name="take">Max items to return.</param>
        /// <returns>Enumerated list of files.</returns>
        IEnumerable<MailFileEntry> ListFiles(MailFolderKind folderKind, int skip, int take);
    }
}
