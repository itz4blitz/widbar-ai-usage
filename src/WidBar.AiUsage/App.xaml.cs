using WidBar.SDK;
using WidBar.SDK.Hosting;

namespace WidBar.AiUsage;

// One exe hosts both widgets. WidBar names the plugin in the pipe argument
// (--widbar-pipe=widbar.<plugin id>.<instance>), which picks the plugin.
public partial class App : WidgetHostApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override IWidgetPlugin CreatePlugin() =>
        Environment.GetCommandLineArgs().Any(a => a.Contains($".{SystemPlugin.PluginId}.", StringComparison.Ordinal))
            ? new SystemPlugin()
            : new AiUsagePlugin();
}
