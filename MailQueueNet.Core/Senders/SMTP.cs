// <copyright file="SMTP.cs" company="IBC Digital">
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
    using MailQueueNet.Core.Logging;

    public class SMTP : ISender
    {
        public async Task<bool> SendMailAsync(MailMessage message, Grpc.MailSettings settings)
        {
            var smtpSettings = settings.Smtp;

            if (string.IsNullOrEmpty(smtpSettings.Host))
            {
                return false;
            }

            using (var smtp = new SmtpClient())
            {
                smtp.Host = smtpSettings.Host.Trim();

                if (smtpSettings.Port > 0)
                {
                    smtp.Port = smtpSettings.Port;
                }

                if (smtpSettings.RequiresAuthentication)
                {
                    smtp.Credentials = new System.Net.NetworkCredential(smtpSettings.Username, smtpSettings.Password);
                }

                smtp.EnableSsl = smtpSettings.RequiresSsl;

                smtp.Timeout = smtpSettings.ConnectionTimeout <= 0 ? 100000 : smtpSettings.ConnectionTimeout;

                MailQueueNetLogger.LogMessage($"email settings: Host: {smtp.Host} Port: {smtp.Port}, Enable SSL: {smtp.EnableSsl}", LogFileTypes.EmailLog, IBC.Logging.LogLevel.None);

                await smtp.SendMailAsync(message);
            }

            return true;
        }
    }
}
