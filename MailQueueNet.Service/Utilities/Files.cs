// <copyright file="Files.cs" company="IBC Digital">
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

    internal static class Files
    {
        internal static string MapPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path is null or empty and cannot be resolved.", nameof(path));
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }
            else if (path.StartsWith("~/", StringComparison.Ordinal))
            {
                var resolved = Path.Combine(AppContext.BaseDirectory, path.Remove(0, 2));
                return resolved;
            }
            else
            {
                throw new InvalidOperationException($"Could not resolve non-rooted path '{path}'. Expected rooted path or '~/' prefix.");
            }
        }

        internal static string CreateEmptyTempFile()
        {
            var tempDir = Folders.GetTempDir();
            if (string.IsNullOrWhiteSpace(tempDir))
            {
                Console.WriteLine("[MailQueueNet] ERROR: Temp directory resolved to empty. Cannot create temp file.");
                return null;
            }

            string tempFilePath = tempDir + Guid.NewGuid().ToString() + @".tmp";
            FileStream fs = null;

            while (true)
            {
                try
                {
                    fs = new FileStream(tempFilePath, FileMode.CreateNew);
                    break;
                }
                catch (IOException ioex)
                {
                    Console.WriteLine("[MailQueueNet] Utility.Files.CreateEmptyTempFile failed for '{0}': {1}", tempFilePath, ioex.Message);
                    if (System.IO.File.Exists(tempFilePath))
                    {
                        tempFilePath = tempDir + Guid.NewGuid().ToString() + @".tmp";
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[MailQueueNet] Utility.Files.CreateEmptyTempFile failed for '{0}': {1}", tempFilePath, ex.Message);
                    break;
                }
            }

            if (fs != null)
            {
                fs.Close();
                fs.Dispose();
                fs = null;
                return tempFilePath;
            }

            return null;
        }
    }
}
