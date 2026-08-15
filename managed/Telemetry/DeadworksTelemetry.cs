using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DeadworksManaged.Telemetry;

/// <summary>
/// Central telemetry bootstrap. Owns the <see cref="ILoggerFactory"/>, <see cref="MeterProvider"/>,
/// and <see cref="TracerProvider"/>. Initialized once during server startup.
/// </summary>
internal static class DeadworksTelemetry
{
    private static ILoggerFactory? _loggerFactory;
    private static MeterProvider? _meterProvider;
    private static TracerProvider? _tracerProvider;

    /// <summary>
    /// Initialize the telemetry system. Must be called after <see cref="DeadworksConfig.Initialize"/>
    /// and after <see cref="NativeLogCallback.Set"/> has stored the native callback pointer.
    /// </summary>
    public static unsafe void Initialize()
    {
        var config = DeadworksConfig.Telemetry;

        // Environment variable overrides (standard OTel + Deadworks-specific)
        var enabled = GetEnvBool("DEADWORKS_TELEMETRY_ENABLED", config.Enabled);
        var otlpEndpoint = GetEnvString("DEADWORKS_OTLP_ENDPOINT", config.OtlpEndpoint);
        var otlpProtocol = GetEnvString("DEADWORKS_OTLP_PROTOCOL", config.OtlpProtocol);
        var serviceName = GetEnvString("DEADWORKS_SERVICE_NAME", config.ServiceName);
        var logLevelStr = GetEnvString("DEADWORKS_LOG_LEVEL", config.LogLevel);

        if (!Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var minLogLevel))
            minLogLevel = LogLevel.Information;

        var exportProtocol = otlpProtocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceVersion: typeof(DeadworksTelemetry).Assembly.GetName().Version?.ToString() ?? "0.0.0")
            .AddAttributes([
                new KeyValuePair<string, object>("host.name", Environment.MachineName),
            ]);

        Action<OtlpExporterOptions> configureOtlp = otlp =>
        {
            otlp.Endpoint = new Uri(otlpEndpoint);
            otlp.Protocol = exportProtocol;
        };

        // --- Logging ---
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minLogLevel);

            // Always add native engine provider (writes to game console)
            var nativeCallback = NativeLogCallback.Callback;
            if (nativeCallback != null)
            {
                builder.AddProvider(new NativeEngineLoggerProvider(nativeCallback, minLogLevel));
            }

            // Add OTLP log exporter if telemetry is enabled
            if (enabled)
            {
                builder.AddOpenTelemetry(options =>
                {
                    options.SetResourceBuilder(resourceBuilder);
                    options.IncludeScopes = true;
                    options.IncludeFormattedMessage = true;
                    options.AddOtlpExporter(configureOtlp);
                });
            }
        });

        _loggerFactory = loggerFactory;

        // --- Metrics ---
        if (enabled && config.EnableMetrics)
        {
            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddMeter(DeadworksMetrics.MeterName)
                .AddOtlpExporter(configureOtlp)
                .Build();
        }

        // --- Traces ---
        if (enabled && config.EnableTraces)
        {
            var tracerBuilder = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder)
                .AddSource(DeadworksTracing.SourceName)
                .AddOtlpExporter(configureOtlp);

            if (config.TraceSamplingRatio < 1.0)
                tracerBuilder.SetSampler(new TraceIdRatioBasedSampler(config.TraceSamplingRatio));

            _tracerProvider = tracerBuilder.Build();
        }

        var logger = CreateLogger("DeadworksTelemetry");
        logger.LogInformation("Telemetry initialized (enabled={Enabled}, endpoint={Endpoint}, protocol={Protocol})",
            enabled, otlpEndpoint, otlpProtocol);
    }

    public static ILogger CreateLogger(string categoryName)
    {
        return _loggerFactory?.CreateLogger(categoryName) ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger(categoryName);
    }

    public static ILogger CreateLogger<T>()
    {
        return _loggerFactory?.CreateLogger<T>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance.CreateLogger<T>();
    }

    /// <summary>
    /// Flush pending exports and dispose all providers. Called during server shutdown.
    /// </summary>
    public static void Shutdown()
    {
        var logger = CreateLogger("DeadworksTelemetry");
        logger.LogInformation("Telemetry shutting down");

        _tracerProvider?.Dispose();
        _tracerProvider = null;

        _meterProvider?.Dispose();
        _meterProvider = null;

        _loggerFactory?.Dispose();
        _loggerFactory = null;
    }

    private static string GetEnvString(string key, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    private static bool GetEnvBool(string key, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(value)) return defaultValue;
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }
}
