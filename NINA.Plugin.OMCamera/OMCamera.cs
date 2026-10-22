using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.OMCamera.Native;
using NINA.Profile.Interfaces;

namespace NINA.Plugin.OMCamera
{
    public class OMCamera : BaseINPC, ICamera, IDisposable
    {
        private readonly IProfileService? _profileService;
        private readonly IExposureDataFactory? _exposureDataFactory;
        private OMCameraDevice? _device;
        private CameraStates _cameraState = CameraStates.Idle;
        private double _exposureDuration = 1.0;
        private bool _isBulb = false;
        private readonly Stopwatch _exposureWatch = new Stopwatch();
        private byte[]? _lastCapturedRawBytes;
        private int _gain = 200;
        private int _gainMin = 80;
        private int _gainMax = 102400;
        private IList<int> _gains = new List<int>
        {
            80, 100, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600,
            2000, 2500, 3200, 4000, 5000, 6400, 8000, 10000, 12800, 16000,
            20000, 25600, 32000, 40000, 51200, 64000, 80000, 102400
        };
        private List<OMCameraDevice.ApertureEntry> _apertureEntries = new List<OMCameraDevice.ApertureEntry>
        {
            new OMCameraDevice.ApertureEntry(0, 0.0, "f/-- (Lens/Auto)")
        };
        private IList<string> _readoutModes = new List<string> { "f/-- (Lens/Auto)" };
        private short _readoutMode = 0;
        private bool _hasDynamicLensApertures = false;
        private long _lastPropertyPollTicks = 0;
        private bool _liveViewEnabled = false;
        private int _cameraXSize = 5220;
        private int _cameraYSize = 3912;
        private double _pixelSize = 3.34;

        public OMCamera() : this(null, null)
        {
        }

        public OMCamera(IProfileService? profileService, IExposureDataFactory? exposureDataFactory)
        {
            _profileService = profileService;
            _exposureDataFactory = exposureDataFactory;

            Id = "OM_Camera_Device";
            Name = "OM System / Olympus Camera";
            DisplayName = "OM System / Olympus Camera (Native REA)";
            Description = "Direct high-speed camera capture driver for OM System / Olympus cameras via OlyCameraLib.";

            BinningModes = new AsyncObservableCollection<BinningMode>(new[]
            {
                new BinningMode(1, 1)
            });
        }

        #region IDevice Implementation

        public string Id { get; }
        public string Name { get; private set; }
        public string DisplayName { get; private set; }
        public string Category => "Camera";
        public string Description { get; }
        public string DriverInfo => "OM System Native REA Driver v1.2";
        public string DriverVersion => "1.2.0";
        public bool HasSetupDialog => false;
        public IList<string> SupportedActions => new List<string>();

