// <copyright file="FileResendLock.cs" company="IBC Digital">
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
// <license>
// MIT Licence – see the repository root LICENCE file for full text.
// </license>

namespace MailQueueNet.Common.FileExtensions
{
    using System;
    using System.IO;

    internal sealed class FileResendLock : IDisposable
    {
        private readonly string path;
        private FileStream? handle;

        public FileResendLock(string folder, string fileName) =>
            this.path = Path.Combine(folder, fileName);

        // non-blocking
        public bool TryAcquire()
        {
            try
            {
                this.handle = new FileStream(
                    this.path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);      // exclusive
                this.handle.WriteByte(0); // touch timestamp later
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public bool StillHeld()
        {
            try
            {
                this.handle?.WriteByte(0);
                return true;
            }

            // lost network share etc.
            catch
            {
                return false;
            }
        }

        public void Dispose() => this.handle?.Dispose();
    }
}
