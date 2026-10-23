using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Z80CPU;

namespace ZedExEss.Diagnostics;

/// <summary>Settings for the portable Interface 1 foundation checks.</summary>
public sealed class Interface1VerificationOptions
{
    public string? OutputPath { get; init; }
    public string? RomPath { get; init; }
}

/// <summary>
/// Verifies Interface 1 ROMCS timing, mirrored mapping and partially decoded ports
/// without booting a desktop host.
/// </summary>
public static class Interface1VerificationRunner
{
    public static int Run(Interface1VerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "interface1-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        writer.WriteLine("Interface 1 verification");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        Check("ROM size validation", VerifyRomSize, ref failed);
        Check("Opcode-fetch ROMCS mapping", VerifyRomMapping, ref failed);
        Check("Partially decoded IF1 ports", VerifyPortDecode, ref failed);
        Check("RS232 modem control and byte framing", VerifyRs232Transport, ref failed);
        Check("RS232 cross-platform stream and file adapter", VerifyRs232StreamAdapter, ref failed);
        Check("RS232 named-pipe connection and reconnect", VerifyRs232NamedPipeConnection, ref failed);
        Check("ZX Net timestamped shared wire", VerifyZxNetSharedWire, ref failed);
        Check("ZX Net TCP transition bridge", VerifyZxNetTcpBridge, ref failed);
        Check("Eight-drive motor shift register", VerifyMotorShiftRegister, ref failed);
        Check("MDR image validation and round trip", VerifyMdrImage, ref failed);
        Check("Microdrive GAP/SYNC and byte transport", VerifyMicrodriveReadTransport, ref failed);
        Check("Microdrive write/erase gates and write protection", VerifyMicrodriveWriteTransport, ref failed);
        Check("Persistent Microdrive session state", VerifyPersistentMediaState, ref failed);
        Check("Interface 1 snapshot capture and exact restore", VerifySnapshotPersistence, ref failed);
        Check("Dirty MDR shutdown flush and reload", VerifyDirtyMediaFlush, ref failed);
        Check("A recorded Microdrive record reaches its file", VerifyWriteReachesFile, ref failed);
        Check("Named files in Interface 1 cartridge layout", VerifyCartridgeFiles, ref failed);
        Check("Cartridge snapshot restores the machine", VerifyCartridgeSnapshot, ref failed);
        Check("Cartridge snapshot restores a 128K machine", VerifyCartridgeSnapshot128, ref failed);
        Check("Packed memory unpacks over itself", VerifyCompressedUnpack, ref failed);

        if (!string.IsNullOrWhiteSpace(options.RomPath))
        {
            Check("Supplied Interface 1 ROM", () => VerifySuppliedRom(options.RomPath!), ref failed);
            Check("Interface 1 ROM cartridge-presence probe", () => VerifyRomPresenceProbe(options.RomPath!), ref failed);
            Check("Interface 1 ROM ZX Net scout waveform", () => VerifyRomNetworkScout(options.RomPath!), ref failed);
            Check("Two-ROM ZX Net scout reception", () => VerifyRomNetworkPeerScout(options.RomPath!), ref failed);
            Check("Two-ROM ZX Net directed packet", () => VerifyRomNetworkPacket(options.RomPath!), ref failed);
            Check("Two-ROM ZX Net packet over TCP bridge", () => VerifyRomNetworkPacketOverTcp(options.RomPath!), ref failed);
        }

        writer.WriteLine();
        writer.WriteLine(failed == 0
            ? "Result: PASS"
            : $"Result: FAIL ({failed.ToString(CultureInfo.InvariantCulture)} failed checks)");
        return failed == 0 ? 0 : 1;

        void Check(string name, Action action, ref int failureCount)
        {
            try
            {
                action();
                writer.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failureCount++;
                writer.WriteLine($"FAIL {name}: {ex.Message}");
                Debug.WriteLine(ex.ToString());
            }
        }
    }

