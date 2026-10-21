using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Plugin.OMCamera.Native;
using NINA.Profile.Interfaces;

namespace NINA.Plugin.OMCamera
{
    [Export(typeof(IEquipmentProvider))]
    [Export(typeof(IEquipmentProvider<ICamera>))]
    public class OMCameraProvider : IEquipmentProvider<ICamera>
    {
        private readonly IProfileService? _profileService;
        private readonly IExposureDataFactory? _exposureDataFactory;

        [ImportingConstructor]
        public OMCameraProvider(IProfileService profileService, IExposureDataFactory exposureDataFactory)
        {
            _profileService = profileService;
            _exposureDataFactory = exposureDataFactory;
        }

        public OMCameraProvider()
        {
        }

        public string Name => "OM System / Olympus Camera";

        public string ContentId => "OMCameraProvider";

        public IList<ICamera> GetEquipment()
        {
            var cameras = new List<ICamera>();
            try
            {
                if (OlyCameraNative.EnsureInitialized())
                {
                    uint count = OlyCameraNative.GetDetectedCameraCount();
                    Logger.Info($"OMCameraProvider: GetEquipment detected {count} OM System camera(s).");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraProvider: Error scanning cameras: {ex.Message}");
            }

            cameras.Add(new OMCamera(_profileService, _exposureDataFactory));
            return cameras;
        }
    }
}
