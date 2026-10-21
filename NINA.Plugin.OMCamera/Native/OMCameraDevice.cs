using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NINA.Core.Utility;

namespace NINA.Plugin.OMCamera.Native
{
    public class OMCameraDevice : IDisposable
    {
        private readonly IntPtr _pCamera;
        private bool _disposed = false;
        private bool _connected = false;

        // Native COM callback structures for Recording, LiveView & Content Download events
        private IntPtr _pRecHandlerObj = IntPtr.Zero;
        private IntPtr _pRecHandlerVtbl = IntPtr.Zero;
        private IntPtr _pLvHandlerObj = IntPtr.Zero;
        private IntPtr _pLvHandlerVtbl = IntPtr.Zero;
        private IntPtr _pDlHandlerObj = IntPtr.Zero;
        private IntPtr _pDlHandlerVtbl = IntPtr.Zero;

        // Keep delegates rooted so GC never collects them while native code holds function pointers
        private QIDelegate? _qiDel;
        private AddRefReleaseDelegate? _addRefDel;
        private AddRefReleaseDelegate? _releaseDel;
        private DummyEventDelegate? _dummyRecDel;
        private DidReceiveCapturedImagePreviewDelegate? _didReceiveRecviewDel;
        private AddedCapturedContentDelegate? _addedCapturedContentDel;
        private WillDownloadCapturedImageDelegate? _willDownloadDel;
        private DidDownloadCapturedImageDelegate? _didDownloadDel;
        private DownloadCapturedImageFailedDelegate? _failedDownloadDel;
        private LiveViewUpdateDelegate? _lvUpdateDel;
        private DownloadProgressDelegate? _dlProgressDel;
        private DownloadCompleteDelegate? _dlCompleteDel;

        private readonly ManualResetEventSlim _captureReadyEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _objectAddedEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _recviewReadyEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _liveViewFrameEvent = new ManualResetEventSlim(false);
        private readonly object _imageLock = new object();
        private byte[]? _lastCapturedBytes;
        private byte[]? _lastRecviewBytes;
        private byte[]? _latestLiveViewJpeg;

