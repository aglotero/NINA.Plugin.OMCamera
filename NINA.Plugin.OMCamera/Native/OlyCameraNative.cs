using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using NINA.Core.Utility;

namespace NINA.Plugin.OMCamera.Native
{
    public static class OlyCameraNative
    {
        private const string DLL_NAME = "OlyCameraLib.dll";

        // Supported OlyCameraLib.dll build shipped with OM Workspace (64-bit):
        // - Exported OlyCamLib_Version: 1.1.49 (0x01, 0x01, 0x31)
        // - PE FileVersionInfo: 1.1.0.49 (491,008 bytes, SHA-256: 690A944736F613CCFFADB1AFD0AD2F35E6F842081230C1262B7A269AB68CBB4C)
        public const uint SupportedApiMajor = 1;
        public const uint SupportedApiMinor = 1;
        public const uint SupportedApiBuild = 49;

        public const int SupportedFileMajor = 1;
        public const int SupportedFileMinor = 1;
        public const int SupportedFileBuild = 0;
        public const int SupportedFilePrivate = 49;

        // Verified RVAs in OlyCameraLib.dll 1.1.0.49 (API 1.1.49) that establish the layout of
        // COlyCameraCtrlManager::m_bHasLocModel at offset +0x80:
        // - RVA 0x35A4F (COlyCameraCtrlManager ctor): 41 C6 87 80 00 00 00 01  (mov BYTE PTR [r15+0x80], 0x1)
        // - RVA 0x3833D (COlyCameraCtrlManager::IsModelSupported): 80 B9 80 00 00 00 01 (cmp BYTE PTR [rcx+0x80], 0x1)
        public const int RvaManagerCtorLocModelWrite = 0x35A4F;
        public const int RvaManagerIsModelSupportedCheck = 0x3833D;

        private static readonly byte[] ExpectedCtorLocModelBytes = { 0x41, 0xC6, 0x87, 0x80, 0x00, 0x00, 0x00, 0x01 };
        private static readonly byte[] ExpectedCheckLocModelBytes = { 0x80, 0xB9, 0x80, 0x00, 0x00, 0x00, 0x01 };

