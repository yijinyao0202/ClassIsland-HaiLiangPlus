using ClassIsland.HaiGaoAutoShutdown.Services;

namespace ClassIsland.HaiGaoAutoShutdown.Tests;

public sealed class WindowsShutdownExecutorTests
{
    [Fact]
    public void StartInfo_UsesNormalShutdownWithoutForceFlag()
    {
        var startInfo = WindowsShutdownExecutor.CreateStartInfo("C:\\Windows");

        Assert.Equal("C:\\Windows\\System32\\shutdown.exe", startInfo.FileName);
        Assert.Equal("/s /t 0", startInfo.Arguments);
        Assert.DoesNotContain("/f", startInfo.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }
}
