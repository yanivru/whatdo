using WhatDo.StatusService.Status;
using Xunit;

namespace WhatDo.StatusService.Tests;

public class InstanceIdFactoryTests
{
    [Theory]
    [InlineData(@"C:\Users\me\source\repos\whatdo", "whatdo")]
    [InlineData("/home/user/My Cool Repo", "my-cool-repo")]
    [InlineData(@"C:\repos\whatdo\", "whatdo")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void CreateName_ReturnsSanitizedFolderName(string? cwd, string expected)
    {
        var name = InstanceIdFactory.CreateName(cwd);

        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("a1b2c3d4e5f6", "a1b2c3d4")]
    [InlineData("short", "short")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void CreateShortSessionId_TruncatesToEightChars(string? sessionId, string expected)
    {
        var shortId = InstanceIdFactory.CreateShortSessionId(sessionId);

        Assert.Equal(expected, shortId);
    }

    [Fact]
    public void CreateInstanceId_CombinesNameAndShortSessionId()
    {
        var instanceId = InstanceIdFactory.CreateInstanceId(@"C:\repos\whatdo", "a1b2c3d4e5f6");

        Assert.Equal("whatdo-a1b2c3d4", instanceId);
    }
}
