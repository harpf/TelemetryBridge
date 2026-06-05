using OpenTelemetry.Exporter;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

public class ExportServiceEndpointTests
{
    [Theory]
    [InlineData("grpc", OtlpExportProtocol.Grpc)]
    [InlineData("GRPC", OtlpExportProtocol.Grpc)]
    [InlineData("http", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
    public void ResolveProtocol_SupportsExpectedValues(string input, OtlpExportProtocol expected)
    {
        var result = ExportService.ResolveProtocol(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveProtocol_ReturnsNull_ForUnsupportedProtocol()
    {
        var result = ExportService.ResolveProtocol("tcp");
        Assert.Null(result);
    }

    [Fact]
    public void BuildEndpoint_AddsTracesPath_ForHttpProtocol()
    {
        var uri = ExportService.BuildEndpoint("http://localhost:4318", OtlpExportProtocol.HttpProtobuf);
        Assert.Equal("http://localhost:4318/v1/traces", uri.ToString());
    }

    [Fact]
    public void BuildEndpoint_DoesNotDuplicatePath_ForHttpProtocol()
    {
        var uri = ExportService.BuildEndpoint("http://localhost:4318/v1/traces", OtlpExportProtocol.HttpProtobuf);
        Assert.Equal("http://localhost:4318/v1/traces", uri.ToString());
    }

    [Fact]
    public void BuildEndpoint_KeepsGrpcEndpointUnchanged()
    {
        var uri = ExportService.BuildEndpoint("http://localhost:4317", OtlpExportProtocol.Grpc);
        Assert.Equal("http://localhost:4317/", uri.ToString());
    }
}
