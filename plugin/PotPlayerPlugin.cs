using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;

namespace Jellyfin.Plugin.PotPlayerLauncher;

/// <summary>
/// 插件注册入口. 继承 BasePlugin 并手动完成属性初始化
/// (非泛型 BasePlugin 不会自动调用 SetAttributes,必须手动补上,否则服务器创建实例时会 NRE).
/// </summary>
public class PotPlayerPlugin : BasePlugin
{
    public const string PluginGuid = "7f5b1d4a-3c2e-4f8b-9a6d-1e0c5b8d2f31";

    public PotPlayerPlugin(IApplicationPaths applicationPaths)
    {
        var assembly = GetType().Assembly;
        SetAttributes(
            assembly.Location,
            Path.Combine(applicationPaths.PluginsPath, "PotPlayerLauncher"),
            assembly.GetName().Version ?? new Version(1, 0, 0, 0));
        SetId(Guid.Parse(PluginGuid));

        ApplicationPaths = applicationPaths;
        Instance = this;
    }

    public static PotPlayerPlugin? Instance { get; private set; }

    public IApplicationPaths ApplicationPaths { get; }

    public override string Name => "PotPlayer Launcher";

    public override string Description => "Provides a local HTTP endpoint (127.0.0.1:13579) that launches PotPlayer to play media files directly. Lifecycle is bound to the Jellyfin server.";
}
