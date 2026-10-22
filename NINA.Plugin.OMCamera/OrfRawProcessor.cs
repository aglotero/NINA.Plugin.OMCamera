using System;
using System.Buffers.Binary;
using System.Text;

namespace NINA.Plugin.OMCamera
{
    /// <summary>
    /// Validates Olympus / OM System .ORF RAW buffers and performs deterministic
    /// bit-depth scaling from the RAW decoder's defined linear sensor range to 16-bit ADU.
    /// </summary>
    public static class OrfRawProcessor
    {
        public const int DefaultOrfSensorBitDepth = 12;
        public const int OutputBitDepth = 16;

        // Little-endian Olympus ORF magic headers: "IIRO" (0x4F52) and "IIRS" (0x5352)
        private const ushort TiffLittleEndianByteOrder = 0x4949; // "II"
        private const ushort OrfMagicStandard = 0x4F52;          // "RO" (0x52, 0x4F)
        private const ushort OrfMagicSub = 0x5352;               // "RS" (0x52, 0x53)

        // TIFF / Exif / Olympus MakerNote tags
        private const ushort TagExifIfdPointer = 0x8769;
        private const ushort TagMakerNote = 0x927C;
        private const ushort TagOlympusImageProcessingIfd = 0x2040;
        private const ushort TagOlympusValidBits = 0x0611;

        /// <summary>
        /// Checks whether the buffer begins with a valid little-endian Olympus/OM .ORF header ("IIRO" or "IIRS")
        /// and has a valid IFD0 offset within the buffer.
        /// </summary>
        public static bool IsValidOrfHeader(ReadOnlySpan<byte> data)
        {
            if (data.Length < 32)
                return false;

            ushort byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2));
            if (byteOrder != TiffLittleEndianByteOrder)
                return false;

            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
            if (magic != OrfMagicStandard && magic != OrfMagicSub)
                return false;

