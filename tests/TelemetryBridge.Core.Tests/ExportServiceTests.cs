using OpenTelemetry.Exporter;
using TelemetryBridge.Core.Services;

namespace TelemetryBridge.Core.Tests;

public sealed class ExportServiceTests
{
    [Theory]
    [InlineData("grpc", OtlpExportProtocol.Grpc)]
    [InlineData("GRPC", OtlpExportProtocol.Grpc)]
    [InlineData("http", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
    public void ResolveProtocol_MapsSupportedProtocols(string input, OtlpExportProtocol expected)
    {
        Assert.Equal(expected, ExportService.ResolveProtocol(input));
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolveProtocol_ReturnsNullForUnsupported(string? input)
    {
        Assert.Null(ExportService.ResolveProtocol(input));
    }

    [Fact]
    public void BuildEndpoint_AppendsTracesPathForHttp()
    {
        var uri = ExportService.BuildEndpoint("http://signoz:4318", OtlpExportProtocol.HttpProtobuf);

        Assert.Equal("http://signoz:4318/v1/traces", uri.ToString());
    }

    [Fact]
    public void BuildEndpoint_DoesNotDoubleAppendTracesPath()
    {
        var uri = ExportService.BuildEndpoint("http://signoz:4318/v1/traces", OtlpExportProtocol.HttpProtobuf);

        Assert.Equal("http://signoz:4318/v1/traces", uri.ToString());
    }

    [Fact]
    public void BuildEndpoint_LeavesGrpcEndpointUntouched()
    {
        var uri = ExportService.BuildEndpoint("http://signoz:4317", OtlpExportProtocol.Grpc);

        Assert.Equal("http://signoz:4317/", uri.ToString());
    }
}
