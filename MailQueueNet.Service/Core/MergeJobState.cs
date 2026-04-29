// <copyright file="MergeJobState.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
//
//  Derived from “MailQueueNet” by Daniel Cohen Gindi
//  (https://github.com/danielgindi/MailQueueNet).
//
//  Original portions:
//    © 2014 Daniel Cohen Gindi (danielgindi@gmail.com)
//    Licensed under the MIT Licence.
//  Modifications and additions:
//    © 2025 IBC Digital Pty Ltd
//    Distributed under the same MIT Licence.
//
//  The above notice and this permission notice shall be included in
//  all copies or substantial portions of this file.
// </copyright>

namespace MailQueueNet.Service.Core
{
    using System;

    /// <summary>
    /// Represents persisted merge job state stored in the dispatcher database.
    /// </summary>
    public sealed record MergeJobState
    {
        /// <summary>
        /// Initialises a new instance of the <see cref="MergeJobState"/> class.
        /// </summary>
        /// <param name="mergeId">Merge identifier.</param>
        /// <param name="templatePath">Template file path.</param>
        /// <param name="status">Persisted status.</param>
        /// <param name="workerAddress">Last worker address used.</param>
        /// <param name="fenceToken">Fence token used.</param>
        /// <param name="message">Optional detail message.</param>
        /// <param name="updatedUtc">Last update time (UTC).</param>
        /// <param name="completedUtc">Completion time (UTC) when status is Completed.</param>
        public MergeJobState(
            string mergeId,
            string templatePath,
            SqliteDispatcherStateStore.MergeJobDispatchStatus status,
            string workerAddress,
            long fenceToken,
            string message,
            DateTimeOffset updatedUtc,
            DateTimeOffset? completedUtc)
        {
            this.MergeId = mergeId;
            this.TemplatePath = templatePath;
            this.Status = status;
            this.WorkerAddress = workerAddress;
            this.FenceToken = fenceToken;
            this.Message = message;
            this.UpdatedUtc = updatedUtc;
            this.CompletedUtc = completedUtc;
        }

        /// <summary>
        /// Gets the merge identifier.
        /// </summary>
        public string MergeId { get; }

        /// <summary>
        /// Gets the template file path.
        /// </summary>
        public string TemplatePath { get; }

        /// <summary>
        /// Gets the persisted dispatch status.
        /// </summary>
        public SqliteDispatcherStateStore.MergeJobDispatchStatus Status { get; }

        /// <summary>
        /// Gets the last worker address used.
        /// </summary>
        public string WorkerAddress { get; }

        /// <summary>
        /// Gets the fence token used.
        /// </summary>
        public long FenceToken { get; }

        /// <summary>
        /// Gets an optional detail message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets the last update time in UTC.
        /// </summary>
        public DateTimeOffset UpdatedUtc { get; }

        /// <summary>
        /// Gets the completion time in UTC when the status is Completed.
        /// </summary>
        public DateTimeOffset? CompletedUtc { get; }
    }
}