            uint ifd0Offset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
            if (ifd0Offset < 8 || ifd0Offset + 2 > (uint)data.Length)
                return false;

            ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((int)ifd0Offset, 2));
            if (entryCount == 0 || ifd0Offset + 2 + ((uint)entryCount * 12) > (uint)data.Length)
                return false;

            return true;
        }

        /// <summary>
        /// Validates that the exposure payload is a non-empty .ORF RAW file and not a JPEG preview or corrupt buffer.
        /// Throws <see cref="InvalidOperationException"/> when valid RAW data is not present.
        /// </summary>
        public static void ValidateOrfBuffer(byte[]? rawBytes)
        {
            if (rawBytes == null || rawBytes.Length == 0)
            {
                throw new InvalidOperationException("Camera exposure failed: no RAW (.ORF) image data was received from the camera.");
            }

            if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xD8)
            {
                throw new InvalidOperationException("Camera exposure failed: received an 8-bit JPEG stream instead of a 12-bit RAW (.ORF) file. Ensure camera image quality is set to RAW.");
            }

            if (!IsValidOrfHeader(rawBytes))
            {
                throw new InvalidOperationException($"Camera exposure failed: buffer ({rawBytes.Length} bytes) does not contain a valid Olympus/OM .ORF RAW header.");
            }
        }

        /// <summary>
        /// Reads the defined sensor bit depth from the .ORF MakerNote ImageProcessing IFD (tag 0x0611 ValidBits)
        /// if present, or returns <see cref="DefaultOrfSensorBitDepth"/> (12 bits), which is the defined linear ADU
        /// output range (0..4095) produced by FreeImage (RAW_UNPROCESSED) and dcraw (-4 -D -T) for Olympus/OM .ORF files.
        /// </summary>
        public static int GetDefinedSensorBitDepth(ReadOnlySpan<byte> orfBytes)
        {
            if (!IsValidOrfHeader(orfBytes))
            {
                throw new InvalidOperationException("Cannot determine sensor bit depth from an invalid .ORF header.");
            }

            try
            {
                uint ifd0Offset = BinaryPrimitives.ReadUInt32LittleEndian(orfBytes.Slice(4, 4));
                if (TryFindIfdTagOffsetAndValue(orfBytes, ifd0Offset, TagExifIfdPointer, out _, out uint exifIfdOffset) &&
                    exifIfdOffset >= 8 && exifIfdOffset + 2 <= (uint)orfBytes.Length)
                {
                    if (TryFindIfdTagOffsetAndValue(orfBytes, exifIfdOffset, TagMakerNote, out uint makerCount, out uint makerOffset) &&
                        makerCount >= 16 && makerOffset >= 8 && makerOffset + makerCount <= (uint)orfBytes.Length)
                    {
                        // Olympus / OM System MakerNote header:
                        // - "OLYMPUS\0II\x03\x00" (12-byte header, offsets relative to makerOffset)
                        // - "OM SYSTEM\0\0\0II\x04\x00" (16-byte header, offsets relative to makerOffset)
                        int subIfdStart = -1;
                        ReadOnlySpan<byte> makerSpan = orfBytes.Slice((int)makerOffset, (int)makerCount);
                        if (makerSpan.Length >= 14 &&
                            makerSpan[0] == (byte)'O' && makerSpan[1] == (byte)'L' && makerSpan[2] == (byte)'Y' &&
                            makerSpan[8] == (byte)'I' && makerSpan[9] == (byte)'I')
                        {
                            subIfdStart = (int)makerOffset + 12;
                        }
                        else if (makerSpan.Length >= 18 &&
                                 makerSpan[0] == (byte)'O' && makerSpan[1] == (byte)'M' && makerSpan[2] == (byte)' ' &&
                                 makerSpan[12] == (byte)'I' && makerSpan[13] == (byte)'I')
                        {
                            subIfdStart = (int)makerOffset + 16;
                        }

                        if (subIfdStart > 0 && subIfdStart + 2 <= orfBytes.Length)
                        {
                            if (TryFindIfdTagOffsetAndValue(orfBytes, (uint)subIfdStart, TagOlympusImageProcessingIfd, out _, out uint imgProcRelOffset))
                            {
                                uint imgProcOffset = makerOffset + imgProcRelOffset;
                                if (imgProcOffset >= 8 && imgProcOffset + 2 <= (uint)orfBytes.Length &&
                                    TryFindIfdTagEntry(orfBytes, imgProcOffset, TagOlympusValidBits, out ushort type, out uint count, out uint valOrOffset))
                                {
                                    // Tag 0x0611 (ValidBits) is SHORT[2], where the first uint16 is the valid bit depth (12 or 14)
                                    ushort validBits = 0;
                                    if (type == 3 && count >= 1)
                                    {
                                        validBits = (ushort)(valOrOffset & 0xFFFF);
                                    }
                                    if (validBits >= 10 && validBits <= 16)
                                    {
                                        return validBits;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fall back to the standard 12-bit Olympus/OM .ORF sensor bit depth
            }

            return DefaultOrfSensorBitDepth;
        }

        /// <summary>
        /// Deterministically scales decoded RAW sensor ADU values to the 16-bit container range (0..65535)
        /// using the decoder's defined bit depth (12-bit -> &lt;&lt; 4). Does not sample image brightness, ensuring
        /// bias, dark, flat, and light frames receive identical scaling.
        /// </summary>
        public static ushort[] ScaleDecodedRawPixelsTo16Bit(ReadOnlySpan<ushort> unscaledPixels, int sensorBitDepth = DefaultOrfSensorBitDepth)
        {
            if (unscaledPixels.IsEmpty)
            {
                throw new InvalidOperationException("RAW decoder returned an empty pixel buffer.");
            }

            if (sensorBitDepth < 8 || sensorBitDepth > OutputBitDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(sensorBitDepth), $"Sensor bit depth must be between 8 and {OutputBitDepth}, but was {sensorBitDepth}.");
            }

            ushort[] scaled = new ushort[unscaledPixels.Length];
            int shift = OutputBitDepth - sensorBitDepth;
            if (shift == 0)
            {
                unscaledPixels.CopyTo(scaled);
                return scaled;
            }

            for (int i = 0; i < unscaledPixels.Length; i++)
            {
                int val = unscaledPixels[i] << shift;
                scaled[i] = (ushort)(val > ushort.MaxValue ? ushort.MaxValue : val);
            }

            return scaled;
        }

        /// <summary>
        /// Patches the in-memory .ORF Make/Model strings so N.I.N.A.'s bundled FreeImage 3.19.0 (LibRaw)
        /// recognizes post-rebrand OM Digital Solutions cameras using the equivalent Olympus decompression table.
        /// </summary>
        public static byte[] PatchOrfHeaderForLegacyLibRaw(byte[] orfBytes)
        {
            ValidateOrfBuffer(orfBytes);

            byte[] copy = (byte[])orfBytes.Clone();
            int scanLimit = Math.Min(copy.Length, 16384);
            ReplaceAsciiInPlace(copy, scanLimit, "OM Digital Solutions", "OLYMPUS CORPORATION ");
            ReplaceAsciiInPlace(copy, scanLimit, "OM-1MarkII", "E-M1MarkII");
            ReplaceAsciiInPlace(copy, scanLimit, "OM-5MarkII", "E-M5MarkII");
            ReplaceAsciiInPlace(copy, scanLimit, "OM-1      ", "E-M1MarkII");
            ReplaceAsciiInPlace(copy, scanLimit, "OM-5      ", "E-M5MarkII");
            ReplaceAsciiInPlace(copy, scanLimit, "OM-3      ", "E-M1MarkII");
            return copy;
        }

        private static void ReplaceAsciiInPlace(byte[] buf, int limit, string target, string replacement)
        {
            if (target.Length != replacement.Length) return;
            byte[] tBytes = Encoding.ASCII.GetBytes(target);
            byte[] rBytes = Encoding.ASCII.GetBytes(replacement);
            for (int i = 0; i <= limit - tBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < tBytes.Length; j++)
                {
                    if (buf[i + j] != tBytes[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    Buffer.BlockCopy(rBytes, 0, buf, i, rBytes.Length);
                }
            }
        }

        private static bool TryFindIfdTagOffsetAndValue(ReadOnlySpan<byte> data, uint ifdOffset, ushort targetTag, out uint count, out uint valueOrOffset)
        {
            return TryFindIfdTagEntry(data, ifdOffset, targetTag, out _, out count, out valueOrOffset);
        }

        private static bool TryFindIfdTagEntry(ReadOnlySpan<byte> data, uint ifdOffset, ushort targetTag, out ushort fieldType, out uint count, out uint valueOrOffset)
        {
            fieldType = 0;
            count = 0;
            valueOrOffset = 0;

            if (ifdOffset + 2 > (uint)data.Length)
                return false;

            ushort numEntries = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice((int)ifdOffset, 2));
            uint entriesStart = ifdOffset + 2;
            if (entriesStart + ((uint)numEntries * 12) > (uint)data.Length)
                return false;

            for (int i = 0; i < numEntries; i++)
            {
                int pos = (int)(entriesStart + (i * 12));
                ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
                if (tag == targetTag)
                {
                    fieldType = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos + 2, 2));
                    count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
                    valueOrOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 8, 4));
                    return true;
                }
            }

            return false;
        }
    }
}
