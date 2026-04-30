// <copyright file="NullLoggerScope.cs" company="IBC Digital">
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

namespace MailQueueNet.Core.Logging
{
    using System;

    /// <summary>
    /// Represents a no-op logging scope used when forwarding Microsoft logger messages to IBC logging.
    /// </summary>
    internal sealed class NullLoggerScope : IDisposable
    {
        private NullLoggerScope()
        {
        }

        /// <summary>
        /// Gets the shared no-op scope instance.
        /// </summary>
        public static NullLoggerScope Instance { get; } = new NullLoggerScope();

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
