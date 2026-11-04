using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Plugin.OMCamera.Native;
using NINA.Profile.Interfaces;

namespace NINA.Plugin.OMCamera
{
    public class OMFocuser : BaseINPC, IFocuser, IDisposable
    {
        public const int DefaultInitialPosition = 50000;
        public const int DefaultMaxStep = 100000;
        public const int DefaultMaxIncrement = 5000;
        public const int MaxPulsesPerNativeCall = 1000;

        private readonly IProfileService? _profileService;
        private readonly Func<int, bool>? _customStepExecutor;
        private readonly Action? _customHaltExecutor;
        private OMCameraDevice? _device;
        private CancellationTokenSource? _moveCts;
        private readonly object _moveLock = new object();

        private int _position = DefaultInitialPosition;
        private bool _isMoving = false;
        private bool _disposed = false;

        public OMFocuser() : this(null)
        {
        }

        public OMFocuser(IProfileService? profileService)
        {
            _profileService = profileService;
            Id = "OM_Focuser_Device";
            Name = "OM System / Olympus Lens Focuser";
            DisplayName = "OM System / Olympus Lens Focuser (Native REA)";
            Description = "Electronic Micro Four Thirds lens focus driver for OM System / Olympus cameras via OlyCameraLib.";
        }

        public OMFocuser(Func<int, bool> customStepExecutor, Action? customHaltExecutor = null, int initialPosition = DefaultInitialPosition)
            : this(null)
        {
            _customStepExecutor = customStepExecutor ?? throw new ArgumentNullException(nameof(customStepExecutor));
            _customHaltExecutor = customHaltExecutor;
            _position = Math.Clamp(initialPosition, 0, DefaultMaxStep);
        }

        #region IDevice Implementation

        public string Id { get; }
        public string Name { get; private set; }
        public string DisplayName { get; private set; }
        public string Category => "Focuser";
        public string Description { get; }
        public string DriverInfo => "OM System Native REA Focuser Driver v1.2";
        public string DriverVersion => "1.2.0";
        public bool HasSetupDialog => false;
        public IList<string> SupportedActions => new List<string> { "ResetPosition", "UnlockAutoFocus" };

        public bool Connected => _customStepExecutor != null || (_device != null && _device.IsConnected());

        public Task<bool> Connect(CancellationToken token)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (_customStepExecutor != null)
                    {
                        RaisePropertyChanged(nameof(Connected));
                        return true;
                    }

                    if (_device != null && _device.IsConnected())
                    {
                        return true;
                    }

                    _device = OMCameraDevice.AcquireSharedSession();
                    if (_device != null && _device.IsConnected())
                    {
                        string model = _device.GetModelName();
                        string sn = _device.GetSerialNumber();
                        Name = $"{model} Lens Focuser";
                        DisplayName = $"{model} Lens Focuser ({sn})";

                        double currentShutter = _device.GetShutterSpeed();
                        if (currentShutter > 0 && _profileService?.ActiveProfile != null)
                        {
                            if (_profileService.ActiveProfile.FocuserSettings != null)
                            {
                                _profileService.ActiveProfile.FocuserSettings.AutoFocusExposureTime = currentShutter;
                            }
                            if (_profileService.ActiveProfile.SnapShotControlSettings != null)
                            {
                                _profileService.ActiveProfile.SnapShotControlSettings.ExposureDuration = currentShutter;
                            }
                        }

                        Logger.Info($"OMFocuser: Connected to {model} (S/N: {sn}) electronic lens focuser at Position={_position} (Synced AutoFocusExposureTime={currentShutter:F4}s).");
                        RaisePropertyChanged(nameof(Connected));
                        RaisePropertyChanged(nameof(Name));
                        RaisePropertyChanged(nameof(DisplayName));
                        RaisePropertyChanged(nameof(Position));
                        RaisePropertyChanged(nameof(MaxStep));
                        RaisePropertyChanged(nameof(MaxIncrement));
                        RaisePropertyChanged(nameof(StepSize));
                        return true;
                    }

