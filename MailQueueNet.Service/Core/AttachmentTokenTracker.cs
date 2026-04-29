//-----------------------------------------------------------------------
// <copyright file="AttachmentTokenTracker.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Tracks attachment tokens resolved for a mail during processing so they can be
    /// released (reference count decremented) on success or terminal failure.
    /// </summary>
    internal sealed class AttachmentTokenTracker
    {
        private readonly List<string> tokens = new List<string>();

        /// <summary>
        /// Adds a token to the tracker.
        /// </summary>
        /// <param name="token">Attachment token.</param>
        public void Add(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            this.tokens.Add(token);
        }

        /// <summary>
        /// Gets the tokens tracked for this item.
        /// </summary>
        /// <returns>Token array.</returns>
        public string[] GetTokens()
        {
            return this.tokens.ToArray();
        }
    }
}
