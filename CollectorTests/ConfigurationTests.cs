using Aspire.Hosting;
using Grpc.Net.Client;
using OddDotCSharp;
using OddDotNet.Proto.Common.V1;
using OddDotNet.Proto.Trace.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace CollectorTests;

public class ConfigurationTests : IAsyncLifetime
{
#pragma warning disable CS8618
    private DistributedApplication _app;
    private SpanQueryService.SpanQueryServiceClient _spanQueryServiceClient;
    private TraceService.TraceServiceClient _traceServiceClient;
#pragma warning restore CS8618

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
                new Where
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
        await resourceNotificationService.WaitForResourceAsync("otel", KnownResourceStates.Running).WaitAsync(TimeSpan.FromSeconds(30));
        await resourceNotificationService.WaitForResourceAsync("odddotnet", KnownResourceStates.Running).WaitAsync(TimeSpan.FromSeconds(30));
        
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