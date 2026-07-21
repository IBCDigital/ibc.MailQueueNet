// <copyright file="RetryMailFileStore.cs" company="IBC Digital">
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

    /// <summary>
    /// Persists retry mail files using a same-directory temporary file and
    /// atomic publication to the final <c>*.mail</c> name.
    /// </summary>
    internal static class RetryMailFileStore
    {
        internal const string FinalMailSearchPattern = "*.mail";
        internal const string TemporaryMailSearchPattern = "*.mail.writing-*.tmp";

        private const int MaxPublishAttempts = 10;

        internal static Action<string, string>? BeforePublishForTests { get; set; }

        internal static bool PreserveTemporaryFileOnFailureForTests { get; set; }

        /// <summary>
        /// Writes a retry message to the undelivered folder and returns the final path.
        /// </summary>
        /// <param name="message">The retry message to write.</param>
        /// <param name="undeliveredFolder">The folder that owns retry files.</param>
        /// <returns>The final <c>*.mail</c> path.</returns>
        internal static string WriteMailToUndeliveredFolder(Grpc.MailMessage message, string undeliveredFolder)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (string.IsNullOrWhiteSpace(undeliveredFolder))
            {
                throw new ArgumentException("The undelivered folder is required.", nameof(undeliveredFolder));
            }

            Directory.CreateDirectory(undeliveredFolder);

            for (int attempt = 0; attempt < MaxPublishAttempts; attempt++)
            {
                string finalPath = CreateFinalMailPath(undeliveredFolder);
                string temporaryPath = CreateTemporaryMailPath(finalPath);

                if (File.Exists(finalPath))
                {
                    continue;
                }

                try
                {
                    WriteTemporaryMailFile(message, temporaryPath);
                    BeforePublishForTests?.Invoke(temporaryPath, finalPath);
                    File.Move(temporaryPath, finalPath, overwrite: false);
                    return finalPath;
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    DeleteTemporaryFile(temporaryPath);
                    continue;
                }
                catch
                {
                    if (!PreserveTemporaryFileOnFailureForTests)
                    {
                        DeleteTemporaryFile(temporaryPath);
                    }

                    throw;
                }
            }

            throw new IOException("Unable to create a unique retry mail file name.");
        }

        private static string CreateFinalMailPath(string undeliveredFolder)
        {
            string uniqueId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid()}";
            return Path.Combine(undeliveredFolder, uniqueId + ".mail");
        }

        private static string CreateTemporaryMailPath(string finalPath)
        {
            string folder = Path.GetDirectoryName(finalPath) ?? string.Empty;
            string finalFileName = Path.GetFileName(finalPath);
            string processId = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string suffix = $".writing-{processId}-{Guid.NewGuid():N}.tmp";

            return Path.Combine(folder, finalFileName + suffix);
        }

        private static void WriteTemporaryMailFile(Grpc.MailMessage message, string temporaryPath)
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var streamWriter = new Google.Protobuf.CodedOutputStream(stream, leaveOpen: true))
            {
                message.WriteTo(streamWriter);
                streamWriter.Flush();
                stream.Flush(true);
            }
        }

        private static void DeleteTemporaryFile(string temporaryPath)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }
        }
    }
}
