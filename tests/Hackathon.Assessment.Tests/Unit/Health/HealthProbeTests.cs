using System.Net;
using Hackathon.Assessment.Api.Health;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Health;

public sealed class HealthProbeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    public async Task ReturnsExpectedExitCode(HttpStatusCode status, int expected)
    {
        using var handler = new FakeHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(status)));

        Assert.Equal(expected, await HealthProbe.RunAsync(handler, CancellationToken.None));
    }

    [Fact]
    public async Task TimeoutReturnsFailure()
    {
        using var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.Equal(1, await HealthProbe.RunAsync(handler, CancellationToken.None));
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
