using System;
using System.Threading.Tasks;
using Foundatio.Testing;
using Xunit;

namespace Foundatio.Tests.Testing;

public class TestContextTests
{
    [Fact]
    public async Task TestMessageContext_ReportProgressAsync_RecordsEveryReportAndKeepsPreviousValuesForNulls()
    {
        var context = new TestMessageContext();

        await context.ReportProgressAsync(25, "copying", TestContext.Current.CancellationToken);
        await context.ReportProgressAsync(message: "verifying", cancellationToken: TestContext.Current.CancellationToken);
        await context.ReportProgressAsync(100, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([(25, "copying"), (null, "verifying"), (100, null)], context.ProgressReports);
        Assert.Equal(100, context.Progress);
        Assert.Equal("verifying", context.ProgressMessage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task TestMessageContext_ReportProgressAsync_WithAPercentOutOfRange_Throws(int percent)
    {
        var context = new TestMessageContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("percent", () => context.ReportProgressAsync(percent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(context.ProgressReports);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task TestJobContext_ReportProgressAsync_WithAPercentOutOfRange_Throws(int percent)
    {
        var context = new TestJobContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("percent", () => context.ReportProgressAsync(percent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(context.ProgressReports);
    }
}
