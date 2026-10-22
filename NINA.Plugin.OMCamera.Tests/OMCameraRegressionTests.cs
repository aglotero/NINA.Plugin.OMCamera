using System;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.OMCamera.Native;
using Xunit;

namespace NINA.Plugin.OMCamera.Tests
{
    public class OMCameraRegressionTests
    {
        [Fact]
        public void ValidateNativeLibrary_AcceptsVerifiedOlyCameraLibVersionAndSignature()
        {
            bool versionOk = OlyCameraNative.ValidateNativeLibraryVersion(
                OlyCameraNative.SupportedApiMajor,
                OlyCameraNative.SupportedApiMinor,
                OlyCameraNative.SupportedApiBuild,
                OlyCameraNative.SupportedFileMajor,
                OlyCameraNative.SupportedFileMinor,
                OlyCameraNative.SupportedFileBuild,
                OlyCameraNative.SupportedFilePrivate,
                out string verError);

            Assert.True(versionOk);
            Assert.Equal(string.Empty, verError);

            bool sigOk = OlyCameraNative.ValidateLocModelSignature(
                OlyCameraNative.ExpectedCtorInstruction,
                OlyCameraNative.ExpectedCheckInstruction,
                out string sigError);

            Assert.True(sigOk);
            Assert.Equal(string.Empty, sigError);

            // Should not throw when both version and instruction signatures match
            OlyCameraNative.EnsureSupportedNativeLibrary(
                OlyCameraNative.SupportedApiMajor,
                OlyCameraNative.SupportedApiMinor,
                OlyCameraNative.SupportedApiBuild,
                OlyCameraNative.SupportedFileMajor,
                OlyCameraNative.SupportedFileMinor,
                OlyCameraNative.SupportedFileBuild,
                OlyCameraNative.SupportedFilePrivate,
                OlyCameraNative.ExpectedCtorInstruction,
                OlyCameraNative.ExpectedCheckInstruction);
        }

        [Theory]
        [InlineData(1u, 0u, 49u, 1, 1, 0, 49)]
        [InlineData(1u, 1u, 48u, 1, 1, 0, 49)]
        [InlineData(1u, 1u, 50u, 1, 1, 0, 49)]
        [InlineData(2u, 1u, 0u, 1, 1, 0, 49)]
        [InlineData(1u, 1u, 49u, 1, 1, 0, 48)]
        [InlineData(1u, 1u, 49u, 1, 1, 0, 50)]
        [InlineData(1u, 1u, 49u, 1, 2, 0, 49)]
        [InlineData(1u, 1u, 49u, 2, 0, 0, 0)]
        public void ValidateNativeLibrary_RejectsUnsupportedApiOrFileVersions(
            uint apiMajor,
            uint apiMinor,
            uint apiBuild,
            int fileMajor,
            int fileMinor,
            int fileBuild,
            int filePrivate)
        {
            bool ok = OlyCameraNative.ValidateNativeLibraryVersion(
                apiMajor,
                apiMinor,
                apiBuild,
                fileMajor,
                fileMinor,
                fileBuild,
                filePrivate,
                out string error);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));

