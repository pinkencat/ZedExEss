using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.Input;

namespace ZedExEss.ZxNext.Core
{
    /// <summary>A chunk this version does not understand, kept so it is not lost.</summary>
    public sealed record ZxNextSnapshotUnknownChunk(string Id, byte[] Payload);

    /// <summary>
    /// Reads and writes whole ZX Spectrum Next machine states as files.
    /// </summary>
    /// <remarks>
    /// <para><b>Layout.</b> An uncompressed header - the signature <c>ZXNS</c>, a format
    /// version, flags and the body's uncompressed length - followed by the body, which is a
    /// sequence of chunks: a four-character id, a length, and that many bytes. The header is
    /// outside the compression so a file can be identified and rejected before anything is
    /// inflated.</para>
    ///
    /// <para><b>Versioning.</b> The version is checked before the body is touched and a file
    /// from a newer format is refused by name rather than misread. Within one version, new
    /// state is added as a new chunk id, which older code skips.</para>
    ///
    /// <para><b>Unknown data.</b> Chunks this version does not know are carried on the
    /// snapshot and written back out, so loading a file from a newer build and saving it
    /// again does not quietly discard what that build recorded. A snapshot captured from a
    /// running machine has none, because there is nothing to have preserved.</para>
    ///
    /// <para><b>Failure.</b> Every refusal names what was wrong -
    /// <see cref="InvalidDataException"/> for a bad signature, an unreadable version, a
    /// truncated chunk or a missing chunk the state cannot do without. Nothing is restored
    /// into a machine until the whole file has been read, so a bad file cannot leave a
    /// half-changed machine.</para>
    ///
    /// <para><b>Not interoperable, deliberately.</b> This is our own container. Reading
    /// another emulator's Next snapshot means implementing that emulator's format against
    /// its specification, which is a separate piece of work; inventing a half-guess at one
    /// would be worse than a documented format of our own.</para>
    /// </remarks>
    public static class ZxNextSnapshotFile
    {
        /// <summary>The signature every file starts with.</summary>
        public static readonly byte[] Signature = "ZXNS"u8.ToArray();

        /// <summary>The format this build writes, and the newest it can read.</summary>
        public const ushort FormatVersion = 1;

        /// <summary>Set in the header flags when the body is deflated.</summary>
        private const ushort BodyDeflated = 0x0001;

        private const int HeaderLength = 12;

        /// <summary>The conventional file extension.</summary>
        public const string Extension = ".zxns";

        private const string CpuChunk = "CPU_";
        private const string ClockChunk = "CLCK";
        private const string RamChunk = "RAM_";
        private const string MemoryBusChunk = "MBUS";
        private const string PagingChunk = "PAGE";
        private const string RegistersChunk = "NREG";
        private const string UlaTimingChunk = "ULAT";
        private const string RasterChunk = "RAST";
        private const string PalettesChunk = "PALS";
        private const string Layer2Chunk = "LYR2";
        private const string TilemapChunk = "TILE";
        private const string SpritesChunk = "SPRT";
        private const string DmaChunk = "DMA_";
        private const string BusArbiterChunk = "ARBT";
        private const string DivMmcChunk = "DIVM";
        private const string MultifaceChunk = "MFAC";
        private const string NmiRoutingChunk = "NMIS";
        private const string AudioChunk = "AUDI";
        private const string InputChunk = "INPT";
        private const string PortBusChunk = "PORT";
        private const string FloatingBusChunk = "FLBS";
        private const string MediaChunk = "MEDA";

        public static void Save(Stream stream, ZxNextMachineSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(snapshot);

            byte[] body = BuildBody(snapshot);

            var header = new byte[HeaderLength];
            Signature.CopyTo(header, 0);
            WriteUInt16(header, 4, FormatVersion);
            WriteUInt16(header, 6, BodyDeflated);
            WriteUInt32(header, 8, (uint)body.Length);
            stream.Write(header);

            using var deflate = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            deflate.Write(body);
        }

        public static ZxNextMachineSnapshot Load(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var header = new byte[HeaderLength];
            if (stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) != HeaderLength)
            {
                throw new InvalidDataException(
                    "This is not a ZX Spectrum Next snapshot: it is too short to hold a header.");
            }

            for (int index = 0; index < Signature.Length; index++)
            {
                if (header[index] != Signature[index])
                {
                    throw new InvalidDataException(
                        "This is not a ZX Spectrum Next snapshot: the file signature does not match.");
                }
            }

            ushort version = ReadUInt16(header, 4);
            if (version > FormatVersion)
            {
                throw new InvalidDataException(
                    $"This snapshot is format version {version}; this build reads up to "
                        + $"{FormatVersion}.");
            }

            ushort flags = ReadUInt16(header, 6);
            uint bodyLength = ReadUInt32(header, 8);
            byte[] body = ReadBody(stream, flags, bodyLength);
            return ParseBody(body);
        }

        private static byte[] ReadBody(Stream stream, ushort flags, uint bodyLength)
        {
            if (bodyLength > 64 * 1024 * 1024)
            {
                throw new InvalidDataException(
                    $"This snapshot claims a {bodyLength}-byte body, which is beyond anything a "
                        + "Next machine can produce.");
            }

            var body = new byte[bodyLength];
            if ((flags & BodyDeflated) != 0)
            {
                using var inflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
                if (inflate.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) != body.Length)
                {
                    throw new InvalidDataException("This snapshot's compressed body is truncated.");
                }

                return body;
            }

