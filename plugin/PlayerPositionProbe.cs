using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Jellyfin.Plugin.PotPlayerLauncher;

/// <summary>
/// 实时读取播放位置的能力。默认实现见 <see cref="PotPlayerWindowProbe"/> (Windows 窗口消息),
/// 测试可替换成假实现。
/// </summary>
public interface IPlayerPositionProbe
{
    /// <summary>
    /// 查询播放器当前播放位置; 查不到返回 null(不要抛异常)。
    /// </summary>
    /// <param name="playerProcessId">播放器进程 id。</param>
    long? TryGetPositionSeconds(int playerProcessId);
}

/// <summary>
/// 决定"这一次要不要采用探针读数"的纯逻辑, 与 P/Invoke 分离以便单元测试。
/// </summary>
public static class PlayerPositionPolicy
{
    /// <summary>探针读数最多可以比"进程存活时长"超出多少秒(超出视为读到别的实例/异常值)。</summary>
    public const int MaxAheadSeconds = 120;

    /// <summary>
    /// 判断探针读数是否可信。
    /// 可信条件: 非空、非负, 且不超过"起播位置 + 存活时长 + 余量"。
    /// 播放器多开时进程 id 可能对上别人的窗口, 这条上限用来挡掉那种情况。
    /// </summary>
    public static bool IsTrustworthy(long? probeSeconds, long startSeconds, long elapsedSeconds)
    {
        if (probeSeconds is not { } probe)
        {
            return false;
        }

        if (probe < 0)
        {
            return false;
        }

        var ceiling = startSeconds + elapsedSeconds + MaxAheadSeconds;
        return probe <= ceiling;
    }
}

/// <summary>
/// 通过 Windows 窗口消息向 PotPlayer 索取实时播放位置。
/// PotPlayer 没有 IPC/HTTP 之类的查询接口, 但它的主窗口(窗口类 <c>PotPlayer64</c>)响应
/// <c>WM_USER(0x400)</c> + wParam <c>0x5004</c>, 返回当前时间码(毫秒)。
/// 读取失败(换版本/权限/窗口不在)时返回 null, 调用方退回"进程存活时长"估算。
/// </summary>
public sealed class PotPlayerWindowProbe : IPlayerPositionProbe
{
    private const string WindowClassName = "PotPlayer64";
    private const uint WmUser = 0x0400;
    private const int QueryCurrentTimeMs = 0x5004;

    private readonly Action<string>? _trace;

    public PotPlayerWindowProbe(Action<string>? trace = null)
    {
        _trace = trace;
    }

    public long? TryGetPositionSeconds(int playerProcessId)
    {
        if (!OperatingSystem.IsWindows() || playerProcessId <= 0)
        {
            return null;
        }

        try
        {
            var hwnd = FindMainWindow(playerProcessId);
            if (hwnd == IntPtr.Zero)
            {
                _trace?.Invoke($"no {WindowClassName} window for pid {playerProcessId}");
                return null;
            }

            var milliseconds = SendMessageW(hwnd, WmUser, (IntPtr)QueryCurrentTimeMs, (IntPtr)1).ToInt64();
            if (milliseconds <= 0)
            {
                return null;
            }

            return milliseconds / 1000;
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"probe failed: {ex.Message}");
            return null;
        }
    }

    private static IntPtr FindMainWindow(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows(
            (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var windowProcessId);
                if (windowProcessId != (uint)processId)
                {
                    return true;
                }

                var className = new StringBuilder(256);
                GetClassNameW(hwnd, className, className.Capacity);
                if (className.ToString() == WindowClassName)
                {
                    found = hwnd;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