    private static void VerifyRomSize()
    {
        _ = new SpectrumInterface1Device(new byte[SpectrumInterface1Device.RomSize]);

        bool rejected = false;
        try
        {
            _ = new SpectrumInterface1Device(new byte[SpectrumInterface1Device.RomSize - 1]);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Require(rejected, "An incorrectly sized Interface 1 ROM was accepted.");
    }

    private static void VerifyZxNetTcpBridge()
    {
        int port = ReserveTcpPort();
        ulong senderClock = 2_000_000;
        ulong receiverClock = 5_000_000;
        var senderBus = new SpectrumInterface1NetworkBus();
        var receiverBus = new SpectrumInterface1NetworkBus();
        using SpectrumInterface1NetworkStation senderStation = senderBus.AttachStation("TCP sender");
        using SpectrumInterface1NetworkStation receiverStation = receiverBus.AttachStation("TCP receiver");
        using var senderBridge = new SpectrumInterface1NetworkBridge(senderBus, () => senderClock);
        using var receiverBridge = new SpectrumInterface1NetworkBridge(receiverBus, () => receiverClock);

        receiverBridge.Listen(port);
        senderBridge.Connect(IPAddress.Loopback.ToString(), port);
        RequireEventually(
            () => senderBridge.State == SpectrumInterface1NetworkBridgeState.Connected &&
                  receiverBridge.State == SpectrumInterface1NetworkBridgeState.Connected,
            "The two TCP ZX Net bridges did not complete their handshake.");

        const ulong pulseStartDelta = 500;
        const ulong pulseLength = 700;
        senderStation.SetOutput(ulaOutputHigh: false, networkSelected: true, senderClock + pulseStartDelta);
        senderStation.SetOutput(ulaOutputHigh: true, networkSelected: true, senderClock + pulseStartDelta + pulseLength);
        RequireEventually(
            () => receiverBus.CopyTransitions().Count >= 2,
            "The TCP bridge did not deliver both edges of a ZX Net pulse.");

        IReadOnlyList<SpectrumInterface1NetworkTransition> received = receiverBus.CopyTransitions();
        SpectrumInterface1NetworkTransition rising = received[^2];
        SpectrumInterface1NetworkTransition falling = received[^1];
        ulong expectedStart = receiverClock + SpectrumInterface1NetworkBridge.TransportLeadTstates + pulseStartDelta;
        Require(rising.LineHigh && !falling.LineHigh, "The bridged ZX Net pulse had the wrong polarity.");
        Require(rising.Tstate == expectedStart,
            $"The bridged pulse began at {rising.Tstate}, expected {expectedStart}.");
        Require(falling.Tstate - rising.Tstate == pulseLength,
            "The TCP bridge changed the emulated width of the ZX Net pulse.");
        Require(receiverStation.Sample(expectedStart),
            "The receiving Interface 1 could not sample the bridged asserted level.");
        Require(!receiverStation.Sample(expectedStart + pulseLength),
            "The receiving Interface 1 did not see the bridged pulse release.");

        senderBridge.Disconnect();
        RequireEventually(
            () => receiverBridge.State == SpectrumInterface1NetworkBridgeState.Listening,
            "The ZX Net listener did not resume listening after its peer disconnected.");

        senderClock += 10_000;
        receiverClock += 10_000;
        int transitionsBeforeReconnect = receiverBus.CopyTransitions().Count;
        senderBridge.Connect(IPAddress.Loopback.ToString(), port);
        RequireEventually(
            () => senderBridge.State == SpectrumInterface1NetworkBridgeState.Connected &&
                  receiverBridge.State == SpectrumInterface1NetworkBridgeState.Connected,
            "The ZX Net bridges did not reconnect.");
        senderStation.SetOutput(ulaOutputHigh: false, networkSelected: true, senderClock + 250);
        senderStation.SetOutput(ulaOutputHigh: true, networkSelected: true, senderClock + 600);
        RequireEventually(
            () => receiverBus.CopyTransitions().Count >= transitionsBeforeReconnect + 2,
            "The reconnected TCP bridge did not deliver a second pulse.");

        received = receiverBus.CopyTransitions();
        rising = received[^2];
        falling = received[^1];
        Require(rising.LineHigh && !falling.LineHigh && falling.Tstate - rising.Tstate == 350,
            "Reconnect changed the polarity or width of the second ZX Net pulse.");
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void RequireEventually(Func<bool> condition, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(1);
        }

        throw new InvalidOperationException(message);
    }

    private static void VerifyRomMapping()
    {
        byte[] firmware = CreatePatternedRom();
        var device = new SpectrumInterface1Device(firmware);
        var memory = new SpectrumMemory(
            SpectrumModel.Spectrum48K,
            RomSet.CreateBlank(1));
        memory.ConfigureInterface1(device);

        Require(!device.IsPaged, "Interface 1 ROM must start unpaged.");
        Require(memory.ReadDirect(0x0008) == 0x00, "Machine ROM should be visible before an IF1 entry fetch.");

        byte entry = memory.FetchOpcode(0x0008);
        Require(device.IsPaged, "Fetch at 0008h did not page Interface 1 ROM before the read.");
        Require(entry == firmware[0x0008], "Entry opcode was not read from Interface 1 ROM.");
        Require(memory.ReadDirect(0x2008) == firmware[0x0008], "Interface 1 ROM is not mirrored at 2000h.");

        byte exit = memory.FetchOpcode(0x0700);
        Require(exit == firmware[0x0700], "Exit opcode was not fetched from Interface 1 ROM.");
        Require(!device.IsPaged, "Fetch at 0700h did not release ROMCS after the read.");
        Require(memory.ReadDirect(0x0700) == 0x00, "Machine ROM was not restored after IF1 unpaging.");

        _ = memory.FetchOpcode(0x1708);
        Require(device.IsPaged, "Fetch at 1708h did not page Interface 1 ROM.");
        Require(memory.ReadDirect(0x3708) == firmware[0x1708], "Upper mirror did not wrap to the 8 KiB ROM.");
    }

    private static void VerifyPortDecode()
    {
        var device = new SpectrumInterface1Device(CreatePatternedRom());

        Require(device.HandlesPort(0x00E7), "Nominal Microdrive data port was not decoded.");
        Require(device.HandlesPort(0xBFEF), "Aliased control port was not decoded.");
        Require(device.HandlesPort(0x7FF7), "Aliased network port was not decoded.");
        Require(!device.HandlesPort(0x0018), "Unknown A3/A4 port combination was decoded.");
        Require(device.Read(0x00E7) == 0xFF, "Idle Microdrive data bus should read FFh.");
        Require(device.Read(0x00EF) == 0xE7, "Idle control/status value should read E7h.");
        Require(device.Read(0x00F7) == 0x7E, "Disconnected communications value should read 7Eh.");

        device.Write(0x00F7, 0x01);
        Require(device.NetworkOutput == 1, "Network output bit was not latched.");
    }

    private static void VerifyMotorShiftRegister()
    {
        var device = new SpectrumInterface1Device(CreatePatternedRom());

        // Clock high then low with active-low DATA starts drive 1.
        device.Write(0x00EF, 0xEE);
        device.Write(0x00EF, 0xEC);
        Require(device.MotorMask == 0x01 && device.IsMotorRunning(1), "Drive 1 did not start on a falling clock edge.");
        Require(device.SelectedDriveNumber == 1, "Selected-drive status did not identify drive 1.");
        Require(device.Activity == MicrodriveActivityState.Idle, "Selecting a drive incorrectly reported data activity.");

        // Shift drive 1 to drive 2 while inserting an off state for drive 1.
        device.Write(0x00EF, 0xEF);
        device.Write(0x00EF, 0xED);
        Require(device.MotorMask == 0x02, "Motor state did not shift from drive 1 to drive 2.");
        Require(!device.IsMotorRunning(1) && device.IsMotorRunning(2), "Shifted motor selection is incorrect.");
        Require(device.SelectedDriveNumber == 2, "Selected-drive status did not follow the motor shift register.");
    }

    private static void VerifyZxNetSharedWire()
    {
        var bus = new SpectrumInterface1NetworkBus();
        using SpectrumInterface1NetworkStation firstStation = bus.AttachStation("First");
        using SpectrumInterface1NetworkStation secondStation = bus.AttachStation("Second");
        var first = new SpectrumInterface1Device(CreatePatternedRom());
        var second = new SpectrumInterface1Device(CreatePatternedRom());
        first.AttachNetworkStation(firstStation);
        second.AttachNetworkStation(secondStation);

        first.SetBusTstate(90);
        second.SetBusTstate(90);
        Require((first.Read(0x00F7) & 0x01) == 0 && (second.Read(0x00F7) & 0x01) == 0,
            "An idle shared wire did not rest low.");

        // Network mode is selected while COMMS DATA (EFh bit 0) is clear. The
        // external transistor inverts F7h bit 0, so writing zero asserts the wire.
        first.SetBusTstate(100);
        first.Write(0x00F7, 0x00);
        second.SetBusTstate(101);
        Require((second.Read(0x00F7) & 0x01) != 0,
            "A second station did not observe the asserted wire.");
        Require((second.Read(0x00EF) & 0x10) != 0,
            "ZX Net BUSY did not follow the asserted wire.");

        second.SetBusTstate(110);
        second.Write(0x00F7, 0x01);
        Require((second.Read(0x00F7) & 0x01) != 0,
            "A released station incorrectly overrode another station's assertion.");

        first.SetBusTstate(120);
        first.Write(0x00F7, 0x01);
        second.SetBusTstate(121);
        Require((second.Read(0x00F7) & 0x01) == 0,
            "The wire did not return low after every station released it.");

        // Selecting RS232 disconnects the shared ULA output pin from ZX Net.
        first.SetBusTstate(130);
        first.Write(0x00F7, 0x00);
        first.SetBusTstate(131);
        first.Write(0x00EF, 0x01);
        second.SetBusTstate(132);
        Require((second.Read(0x00F7) & 0x01) == 0,
            "Selecting RS232 left the network output asserted.");

        IReadOnlyList<SpectrumInterface1NetworkTransition> transitions = bus.CopyTransitions();
        Require(transitions.Count == 4,
            $"Expected four aggregate wire transitions, observed {transitions.Count}.");
        Require(transitions[0] == new SpectrumInterface1NetworkTransition(100, 1, true) &&
                transitions[1] == new SpectrumInterface1NetworkTransition(120, 1, false) &&
                transitions[2] == new SpectrumInterface1NetworkTransition(130, 1, true) &&
                transitions[3] == new SpectrumInterface1NetworkTransition(131, 1, false),
            "ZX Net transition timestamps or aggregate levels are incorrect.");
    }

    private static void VerifyRs232Transport()
    {
        var device = new SpectrumInterface1Device(CreatePatternedRom());
        var endpoint = new SpectrumInterface1Rs232Buffer();
        device.AttachRs232Endpoint(endpoint);

        Require(device.Read(0x00EF) == 0xEF,
            "A connected endpoint did not raise the active DTR status input.");
        endpoint.DataTerminalReady = false;
        Require(device.Read(0x00EF) == 0xE7,
            "A lowered endpoint DTR input was not reflected in status.");
        endpoint.DataTerminalReady = true;

        // COMMS DATA high selects RS232 and bit 4 drives the endpoint's CTS line.
        device.Write(0x00EF, 0x11);
        Require(endpoint.ClearToSend, "The Interface 1 CTS output did not reach the endpoint.");

        endpoint.QueueReceived([0xA5]);
        bool[] expectedInputLine =
        [
            false, true, true, true, true,
            false, true, false, true, true, false, true, false
        ];
        for (int sample = 0; sample < expectedInputLine.Length; sample++)
        {
            byte value = device.Read(0x00F7);
            Require(((value & 0x80) != 0) == expectedInputLine[sample],
                $"RS232 input framing differs at sample {sample}.");
        }

        // Outbound framing uses the inverse serial level for each LSB-first data bit.
        // CTS must be low while the Spectrum transmits a byte.
        device.Write(0x00EF, 0x01);
        Require(!endpoint.ClearToSend, "The endpoint CTS line did not follow control bit 4.");
        WriteRs232Line(device, false); // leader/start transition
        WriteRs232Line(device, true);
        byte outbound = 0xA5;
        for (int bit = 0; bit < 8; bit++)
        {
            WriteRs232Line(device, (outbound & (1 << bit)) == 0);
        }

        WriteRs232Line(device, false);
        WriteRs232Line(device, false);
        WriteRs232Line(device, true);
        WriteRs232Line(device, false);
        Require(endpoint.TryDequeueTransmitted(out byte decoded) && decoded == outbound,
            $"RS232 output framing decoded {decoded:X2}; expected {outbound:X2}.");

        SpectrumInterface1DeviceState captured = device.CaptureState();
        Require(captured.Rs232.OutputPhase == 0 && captured.Rs232.InputPhase == 13,
            "RS232 framing state was not included in the Interface 1 snapshot boundary.");
    }

    private static void WriteRs232Line(SpectrumInterface1Device device, bool high)
    {
        device.Write(0x00F7, high ? (byte)1 : (byte)0);
    }

    private static void VerifyRs232StreamAdapter()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zedexess-if1-rs232-{Guid.NewGuid():N}");
        string inputPath = Path.Combine(directory, "receive.bin");
        string outputPath = Path.Combine(directory, "transmit.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(inputPath, [0xA5, 0x5A]);
            using (var endpoint = new SpectrumInterface1Rs232StreamEndpoint())
            {
                endpoint.AttachReceiveFile(inputPath);
                endpoint.AttachTransmitFile(outputPath);
                Require(endpoint.DataTerminalReady, "An attached stream endpoint did not assert DTR.");

                byte first = 0;
                byte second = 0;
                Require(
                    SpinWait.SpinUntil(() => endpoint.TryReadByte(out first), TimeSpan.FromSeconds(2)),
                    "The receive pump did not expose the first file byte.");
                Require(
                    SpinWait.SpinUntil(() => endpoint.TryReadByte(out second), TimeSpan.FromSeconds(2)),
                    "The receive pump did not expose the second file byte.");
                Require(first == 0xA5 && second == 0x5A,
                    $"RS232 receive file returned {first:X2} {second:X2}; expected A5 5A.");

                endpoint.WriteByte(0x3C);
                endpoint.WriteByte(0xC3);
            }

            Require(File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(new byte[] { 0x3C, 0xC3 }),
                "RS232 transmit file did not preserve complete decoded bytes.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyRs232NamedPipeConnection()
    {
        string pipeName = $"zedexess-if1-{Guid.NewGuid():N}";
        using var endpoint = new SpectrumInterface1Rs232StreamEndpoint();
        using var connection = new SpectrumInterface1Rs232ConnectionManager(endpoint);

        using (var firstServer = CreatePipeServer(pipeName))
        {
            Task firstConnection = firstServer.WaitForConnectionAsync();
            connection.ConnectNamedPipe(pipeName);
            Require(firstConnection.Wait(TimeSpan.FromSeconds(3)),
                "The RS232 named-pipe client did not connect to its server.");
            Require(SpinWait.SpinUntil(
                    () => connection.State == SpectrumInterface1Rs232ConnectionState.Connected,
                    TimeSpan.FromSeconds(2)),
                "The named-pipe connection did not enter the connected state.");

            firstServer.WriteByte(0x96);
            firstServer.Flush();
            byte received = 0;
            Require(SpinWait.SpinUntil(
                    () => endpoint.TryReadByte(out received),
                    TimeSpan.FromSeconds(2)),
                "The named pipe did not deliver a byte to the RS232 receive queue.");
            Require(received == 0x96, $"Named-pipe receive returned {received:X2}; expected 96.");

            byte[] transmitted = new byte[1];
            Task<int> read = firstServer.ReadAsync(transmitted, 0, transmitted.Length);
            endpoint.WriteByte(0x69);
            Require(read.Wait(TimeSpan.FromSeconds(2)) && read.Result == 1 && transmitted[0] == 0x69,
                "The RS232 transmit byte did not reach the named-pipe server.");
        }

        Require(SpinWait.SpinUntil(
                () => connection.State == SpectrumInterface1Rs232ConnectionState.Reconnecting,
                TimeSpan.FromSeconds(3)),
            "A closed named pipe did not enter the reconnecting state.");

        using (var secondServer = CreatePipeServer(pipeName))
        {
            Task secondConnection = secondServer.WaitForConnectionAsync();
            Require(secondConnection.Wait(TimeSpan.FromSeconds(4)),
                "The RS232 client did not reconnect to a replacement named-pipe server.");
            Require(SpinWait.SpinUntil(
                    () => connection.State == SpectrumInterface1Rs232ConnectionState.Connected,
                    TimeSpan.FromSeconds(2)),
                "The replacement named pipe did not return to the connected state.");
        }

        connection.Disconnect();
        Require(connection.State == SpectrumInterface1Rs232ConnectionState.Disconnected,
            "Explicit named-pipe disconnect did not clear the connection state.");
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private static void VerifyMdrImage()
    {
        byte[] image = CreatePatternedMdr(writeProtected: true);
        MicrodriveCartridge cartridge = MicrodriveCartridge.Load(image);

        Require(cartridge.SectorCount == MicrodriveCartridge.MinimumSectorCount, "MDR sector count was decoded incorrectly.");
        Require(cartridge.WriteProtected, "MDR trailing write-protect byte was ignored.");
        Require(cartridge.ToMdrBytes().AsSpan().SequenceEqual(image), "MDR round trip changed image bytes.");

        bool rejected = false;
        try
        {
            _ = MicrodriveCartridge.Load(new byte[(MicrodriveCartridge.MinimumSectorCount * MicrodriveCartridge.SectorLength) - 1]);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected, "An invalid MDR length was accepted.");

        MicrodriveCartridge formatted = MicrodriveCartridge.CreateFormatted("Success", 179);
        byte[] expectedFirstHeader =
        [
            0x01, 0x4B, 0x00, 0x00,
            (byte)'S', (byte)'u', (byte)'c', (byte)'c', (byte)'e', (byte)'s', (byte)'s',
            0x20, 0x20, 0x20, 0x88
        ];
        for (int i = 0; i < expectedFirstHeader.Length; i++)
        {
            Require(formatted.ReadByte(i) == expectedFirstHeader[i],
                $"Formatted MDR header byte {i} is {formatted.ReadByte(i):X2}; expected {expectedFirstHeader[i]:X2}.");
        }

        Require(formatted.GetPreambleState(0) == byte.MaxValue,
            "Formatted cartridge did not expose a valid sector-header preamble.");
        Require(formatted.GetPreambleState(formatted.SectorCount) == byte.MaxValue,
            "Formatted cartridge did not expose a valid record-header preamble.");
    }

    private static void VerifyMicrodriveReadTransport()
    {
        byte[] image = CreatePatternedMdr(writeProtected: false);
        var cartridge = MicrodriveCartridge.Load(image);
        var device = new SpectrumInterface1Device(CreatePatternedRom());
        device.InsertCartridge(1, cartridge);
        SelectDriveOne(device);

        for (int i = 0; i < 15; i++)
        {
            Require(device.Read(0x00EF) == 0xE7, $"GAP ended too early at status read {i}.");
        }

        Require(device.Read(0x00EF) == 0xE1, "GAP/SYNC active-low status was not exposed after the gap.");
        Require(device.Activity == MicrodriveActivityState.Reading, "Status polling did not report Microdrive read activity.");

        for (int i = 0; i < MicrodriveCartridge.HeaderLength; i++)
        {
            Require(device.Read(0x00E7) == image[i], $"Header transport byte {i} is incorrect.");
        }

        // A status poll restarts the transfer at the record-header boundary.
        _ = device.Read(0x00EF);
        Require(device.Read(0x00E7) == image[MicrodriveCartridge.HeaderLength],
            "Record-header transport did not follow the sector header.");

        Require(ReferenceEquals(device.EjectCartridge(1), cartridge), "Eject did not return the inserted cartridge.");
        Require(device.GetCartridge(1) == null, "Drive retained an ejected cartridge.");
    }

    private static void VerifyMicrodriveWriteTransport()
    {
        var cartridge = MicrodriveCartridge.CreateBlank(MicrodriveCartridge.MinimumSectorCount);
        var device = new SpectrumInterface1Device(CreatePatternedRom());
        device.InsertCartridge(1, cartridge);
        SelectDriveOne(device);

        Require(!device.MicrodriveWriteEnabled && !device.MicrodriveEraseEnabled,
            "Drive selection did not leave the IF1 in its EEh read state.");

        // The data register is electrically disconnected from the write head
        // in read mode. An E7h output must therefore leave the cartridge and
        // transport untouched.
        byte originalFirstByte = cartridge.ReadByte(0);
        WritePreamble(device);
        device.Write(0x00E7, 0x35);
        Require(cartridge.ReadByte(0) == originalFirstByte,
            "A data-port write modified the cartridge while R/W selected read mode.");

        // E6h starts the leading erase head, but does not yet enable the data
        // write head. MDR images contain logical sectors rather than raw flux,
        // so this phase is represented by its gate/activity state only.
        device.Write(0x00EF, 0xE6);
        Require(!device.MicrodriveWriteEnabled && device.MicrodriveEraseEnabled,
            "E6h did not select the erase-only lead-in state.");
        device.Write(0x00E7, 0x35);
        Require(cartridge.ReadByte(0) == originalFirstByte,
            "Erase-only mode incorrectly routed a data byte to the write head.");

        device.Write(0x00EF, 0xE2);
        Require(device.MicrodriveWriteEnabled && device.MicrodriveEraseEnabled,
            "E2h did not enable the Microdrive write and erase heads.");

        byte[] header = Enumerable.Range(0, MicrodriveCartridge.HeaderLength)
            .Select(static i => (byte)(0x80 + i))
            .ToArray();
        WritePreamble(device);
        foreach (byte value in header)
        {
            device.Write(0x00E7, value);
        }

        for (int i = 0; i < header.Length; i++)
        {
            Require(cartridge.ReadByte(i) == header[i], $"Written header byte {i} was not stored.");
        }

        device.Write(0x00EF, 0xEE);
        Require(!device.MicrodriveWriteEnabled && !device.MicrodriveEraseEnabled,
            "EEh did not return the Microdrive to read mode.");

        cartridge.SetWriteProtected(true);
        _ = device.Read(0x00EF); // Move transport to the record-header half.
        Require(device.Read(0x00EF) == 0xE6, "Write-protect status bit is not active-low.");

        int recordOffset = MicrodriveCartridge.HeaderLength;
        byte before = cartridge.ReadByte(recordOffset);
        device.Write(0x00EF, 0xE2);
        WritePreamble(device);
        device.Write(0x00E7, 0x35);
        Require(cartridge.ReadByte(recordOffset) == before, "Write-protected media was modified.");
    }

    private static void VerifySuppliedRom(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        _ = new SpectrumInterface1Device(firmware);
        Require(firmware.Any(static value => value != 0x00), "Supplied Interface 1 ROM is blank.");
        Require(firmware.Any(static value => value != 0xFF), "Supplied Interface 1 ROM contains only FFh.");
    }

    /// <summary>
    /// Executes the firmware's own SEL-DRIVE routine. This covers the real
    /// eight-pulse motor sequence, settling delay and six-consecutive-GAP presence
    /// test instead of merely duplicating those assumptions in a diagnostic helper.
    /// </summary>
    private static void VerifyRomPresenceProbe(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        string machineRomPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "48.rom");
        Require(File.Exists(machineRomPath), $"48K ROM not found beside Interface 1 ROM: {machineRomPath}");

        SpectrumInterface1Device? device = null;
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRomPath]),
            RenderEnabled = false,
            ConfigureDevices = context =>
            {
                device = new SpectrumInterface1Device(firmware);
                context.Memory.ConfigureInterface1(device);
                context.Ports.AddDevice(device);
            }
        });
        SpectrumInterface1Device attachedDevice = device
            ?? throw new InvalidOperationException("Interface 1 device was not attached to the full machine graph.");

