// <copyright file="MailService.cs" company="IBC Digital">
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

namespace MailQueueNet.Service.GrpcServices
{
    using System;
    using System.Threading.Tasks;
    using global::Grpc.Core;
    using MailQueueNet.Core.Logging;
    using MailQueueNet.Service.Core;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;

    internal class MailService : Grpc.MailGrpcService.MailGrpcServiceBase
    {
        private readonly ILogger<MailService> logger;
        private readonly IConfiguration configuration;
        private readonly Coordinator coordinator;

        public MailService(ILogger<MailService> logger, IConfiguration configuration, Coordinator coordinator)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.coordinator = coordinator;
        }

        public override Task<Grpc.MailMessageReply> QueueMail(Grpc.MailMessage request, ServerCallContext context)
        {
            var success = false;

            MailQueueNetLogger.LogMessage($"QueMail Received: {Coordinator.GetLongDesc(request)}", LogFileTypes.AccessLog, IBC.Logging.LogLevel.None);

            try
            {
                this.coordinator.AddMail(request);
                success = true;
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, $"Exception thrown for QueueMail");
                MailQueueNetLogger.LogException($"Exception thrown for QueueMail: {ex}");
            }

            MailQueueNetLogger.SaveLogFiles(true);

            return Task.FromResult(new Grpc.MailMessageReply
            {
                Success = success,
            });
        }

        public override Task<Grpc.MailMessageReply> QueueMailWithSettings(Grpc.MailMessageWithSettings request, ServerCallContext context)
        {
            var success = false;

            MailQueueNetLogger.LogMessage($"QueueMailWithSettings Received: {Coordinator.GetLongDesc(request.Message)}", LogFileTypes.AccessLog, IBC.Logging.LogLevel.None);

            try
            {
                this.coordinator.AddMail(request);
                success = true;
            }
            catch (Exception ex)
            {
                this.logger?.LogError(ex, $"Exception thrown for QueueMail");
                MailQueueNetLogger.LogException($"Exception thrown for QueueMail: {ex}");
            }

            MailQueueNetLogger.SaveLogFiles(true);

            return Task.FromResult(new Grpc.MailMessageReply
            {
                Success = success,
            });
        }

        public override Task<Grpc.SetMailSettingsReply> SetMailSettings(Grpc.SetMailSettingsMessage request, ServerCallContext context)
        {
            SettingsController.SetMailSettings(request.Settings);
            return Task.FromResult(new Grpc.SetMailSettingsReply { });
        }

        public override Task<Grpc.GetMailSettingsReply> GetMailSettings(Grpc.GetMailSettingsMessage request, ServerCallContext context)
        {
            return Task.FromResult(new Grpc.GetMailSettingsReply { Settings = SettingsController.GetMailSettings(this.configuration) });
        }

        public override Task<Grpc.SetSettingsReply> SetSettings(Grpc.SetSettingsMessage request, ServerCallContext context)
        {
            SettingsController.SetSettings(request.Settings);
            return Task.FromResult(new Grpc.SetSettingsReply { });
        }

        public override Task<Grpc.GetSettingsReply> GetSettings(Grpc.GetSettingsMessage request, ServerCallContext context)
        {
            return Task.FromResult(new Grpc.GetSettingsReply { Settings = SettingsController.GetSettings(this.configuration) });
        }
    }
}
