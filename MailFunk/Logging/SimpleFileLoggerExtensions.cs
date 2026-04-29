// <copyright file="SimpleFileLoggerExtensions.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>

namespace MailFunk.Logging;

using System;
using System.IO;
using MailQueueNet.Common.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public static class SimpleFileLoggerExtensions
{
    public static ILoggingBuilder AddSimpleFile(this ILoggingBuilder builder, IConfiguration configuration, string? contentRootPath = null)
    {
        var section = configuration.GetSection("FileLogging");
        var configuredPath = section["Path"];
        var fileSizeLimitMb = section.GetValue<int?>("FileSizeLimitMb") ?? 10;
        var maxFiles = section.GetValue<int?>("MaxFiles") ?? 5;

        var basePath = contentRootPath ?? AppContext.BaseDirectory;
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(basePath, "Logs")
            : Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(basePath, configuredPath);

        Directory.CreateDirectory(path);
        var provider = new SimpleFileLoggerProvider(path, fileSizeLimitMb * 1024 * 1024, maxFiles);
        builder.AddProvider(provider);
        return builder;
    }
}
