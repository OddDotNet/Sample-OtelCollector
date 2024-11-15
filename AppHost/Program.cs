var builder = DistributedApplication.CreateBuilder(args);

var oddDotNet = builder.AddContainer("odddotnet", "ghcr.io/odddotnet/odddotnet")
    .WithHttpEndpoint(targetPort: 4317, name: "grpc")
    .WithHttpEndpoint(targetPort: 4318, name: "http"); // For the healthcheck endpoint

builder
    .AddContainer("otel", "ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector-contrib")
    .WithHttpEndpoint(targetPort: 4317, name: "grpc")
    .WithHttpEndpoint(targetPort: 55679, name: "zpages")
    .WithBindMount("config.yaml", "/etc/otelcol-contrib/config.yaml") // Mount our config file to the container
    .WithBindMount("odd.yaml", "/etc/otelcol-contrib/odd.yaml") // Also mount our OddDotNet config file
    .WithEnvironment("ODD_OTLP_ENDPOINT", oddDotNet.GetEndpoint("grpc")) // Set an env for the OddDotNet gRPC endpoint
    .WithArgs("--config=/etc/otelcol-contrib/config.yaml") // Specify our ("Production")config file
    .WithArgs("--config=/etc/otelcol-contrib/odd.yaml"); // Also grab our OddDotNet config file

builder.Build().Run();