// <copyright file="SettingsController.cs" company="IBC Digital">
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

namespace MailQueueNet.Service.Core
{
    using System;
    using System.IO;
    using System.Text;
    using Microsoft.Extensions.Configuration;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public static class SettingsController
    {
        private const string OverridesFileName = "appsettings.overrides.json";
        private const string OverridesDirectoryEnvVar = "MAILQUEUENET_CONFIG_DIR";

        public static JObject ReadSettingsForUpdate()
        {
            var filePath = GetOverridesFilePath();

            if (!File.Exists(filePath))
            {
                EnsureDirectoryExistsForFile(filePath);
                var defaults = new JObject
                {
                    ["queue"] = new JObject(),
                };

                File.WriteAllText(filePath, defaults.ToString(Formatting.Indented), Encoding.UTF8);
            }

            using (var reader = new StringReader(File.ReadAllText(filePath, Encoding.UTF8)))
            using (var jsonReader = new JsonTextReader(reader)
            {
                DateParseHandling = DateParseHandling.None,
                DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
            })
            {
                return JObject.Load(jsonReader);
            }
        }

        public static void CommitSettingsUpdates(JObject jSettings)
        {
            var filePath = GetOverridesFilePath();
            EnsureDirectoryExistsForFile(filePath);

            File.WriteAllText(filePath, jSettings.ToString(Formatting.Indented), Encoding.UTF8);
        }

        public static void AddOrUpdateAppSetting<T>(JObject jSettings, string key, T value)
        {
            JToken jElement = jSettings;

            var keyParts = key.Split(":");
            for (var i = 0; i < keyParts.Length - 1; i++)
            {
                if (!(jElement is JObject))
                {
                    return;
                }

                var keyPart = keyParts[i];
                if (((JObject)jElement).ContainsKey(keyPart))
                {
                    jElement = jElement[keyPart];
                }
                else
                {
                    var jEl = new JObject();
                    jElement[keyPart] = jEl;
                    jElement = jEl;
                }
            }

            var lastKey = keyParts[keyParts.Length - 1];
            jElement[lastKey] = value == null ? null : new JValue(value);
        }

        public static void AddOrUpdateAppSetting<T>(string key, T value)
        {
            // Intentionally restrict file updates to admin routes that call SetSettings/SetMailSettings.
            // This helper remains for compatibility but still writes to the overrides file.
            var jSettings = ReadSettingsForUpdate();
            AddOrUpdateAppSetting(jSettings, key, value);
            CommitSettingsUpdates(jSettings);
        }

        public static void SetSettings(Grpc.Settings settings)
        {
            var jSettings = ReadSettingsForUpdate();

            AddOrUpdateAppSetting(jSettings, "queue:queue_folder", settings.QueueFolder);
            AddOrUpdateAppSetting(jSettings, "queue:failed_folder", settings.FailedFolder);
            AddOrUpdateAppSetting(jSettings, "queue:mail_merge_queue_folder", settings.MailMergeQueueFolder);
            AddOrUpdateAppSetting(jSettings, "queue:seconds_until_folder_refresh", settings.SecondsUntilFolderRefresh);
            AddOrUpdateAppSetting(jSettings, "queue:maximum_concurrent_workers", settings.MaximumConcurrentWorkers);
            AddOrUpdateAppSetting(jSettings, "queue:maximum_failure_retries", settings.MaximumFailureRetries);
            AddOrUpdateAppSetting(jSettings, "queue:maximum_pause_minutes", settings.MaximumPauseMinutes);
            AddOrUpdateAppSetting(jSettings, "StagingMailRouting:Enabled", settings.StagingMailRoutingEnabled);
            AddOrUpdateAppSetting(jSettings, "StagingMailRouting:ForceMailpitOnly", settings.StagingForceMailpitOnly);
            AddOrUpdateAppSetting(jSettings, "StagingMailRouting:SubjectPrefix", settings.StagingSubjectPrefix);
            AddOrUpdateSmtpDeliverySettings(jSettings, "StagingMailRouting:Mailpit", settings.StagingMailpit);
            AddOrUpdateSmtpDeliverySettings(jSettings, "StagingMailRouting:RealSmtp", settings.StagingRealSmtp);

            CommitSettingsUpdates(jSettings);
        }

        public static void SetMailSettings(Grpc.MailSettings settings)
        {
            var jSettings = ReadSettingsForUpdate();

            switch (settings?.SettingsCase)
            {
                case Grpc.MailSettings.SettingsOneofCase.Smtp:
                    {
                        AddOrUpdateAppSetting(jSettings, "queue:smtp:server", settings.Smtp.Host);
                        AddOrUpdateAppSetting(jSettings, "queue:smtp:port", settings.Smtp.Port);
                        AddOrUpdateAppSetting(jSettings, "queue:smtp:ssl", settings.Smtp.RequiresSsl);
                        AddOrUpdateAppSetting(jSettings, "queue:smtp:authentication", settings.Smtp.RequiresAuthentication);
                        AddOrUpdateAppSetting(jSettings, "queue:smtp:username", settings.Smtp.Username);
                        AddOrUpdateAppSetting(jSettings, "queue:smtp:password", settings.Smtp.Password);
                        AddOrUpdateAppSetting(jSettings, "queue:smtp:connection_timeout", settings.Smtp.ConnectionTimeout);
                    }

                    break;
                case Grpc.MailSettings.SettingsOneofCase.Mailgun:
                    {
                        AddOrUpdateAppSetting(jSettings, "queue:mailgun:domain", settings.Mailgun.Domain);
                        AddOrUpdateAppSetting(jSettings, "queue:mailgun:api_key", settings.Mailgun.ApiKey);
                        AddOrUpdateAppSetting(jSettings, "queue:mailgun:connection_timeout", settings.Mailgun.ConnectionTimeout);
                    }

                    break;
                default:
                    {
                        AddOrUpdateAppSetting(jSettings, "queue:mail_service_type", string.Empty);
                    }

                    break;
            }

            CommitSettingsUpdates(jSettings);
        }

        public static Grpc.Settings GetSettings(IConfiguration configuration)
        {
            return new Grpc.Settings
            {
                QueueFolder = configuration.GetValue("queue:queue_folder", "~/mail/queue"),
                FailedFolder = configuration.GetValue("queue:failed_folder", "~/mail/failed"),
                MailMergeQueueFolder = configuration.GetValue("queue:mail_merge_queue_folder", "~/mail/merge"),
                SecondsUntilFolderRefresh = configuration.GetValue("queue:seconds_until_folder_refresh", 10.0f),
                MaximumConcurrentWorkers = configuration.GetValue("queue:maximum_concurrent_workers", 4),
                MaximumFailureRetries = configuration.GetValue("queue:maximum_failure_retries", 5),
                MaximumPauseMinutes = configuration.GetValue("queue:maximum_pause_minutes", 30),
                StagingMailRoutingEnabled = configuration.GetValue("StagingMailRouting:Enabled", false),
                StagingForceMailpitOnly = configuration.GetValue("StagingMailRouting:ForceMailpitOnly", true),
                StagingSubjectPrefix = configuration.GetValue("StagingMailRouting:SubjectPrefix", "[STAGING] "),
                StagingMailpit = GetSmtpDeliverySettings(configuration, "StagingMailRouting:Mailpit"),
                StagingRealSmtp = GetSmtpDeliverySettings(configuration, "StagingMailRouting:RealSmtp"),
            };
        }

        private static void AddOrUpdateSmtpDeliverySettings(JObject jSettings, string keyPrefix, Grpc.SmtpMailSettings? settings)
        {
            if (settings == null)
            {
                return;
            }

            AddOrUpdateAppSetting(jSettings, keyPrefix + ":Host", settings.Host);
            AddOrUpdateAppSetting(jSettings, keyPrefix + ":Port", settings.Port);
            AddOrUpdateAppSetting(jSettings, keyPrefix + ":RequiresSsl", settings.RequiresSsl);
            AddOrUpdateAppSetting(jSettings, keyPrefix + ":RequiresAuthentication", settings.RequiresAuthentication);
            AddOrUpdateAppSetting(jSettings, keyPrefix + ":Username", settings.Username);
            AddOrUpdateAppSetting(jSettings, keyPrefix + ":Password", settings.Password);
            AddOrUpdateAppSetting(jSettings, keyPrefix + ":ConnectionTimeout", settings.ConnectionTimeout);
        }

        private static Grpc.SmtpMailSettings GetSmtpDeliverySettings(IConfiguration configuration, string keyPrefix)
        {
            return new Grpc.SmtpMailSettings
            {
                Host = configuration.GetValue(keyPrefix + ":Host", string.Empty),
                Port = configuration.GetValue(keyPrefix + ":Port", 0),
                RequiresSsl = configuration.GetValue(keyPrefix + ":RequiresSsl", false),
                RequiresAuthentication = configuration.GetValue(keyPrefix + ":RequiresAuthentication", false),
                Username = configuration.GetValue(keyPrefix + ":Username", string.Empty),
                Password = configuration.GetValue(keyPrefix + ":Password", string.Empty),
                ConnectionTimeout = configuration.GetValue(keyPrefix + ":ConnectionTimeout", 100000),
            };
        }

        public static Grpc.MailSettings GetMailSettings(IConfiguration configuration)
        {
            switch (configuration.GetValue("queue:mail_service_type", "smtp"))
            {
                case "smtp":
                    {
                        var timeout = configuration.GetValue("queue:smtp:connection_timeout", 100000);
                        if (timeout <= 0)
                        {
                            timeout = 100000;
                        }

                        return new Grpc.MailSettings
                        {
                            Smtp = new Grpc.SmtpMailSettings
                            {
                                Host = configuration.GetValue("queue:smtp:server", string.Empty),
                                Port = configuration.GetValue("queue:smtp:port", 0),
                                RequiresSsl = configuration.GetValue("queue:smtp:ssl", false),
                                RequiresAuthentication = configuration.GetValue("queue:smtp:authentication", false),
                                Username = configuration.GetValue("queue:smtp:username", string.Empty),
                                Password = configuration.GetValue("queue:smtp:password", string.Empty),
                                ConnectionTimeout = timeout,
                            },
                        };
                    }

                case "mailgun":
                    {
                        var timeout = configuration.GetValue("queue:mailgun:connection_timeout", 100000);
                        if (timeout <= 0)
                        {
                            timeout = 100000;
                        }

                        return new Grpc.MailSettings
                        {
                            Mailgun = new Grpc.MailgunMailSettings
                            {
                                Domain = configuration.GetValue("queue:mailgun:domain", string.Empty),
                                ApiKey = configuration.GetValue("queue:mailgun:api_key", string.Empty),
                                ConnectionTimeout = timeout,
                            },
                        };
                    }
            }

            // Something really empty that won't send anything
            return new Grpc.MailSettings();
        }

        private static string GetOverridesFilePath()
        {
            var configDir = Environment.GetEnvironmentVariable(OverridesDirectoryEnvVar);
            if (!string.IsNullOrWhiteSpace(configDir))
            {
                return Path.Combine(configDir, OverridesFileName);
            }

            if (Directory.Exists("/data/config"))
            {
                return Path.Combine("/data/config", OverridesFileName);
            }

            return Path.Combine(AppContext.BaseDirectory, OverridesFileName);
        }

        private static void EnsureDirectoryExistsForFile(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