        [DllImport(@"C:\Program Files (x86)\OMDS\OM Workspace\OlyCameraLib.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "OlyCamLib_CreateMemory")]
        private static extern int Raw_OlyCamLib_CreateMemory(out IntPtr ppMemory, uint size);

        // COM IUnknown delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QIDelegate(IntPtr pThis, IntPtr riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint AddRefReleaseDelegate(IntPtr pThis);

        // IOlyCameraRecordingEventHandler delegates (registered via Slot 43)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DummyEventDelegate(IntPtr pThis, IntPtr pCam, IntPtr arg1, IntPtr arg2);

        // Slot 8 (+0x40): OnDidReceiveCapturedImagePreview(this, pCam, IOlyMemory* pMemory, IOlyDictionary* pDict)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DidReceiveCapturedImagePreviewDelegate(IntPtr pThis, IntPtr pCam, IntPtr pMemory, IntPtr pDict);

        // Slot 10 (+0x50): OnAddedCapturedContent(this, pCam, IOlyContent* pContent)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AddedCapturedContentDelegate(IntPtr pThis, IntPtr pCam, IntPtr pContent);

        // Slot 11 (+0x58): OnWillDownloadCapturedImage(this, pCam, pDict, byte* pCancel)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int WillDownloadCapturedImageDelegate(IntPtr pThis, IntPtr pCam, IntPtr pDict, IntPtr pCancelByte);

        // Slot 12 (+0x60): OnDidDownloadCapturedImage(this, pCam, IOlyMemory* pMemory, IOlyDictionary* pDict)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DidDownloadCapturedImageDelegate(IntPtr pThis, IntPtr pCam, IntPtr pMemory, IntPtr pDict);

        // Slot 13 (+0x68): OnDownloadCapturedImageFailed(this, pCam, int hrError)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DownloadCapturedImageFailedDelegate(IntPtr pThis, IntPtr pCam, int hrError);

        // IOlyDownloadEventHandler delegates (passed to Slot 105 DownloadLastCapturedImage / Slot 117 DownloadContent)
        // Slot 3 (+0x18): bool OnProgress(this, pCam, ulong percent) -> MUST return true (1) or download aborts with 0x80004004!
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool DownloadProgressDelegate(IntPtr pThis, IntPtr pCam, ulong percent);

        // Slot 4 (+0x20): void OnDownloadComplete(this, pCam, IOlyMemory* pMemory)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DownloadCompleteDelegate(IntPtr pThis, IntPtr pCam, IntPtr pMemory);

        // IOlyCameraLiveViewEventHandler Slot 3 (+0x18): OnUpdateLiveViewImage(this, pCam, IOlyMemory* pMemory, IOlyDictionary* pDict)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LiveViewUpdateDelegate(IntPtr pThis, IntPtr pCam, IntPtr pMemory, IntPtr pDict);

        // IOlyMemory VTable delegates
        // Slot 6 (+0x30): GetBuffer(this, out IntPtr ppBuffer)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MemGetBufferDelegate(IntPtr pMem, out IntPtr ppBuffer);

        // Slot 7 (+0x38): GetSize(this, out uint pSize)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MemGetSizeDelegate(IntPtr pMem, out uint pSize);

        // IOlyCamera VTable exact delegates verified by REA disassembly:
        // Slot 3: Connect
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConnectDelegate(IntPtr pThis);

        // Slot 4: Disconnect
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DisconnectDelegate(IntPtr pThis, uint option);

        // Slot 5: IsConnected
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsConnectedDelegate(IntPtr pThis);

        // Slot 10: GetModelName
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetModelNameDelegate(IntPtr pThis, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder sb, uint maxLen);

        // Slot 12: GetCameraSerialNumber
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSerialNumberDelegate(IntPtr pThis, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder sb, uint maxLen);

        // Slot 15: SetCameraPropertyValue(this, IOlyPropertyValue* pPropVal)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetCameraPropertyValueDelegate(IntPtr pThis, IntPtr pPropVal);

        // Slot 16: GetCameraPropertyValue(this, uint propId, out IOlyPropertyValue* ppPropVal)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCameraPropertyValueDelegate(IntPtr pThis, uint propId, out IntPtr ppPropVal);

        // IOlyPropertyValue VTable delegates:
        // Slot 6 (+0x30): GetValue(this, out uint pVal)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PropGetValueDelegate(IntPtr pProp, out uint pVal);

        // Slot 7 (+0x38): SetValue(this, uint val)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PropSetValueDelegate(IntPtr pProp, uint val);

        // Slot 9 (+0x48): GetEnumList(this, out IntPtr ppEnumList)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PropGetEnumListDelegate(IntPtr pProp, out IntPtr ppEnumList);

        // IOlyEnumList VTable delegates:
        // Slot 3 (+0x18): GetCount(this, out uint pCount)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumGetCountDelegate(IntPtr pEnumList, out uint pCount);

        // Slot 4 (+0x20): GetItem(this, uint index, out uint pVal)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumGetItemDelegate(IntPtr pEnumList, uint index, out uint pVal);

        // Property IDs verified from OlyCameraLib.dll & live OM-1 Mark II PROPDUMP:
        public const uint PROP_APERTURE = 0x202;
        public const uint PROP_EXPOSURE_MODE = 0x206;
        public const uint PROP_ISO_SENSITIVITY_LEGACY = 0x207;
        public const uint PROP_EXPOSURE_COMP = 0x208;
        public const uint PROP_SHUTTER_SPEED = 0x21C;
        public const uint PROP_ISO_SENSITIVITY = 0x37C;

        // Slot 21: ChangeRunMode
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ChangeRunModeDelegate(IntPtr pThis, uint mode);

        // Slot 22: GetRunMode
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetRunModeDelegate(IntPtr pThis, out uint pMode);

        // Slot 34: StartLiveView
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StartLiveViewDelegate(IntPtr pThis);

        // Slot 35: StopLiveView
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StopLiveViewDelegate(IntPtr pThis);

        // Slot 36: IsLiveViewEnabled
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsLiveViewEnabledDelegate(IntPtr pThis);

        // Slot 37: SetCameraLiveViewEventHandler
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetLiveViewEventHandlerDelegate(IntPtr pThis, IntPtr pHandler);

        // Slot 42: GetActionType
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetActionTypeDelegate(IntPtr pThis, out uint pActionType);

        // Slot 43: SetCameraRecordingEventHandler
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetRecordingEventHandlerDelegate(IntPtr pThis, IntPtr pHandler);

        // Slot 46: TakePicture
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TakePictureDelegate(IntPtr pThis, uint action);

        // Slot 48: IsTakingPicture
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsTakingPictureDelegate(IntPtr pThis);

        // Slot 58: SetFocusAdjustPulse
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetFocusAdjustPulseDelegate(IntPtr pThis, int pulseStep, int reserved);

        // Slot 105: DownloadLastCapturedImage (fallback for standalone mode)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DownloadLastCapturedImageDelegate(IntPtr pThis, IntPtr pMemoryData);

        // IUnknown Release (Slot 2)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr pThis);

        public OMCameraDevice(IntPtr pCamera)
        {
            _pCamera = pCamera;
            InitializeNativeCallbacks();
        }

        private void InitializeNativeCallbacks()
        {
            _qiDel = (IntPtr pThis, IntPtr riid, out IntPtr ppv) =>
            {
                ppv = pThis;
                return 0;
            };
            _addRefDel = (IntPtr pThis) => 1;
            _releaseDel = (IntPtr pThis) => 1;
            _dummyRecDel = (IntPtr pThis, IntPtr pCam, IntPtr a, IntPtr b) => 0;

            _didReceiveRecviewDel = (IntPtr pThis, IntPtr pCam, IntPtr pMemory, IntPtr pDict) =>
            {
                try
                {
                    byte[]? data = ExtractBytesFromOlyMemory(pMemory);
                    if (data != null && data.Length > 0)
                    {
                        Logger.Info($"OMCameraDevice: Received Recview preview image ({data.Length} bytes).");
                        lock (_imageLock)
                        {
                            _lastRecviewBytes = data;
                        }
                        _recviewReadyEvent.Set();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"OMCameraDevice: Error in OnDidReceiveCapturedImagePreview: {ex.Message}");
                }
                return 0;
            };

            _addedCapturedContentDel = (IntPtr pThis, IntPtr pCam, IntPtr pContent) =>
            {
                Logger.Info($"OMCameraDevice: Camera notified Object has been added (OnAddedCapturedContent, pContent=0x{pContent.ToInt64():X}).");
                _objectAddedEvent.Set();
                return 0;
            };

            _willDownloadDel = (IntPtr pThis, IntPtr pCam, IntPtr pDict, IntPtr pCancelByte) =>
            {
                Logger.Info("OMCameraDevice: Camera notified image ready for PC transfer (OnWillDownloadCapturedImage).");
                if (pCancelByte != IntPtr.Zero)
                {
                    Marshal.WriteByte(pCancelByte, 0); // 0 = Do not cancel, proceed with download
                }
                return 0;
            };

            _didDownloadDel = (IntPtr pThis, IntPtr pCam, IntPtr pMemory, IntPtr pDict) =>
            {
                try
                {
                    byte[]? data = ExtractBytesFromOlyMemory(pMemory);
                    if (data != null && data.Length > 0)
                    {
                        bool isOrf = data.Length > 4 && data[0] == 0x49 && data[1] == 0x49;
                        Logger.Info($"OMCameraDevice: Downloaded captured image from camera ({data.Length} bytes, format={(isOrf ? "ORF" : "JPEG")}).");
                        lock (_imageLock)
                        {
                            // If RAW+JPEG is enabled on camera, prefer ORF or keep latest if none stored yet
                            if (_lastCapturedBytes == null || isOrf)
                            {
                                _lastCapturedBytes = data;
                            }
                        }
                        _captureReadyEvent.Set();
                    }
                    else
                    {
                        Logger.Warning("OMCameraDevice: OnDidDownloadCapturedImage received empty IOlyMemory buffer.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"OMCameraDevice: Error in OnDidDownloadCapturedImage: {ex.Message}");
                }
                return 0;
            };

            _failedDownloadDel = (IntPtr pThis, IntPtr pCam, int hrError) =>
            {
                Logger.Error($"OMCameraDevice: OnDownloadCapturedImageFailed hr=0x{hrError:X}");
                _captureReadyEvent.Set();
                return 0;
            };

            _dlProgressDel = (IntPtr pThis, IntPtr pCam, ulong percent) =>
            {
                // Return true (1 in AL) so DownloadContent continues and does not abort with 0x80004004 (E_ABORT)
                return true;
            };

            _dlCompleteDel = (IntPtr pThis, IntPtr pCam, IntPtr pMemory) =>
            {
                try
                {
                    byte[]? data = ExtractBytesFromOlyMemory(pMemory);
                    if (data != null && data.Length > 0)
                    {
                        bool isOrf = data.Length > 4 && data[0] == 0x49 && data[1] == 0x49;
                        Logger.Info($"OMCameraDevice: OnDownloadComplete received image ({data.Length} bytes, format={(isOrf ? "ORF" : "JPEG")}).");
                        lock (_imageLock)
                        {
                            if (_lastCapturedBytes == null || isOrf)
                            {
                                _lastCapturedBytes = data;
                            }
                        }
                        _captureReadyEvent.Set();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"OMCameraDevice: Error in OnDownloadComplete: {ex.Message}");
                }
                return 0;
            };

            _lvUpdateDel = (IntPtr pThis, IntPtr pCam, IntPtr pMemory, IntPtr pDict) =>
            {
                try
                {
                    byte[]? jpeg = ExtractBytesFromOlyMemory(pMemory);
                    if (jpeg != null && jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                    {
                        lock (_imageLock)
                        {
                            _latestLiveViewJpeg = jpeg;
                        }
                        _liveViewFrameEvent.Set();
                    }
                }
                catch { }
                return 0;
            };

            // Allocate 24-slot VTable for IOlyCameraRecordingEventHandler
            _pRecHandlerVtbl = Marshal.AllocHGlobal(24 * IntPtr.Size);
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_qiDel));
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_addRefDel));
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_releaseDel));
            for (int i = 3; i < 24; i++)
            {
                Marshal.WriteIntPtr(_pRecHandlerVtbl, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_dummyRecDel));
            }
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 8 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_didReceiveRecviewDel));
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 10 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_addedCapturedContentDel));
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 11 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_willDownloadDel));
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 12 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_didDownloadDel));
            Marshal.WriteIntPtr(_pRecHandlerVtbl, 13 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_failedDownloadDel));

            _pRecHandlerObj = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_pRecHandlerObj, _pRecHandlerVtbl);

            // Allocate 8-slot VTable for IOlyDownloadEventHandler (used by Slot 105 DownloadLastCapturedImage)
            _pDlHandlerVtbl = Marshal.AllocHGlobal(8 * IntPtr.Size);
            Marshal.WriteIntPtr(_pDlHandlerVtbl, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_qiDel));
            Marshal.WriteIntPtr(_pDlHandlerVtbl, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_addRefDel));
            Marshal.WriteIntPtr(_pDlHandlerVtbl, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_releaseDel));
            Marshal.WriteIntPtr(_pDlHandlerVtbl, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_dlProgressDel));
            Marshal.WriteIntPtr(_pDlHandlerVtbl, 4 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_dlCompleteDel));
            for (int i = 5; i < 8; i++)
            {
                Marshal.WriteIntPtr(_pDlHandlerVtbl, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_dummyRecDel));
            }

            _pDlHandlerObj = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_pDlHandlerObj, _pDlHandlerVtbl);

            // Allocate 8-slot VTable for IOlyCameraLiveViewEventHandler
            _pLvHandlerVtbl = Marshal.AllocHGlobal(8 * IntPtr.Size);
            Marshal.WriteIntPtr(_pLvHandlerVtbl, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_qiDel));
            Marshal.WriteIntPtr(_pLvHandlerVtbl, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_addRefDel));
            Marshal.WriteIntPtr(_pLvHandlerVtbl, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_releaseDel));
            Marshal.WriteIntPtr(_pLvHandlerVtbl, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_lvUpdateDel));
            for (int i = 4; i < 8; i++)
            {
                Marshal.WriteIntPtr(_pLvHandlerVtbl, i * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_dummyRecDel));
            }

            _pLvHandlerObj = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(_pLvHandlerObj, _pLvHandlerVtbl);
        }

        private static byte[]? ExtractBytesFromOlyMemory(IntPtr pMemory)
        {
            if (pMemory == IntPtr.Zero) return null;
            IntPtr memVtbl = Marshal.ReadIntPtr(pMemory);
            if (memVtbl == IntPtr.Zero) return null;

            var getBuf = Marshal.GetDelegateForFunctionPointer<MemGetBufferDelegate>(Marshal.ReadIntPtr(memVtbl, 6 * IntPtr.Size));
            var getSize = Marshal.GetDelegateForFunctionPointer<MemGetSizeDelegate>(Marshal.ReadIntPtr(memVtbl, 7 * IntPtr.Size));

            int hr1 = getBuf(pMemory, out IntPtr pBuf);
            int hr2 = getSize(pMemory, out uint size);
            if (hr1 != 0 || hr2 != 0 || pBuf == IntPtr.Zero || size == 0)
                return null;

            byte[] bytes = new byte[size];
            Marshal.Copy(pBuf, bytes, 0, (int)size);
            return bytes;
        }

        private T GetMethod<T>(int slotIndex) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(_pCamera);
            IntPtr methodPtr = Marshal.ReadIntPtr(vtable, slotIndex * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
        }

        public bool Connect()
        {
            if (_pCamera == IntPtr.Zero) return false;
            try
            {
                // Slot 3: Connect
                var conn = GetMethod<ConnectDelegate>(3);
                int hr = conn(_pCamera);
                if (hr == 0)
                {
                    _connected = true;

                    // Register Recording Event Handler (Slot 43) so captured images are downloaded to PC
                    try
                    {
                        var setRec = GetMethod<SetRecordingEventHandlerDelegate>(43);
                        int hrRec = setRec(_pCamera, _pRecHandlerObj);
                        Logger.Info($"OMCameraDevice: SetCameraRecordingEventHandler hr=0x{hrRec:X}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"OMCameraDevice: Failed to set recording event handler: {ex.Message}");
                    }

                    // Register LiveView Event Handler (Slot 37)
                    try
                    {
                        var setLv = GetMethod<SetLiveViewEventHandlerDelegate>(37);
                        int hrLv = setLv(_pCamera, _pLvHandlerObj);
                        Logger.Info($"OMCameraDevice: SetCameraLiveViewEventHandler hr=0x{hrLv:X}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"OMCameraDevice: Failed to set live view event handler: {ex.Message}");
                    }

                    // Slot 21: Set mode to RUN_MODE_RECORDING (PC Tethered remote)
                    var runMode = GetMethod<ChangeRunModeDelegate>(21);
                    int hrMode = runMode(_pCamera, OlyCameraNative.RUN_MODE_RECORDING);
                    Logger.Info($"OMCameraDevice: ChangeRunMode(RUN_MODE_RECORDING) hr=0x{hrMode:X}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: Connect exception: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            if (_pCamera == IntPtr.Zero || !_connected) return;
            _connected = false;
            try
            {
                try
                {
                    StopLiveView();
                }
                catch { }

                try
                {
                    var setLv = GetMethod<SetLiveViewEventHandlerDelegate>(37);
                    setLv(_pCamera, IntPtr.Zero);
                }
                catch { }

                try
                {
                    var setRec = GetMethod<SetRecordingEventHandlerDelegate>(43);
                    setRec(_pCamera, IntPtr.Zero);
                }
                catch { }

                // Slot 21: Set mode to RUN_MODE_STANDALONE before disconnect
                try
                {
                    var runMode = GetMethod<ChangeRunModeDelegate>(21);
                    runMode(_pCamera, OlyCameraNative.RUN_MODE_STANDALONE);
                }
                catch { }

                // Slot 4: Disconnect
                var disconn = GetMethod<DisconnectDelegate>(4);
                disconn(_pCamera, 0);
            }
            catch { }
        }

        public bool IsConnected()
        {
            if (_pCamera == IntPtr.Zero) return false;
            try
            {
                var isConn = GetMethod<IsConnectedDelegate>(5);
                return isConn(_pCamera);
            }
            catch
            {
                return false;
            }
        }

        public string GetModelName()
        {
            if (_pCamera == IntPtr.Zero) return "OM System Camera";
            try
            {
                var sb = new StringBuilder(256);
                var getModel = GetMethod<GetModelNameDelegate>(10);
                int hr = getModel(_pCamera, sb, 256);
                string res = sb.ToString().Trim();
                return string.IsNullOrEmpty(res) ? "OM System / Olympus Camera" : res;
            }
            catch
            {
                return "OM System / Olympus Camera";
            }
        }

        public string GetSerialNumber()
        {
            if (_pCamera == IntPtr.Zero) return "Unknown";
            try
            {
                var sb = new StringBuilder(256);
                var getSn = GetMethod<GetSerialNumberDelegate>(12);
                int hr = getSn(_pCamera, sb, 256);
                string res = sb.ToString().Trim();
                return string.IsNullOrEmpty(res) ? "OM-SN-001" : res;
            }
            catch
            {
                return "OM-SN-001";
            }
        }

        public uint GetActionType()
        {
            if (_pCamera == IntPtr.Zero) return OlyCameraNative.ACTION_TYPE_SINGLE;
            try
            {
                var getAct = GetMethod<GetActionTypeDelegate>(42);
                int hr = getAct(_pCamera, out uint actType);
                Logger.Info($"OMCameraDevice: GetActionType hr=0x{hr:X}, actionType={actType}");
                return (hr == 0 && actType != 0) ? actType : OlyCameraNative.ACTION_TYPE_SINGLE;
            }
            catch
            {
                return OlyCameraNative.ACTION_TYPE_SINGLE;
            }
        }

        private static void SafeReleaseCom(IntPtr pUnk)
        {
            if (pUnk == IntPtr.Zero) return;
            try
            {
                IntPtr vtbl = Marshal.ReadIntPtr(pUnk);
                if (vtbl != IntPtr.Zero)
                {
                    var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                    rel(pUnk);
                }
            }
            catch { }
        }

        /// <summary>
        /// For composite property wrappers in OlyCameraLib.dll (0x202 ApertureProp vtable *EDE0,
        /// 0x21C ShutterProp vtable *AF88), offset +0x08 holds the raw underlying IOlyCameraProperty*
        /// before min/max range clamping by 0x27E/0x27F or 0x285/0x286.
        /// </summary>
        private static IntPtr GetUnderlyingProperty(IntPtr pProp)
        {
            if (pProp == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                long vtbl = Marshal.ReadIntPtr(pProp).ToInt64();
                long low16 = vtbl & 0xFFFF;
                if (low16 == 0xEDE0 || low16 == 0xAF88)
                {
                    IntPtr pInner = Marshal.ReadIntPtr(pProp, 0x08);
                    if (pInner != IntPtr.Zero)
                        return pInner;
                }
            }
            catch { }
            return pProp;
        }

        public readonly struct ApertureEntry
        {
            public uint Code { get; }
            public double FStop { get; }
            public string DisplayText { get; }

            public ApertureEntry(uint code, double fStop, string displayText)
            {
                Code = code;
                FStop = fStop;
                DisplayText = displayText;
            }
        }

        public static ApertureEntry DecodeApertureCode(uint code)
        {
            if (code == 0 || code == 0xFFFF || code == 0xFFFFFFFF)
                return new ApertureEntry(0, 0.0, "f/-- (Lens/Auto)");

            double fStop;
            if ((code >> 16) > 0 && (code & 0xFFFF) > 0)
            {
                fStop = (double)(code >> 16) / (double)(code & 0xFFFF);
            }
            else if (code > 320 && code <= 6400 && (code % 10 == 0))
            {
                fStop = code / 100.0;
            }
            else
            {
                fStop = code / 10.0;
            }

            string label = fStop < 10.0
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "f/{0:0.0}", fStop)
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, "f/{0:0.#}", fStop);

            return new ApertureEntry(code, fStop, label);
        }

        public System.Collections.Generic.List<ApertureEntry> GetSupportedApertures()
        {
            var result = new System.Collections.Generic.List<ApertureEntry>();
            if (_pCamera == IntPtr.Zero) return result;

            IntPtr pProp = IntPtr.Zero;
            try
            {
                var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
                int hr = getProp(_pCamera, PROP_APERTURE_VALUE, out pProp);
                if (hr != 0 || pProp == IntPtr.Zero) return result;

                IntPtr pInner = GetUnderlyingProperty(pProp);
                foreach (IntPtr candidateProp in (pInner != pProp ? new[] { pProp, pInner } : new[] { pProp }))
                {
                    IntPtr pEnum = IntPtr.Zero;
                    try
                    {
                        IntPtr propVtbl = Marshal.ReadIntPtr(candidateProp);
                        var getEnum = Marshal.GetDelegateForFunctionPointer<PropGetEnumListDelegate>(Marshal.ReadIntPtr(propVtbl, 9 * IntPtr.Size));
                        if (getEnum(candidateProp, out pEnum) == 0 && pEnum != IntPtr.Zero)
                        {
                            IntPtr enumVtbl = Marshal.ReadIntPtr(pEnum);
                            var getCount = Marshal.GetDelegateForFunctionPointer<EnumGetCountDelegate>(Marshal.ReadIntPtr(enumVtbl, 3 * IntPtr.Size));
                            var getItem = Marshal.GetDelegateForFunctionPointer<EnumGetItemDelegate>(Marshal.ReadIntPtr(enumVtbl, 4 * IntPtr.Size));
                            if (getCount(pEnum, out uint count) == 0 && count > 0)
                            {
                                for (uint i = 0; i < count; i++)
                                {
                                    if (getItem(pEnum, i, out uint code) == 0)
                                    {
                                        if (code == 0 || code == 0xFFFF || code == 0xFFFFFFFF)
                                            continue;
                                        var entry = DecodeApertureCode(code);
                                        if (entry.FStop >= 0.7 && entry.FStop <= 64.0 && !result.Exists(e => e.Code == entry.Code))
                                        {
                                            result.Add(entry);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        SafeReleaseCom(pEnum);
                    }
                    if (result.Count > 0) break;
                }

                result.Sort((a, b) => a.FStop.CompareTo(b.FStop));
            }
            catch (Exception ex)
            {
                Logger.Warning($"OMCameraDevice: GetSupportedApertures error: {ex.Message}");
            }
            finally
            {
                SafeReleaseCom(pProp);
            }
            return result;
        }

        public uint GetApertureCode()
        {
            if (_pCamera == IntPtr.Zero) return 0;
            IntPtr pProp = IntPtr.Zero;
            try
            {
                var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
                int hr = getProp(_pCamera, PROP_APERTURE_VALUE, out pProp);
                if (hr != 0 || pProp == IntPtr.Zero) return 0;

                IntPtr pInner = GetUnderlyingProperty(pProp);
                foreach (IntPtr candidateProp in (pInner != pProp ? new[] { pProp, pInner } : new[] { pProp }))
                {
                    IntPtr propVtbl = Marshal.ReadIntPtr(candidateProp);
                    var getVal = Marshal.GetDelegateForFunctionPointer<PropGetValueDelegate>(Marshal.ReadIntPtr(propVtbl, 6 * IntPtr.Size));
                    if (getVal(candidateProp, out uint rawVal) == 0 && rawVal != 0 && rawVal != 0xFFFF && rawVal != 0xFFFFFFFF)
                    {
                        return rawVal;
                    }
                }
            }
            catch { }
            finally
            {
                SafeReleaseCom(pProp);
            }
            return 0;
        }

        public bool SetApertureCode(uint targetCode, out uint matchedCode)
        {
            matchedCode = targetCode;
            if (_pCamera == IntPtr.Zero || targetCode == 0) return false;
            IntPtr pProp = IntPtr.Zero;
            try
            {
                var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
                int hr = getProp(_pCamera, PROP_APERTURE_VALUE, out pProp);
                if (hr != 0 || pProp == IntPtr.Zero)
                {
                    Logger.Warning($"OMCameraDevice: GetCameraPropertyValue(APERTURE 0x202) failed hr=0x{hr:X}");
                    return false;
                }

                var supported = GetSupportedApertures();
                if (supported.Count > 0)
                {
                    var targetEntry = DecodeApertureCode(targetCode);
                    double bestDiff = double.MaxValue;
                    uint bestCode = targetCode;
                    foreach (var s in supported)
                    {
                        double diff = Math.Abs(s.FStop - targetEntry.FStop);
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            bestCode = s.Code;
                        }
                    }
                    matchedCode = bestCode;
                }

                IntPtr propVtbl = Marshal.ReadIntPtr(pProp);
                var setVal = Marshal.GetDelegateForFunctionPointer<PropSetValueDelegate>(Marshal.ReadIntPtr(propVtbl, 7 * IntPtr.Size));
                int hrSetVal = setVal(pProp, matchedCode);
                if (hrSetVal != 0)
                {
                    IntPtr pInner = GetUnderlyingProperty(pProp);
                    if (pInner != IntPtr.Zero && pInner != pProp)
                    {
                        IntPtr innerVtbl = Marshal.ReadIntPtr(pInner);
                        var setInnerVal = Marshal.GetDelegateForFunctionPointer<PropSetValueDelegate>(Marshal.ReadIntPtr(innerVtbl, 7 * IntPtr.Size));
                        hrSetVal = setInnerVal(pInner, matchedCode);
                        Marshal.WriteInt32(pProp, 0x20, (int)matchedCode);
                    }
                }

                var setProp = GetMethod<SetCameraPropertyValueDelegate>(15);
                int hrCommit = setProp(_pCamera, pProp);
                var entry = DecodeApertureCode(matchedCode);
                Logger.Info($"OMCameraDevice: SetApertureCode(target=0x{targetCode:X} -> matched={entry.DisplayText} [0x{matchedCode:X}]) setVal=0x{hrSetVal:X}, commit=0x{hrCommit:X}");
                return hrCommit == 0;
            }
            catch (Exception ex)
            {
                Logger.Warning($"OMCameraDevice: SetApertureCode error: {ex.Message}");
                return false;
            }
            finally
            {
                SafeReleaseCom(pProp);
            }
        }

        private static readonly uint[] CandidateIsoPropIds = { PROP_ISO_SENSITIVITY, PROP_ISO_SENSITIVITY_LEGACY };
        private const uint PROP_APERTURE_VALUE = OlyCameraNative.PROP_APERTURE_VALUE;

        public System.Collections.Generic.List<int> GetSupportedIsoValues()
        {
            var result = new System.Collections.Generic.List<int>();
            if (_pCamera == IntPtr.Zero) return result;
            var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
            foreach (uint propId in CandidateIsoPropIds)
            {
                IntPtr pProp = IntPtr.Zero;
                IntPtr pEnum = IntPtr.Zero;
                try
                {
                    int hr = getProp(_pCamera, propId, out pProp);
                    if (hr != 0 || pProp == IntPtr.Zero) continue;

                    IntPtr propVtbl = Marshal.ReadIntPtr(pProp);
                    var getEnum = Marshal.GetDelegateForFunctionPointer<PropGetEnumListDelegate>(Marshal.ReadIntPtr(propVtbl, 9 * IntPtr.Size));
                    if (getEnum(pProp, out pEnum) == 0 && pEnum != IntPtr.Zero)
                    {
                        IntPtr enumVtbl = Marshal.ReadIntPtr(pEnum);
                        var getCount = Marshal.GetDelegateForFunctionPointer<EnumGetCountDelegate>(Marshal.ReadIntPtr(enumVtbl, 3 * IntPtr.Size));
                        var getItem = Marshal.GetDelegateForFunctionPointer<EnumGetItemDelegate>(Marshal.ReadIntPtr(enumVtbl, 4 * IntPtr.Size));
                        if (getCount(pEnum, out uint count) == 0 && count > 0)
                        {
                            for (uint i = 0; i < count; i++)
                            {
                                if (getItem(pEnum, i, out uint code) == 0)
                                {
                                    if (code == 0xFFFFFFFF || code == 0xFFFF || code < 50 || code > 204800)
                                        continue;
                                    int iso = (int)code;
                                    if (!result.Contains(iso))
                                        result.Add(iso);
                                }
                            }
                        }
                    }
                    if (result.Count > 0)
                    {
                        result.Sort();
                        return result;
                    }
                }
                catch { }
                finally
                {
                    SafeReleaseCom(pEnum);
                    SafeReleaseCom(pProp);
                }
            }
            return result;
        }

        public int GetIsoSensitivity()
        {
            if (_pCamera == IntPtr.Zero) return 200;
            var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
            foreach (uint propId in CandidateIsoPropIds)
            {
                IntPtr pProp = IntPtr.Zero;
                try
                {
                    int hr = getProp(_pCamera, propId, out pProp);
                    if (hr != 0 || pProp == IntPtr.Zero) continue;

                    IntPtr propVtbl = Marshal.ReadIntPtr(pProp);
                    var getVal = Marshal.GetDelegateForFunctionPointer<PropGetValueDelegate>(Marshal.ReadIntPtr(propVtbl, 6 * IntPtr.Size));
                    if (getVal(pProp, out uint rawIso) == 0)
                    {
                        if (rawIso >= 50 && rawIso <= 204800 && rawIso != 0xFFFF && rawIso != 0xFFFFFFFF)
                            return (int)rawIso;
                    }
                }
                catch { }
                finally
                {
                    SafeReleaseCom(pProp);
                }
            }
            return 200;
        }

        public bool SetIsoSensitivity(int targetIso, out int matchedIso)
        {
            matchedIso = targetIso;
            if (_pCamera == IntPtr.Zero) return false;
            var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
            foreach (uint propId in CandidateIsoPropIds)
            {
                IntPtr pProp = IntPtr.Zero;
                IntPtr pEnum = IntPtr.Zero;
                try
                {
                    int hr = getProp(_pCamera, propId, out pProp);
                    if (hr != 0 || pProp == IntPtr.Zero) continue;

                    IntPtr propVtbl = Marshal.ReadIntPtr(pProp);
                    var getEnum = Marshal.GetDelegateForFunctionPointer<PropGetEnumListDelegate>(Marshal.ReadIntPtr(propVtbl, 9 * IntPtr.Size));
                    var setVal = Marshal.GetDelegateForFunctionPointer<PropSetValueDelegate>(Marshal.ReadIntPtr(propVtbl, 7 * IntPtr.Size));

                    uint chosenCode = 0;
                    if (getEnum(pProp, out pEnum) == 0 && pEnum != IntPtr.Zero)
                    {
                        IntPtr enumVtbl = Marshal.ReadIntPtr(pEnum);
                        var getCount = Marshal.GetDelegateForFunctionPointer<EnumGetCountDelegate>(Marshal.ReadIntPtr(enumVtbl, 3 * IntPtr.Size));
                        var getItem = Marshal.GetDelegateForFunctionPointer<EnumGetItemDelegate>(Marshal.ReadIntPtr(enumVtbl, 4 * IntPtr.Size));

                        if (getCount(pEnum, out uint count) == 0 && count > 0)
                        {
                            int bestDiff = int.MaxValue;
                            for (uint i = 0; i < count; i++)
                            {
                                if (getItem(pEnum, i, out uint code) == 0)
                                {
                                    if (code == 0xFFFFFFFF || code == 0xFFFF || code < 50 || code > 204800)
                                        continue;
                                    int isoVal = (int)code;
                                    int diff = Math.Abs(isoVal - targetIso);
                                    if (diff < bestDiff)
                                    {
                                        bestDiff = diff;
                                        chosenCode = code;
                                    }
                                }
                            }
                        }
                    }

                    if (chosenCode == 0) continue;

                    matchedIso = (int)chosenCode;
                    int hrSetVal = setVal(pProp, chosenCode);
                    var setProp = GetMethod<SetCameraPropertyValueDelegate>(15);
                    int hrCommit = setProp(_pCamera, pProp);
                    Logger.Info($"OMCameraDevice: SetIsoSensitivity(0x{propId:X3}, target={targetIso} -> matched={matchedIso}, code=0x{chosenCode:X}) setVal=0x{hrSetVal:X}, commit=0x{hrCommit:X}");
                    if (hrCommit == 0) return true;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"OMCameraDevice: SetIsoSensitivity(0x{propId:X3}) error: {ex.Message}");
                }
                finally
                {
                    SafeReleaseCom(pEnum);
                    SafeReleaseCom(pProp);
                }
            }
            return false;
        }

        public bool SetIsoSensitivity(int targetIso) => SetIsoSensitivity(targetIso, out _);

        public double GetShutterSpeed()
        {
            if (_pCamera == IntPtr.Zero) return 0;
            IntPtr pProp = IntPtr.Zero;
            try
            {
                var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
                int hr = getProp(_pCamera, PROP_SHUTTER_SPEED, out pProp);
                if (hr != 0 || pProp == IntPtr.Zero) return 0;

                IntPtr propVtbl = Marshal.ReadIntPtr(pProp);
                var getVal = Marshal.GetDelegateForFunctionPointer<PropGetValueDelegate>(Marshal.ReadIntPtr(propVtbl, 6 * IntPtr.Size));
                if (getVal(pProp, out uint code) == 0)
                {
                    if (code >= 0xFFF00000 || (code & 0xFFFF) >= 0xFFF0) return 0;
                    uint num = code >> 16;
                    uint den = code & 0xFFFF;
                    if (num > 0 && den > 0)
                        return (double)num / (double)den;
                }
            }
            catch { }
            finally
            {
                SafeReleaseCom(pProp);
            }
            return 0;
        }

        public bool SetShutterSpeed(double targetSeconds, out double matchedSeconds)
        {
            matchedSeconds = targetSeconds;
            if (_pCamera == IntPtr.Zero) return false;
            IntPtr pProp = IntPtr.Zero;
            IntPtr pEnum = IntPtr.Zero;
            try
            {
                var getProp = GetMethod<GetCameraPropertyValueDelegate>(16);
                int hr = getProp(_pCamera, PROP_SHUTTER_SPEED, out pProp);
                if (hr != 0 || pProp == IntPtr.Zero)
                {
                    Logger.Warning($"OMCameraDevice: GetCameraPropertyValue(SHUTTER 0x21C) failed hr=0x{hr:X}");
                    return false;
                }

                IntPtr propVtbl = Marshal.ReadIntPtr(pProp);
                var getEnum = Marshal.GetDelegateForFunctionPointer<PropGetEnumListDelegate>(Marshal.ReadIntPtr(propVtbl, 9 * IntPtr.Size));
                var setVal = Marshal.GetDelegateForFunctionPointer<PropSetValueDelegate>(Marshal.ReadIntPtr(propVtbl, 7 * IntPtr.Size));

                uint chosenCode = 0;
                double chosenSec = 0;
                if (getEnum(pProp, out pEnum) == 0 && pEnum != IntPtr.Zero)
                {
                    IntPtr enumVtbl = Marshal.ReadIntPtr(pEnum);
                    var getCount = Marshal.GetDelegateForFunctionPointer<EnumGetCountDelegate>(Marshal.ReadIntPtr(enumVtbl, 3 * IntPtr.Size));
                    var getItem = Marshal.GetDelegateForFunctionPointer<EnumGetItemDelegate>(Marshal.ReadIntPtr(enumVtbl, 4 * IntPtr.Size));

                    if (getCount(pEnum, out uint count) == 0 && count > 0)
                    {
                        double bestLogDiff = double.MaxValue;
                        double targetClamped = Math.Max(0.00003, targetSeconds);

                        for (uint i = 0; i < count; i++)
                        {
                            if (getItem(pEnum, i, out uint code) == 0)
                            {
                                // Skip Bulb/LiveTime/LiveComp special codes (>= 0xFFFFFFF0 or high word >= 0xFFF0)
                                if (code >= 0xFFF00000 || (code & 0xFFFF) >= 0xFFF0) continue;
                                // In PROP_SHUTTER_SPEED (0x21C):
                                // High 16 bits (code >> 16) = Numerator (e.g. 0x00C8 = 200)
                                // Low 16 bits (code & 0xFFFF) = Denominator (e.g. 0x000A = 10 -> 200/10 = 20s; or 0x000A00C8 -> 10/200 = 0.05s)
                                uint num = code >> 16;
                                uint den = code & 0xFFFF;
                                if (num == 0 || den == 0) continue;

                                double sec = (double)num / (double)den;
                                double logDiff = Math.Abs(Math.Log(sec / targetClamped));
                                if (logDiff < bestLogDiff)
                                {
                                    bestLogDiff = logDiff;
                                    chosenCode = code;
                                    chosenSec = sec;
                                }
                            }
                        }
                    }
                }

                if (chosenCode == 0)
                {
                    Logger.Warning("OMCameraDevice: SetShutterSpeed could not find a matching shutter speed in camera enum list (camera may be in A/P/Auto mode instead of M/S).");
                    return false;
                }

                matchedSeconds = chosenSec;
                int hrSetVal = setVal(pProp, chosenCode);
                var setProp = GetMethod<SetCameraPropertyValueDelegate>(15);
                int hrCommit = setProp(_pCamera, pProp);
                Logger.Info($"OMCameraDevice: SetShutterSpeed(target={targetSeconds}s -> matched={chosenSec:F4}s, code=0x{chosenCode:X8}) setVal=0x{hrSetVal:X}, commit=0x{hrCommit:X}");
                return hrCommit == 0;
            }
            catch (Exception ex)
            {
                Logger.Warning($"OMCameraDevice: SetShutterSpeed error: {ex.Message}");
                return false;
            }
            finally
            {
                SafeReleaseCom(pEnum);
                SafeReleaseCom(pProp);
            }
        }

        public bool SetShutterSpeed(double targetSeconds) => SetShutterSpeed(targetSeconds, out _);

        public void PrepareForCapture()
        {
            lock (_imageLock)
            {
                _lastCapturedBytes = null;
                _lastRecviewBytes = null;
            }
            _captureReadyEvent.Reset();
            _objectAddedEvent.Reset();
            _recviewReadyEvent.Reset();
        }

        public bool StartBulbExposure()
        {
            try
            {
                var takePic = GetMethod<TakePictureDelegate>(46);
                // Action 3 = 1st + 2nd stage press ON simultaneously (opens Bulb shutter)
                int hr = takePic(_pCamera, OlyCameraNative.ACTION_1ST_2ND_PRESS);
                Logger.Info($"OMCameraDevice: StartBulbExposure TakePicture(3) hr=0x{hr:X}");
                if (hr != 0)
                {
                    // Fallback: 1st stage press (1) then 2nd stage press (2)
                    int hr1 = takePic(_pCamera, OlyCameraNative.ACTION_1ST_PRESS);
                    Thread.Sleep(50);
                    int hr2 = takePic(_pCamera, OlyCameraNative.ACTION_2ND_PRESS);
                    Logger.Info($"OMCameraDevice: StartBulbExposure fallback TakePicture(1)->0x{hr1:X}, TakePicture(2)->0x{hr2:X}");
                    return hr2 == 0;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: StartBulbExposure error: {ex.Message}");
                return false;
            }
        }

        public bool StopBulbExposure()
        {
            try
            {
                var takePic = GetMethod<TakePictureDelegate>(46);
                // Action 6 = Release 1st + 2nd stage simultaneously (closes Bulb shutter)
                int hr = takePic(_pCamera, OlyCameraNative.ACTION_1ST_2ND_RELEASE);
                Logger.Info($"OMCameraDevice: StopBulbExposure TakePicture(6) hr=0x{hr:X}");
                return hr == 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: StopBulbExposure error: {ex.Message}");
                return false;
            }
        }

        public bool TriggerTimedExposure()
        {
            try
            {
                var takePic = GetMethod<TakePictureDelegate>(46);
                // First try Action 10 (SDK Single Shot: 1 -> wait AF -> 2 -> 6)
                int hr = takePic(_pCamera, OlyCameraNative.ACTION_SINGLE_SHOT);
                Logger.Info($"OMCameraDevice: TriggerTimedExposure TakePicture(10) hr=0x{hr:X}");
                if (hr == 0)
                {
                    return true;
                }

                // If Action 10 failed (e.g. AF could not lock in low light), fire shutter directly with Action 3 -> Action 6
                Logger.Warning($"OMCameraDevice: TakePicture(10) returned 0x{hr:X}, falling back to direct shutter release (3 -> 6).");
                int hr3 = takePic(_pCamera, OlyCameraNative.ACTION_1ST_2ND_PRESS);
                Thread.Sleep(120);
                int hr6 = takePic(_pCamera, OlyCameraNative.ACTION_1ST_2ND_RELEASE);
                Logger.Info($"OMCameraDevice: Fallback TakePicture(3)->0x{hr3:X}, TakePicture(6)->0x{hr6:X}");
                return hr3 == 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: TriggerTimedExposure error: {ex.Message}");
                return false;
            }
        }

        public bool IsTakingPicture()
        {
            try
            {
                var isTaking = GetMethod<IsTakingPictureDelegate>(48);
                return isTaking(_pCamera);
            }
            catch
            {
                return false;
            }
        }

        public byte[]? WaitForCapturedImage(CancellationToken token, int timeoutMs = 30000)
        {
            try
            {
                var handles = new WaitHandle[]
                {
                    _captureReadyEvent.WaitHandle,
                    _objectAddedEvent.WaitHandle,
                    token.WaitHandle
                };

                int signaledIdx = WaitHandle.WaitAny(handles, timeoutMs);
                token.ThrowIfCancellationRequested();

                if (signaledIdx == 0)
                {
                    // Direct PC download callback (Slot 12) already delivered the bytes
                    Thread.Sleep(50);
                    lock (_imageLock)
                    {
                        if (_lastCapturedBytes != null && _lastCapturedBytes.Length > 0)
                            return _lastCapturedBytes;
                    }
                }
                else if (signaledIdx == 1)
                {
                    // Camera fired ObjectAdded (Slot 10) right after exposure!
                    // Wait 120ms in case camera is in RAW+JPEG mode (where a 2nd ObjectAdded arrives ~17ms later)
                    Thread.Sleep(120);
                    Logger.Info("OMCameraDevice: ObjectAdded signaled; downloading captured image via DownloadLastCapturedImage...");
                    byte[]? downloaded = DownloadCapturedImageWithHandler();
                    if (downloaded != null && downloaded.Length > 0)
                    {
                        return downloaded;
                    }
                }
                else
                {
                    Logger.Warning($"OMCameraDevice: Timed out waiting ({timeoutMs}ms) for captured image notification.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: WaitForCapturedImage error: {ex.Message}");
            }

            // Try downloading via Slot 105 in case ObjectAdded fired before WaitAny
            byte[]? fallback = DownloadCapturedImageWithHandler();
            if (fallback != null && fallback.Length > 0)
                return fallback;

            // Final fallback: return the Recview JPEG if no SD card was present or full download failed
            lock (_imageLock)
            {
                if (_lastRecviewBytes != null && _lastRecviewBytes.Length > 0)
                {
                    Logger.Info($"OMCameraDevice: Using Recview preview image fallback ({_lastRecviewBytes.Length} bytes).");
                    return _lastRecviewBytes;
                }
            }
            return null;
        }

        private byte[]? DownloadCapturedImageWithHandler()
        {
            if (_pCamera == IntPtr.Zero || _pDlHandlerObj == IntPtr.Zero) return null;

            try
            {
                var download = GetMethod<DownloadLastCapturedImageDelegate>(105);
                int hr = download(_pCamera, _pDlHandlerObj);
                Logger.Info($"OMCameraDevice: DownloadLastCapturedImage hr=0x{hr:X}");

                lock (_imageLock)
                {
                    if (_lastCapturedBytes != null && _lastCapturedBytes.Length > 0)
                    {
                        return _lastCapturedBytes;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: DownloadCapturedImageWithHandler exception: {ex.Message}");
                return null;
            }
        }

        public bool StartLiveView()
        {
            if (_pCamera == IntPtr.Zero) return false;
            try
            {
                lock (_imageLock)
                {
                    _latestLiveViewJpeg = null;
                }
                _liveViewFrameEvent.Reset();

                var startLv = GetMethod<StartLiveViewDelegate>(34);
                int hr = startLv(_pCamera);
                Logger.Info($"OMCameraDevice: StartLiveView hr=0x{hr:X}");
                return hr == 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: StartLiveView error: {ex.Message}");
                return false;
            }
        }

        public bool StopLiveView()
        {
            if (_pCamera == IntPtr.Zero) return false;
            try
            {
                var stopLv = GetMethod<StopLiveViewDelegate>(35);
                int hr = stopLv(_pCamera);
                Logger.Info($"OMCameraDevice: StopLiveView hr=0x{hr:X}");
                return hr == 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"OMCameraDevice: StopLiveView error: {ex.Message}");
                return false;
            }
        }

        public byte[]? GetNextLiveViewFrame(CancellationToken token, int timeoutMs = 3000)
        {
            lock (_imageLock)
            {
                if (_latestLiveViewJpeg != null)
                {
                    var frame = _latestLiveViewJpeg;
                    _latestLiveViewJpeg = null;
                    _liveViewFrameEvent.Reset();
                    return frame;
                }
            }

            if (_liveViewFrameEvent.Wait(timeoutMs, token))
            {
                lock (_imageLock)
                {
                    var frame = _latestLiveViewJpeg;
                    _latestLiveViewJpeg = null;
                    _liveViewFrameEvent.Reset();
                    return frame;
                }
            }
            return null;
        }

        public bool SetFocusStep(int pulseStep)
        {
            try
            {
                var focusFunc = GetMethod<SetFocusAdjustPulseDelegate>(58);
                int hr = focusFunc(_pCamera, pulseStep, 0);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                if (_pCamera != IntPtr.Zero)
                {
                    try
                    {
                        var release = GetMethod<ReleaseDelegate>(2);
                        release(_pCamera);
                    }
                    catch { }
                }
                if (_pRecHandlerObj != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_pRecHandlerObj);
                    _pRecHandlerObj = IntPtr.Zero;
                }
                if (_pRecHandlerVtbl != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_pRecHandlerVtbl);
                    _pRecHandlerVtbl = IntPtr.Zero;
                }
                if (_pLvHandlerObj != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_pLvHandlerObj);
                    _pLvHandlerObj = IntPtr.Zero;
                }
                if (_pLvHandlerVtbl != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_pLvHandlerVtbl);
                    _pLvHandlerVtbl = IntPtr.Zero;
                }
                if (_pDlHandlerObj != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_pDlHandlerObj);
                    _pDlHandlerObj = IntPtr.Zero;
                }
                if (_pDlHandlerVtbl != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_pDlHandlerVtbl);
                    _pDlHandlerVtbl = IntPtr.Zero;
                }
                _disposed = true;
            }
        }
    }
}
