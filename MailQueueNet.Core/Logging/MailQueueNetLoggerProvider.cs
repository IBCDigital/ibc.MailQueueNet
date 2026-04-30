// <copyright file="MailQueueNetLoggerProvider.cs" company="IBC Digital">
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
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Provides Microsoft logging adapters that write to the MailQueueNet IBC logger.
    /// </summary>
    public sealed class MailQueueNetLoggerProvider : ILoggerProvider
    {
        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return new MailQueueNetMicrosoftLogger(categoryName);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            MailQueueNetLogger.SaveLogFiles(true);
        }
    }
}
