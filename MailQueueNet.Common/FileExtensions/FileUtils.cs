// <copyright file="FileUtils.cs" company="IBC Digital">
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
    using System.IO;

    /// <summary>
    /// Utility helpers for writing and reading protobuf-encoded mail messages
    /// to and from disk.  The helpers are used by the client-side resilience
    /// layer to persist undelivered messages.
    /// </summary>
    public class FileUtils
    {
        /// <summary>
        /// Reads a <see cref="Grpc.MailMessageWithSettings"/> from the specified
        /// file path.
        /// </summary>
        /// <param name="path">Absolute or relative path of the file to read.</param>
        /// <returns>
        /// A deserialised <see cref="Grpc.MailMessageWithSettings"/> instance.
        /// </returns>
        public static Grpc.MailMessageWithSettings ReadMailFromFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var streamReader = new Google.Protobuf.CodedInputStream(stream))
            {
                return Grpc.MailMessageWithSettings.Parser.ParseFrom(streamReader);
            }
        }

        /// <summary>
        /// Reads a <see cref="Grpc.MailMessage"/> from the specified
        /// file path.
        /// </summary>
        /// <param name="path">Absolute or relative path of the file to read.</param>
        /// <returns>
        /// A deserialised <see cref="Grpc.MailMessage"/> instance.
        /// </returns>
        public static Grpc.MailMessage ReadMailNoSettingsFromFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var streamReader = new Google.Protobuf.CodedInputStream(stream))
            {
                return Grpc.MailMessage.Parser.ParseFrom(streamReader);
            }
        }

        /// <summary>
        /// Writes a <see cref="Grpc.MailMessageWithSettings"/> instance to disk
        /// in protobuf format.
        /// </summary>
        /// <param name="message">The message to serialise.</param>
        /// <param name="path">Destination file path.</param>
        /// <returns>
        /// <see langword="true"/> if the operation succeeded; otherwise
        /// <see langword="false"/>.
        /// </returns>
        public static bool WriteMailToFile(Grpc.MailMessageWithSettings message, string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (var streamWriter = new Google.Protobuf.CodedOutputStream(stream))
                {
                    message.WriteTo(streamWriter);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Writes a plain <see cref="Grpc.MailMessage"/> to disk in protobuf
        /// format.
        /// </summary>
        /// <param name="message">The message to serialise.</param>
        /// <param name="path">Destination file path.</param>
        /// <returns>
        /// <see langword="true"/> if the write succeeds; otherwise
        /// <see langword="false"/>.
        /// </returns>
        public static bool WriteMailToFile(Grpc.MailMessage message, string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                using (var streamWriter = new Google.Protobuf.CodedOutputStream(stream))
                {
                    message.WriteTo(streamWriter);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}