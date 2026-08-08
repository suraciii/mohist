using Mohist.Server.Auth.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Auth;

public sealed class DeviceFlowPolicyTests
{
    [Fact]
    public void GenerateUserCode_IsEightCharactersFromTheConfusionFreeAlphabet()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var code = DeviceFlowPolicy.GenerateUserCode();

            Assert.Equal(8, code.Length);
            foreach (var character in code)
            {
                Assert.Contains(character, DeviceFlowPolicy.UserCodeAlphabet);
                Assert.DoesNotContain(character, "IO01");
            }
        }
    }

    [Fact]
    public void NormalizeUserCode_IgnoresCaseHyphensAndSpaces()
    {
        Assert.Equal("ABCDEFGH", DeviceFlowPolicy.NormalizeUserCode("abcd-efgh"));
        Assert.Equal("ABCDEFGH", DeviceFlowPolicy.NormalizeUserCode("ABCD EFGH"));
        Assert.Equal("ABCDEFGH", DeviceFlowPolicy.NormalizeUserCode("abcdefgh"));
        Assert.Equal("ABCDEFGH", DeviceFlowPolicy.NormalizeUserCode(" aBcDeFgH-"));
    }

    [Fact]
    public void NormalizeUserCode_DropsCharactersOutsideTheAlphabet()
    {
        Assert.Equal("ABCD", DeviceFlowPolicy.NormalizeUserCode("AB!CD"));
        Assert.Equal(string.Empty, DeviceFlowPolicy.NormalizeUserCode("io01"));
        Assert.Equal(string.Empty, DeviceFlowPolicy.NormalizeUserCode(""));
        Assert.Equal(string.Empty, DeviceFlowPolicy.NormalizeUserCode("   "));
    }

    [Fact]
    public void DisplayUserCode_GroupsAsFourDashFour()
    {
        Assert.Equal("ABCD-EFGH", DeviceFlowPolicy.DisplayUserCode("ABCDEFGH"));
    }
}
