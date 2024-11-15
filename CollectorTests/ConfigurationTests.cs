using Grpc.Net.Client;
using OddDotCSharp;
using OddDotNet.Proto.Common.V1;
using OddDotNet.Proto.Metrics.V1;
using OddDotNet.Proto.Trace.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit.Abstractions;
using PropertyFilter = OddDotNet.Proto.Trace.V1.PropertyFilter;
using Where = OddDotNet.Proto.Metrics.V1.Where;

namespace CollectorTests;

public class ConfigurationTests : IAsyncLifetime
{
#pragma warning disable CS8618
    private DistributedApplication _app;
    private SpanQueryService.SpanQueryServiceClient _spanQueryServiceClient;
    private TraceService.TraceServiceClient _traceServiceClient;
#pragma warning enable CS8618
    
    private readonly ITestOutputHelper _testOutputHelper;

    public ConfigurationTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task OptedOutSpansAreAlwaysSampled()
    {
        var optedOutTrace = TraceHelpers.CreateExportTraceServiceRequest();
        var optedInTrace = TraceHelpers.CreateExportTraceServiceRequest();
        optedOutTrace.ResourceSpans[0].Resource.Attributes.Add(CommonHelpers.CreateKeyValue("service.name", "service-4"));
        optedInTrace.ResourceSpans[0].Resource.Attributes.Add(CommonHelpers.CreateKeyValue("service.name", "service-1"));

        var optedOutSpan = optedOutTrace.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var optedInSpan = optedInTrace.ResourceSpans[0].ScopeSpans[0].Spans[0];
        
        await _traceServiceClient.ExportAsync(optedOutTrace);
        await _traceServiceClient.ExportAsync(optedInTrace);

        var query = new SpanQueryRequestBuilder()
            .TakeAll()
            .Wait(TimeSpan.FromSeconds(5))
            .Where(filters =>
            {
                filters.AddOrFilter(orFilters =>
                {
                    orFilters.AddSpanIdFilter(optedOutSpan.SpanId.ToByteArray(), ByteStringCompareAsType.Equals)
                        .AddSpanIdFilter(optedInSpan.SpanId.ToByteArray(), ByteStringCompareAsType.Equals);
                });
            })
            .Build();
        
        var response = await _spanQueryServiceClient.QueryAsync(query);
        Assert.Contains(response.Spans, span => span.Span.SpanId == optedOutSpan.SpanId);
        Assert.DoesNotContain(response.Spans, span => span.Span.SpanId == optedInSpan.SpanId);
    }

    [Fact]
    public async Task LowSamplingForReadinessProbes()
    {
        var request = TraceHelpers.CreateExportTraceServiceRequest();
        request.ResourceSpans[0].ScopeSpans[0].Spans.Clear();
        request.ResourceSpans[0].Resource.Attributes.Add(CommonHelpers.CreateKeyValue("service.name", "service-1"));
        for (int i = 0; i < 100; i++)
        {
            var span = TraceHelpers.CreateSpan();
            span.Attributes.Add(CommonHelpers.CreateKeyValue("http.route", "/ready"));
            request.ResourceSpans[0].ScopeSpans[0].Spans.Add(span);
        }
        
        await _traceServiceClient.ExportAsync(request);

        var query = new SpanQueryRequestBuilder()
            .TakeAll()
            .Wait(TimeSpan.FromSeconds(5))
            .Where(filters =>
            {
                filters
                    .AddAttributeFilter("http.route", "/ready", StringCompareAsType.Equals)
                    .Resource.AddAttributeFilter("service.name", "service-1", StringCompareAsType.Equals);
            })
            .Build();
        
        var response = await _spanQueryServiceClient.QueryAsync(query);
        Assert.InRange(response.Spans.Count, 5, 15);
    }