            Assert.Throws<NotSupportedException>(() =>
                OlyCameraNative.EnsureSupportedNativeLibrary(
                    apiMajor,
                    apiMinor,
                    apiBuild,
                    fileMajor,
                    fileMinor,
                    fileBuild,
                    filePrivate,
                    OlyCameraNative.ExpectedCtorInstruction,
                    OlyCameraNative.ExpectedCheckInstruction));
        }

        [Fact]
        public void ValidateLocModelSignature_RejectsModifiedInstructionBytes()
        {
            byte[] validCtor = OlyCameraNative.ExpectedCtorInstruction.ToArray();
            byte[] validCheck = OlyCameraNative.ExpectedCheckInstruction.ToArray();

            // Mutate the +0x80 struct offset byte in constructor instruction
            byte[] badCtor = (byte[])validCtor.Clone();
            badCtor[3] = 0x88;

            Assert.False(OlyCameraNative.ValidateLocModelSignature(badCtor, validCheck, out string err1));
            Assert.Contains("0x35A4F", err1, StringComparison.OrdinalIgnoreCase);

            // Mutate the +0x80 struct offset byte in IsModelSupported instruction
            byte[] badCheck = (byte[])validCheck.Clone();
            badCheck[2] = 0x90;

            Assert.False(OlyCameraNative.ValidateLocModelSignature(validCtor, badCheck, out string err2));
            Assert.Contains("0x3833D", err2, StringComparison.OrdinalIgnoreCase);

            Assert.Throws<NotSupportedException>(() =>
                OlyCameraNative.EnsureSupportedNativeLibrary(
                    OlyCameraNative.SupportedApiMajor,
                    OlyCameraNative.SupportedApiMinor,
                    OlyCameraNative.SupportedApiBuild,
                    OlyCameraNative.SupportedFileMajor,
                    OlyCameraNative.SupportedFileMinor,
                    OlyCameraNative.SupportedFileBuild,
                    OlyCameraNative.SupportedFilePrivate,
                    badCtor,
                    validCheck));
        }

        [Fact]
        public async Task CreateExposureDataFromBytes_RejectsNullEmptyJpegAndCorruptBuffers_WithoutFallback()
        {
            var camera = new OMCamera(null, null);
            var meta = new ImageMetaData();

            // 1. Null / empty buffer must fail
            Assert.Throws<InvalidOperationException>(() => OrfRawProcessor.ValidateOrfBuffer(null));
            Assert.Throws<InvalidOperationException>(() => OrfRawProcessor.ValidateOrfBuffer(Array.Empty<byte>()));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                camera.CreateExposureDataFromBytes(Array.Empty<byte>(), meta, CancellationToken.None));

            // 2. Direct 8-bit JPEG stream must fail in still exposure path (never decoded as science frame)
            byte[] fakeJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0xFF, 0xD9 };
            var jpegEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                camera.CreateExposureDataFromBytes(fakeJpeg, meta, CancellationToken.None));
            Assert.Contains("JPEG", jpegEx.Message, StringComparison.OrdinalIgnoreCase);

            // 3. Corrupt / non-ORF bytes must fail
            byte[] randomBytes = new byte[1024];
            new Random(42).NextBytes(randomBytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                camera.CreateExposureDataFromBytes(randomBytes, meta, CancellationToken.None));

            // 4. Valid ORF header containing an embedded JPEG preview but un-decodable RAW data must fail
            // (verifying embedded JPEGs and raw trailing bytes are never returned as substitute exposure data)
            byte[] syntheticOrfWithEmbeddedJpeg = BuildMinimalOrfHeaderWithEmbeddedJpeg(validBits: 12);
            Assert.True(OrfRawProcessor.IsValidOrfHeader(syntheticOrfWithEmbeddedJpeg));

            var failingFactory = FakeExposureDataFactory.Create();
            var cameraWithFactory = new OMCamera(null, failingFactory.Instance)
            {
                RawDecoderOverride = (_, _, _, _) => Task.FromException<IImageData?>(new InvalidOperationException("Simulated FreeImage/DCRaw failure"))
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                cameraWithFactory.CreateExposureDataFromBytes(syntheticOrfWithEmbeddedJpeg, meta, CancellationToken.None));
            Assert.False(failingFactory.CreatedSubstituteImageArray);
        }

        [Fact]
        public async Task CreateExposureDataFromBytes_ScalesDarkAndLightFramesConsistentlyViaExposureDataFactory()
        {
            byte[] validOrf12 = BuildMinimalOrfHeaderWithEmbeddedJpeg(validBits: 12);
            var meta = new ImageMetaData();

            // 1. Dark frame (max pixel 264 <= 4095)
            ushort[] darkPixels = { 250, 256, 260, 264 };
            var darkFactory = FakeExposureDataFactory.Create();
            var darkCamera = new OMCamera(null, darkFactory.Instance)
            {
                RawDecoderOverride = (_, _, _, _) => Task.FromResult<IImageData?>(FakeImageData.Create(darkPixels, 2, 2))
            };
            await darkCamera.CreateExposureDataFromBytes(validOrf12, meta, CancellationToken.None);
            Assert.Equal(new ushort[] { 4000, 4096, 4160, 4224 }, darkFactory.LastCapturedScaledPixels);
            Assert.Equal(16, darkFactory.LastCapturedBitDepth);
            Assert.True(darkFactory.LastCapturedIsBayered);

            // 2. Bright light frame (includes 256 sky background + 4095 saturated pixel)
            ushort[] lightPixels = { 256, 1024, 2048, 4095 };
            var lightFactory = FakeExposureDataFactory.Create();
            var lightCamera = new OMCamera(null, lightFactory.Instance)
            {
                RawDecoderOverride = (_, _, _, _) => Task.FromResult<IImageData?>(FakeImageData.Create(lightPixels, 2, 2))
            };
            await lightCamera.CreateExposureDataFromBytes(validOrf12, meta, CancellationToken.None);
            Assert.Equal(new ushort[] { 4096, 16384, 32768, 65520 }, lightFactory.LastCapturedScaledPixels);
            Assert.Equal(darkFactory.LastCapturedScaledPixels![1], lightFactory.LastCapturedScaledPixels![0]);
        }

        [Fact]
        public void ScaleDecodedRawPixelsTo16Bit_ScalesDarksFlatsAndLightsConsistently()
        {
            // Dark / bias frame: all pixels near black level (~256 ADU in 12-bit, well below 4095)
            ushort[] dark12 = { 0, 250, 256, 262 };
            // Flat frame: mid-histogram (~2048 ADU in 12-bit)
            ushort[] flat12 = { 1900, 2000, 2048, 2150 };
            // Light frame: background sky (260 ADU) + bright stars + saturated core (4095 ADU)
            ushort[] light12 = { 260, 1024, 3500, 4095 };

            ushort[] scaledDark = OrfRawProcessor.ScaleDecodedRawPixelsTo16Bit(dark12, sensorBitDepth: 12);
            ushort[] scaledFlat = OrfRawProcessor.ScaleDecodedRawPixelsTo16Bit(flat12, sensorBitDepth: 12);
            ushort[] scaledLight = OrfRawProcessor.ScaleDecodedRawPixelsTo16Bit(light12, sensorBitDepth: 12);

            // Every frame type must be scaled deterministically by << 4 (x16) regardless of sampled brightness
            Assert.Equal(new ushort[] { 0, 4000, 4096, 4192 }, scaledDark);
            Assert.Equal(new ushort[] { 30400, 32000, 32768, 34400 }, scaledFlat);
            Assert.Equal(new ushort[] { 4160, 16384, 56000, 65520 }, scaledLight);

            // A 256 ADU pedestal pixel in a dark frame and a 256 ADU background pixel in a bright light frame
            // must produce the exact same 16-bit value (4096 ADU).
            ushort[] lightWith256 = { 256, 4095 };
            ushort[] scaledLightWith256 = OrfRawProcessor.ScaleDecodedRawPixelsTo16Bit(lightWith256, sensorBitDepth: 12);
            Assert.Equal(scaledDark[2], scaledLightWith256[0]);
        }

        [Fact]
        public void GetDefinedSensorBitDepth_ReadsMakerNoteValidBitsOrDefaultsTo12Bit()
        {
            byte[] orf12 = BuildMinimalOrfHeaderWithEmbeddedJpeg(validBits: 12);
            Assert.Equal(12, OrfRawProcessor.GetDefinedSensorBitDepth(orf12));

            byte[] orf14 = BuildMinimalOrfHeaderWithEmbeddedJpeg(validBits: 14);
            Assert.Equal(14, OrfRawProcessor.GetDefinedSensorBitDepth(orf14));

            // 14-bit High-Res mode scales by << 2 (x4)
            ushort[] raw14 = { 1024, 16383 };
            ushort[] scaled14 = OrfRawProcessor.ScaleDecodedRawPixelsTo16Bit(raw14, sensorBitDepth: 14);
            Assert.Equal(new ushort[] { 4096, 65532 }, scaled14);
        }

        [Fact]
        public void AssemblyMetadata_MinimumApplicationVersion_MatchesReferencedNinaVersion()
        {
            Assembly pluginAsm = typeof(OMCameraPlugin).Assembly;
            var minAppVerAttr = pluginAsm
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "MinimumApplicationVersion");

            Assert.NotNull(minAppVerAttr);
            Assert.Equal("3.2.0.9001", minAppVerAttr!.Value);
        }

        [Fact]
        public void EnsureInitialized_ValidatesRealOlyCameraLibWhenInstalled()
        {
            const string installedDll = @"C:\Program Files (x86)\OMDS\OM Workspace\OlyCameraLib.dll";
            if (!System.IO.File.Exists(installedDll))
            {
                // Skip on CI runners where OM Workspace is not installed
                return;
            }

            try
            {
                bool initialized = OlyCameraNative.EnsureInitialized();
                Assert.True(initialized);
            }
            finally
            {
                OlyCameraNative.Shutdown();
            }
        }

        private static byte[] BuildMinimalOrfHeaderWithEmbeddedJpeg(ushort validBits)
        {
            byte[] buf = new byte[512];
            // "IIRO" (0x49, 0x49, 0x52, 0x4F) + IFD0 offset = 8
            buf[0] = (byte)'I';
            buf[1] = (byte)'I';
            buf[2] = (byte)'R';
            buf[3] = (byte)'O';
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), 8);

            // IFD0 at offset 8: 1 entry (TagExifIfdPointer 0x8769 -> offset 32)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10, 2), 0x8769);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), 4); // LONG
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(14, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(18, 4), 32);

            // ExifIFD at offset 32: 1 entry (TagMakerNote 0x927C -> offset 64, length 128)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(32, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(34, 2), 0x927C);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(36, 2), 7); // UNDEFINED
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(38, 4), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(42, 4), 64);

            // Olympus MakerNote at offset 64: "OLYMPUS\0II\x03\x00" (12 bytes)
            byte[] olyHdr = { (byte)'O', (byte)'L', (byte)'Y', (byte)'M', (byte)'P', (byte)'U', (byte)'S', 0, (byte)'I', (byte)'I', 3, 0 };
            Buffer.BlockCopy(olyHdr, 0, buf, 64, olyHdr.Length);

            // MakerNote IFD at offset 76 (64 + 12): 1 entry (Tag 0x2040 ImageProcessing IFD -> relative offset 40 -> abs offset 104)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(76, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(78, 2), 0x2040);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(80, 2), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(82, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(86, 4), 40);

            // ImageProcessing IFD at offset 104 (64 + 40): 1 entry (Tag 0x0611 ValidBits = validBits)
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(104, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(106, 2), 0x0611);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(108, 2), 3); // SHORT
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(110, 4), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(114, 2), validBits);

            // Embed a fake JPEG stream (FF D8 FF E0 ... FF D9) inside the buffer to ensure fallback never extracts it
            buf[200] = 0xFF;
            buf[201] = 0xD8;
            buf[202] = 0xFF;
            buf[203] = 0xE0;
            buf[450] = 0xFF;
            buf[451] = 0xD9;

            return buf;
        }
    }

    public class FakeExposureDataFactory : DispatchProxy
    {
        public IExposureDataFactory Instance => (IExposureDataFactory)(object)this;
        public ushort[]? LastCapturedScaledPixels { get; private set; }
        public int LastCapturedBitDepth { get; private set; }
        public bool LastCapturedIsBayered { get; private set; }
        public bool CreatedSubstituteImageArray { get; private set; }

        public static FakeExposureDataFactory Create()
        {
            return (FakeExposureDataFactory)(object)DispatchProxy.Create<IExposureDataFactory, FakeExposureDataFactory>();
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null) return null;

            if (targetMethod.Name == nameof(IExposureDataFactory.CreateImageArrayExposureData))
            {
                CreatedSubstituteImageArray = true;
                if (args != null && args.Length >= 5)
                {
                    LastCapturedScaledPixels = args[0] as ushort[];
                    LastCapturedBitDepth = args[3] is int bd ? bd : 0;
                    LastCapturedIsBayered = args[4] is bool b && b;
                }
                return null;
            }

            if (targetMethod.Name == nameof(IExposureDataFactory.CreateImageArrayExposureDataFromBitmapSource))
            {
                CreatedSubstituteImageArray = true;
                throw new InvalidOperationException("CreateImageArrayExposureDataFromBitmapSource should not be called on RAW exposure path.");
            }

            return null;
        }
    }

    public class FakeImageData : DispatchProxy
    {
        private ImageProperties? _props;
        private IImageArray? _data;

        public static IImageData Create(ushort[] pixels, int width, int height)
        {
            var proxy = (FakeImageData)(object)DispatchProxy.Create<IImageData, FakeImageData>();
            proxy._props = new ImageProperties(width, height, 16, true, 200, 0);
            proxy._data = FakeImageArray.Create(pixels);
            return (IImageData)(object)proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_" + nameof(IImageData.Properties))
                return _props;
            if (targetMethod?.Name == "get_" + nameof(IImageData.Data))
                return _data;
            return null;
        }
    }

    public class FakeImageArray : DispatchProxy
    {
        private ushort[]? _flat;

        public static IImageArray Create(ushort[] pixels)
        {
            var proxy = (FakeImageArray)(object)DispatchProxy.Create<IImageArray, FakeImageArray>();
            proxy._flat = pixels;
            return (IImageArray)(object)proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_" + nameof(IImageArray.FlatArray))
                return _flat;
            return null;
        }
    }

    public class NoopProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
