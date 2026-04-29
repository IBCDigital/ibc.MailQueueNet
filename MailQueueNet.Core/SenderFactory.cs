// <copyright file="SenderFactory.cs" company="IBC Digital">
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

namespace MailQueueNet
{
    using System.Net.Mail;
    using System.Threading.Tasks;
    using MailQueueNet.Senders;

    public static class SenderFactory
    {
        public static ISender GetSenderForSettings(Grpc.MailSettings settings)
        {
            if (settings.SettingsCase == Grpc.MailSettings.SettingsOneofCase.Smtp)
            {
                return new Senders.SMTP();
            }
            else if (settings.SettingsCase == Grpc.MailSettings.SettingsOneofCase.Mailgun)
            {
                return new Senders.Mailgun();
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Sends an email with a MailMessage and a set of settings.
        /// </summary>
        /// <param name="message"></param>
        /// <param name="settings"></param>
        /// <returns>true if succeeded, false if sending was skipped (i.e missing settings). Could throw exceptions for other reasons.</returns>
        public static async Task<bool> SendMailAsync(MailMessage message, Grpc.MailSettings settings)
        {
            var sender = GetSenderForSettings(settings);
            if (sender == null)
            {
                return false;
            }

            return await sender.SendMailAsync(message, settings);
        }
    }
}
