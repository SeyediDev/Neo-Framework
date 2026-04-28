using Neo.Endpoint.Features.Monitoring.Models;

namespace Neo.Endpoint.Features.Monitoring.Services;

/// <summary>
/// Background service that collects metrics using MeterListener
/// </summary>
public class MetricsCollector(
	IMetricsStore store,
	IOptions<MonitoringStorageOptions> options,
	ILogger<MetricsCollector> logger) : BackgroundService
{
	private readonly MonitoringStorageOptions _options = options?.Value ?? new MonitoringStorageOptions();
    private MeterListener? _meterListener;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MetricsCollector starting...");

        _meterListener = new MeterListener();

        // Subscribe to all instruments
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            logger.LogDebug("Instrument published: {Name} ({Type}) from {MeterName}", 
                instrument.Name, instrument.GetType().Name, instrument.Meter.Name);
            
            listener.EnableMeasurementEvents(instrument);
        };

        // Handle different measurement types
        _meterListener.SetMeasurementEventCallback<int>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<long>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<double>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<float>(OnMeasurement);
        _meterListener.SetMeasurementEventCallback<decimal>((instrument, value, tags, state) => 
            OnMeasurement(instrument, (double)value, tags, state));
        _meterListener.SetMeasurementEventCallback<byte>((instrument, value, tags, state) => 
            OnMeasurement(instrument, (double)value, tags, state));
        _meterListener.SetMeasurementEventCallback<short>((instrument, value, tags, state) => 
            OnMeasurement(instrument, (double)value, tags, state));

        _meterListener.Start();

        logger.LogInformation("MetricsCollector started. Listening for metrics...");

        // Periodic cleanup
        var cleanupInterval = TimeSpan.FromMinutes(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Record observable instruments
                _meterListener.RecordObservableInstruments();
                
                await Task.Delay(_options.MetricsCollectionIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in metrics collection loop");
                await Task.Delay(1000, stoppingToken);
            }
        }

        logger.LogInformation("MetricsCollector stopping...");
    }

    private void OnMeasurement<T>(
        Instrument instrument, 
        T measurement, 
        ReadOnlySpan<KeyValuePair<string, object?>> tags, 
        object? state) where T : struct
    {
        try
        {
            var tagDict = new Dictionary<string, string>();
            foreach (var tag in tags)
            {
                tagDict[tag.Key] = tag.Value?.ToString() ?? "";
            }

            var dataPoint = new MetricDataPoint
            {
                MetricName = instrument.Name,
                MetricType = GetInstrumentType(instrument),
                Timestamp = DateTime.UtcNow,
                Value = Convert.ToDouble(measurement),
                Tags = tagDict,
                Unit = instrument.Unit,
                Description = instrument.Description
            };

            store.Record(dataPoint);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error recording metric {MetricName}", instrument.Name);
        }
    }

    private static string GetInstrumentType(Instrument instrument)
    {
        return instrument.GetType().Name switch
        {
            var name when name.Contains("Counter") && name.Contains("UpDown") => "UpDownCounter",
            var name when name.Contains("Counter") => "Counter",
            var name when name.Contains("Histogram") => "Histogram",
            var name when name.Contains("Gauge") => "Gauge",
            var name when name.Contains("Observable") => "Observable",
            _ => "Unknown"
        };
    }

    public override void Dispose()
    {
        _meterListener?.Dispose();
        base.Dispose();
    }
}

