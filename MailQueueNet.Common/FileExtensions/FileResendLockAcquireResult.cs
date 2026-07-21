// <copyright file="FileResendLockAcquireResult.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailQueueNet.Common.FileExtensions
{
    /// <summary>
    /// Represents the outcome of attempting to acquire the shared resend lock.
    /// </summary>
    internal sealed class FileResendLockAcquireResult
    {
        private FileResendLockAcquireResult(bool acquired, string lockPath, string state, string? failureReason, FileResendLockMetadata? metadata)
        {
            this.Acquired = acquired;
            this.LockPath = lockPath;
            this.State = state;
            this.FailureReason = failureReason;
            this.Metadata = metadata;
        }

        public bool Acquired { get; }

        public string LockPath { get; }

        public string State { get; }

        public string? FailureReason { get; }

        public FileResendLockMetadata? Metadata { get; }

        public static FileResendLockAcquireResult Succeeded(string lockPath, FileResendLockMetadata metadata)
        {
            return new FileResendLockAcquireResult(true, lockPath, "acquired", null, metadata);
        }

        public static FileResendLockAcquireResult Failed(string lockPath, string state, string failureReason, FileResendLockMetadata? metadata = null)
        {
            return new FileResendLockAcquireResult(false, lockPath, state, failureReason, metadata);
        }
    }
}
