using System;
using System.Collections.Generic;
using System.Reflection;
using VideoOS.Platform;
using VideoOS.Platform.Background;
using VideoOS.Platform.Client;

namespace FisheyeDewarp
{
    public sealed class FisheyeDewarpDefinition : PluginDefinition
    {
        private readonly List<BackgroundPlugin> _backgroundPlugins = new List<BackgroundPlugin>();
        private readonly List<ViewItemToolbarPlugin> _toolbarPlugins = new List<ViewItemToolbarPlugin>();

        public override Guid Id => new Guid("9E2A4B6C-3D1F-4A8E-B5C7-1F0E2D3C4B5A");

        public override string Name => "Fisheye Dewarp";

        public override string Manufacturer => "Continental Computers";

        public override string VersionString =>
            typeof(FisheyeDewarpDefinition).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

        public override System.Drawing.Image Icon => null;

        public override void Init()
        {
            if (EnvironmentManager.Instance.EnvironmentType != EnvironmentType.SmartClient) return;
            Log.Info($"Plugin loading. Smart Client environment, CLR {Environment.Version}");
            _backgroundPlugins.Add(new DewarpBackgroundPlugin());
            _toolbarPlugins.Add(new DewarpToolbarPlugin(ToolbarKind.Dewarp));
            _toolbarPlugins.Add(new DewarpToolbarPlugin(ToolbarKind.Snapshot));
            _toolbarPlugins.Add(new DewarpToolbarPlugin(ToolbarKind.Export));
        }

        public override void Close()
        {
            _backgroundPlugins.Clear();
            _toolbarPlugins.Clear();
        }

        public override List<BackgroundPlugin> BackgroundPlugins => _backgroundPlugins;

        public override List<ViewItemToolbarPlugin> ViewItemToolbarPlugins => _toolbarPlugins;
    }
}