    [Fact]
    public async Task OptedInServiceErrorsAlwaysSample()
    {
        var request = TraceHelpers.CreateExportTraceServiceRequest();
        request.ResourceSpans[0].Resource.Attributes.Add(CommonHelpers.CreateKeyValue("service.name", "service-1"));
        var spanToFind = request.ResourceSpans[0].ScopeSpans[0].Spans[0];
        spanToFind.Status.Code = Status.Types.StatusCode.Error;
        
        await _traceServiceClient.ExportAsync(request);

        var query = new SpanQueryRequestBuilder()
            .TakeFirst()
            .Wait(TimeSpan.FromSeconds(5))
            .Where(filters =>
            {
                filters.AddSpanIdFilter(spanToFind.SpanId.ToByteArray(), ByteStringCompareAsType.Equals);
            })
            .Build();

        var response = await _spanQueryServiceClient.QueryAsync(query);
        Assert.NotEmpty(response.Spans);
    }
    
    [Fact]
    public async Task OptedInServiceErrorsAlwaysSampleV2()
    {
        var request = TraceHelpers.CreateExportTraceServiceRequest();
        request.ResourceSpans[0].Resource.Attributes.Add(CommonHelpers.CreateKeyValue("service.name", "service-1"));
        var spanToFind = request.ResourceSpans[0].ScopeSpans[0].Spans[0];
        spanToFind.Status.Code = Status.Types.StatusCode.Error;
        
        await _traceServiceClient.ExportAsync(request);

        // var query = new SpanQueryRequestBuilder()
        //     .TakeFirst()
        //     .Wait(TimeSpan.FromSeconds(5))
        //     .Where(filters =>
        //     {
        //         filters.AddSpanIdFilter(spanToFind.SpanId.ToByteArray(), ByteStringCompareAsType.Equals);
        //     })
        //     .Build();

        var query = new SpanQueryRequest
        {
            Take = new Take
            {
                TakeFirst = new TakeFirst()
            },
            Duration = new Duration
            {
                Milliseconds = 5000
            },
            Filters =
            {
                new OddDotNet.Proto.Trace.V1.Where
                {
                    Property = new PropertyFilter
                    {
                        SpanId = new ByteStringProperty
                        {
                            CompareAs = ByteStringCompareAsType.Equals,
                            Compare = spanToFind.SpanId
                        }
                    }
                }
            }
        };

        var response = await _spanQueryServiceClient.QueryAsync(query);
        Assert.NotEmpty(response.Spans);
    }

    public async Task InitializeAsync()
    {
        var appHostBuilder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>();
        _app = await appHostBuilder.BuildAsync();
        var resourceNotificationService = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();
        await resourceNotificationService.WaitForResourceAsync("odddotnet", KnownResourceStates.Running).WaitAsync(TimeSpan.FromSeconds(5));
        await resourceNotificationService.WaitForResourceAsync("otel", KnownResourceStates.Running).WaitAsync(TimeSpan.FromSeconds(5));
        
        // .NET Aspire 8.2 does not have health check capabilities or the ability for a resource to
        // report healthy before continuing. .NET Aspire 9 *WILL* have it, but until then, perform
        // a manual health check.
        var oddDotNetClient = _app.CreateHttpClient("odddotnet");
        bool oddDotNetIsHealthy;
        const int maxAttempts = 3;
        int currentAttempt = 0;
        do
        {
            currentAttempt++;
            var healthCheckResponse = await oddDotNetClient.GetAsync("/healthz");
            oddDotNetIsHealthy = healthCheckResponse.IsSuccessStatusCode;
            if (!oddDotNetIsHealthy)
                await Task.Delay(TimeSpan.FromSeconds(1));
        } while (!oddDotNetIsHealthy && currentAttempt <= maxAttempts);
        
        var channel = GrpcChannel.ForAddress(_app.GetEndpoint("odddotnet", "grpc"));
        _spanQueryServiceClient = new SpanQueryService.SpanQueryServiceClient(channel);
        
        var otelChannel = GrpcChannel.ForAddress(_app.GetEndpoint("otel", "grpc"));
        _traceServiceClient = new TraceService.TraceServiceClient(otelChannel);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}