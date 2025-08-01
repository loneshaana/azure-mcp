// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine.Parsing;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureMcp.Areas.Extension.Commands;
using AzureMcp.Models.Command;
using AzureMcp.Services.ProcessExecution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace AzureMcp.Tests.Areas.Extension.UnitTests;

[Trait("Area", "Extension")]
public sealed class AzCommandTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IExternalProcessService _processService;
    private readonly ILogger<AzCommand> _logger;

    public AzCommandTests()
    {
        _processService = Substitute.For<IExternalProcessService>();
        _logger = Substitute.For<ILogger<AzCommand>>();

        var collection = new ServiceCollection();
        collection.AddSingleton(_processService);
        _serviceProvider = collection.BuildServiceProvider();
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSuccessResult_WhenCommandExecutesSuccessfully()
    {
        using (new TestEnvVar(new Dictionary<string, string>
            {
                { "AZURE_CREDENTIALS", """{"clientId": "myClientId","clientSecret": "myClientSecret","subscriptionId": "mySubscriptionID","tenantId": "myTenantId"}""" }
            }))
        {
            // Arrange
            var command = new AzCommand(_logger);
            var parser = new Parser(command.GetCommand());
            var args = parser.Parse("--command \"group list\"");
            var context = new CommandContext(_serviceProvider);

            var expectedOutput = """{"value":[{"id":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg","name":"test-rg","type":"Microsoft.Resources/resourceGroups","location":"eastus","properties":{"provisioningState":"Succeeded"}}]}""";
            var expectedJson = JsonDocument.Parse(expectedOutput).RootElement.Clone();

            _processService.ExecuteAsync(
                Arg.Any<string>(),
                "group list",
                Arg.Any<int>(),
                Arg.Any<IEnumerable<string>>())
                .Returns(new ProcessResult(0, expectedOutput, string.Empty, "group list"));

            _processService.ParseJsonOutput(Arg.Any<ProcessResult>())
                .Returns(expectedJson);

            // Act
            var response = await command.ExecuteAsync(context, args);

            // Assert
            Assert.NotNull(response);
            Assert.Equal(200, response.Status);
            Assert.NotNull(response.Results);

            // Verify the ProcessService was called with expected parameters
            await _processService.Received().ExecuteAsync(
                Arg.Any<string>(),
                "group list",
                Arg.Any<int>(),
                Arg.Any<IEnumerable<string>>());

            await _processService.Received().ExecuteAsync(
                Arg.Any<string>(),
                $"login --service-principal -u myClientId -p myClientSecret --tenant myTenantId",
                Arg.Any<int>(),
                Arg.Any<IEnumerable<string>>());
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsErrorResponse_WhenCommandFails()
    {
        // Arrange
        var command = new AzCommand(_logger);
        var parser = new Parser(command.GetCommand());
        var args = parser.Parse("--command \"group invalid-command\"");
        var context = new CommandContext(_serviceProvider);

        var errorMessage = "Error: az group: 'invalid-command' is not an az command.";

        _processService.ExecuteAsync(
            Arg.Any<string>(),
            "group invalid-command",
            Arg.Any<int>(),
            Arg.Any<IEnumerable<string>>())
            .Returns(new ProcessResult(1, string.Empty, errorMessage, "group invalid-command"));

        // Act
        var response = await command.ExecuteAsync(context, args);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(500, response.Status);
        Assert.Equal(errorMessage, response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesException_AndSetsException()
    {
        // Arrange
        var command = new AzCommand(_logger);
        var parser = new Parser(command.GetCommand());
        var args = parser.Parse("--command \"group list\"");
        var context = new CommandContext(_serviceProvider);

        var exceptionMessage = "Azure CLI executable not found";

        _processService.ExecuteAsync(
            Arg.Any<string>(),
            "group list",
            Arg.Any<int>(),
            Arg.Any<IEnumerable<string>>())
            .Returns(Task.FromException<ProcessResult>(new FileNotFoundException(exceptionMessage)));

        // Act
        var response = await command.ExecuteAsync(context, args);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(500, response.Status);
        Assert.Contains("To mitigate this issue", response.Message);
        Assert.Contains(exceptionMessage, response.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsBadRequest_WhenMissingRequiredOptions()
    {
        // Arrange
        var command = new AzCommand(_logger);
        var parser = new Parser(command.GetCommand());
        var args = parser.Parse(""); // No command specified
        var context = new CommandContext(_serviceProvider);

        // Act
        var response = await command.ExecuteAsync(context, args);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(400, response.Status);
    }

    [Fact]
    public async Task ExecuteAsync_HandlesNonJsonOutput_AndWrapsInParseOutput()
    {
        // Arrange
        var command = new AzCommand(_logger);
        var parser = new Parser(command.GetCommand());
        var args = parser.Parse("--command \"group list --query name\"");
        var context = new CommandContext(_serviceProvider);

        var nonJsonOutput = "test-rg1\ntest-rg2";

        _processService.ExecuteAsync(
            Arg.Any<string>(),
            "group list --query name",
            Arg.Any<int>(),
            Arg.Any<IEnumerable<string>>())
            .Returns(new ProcessResult(0, nonJsonOutput, string.Empty, "group list --query name"));
        var expectedJsonOutput = JsonSerializer.SerializeToElement(
            new { output = nonJsonOutput },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        _processService.ParseJsonOutput(Arg.Any<ProcessResult>())
            .Returns(expectedJsonOutput);

        // Act
        var response = await command.ExecuteAsync(context, args);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        Assert.NotNull(response.Results);
    }

    private sealed class AzResult
    {
        [JsonPropertyName("value")]
        public List<JsonElement> Value { get; set; } = new();
    }
}
