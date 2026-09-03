using Microsoft.Extensions.Configuration;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Config;
using Xunit;

namespace Mohist.Server.Tests.Runner.Config;

[Trait("level", "L0")]
public class CleanupPolicyOptionsTests
{
    [Fact]
    public void Default_AllFieldsNull_DisablesAllEviction()
    {
        var options = new CleanupPolicyOptions();

        Assert.Null(options.RetentionDays);
        Assert.Null(options.StorageBudgetBytes);
        Assert.Null(options.StorageTargetWatermarkBytes);
        Assert.False(options.HasAnyEnabled);
    }

    [Fact]
    public void HasAnyEnabled_OnlyRetentionConfigured_True()
    {
        var options = new CleanupPolicyOptions { RetentionDays = 7 };

        Assert.True(options.HasAnyEnabled);
    }

    [Fact]
    public void HasAnyEnabled_OnlyBudgetConfigured_True()
    {
        var options = new CleanupPolicyOptions { StorageBudgetBytes = 1_000_000L };

        Assert.True(options.HasAnyEnabled);
    }

    [Fact]
    public void HasAnyEnabled_OnlyWatermarkConfigured_True()
    {
        var options = new CleanupPolicyOptions { StorageTargetWatermarkBytes = 500_000L };

        Assert.True(options.HasAnyEnabled);
    }

    [Fact]
    public void Bind_FromMohistWorkspaceCleanupSection_PopulatesAllFields()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mohist:WorkspaceCleanup:RetentionDays"] = "30",
                ["Mohist:WorkspaceCleanup:StorageBudgetBytes"] = "1073741824",
                ["Mohist:WorkspaceCleanup:StorageTargetWatermarkBytes"] = "536870912",
            })
            .Build();

        var options = new CleanupPolicyOptions();
        config.GetSection(CleanupPolicyOptions.SectionName).Bind(options);

        Assert.Equal(30, options.RetentionDays);
        Assert.Equal(1_073_741_824L, options.StorageBudgetBytes);
        Assert.Equal(536_870_912L, options.StorageTargetWatermarkBytes);
        Assert.True(options.HasAnyEnabled);
    }

    [Fact]
    public void ToCleanupPolicyDto_DefaultOptions_ReturnsNulls()
    {
        var dto = RunnerRoutes.ToCleanupPolicyDto(new CleanupPolicyOptions());

        Assert.Null(dto.RetentionDays);
        Assert.Null(dto.StorageBudgetBytes);
        Assert.Null(dto.StorageTargetWatermarkBytes);
    }

    [Fact]
    public void ToCleanupPolicyDto_PositiveValues_Propagates()
    {
        var dto = RunnerRoutes.ToCleanupPolicyDto(new CleanupPolicyOptions
        {
            RetentionDays = 14,
            StorageBudgetBytes = 2_000_000_000L,
            StorageTargetWatermarkBytes = 1_000_000_000L,
        });

        Assert.Equal(14, dto.RetentionDays);
        Assert.Equal(2_000_000_000L, dto.StorageBudgetBytes);
        Assert.Equal(1_000_000_000L, dto.StorageTargetWatermarkBytes);
    }

    [Fact]
    public void ToCleanupPolicyDto_NonPositiveValues_NulledAsUnlimitedSentinel()
    {
        var dto = RunnerRoutes.ToCleanupPolicyDto(new CleanupPolicyOptions
        {
            RetentionDays = 0,
            StorageBudgetBytes = -1,
            StorageTargetWatermarkBytes = 0,
        });

        Assert.Null(dto.RetentionDays);
        Assert.Null(dto.StorageBudgetBytes);
        Assert.Null(dto.StorageTargetWatermarkBytes);
    }
}
