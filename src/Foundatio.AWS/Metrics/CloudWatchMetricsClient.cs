using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.Runtime;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Utility;

namespace Foundatio.Metrics {
    public class CloudWatchMetricsClient : BufferedMetricsClientBase, IMetricsClientStats {
        private readonly Lazy<AmazonCloudWatchClient> _client;
        private readonly string _namespace;
        private readonly Dimension _instanceIdDimension;

        public CloudWatchMetricsClient(AWSCredentials credentials, RegionEndpoint region, string @namespace = null, bool buffered = true, ILoggerFactory loggerFactory = null) : base(buffered, loggerFactory) {
            _client = new Lazy<AmazonCloudWatchClient>(() => new AmazonCloudWatchClient(
                credentials ?? FallbackCredentialsFactory.GetCredentials(),
                new AmazonCloudWatchConfig {
                    LogResponse = false,
                    DisableLogging = true,
                    RegionEndpoint = region ?? RegionEndpoint.USEast1
                }));
            
            _namespace = @namespace ?? "app/metrics";
            string instanceId = null;
            
#if NET46
            instanceId = Amazon.Util.EC2InstanceMetadata.InstanceId ?? instanceId;
#endif
            if (String.IsNullOrEmpty(instanceId))
                instanceId = Environment.MachineName;

            _instanceIdDimension = new Dimension {
                Name = "InstanceId",
                Value = instanceId
            };
        }

        protected override async Task StoreCounterAsync(MetricKey key, int value, List<MetricEntry> entries) {
            var response = await _client.Value.PutMetricDataAsync(new PutMetricDataRequest {
                Namespace = _namespace,
                MetricData = new List<MetricDatum> {
                    new MetricDatum {
                        Timestamp = key.StartTimeUtc,
                        MetricName = GetMetricName(MetricType.Counter, key.Name),
                        Value = value
                    },
                    new MetricDatum {
                        Dimensions = new List<Dimension>{ _instanceIdDimension },
                        Timestamp = key.StartTimeUtc,
                        MetricName = GetMetricName(MetricType.Counter, key.Name),
                        Value = value
                    }
                }
            }).AnyContext();
        }

        protected override async Task StoreGaugeAsync(MetricKey key, int count, double total, double last, double min, double max, List<MetricEntry> entries) {
            await _client.Value.PutMetricDataAsync(new PutMetricDataRequest {
                Namespace = _namespace,
                MetricData = new List<MetricDatum> {
                    new MetricDatum {
                        Timestamp = key.StartTimeUtc,
                        MetricName = GetMetricName(MetricType.Gauge, key.Name),
                        StatisticValues = new StatisticSet {
                            SampleCount = count,
                            Sum = total,
                            Minimum = min,
                            Maximum = max
                        }
                    },
                    new MetricDatum {
                        Dimensions = new List<Dimension>{ _instanceIdDimension },
                        Timestamp = key.StartTimeUtc,
                        MetricName = GetMetricName(MetricType.Gauge, key.Name),
                        StatisticValues = new StatisticSet {
                            SampleCount = count,
                            Sum = total,
                            Minimum = min,
                            Maximum = max
                        }
                    }
                }
            }).AnyContext();
        }

        protected override async Task StoreTimingAsync(MetricKey key, int count, int totalDuration, int minDuration, int maxDuration, List<MetricEntry> entries) {
            await _client.Value.PutMetricDataAsync(new PutMetricDataRequest {
                Namespace = _namespace,
                MetricData = new List<MetricDatum> {
                    new MetricDatum {
                        Timestamp = key.StartTimeUtc,
                        MetricName = GetMetricName(MetricType.Timing, key.Name),
                        StatisticValues = new StatisticSet {
                            SampleCount = count,
                            Sum = totalDuration,
                            Minimum = minDuration,
                            Maximum = maxDuration
                        },
                        Unit = StandardUnit.Milliseconds
                    },
                    new MetricDatum {
                        Dimensions = new List<Dimension>{ _instanceIdDimension },
                        Timestamp = key.StartTimeUtc,
                        MetricName = GetMetricName(MetricType.Timing, key.Name),
                        StatisticValues = new StatisticSet {
                            SampleCount = count,
                            Sum = totalDuration,
                            Minimum = minDuration,
                            Maximum = maxDuration
                        },
                        Unit = StandardUnit.Milliseconds
                    }
                }
            }).AnyContext();
        }

        private string GetMetricName(MetricType metricType, string name) {
            return String.Concat(metricType, " ", name);
        }

        public async Task<CounterStatSummary> GetCounterStatsAsync(string name, DateTime? start = default(DateTime?), DateTime? end = default(DateTime?), int dataPoints = 20) {
            if (!start.HasValue)
                start = SystemClock.UtcNow.AddHours(-4);

            if (!end.HasValue)
                end = SystemClock.UtcNow;

            var interval = end.Value.Subtract(start.Value).TotalMinutes > 60 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);

            var request = new GetMetricStatisticsRequest {
                Namespace = _namespace,
                MetricName = GetMetricName(MetricType.Counter, name),
                Period = (int)interval.TotalMinutes,
                StartTime = start.Value,
                EndTime = end.Value,
                Statistics = new List<string> { "Sum" }
            };

            var response = await _client.Value.GetMetricStatisticsAsync(request).AnyContext();
            return new CounterStatSummary(
                name,
                response.Datapoints.Select(dp => new CounterStat {
                    Count = (long)dp.Sum,
                    Time = dp.Timestamp
                }).ToList(),
                start.Value,
                end.Value);
        }

        public Task<GaugeStatSummary> GetGaugeStatsAsync(string name, DateTime? start = default(DateTime?), DateTime? end = default(DateTime?), int dataPoints = 20) {
            throw new NotImplementedException();
        }

        public Task<TimingStatSummary> GetTimerStatsAsync(string name, DateTime? start = default(DateTime?), DateTime? end = default(DateTime?), int dataPoints = 20) {
            throw new NotImplementedException();
        }
    }
}
