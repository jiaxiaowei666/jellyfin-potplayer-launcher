using System;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PotPlayerLauncher;

/// <summary>
/// 向 Jellyfin 宿主注册本插件的托管服务. 服务器启动时自动实例化, 关闭时自动停止.
/// 注意:此接口要求无参构造函数.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // 监听器要用 HttpClient 回调 Jellyfin 自身 API (回传播放进度 / 校验 token)
        serviceCollection.AddHttpClient("PotPlayerLauncher", client =>
        {
            var baseUrl = applicationHost.GetApiUrlForLocalAccess();
            if (!baseUrl.EndsWith('/'))
            {
                baseUrl += "/";
            }

            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        serviceCollection.AddHostedService<PotPlayerListener>();
    }
}
