// <copyright file="AuditNonceStore.cs" company="IBC Digital">
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

namespace MailQueueNet.Service.Security
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// Simple in-memory nonce store with time window cleanup.
    /// </summary>
    internal sealed class AuditNonceStore
    {
        private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);
        private readonly ConcurrentDictionary<string, DateTimeOffset> nonces = new();

        public bool TryRegister(string nonce, DateTimeOffset ts)
        {
            this.Cleanup();
            if (this.nonces.ContainsKey(nonce))
            {
                return false;
            }

            if (!this.nonces.TryAdd(nonce, ts))
            {
                return false;
            }

            return true;
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
