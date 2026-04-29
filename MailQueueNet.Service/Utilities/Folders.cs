// <copyright file="Folders.cs" company="IBC Digital">
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

namespace MailQueueNet.Service.Utilities
{
    using System;
    using System.IO;

    internal static class Folders
    {
        public static string GetTempDir()
        {
            string path = null;
            try
            {
                path = Path.GetTempPath();

                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Environment.GetEnvironmentVariable("TMPDIR");
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Environment.GetEnvironmentVariable("TEMP");
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Environment.GetEnvironmentVariable("TMP");
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Environment.GetEnvironmentVariable("WINDIR");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        path = Path.Combine(path, @"TEMP");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[MailQueueNet] WARNING: Failed reading TEMP/TMP/WINDIR env vars: {0}", ex.Message);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, "tmp");
                Console.WriteLine("[MailQueueNet] WARNING: TEMP/TMP/WINDIR not set. Falling back to '{0}'.", path);
            }

            path = Path.Combine(path, "MailQueueNet") + Path.DirectorySeparatorChar;

            if (VerifyDirectoryExists(path))
            {
                return path;
            }

            throw new UnauthorizedAccessException("Cannot access temp folder: " + path);
        }

        internal static bool VerifyDirectoryExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (!Path.IsPathRooted(path))
            {
                path = Files.MapPath(path);
            }

            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                }
                catch
                {
                    try
                    {
                        CreateDirectory(path);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal static DirectoryInfo CreateDirectory(string path)
        {
            DirectoryInfo dirInfo = new DirectoryInfo(Path.GetFullPath(path));

            try
            {
                if (!dirInfo.Exists)
                {
                    dirInfo.Create();
                }

                return dirInfo;
            }
            catch
            {
                return new DirectoryInfo(path);
            }
        }
    }
}
