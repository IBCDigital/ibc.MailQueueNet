// <copyright file="ISender.cs" company="IBC Digital">
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

namespace MailQueueNet.Senders
{
    using System.Net.Mail;
    using System.Threading.Tasks;

    public interface ISender
    {
        /// <summary>
        /// Sends an email with a MailMessage and a set of settings.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="settings"></param>
        /// <returns>true if succeeded, false if sending was skipped (i.e missing settings). Could throw exceptions for other reasons.</returns>
        Task<bool> SendMailAsync(MailMessage message, Grpc.MailSettings settings);
    }
}
