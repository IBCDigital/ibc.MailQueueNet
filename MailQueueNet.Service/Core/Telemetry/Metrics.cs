//-----------------------------------------------------------------------
// <copyright file="Metrics.cs" company="IBC Digital">
//   Copyright (c) IBC Digital. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace MailQueueNet.Service.Core.Telemetry
{
    using System;
    using System.Diagnostics.Metrics;

    /// <summary>
    /// Provides OpenTelemetry metric instruments used by the service.
    /// </summary>
    internal static class Metrics
    {
        private static readonly Meter Meter = new("MailQueueNet.Service");
        private static readonly Counter<long> MailsQueued = Metrics.Meter.CreateCounter<long>("mails_queued_total", unit: "mail", description: "Total mail items queued.");
        private static readonly Counter<long> MailsSent = Metrics.Meter.CreateCounter<long>("mails_sent_total", unit: "mail", description: "Total mail items sent successfully.");
        private static readonly Counter<long> MailsFailed = Metrics.Meter.CreateCounter<long>("mails_failed_total", unit: "mail", description: "Total mail items failed permanently.");
        private static readonly Histogram<double> SendLatency = Metrics.Meter.CreateHistogram<double>("send_latency_ms", unit: "ms", description: "Elapsed time for send attempts.");

        /// <summary>
        /// Records a single queued mail event.
        /// </summary>
        public static void IncQueued()
        {
            Metrics.MailsQueued.Add(1);
        }

        /// <summary>
        /// Records a sent mail event.
        /// </summary>
        public static void IncSent()
        {
            Metrics.MailsSent.Add(1);
        }

        /// <summary>
        /// Records a failed mail event.
        /// </summary>
        public static void IncFailed()
        {
            Metrics.MailsFailed.Add(1);
        }

        /// <summary>
        /// Records the elapsed send latency in milliseconds.
        /// </summary>
        /// <param name="ms">The elapsed time in milliseconds.</param>
        public static void RecordSendLatency(double ms)
        {
            Metrics.SendLatency.Record(ms);
        }
    }
}
