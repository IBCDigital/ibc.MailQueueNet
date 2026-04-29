//-----------------------------------------------------------------------
// <copyright file="NonceStore.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailForge.Security
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// Simple in-memory nonce store with time window cleanup.
    /// </summary>
    public sealed class NonceStore
    {
        private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, DateTimeOffset> nonces = new();

        /// <summary>
        /// Attempts to register a nonce for the specified timestamp.
        /// </summary>
        /// <param name="nonce">The nonce to register.</param>
        /// <param name="timestamp">The timestamp associated with the request.</param>
        /// <returns>True if the nonce was registered; otherwise false.</returns>
        public bool TryRegister(string nonce, DateTimeOffset timestamp)
        {
            this.Cleanup();
            if (this.nonces.ContainsKey(nonce))
            {
                return false;
            }

            return this.nonces.TryAdd(nonce, timestamp);
        }

        private void Cleanup()
        {
            var threshold = DateTimeOffset.UtcNow - Expiry;
            foreach (var kvp in this.nonces)
            {
                if (kvp.Value < threshold)
                {
                    this.nonces.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
