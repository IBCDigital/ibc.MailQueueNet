// <copyright file="AttachmentQueryCursor.cs" company="IBC Digital">
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
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Represents a cursor used for stable, cursor-based paging for attachment queries.
    /// </summary>
    internal sealed class AttachmentQueryCursor
    {
        /// <summary>
        /// Gets or sets the last uploaded UTC timestamp (ISO 8601) used for paging.
        /// </summary>
        public DateTimeOffset UploadedUtc { get; set; }

        /// <summary>
        /// Gets or sets the last token used for paging.
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// Parses a cursor token produced by <see cref="ToPageToken"/>.
        /// </summary>
        /// <param name="pageToken">The page token to parse.</param>
        /// <returns>The parsed cursor.</returns>
        public static AttachmentQueryCursor? TryParse(string? pageToken)
        {
            if (string.IsNullOrWhiteSpace(pageToken))
            {
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(pageToken);
                var raw = Encoding.UTF8.GetString(bytes);
                var parts = raw.Split('|');
                if (parts.Length != 2)
                {
                    return null;
                }

                if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var uploadedUtc))
                {
                    return null;
                }

                var token = parts[1] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }

                return new AttachmentQueryCursor
                {
                    UploadedUtc = uploadedUtc,
                    Token = token,
                };
            }
            catch
            {
                return null;
            }
        }

        public static ExtendedCursor? TryParseExtended(string? extendedPageToken)
        {
            if (string.IsNullOrWhiteSpace(extendedPageToken))
            {
                return null;
            }

            try
            {
                var bytes = Convert.FromBase64String(extendedPageToken);
                var raw = Encoding.UTF8.GetString(bytes);
                var parts = raw.Split('|');
                if (parts.Length != 3)
                {
                    return null;
                }

                var key = parts[0] ?? string.Empty;
                if (!DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var uploadedUtc))
                {
                    return null;
                }

                var token = parts[2] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(token))
                {
                    return null;
                }

                var cur = new ExtendedCursor
                {
                    UploadedUtc = uploadedUtc,
                    Token = token,
                };

                if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var len))
                {
                    cur.Length = len;
                }

                if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rc))
                {
                    cur.RefCount = rc;
                }

                return cur;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Converts this cursor to an opaque token suitable for returning to clients.
        /// </summary>
        /// <returns>A base64 encoded page token.</returns>
        public string ToPageToken()
        {
            var raw = this.UploadedUtc.ToString("o", CultureInfo.InvariantCulture) + "|" + (this.Token ?? string.Empty);
            var bytes = Encoding.UTF8.GetBytes(raw);
            return Convert.ToBase64String(bytes);
        }

        internal sealed class ExtendedCursor
        {
            public long Length { get; set; }

            public int RefCount { get; set; }

            public DateTimeOffset UploadedUtc { get; set; }

            public string Token { get; set; } = string.Empty;
        }
    }
}
