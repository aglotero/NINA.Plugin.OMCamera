using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Plugin.OMCamera.Native;
using NINA.Profile.Interfaces;

namespace NINA.Plugin.OMCamera
{
    [Export(typeof(IEquipmentProvider))]
    [Export(typeof(IEquipmentProvider<IFocuser>))]
    public class OMFocuserProvider : IEquipmentProvider<IFocuser>
    {
        private readonly IProfileService? _profileService;

        [ImportingConstructor]
        public OMFocuserProvider(IProfileService profileService)
        {
            _profileService = profileService;
        }

        public OMFocuserProvider()
        {
        }

        public string Name => "OM System / Olympus Lens Focuser";

        public string ContentId => "OMFocuserProvider";

        public IList<IFocuser> GetEquipment()
        {
            var focusers = new List<IFocuser>();
            try
            {
                if (OlyCameraNative.EnsureInitialized())
                {
                    Logger.Info("OMFocuserProvider: OlyCameraLib initialized for OM System lens focuser provider.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"OMFocuserProvider: Error initializing focuser provider: {ex.Message}");
            }

            focusers.Add(new OMFocuser(_profileService));
            return focusers;
        }
    }
}
