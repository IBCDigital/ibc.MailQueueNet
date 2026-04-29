// <copyright file="MailSettings.cs" company="IBC Digital">
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

namespace MailQueueNet.Grpc
{
    public partial class MailSettings
    {
        public bool IsEmpty()
        {
            if (this.SettingsCase == SettingsOneofCase.Mailgun && this.Mailgun != null)
            {
                return string.IsNullOrEmpty(this.Mailgun.Domain)
                    || string.IsNullOrEmpty(this.Mailgun.ApiKey);
            }
            else if (this.SettingsCase == SettingsOneofCase.Smtp && this.Smtp != null)
            {
                return string.IsNullOrEmpty(this.Smtp.Host);
            }

            return true;
        }
    }
}
