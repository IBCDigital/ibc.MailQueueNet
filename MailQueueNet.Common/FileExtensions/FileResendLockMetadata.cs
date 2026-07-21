// <copyright file="FileResendLockMetadata.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailQueueNet.Common.FileExtensions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;

    /// <summary>
    /// Safe diagnostic metadata stored in the shared resend lock file.
    /// </summary>
    internal sealed class FileResendLockMetadata
    {
        private const string PackageName = "MailQueueNet";

        public string Owner { get; set; } = string.Empty;

        public int ProcessId { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public DateTimeOffset ExpiresUtc { get; set; }

        public string Package { get; set; } = PackageName;

        public string? State { get; set; }

        public DateTimeOffset? ReleasedUtc { get; set; }

        public static FileResendLockMetadata Create(string owner, TimeSpan timeout)
        {
            var now = DateTimeOffset.UtcNow;
            return new FileResendLockMetadata
            {
                Owner = owner,
                ProcessId = Environment.ProcessId,
                CreatedUtc = now,
                ExpiresUtc = now.Add(timeout),
                Package = PackageName,
                State = "active",
            };
        }

        public string ToFileText()
        {
            var builder = new StringBuilder();
            builder.Append("owner=").AppendLine(this.Owner ?? string.Empty);
            builder.Append("processId=").AppendLine(this.ProcessId.ToString(CultureInfo.InvariantCulture));
            builder.Append("createdUtc=").AppendLine(this.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            builder.Append("expiresUtc=").AppendLine(this.ExpiresUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            builder.Append("package=").AppendLine(this.Package ?? PackageName);

            if (!string.IsNullOrWhiteSpace(this.State))
            {
                builder.Append("state=").AppendLine(this.State);
            }

            if (this.ReleasedUtc.HasValue)
            {
                builder.Append("releasedUtc=").AppendLine(this.ReleasedUtc.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        public static FileResendLockMetadata? TryRead(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(path))
                {
                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    values[line.Substring(0, separatorIndex)] = line.Substring(separatorIndex + 1);
                }

                var metadata = new FileResendLockMetadata();
                if (values.TryGetValue("owner", out var owner))
                {
                    metadata.Owner = owner;
                }

                if (values.TryGetValue("processId", out var processId) && int.TryParse(processId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedProcessId))
                {
                    metadata.ProcessId = parsedProcessId;
                }

                if (values.TryGetValue("createdUtc", out var createdUtc) && DateTimeOffset.TryParse(createdUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedCreatedUtc))
                {
                    metadata.CreatedUtc = parsedCreatedUtc;
                }

                if (values.TryGetValue("expiresUtc", out var expiresUtc) && DateTimeOffset.TryParse(expiresUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedExpiresUtc))
                {
                    metadata.ExpiresUtc = parsedExpiresUtc;
                }

                if (values.TryGetValue("package", out var package))
                {
                    metadata.Package = package;
                }

                if (values.TryGetValue("state", out var state))
                {
                    metadata.State = state;
                }

                if (values.TryGetValue("releasedUtc", out var releasedUtc) && DateTimeOffset.TryParse(releasedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedReleasedUtc))
                {
                    metadata.ReleasedUtc = parsedReleasedUtc;
                }

                return metadata;
            }
            catch
            {
                return null;
            }
        }
    }
}