        attachedDevice.InsertCartridge(1, MicrodriveCartridge.CreateFormatted("Verifier", MicrodriveCartridge.MinimumSectorCount));

        Z80 cpu = machine.Cpu;
        cpu.A = 1;
        cpu.SP = 0x9000;
        machine.Memory.WriteDirect(0x9000, 0x00);
        machine.Memory.WriteDirect(0x9001, 0x80);

        // An M1 fetch at 0008h asserts ROMCS. Continue directly at the matching
        // firmware revision's SEL-DRIVE entry and stop at the synthetic RAM return.
        _ = machine.Memory.FetchOpcode(0x0008);
        cpu.PC = FindSelectDriveEntry(firmware);
        const int maximumInstructions = 250_000;
        for (int i = 0; i < maximumInstructions && cpu.PC != 0x8000; i++)
        {
            machine.Emulator.StepInstruction();
        }

        Require(cpu.PC == 0x8000,
            $"Firmware presence probe did not return (PC={cpu.PC:X4}, motor={attachedDevice.MotorMask:X2}).");
        Require(attachedDevice.MotorMask == 0x01 && attachedDevice.IsMotorRunning(1),
            $"Firmware selected the wrong Microdrive motor (mask={attachedDevice.MotorMask:X2}).");
    }

    private static ushort FindSelectDriveEntry(ReadOnlySpan<byte> firmware)
    {
        // Both Sinclair revisions begin SEL-DRIVE with PUSH HL / CP 0 / JR NZ and
        // contain the distinctive 1388h settling counter shortly afterwards.
        ReadOnlySpan<byte> prefix = [0xE5, 0xFE, 0x00, 0x20];
        for (int offset = 0; offset <= firmware.Length - 24; offset++)
        {
            if (!firmware.Slice(offset, prefix.Length).SequenceEqual(prefix))
            {
                continue;
            }

            ReadOnlySpan<byte> window = firmware.Slice(offset, 24);
            for (int i = 0; i <= window.Length - 3; i++)
            {
                if (window[i] == 0x21 && window[i + 1] == 0x88 && window[i + 2] == 0x13)
                {
                    return checked((ushort)offset);
                }
            }
        }

        throw new InvalidDataException("Could not locate SEL-DRIVE in the supplied Interface 1 ROM.");
    }

    /// <summary>
    /// Runs the ROM's SEND-SC routine against the electrical bus. SEND-SC reads
    /// back every inverted bit it drives and retries on a collision, so returning
    /// proves that F7h output, shared-line input and access timestamps agree.
    /// </summary>
    private static void VerifyRomNetworkScout(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        string machineRomPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "48.rom");
        Require(File.Exists(machineRomPath), $"48K ROM not found beside Interface 1 ROM: {machineRomPath}");

        var bus = new SpectrumInterface1NetworkBus();
        using SpectrumInterface1NetworkStation station = bus.AttachStation("ROM verifier");
        SpectrumInterface1Device? device = null;
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRomPath]),
            RenderEnabled = false,
            ConfigureDevices = context =>
            {
                device = new SpectrumInterface1Device(firmware);
                device.AttachNetworkStation(station);
                context.Memory.ConfigureInterface1(device);
                context.Ports.AddDevice(device);
            }
        });

        Z80 cpu = machine.Cpu;
        cpu.IY = 0x5C3A;
        cpu.SP = 0x9000;
        machine.Memory.WriteDirect(0x9000, 0x00);
        machine.Memory.WriteDirect(0x9001, 0x80);
        machine.Memory.WriteDirect(0x5CC5, 0x55); // alternating station bits
        machine.Ports.WriteUncontended(0x00EF, 0xEE);

        _ = machine.Memory.FetchOpcode(0x0008);
        cpu.PC = FindSendScoutEntry(firmware);
        const int maximumInstructions = 500_000;
        for (int i = 0; i < maximumInstructions && cpu.PC != 0x8000; i++)
        {
            machine.Emulator.StepInstruction();
        }

        Require(cpu.PC == 0x8000,
            $"Firmware SEND-SC did not return (PC={cpu.PC:X4}, line={(bus.LineHigh ? 1 : 0)}).");
        Require(!bus.LineHigh, "Firmware SEND-SC did not release the network wire.");

        IReadOnlyList<SpectrumInterface1NetworkTransition> transitions = bus.CopyTransitions();
        Require(transitions.Count >= 8,
            $"Firmware scout produced only {transitions.Count} physical wire transitions.");
        Require(transitions[0].LineHigh && !transitions[^1].LineHigh,
            "Firmware scout did not begin active and finish at the resting level.");
        for (int i = 1; i < transitions.Count; i++)
        {
            Require(transitions[i].Tstate > transitions[i - 1].Tstate,
                "Firmware scout transitions were not timestamped in CPU execution order.");
            Require(transitions[i].LineHigh != transitions[i - 1].LineHigh,
                "Firmware scout transition history contains a duplicate wire level.");
        }
    }

    private static ushort FindSendScoutEntry(ReadOnlySpan<byte> firmware)
    {
        // SEND-SC starts by calling NET-STATE and then loads C=F7h, HL=0009h.
        // Locate it by structure rather than hard-coding the revision-2 address.
        for (int offset = 0; offset <= firmware.Length - 9; offset++)
        {
            if (firmware[offset] != 0xCD ||
                firmware[offset + 3] != 0x0E || firmware[offset + 4] != 0xF7 ||
                firmware[offset + 5] != 0x21 || firmware[offset + 6] != 0x09 ||
                firmware[offset + 7] != 0x00)
            {
                continue;
            }

            ushort callTarget = (ushort)(firmware[offset + 1] | (firmware[offset + 2] << 8));
            if (callTarget < firmware.Length)
            {
                return checked((ushort)offset);
            }
        }

        throw new InvalidDataException("Could not locate SEND-SC in the supplied Interface 1 ROM.");
    }

    private static void VerifyRomNetworkPeerScout(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        string machineRomPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "48.rom");
        Require(File.Exists(machineRomPath), $"48K ROM not found beside Interface 1 ROM: {machineRomPath}");

        var bus = new SpectrumInterface1NetworkBus();
        using SpectrumInterface1NetworkStation senderStation = bus.AttachStation("Sender");
        using SpectrumInterface1NetworkStation receiverStation = bus.AttachStation("Receiver");
        SpectrumMachine sender = CreateNetworkRomMachine(machineRomPath, firmware, senderStation);
        SpectrumMachine receiver = CreateNetworkRomMachine(machineRomPath, firmware, receiverStation);

        PrepareNetworkRomCall(sender, returnAddress: 0x8000, ix: 0x6000);
        PrepareNetworkRomCall(receiver, returnAddress: 0x8001, ix: 0x6200);
        sender.Memory.WriteDirect(0x5CC5, 0x55);
        receiver.Memory.WriteDirect(0x620B, 0x00); // wait indefinitely for a scout
        sender.Cpu.PC = FindSendScoutEntry(firmware);
        receiver.Cpu.PC = FindWaitScoutEntry(firmware);

        RunLockstep(sender, 0x8000, receiver, 0x8001, maximumInstructions: 1_000_000);

        Require(sender.Cpu.PC == 0x8000,
            $"Sender ROM did not complete SEND-SC (PC={sender.Cpu.PC:X4}).");
        Require(receiver.Cpu.PC == 0x8001,
            $"Receiver ROM did not recognise the scout (PC={receiver.Cpu.PC:X4}).");
        Require((receiver.Cpu.GetFlags() & 0x01) != 0,
            "WT-SC-E returned without carry after a received scout.");
        Require(!bus.LineHigh, "The shared wire remained asserted after peer scout reception.");
    }

    private static SpectrumMachine CreateNetworkRomMachine(
        string machineRomPath,
        byte[] firmware,
        SpectrumInterface1NetworkStation station)
    {
        return SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRomPath]),
            RenderEnabled = false,
            ConfigureDevices = context =>
            {
                var device = new SpectrumInterface1Device(firmware);
                device.AttachNetworkStation(station);
                context.Memory.ConfigureInterface1(device);
                context.Ports.AddDevice(device);
            }
        });
    }

    private static void PrepareNetworkRomCall(
        SpectrumMachine machine,
        ushort returnAddress,
        ushort ix)
    {
        Z80 cpu = machine.Cpu;
        cpu.IY = 0x5C3A;
        cpu.IX = ix;
        cpu.SP = 0x9000;
        machine.Memory.WriteDirect(0x9000, (byte)returnAddress);
        machine.Memory.WriteDirect(0x9001, (byte)(returnAddress >> 8));
        // Directly entering a network subroutine bypasses the IF1 ROM's normal
        // startup, which leaves EFh at EEh (network selected, WAIT disabled).
        // INPAK changes it to CEh only around the bit cells that require WAIT.
        machine.Ports.WriteUncontended(0x00EF, 0xEE);
        _ = machine.Memory.FetchOpcode(0x0008);
    }

    private static void RunLockstep(
        SpectrumMachine first,
        ushort firstReturn,
        SpectrumMachine second,
        ushort secondReturn,
        int maximumInstructions,
        Action<SpectrumMachine, SpectrumMachine>? observe = null)
    {
        for (int i = 0; i < maximumInstructions; i++)
        {
            observe?.Invoke(first, second);
            bool firstDone = first.Cpu.PC == firstReturn;
            bool secondDone = second.Cpu.PC == secondReturn;
            if (firstDone && secondDone)
            {
                return;
            }

            if (firstDone)
            {
                second.Emulator.StepInstruction();
            }
            else if (secondDone)
            {
                first.Emulator.StepInstruction();
            }
            else if (first.Cpu.Cyc < second.Cpu.Cyc)
            {
                if (!StepMadeProgress(first))
                {
                    StepMadeProgress(second);
                }
            }
            else
            {
                // Let the receiver run first on equal timestamps so its polling
                // loop is established before the sender asserts the wire. Once
                // INPAK enables hardware WAIT this step intentionally makes no
                // progress, in which case the sender must be allowed to produce
                // the transition which releases it.
                if (!StepMadeProgress(second))
                {
                    StepMadeProgress(first);
                }
            }
        }

        static bool StepMadeProgress(SpectrumMachine machine)
        {
            ulong before = machine.Cpu.Cyc;
            machine.Emulator.StepInstruction();
            return machine.Cpu.Cyc != before;
        }
    }

    private static ushort FindWaitScoutEntry(ReadOnlySpan<byte> firmware)
    {
        // Revision 2 calls TEST-BRK before this sequence; revision 1 starts at
        // the LD HL directly. Entering at their common timeout initialisation
        // exercises the same scout receiver in both ROMs.
        for (int offset = 0; offset <= firmware.Length - 12; offset++)
        {
            if (firmware[offset] == 0x21 && firmware[offset + 1] == 0xC2 && firmware[offset + 2] == 0x01 &&
                firmware[offset + 3] == 0x06 && firmware[offset + 4] == 0x80 &&
                firmware[offset + 5] == 0xCD &&
                firmware[offset + 8] == 0x30)
            {
                return checked((ushort)offset);
            }
        }

        throw new InvalidDataException("Could not locate WT-SC-E in the supplied Interface 1 ROM.");
    }

    private static void VerifyRomNetworkPacket(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        string machineRomPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "48.rom");
        Require(File.Exists(machineRomPath), $"48K ROM not found beside Interface 1 ROM: {machineRomPath}");

        var bus = new SpectrumInterface1NetworkBus();
        using SpectrumInterface1NetworkStation senderStation = bus.AttachStation("Packet sender");
        using SpectrumInterface1NetworkStation receiverStation = bus.AttachStation("Packet receiver");
        SpectrumMachine sender = CreateNetworkRomMachine(machineRomPath, firmware, senderStation);
        SpectrumMachine receiver = CreateNetworkRomMachine(machineRomPath, firmware, receiverStation);
        const ushort senderChannel = 0x6000;
        const ushort receiverChannel = 0x6200;
        ReadOnlySpan<byte> payload = [0x12, 0x34, 0x56, 0xA5];

        PrepareNetworkRomCall(sender, returnAddress: 0x8000, ix: senderChannel);
        PrepareNetworkRomCall(receiver, returnAddress: 0x8001, ix: receiverChannel);
        PrepareNetworkChannel(sender.Memory, senderChannel, remoteStation: 2, localStation: 1, payload: payload);
        PrepareNetworkChannel(receiver.Memory, receiverChannel, remoteStation: 1, localStation: 2, payload: []);
        sender.Cpu.A = 0; // normal data packet
        sender.Cpu.PC = FindSendPacketEntry(firmware);
        ushort getPacketEntry = FindGetPacketEntry(firmware);
        receiver.Cpu.PC = getPacketEntry;
        bool receiverVisitedError = false;
        bool receiverVisitedSuccess = false;

        RunLockstep(
            sender,
            0x8000,
            receiver,
            0x8001,
            maximumInstructions: 4_000_000,
            observe: (_, receivingMachine) =>
            {
                receiverVisitedSuccess |= receivingMachine.Cpu.PC == getPacketEntry + 0x0D;
                receiverVisitedError |= receivingMachine.Cpu.PC == getPacketEntry + 0x15;
            });

        Require(sender.Cpu.PC == 0x8000,
            $"Sender ROM did not complete SEND-PACK (PC={sender.Cpu.PC:X4}, " +
            $"receiver PC={receiver.Cpu.PC:X4}, sender T={sender.Cpu.Cyc}, " +
            $"receiver T={receiver.Cpu.Cyc}, receiver F={receiver.Cpu.GetFlags():X2}, " +
            $"header={FormatBytes(receiver.Memory, 0x5CCE, 8)}, line={(bus.LineHigh ? 1 : 0)}).");
        Require(receiver.Cpu.PC == 0x8001,
            $"Receiver ROM did not complete GET-PACK (PC={receiver.Cpu.PC:X4}).");
        Require((receiver.Cpu.GetFlags() & 0x01) == 0,
            $"GET-PACK returned carry set after a directed packet " +
            $"(F={receiver.Cpu.GetFlags():X2}, header={FormatBytes(receiver.Memory, 0x5CCE, 8)}, " +
            $"NCIBL={receiver.Memory.ReadDirect((ushort)(receiverChannel + 0x14)):X2}, " +
            $"payload={FormatBytes(receiver.Memory, (ushort)(receiverChannel + 0x15), payload.Length)}, " +
            $"successPath={receiverVisitedSuccess}, errorPath={receiverVisitedError}).");
        Require(receiver.Memory.ReadDirect((ushort)(receiverChannel + 0x14)) == payload.Length,
            "GET-PACK stored the wrong payload length in NCIBL.");
        for (int i = 0; i < payload.Length; i++)
        {
            Require(receiver.Memory.ReadDirect((ushort)(receiverChannel + 0x15 + i)) == payload[i],
                $"GET-PACK payload differs at byte {i}.");
        }

        Require(!bus.LineHigh, "The shared wire remained asserted after packet completion.");
    }

    private static void VerifyRomNetworkPacketOverTcp(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        string machineRomPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "48.rom");
        Require(File.Exists(machineRomPath), $"48K ROM not found beside Interface 1 ROM: {machineRomPath}");

        var senderBus = new SpectrumInterface1NetworkBus();
        var receiverBus = new SpectrumInterface1NetworkBus();
        using SpectrumInterface1NetworkStation senderStation = senderBus.AttachStation("TCP ROM sender");
        using SpectrumInterface1NetworkStation receiverStation = receiverBus.AttachStation("TCP ROM receiver");
        SpectrumMachine sender = CreateNetworkRomMachine(machineRomPath, firmware, senderStation);
        SpectrumMachine receiver = CreateNetworkRomMachine(machineRomPath, firmware, receiverStation);
        using var senderBridge = new SpectrumInterface1NetworkBridge(senderBus, () => sender.Cpu.Cyc);
        using var receiverBridge = new SpectrumInterface1NetworkBridge(receiverBus, () => receiver.Cpu.Cyc);
        int port = ReserveTcpPort();
        receiverBridge.Listen(port);
        senderBridge.Connect(IPAddress.Loopback.ToString(), port);
        RequireEventually(
            () => senderBridge.State == SpectrumInterface1NetworkBridgeState.Connected &&
                  receiverBridge.State == SpectrumInterface1NetworkBridgeState.Connected,
            "The ROM packet bridge did not complete its TCP handshake.");

        const ushort senderChannel = 0x6000;
        const ushort receiverChannel = 0x6200;
        byte[] payload = [0xC3, 0x5A, 0x81, 0x7E];
        PrepareNetworkRomCall(sender, returnAddress: 0x8000, ix: senderChannel);
        PrepareNetworkRomCall(receiver, returnAddress: 0x8001, ix: receiverChannel);
        PrepareNetworkChannel(sender.Memory, senderChannel, remoteStation: 2, localStation: 1, payload: payload);
        PrepareNetworkChannel(receiver.Memory, receiverChannel, remoteStation: 1, localStation: 2, payload: []);
        sender.Cpu.A = 0;
        sender.Cpu.PC = FindSendPacketEntry(firmware);
        receiver.Cpu.PC = FindGetPacketEntry(firmware);

        var realtime = Stopwatch.StartNew();
        RunLockstep(
            sender,
            0x8000,
            receiver,
            0x8001,
            maximumInstructions: 4_000_000,
            observe: (sendingMachine, receivingMachine) =>
            {
                // The production hosts are realtime-paced. The diagnostic CPUs are
                // otherwise fast enough to outrun a background TCP thread. Pace this
                // acceptance path against the same 3.5 MHz wall-clock relationship
                // without using coarse operating-system sleeps.
                long targetTicks = (long)(
                    Math.Max(sendingMachine.Cpu.Cyc, receivingMachine.Cpu.Cyc) *
                    (double)Stopwatch.Frequency / 3_500_000.0);
                while (realtime.ElapsedTicks < targetTicks)
                {
                    Thread.Yield();
                }
            });

        Require(sender.Cpu.PC == 0x8000,
            $"TCP sender did not complete SEND-PACK (PC={sender.Cpu.PC:X4}, T={sender.Cpu.Cyc}, " +
            $"receiver PC={receiver.Cpu.PC:X4}, T={receiver.Cpu.Cyc}, F={receiver.Cpu.GetFlags():X2}, " +
            $"sender edges={senderBus.CopyTransitions().Count}, receiver edges={receiverBus.CopyTransitions().Count}, " +
            $"sender line={(senderBus.LineHigh ? 1 : 0)}, receiver line={(receiverBus.LineHigh ? 1 : 0)})." );
        Require(receiver.Cpu.PC == 0x8001,
            $"TCP receiver did not complete GET-PACK (PC={receiver.Cpu.PC:X4}, T={receiver.Cpu.Cyc}).");
        Require((receiver.Cpu.GetFlags() & 0x01) == 0,
            $"TCP GET-PACK returned carry set (F={receiver.Cpu.GetFlags():X2}).");
        Require(receiver.Memory.ReadDirect((ushort)(receiverChannel + 0x14)) == payload.Length,
            "TCP GET-PACK stored the wrong payload length.");
        for (int i = 0; i < payload.Length; i++)
        {
            Require(receiver.Memory.ReadDirect((ushort)(receiverChannel + 0x15 + i)) == payload[i],
                $"TCP GET-PACK payload differs at byte {i}.");
        }

        Require(!senderBus.LineHigh && !receiverBus.LineHigh,
            "A bridged ZX Net wire remained asserted after ROM packet completion.");
    }

    private static void PrepareNetworkChannel(
        SpectrumMemory memory,
        ushort channel,
        byte remoteStation,
        byte localStation,
        ReadOnlySpan<byte> payload)
    {
        memory.WriteDirect((ushort)(channel + 0x0B), remoteStation);
        memory.WriteDirect((ushort)(channel + 0x0C), localStation);
        memory.WriteDirect((ushort)(channel + 0x0D), 0x00);
        memory.WriteDirect((ushort)(channel + 0x0E), 0x00);
        memory.WriteDirect((ushort)(channel + 0x0F), 0x00);
        memory.WriteDirect((ushort)(channel + 0x10), checked((byte)payload.Length));
        memory.WriteDirect((ushort)(channel + 0x11), 0x00);
        memory.WriteDirect((ushort)(channel + 0x12), 0x00);
        memory.WriteDirect((ushort)(channel + 0x13), 0x00);
        memory.WriteDirect((ushort)(channel + 0x14), 0x00);
        for (int i = 0; i < payload.Length; i++)
        {
            memory.WriteDirect((ushort)(channel + 0x15 + i), payload[i]);
        }
    }

    private static ushort FindSendPacketEntry(ReadOnlySpan<byte> firmware)
    {
        ReadOnlySpan<byte> signature =
        [
            0xDD, 0x77, 0x0F,       // LD (IX+0Fh),A
            0xDD, 0x46, 0x10,       // LD B,(IX+10h)
            0x3A, 0xC6, 0x5C,       // LD A,(5CC6h)
            0xD3, 0xFE              // OUT (FEh),A
        ];
        return FindFirmwareSignature(firmware, signature, "SEND-PACK");
    }

    private static ushort FindGetPacketEntry(ReadOnlySpan<byte> firmware)
    {
        // GET-N-BUF begins with the same IOBORD/DI/CALL WT-SC-E sequence.
        // The hook is distinguished by its compact success/error tails: EI,
        // AND A, JP BORD-REST followed by SCF, EI, JP BORD-REST.
        for (int offset = 0; offset <= firmware.Length - 26; offset++)
        {
            if (firmware[offset] == 0x3A && firmware[offset + 1] == 0xC6 && firmware[offset + 2] == 0x5C &&
                firmware[offset + 3] == 0xD3 && firmware[offset + 4] == 0xFE &&
                firmware[offset + 5] == 0xF3 && firmware[offset + 6] == 0xCD &&
                firmware[offset + 16] == 0xFB && firmware[offset + 17] == 0xA7 &&
                firmware[offset + 18] == 0xC3 &&
                firmware[offset + 21] == 0x37 && firmware[offset + 22] == 0xFB &&
                firmware[offset + 23] == 0xC3)
            {
                return checked((ushort)offset);
            }
        }

        throw new InvalidDataException("Could not locate GET-PACK in the supplied Interface 1 ROM.");
    }

    private static ushort FindFirmwareSignature(
        ReadOnlySpan<byte> firmware,
        ReadOnlySpan<byte> signature,
        string routine)
    {
        for (int offset = 0; offset <= firmware.Length - signature.Length; offset++)
        {
            if (firmware.Slice(offset, signature.Length).SequenceEqual(signature))
            {
                return checked((ushort)offset);
            }
        }

        throw new InvalidDataException($"Could not locate {routine} in the supplied Interface 1 ROM.");
    }

    private static string FormatBytes(SpectrumMemory memory, ushort address, int length)
    {
        var builder = new StringBuilder(length * 3);
        for (int i = 0; i < length; i++)
        {
            if (i > 0)
            {
                builder.Append('-');
            }

            builder.Append(memory.ReadDirect((ushort)(address + i)).ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static void VerifyPersistentMediaState()
    {
        var media = new SpectrumInterface1MediaState();
        MicrodriveCartridge cartridge = media.Create(3, MicrodriveCartridge.MinimumSectorCount);
        cartridge.SetWriteProtected(true);

        var firstDevice = new SpectrumInterface1Device(CreatePatternedRom());
        media.ConnectDevice(firstDevice);
        Require(ReferenceEquals(firstDevice.GetCartridge(4), cartridge), "Persistent media was not connected to its drive.");

        var replacement = new SpectrumInterface1Device(CreatePatternedRom());
        media.ConnectDevice(replacement);
        Require(firstDevice.GetCartridge(4) == null, "The replaced device retained session media.");
        Require(ReferenceEquals(replacement.GetCartridge(4), cartridge), "Media did not survive device replacement.");
        Require(media.GetPath(3) == null && media.GetCartridge(3)?.WriteProtected == true,
            "Unsaved media state changed during replacement.");

        media.ConnectDevice(null);
        Require(replacement.GetCartridge(4) == null, "Disconnect did not detach media from the old device.");
        Require(ReferenceEquals(media.GetCartridge(3), cartridge), "Disconnect discarded persistent media.");
    }


    /// <summary>
    /// Records one Microdrive record through the Interface 1 and requires the cartridge
    /// file to contain it before anything asks for a flush.
    /// </summary>
    /// <remarks>
    /// Waiting for the session to end means a cartridge survives only a tidy shutdown.
    /// The transport returning to read mode is the record boundary, and is where the
    /// image gets written back, so what this really pins down is that the boundary fires
    /// once per record and that saving is hung off it.
    /// </remarks>

    /// <summary>
    /// Writes named files into a cartridge and checks both their bytes and the shape the
    /// Interface 1 ROM expects to find them in.
    /// </summary>
    /// <remarks>
    /// The rules asserted here were read off cartridges the real ROM wrote: every record
    /// of a file carries flag bit 2, the last also carries bit 1, record zero's length
    /// counts the nine-byte header as well as its share of the data, and both the record
    /// descriptor and the 512-byte data block end in a running-sum checksum.
    /// </remarks>

    /// <summary>
    /// Saves a machine to a cartridge, then puts the pieces back on a different machine
    /// and runs the restoring code, which must bring back every register and all the RAM
    /// outside the window the loader needs for itself.
    /// </summary>
    /// <remarks>
    /// The blocks are placed directly rather than loaded through the ROM, because what is
    /// under test is the restoring code and the shape of what was written - whether the
    /// Interface 1 can find and read those files is the file-layout check's business.
    /// Reading the code back out of the cartridge rather than keeping it to hand means
    /// the whole chain is exercised: register capture, the file layout, and playback.
    /// </remarks>

    /// <summary>
    /// Saves a machine to a cartridge, replays what the loader would do on a different
    /// machine, and requires every register and all of RAM back except the screen the
    /// restore ran from.
    /// </summary>
    /// <remarks>
    /// The blocks are placed and the entry points called exactly as the generated BASIC
    /// would, following the plan the writer reports, so what is under test is the
    /// restoring code, the packing, and the addresses the loader was given. Whether the
    /// Interface 1 can find and read those files is the file-layout check's business.
    /// </remarks>
    private static void VerifyCartridgeSnapshot()
    {
        string machineRom = Path.Combine(AppContext.BaseDirectory, "ROMs", "48.rom");
        Require(File.Exists(machineRom), $"48K ROM not found: {machineRom}");

        SpectrumMachine saved = CreateBareMachine(machineRom);
        GiveMachineDistinctiveState(saved);
        for (int address = 0x4000; address <= 0xFFFF; address++)
        {
            saved.Memory.WriteDirect((ushort)address, (byte)(address * 31 >> 3));
        }

        var cartridge = MicrodriveCartridge.CreateFormatted("Snapshot", 200);
        MicrodriveSnapshotPlan plan =
            MicrodriveSnapshotWriter.Write(cartridge, saved);

        Require(
            MicrodriveFileSystem.Catalogue(cartridge).Any(entry => entry.Name == "run"
                && entry.Kind == MicrodriveFileKind.BasicProgram
                && entry.AutostartLine == 10),
            "The cartridge has no self-starting BASIC loader.");

        SpectrumMachine restored = CreateBareMachine(machineRom);
        ReplaySnapshotPlan(restored, cartridge, plan);
        RunRestore(restored, plan.RestoreEntry, saved.Cpu.PC);
        RequireRegistersMatch(saved, restored);

        int disturbedEnd = 0x4000 + plan.ScreenBytesUsed - 1;
        for (int address = disturbedEnd + 1; address <= 0xFFFF; address++)
        {
            Require(
                restored.Memory.ReadDirect((ushort)address) == saved.Memory.ReadDirect((ushort)address),
                $"RAM at {address:X4} was not restored.");
        }
    }

    /// <summary>
    /// The same round trip on a 128K, where each bank has to be paged in and unpacked over
    /// itself because there is nowhere else to put it.
    /// </summary>
    /// <remarks>
    /// Bank five is the interesting one: the screen, the workspace and the restoring code
    /// all live in it, so it cannot be paged over like the others and is rebuilt in pieces
    /// instead. The sound chip and the paging port are part of the machine too, and are
    /// checked here for the same reason the registers are.
    /// </remarks>
    private static void VerifyCartridgeSnapshot128()
    {
        string romDirectory = Path.Combine(AppContext.BaseDirectory, "ROMs");
        string[] roms =
        [
            Path.Combine(romDirectory, "128_0.ROM"),
            Path.Combine(romDirectory, "128_1.ROM")
        ];

        foreach (string rom in roms)
        {
            Require(File.Exists(rom), $"128K ROM not found: {rom}");
        }

        SpectrumMachine saved = Create128Machine(roms);
        GiveMachineDistinctiveState(saved);

        for (int bank = 0; bank < 8; bank++)
        {
            var contents = new byte[0x4000];
            for (int i = 0; i < contents.Length; i++)
            {
                // Compressible in places and not in others, so packing a bank and storing
                // one whole are both exercised.
                contents[i] = i % 512 < 200 ? (byte)(bank * 17) : (byte)((i * 7) + bank);
            }

            saved.Memory.LoadRamBank(bank, contents);
        }

        for (int register = 0; register < 16; register++)
        {
            saved.AyChip?.WriteRegister(register, (byte)(0x20 + register));
        }

        saved.Ports.WriteUncontended(0x7FFD, 0x03);

        // Deliberately the default size: a 128K snapshot fitting a newly created
        // cartridge is the reason that default is what it is.
        var cartridge = MicrodriveCartridge.CreateFormatted("Snap128");
        MicrodriveSnapshotPlan plan =
            MicrodriveSnapshotWriter.Write(cartridge, saved);

        Require(plan.PackedBytes < plan.UnpackedBytes,
            $"Packing did not shrink the banks: {plan.PackedBytes} against {plan.UnpackedBytes}.");

        // BASIC, its channels and its stack share what is under RAMTOP with the Interface
        // 1 channel buffer. Too long a loader and the load stops before it starts.
        int usable = cartridge.SectorCount * MicrodriveCartridge.DataLength;
        Require(plan.PackedBytes < usable,
            $"A 128K snapshot needs {plan.PackedBytes} bytes of a {cartridge.SectorCount}-sector "
                + $"cartridge, which holds {usable}.");

        Require(plan.LoaderBytes <= 400,
            $"The loader is {plan.LoaderBytes} bytes, which leaves the Interface 1 too "
                + "little room to open its channel.");

        SpectrumMachine restored = Create128Machine(roms);
        ReplaySnapshotPlan(restored, cartridge, plan);
        RunRestore(restored, plan.RestoreEntry, saved.Cpu.PC);
        RequireRegistersMatch(saved, restored);

        Require(restored.Memory.Port7FFD == saved.Memory.Port7FFD,
            $"The paging port came back as {restored.Memory.Port7FFD:X2}, "
                + $"not {saved.Memory.Port7FFD:X2}.");

        byte[]? savedAy = saved.AyChip?.CopyRegisters();
        byte[]? restoredAy = restored.AyChip?.CopyRegisters();
        if (savedAy != null && restoredAy != null)
        {
            for (int register = 0; register < 14; register++)
            {
                Require(restoredAy[register] == savedAy[register],
                    $"Sound register {register} came back as {restoredAy[register]:X2}, "
                        + $"not {savedAy[register]:X2}.");
            }
        }

        // Everything except the slice of bank five the restore ran from.
        int disturbed = plan.ScreenBytesUsed;
        for (int bank = 0; bank < 8; bank++)
        {
            byte[] before = saved.Memory.CopyRamBank(bank);
            byte[] after = restored.Memory.CopyRamBank(bank);
            int from = bank == 5 ? disturbed : 0;
            for (int i = from; i < before.Length; i++)
            {
                Require(after[i] == before[i],
                    $"Bank {bank} differs at {i:X4}: {after[i]:X2} against {before[i]:X2}.");
            }
        }
    }

    private static void GiveMachineDistinctiveState(SpectrumMachine machine)
    {
        Z80 cpu = machine.Cpu;
        cpu.A = 0x12;
        cpu.SetFlags(0x34);
        cpu.B = 0x56;
        cpu.C = 0x78;
        cpu.D = 0x9A;
        cpu.E = 0xBC;
        cpu.H = 0xDE;
        cpu.L = 0xF0;
        cpu.A_ = 0x0F;
        cpu.F_ = 0x1E;
        cpu.B_ = 0x2D;
        cpu.C_ = 0x3C;
        cpu.D_ = 0x4B;
        cpu.E_ = 0x5A;
        cpu.H_ = 0x69;
        cpu.L_ = 0x78;
        cpu.IX = 0xA55A;
        cpu.IY = 0x5AA5;
        cpu.SP = 0x7F00;
        cpu.PC = 0x9000;
        cpu.I = 0x7E;
        cpu.SetInterruptState(2, iff1: true, iff2: true);
        machine.Renderer.BorderColorIndex = 5;
    }

    /// <summary>Does what the generated BASIC does: load, and call what it is told to.</summary>
    /// <remarks>
    /// Paging is deliberately not done here. The generated code pages the first bank in
    /// and then each bank's routine pages in the one after it, which is what keeps the
    /// BASIC short enough for the Interface 1 to have room to work; leaving that to the
    /// routines is what puts them under test.
    /// </remarks>
    private static void ReplaySnapshotPlan(
        SpectrumMachine target,
        MicrodriveCartridge cartridge,
        MicrodriveSnapshotPlan plan)
    {
        bool pagedFirst = false;
        foreach (MicrodriveSnapshotStep step in plan.Steps)
        {
            if (!pagedFirst && step.Bank >= 0 && plan.PageFirstBank != null)
            {
                RunToReturn(target, plan.PageFirstBank.Value);
                pagedFirst = true;
            }

            if (step.Bank >= 0)
            {
                Require((target.Memory.Port7FFD & 0x07) == step.Bank,
                    $"'{step.File}' expected bank {step.Bank} paged in, but "
                        + $"{target.Memory.Port7FFD & 0x07} is.");
            }

            byte[] data = MicrodriveFileSystem.TryReadFile(cartridge, step.File)
                ?? throw new InvalidOperationException($"'{step.File}' is missing.");
            for (int i = 0; i < data.Length; i++)
            {
                target.Memory.WriteDirect((ushort)(step.LoadAddress + i), data[i]);
            }

            if (step.Unpack != null)
            {
                RunToReturn(target, step.Unpack.Value);
            }
        }
    }

    /// <summary>Calls an entry point and waits for it to come back.</summary>
    /// <remarks>
    /// The stack sits in the part of the screen the restore has already claimed, which is
    /// the one place that is neither being unpacked into nor restored.
    /// </remarks>
    private static void RunToReturn(SpectrumMachine machine, ushort entry)
    {
        const ushort Sentinel = 0x0038;
        const ushort Stack = 0x4AF0;

        machine.Cpu.SP = Stack;
        machine.Memory.WriteDirect(Stack, (byte)(Sentinel & 0xFF));
        machine.Memory.WriteDirect((ushort)(Stack + 1), (byte)(Sentinel >> 8));
        machine.Cpu.PC = entry;

        int steps = 0;
        while (machine.Cpu.PC != Sentinel && steps++ < 10_000_000)
        {
            machine.Emulator.StepInstruction();
        }

        Require(machine.Cpu.PC == Sentinel,
            $"The routine at {entry:X4} did not return; it stopped at {machine.Cpu.PC:X4}.");
    }

    private static void RunRestore(SpectrumMachine machine, ushort entry, ushort expected)
    {
        machine.Cpu.PC = entry;
        machine.Cpu.SetInterruptState(0, iff1: false, iff2: false);

        int steps = 0;
        while (machine.Cpu.PC != expected && steps++ < 100_000)
        {
            machine.Emulator.StepInstruction();
        }

        Require(machine.Cpu.PC == expected,
            $"The restore ended at {machine.Cpu.PC:X4}, not {expected:X4}.");
    }

    private static void RequireRegistersMatch(SpectrumMachine saved, SpectrumMachine restored)
    {
        Z80 want = saved.Cpu;
        Z80 got = restored.Cpu;
        Require(got.AF == want.AF, $"AF came back as {got.AF:X4}, not {want.AF:X4}.");
        Require(got.BC == want.BC && got.DE == want.DE && got.HL == want.HL,
            $"BC/DE/HL came back as {got.BC:X4}/{got.DE:X4}/{got.HL:X4}.");
        Require(got.AF_ == want.AF_, $"AF' came back as {got.AF_:X4}, not {want.AF_:X4}.");
        Require(got.BC_ == want.BC_ && got.DE_ == want.DE_ && got.HL_ == want.HL_,
            $"The alternate registers came back as {got.BC_:X4}/{got.DE_:X4}/{got.HL_:X4}.");
        Require(got.IX == want.IX && got.IY == want.IY,
            $"IX/IY came back as {got.IX:X4}/{got.IY:X4}.");
        Require(got.SP == want.SP, $"SP came back as {got.SP:X4}, not {want.SP:X4}.");
        Require(got.I == want.I, $"I came back as {got.I:X2}, not {want.I:X2}.");
        Require(got.InterruptModeValue == want.InterruptModeValue,
            $"Interrupt mode came back as {got.InterruptModeValue}.");
        Require(got.Iff1, "Interrupts were left disabled by a snapshot that had them on.");
        Require(restored.Renderer.BorderColorIndex == saved.Renderer.BorderColorIndex,
            $"The border came back as {restored.Renderer.BorderColorIndex}.");
    }

    private static SpectrumMachine Create128Machine(string[] roms) =>
        SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum128K,
            Roms = RomSet.LoadFromFiles(roms),
            RenderEnabled = false
        });

    /// <summary>
    /// Packs a block, lays it down where packing says it must go, and has the processor
    /// unpack it over itself.
    /// </summary>
    /// <remarks>
    /// Unpacking in place is what lets a bank be restored when there is nowhere to put it,
    /// and it only works if the writer never catches the reader. Whether it does depends on
    /// the data, so packing works out how far in the block has to be loaded and this runs
    /// the result: the block deliberately mixes long runs, repeated structure and
    /// incompressible noise, which is what decides where the worst moment falls.
    /// </remarks>
    private static void VerifyCompressedUnpack()
    {
        const ushort Destination = 0xC000;
        const int Length = 0x4000;
        const ushort RoutineAt = 0x8000;
        const ushort Sentinel = 0x7000;

        var original = new byte[Length];
        var random = new Random(20260902);
        int position = 0;
        while (position < Length)
        {
            int choice = random.Next(4);
            if (choice == 0)
            {
                int run = Math.Min(random.Next(5, 300), Length - position);
                byte value = (byte)random.Next(256);
                for (int i = 0; i < run; i++)
                {
                    original[position++] = value;
                }
            }
            else if (choice == 1 && position > 512)
            {
                // Repeat something from earlier, which is what a reference encodes.
                int from = random.Next(position - 512);
                int run = Math.Min(random.Next(4, 60), Length - position);
                for (int i = 0; i < run; i++)
                {
                    original[position++] = original[from + i];
                }
            }
            else
            {
                int run = Math.Min(random.Next(1, 40), Length - position);
                for (int i = 0; i < run; i++)
                {
                    original[position++] = (byte)random.Next(256);
                }
            }
        }

        MicrodrivePackedBlock? packed = MicrodriveCompression.TryPack(original);
        Require(packed != null, "A compressible block did not pack.");
        Require(
            MicrodriveCompression.Unpack(packed!.Data, Length).SequenceEqual(original),
            "Packing and unpacking on the host did not round trip.");
        Require(packed.LoadOffset + packed.Data.Length <= Length,
            "The packed block does not fit inside its own destination.");

        string machineRom = Path.Combine(AppContext.BaseDirectory, "ROMs", "48.rom");
        Require(File.Exists(machineRom), $"48K ROM not found: {machineRom}");
        SpectrumMachine machine = CreateBareMachine(machineRom);

        int packedStart = Destination + packed.LoadOffset;
        for (int i = 0; i < packed.Data.Length; i++)
        {
            machine.Memory.WriteDirect((ushort)(packedStart + i), packed.Data[i]);
        }

        byte[] routine = MicrodriveCompression.UnpackRoutine(RoutineAt);
        for (int i = 0; i < routine.Length; i++)
        {
            machine.Memory.WriteDirect((ushort)(RoutineAt + i), routine[i]);
        }

        machine.Cpu.SP = 0x7FF0;
        machine.Memory.WriteDirect(0x7FF0, (byte)(Sentinel & 0xFF));
        machine.Memory.WriteDirect(0x7FF1, (byte)(Sentinel >> 8));
        machine.Cpu.H = (byte)(packedStart >> 8);
        machine.Cpu.L = (byte)(packedStart & 0xFF);
        machine.Cpu.D = Destination >> 8;
        machine.Cpu.E = Destination & 0xFF;
        machine.Cpu.PC = RoutineAt;

        int steps = 0;
        while (machine.Cpu.PC != Sentinel && steps++ < 5_000_000)
        {
            machine.Emulator.StepInstruction();
        }

        Require(machine.Cpu.PC == Sentinel,
            $"The unpacker did not return; it stopped at {machine.Cpu.PC:X4}.");

        int different = 0;
        int firstDifference = -1;
        for (int i = 0; i < Length; i++)
        {
            if (machine.Memory.ReadDirect((ushort)(Destination + i)) != original[i])
            {
                different++;
                if (firstDifference < 0)
                {
                    firstDifference = i;
                }
            }
        }

        Require(different == 0,
            $"{different} of {Length} unpacked bytes are wrong, from offset {firstDifference:X4}. "
                + $"Packed to {packed.Data.Length} bytes, loaded {packed.LoadOffset} in.");
    }

    private static SpectrumMachine CreateBareMachine(string machineRom) =>
        SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRom]),
            RenderEnabled = false
        });

    private static void VerifyCartridgeFiles()
    {
        var cartridge = MicrodriveCartridge.CreateFormatted("Files", 60);

        // Longer than one record, and not a multiple of one, so the split and the final
        // short record are both exercised.
        byte[] code = new byte[1500];
        for (int i = 0; i < code.Length; i++)
        {
            code[i] = (byte)(i * 7);
        }

        byte[] program = [0x00, 0x0A, 0x05, 0x00, 0xF9, 0xC0, 0x31, 0x0E, 0x0D];

        MicrodriveFileSystem.WriteCode(cartridge, "SNAPHI", code, 0x8000);
        MicrodriveFileSystem.WriteBasicProgram(cartridge, "run", program, autostartLine: 10);

        byte[]? readBack = MicrodriveFileSystem.TryReadFile(cartridge, "SNAPHI");
        Require(readBack != null, "The code file could not be read back.");
        Require(readBack!.SequenceEqual(code), "The code file did not survive a round trip.");

        byte[]? programBack = MicrodriveFileSystem.TryReadFile(cartridge, "run");
        Require(programBack != null && programBack.SequenceEqual(program),
            "The BASIC program did not survive a round trip.");

        IReadOnlyList<MicrodriveFileEntry> catalogue = MicrodriveFileSystem.Catalogue(cartridge);
        Require(catalogue.Count == 2, $"The catalogue listed {catalogue.Count} files, expected 2.");

        MicrodriveFileEntry saved = catalogue.First(entry => entry.Name == "SNAPHI");
        Require(saved.Kind == MicrodriveFileKind.Code, "The code file was not recorded as CODE.");
        Require(saved.Length == code.Length,
            $"The catalogue reported {saved.Length} bytes for a {code.Length}-byte file.");
        Require(saved.Start == 0x8000, $"The code file's start address came back as {saved.Start:X4}.");
        Require(saved.RecordCount == 3,
            $"{code.Length} bytes plus a header took {saved.RecordCount} records, expected 3.");

        MicrodriveFileEntry basic = catalogue.First(entry => entry.Name == "run");
        Require(basic.Kind == MicrodriveFileKind.BasicProgram, "The program was not recorded as BASIC.");
        Require(basic.AutostartLine == 10,
            $"The program's autostart line came back as {basic.AutostartLine}.");

        // Now the layout itself, sector by sector.
        int inUse = 0;
        int endMarkers = 0;
        int firstRecordLength = 0;
        for (int sector = 0; sector < cartridge.SectorCount; sector++)
        {
            int offset = sector * MicrodriveCartridge.SectorLength;
            byte flags = cartridge.ReadByte(offset + MicrodriveCartridge.HeaderLength);
            if ((flags & 0x04) == 0)
            {
                continue;
            }

            inUse++;
            if ((flags & 0x02) != 0)
            {
                endMarkers++;
            }

            byte[] descriptor = new byte[MicrodriveCartridge.HeaderLength];
            for (int i = 0; i < descriptor.Length; i++)
            {
                descriptor[i] = cartridge.ReadByte(offset + MicrodriveCartridge.HeaderLength + i);
            }

            Require(
                MicrodriveCartridge.ChecksumOf(descriptor.AsSpan(0, 14)) == descriptor[14],
                $"Sector {sector}'s record descriptor checksum is wrong.");

            byte[] block = new byte[MicrodriveCartridge.DataLength];
            int dataOffset = offset + (2 * MicrodriveCartridge.HeaderLength);
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = cartridge.ReadByte(dataOffset + i);
            }

            Require(
                MicrodriveCartridge.ChecksumOf(block) == cartridge.ReadByte(dataOffset + block.Length),
                $"Sector {sector}'s data checksum is wrong.");

            if (descriptor[1] == 0 && descriptor[4] == 'S')
            {
                firstRecordLength = descriptor[2] | (descriptor[3] << 8);
            }
        }

        Require(inUse == 4, $"The two files occupy {inUse} sectors, expected 4.");
        Require(endMarkers == 2, $"{endMarkers} records are marked as a file's last, expected 2.");
        Require(firstRecordLength == MicrodriveCartridge.DataLength,
            $"The first record of a long file reported {firstRecordLength} bytes.");
    }

    private static void VerifyWriteReachesFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zedexess-if1-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "autosave-test.mdr");
        Directory.CreateDirectory(directory);
        try
        {
            var media = new SpectrumInterface1MediaState();
            media.Create(0, MicrodriveCartridge.MinimumSectorCount, "AutoSave");
            media.SaveAs(0, path);

            var device = new SpectrumInterface1Device(CreatePatternedRom());
            media.ConnectDevice(device);
            SelectDriveOne(device);

            int completed = 0;
            device.MicrodriveWriteCompleted += () => completed++;

            byte[] header = Enumerable.Range(0, MicrodriveCartridge.HeaderLength)
                .Select(static i => (byte)(0x40 + i))
                .ToArray();

            // Erase lead-in, write head on, the record itself, then back to read mode.
            device.Write(0x00EF, 0xE6);
            device.Write(0x00EF, 0xE2);
            WritePreamble(device);
            foreach (byte value in header)
            {
                device.Write(0x00E7, value);
            }

            Require(completed == 0, "The write boundary fired before the transport left write mode.");
            device.Write(0x00EF, 0xEE);
            Require(completed == 1,
                $"Recording one record signalled {completed} write boundaries; it should signal one.");

            MicrodriveCartridge? cartridge = media.GetCartridge(0);
            Require(cartridge?.Modified == false,
                "The cartridge was still dirty after a record was recorded.");

            // Nothing has asked for a flush: the file must already hold the record.
            MicrodriveCartridge reloaded = MicrodriveCartridge.Load(path);
            for (int i = 0; i < header.Length; i++)
            {
                Require(
                    reloaded.ReadByte(i) == header[i],
                    $"Recorded byte {i} had not reached the cartridge file.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyDirtyMediaFlush()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zedexess-if1-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "flush-test.mdr");
        Directory.CreateDirectory(directory);
        try
        {
            var media = new SpectrumInterface1MediaState();
            MicrodriveCartridge cartridge = media.Create(0, MicrodriveCartridge.MinimumSectorCount, "FlushTest");
            media.SaveAs(0, path);

            byte original = cartridge.ReadByte(30);
            byte replacement = (byte)(original ^ 0x5A);
            Require(cartridge.TryWriteByte(30, replacement), "Writable cartridge rejected a verification byte.");
            Require(cartridge.Modified, "Changing a cartridge byte did not mark the image dirty.");

            media.FlushAll();
            Require(!cartridge.Modified, "Flushing a cartridge did not clear its dirty state.");

            MicrodriveCartridge reloaded = MicrodriveCartridge.Load(path);
            Require(reloaded.ReadByte(30) == replacement,
                "A dirty cartridge byte was lost when the saved MDR was reloaded.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySnapshotPersistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zedexess-if1-state-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "snapshot-test.mdr");
        Directory.CreateDirectory(directory);
        try
        {
            var media = new SpectrumInterface1MediaState();
            MicrodriveCartridge cartridge = media.Create(
                0,
                MicrodriveCartridge.MinimumSectorCount,
                "Snapshot");
            media.SaveAs(0, path);

            var device = new SpectrumInterface1Device(CreatePatternedRom());
            media.ConnectDevice(device);
            SelectDriveOne(device);
            device.BeforeOpcodeFetch(0x0008);
            device.Write(0x00F7, 0x01);
            device.Write(0x00EF, 0xE2);
            WritePreamble(device);
            device.Write(0x00E7, 0x91);
            device.Write(0x00E7, 0x92);
            device.Write(0x00E7, 0x93);
            Require(cartridge.TryWriteByte(20, 0x42), "Could not prepare a cartridge byte for snapshot verification.");
            cartridge.SetWriteProtected(true);

            SpectrumInterface1Snapshot captured = media.CaptureSnapshot();
            SpectrumInterface1MediaSlotState capturedSlot = captured.Media.Slots[0];
            MicrodriveCartridgeState capturedCartridge = capturedSlot.Cartridge
                ?? throw new InvalidOperationException("Captured snapshot omitted the mounted cartridge.");
            byte[] capturedData = capturedCartridge.CopyData();
            byte[] capturedPreambles = capturedCartridge.CopyPreambleState();

            // Exercise reconstruction of preamble state which MDR files do not
            // contain. This is the state a native snapshot serializer will carry.
            int recordPreamble = capturedCartridge.SectorCount;
            capturedPreambles[recordPreamble] = 5;
            var replacementCartridgeState = new MicrodriveCartridgeState(
                capturedCartridge.SectorCount,
                capturedData,
                capturedPreambles,
                capturedCartridge.WriteProtected,
                capturedCartridge.Modified);
            SpectrumInterface1MediaSlotState[] slots = captured.Media.Slots.ToArray();
            slots[0] = new SpectrumInterface1MediaSlotState(capturedSlot.BackingPath, replacementCartridgeState);
            captured = new SpectrumInterface1Snapshot(
                new SpectrumInterface1MediaSnapshot(slots),
                captured.Device);

            SpectrumInterface1DeviceState expectedDevice = captured.Device
                ?? throw new InvalidOperationException("Connected device state was not captured.");

            // Mutate every ownership layer after capture. None of these changes
            // may leak into the saved state, and discarded dirty media must not be
            // flushed merely because the snapshot is restored.
            cartridge.SetWriteProtected(false);
            Require(cartridge.TryWriteByte(20, 0x99), "Could not mutate live cartridge after capture.");
            device.Write(0x00F7, 0x00);
            device.Reset();
            _ = media.Eject(0, saveDirtyImage: false);
            Require(replacementCartridgeState.CopyData()[20] == 0x42,
                "Live cartridge writes changed the deep-copied snapshot.");

            media.RestoreSnapshot(captured);

            MicrodriveCartridge restored = media.GetCartridge(0)
                ?? throw new InvalidOperationException("Snapshot restore did not remount drive 1.");
            Require(media.GetPath(0) == Path.GetFullPath(path), "Snapshot restore lost the MDR backing path.");
            Require(restored.ReadByte(20) == 0x42, "Snapshot restore returned mutated future cartridge data.");
            Require(restored.WriteProtected, "Snapshot restore lost cartridge write protection.");
            Require(restored.Modified, "Snapshot restore lost the cartridge dirty flag.");
            Require(restored.GetPreambleState(recordPreamble) == 5,
                "Snapshot restore lost an in-progress record preamble.");
            Require(ReferenceEquals(device.GetCartridge(1), restored),
                "Restored media was not reconnected to the active Interface 1 device.");

            SpectrumInterface1Snapshot roundTrip = media.CaptureSnapshot();
            SpectrumInterface1DeviceState actualDevice = roundTrip.Device
                ?? throw new InvalidOperationException("Restored device state could not be recaptured.");
            Require(actualDevice.IsPaged == expectedDevice.IsPaged, "ROMCS paging state changed during restore.");
            Require(actualDevice.Control == expectedDevice.Control, "Control latch changed during restore.");
            Require(actualDevice.NetworkOutput == expectedDevice.NetworkOutput, "Network latch changed during restore.");
            Require(actualDevice.MotorMask == expectedDevice.MotorMask, "Motor selection changed during restore.");
            Require(actualDevice.Activity == expectedDevice.Activity, "Activity state changed during restore.");
            for (int drive = 0; drive < SpectrumInterface1Device.DriveCount; drive++)
            {
                Require(actualDevice.Drives[drive] == expectedDevice.Drives[drive],
                    $"Drive {drive + 1} transport state changed during restore.");
            }

            byte[] hostImage = File.ReadAllBytes(path);
            Require(hostImage[20] != 0x99,
                "Restoring a snapshot flushed discarded future cartridge data to the MDR file.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreatePatternedRom()
    {
        byte[] firmware = new byte[SpectrumInterface1Device.RomSize];
        for (int i = 0; i < firmware.Length; i++)
        {
            firmware[i] = (byte)((i * 37 + (i >> 8) + 0x41) & 0xFF);
        }

        return firmware;
    }

    private static byte[] CreatePatternedMdr(bool writeProtected)
    {
        int dataLength = MicrodriveCartridge.MinimumSectorCount * MicrodriveCartridge.SectorLength;
        byte[] image = new byte[dataLength + 1];
        for (int i = 0; i < dataLength; i++)
        {
            image[i] = (byte)((i * 19 + 0x23) & 0xFF);
        }

        image[^1] = writeProtected ? (byte)1 : (byte)0;
        return image;
    }

    private static void SelectDriveOne(SpectrumInterface1Device device)
    {
        // Preserve the ROM's idle/read gate state while clocking an active-low
        // COMMS DATA bit into drive 1's motor-selection shift register.
        device.Write(0x00EF, 0xEE);
        device.Write(0x00EF, 0xEC);
        Require(device.IsMotorRunning(1), "Drive 1 was not selected.");
    }

    private static void WritePreamble(SpectrumInterface1Device device)
    {
        for (int i = 0; i < 10; i++)
        {
            device.Write(0x00E7, 0x00);
        }

        device.Write(0x00E7, 0xFF);
        device.Write(0x00E7, 0xFF);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
