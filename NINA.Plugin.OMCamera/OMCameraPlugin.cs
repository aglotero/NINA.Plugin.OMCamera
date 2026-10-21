using System.ComponentModel.Composition;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

namespace NINA.Plugin.OMCamera
{
    [Export(typeof(IPluginManifest))]
    public class OMCameraPlugin : PluginBase
    {
        [ImportingConstructor]
        public OMCameraPlugin()
        {
        }
    }
}