        public static ReadOnlySpan<byte> ExpectedCtorInstruction => ExpectedCtorLocModelBytes;
        public static ReadOnlySpan<byte> ExpectedCheckInstruction => ExpectedCheckLocModelBytes;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpLibFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "OlyCamLib_Initialize")]
        private static extern int Raw_OlyCamLib_Initialize([MarshalAs(UnmanagedType.LPStr)] string licenseKey);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "OlyCamLib_GetManagerInstance")]
        private static extern int Raw_OlyCamLib_GetManagerInstance(out IntPtr ppManager);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "OlyCamLib_Finalize")]
        private static extern int Raw_OlyCamLib_Finalize();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "OlyCamLib_Version")]
        public static extern int OlyCamLib_Version(out uint major, out uint minor, out uint build);

        public const uint PROP_APERTURE_VALUE = 0x202;

        /// <summary>
        /// Validates that the loaded OlyCameraLib.dll matches the supported API and PE file version before
        /// invoking internal COM interfaces or applying the manager initialization fix.
        /// </summary>
        public static bool ValidateNativeLibraryVersion(
            uint apiMajor,
            uint apiMinor,
            uint apiBuild,
            int fileMajor,
            int fileMinor,
            int fileBuild,
            int filePrivate,
            out string errorMessage)
        {
            if (apiMajor != SupportedApiMajor || apiMinor != SupportedApiMinor || apiBuild != SupportedApiBuild)
            {
                errorMessage =
                    $"Unsupported OlyCameraLib.dll SDK version {apiMajor}.{apiMinor}.{apiBuild}. " +
                    $"Expected SDK version {SupportedApiMajor}.{SupportedApiMinor}.{SupportedApiBuild}.";
                return false;
            }

            if (fileMajor != SupportedFileMajor ||
                fileMinor != SupportedFileMinor ||
                fileBuild != SupportedFileBuild ||
                filePrivate != SupportedFilePrivate)
            {
                errorMessage =
                    $"Unsupported OlyCameraLib.dll file version {fileMajor}.{fileMinor}.{fileBuild}.{filePrivate}. " +
                    $"Expected file version {SupportedFileMajor}.{SupportedFileMinor}.{SupportedFileBuild}.{SupportedFilePrivate}.";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        /// <summary>
        /// Verifies the exact instruction opcodes at RVA 0x35A4F and RVA 0x3833D that access
        /// COlyCameraCtrlManager::m_bHasLocModel at offset +0x80 before touching that offset.
        /// </summary>
        public static bool ValidateLocModelSignature(
            ReadOnlySpan<byte> ctorInstrBytes,
            ReadOnlySpan<byte> checkInstrBytes,
            out string errorMessage)
        {
            if (!ctorInstrBytes.SequenceEqual(ExpectedCtorLocModelBytes))
            {
                errorMessage =
                    $"OlyCameraLib.dll instruction signature mismatch at RVA 0x{RvaManagerCtorLocModelWrite:X}. " +
                    "Refusing to modify COlyCameraCtrlManager offset +0x80 on an unrecognized binary layout.";
                return false;
            }

            if (!checkInstrBytes.SequenceEqual(ExpectedCheckLocModelBytes))
            {
                errorMessage =
                    $"OlyCameraLib.dll instruction signature mismatch at RVA 0x{RvaManagerIsModelSupportedCheck:X}. " +
                    "Refusing to modify COlyCameraCtrlManager offset +0x80 on an unrecognized binary layout.";
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        /// <summary>
        /// Throws <see cref="NotSupportedException"/> if either the native library version or instruction signature
        /// does not match the verified OlyCameraLib.dll build.
        /// </summary>
        public static void EnsureSupportedNativeLibrary(
            uint apiMajor,
            uint apiMinor,
            uint apiBuild,
            int fileMajor,
            int fileMinor,
            int fileBuild,
            int filePrivate,
            ReadOnlySpan<byte> ctorInstrBytes,
            ReadOnlySpan<byte> checkInstrBytes)
        {
            if (!ValidateNativeLibraryVersion(apiMajor, apiMinor, apiBuild, fileMajor, fileMinor, fileBuild, filePrivate, out string verErr))
            {
                throw new NotSupportedException(verErr);
            }

            if (!ValidateLocModelSignature(ctorInstrBytes, checkInstrBytes, out string sigErr))
            {
                throw new NotSupportedException(sigErr);
            }
        }

        private static string? ResolveOlyCameraLibPath()
        {
            var candidates = new List<string>();

            // 1. Optional user-configured environment variable
            string? envDir = Environment.GetEnvironmentVariable("OM_WORKSPACE_PATH");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                candidates.Add(envDir.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? envDir
                    : Path.Combine(envDir, DLL_NAME));
            }

            // 2. Standard Program Files paths on system drive and all fixed drives
            string[] relativeDirs =
            {
                @"Program Files (x86)\OMDS\OM Workspace",
                @"Program Files\OMDS\OM Workspace",
                @"Program Files (x86)\OMDS\OM Capture",
                @"Program Files\OMDS\OM Capture",
                @"Program Files (x86)\Olympus\Olympus Workspace",
                @"Program Files\Olympus\Olympus Workspace"
            };

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                foreach (string rel in relativeDirs)
                {
                    candidates.Add(Path.Combine(drive.RootDirectory.FullName, rel, DLL_NAME));
                }
            }

            // 3. Windows Registry lookup (Uninstall entries for OM Workspace / OM Capture)
            string[] regRoots =
            {
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (string root in regRoots)
            {
                try
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(root);
                    if (baseKey == null) continue;
                    foreach (string subName in baseKey.GetSubKeyNames())
                    {
                        using var subKey = baseKey.OpenSubKey(subName);
                        string? loc = subKey?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc))
                        {
                            string candidate = Path.Combine(loc, DLL_NAME);
                            if (File.Exists(candidate))
                                candidates.Insert(0, candidate);
                        }
                    }
                }
                catch { }
            }

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private static bool _isLoaded = false;
        private static bool _isInitialized = false;
        private static IntPtr _pManager = IntPtr.Zero;
        private static readonly object _syncLock = new object();

        // RunModes
        public const uint RUN_MODE_RECORDING = 1;
        public const uint RUN_MODE_STANDALONE = 2;
        public const uint RUN_MODE_RAW_DEVELOP = 3;

        // Action Types returned by IOlyCamera::GetActionType (Slot 42)
        public const uint ACTION_TYPE_SINGLE = 1;
        public const uint ACTION_TYPE_BULB = 2;
        public const uint ACTION_TYPE_CONTINUOUS = 3;

        // TakePicture Actions verified from IOlyCamera::TakePicture (Slot 46, RVA 0xDF60)
        public const uint ACTION_1ST_PRESS = 1;         // Half-press ON ([pCam+0x43B]=1)
        public const uint ACTION_2ND_PRESS = 2;         // Full-press ON ([pCam+0x43C]=1)
        public const uint ACTION_1ST_2ND_PRESS = 3;     // Half+Full press ON ([pCam+0x43B]=0x0101) -> Start Bulb / Immediate Shutter
        public const uint ACTION_2ND_RELEASE = 4;       // Full-press OFF ([pCam+0x43C]=0)
        public const uint ACTION_1ST_RELEASE = 5;       // Half-press OFF ([pCam+0x43B]=0)
        public const uint ACTION_1ST_2ND_RELEASE = 6;   // Half+Full press OFF ([pCam+0x43B]=0x0000) -> Stop Bulb / Release All
        public const uint ACTION_SINGLE_SHOT = 10;      // Auto Single Shot (1 -> wait AF -> 2 -> 6)

        public static bool EnsureInitialized()
        {
            lock (_syncLock)
            {
                if (_isInitialized && _pManager != IntPtr.Zero)
                    return true;

                string? dllPath = ResolveOlyCameraLibPath();
                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                {
                    Logger.Error("OlyCameraNative: OlyCameraLib.dll not found. Please install OM Workspace (64-bit) or set the OM_WORKSPACE_PATH environment variable.");
                    return false;
                }

                FileVersionInfo fvi;
                try
                {
                    fvi = FileVersionInfo.GetVersionInfo(dllPath);
                }
                catch (Exception ex)
                {
                    Logger.Error($"OlyCameraNative: Failed to read FileVersionInfo for '{dllPath}': {ex.Message}");
                    return false;
                }

                if (!_isLoaded)
                {
                    string dir = Path.GetDirectoryName(dllPath)!;
                    SetDllDirectory(dir);
                    Environment.SetEnvironmentVariable("PATH", dir + ";" + Environment.GetEnvironmentVariable("PATH"));
                    IntPtr hLoaded = LoadLibrary(dllPath);
                    if (hLoaded == IntPtr.Zero)
                    {
                        Logger.Error($"OlyCameraNative: LoadLibrary failed for '{dllPath}' (Win32 error {Marshal.GetLastWin32Error()}).");
                        return false;
                    }
                    _isLoaded = true;
                }

                uint apiMaj = 0, apiMin = 0, apiBld = 0;
                int hrVer = OlyCamLib_Version(out apiMaj, out apiMin, out apiBld);
                if (hrVer != 0)
                {
                    Logger.Error($"OlyCameraNative: OlyCamLib_Version failed with hr=0x{hrVer:X}.");
                    return false;
                }

                if (!ValidateNativeLibraryVersion(
                        apiMaj,
                        apiMin,
                        apiBld,
                        fvi.FileMajorPart,
                        fvi.FileMinorPart,
                        fvi.FileBuildPart,
                        fvi.FilePrivatePart,
                        out string verError))
                {
                    Logger.Error($"OlyCameraNative: {verError}");
                    return false;
                }

                IntPtr hMod = GetModuleHandle(DLL_NAME);
                if (hMod == IntPtr.Zero)
                {
                    Logger.Error("OlyCameraNative: GetModuleHandle(OlyCameraLib.dll) returned null after loading.");
                    return false;
                }

                byte[] ctorBytes = new byte[ExpectedCtorLocModelBytes.Length];
                byte[] checkBytes = new byte[ExpectedCheckLocModelBytes.Length];
                Marshal.Copy(hMod + RvaManagerCtorLocModelWrite, ctorBytes, 0, ctorBytes.Length);
                Marshal.Copy(hMod + RvaManagerIsModelSupportedCheck, checkBytes, 0, checkBytes.Length);

                if (!ValidateLocModelSignature(ctorBytes, checkBytes, out string sigError))
                {
                    Logger.Error($"OlyCameraNative: {sigError}");
                    return false;
                }

                int hr = Raw_OlyCamLib_Initialize("OM Workspace");
                if (hr != 0)
                {
                    Logger.Error($"OlyCameraNative: OlyCamLib_Initialize failed with hr=0x{hr:X}");
                    return false;
                }

                hr = Raw_OlyCamLib_GetManagerInstance(out _pManager);
                if (hr != 0 || _pManager == IntPtr.Zero)
                {
                    Logger.Error($"OlyCameraNative: OlyCamLib_GetManagerInstance failed with hr=0x{hr:X}");
                    return false;
                }

                try
                {
                    // Why clearing byte +0x80 on COlyCameraCtrlManager is required on OlyCameraLib.dll 1.1.0.49 (SDK 2.1.0):
                    // 1. During OlyCamLib_Initialize (RVA 0x481A0 -> sub_1800493B0), the library inspects GetModuleFileNameW(NULL)
                    //    against "OM Workspace.exe" (RVA 0x60668). When hosted in NINA.exe, sub_1800493B0 returns 0x80041004 and
                    //    leaves the global flag at RVA 0x767D6 set to 0.
                    // 2. OlyCamLib_CreateInstance (RVA 0x33737) allocates the 0xC0-byte COlyCameraCtrlManager via operator new(0xC0)
                    //    (unzeroed heap memory) and runs its constructor (RVA 0x356D0). When RVA 0x767D6 != 1 (or when 'locmodel'
                    //    is not parsed at RVA 0x35A4F), the constructor omits initializing BYTE PTR [this + 0x80] (m_bHasLocModel).
                    // 3. During ScanCameras, COlyCameraCtrlManager::IsModelSupported (RVA 0x38300) executes
                    //    'cmp BYTE PTR [rcx + 0x80], 0x1' (RVA 0x3833D) and, if equal to 1, filters cameras against the empty map
                    //    at [rcx + 0x70], rejecting all cameras.
                    // Having verified both the exact library version (1.1.0.49 / 2.1.0) and the instruction opcodes at RVA 0x35A4F
                    // and RVA 0x3833D above, we deterministically initialize byte +0x80 to 0.
                    byte oldLocFlag = Marshal.ReadByte(_pManager, 0x80);
                    Marshal.WriteByte(_pManager, 0x80, 0);
                    Logger.Info($"OlyCameraNative: Initialized verified OlyCameraLib.dll (SDK {apiMaj}.{apiMin}.{apiBld}, File {fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}), pMgr=0x{_pManager.ToInt64():X}, locmodelFlag=0 (was {oldLocFlag})");
                }
                catch (Exception ex)
                {
                    Logger.Error($"OlyCameraNative: Failed to initialize COlyCameraCtrlManager state: {ex.Message}");
                    return false;
                }

                _isInitialized = true;
                return true;
            }
        }

        public static void Shutdown()
        {
            lock (_syncLock)
            {
                if (_isInitialized)
                {
                    try
                    {
                        Raw_OlyCamLib_Finalize();
                    }
                    catch { }
                    _isInitialized = false;
                    _pManager = IntPtr.Zero;
                }
            }
        }

        // Delegate types for IOlyCameraCtrlManager VTable
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ScanCamerasDelegate(IntPtr pThis, out uint pCountOrStatus);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCameraCountDelegate(IntPtr pThis, out uint pCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetCameraInstanceDelegate(IntPtr pThis, uint index, out IntPtr ppCamera);

        private static T GetVTableDelegate<T>(IntPtr pInterface, int slotIndex) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(pInterface);
            IntPtr methodPtr = Marshal.ReadIntPtr(vtable, slotIndex * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
        }

        public static uint GetDetectedCameraCount()
        {
            if (!EnsureInitialized()) return 0;

            try
            {
                // Slot 3: ScanCameras
                var scan = GetVTableDelegate<ScanCamerasDelegate>(_pManager, 3);
                uint scanOut = 0;
                int hrScan = scan(_pManager, out scanOut);
                Logger.Info($"OlyCameraNative: Slot 3 (ScanCameras) hr=0x{hrScan:X}, scanOut={scanOut}");

                // Slot 4: GetCameraCount
                var countFunc = GetVTableDelegate<GetCameraCountDelegate>(_pManager, 4);
                uint count = 0;
                int hrCount = countFunc(_pManager, out count);
                Logger.Info($"OlyCameraNative: Slot 4 (GetCameraCount) hr=0x{hrCount:X}, count={count}");

                uint resolved = Math.Max(scanOut, count);
                if (resolved == 0)
                {
                    LogWpdDiagnostics();
                }
                return resolved;
            }
            catch (Exception ex)
            {
                Logger.Error($"OlyCameraNative: GetDetectedCameraCount exception: {ex}");
                return 0;
            }
        }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int WpdGetDevicesDelegate(IntPtr pThis, IntPtr pPnPDeviceIDs, ref uint pcPnPDeviceIDs);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint WpdReleaseDelegate(IntPtr pThis);

        private static void LogWpdDiagnostics()
        {
            try
            {
                Guid clsid = new Guid("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3"); // CLSID_PortableDeviceManager
                Guid iid = new Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40");   // IID_IPortableDeviceManager
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out IntPtr pWpd);
                if (hr != 0 || pWpd == IntPtr.Zero)
                {
                    Logger.Warning($"OlyCameraNative: WPD CoCreateInstance failed hr=0x{hr:X}");
                    return;
                }
                IntPtr vtbl = Marshal.ReadIntPtr(pWpd);
                var getDevs = Marshal.GetDelegateForFunctionPointer<WpdGetDevicesDelegate>(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
                uint wpdCount = 0;
                int hrDevs = getDevs(pWpd, IntPtr.Zero, ref wpdCount);
                Logger.Info($"OlyCameraNative: WPD GetDevices hr=0x{hrDevs:X}, wpdCount={wpdCount}");
                var rel = Marshal.GetDelegateForFunctionPointer<WpdReleaseDelegate>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(pWpd);
            }
            catch (Exception ex)
            {
                Logger.Warning($"OlyCameraNative: LogWpdDiagnostics error: {ex.Message}");
            }
        }

        public static IntPtr GetCameraInstance(uint index)
        {
            if (!EnsureInitialized()) return IntPtr.Zero;

            try
            {
                var getCam = GetVTableDelegate<GetCameraInstanceDelegate>(_pManager, 5);
                int hr = getCam(_pManager, index, out IntPtr pCamera);
                Logger.Info($"OlyCameraNative: Slot 5 (GetCameraInstance {index}) hr=0x{hr:X}, pCamera=0x{pCamera.ToInt64():X}");
                return (hr == 0) ? pCamera : IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Logger.Error($"OlyCameraNative: GetCameraInstance exception: {ex}");
                return IntPtr.Zero;
            }
        }
    }
}
