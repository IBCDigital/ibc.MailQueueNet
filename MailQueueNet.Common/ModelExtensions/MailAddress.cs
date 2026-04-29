// <copyright file="MailAddress.cs" company="IBC Digital">
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
    public partial class MailAddress
    {
        public static MailAddress From(System.Net.Mail.MailAddress address)
        {
            return new MailAddress
            {
                Address = address.Address,
                DisplayName = address.DisplayName,
            };
        }

        public System.Net.Mail.MailAddress ToSystemType()
        {
            return new System.Net.Mail.MailAddress(this.Address, this.DisplayName);
        }
    }
}
