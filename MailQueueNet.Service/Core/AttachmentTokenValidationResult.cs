// <copyright file="AttachmentTokenValidationResult.cs" company="IBC Digital">
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
    using System.Collections.Generic;

    /// <summary>
    /// Represents the result of validating attachment tokens.
    /// </summary>
    internal sealed class AttachmentTokenValidationResult
    {
        /// <summary>
        /// Initialises a new instance of the <see cref="AttachmentTokenValidationResult"/> class.
        /// </summary>
        public AttachmentTokenValidationResult()
        {
            this.MissingTokens = new List<string>();
            this.NotReadyTokens = new List<string>();
        }

        /// <summary>
        /// Gets a value indicating whether all tokens were valid and ready.
        /// </summary>
        public bool Success => this.MissingTokens.Count == 0 && this.NotReadyTokens.Count == 0;

        /// <summary>
        /// Gets the list of tokens that were not found on disk.
        /// </summary>
        public IList<string> MissingTokens { get; }

        /// <summary>
        /// Gets the list of tokens that were found but not ready.
        /// </summary>
        public IList<string> NotReadyTokens { get; }

        /// <summary>
        /// Builds a concise message describing the failure.
        /// </summary>
        /// <returns>A single-line message describing invalid tokens.</returns>
        public string ToMessage()
        {
            if (this.Success)
            {
                return string.Empty;
            }

            var parts = new List<string>();

            if (this.MissingTokens.Count > 0)
            {
                parts.Add("Missing: " + string.Join(",", this.MissingTokens));
            }

            if (this.NotReadyTokens.Count > 0)
            {
                parts.Add("NotReady: " + string.Join(",", this.NotReadyTokens));
            }

            return string.Join("; ", parts);
        }
    }
}
