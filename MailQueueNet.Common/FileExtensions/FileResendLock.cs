// <copyright file="FileResendLock.cs" company="IBC Digital">
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
// <license>
// MIT Licence – see the repository root LICENCE file for full text.
// </license>

namespace MailQueueNet.Common.FileExtensions
{
    using System;
    using System.IO;
    using System.Text;

    internal sealed class FileResendLock : IDisposable
    {
        private readonly string path;
        private readonly TimeSpan timeout;
        private readonly string owner;
        private FileStream? handle;
        private FileResendLockMetadata? currentMetadata;

        public FileResendLock(string folder, string fileName)
            : this(folder, fileName, TimeSpan.FromSeconds(300), $"{Environment.MachineName}:{Environment.ProcessId}")
        {
        }

        public FileResendLock(string folder, string fileName, TimeSpan timeout, string owner)
        {
            this.path = Path.Combine(folder, fileName);
            this.timeout = timeout;
            this.owner = NormalizeOwner(owner);
        }

        private static string NormalizeOwner(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                return $"{Environment.MachineName}:{Environment.ProcessId}";
            }

            return owner.Replace('\r', '_').Replace('\n', '_').Replace('=', '-');
        }

        // non-blocking
        public FileResendLockAcquireResult TryAcquire()
        {
            try
            {
                this.handle = new FileStream(
                    this.path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);      // exclusive
                var metadata = FileResendLockMetadata.Create(this.owner, this.timeout);
                this.WriteMetadata(metadata);
                this.currentMetadata = metadata;
                return FileResendLockAcquireResult.Succeeded(this.path, metadata);
            }
            catch (IOException ex)
            {
                var metadata = FileResendLockMetadata.TryRead(this.path);
                string state = this.ClassifyFailedAcquisition(metadata);
                string failureReason = state == "stale"
                    ? "Stale lock metadata detected, but exclusive access could not be acquired safely. " + ex.Message
                    : ex.Message;

                return FileResendLockAcquireResult.Failed(this.path, state, failureReason, metadata);
            }
        }

        private string ClassifyFailedAcquisition(FileResendLockMetadata? metadata)
        {
            var now = DateTimeOffset.UtcNow;
            if (metadata != null && metadata.ExpiresUtc != default)
            {
                return metadata.ExpiresUtc <= now ? "stale" : "active";
            }

            try
            {
                if (File.Exists(this.path))
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(this.path);
                    if (lastWriteUtc != default && new DateTimeOffset(lastWriteUtc, TimeSpan.Zero).Add(this.timeout) <= now)
                    {
                        return "stale";
                    }
                }
            }
            catch
            {
            }

            return "unknown";
        }

        private void WriteMetadata(FileResendLockMetadata metadata)
        {
            if (this.handle == null)
            {
                throw new IOException("The resend lock is not held.");
            }

            byte[] content = Encoding.UTF8.GetBytes(metadata.ToFileText());
            this.handle.SetLength(0);
            this.handle.Position = 0;
            this.handle.Write(content, 0, content.Length);
            this.handle.Flush(true);
        }

        public bool StillHeld()
        {
            try
            {
                if (this.handle == null || this.currentMetadata == null)
                {
                    return false;
                }

                this.currentMetadata.ExpiresUtc = DateTimeOffset.UtcNow.Add(this.timeout);
                this.currentMetadata.State = "active";
                this.WriteMetadata(this.currentMetadata);
                return true;
            }

            // lost network share etc.
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                if (this.handle != null && this.currentMetadata != null)
                {
                    this.currentMetadata.State = "released";
                    this.currentMetadata.ExpiresUtc = DateTimeOffset.UtcNow;
                    this.currentMetadata.ReleasedUtc = DateTimeOffset.UtcNow;
                    this.WriteMetadata(this.currentMetadata);
                }
            }
            catch
            {
            }
            finally
            {
                this.handle?.Dispose();
                this.handle = null;
                this.currentMetadata = null;
            }
        }
    }
}
