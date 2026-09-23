using System.Diagnostics;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit;

public sealed class SmokeTests
{
    [Fact]
    public async Task MinimalWebApplicationStarts()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Hackathon.Assessment.Api.dll");
        Assert.True(File.Exists(assembly), $"Missing application assembly: {assembly}");

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the API.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    throw new InvalidOperationException(
                        $"API exited before listening: {await process.StandardError.ReadToEndAsync(timeout.Token)}");
                }
                if (line.Contains("Now listening on:", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            await process.WaitForExitAsync();
        }
    }
}