            if (stream.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) != body.Length)
            {
                throw new InvalidDataException("This snapshot's body is truncated.");
            }

            return body;
        }

        private static byte[] BuildBody(ZxNextMachineSnapshot snapshot)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

            WriteChunk(writer, CpuChunk, chunk => WriteCpu(chunk, snapshot.Cpu));
            WriteChunk(writer, ClockChunk, chunk =>
            {
                chunk.Write(snapshot.Clock.MasterTicks);
                chunk.Write((byte)snapshot.Clock.CpuSpeed);
            });
            WriteChunk(writer, RamChunk, chunk => WriteByteArray(chunk, snapshot.PhysicalRam));
            WriteChunk(writer, MemoryBusChunk, chunk => WriteMemoryBus(chunk, snapshot.MemoryBus));
            WriteChunk(writer, PagingChunk, chunk =>
            {
                chunk.Write(snapshot.Paging.Port7Ffd);
                chunk.Write(snapshot.Paging.PortDFfd);
                chunk.Write(snapshot.Paging.Port1Ffd);
                chunk.Write(snapshot.Paging.PortEff7);
            });
            WriteChunk(writer, RegistersChunk, chunk => WriteRegisters(chunk, snapshot.Registers));
            WriteChunk(writer, UlaTimingChunk, chunk => WriteUlaTiming(chunk, snapshot.UlaTiming));
            WriteChunk(writer, RasterChunk, chunk => WriteRaster(chunk, snapshot.Raster));
            WriteChunk(writer, PalettesChunk, chunk => WritePalettes(chunk, snapshot.Palettes));
            WriteChunk(writer, Layer2Chunk, chunk => WriteLayer2(chunk, snapshot.Layer2));
            WriteChunk(writer, TilemapChunk, chunk => WriteTilemap(chunk, snapshot.Tilemap));
            WriteChunk(writer, SpritesChunk, chunk => WriteSprites(chunk, snapshot.Sprites));
            WriteChunk(writer, DmaChunk, chunk => WriteDma(chunk, snapshot.Dma));
            WriteChunk(writer, BusArbiterChunk, chunk =>
            {
                chunk.Write((byte)snapshot.BusArbiter.Owner);
                chunk.Write(snapshot.BusArbiter.CpuOwnedMasterTicks);
                chunk.Write(snapshot.BusArbiter.DmaOwnedMasterTicks);
                chunk.Write(snapshot.BusArbiter.DmaGrantCount);
                chunk.Write(snapshot.BusArbiter.CpuTstatesAtLastDmaGrant);
            });
            WriteChunk(writer, DivMmcChunk, chunk => WriteDivMmc(chunk, snapshot.DivMmc));
            WriteChunk(writer, MultifaceChunk, chunk =>
            {
                chunk.Write(snapshot.Multiface.Enabled);
                chunk.Write(snapshot.Multiface.NmiActive);
                chunk.Write(snapshot.Multiface.IsMapped);
                chunk.Write(snapshot.Multiface.Invisible);
            });
            WriteChunk(writer, NmiRoutingChunk, chunk =>
                chunk.Write(snapshot.NmiRouting.PendingNmiSource));
            WriteChunk(writer, AudioChunk, chunk => WriteAudio(chunk, snapshot.Audio));
            WriteChunk(writer, InputChunk, chunk => WriteInput(chunk, snapshot.Input));
            WriteChunk(writer, PortBusChunk, chunk =>
            {
                chunk.Write(snapshot.PortBus.LastWrittenPort);
                chunk.Write(snapshot.PortBus.HasLastWrittenPort);
                chunk.Write(snapshot.PortBus.LastWrittenValue);
                chunk.Write(snapshot.PortBus.SelectedRegister);
                chunk.Write(snapshot.PortBus.UlaOutputLatch);
            });
            WriteChunk(writer, FloatingBusChunk, chunk =>
            {
                chunk.Write(snapshot.FloatingBus.UlaFetchValue);
                chunk.Write(snapshot.FloatingBus.Plus3CpuData);
                chunk.Write(snapshot.FloatingBus.UlaDriving);
            });
            WriteChunk(writer, MediaChunk, chunk => WriteMedia(chunk, snapshot.Media));

            // Anything a newer build recorded goes back out after what this one understands.
            foreach (ZxNextSnapshotUnknownChunk unknown in snapshot.UnknownChunks)
            {
                WriteRawChunk(writer, unknown.Id, unknown.Payload);
            }

            writer.Flush();
            return buffer.ToArray();
        }

        private static ZxNextMachineSnapshot ParseBody(byte[] body)
        {
            var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var unknown = new List<ZxNextSnapshotUnknownChunk>();

            int offset = 0;
            while (offset < body.Length)
            {
                if (offset + 8 > body.Length)
                {
                    throw new InvalidDataException(
                        $"This snapshot has {body.Length - offset} trailing byte(s), too few for a "
                            + "chunk header.");
                }

                string id = Encoding.ASCII.GetString(body, offset, 4);
                uint length = ReadUInt32(body, offset + 4);
                offset += 8;
                if (length > body.Length - offset)
                {
                    throw new InvalidDataException(
                        $"Chunk '{id}' claims {length} byte(s) but only {body.Length - offset} remain.");
                }

                var payload = new byte[length];
                Array.Copy(body, offset, payload, 0, length);
                offset += (int)length;

                if (IsKnown(id))
                {
                    chunks[id] = payload;
                }
                else
                {
                    unknown.Add(new ZxNextSnapshotUnknownChunk(id, payload));
                }
            }

            return new ZxNextMachineSnapshot(
                ReadChunk(chunks, CpuChunk, ReadCpu),
                ReadChunk(chunks, ClockChunk, static reader =>
                    new ZxNextClockState(reader.ReadUInt64(), (ZxNextCpuSpeed)reader.ReadByte())),
                ReadChunk(chunks, RamChunk, ReadByteArray),
                ReadChunk(chunks, MemoryBusChunk, ReadMemoryBus),
                ReadChunk(chunks, PagingChunk, static reader => new ZxNextPagingState(
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadByte())),
                ReadChunk(chunks, RegistersChunk, ReadRegisters),
                ReadChunk(chunks, UlaTimingChunk, ReadUlaTiming),
                ReadChunk(chunks, RasterChunk, ReadRaster),
                ReadChunk(chunks, PalettesChunk, ReadPalettes),
                ReadChunk(chunks, Layer2Chunk, ReadLayer2),
                ReadChunk(chunks, TilemapChunk, ReadTilemap),
                ReadChunk(chunks, SpritesChunk, ReadSprites),
                ReadChunk(chunks, DmaChunk, ReadDma),
                ReadChunk(chunks, BusArbiterChunk, static reader => new ZxNextBusArbiterState(
                    (ZxNextBusOwner)reader.ReadByte(),
                    reader.ReadUInt64(),
                    reader.ReadUInt64(),
                    reader.ReadUInt64(),
                    reader.ReadUInt64())),
                ReadChunk(chunks, DivMmcChunk, ReadDivMmc),
                ReadChunk(chunks, MultifaceChunk, static reader => new ZxNextMultifaceState(
                    reader.ReadBoolean(),
                    reader.ReadBoolean(),
                    reader.ReadBoolean(),
                    reader.ReadBoolean())),
                ReadChunk(chunks, NmiRoutingChunk, static reader =>
                    new ZxNextNmiRoutingState(reader.ReadByte())),
                ReadChunk(chunks, AudioChunk, ReadAudio),
                ReadChunk(chunks, InputChunk, ReadInput),
                ReadChunk(chunks, PortBusChunk, static reader => new ZxNextPortBusState(
                    reader.ReadUInt16(),
                    reader.ReadBoolean(),
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadByte())),
                ReadChunk(chunks, FloatingBusChunk, static reader => new ZxNextFloatingBusState(
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadBoolean())),
                ReadChunk(chunks, MediaChunk, ReadMedia))
            {
                UnknownChunks = unknown
            };
        }

        private static bool IsKnown(string id) => id is CpuChunk or ClockChunk or RamChunk
            or MemoryBusChunk or PagingChunk or RegistersChunk or UlaTimingChunk or RasterChunk
            or PalettesChunk or Layer2Chunk or TilemapChunk or SpritesChunk or DmaChunk
            or BusArbiterChunk or DivMmcChunk or MultifaceChunk or NmiRoutingChunk or AudioChunk
            or InputChunk or PortBusChunk or FloatingBusChunk or MediaChunk;

        /// <summary>Reads one required chunk, naming it if it is absent or short.</summary>
        private static T ReadChunk<T>(
            Dictionary<string, byte[]> chunks,
            string id,
            Func<BinaryReader, T> read)
        {
            if (!chunks.TryGetValue(id, out byte[]? payload))
            {
                throw new InvalidDataException(
                    $"This snapshot has no '{id}' chunk, which a Next machine cannot be rebuilt without.");
            }

            using var buffer = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);
            try
            {
                return read(reader);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"Chunk '{id}' ended before its contents were complete.");
            }
        }

        private static void WriteChunk(BinaryWriter writer, string id, Action<BinaryWriter> write)
        {
            using var buffer = new MemoryStream();
            using (var chunk = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                write(chunk);
            }

            WriteRawChunk(writer, id, buffer.ToArray());
        }

        private static void WriteRawChunk(BinaryWriter writer, string id, byte[] payload)
        {
            writer.Write(Encoding.ASCII.GetBytes(id.PadRight(4)[..4]));
            writer.Write((uint)payload.Length);
            writer.Write(payload);
        }

        // ------------------------------------------------------------- records

        /// <summary>
        /// Writes the SD slots, which is the one chunk whose size the guest decides.
        /// </summary>
        /// <remarks>
        /// A slot is written as a presence flag followed by the card, so "no card" costs one
        /// byte and is distinguishable from a card with nothing in its journal. The path is
        /// written because a restore has to recognise the medium; it is never opened.
        /// </remarks>
        private static void WriteMedia(BinaryWriter writer, ZxNextMediaState media)
        {
            writer.Write(media.Slots.Length);
            foreach (SpectrumSdCardState? slot in media.Slots)
            {
                writer.Write(slot != null);
                if (slot == null)
                {
                    continue;
                }

                writer.Write(slot.Identity.Path);
                writer.Write(slot.Identity.SectorCount);
                writer.Write(slot.Identity.WriteProtected);
                writer.Write(slot.Identity.FolderBacked);
                writer.Write(slot.Identity.WritesBackToFolder);
                WriteByteArray(writer, slot.Command);
                writer.Write(slot.CommandLength);
                WriteByteArray(writer, slot.Response);
                writer.Write(slot.Idle);
                writer.Write(slot.AppCommand);
                writer.Write(slot.MultipleRead);
                writer.Write(slot.MultipleReadLba);
                writer.Write(slot.WriteState);
                writer.Write(slot.WriteLba);
                writer.Write(slot.WriteIndex);
                WriteByteArray(writer, slot.WriteBuffer);
                writer.Write(slot.Modified);
                writer.Write(slot.FolderBackingFlushed);
                writer.Write(slot.WriteCount);
                writer.Write(slot.SectorsComplete);
                writer.Write(slot.DirtySectorLbas.Length);
                foreach (uint lba in slot.DirtySectorLbas)
                {
                    writer.Write(lba);
                }

                WriteByteArray(writer, slot.DirtySectorData);
            }
        }

        private static ZxNextMediaState ReadMedia(BinaryReader reader)
        {
            int slotCount = ReadCount(reader, 8, "SD slot");
            var slots = new SpectrumSdCardState?[slotCount];
            for (int slot = 0; slot < slotCount; slot++)
            {
                if (!reader.ReadBoolean())
                {
                    continue;
                }

                var identity = new SpectrumSdCardIdentity(
                    reader.ReadString(),
                    reader.ReadInt64(),
                    reader.ReadBoolean(),
                    reader.ReadBoolean(),
                    reader.ReadBoolean());
                byte[] command = ReadByteArray(reader);
                int commandLength = reader.ReadInt32();
                byte[] response = ReadByteArray(reader);
                bool idle = reader.ReadBoolean();
                bool appCommand = reader.ReadBoolean();
                bool multipleRead = reader.ReadBoolean();
                uint multipleReadLba = reader.ReadUInt32();
                byte writeState = reader.ReadByte();
                uint writeLba = reader.ReadUInt32();
                int writeIndex = reader.ReadInt32();
                byte[] writeBuffer = ReadByteArray(reader);
                bool modified = reader.ReadBoolean();
                bool folderBackingFlushed = reader.ReadBoolean();
                ulong writeCount = reader.ReadUInt64();
                bool sectorsComplete = reader.ReadBoolean();

                var lbas = new uint[ReadCount(
                    reader,
                    SpectrumDivMmcSdCard.SectorJournalLimit,
                    "journalled sector")];
                for (int index = 0; index < lbas.Length; index++)
                {
                    lbas[index] = reader.ReadUInt32();
                }

                byte[] contents = ReadByteArray(reader);
                if (contents.Length != lbas.Length * 512)
                {
                    throw new InvalidDataException(
                        $"The '{MediaChunk}' chunk lists {lbas.Length} journalled sector(s) but "
                            + $"carries {contents.Length} byte(s) of content.");
                }

                slots[slot] = new SpectrumSdCardState(
                    identity,
                    command,
                    commandLength,
                    response,
                    idle,
                    appCommand,
                    multipleRead,
                    multipleReadLba,
                    writeState,
                    writeLba,
                    writeIndex,
                    writeBuffer,
                    modified,
                    folderBackingFlushed,
                    writeCount,
                    lbas,
                    contents,
                    sectorsComplete);
            }

            return new ZxNextMediaState(slots);
        }

        private static void WriteDma(BinaryWriter writer, ZxNextDmaState dma)
        {
            writer.Write(dma.IsActive);
            writer.Write(dma.Loaded);
            writer.Write(dma.Z80CompatibleMode);
            writer.Write(dma.DirectionAtoB);
            writer.Write(dma.EndOfBlockNot);
            writer.Write(dma.TransferredAtLeastOne);
            writer.Write(dma.PortAIsIo);
            writer.Write(dma.PortBIsIo);
            writer.Write(dma.PendingWrite);
            writer.Write(dma.PendingHeader);
            writer.Write(dma.ReadField);
            writer.Write(dma.ReadMask);
            writer.Write(dma.TransferPhase);
            writer.Write(dma.PortAAddressMode);
            writer.Write(dma.PortBAddressMode);
            writer.Write(dma.PortATiming);
            writer.Write(dma.PortBTiming);
            writer.Write(dma.PortBPrescaler);
            writer.Write(dma.TransferByte);
            writer.Write(dma.PortAStart);
            writer.Write(dma.PortBStart);
            writer.Write(dma.BlockLength);
            writer.Write(dma.SourceAddress);
            writer.Write(dma.DestinationAddress);
            writer.Write(dma.Counter);
            writer.Write(dma.CurrentTransferLength);
            writer.Write(dma.LastTransferLength);
        }

        private static ZxNextDmaState ReadDma(BinaryReader reader) => new(
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadInt32(),
            reader.ReadInt32());

        private static void WriteDivMmc(BinaryWriter writer, ZxNextDivMmcState divMmc)
        {
            writer.Write(divMmc.ControlRegister);
            writer.Write(divMmc.CardSelectRegister);
            writer.Write(divMmc.SdCardSwap);
            writer.Write(divMmc.Enabled);
            writer.Write(divMmc.AutomapEnabled);
            writer.Write(divMmc.AutomapHeld);
            writer.Write(divMmc.NmiActive);
            writer.Write(divMmc.EntryPoints0);
            writer.Write(divMmc.EntryPointsValid0);
            writer.Write(divMmc.EntryPointsTiming0);
            writer.Write(divMmc.EntryPoints1);
        }

        private static ZxNextDivMmcState ReadDivMmc(BinaryReader reader) => new(
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte());

        private static void WriteAudio(BinaryWriter writer, ZxNextAudioState audio)
        {
            writer.Write(audio.Chips.Length);
            foreach (AySnapshotState chip in audio.Chips)
            {
                WriteAyChip(writer, chip);
            }

            WriteByteArray(writer, audio.SelectedRegisters);
            WriteByteArray(writer, audio.Pan);
            writer.Write(audio.ActiveChip);
            writer.Write(audio.UlaOutputLatch);
            writer.Write(audio.Peripheral2Settings);
            writer.Write(audio.Peripheral3Settings);
            writer.Write(audio.Peripheral4Settings);
            writer.Write(audio.CurrentUlaLevel);
            writer.Write(audio.UlaLevelRunTicks);
            writer.Write(audio.UlaLevelSum);
            writer.Write(audio.UlaLevelWeight);
            writer.Write(audio.SampleRemainder);
        }

        private static ZxNextAudioState ReadAudio(BinaryReader reader)
        {
            int chipCount = ReadCount(reader, 8, "PSG");
            var chips = new AySnapshotState[chipCount];
            for (int chip = 0; chip < chipCount; chip++)
            {
                chips[chip] = ReadAyChip(reader);
            }

            return new ZxNextAudioState(
                chips,
                ReadByteArray(reader),
                ReadByteArray(reader),
                reader.ReadInt32(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadInt32(),
                reader.ReadUInt64(),
                reader.ReadInt64(),
                reader.ReadUInt64(),
                reader.ReadInt64());
        }

        private static void WriteAyChip(BinaryWriter writer, AySnapshotState chip)
        {
            WriteByteArray(writer, chip.Registers);
            writer.Write(chip.ClockAccumulator);
            writer.Write(chip.ToneDivCounter);
            writer.Write(chip.NoiseDivCounter);
            writer.Write(chip.EnvelopeDivCounter);
            writer.Write(chip.ToneCounterA);
            writer.Write(chip.ToneCounterB);
            writer.Write(chip.ToneCounterC);
            writer.Write(chip.PeriodA);
            writer.Write(chip.PeriodB);
            writer.Write(chip.PeriodC);
            writer.Write(chip.ToneOutputA);
            writer.Write(chip.ToneOutputB);
            writer.Write(chip.ToneOutputC);
            writer.Write(chip.NoiseCounter);
            writer.Write(chip.NoisePeriod);
            writer.Write(chip.NoiseRng);
            writer.Write(chip.NoiseOutput);
            writer.Write(chip.EnvelopeCounter);
            writer.Write(chip.EnvelopePeriod);
            writer.Write(chip.EnvelopeVolume);
            writer.Write(chip.EnvelopeHold);
            writer.Write(chip.EnvelopeAlternate);
            writer.Write(chip.EnvelopeAttack);
            writer.Write(chip.EnvelopeContinue);
            writer.Write(chip.EnvelopeStep);
            writer.Write(chip.EnvelopeFirst);
            writer.Write(chip.EnvelopeReverse);
        }

        private static AySnapshotState ReadAyChip(BinaryReader reader) => new(
            ReadByteArray(reader),
            reader.ReadDouble(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadInt32(),
            reader.ReadBoolean(),
            reader.ReadBoolean());

        private static void WriteInput(BinaryWriter writer, ZxNextInputState input)
        {
            WriteByteArray(writer, input.KeyboardRows);
            WriteByteArray(writer, input.JoystickRows);
            WriteByteArray(writer, input.ConnectorStates);
            writer.Write(input.ConnectorModes.Length);
            foreach (ZxNextJoystickMode mode in input.ConnectorModes)
            {
                writer.Write((byte)mode);
            }
        }

        private static ZxNextInputState ReadInput(BinaryReader reader)
        {
            byte[] keyboardRows = ReadByteArray(reader);
            byte[] joystickRows = ReadByteArray(reader);
            byte[] connectorStates = ReadByteArray(reader);
            int modeCount = ReadCount(reader, 8, "joystick connector");
            var modes = new ZxNextJoystickMode[modeCount];
            for (int mode = 0; mode < modeCount; mode++)
            {
                modes[mode] = (ZxNextJoystickMode)reader.ReadByte();
            }

            return new ZxNextInputState(keyboardRows, joystickRows, connectorStates, modes);
        }

        private static void WriteCpu(BinaryWriter writer, Z80SnapshotState cpu)
        {
            writer.Write(cpu.Cycles);
            writer.Write(cpu.PC);
            writer.Write(cpu.SP);
            writer.Write(cpu.IX);
            writer.Write(cpu.IY);
            writer.Write(cpu.MemPtr);
            writer.Write(cpu.A);
            writer.Write(cpu.F);
            writer.Write(cpu.B);
            writer.Write(cpu.C);
            writer.Write(cpu.D);
            writer.Write(cpu.E);
            writer.Write(cpu.H);
            writer.Write(cpu.L);
            writer.Write(cpu.AlternateA);
            writer.Write(cpu.AlternateF);
            writer.Write(cpu.AlternateB);
            writer.Write(cpu.AlternateC);
            writer.Write(cpu.AlternateD);
            writer.Write(cpu.AlternateE);
            writer.Write(cpu.AlternateH);
            writer.Write(cpu.AlternateL);
            writer.Write(cpu.I);
            writer.Write(cpu.R);
            writer.Write(cpu.InterruptMode);
            writer.Write(cpu.Iff1);
            writer.Write(cpu.Iff2);
            writer.Write(cpu.Halted);
            writer.Write(cpu.IffDelay);
            writer.Write(cpu.InterruptData);
            writer.Write(cpu.IntPending);
            writer.Write(cpu.NmiPending);
            writer.Write(cpu.Q);
            writer.Write(cpu.LastQ);
        }

        private static Z80SnapshotState ReadCpu(BinaryReader reader) => new(
            reader.ReadUInt64(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadByte(),
            reader.ReadByte());

        private static void WriteMemoryBus(BinaryWriter writer, ZxNextMemoryBusState state)
        {
            WriteByteArray(writer, state.MmuPages);
            WriteByteArray(writer, state.SystemSram);
            writer.Write(state.CurrentRomBank);
            writer.Write(state.BootRomEnabled);
            writer.Write(state.ConfigurationMode);
            writer.Write(state.ConfigurationBank);
            writer.Write(state.AlternateRomRegister);

            writer.Write(state.ShadowedPages.Length);
            foreach (int page in state.ShadowedPages)
            {
                writer.Write(page);
            }

            writer.Write(state.DisplayShadows.Length);
            foreach (byte[] shadow in state.DisplayShadows)
            {
                WriteByteArray(writer, shadow);
            }

            writer.Write(state.PendingDisplayWrites.Length);
            foreach (ZxNextPendingDisplayWriteState pending in state.PendingDisplayWrites)
            {
                writer.Write(pending.Page);
                writer.Write(pending.Offset);
                writer.Write(pending.Value);
                writer.Write(pending.MasterTick);
            }
        }

        private static ZxNextMemoryBusState ReadMemoryBus(BinaryReader reader)
        {
            byte[] mmuPages = ReadByteArray(reader);
            byte[] systemSram = ReadByteArray(reader);
            int currentRomBank = reader.ReadInt32();
            bool bootRomEnabled = reader.ReadBoolean();
            bool configurationMode = reader.ReadBoolean();
            byte configurationBank = reader.ReadByte();
            byte alternateRomRegister = reader.ReadByte();

            var shadowedPages = new int[ReadCount(reader, 64)];
            for (int index = 0; index < shadowedPages.Length; index++)
            {
                shadowedPages[index] = reader.ReadInt32();
            }

            var shadows = new byte[ReadCount(reader, 64)][];
            for (int index = 0; index < shadows.Length; index++)
            {
                shadows[index] = ReadByteArray(reader);
            }

            var pending = new ZxNextPendingDisplayWriteState[ReadCount(reader, 1 << 20)];
            for (int index = 0; index < pending.Length; index++)
            {
                pending[index] = new ZxNextPendingDisplayWriteState(
                    reader.ReadByte(),
                    reader.ReadInt32(),
                    reader.ReadByte(),
                    reader.ReadUInt64());
            }

            return new ZxNextMemoryBusState(
                mmuPages,
                systemSram,
                currentRomBank,
                bootRomEnabled,
                configurationMode,
                configurationBank,
                alternateRomRegister,
                shadowedPages,
                shadows,
                pending);
        }

        private static void WriteRegisters(BinaryWriter writer, ZxNextRegisterFileState state)
        {
            writer.Write(state.Peripheral1Settings);
            writer.Write(state.Peripheral2Settings);
            writer.Write(state.Peripheral3Settings);
            writer.Write(state.Peripheral4Settings);
            writer.Write(state.Peripheral5Settings);
            writer.Write(state.UserRegister);
            writer.Write(state.MachineTiming);
            writer.Write(state.MachineType);
            writer.Write(state.ExpansionInterruptEnabled);
            writer.Write(state.UlaInterruptEnabled);
            writer.Write(state.LineInterruptEnabled);
            writer.Write(state.LineInterruptMsb);
            writer.Write(state.LineInterruptLsb);
            writer.Write(state.LineInterruptGeneration);
            writer.Write(state.InterruptControl);
            writer.Write(state.InternalPortDecoding);
            writer.Write(state.NmiReturnAddress);
            writer.Write(state.StacklessNmiReturnPending);
            writer.Write(state.PendingDivMmcNmi);
            writer.Write(state.PendingMultifaceNmi);
            writer.Write(state.DivMmcNmiCause);
            writer.Write(state.MultifaceNmiCause);
            writer.Write(state.DisplayTimingLocked);
            writer.Write(state.PeripheralReset);
            WriteByteArray(writer, state.UlaClip);
            writer.Write(state.UlaClipIndex);
            writer.Write(state.TimexPort);
            writer.Write(state.UlaScrollX);
            writer.Write(state.UlaScrollY);
            writer.Write(state.LoresScrollX);
            writer.Write(state.LoresScrollY);
            writer.Write(state.LoresControl);
            writer.Write((byte)state.LastResetKind);
            writer.Write(state.PendingReset.HasValue);
            writer.Write((byte)(state.PendingReset ?? default));
        }

        private static ZxNextRegisterFileState ReadRegisters(BinaryReader reader)
        {
            byte peripheral1 = reader.ReadByte();
            byte peripheral2 = reader.ReadByte();
            byte peripheral3 = reader.ReadByte();
            byte peripheral4 = reader.ReadByte();
            byte peripheral5 = reader.ReadByte();
            byte userRegister = reader.ReadByte();
            byte machineTiming = reader.ReadByte();
            byte machineType = reader.ReadByte();
            bool expansionInterrupt = reader.ReadBoolean();
            bool ulaInterrupt = reader.ReadBoolean();
            bool lineInterrupt = reader.ReadBoolean();
            bool lineInterruptMsb = reader.ReadBoolean();
            byte lineInterruptLsb = reader.ReadByte();
            int lineInterruptGeneration = reader.ReadInt32();
            byte interruptControl = reader.ReadByte();
            uint internalPortDecoding = reader.ReadUInt32();
            ushort nmiReturnAddress = reader.ReadUInt16();
            bool stacklessNmiReturnPending = reader.ReadBoolean();
            bool pendingDivMmcNmi = reader.ReadBoolean();
            bool pendingMultifaceNmi = reader.ReadBoolean();
            bool divMmcNmiCause = reader.ReadBoolean();
            bool multifaceNmiCause = reader.ReadBoolean();
            bool displayTimingLocked = reader.ReadBoolean();
            bool peripheralReset = reader.ReadBoolean();
            byte[] ulaClip = ReadByteArray(reader);
            byte ulaClipIndex = reader.ReadByte();
            byte timexPort = reader.ReadByte();
            byte ulaScrollX = reader.ReadByte();
            byte ulaScrollY = reader.ReadByte();
            byte loresScrollX = reader.ReadByte();
            byte loresScrollY = reader.ReadByte();
            byte loresControl = reader.ReadByte();
            var lastResetKind = (ZxNextResetKind)reader.ReadByte();
            bool hasPendingReset = reader.ReadBoolean();
            var pendingResetValue = (ZxNextResetKind)reader.ReadByte();

            return new ZxNextRegisterFileState(
                peripheral1,
                peripheral2,
                peripheral3,
                peripheral4,
                peripheral5,
                userRegister,
                machineTiming,
                machineType,
                expansionInterrupt,
                ulaInterrupt,
                lineInterrupt,
                lineInterruptMsb,
                lineInterruptLsb,
                lineInterruptGeneration,
                interruptControl,
                internalPortDecoding,
                nmiReturnAddress,
                stacklessNmiReturnPending,
                pendingDivMmcNmi,
                pendingMultifaceNmi,
                divMmcNmiCause,
                multifaceNmiCause,
                displayTimingLocked,
                peripheralReset,
                ulaClip,
                ulaClipIndex,
                timexPort,
                ulaScrollX,
                ulaScrollY,
                loresScrollX,
                loresScrollY,
                loresControl,
                lastResetKind,
                hasPendingReset ? pendingResetValue : null);
        }

        /// <summary>
        /// The raster cadence. The timing profile is not stored.
        /// </summary>
        /// <remarks>
        /// <see cref="ZxNextUlaTimingProfile.Create"/> derives it from the machine timing byte
        /// and the 60 Hz flag, so storing it as well would be storing the same fact twice and
        /// inviting the two copies to disagree.
        /// </remarks>
        private static void WriteUlaTiming(BinaryWriter writer, ZxNextUlaTimingState state)
        {
            writer.Write(state.TimingProfile);
            writer.Write(state.SixtyHertz);
            writer.Write(state.ProfileGeneration);
            writer.Write(state.PixelsPerLine);
            writer.Write(state.LinesPerFrame);
            writer.Write(state.MasterTicksPerFrame);
            writer.Write(state.InterruptOffsetMasterTicks);
            writer.Write(state.InterruptPulseCpuTstates);
            writer.Write(state.FrameNumber);
            writer.Write(state.FrameStartMasterTick);
            writer.Write(state.InterruptCount);
            writer.Write(state.LineInterruptCount);
            writer.Write(state.LastLineInterruptMasterTick);
            writer.Write(state.InterruptLineActive);
            writer.Write(state.NextFrameTick);
            writer.Write(state.NextInterruptTick);
            writer.Write(state.NextLineInterruptTick);
            writer.Write(state.InterruptEndTick);
            writer.Write(state.NextEventTick);
            writer.Write(state.ScheduledLineInterruptEnabled);
            writer.Write(state.ScheduledLineInterruptValue);
            writer.Write(state.LineInterruptGeneration);
        }

        private static ZxNextUlaTimingState ReadUlaTiming(BinaryReader reader)
        {
            byte timingProfile = reader.ReadByte();
            bool sixtyHertz = reader.ReadBoolean();
            return new ZxNextUlaTimingState(
                timingProfile,
                sixtyHertz,
                ZxNextUlaTimingProfile.Create(timingProfile, sixtyHertz),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadInt32(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadBoolean(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadBoolean(),
                reader.ReadUInt16(),
                reader.ReadInt32());
        }

        private static void WriteRaster(BinaryWriter writer, ZxNextRasterState state)
        {
            writer.Write(state.UlaGeneratorIsPlain);
            writer.Write(state.Profile.MachineTiming);
            writer.Write(state.Profile.SixtyHertz);
            writer.Write(state.ProfileGeneration);
            writer.Write(state.ProfileMasterTicksPerFrame);
            writer.Write(state.BeamMasterTick);
            writer.Write(state.AbsoluteMasterTick);
            writer.Write(state.WorkingFrameNumber);
            writer.Write(state.CompletedFrameNumber);
            writer.Write(state.RasterHorizontal);
            writer.Write(state.RasterVertical);
            writer.Write(state.RasterSubpixel);
            writer.Write(state.CurrentRasterPixelRendered);
            writer.Write(state.CurrentFetchPhaseProcessed);
            writer.Write(state.CurrentVideoMemoryTickProcessed);
            WriteIntArray(writer, state.FrameBuffer);
            WriteIntArray(writer, state.WorkingBuffer);
        }

        private static ZxNextRasterState ReadRaster(BinaryReader reader)
        {
            bool ulaGeneratorIsPlain = reader.ReadBoolean();
            byte machineTiming = reader.ReadByte();
            bool sixtyHertz = reader.ReadBoolean();
            return new ZxNextRasterState(
                ulaGeneratorIsPlain,
                ZxNextUlaTimingProfile.Create(machineTiming, sixtyHertz),
                reader.ReadInt32(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadUInt64(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadBoolean(),
                reader.ReadBoolean(),
                reader.ReadBoolean(),
                ReadIntArray(reader),
                ReadIntArray(reader));
        }

        private static void WritePalettes(BinaryWriter writer, ZxNextPaletteState state)
        {
            writer.Write(state.Palettes.Length);
            foreach (ushort[] palette in state.Palettes)
            {
                WriteUInt16Array(writer, palette);
            }

            writer.Write(state.ArgbPalettes.Length);
            foreach (int[] palette in state.ArgbPalettes)
            {
                WriteIntArray(writer, palette);
            }

            writer.Write(state.StoredNineBitValue);
            writer.Write(state.NineBitSecondWrite);
            writer.Write(state.Index);
            writer.Write(state.UlaNextAttributeMask);
            writer.Write(state.Control);
            writer.Write(state.FallbackColor);
            writer.Write(state.UlaControl);
        }

        private static ZxNextPaletteState ReadPalettes(BinaryReader reader)
        {
            var palettes = new ushort[ReadCount(reader, 64)][];
            for (int index = 0; index < palettes.Length; index++)
            {
                palettes[index] = ReadUInt16Array(reader);
            }

            var argb = new int[ReadCount(reader, 64)][];
            for (int index = 0; index < argb.Length; index++)
            {
                argb[index] = ReadIntArray(reader);
            }

            return new ZxNextPaletteState(
                palettes,
                argb,
                reader.ReadByte(),
                reader.ReadBoolean(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte());
        }

        private static void WriteLayer2(BinaryWriter writer, ZxNextLayer2State state)
        {
            WriteByteArray(writer, state.Clip);
            writer.Write(state.ClipIndex);
            writer.Write(state.MapSegment);
            writer.Write(state.MapOffset);
            writer.Write(state.ActiveBank);
            writer.Write(state.ShadowBank);
            writer.Write(state.GlobalTransparencyColor);
            writer.Write(state.SpriteAndLayersControl);
            writer.Write(state.ScrollXLeastSignificant);
            writer.Write(state.ScrollXMostSignificant);
            writer.Write(state.ScrollY);
            writer.Write(state.Control);
            writer.Write(state.DisplayEnabled);
            writer.Write(state.MapWritesEnabled);
            writer.Write(state.MapReadsEnabled);
            writer.Write(state.MapShadowBank);
        }

        private static ZxNextLayer2State ReadLayer2(BinaryReader reader) => new(
            ReadByteArray(reader),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean());

        private static void WriteTilemap(BinaryWriter writer, ZxNextTilemapState state)
        {
            WriteByteArray(writer, state.Clip);
            writer.Write(state.ClipIndex);
            writer.Write(state.ScrollXMostSignificant);
            writer.Write(state.ScrollXLeastSignificant);
            writer.Write(state.ScrollY);
            writer.Write(state.TransparencyIndex);
            writer.Write(state.Control);
            writer.Write(state.DefaultAttribute);
            writer.Write(state.MapBase);
            writer.Write(state.TileBase);
            WriteUInt16Array(writer, state.TimedPixels);
            writer.Write(state.FetchState);
            writer.Write(state.RequestInFlight);
            writer.Write(state.TimedLine);
            writer.Write(state.LatestPrefetchLimit);
            writer.Write(state.NextGroupStart);
            writer.Write(state.FetchGroupStart);
            writer.Write(state.FetchPixel);
            writer.Write(state.CurrentMapIndex);
            writer.Write(state.FetchedTile);
            writer.Write(state.FetchedAttribute);
            writer.Write(state.FetchEightyColumns);
            writer.Write(state.FetchStripAttributes);
            writer.Write(state.FetchTextMode);
            writer.Write(state.FetchFiveHundredTwelveTiles);
            writer.Write(state.FetchForceAboveUla);
            writer.Write(state.FetchDefaultAttribute);
            writer.Write(state.FetchTransparency);
            writer.Write(state.FetchMapBase);
            writer.Write(state.FetchTileBase);
            writer.Write(state.CompletedCharacterFetches);
            writer.Write(state.FetchedPixelCount);
            writer.Write(state.TimedPixelDeadlineMisses);
        }

        private static ZxNextTilemapState ReadTilemap(BinaryReader reader) => new(
            ReadByteArray(reader),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            ReadUInt16Array(reader),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadUInt64());

        private static void WriteSprites(BinaryWriter writer, ZxNextSpriteState state)
        {
            WriteByteArray(writer, state.Patterns);
            WriteByteArray(writer, state.Attributes);
            WriteByteArray(writer, state.Clip);
            writer.Write(state.ClipIndex);
            WriteByteArray(writer, state.LinePaletteIndices);
            WriteBoolArray(writer, state.LineOpaque);
            WriteByteArray(writer, state.DisplayPaletteIndices);
            WriteBoolArray(writer, state.DisplayOpaque);
            WriteByteArray(writer, state.BuildPaletteIndices);
            WriteBoolArray(writer, state.BuildOpaque);
            writer.Write(state.PortSprite);
            writer.Write(state.PortAttributeIndex);
            writer.Write(state.NextRegisterSprite);
            writer.Write(state.PatternAddress);
            writer.Write(state.Status);
            writer.Write(state.IdLockstep);
            writer.Write(state.Revision);
            writer.Write(state.CachedLine);
            writer.Write(state.CachedControl);
            writer.Write(state.PreparationState);
            writer.Write(state.TransparencyIndex);
            writer.Write(state.SpritePreparationMasterTicks);
            writer.Write(state.LastPreparationMasterTicks);
            writer.Write(state.LastQualifiedSpriteCount);
            writer.Write(state.OvertimeCount);
        }

        private static ZxNextSpriteState ReadSprites(BinaryReader reader) => new(
            ReadByteArray(reader),
            ReadByteArray(reader),
            ReadByteArray(reader),
            reader.ReadByte(),
            ReadByteArray(reader),
            ReadBoolArray(reader),
            ReadByteArray(reader),
            ReadBoolArray(reader),
            ReadByteArray(reader),
            ReadBoolArray(reader),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadInt32(),
            reader.ReadByte(),
            reader.ReadBoolean(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadByte(),
            reader.ReadInt32(),
            reader.ReadByte(),
            reader.ReadUInt64(),
            reader.ReadUInt64(),
            reader.ReadInt32(),
            reader.ReadUInt64());

        // ------------------------------------------------------------- helpers

        /// <summary>
        /// Reads an element count, refusing one a real machine could not have produced.
        /// </summary>
        /// <remarks>
        /// A corrupt length would otherwise ask for an enormous allocation before the read
        /// that would have failed anyway.
        /// </remarks>
        private static int ReadCount(BinaryReader reader, int limit, string? what = null)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > limit)
            {
                throw new InvalidDataException(what == null
                    ? $"A chunk declares {count} elements, which cannot be right."
                    : $"A chunk declares {count} {what} element(s), which cannot be right.");
            }

            return count;
        }

        private static void WriteByteArray(BinaryWriter writer, byte[] values)
        {
            writer.Write(values.Length);
            writer.Write(values);
        }

        private static byte[] ReadByteArray(BinaryReader reader)
        {
            int length = ReadCount(reader, 4 * 1024 * 1024);
            byte[] values = reader.ReadBytes(length);
            if (values.Length != length)
            {
                throw new EndOfStreamException();
            }

            return values;
        }

        private static void WriteIntArray(BinaryWriter writer, int[] values)
        {
            writer.Write(values.Length);
            foreach (int value in values)
            {
                writer.Write(value);
            }
        }

        private static int[] ReadIntArray(BinaryReader reader)
        {
            var values = new int[ReadCount(reader, 4 * 1024 * 1024)];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = reader.ReadInt32();
            }

            return values;
        }

        private static void WriteUInt16Array(BinaryWriter writer, ushort[] values)
        {
            writer.Write(values.Length);
            foreach (ushort value in values)
            {
                writer.Write(value);
            }
        }

        private static ushort[] ReadUInt16Array(BinaryReader reader)
        {
            var values = new ushort[ReadCount(reader, 1 << 20)];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = reader.ReadUInt16();
            }

            return values;
        }

        private static void WriteBoolArray(BinaryWriter writer, bool[] values)
        {
            writer.Write(values.Length);
            foreach (bool value in values)
            {
                writer.Write(value);
            }
        }

        private static bool[] ReadBoolArray(BinaryReader reader)
        {
            var values = new bool[ReadCount(reader, 1 << 20)];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = reader.ReadBoolean();
            }

            return values;
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset) =>
            (ushort)(buffer[offset] | (buffer[offset + 1] << 8));

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32(byte[] buffer, int offset) => (uint)(buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24));
    }
}