        public bool Connected
        {
            get
            {
                bool connected = _device != null && _device.IsConnected();
                if (connected && _device != null && _cameraState == CameraStates.Idle)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastPropertyPollTicks >= 2000)
                    {
                        _lastPropertyPollTicks = now;
                        int camIso = _device.GetIsoSensitivity();
                        if (camIso > 0 && camIso != _gain)
                        {
                            _gain = camIso;
                            RaisePropertyChanged(nameof(Gain));
                        }
                        PollApertureFromCamera();
                        EnsureImagingPanelApertureControl();
                    }
                }
                return connected;
            }
        }

        public Task<bool> Connect(CancellationToken token)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!OlyCameraNative.EnsureInitialized())
                    {
                        Logger.Error("OMCamera: Failed to initialize OlyCameraLib native backend.");
                        return false;
                    }

                    uint count = OlyCameraNative.GetDetectedCameraCount();
                    Logger.Info($"OMCamera: Scanning for cameras... Found {count} connected camera(s).");

                    if (count == 0)
                    {
                        Thread.Sleep(300);
                        count = OlyCameraNative.GetDetectedCameraCount();
                        Logger.Info($"OMCamera: Retry scanning for cameras... Found {count} connected camera(s).");
                    }

                    IntPtr pCam = OlyCameraNative.GetCameraInstance(0);
                    if (pCam == IntPtr.Zero)
                    {
                        Logger.Warning("OMCamera: No camera instance available at index 0. Ensure camera is powered on and in PC / Tether mode.");
                        return false;
                    }

                    _device = new OMCameraDevice(pCam);
                    if (_device.Connect())
                    {
                        string model = _device.GetModelName();
                        string sn = _device.GetSerialNumber();
                        Name = model;
                        DisplayName = $"{model} ({sn})";
                        ApplyModelSensorGeometry(model);

                        // Dynamically populate the exact supported ISO values from the camera
                        var supportedIsos = _device.GetSupportedIsoValues();
                        if (supportedIsos.Count > 0)
                        {
                            _gains = supportedIsos;
                            _gainMin = supportedIsos[0];
                            _gainMax = supportedIsos[supportedIsos.Count - 1];
                        }

                        // Sync initial ISO from camera body so NINA displays and preserves the camera's current ISO
                        _gain = _device.GetIsoSensitivity();

                        // Populate supported Apertures (F-Stop) into ReadoutModes and sync initial camera aperture
                        SyncApertureListFromCamera(forceRebuild: true);

                        _lastPropertyPollTicks = Environment.TickCount64;
                        if (_profileService?.ActiveProfile?.CameraSettings != null)
                        {
                            _profileService.ActiveProfile.CameraSettings.Gain = _gain;
                            _profileService.ActiveProfile.CameraSettings.ReadoutMode = _readoutMode;
                            _profileService.ActiveProfile.CameraSettings.ReadoutModeForSnapImages = _readoutMode;
                            _profileService.ActiveProfile.CameraSettings.ReadoutModeForNormalImages = _readoutMode;
                        }

                        double currentShutter = _device.GetShutterSpeed();
                        string currentApertureLabel = (_readoutMode >= 0 && _readoutMode < _readoutModes.Count) ? _readoutModes[_readoutMode] : "f/--";
                        Logger.Info($"OMCamera: Connected to {model} (S/N: {sn}) in PC Capture mode (ISO={_gain}, Shutter={currentShutter:F4}s, Aperture={currentApertureLabel}, SupportedISOs={_gains.Count}, SupportedApertures={_readoutModes.Count}).");
                        CameraState = CameraStates.Idle;
                        RaisePropertyChanged(nameof(Connected));
                        RaisePropertyChanged(nameof(Name));
                        RaisePropertyChanged(nameof(DisplayName));
                        RaisePropertyChanged(nameof(CameraXSize));
                        RaisePropertyChanged(nameof(CameraYSize));
                        RaisePropertyChanged(nameof(PixelSizeX));
                        RaisePropertyChanged(nameof(PixelSizeY));
                        RaisePropertyChanged(nameof(BitDepth));
                        RaisePropertyChanged(nameof(SensorType));
                        RaisePropertyChanged(nameof(BayerOffsetX));
                        RaisePropertyChanged(nameof(BayerOffsetY));
                        RaisePropertyChanged(nameof(GainMin));
                        RaisePropertyChanged(nameof(GainMax));
                        RaisePropertyChanged(nameof(Gains));
                        RaisePropertyChanged(nameof(Gain));
                        RaisePropertyChanged(nameof(ReadoutModes));
                        RaisePropertyChanged(nameof(ReadoutMode));
                        RaisePropertyChanged(nameof(ReadoutModeForSnapImages));
                        RaisePropertyChanged(nameof(ReadoutModeForNormalImages));
                        RaisePropertyChanged(nameof(ExposureMin));
                        RaisePropertyChanged(nameof(ExposureMax));
                        RaisePropertyChanged(nameof(HasBattery));
                        RaisePropertyChanged(nameof(BatteryLevel));
                        RaisePropertyChanged(nameof(HasShutter));
                        RaisePropertyChanged(nameof(CanShowLiveView));
                        RaisePropertyChanged(nameof(CanGetGain));
                        RaisePropertyChanged(nameof(CanSetGain));
                        EnsureImagingPanelApertureControl();
                        return true;
                    }
                    else
                    {
                        Logger.Error("OMCamera: Failed to establish camera session.");
                        _device.Dispose();
                        _device = null;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"OMCamera: Connect error: {ex.Message}");
                    return false;
                }
            }, token);
        }

        public void Disconnect()
        {
            try
            {
                if (_device != null)
                {
                    _device.Disconnect();
                    _device.Dispose();
                    _device = null;
                }
            }
            finally
            {
                LiveViewEnabled = false;
                CameraState = CameraStates.Idle;
                RaisePropertyChanged(nameof(Connected));
            }
        }

        public void SetupDialog() { }

        public string Action(string actionName, string actionParameters) => string.Empty;
        public string SendCommandString(string command, bool raw) => string.Empty;
        public bool SendCommandBool(string command, bool raw) => false;
        public void SendCommandBlind(string command, bool raw) { }

        #endregion

        #region ICamera Sensor & Geometry Properties

        public string SensorName => Name;
        public SensorType SensorType => SensorType.RGGB;
        public short BayerOffsetX { get; set; } = 0;
        public short BayerOffsetY { get; set; } = 0;

        public int CameraXSize => _cameraXSize;
        public int CameraYSize => _cameraYSize;
        public double PixelSizeX => _pixelSize;
        public double PixelSizeY => _pixelSize;
        public int BitDepth => 16;

        private void ApplyModelSensorGeometry(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return;

            // 16 MP generation bodies (E-M1, E-M5 Mark II, E-M10 Mark II/III)
            if (model.Equals("E-M1", StringComparison.OrdinalIgnoreCase) ||
                model.Contains("E-M5MarkII", StringComparison.OrdinalIgnoreCase) ||
                model.Contains("E-M5 Mark II", StringComparison.OrdinalIgnoreCase) ||
                model.Contains("E-M10", StringComparison.OrdinalIgnoreCase))
            {
                _cameraXSize = 4640;
                _cameraYSize = 3472;
                _pixelSize = 3.74;
            }
            else
            {
                // 20 MP generation bodies (OM-1, OM-1 Mark II, OM-5, E-M1 Mark II/III, E-M1X, E-M5 Mark III)
                _cameraXSize = 5220;
                _cameraYSize = 3912;
                _pixelSize = 3.34;
            }
        }

        public short BinX { get; set; } = 1;
        public short BinY { get; set; } = 1;
        public short MaxBinX => 1;
        public short MaxBinY => 1;
        public AsyncObservableCollection<BinningMode> BinningModes { get; }

        public void SetBinning(short x, short y)
        {
            BinX = x;
            BinY = y;
            RaisePropertyChanged(nameof(BinX));
            RaisePropertyChanged(nameof(BinY));
        }

        public bool HasShutter => true;
        public double ExposureMin => 0.000125;
        public double ExposureMax => 3600.0;

        public bool HasBattery => true;
        public int BatteryLevel => 100;

        public double Temperature => double.NaN;
        public double TemperatureSetPoint { get; set; } = double.NaN;
        public bool CanSetTemperature => false;
        public bool CoolerOn { get; set; } = false;
        public double CoolerPower => 0.0;
        public bool HasDewHeater => false;
        public bool DewHeaterOn { get; set; } = false;

        public bool CanSubSample => false;
        public bool EnableSubSample { get; set; } = false;
        public int SubSampleX { get; set; } = 0;
        public int SubSampleY { get; set; } = 0;
        public int SubSampleWidth { get; set; } = 0;
        public int SubSampleHeight { get; set; } = 0;
        public void UpdateSubSampleArea() { }

        public bool CanShowLiveView => true;
        public bool LiveViewEnabled
        {
            get => _liveViewEnabled;
            set
            {
                _liveViewEnabled = value;
                RaisePropertyChanged(nameof(LiveViewEnabled));
            }
        }

        public bool CanSetOffset => false;
        public int Offset { get; set; } = 0;
        public int OffsetMin => 0;
        public int OffsetMax => 0;

        public bool CanSetUSBLimit => false;
        public int USBLimit { get; set; } = 0;
        public int USBLimitMin => 0;
        public int USBLimitMax => 0;
        public int USBLimitStep => 1;

        public bool CanGetGain => true;
        public bool CanSetGain => true;
        public int GainMin => _gainMin;
        public int GainMax => _gainMax;

        public int Gain
        {
            get => _gain;
            set
            {
                if (_device != null)
                {
                    if (_device.SetIsoSensitivity(value, out int matchedIso))
                    {
                        _gain = matchedIso;
                    }
                    else
                    {
                        _gain = _device.GetIsoSensitivity();
                    }
                }
                else
                {
                    _gain = value;
                }
                _lastPropertyPollTicks = Environment.TickCount64;
                RaisePropertyChanged(nameof(Gain));
            }
        }

        public double ElectronsPerADU => 1.0;
        public IList<int> Gains => _gains;

        public IList<string> ReadoutModes => _readoutModes;

        public short ReadoutMode
        {
            get => _readoutMode;
            set => ApplyReadoutModeSelection(value);
        }

        public short ReadoutModeForSnapImages
        {
            get => _readoutMode;
            set => ApplyReadoutModeSelection(value);
        }

        public short ReadoutModeForNormalImages
        {
            get => _readoutMode;
            set => ApplyReadoutModeSelection(value);
        }

        private static readonly uint[] FallbackApertureCodes =
        {
            12, 14, 16, 18, 20, 22, 25, 28, 32, 35, 40, 45, 50,
            56, 63, 71, 80, 90, 100, 110, 130, 140, 160, 180, 200, 220
        };

        private void SyncApertureListFromCamera(bool forceRebuild)
        {
            if (_device == null) return;

            var supported = _device.GetSupportedApertures();
            uint currentCode = _device.GetApertureCode();

            if (supported.Count > 0 && (!_hasDynamicLensApertures || forceRebuild || supported.Count != _apertureEntries.Count))
            {
                _apertureEntries = supported;
                _hasDynamicLensApertures = true;
                var labels = new List<string>(supported.Count);
                foreach (var entry in supported)
                    labels.Add(entry.DisplayText);
                _readoutModes = labels;
                RaisePropertyChanged(nameof(ReadoutModes));
            }
            else if (supported.Count == 0 && forceRebuild)
            {
                _hasDynamicLensApertures = false;
                var entries = new List<OMCameraDevice.ApertureEntry>
                {
                    new OMCameraDevice.ApertureEntry(0, 0.0, "f/-- (Lens/Auto)")
                };
                foreach (uint code in FallbackApertureCodes)
                    entries.Add(OMCameraDevice.DecodeApertureCode(code));

                if (currentCode != 0 && !entries.Exists(e => e.Code == currentCode))
                    entries.Add(OMCameraDevice.DecodeApertureCode(currentCode));

                _apertureEntries = entries;
                var labels = new List<string>(entries.Count);
                foreach (var entry in entries)
                    labels.Add(entry.DisplayText);
                _readoutModes = labels;
                RaisePropertyChanged(nameof(ReadoutModes));
            }

            if (currentCode != 0)
            {
                int idx = FindApertureIndexByCode(currentCode);
                if (idx >= 0 && _readoutMode != (short)idx)
                {
                    _readoutMode = (short)idx;
                    RaisePropertyChanged(nameof(ReadoutMode));
                    RaisePropertyChanged(nameof(ReadoutModeForSnapImages));
                    RaisePropertyChanged(nameof(ReadoutModeForNormalImages));
                }
            }
        }

        private void PollApertureFromCamera()
        {
            if (_device == null) return;
            if (!_hasDynamicLensApertures)
            {
                SyncApertureListFromCamera(forceRebuild: false);
                if (_hasDynamicLensApertures) return;
            }

            uint currentCode = _device.GetApertureCode();
            if (currentCode != 0)
            {
                int idx = FindApertureIndexByCode(currentCode);
                if (idx >= 0 && _readoutMode != (short)idx)
                {
                    _readoutMode = (short)idx;
                    RaisePropertyChanged(nameof(ReadoutMode));
                    RaisePropertyChanged(nameof(ReadoutModeForSnapImages));
                    RaisePropertyChanged(nameof(ReadoutModeForNormalImages));
                }
            }
        }

        private int FindApertureIndexByCode(uint code)
        {
            for (int i = 0; i < _apertureEntries.Count; i++)
            {
                if (_apertureEntries[i].Code == code)
                    return i;
            }

            var target = OMCameraDevice.DecodeApertureCode(code);
            if (target.FStop <= 0) return -1;

            int bestIdx = -1;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < _apertureEntries.Count; i++)
            {
                if (_apertureEntries[i].FStop <= 0) continue;
                double diff = Math.Abs(_apertureEntries[i].FStop - target.FStop);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        private void ApplyReadoutModeSelection(short value)
        {
            if (value < 0 || value >= _apertureEntries.Count)
                return;

            _readoutMode = value;
            uint targetCode = _apertureEntries[value].Code;
            if (_device != null && targetCode != 0)
            {
                if (_device.SetApertureCode(targetCode, out uint matchedCode) && matchedCode != 0)
                {
                    int matchedIdx = FindApertureIndexByCode(matchedCode);
                    if (matchedIdx >= 0)
                        _readoutMode = (short)matchedIdx;
                }
                else
                {
                    uint currentCode = _device.GetApertureCode();
                    if (currentCode != 0)
                    {
                        int currentIdx = FindApertureIndexByCode(currentCode);
                        if (currentIdx >= 0)
                            _readoutMode = (short)currentIdx;
                    }
                }
            }

            _lastPropertyPollTicks = Environment.TickCount64;
            RaisePropertyChanged(nameof(ReadoutMode));
            RaisePropertyChanged(nameof(ReadoutModeForSnapImages));
            RaisePropertyChanged(nameof(ReadoutModeForNormalImages));
        }

        private void EnsureImagingPanelApertureControl()
        {
            try
            {
                var app = Application.Current;
                if (app?.Dispatcher == null) return;

                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        foreach (Window win in app.Windows)
                        {
                            InjectApertureIntoSnapshotPanel(win);
                        }
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void InjectApertureIntoSnapshotPanel(DependencyObject root)
        {
            if (root == null) return;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is StackPanel sp &&
                    sp.Orientation == Orientation.Vertical &&
                    sp.DataContext != null &&
                    sp.DataContext.GetType().Name.Contains("SnapshotVM", StringComparison.OrdinalIgnoreCase) &&
                    sp.Children.Count >= 5)
                {
                    foreach (UIElement existing in sp.Children)
                    {
                        if (existing is Border existingBorder && (existingBorder.Tag as string) == "OMCamera_ApertureRow")
                        {
                            if (!ReferenceEquals(existingBorder.DataContext, this))
                            {
                                existingBorder.DataContext = this;
                            }
                            return;
                        }
                    }

                    var border = new Border
                    {
                        Tag = "OMCamera_ApertureRow",
                        MinHeight = 30,
                        BorderThickness = new Thickness(0),
                        DataContext = this
                    };
                    border.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(Connected))
                    {
                        Converter = new BooleanToVisibilityConverter()
                    });

                    var grid = new UniformGrid
                    {
                        Columns = 2,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var label = new TextBlock
                    {
                        Text = "Aperture",
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    var combo = new ComboBox
                    {
                        Height = 25,
                        Margin = new Thickness(5, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ReadoutModes))
                    {
                        Mode = BindingMode.OneWay
                    });
                    combo.SetBinding(Selector.SelectedIndexProperty, new Binding(nameof(ReadoutMode))
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    });

                    grid.Children.Add(label);
                    grid.Children.Add(combo);
                    border.Child = grid;

                    int insertIdx = Math.Min(4, sp.Children.Count);
                    sp.Children.Insert(insertIdx, border);
                    Logger.Info("OMCamera: Injected Aperture control row directly into NINA Imaging Snapshot panel.");
                    return;
                }

                InjectApertureIntoSnapshotPanel(child);
            }
        }

        public CameraStates CameraState
        {
            get => _cameraState;
            set
            {
                _cameraState = value;
                RaisePropertyChanged(nameof(CameraState));
            }
        }

        #endregion

        #region Exposure Execution

        public void StartExposure(CaptureSequence sequence)
        {
            if (_device == null)
                throw new InvalidOperationException("Camera is not connected.");

            _exposureDuration = sequence.ExposureTime;
            _lastCapturedRawBytes = null;

            // Apply requested ISO (Gain) to camera and sync _gain to exact camera value
            if (sequence.Gain > 0)
            {
                _gain = sequence.Gain;
            }
            if (_device.SetIsoSensitivity(_gain, out int matchedIso))
            {
                if (_gain != matchedIso)
                {
                    _gain = matchedIso;
                    RaisePropertyChanged(nameof(Gain));
                }
            }
            else
            {
                int currentIso = _device.GetIsoSensitivity();
                if (currentIso > 0 && _gain != currentIso)
                {
                    _gain = currentIso;
                    RaisePropertyChanged(nameof(Gain));
                }
            }

            // Apply requested Aperture (ReadoutMode) if a specific f-stop is selected
            if (_readoutMode >= 0 && _readoutMode < _apertureEntries.Count)
            {
                uint targetApertureCode = _apertureEntries[_readoutMode].Code;
                if (targetApertureCode != 0)
                {
                    if (_device.SetApertureCode(targetApertureCode, out uint matchedApertureCode) && matchedApertureCode != 0)
                    {
                        int matchedIdx = FindApertureIndexByCode(matchedApertureCode);
                        if (matchedIdx >= 0 && _readoutMode != (short)matchedIdx)
                        {
                            _readoutMode = (short)matchedIdx;
                            RaisePropertyChanged(nameof(ReadoutMode));
                            RaisePropertyChanged(nameof(ReadoutModeForSnapImages));
                            RaisePropertyChanged(nameof(ReadoutModeForNormalImages));
                        }
                    }
                }
            }

            // Check camera's current shutter action type via Slot 42 (GetActionType):
            // 1 = Single/Timed shutter speed, 2 = Bulb/LiveTime, 3 = Continuous
            uint actionType = _device.GetActionType();
            _isBulb = (actionType == OlyCameraNative.ACTION_TYPE_BULB);

            // Apply requested Shutter Speed (Exposure Time) when in Timed Exposure mode
            double matchedShutter = _exposureDuration;
            if (!_isBulb && _exposureDuration > 0)
            {
                if (_device.SetShutterSpeed(_exposureDuration, out double camSec) && camSec > 0)
                {
                    matchedShutter = camSec;
                }
                // Re-query actionType in case setting shutter speed switched to/from Bulb
                actionType = _device.GetActionType();
                _isBulb = (actionType == OlyCameraNative.ACTION_TYPE_BULB);
                if (!_isBulb)
                {
                    _exposureDuration = matchedShutter;
                }
            }

            _device.PrepareForCapture();

            string apertureText = (_readoutMode >= 0 && _readoutMode < _readoutModes.Count) ? _readoutModes[_readoutMode] : "f/--";
            Logger.Info($"OMCamera: StartExposure requested ({sequence.ExposureTime}s -> {matchedShutter:F4}s, ISO={_gain}, Aperture={apertureText}), camera actionType={actionType} (isBulb={_isBulb}).");

            CameraState = CameraStates.Exposing;
            _exposureWatch.Restart();

            if (_isBulb)
            {
                bool ok = _device.StartBulbExposure();
                if (!ok)
                {
                    CameraState = CameraStates.Error;
                    throw new InvalidOperationException("Failed to open camera bulb shutter.");
                }
            }
            else
            {
                bool ok = _device.TriggerTimedExposure();
                if (!ok)
                {
                    CameraState = CameraStates.Error;
                    throw new InvalidOperationException("Failed to trigger camera exposure.");
                }
            }
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token)
        {
            if (_device == null) return;

            if (_isBulb)
            {
                long targetMs = (long)(_exposureDuration * 1000.0);
                while (_exposureWatch.ElapsedMilliseconds < targetMs)
                {
                    token.ThrowIfCancellationRequested();
                    long remaining = targetMs - _exposureWatch.ElapsedMilliseconds;
                    await Task.Delay((int)Math.Min(remaining, 50), token);
                }

                _device.StopBulbExposure();
            }

            // Wait while camera is still exposing
            while (_device.IsTakingPicture())
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(50, token);
            }

            _exposureWatch.Stop();
        }

        public void StopExposure()
        {
            if (_device != null && _isBulb)
            {
                _device.StopBulbExposure();
            }
            CameraState = CameraStates.Idle;
        }

        public void AbortExposure()
        {
            StopExposure();
        }

        public async Task<IExposureData> DownloadExposure(CancellationToken token)
        {
            if (_device == null)
                throw new InvalidOperationException("Camera not connected.");

            CameraState = CameraStates.Download;

            try
            {
                _lastCapturedRawBytes = await Task.Run(() => _device.WaitForCapturedImage(token, 30000), token);
                if (_lastCapturedRawBytes == null || _lastCapturedRawBytes.Length == 0)
                {
                    CameraState = CameraStates.Error;
                    throw new InvalidOperationException("Failed to download image data from OM System camera.");
                }

                CameraState = CameraStates.Idle;

                var meta = CreateMetaData("LIGHT", _exposureDuration);
                return await CreateExposureDataFromBytes(_lastCapturedRawBytes, meta, token);
            }
            catch
            {
                CameraState = CameraStates.Error;
                throw;
            }
        }

        public void StartLiveView(CaptureSequence sequence)
        {
            if (_device == null) return;
            if (_device.StartLiveView())
            {
                LiveViewEnabled = true;
            }
        }

        public async Task<IExposureData> DownloadLiveView(CancellationToken token)
        {
            if (_device == null)
                throw new InvalidOperationException("Camera not connected.");

            byte[]? jpegBytes = await Task.Run(() => _device.GetNextLiveViewFrame(token, 3000), token);
            if (jpegBytes == null || jpegBytes.Length == 0)
            {
                throw new InvalidOperationException("No LiveView frame available from camera.");
            }

            var meta = CreateMetaData("SNAPSHOT", 0.033);
            return await DecodeBitmapExposureData(jpegBytes, meta);
        }

        public void StopLiveView()
        {
            try
            {
                _device?.StopLiveView();
            }
            finally
            {
                LiveViewEnabled = false;
            }
        }

        private ImageMetaData CreateMetaData(string imageType, double exposureTime)
        {
            var meta = new ImageMetaData();
            meta.Camera.Name = Name;
            meta.Camera.BinX = BinX;
            meta.Camera.BinY = BinY;
            meta.Camera.PixelSize = PixelSizeX;
            meta.Camera.Gain = Gain;
            meta.Camera.ElectronsPerADU = ElectronsPerADU;
            meta.Camera.SensorType = SensorType;
            meta.Camera.BayerOffsetX = BayerOffsetX;
            meta.Camera.BayerOffsetY = BayerOffsetY;

            meta.Image.ExposureTime = exposureTime;
            meta.Image.Binning = $"{BinX}x{BinY}";
            meta.Image.ExposureStart = DateTime.UtcNow;
            meta.Image.ImageType = imageType;
            return meta;
        }

        internal Func<RawConverterEnum, byte[], ImageMetaData, CancellationToken, Task<IImageData?>>? RawDecoderOverride { get; set; }

        internal async Task<IExposureData> CreateExposureDataFromBytes(byte[] rawBytes, ImageMetaData meta, CancellationToken token)
        {
            // Enforce that still/calibration exposures only accept valid .ORF RAW files (never JPEG previews or raw file bytes).
            OrfRawProcessor.ValidateOrfBuffer(rawBytes);

            if (_exposureDataFactory == null)
            {
                throw new InvalidOperationException("Unable to decode RAW exposure: IExposureDataFactory is unavailable.");
            }

            int sensorBitDepth = OrfRawProcessor.GetDefinedSensorBitDepth(rawBytes);
            byte[] patchedOrf = OrfRawProcessor.PatchOrfHeaderForLegacyLibRaw(rawBytes);
            Exception? lastException = null;

            foreach (var converter in new[] { RawConverterEnum.FREEIMAGE, RawConverterEnum.DCRAW })
            {
                try
                {
                    Logger.Info($"OMCamera: Decoding {sensorBitDepth}-bit ORF RAW ({patchedOrf.Length} bytes) via {converter}...");
                    IImageData? imgData;
                    if (RawDecoderOverride != null)
                    {
                        imgData = await RawDecoderOverride(converter, patchedOrf, meta, token);
                    }
                    else
                    {
                        var rawExp = _exposureDataFactory.CreateRAWExposureData(converter, patchedOrf, "orf", OrfRawProcessor.OutputBitDepth, meta);
                        if (rawExp == null)
                        {
                            continue;
                        }
                        imgData = await rawExp.ToImageData(null, token);
                    }

                    ushort[]? flat = imgData?.Data?.FlatArray;
                    int width = imgData?.Properties?.Width ?? 0;
                    int height = imgData?.Properties?.Height ?? 0;

                    if (imgData != null && width > 0 && height > 0 && flat != null && flat.Length == width * height)
                    {
                        if (_cameraXSize != width || _cameraYSize != height)
                        {
                            _cameraXSize = width;
                            _cameraYSize = height;
                            RaisePropertyChanged(nameof(CameraXSize));
                            RaisePropertyChanged(nameof(CameraYSize));
                        }

                        // Deterministically scale from the decoder's defined linear sensor range (e.g. 12-bit 0..4095 -> 16-bit 0..65520)
                        // without sampling image brightness, so bias, darks, flats, and lights receive identical scaling.
                        ushort[] scaled = OrfRawProcessor.ScaleDecodedRawPixelsTo16Bit(flat, sensorBitDepth);
                        Logger.Info($"OMCamera: Successfully decoded {sensorBitDepth}-bit ORF RAW with {converter} ({width}x{height}) and scaled deterministically (<< {OrfRawProcessor.OutputBitDepth - sensorBitDepth}) to 16-bit range.");
                        return _exposureDataFactory.CreateImageArrayExposureData(scaled, width, height, OrfRawProcessor.OutputBitDepth, true, meta);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Logger.Warning($"OMCamera: RAW converter {converter} failed on ORF: {ex.Message}");
                }
            }

            throw new InvalidOperationException(
                "Camera exposure failed: RAW converters (FreeImage / DCRaw) could not decode valid pixel data from the .ORF buffer.",
                lastException);
        }

        private async Task<IExposureData> DecodeBitmapExposureData(byte[] imageBytes, ImageMetaData meta)
        {
            using var ms = new MemoryStream(imageBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame.Freeze();

            if (_exposureDataFactory != null)
            {
                try
                {
                    // Convert to Bgr24 (instead of Gray16 which applies a destructive 2.2 gamma crush that causes MAD=0.00 in NINA Auto-Stretch)
                    var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
                    bgr.Freeze();
                    int w = bgr.PixelWidth;
                    int h = bgr.PixelHeight;
                    int stride = w * 3;
                    byte[] rgbBytes = new byte[stride * h];
                    bgr.CopyPixels(rgbBytes, stride, 0);

                    ushort[] pixels = new ushort[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        int rowByteIdx = y * stride;
                        int rowPixIdx = y * w;
                        bool evenRow = (y & 1) == 0;
                        for (int x = 0; x < w; x++)
                        {
                            int b = rgbBytes[rowByteIdx];
                            int g = rgbBytes[rowByteIdx + 1];
                            int r = rgbBytes[rowByteIdx + 2];
                            rowByteIdx += 3;

                            // Encode 8-bit RGB into a 16-bit RGGB Bayer pattern (0..65535) + tiny sub-LSB dither (0..15)
                            // so NINA's Debayer pipeline renders full-color LiveView frames without MAD=0.00 collapse
                            int channelVal = evenRow
                                ? (((x & 1) == 0) ? r : g)
                                : (((x & 1) == 0) ? g : b);

                            int scaled = (channelVal * 257) + ((x * 7 + y * 13) & 0x0F);
                            pixels[rowPixIdx + x] = (ushort)(scaled > 65535 ? 65535 : scaled);
                        }
                    }

                    meta.Camera.SensorType = SensorType.RGGB;
                    meta.Camera.BayerOffsetX = 0;
                    meta.Camera.BayerOffsetY = 0;
                    return _exposureDataFactory.CreateImageArrayExposureData(pixels, w, h, 16, true, meta);
                }
                catch
                {
                    return await _exposureDataFactory.CreateImageArrayExposureDataFromBitmapSource(frame);
                }
            }

            throw new InvalidOperationException("IExposureDataFactory is not initialized.");
        }

        #endregion

        #region Focus Pulse Motor Control (Electronic AF)

        public bool MoveFocusPulses(int pulses)
        {
            return _device != null && _device.SetFocusStep(pulses);
        }

        #endregion

        public void Dispose()
        {
            Disconnect();
        }
    }
}