                    Logger.Error("OMFocuser: Failed to acquire shared OM camera session for lens focus control.");
                    _device = null;
                    return false;
                }
                catch (Exception ex)
                {
                    Logger.Error($"OMFocuser: Connect error: {ex.Message}");
                    return false;
                }
            }, token);
        }

        public void Disconnect()
        {
            try
            {
                Halt();
                if (_device != null)
                {
                    var dev = _device;
                    _device = null;
                    OMCameraDevice.ReleaseSharedSession(dev);
                }
            }
            finally
            {
                IsMoving = false;
                RaisePropertyChanged(nameof(Connected));
            }
        }

        public void SetupDialog() { }

        public string Action(string actionName, string actionParameters)
        {
            if (string.Equals(actionName, "ResetPosition", StringComparison.OrdinalIgnoreCase))
            {
                int newPos = DefaultInitialPosition;
                if (!string.IsNullOrWhiteSpace(actionParameters) && int.TryParse(actionParameters.Trim(), out int parsed))
                {
                    newPos = Math.Clamp(parsed, 0, MaxStep);
                }
                Position = newPos;
                return Position.ToString();
            }

            if (string.Equals(actionName, "UnlockAutoFocus", StringComparison.OrdinalIgnoreCase))
            {
                bool ok = _device != null && _device.UnlockAutoFocus();
                return ok ? "OK" : "FAILED";
            }

            return string.Empty;
        }

        public string SendCommandString(string command, bool raw) => string.Empty;
        public bool SendCommandBool(string command, bool raw) => false;
        public void SendCommandBlind(string command, bool raw) { }

        #endregion

        #region IFocuser Implementation

        public bool IsMoving
        {
            get => _isMoving;
            private set
            {
                if (_isMoving != value)
                {
                    _isMoving = value;
                    RaisePropertyChanged();
                }
            }
        }

        public int MaxIncrement => DefaultMaxIncrement;

        public int MaxStep => DefaultMaxStep;

        public int Position
        {
            get => _position;
            private set
            {
                if (_position != value)
                {
                    _position = value;
                    RaisePropertyChanged();
                }
            }
        }

        public double StepSize => 1.0;

        public bool TempCompAvailable => false;

        public bool TempComp
        {
            get => false;
            set { }
        }

        public double Temperature => double.NaN;

        public async Task Move(int position, CancellationToken ct, int waitInMs = 1000)
        {
            if (!Connected)
            {
                throw new InvalidOperationException("OMFocuser is not connected.");
            }

            int targetPosition = Math.Clamp(position, 0, MaxStep);
            int totalDelta = targetPosition - Position;
            if (totalDelta == 0)
            {
                return;
            }

            CancellationTokenSource linkedCts;
            lock (_moveLock)
            {
                _moveCts?.Cancel();
                _moveCts?.Dispose();
                _moveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts = _moveCts;
            }

            IsMoving = true;
            try
            {
                await Task.Run(async () =>
                {
                    int remaining = targetPosition - Position;
                    while (remaining != 0)
                    {
                        linkedCts.Token.ThrowIfCancellationRequested();

                        int sign = remaining > 0 ? 1 : -1;
                        int stepMagnitude = Math.Min(Math.Abs(remaining), Math.Min(MaxIncrement, MaxPulsesPerNativeCall));
                        int stepDelta = sign * stepMagnitude;

                        bool moved = ExecuteFocusStep(stepDelta);
                        if (!moved)
                        {
                            Logger.Warning($"OMFocuser: Native focus pulse step ({stepDelta}) reported failure at Position={Position} (target={targetPosition}).");
                            break;
                        }

                        Position = Math.Clamp(Position + stepDelta, 0, MaxStep);
                        remaining = targetPosition - Position;

                        if (remaining != 0)
                        {
                            await Task.Delay(Math.Clamp(waitInMs > 0 ? Math.Min(waitInMs, 50) : 20, 10, 100), linkedCts.Token).ConfigureAwait(false);
                        }
                    }

                    if (waitInMs > 0)
                    {
                        int settleMs = Math.Min(waitInMs, 100);
                        if (settleMs > 0)
                        {
                            await Task.Delay(settleMs, linkedCts.Token).ConfigureAwait(false);
                        }
                    }
                }, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"OMFocuser: Focus move canceled at Position={Position}.");
                throw;
            }
            finally
            {
                IsMoving = false;
            }
        }

        public void Halt()
        {
            lock (_moveLock)
            {
                try
                {
                    _moveCts?.Cancel();
                }
                catch { }
            }

            try
            {
                if (_customHaltExecutor != null)
                {
                    _customHaltExecutor();
                }
                else
                {
                    _device?.HaltFocus();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"OMFocuser: Halt error: {ex.Message}");
            }
            finally
            {
                IsMoving = false;
            }
        }

        private bool ExecuteFocusStep(int stepDelta)
        {
            if (_customStepExecutor != null)
            {
                return _customStepExecutor(stepDelta);
            }
            return _device != null && _device.SetFocusStep(stepDelta);
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
                lock (_moveLock)
                {
                    _moveCts?.Dispose();
                    _moveCts = null;
                }
            }
        }
    }
}
