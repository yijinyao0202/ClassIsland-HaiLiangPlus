using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ClassIsland.HaiGaoAutoShutdown.Services;

public sealed class WindowsShutdownExecutor(ILogger<WindowsShutdownExecutor> logger) : IShutdownExecutor
{
    public async Task<ShutdownExecutionResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo());
            if (process is null)
            {
                return new ShutdownExecutionResult(false, "无法启动 Windows 关机命令。");
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new ShutdownExecutionResult(true, ExitCode: process.ExitCode)
                : new ShutdownExecutionResult(false, $"Windows 关机命令返回错误代码 {process.ExitCode}。", process.ExitCode);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "执行 Windows 正常关机命令失败。");
            return new ShutdownExecutionResult(false, exception.Message);
        }
    }

    public static ProcessStartInfo CreateStartInfo(string? windowsDirectory = null)
    {
        var systemDirectory = windowsDirectory is null
            ? Environment.SystemDirectory
            : Path.Combine(windowsDirectory, "System32");
        return new ProcessStartInfo
        {
            FileName = Path.Combine(systemDirectory, "shutdown.exe"),
            Arguments = "/s /t 0",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }
}
