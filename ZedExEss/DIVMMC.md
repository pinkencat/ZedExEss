Below is a baseline emulator specification for **original DivIDE-compatible hardware** and **original DivMMC-compatible hardware**. Treat later devices such as DivIDE Plus, Spectrum Next internal DivMMC, DivSD, DivTIESUS, EnJOY variants with extras, RTC, joystick, mouse, or all-RAM modes as separate extensions unless you explicitly want those variants.

The key design rule is:

**DivMMC = DivIDE memory mapper + more 8 KB RAM pages + SD/MMC SPI ports instead of IDE/ATA ports.** The DivMMC memory mapping is described as DivIDE-compatible, with at least 128 KB SRAM and up to 512 KB, while the SD interface uses ports `#E7` and `#EB`. ([Spectrum for Everyone][1])

---

## 1. Global bus and decoding model

Use Z80-style active-low signals conceptually:

```text
/MREQ  memory request
/IORQ  I/O request
/RD    read
/WR    write
/M1    opcode fetch or interrupt acknowledge marker
```

For I/O, both DivIDE and DivMMC decode **only the low 8 address bits**, `A0..A7`; the high byte of the Z80 port address is ignored. The original DivIDE programming model states that all ports are decoded with low address lines only, and the original DivMMC VHDL similarly assigns `address <= A(7 downto 0)`. ([Divide][2])

For emulator purposes:

```c
uint8_t p = port & 0xff;
```

Decode I/O only when `/IORQ` is active and the cycle is **not** an interrupt-acknowledge cycle. In the DivMMC VHDL, the I/O port logic is gated by `iorq='0'` and `m1='1'`, so do not respond during `/IORQ + /M1` interrupt acknowledge. ([GitHub][3])

All normal DivIDE/DivMMC ports have low bit 1, so they do not collide with the ULA’s broad `A0=0` port decoding.

---

## 2. Common DivIDE/DivMMC memory mapper

### 2.1 External memory layout

When the interface is mapped in, it replaces the Spectrum’s normal `0000h-3FFFh` ROM area with:

```text
0000h-1FFFh  8 KB DivIDE/DivMMC ROM, EEPROM, or MAPRAM bank 3
2000h-3FFFh  8 KB DivIDE/DivMMC RAM bank
```

The original DivIDE contains an 8 KB ROM/EPROM/EEPROM at `0000h-1FFFh` and 32 KB RAM paged as four 8 KB banks at `2000h-3FFFh`. ([Divide][2]) DivMMC keeps this mapping model but normally provides more SRAM, commonly 128 KB or up to 512 KB. ([Spectrum for Everyone][1])

State you should keep:

```c
bool conmem;       // control bit 7
bool mapram;       // sticky control bit 6
bool automap;      // automatic mapping latch
uint8_t bank;      // DivIDE: 0..3; DivMMC: usually 0..15 or 0..63
```

The external memory is active when:

```c
div_active = conmem || automap;
```

If inactive, use the Spectrum’s normal memory map.

### 2.2 Control port `#E3`

Port:

```text
low byte #E3, decimal 227
write-only on original DivIDE/DivMMC-compatible hardware
```

Original DivIDE bit layout:

```text
bit 7  CONMEM
bit 6  MAPRAM
bits 5..2 reserved on original 32 KB DivIDE
bits 1..0 BANK1..BANK0
```

The original documentation says the `#E3` register is write-only, power-on clears its bits, bits 2..5 should be written as zero on 32 KB DivIDE for future compatibility, and `MAPRAM` can only be cleared by power-on; reset leaves `MAPRAM` unchanged. ([Divide][2])

Write behavior for original DivIDE:

```c
void write_E3_divide(uint8_t v) {
    conmem = (v & 0x80) != 0;
    mapram = mapram || ((v & 0x40) != 0);   // sticky 1
    bank   = v & 0x03;                      // four 8 KB banks
    ata_data_phase = LOW_NEXT;              // DivIDE ATA bridge rule
}
```

Write behavior for original-style DivMMC:

```c
void write_E3_divmmc(uint8_t v) {
    conmem = (v & 0x80) != 0;
    mapram = mapram || ((v & 0x40) != 0);   // sticky 1
    bank   = v & bank_mask;                 // e.g. 0x0f for 128 KB, 0x3f for 512 KB
}
```

The original DivMMC VHDL stores `D(5 downto 0)` as the RAM bank, uses `D(6) or mapram` for sticky `MAPRAM`, and uses `D(7)` for `CONMEM`. ([GitHub][3])

Suggested masks:

```text
DivIDE 32 KB      4 pages   bank_mask = 0x03
DivMMC 128 KB    16 pages   bank_mask = 0x0f
DivMMC 512 KB    64 pages   bank_mask = 0x3f
```

On original hardware, reads from `#E3` are not defined. Return open bus, `0xFF`, or your emulator’s normal floating-bus value. The Spectrum Next’s internal DivMMC makes `#E3` readable and adds enhancements, but that is not the original baseline. ([wiki.specnext.dev][4])

### 2.3 Memory read/write source table

When `div_active == false`:

```text
0000h-FFFFh  normal Spectrum memory behavior
```

When `div_active == true`:

| Address range | Condition                                       | Source                        | Writable?                                                |
| ------------: | ----------------------------------------------- | ----------------------------- | -------------------------------------------------------- |
| `0000h-1FFFh` | `CONMEM=1`                                      | external ROM/EPROM/EEPROM     | only if writable EEPROM/flash and write protect disabled |
| `0000h-1FFFh` | `CONMEM=0, MAPRAM=1`                            | RAM bank 3, zero-based page 3 | no                                                       |
| `0000h-1FFFh` | `CONMEM=0, MAPRAM=0`                            | external ROM/EPROM/EEPROM     | no                                                       |
| `2000h-3FFFh` | active, selected bank not bank 3 in MAPRAM mode | selected RAM bank             | yes                                                      |
| `2000h-3FFFh` | `CONMEM=0, MAPRAM=1, bank=3`                    | RAM bank 3                    | no                                                       |
| `2000h-3FFFh` | `CONMEM=1, bank=3`                              | RAM bank 3                    | yes                                                      |
| `4000h-FFFFh` | any                                             | normal Spectrum memory        | normal Spectrum rules                                    |

The original DivIDE documentation explicitly says `CONMEM` overrides `MAPRAM` in the lower 8 KB, that `CONMEM` maps ROM/EPROM/EEPROM at `0000h-1FFFh` and selected RAM at `2000h-3FFFh`, and that MAPRAM mode uses write-protected bank 3 as ROM. ([Divide][2]) The DivMMC VHDL implements the same idea by forcing the lower 8 KB RAM bank address to bank 3 when RAM is used there, while using the selected bank for the upper 8 KB. ([GitHub][3])

A useful read helper:

```c
uint8_t div_read(uint16_t a) {
    if (!div_active || a >= 0x4000)
        return spectrum_read(a);

    if (a < 0x2000) {
        if (!conmem && mapram)
            return div_ram[3][a & 0x1fff];

        if (rom_present)
            return div_rom[a & 0x1fff];

        return floating_bus();
    }

    return div_ram[bank & bank_mask][a & 0x1fff];
}
```

A useful write helper:

```c
void div_write(uint16_t a, uint8_t v) {
    if (!div_active || a >= 0x4000) {
        spectrum_write(a, v);
        return;
    }

    if (a < 0x2000) {
        if (conmem && rom_is_eeprom && eeprom_write_enabled)
            div_rom[a & 0x1fff] = v;       // simplified; real flash has command protocol
        return;
    }

    uint8_t b = bank & bank_mask;

    if (!conmem && mapram && b == 3)
        return;                            // bank 3 write protected in MAPRAM mode

    div_ram[b][a & 0x1fff] = v;
}
```

If you emulate EEPROM/flash programming, use the command algorithm of the particular chip image you are modelling. For most emulator use, treating firmware ROM as read-only is sufficient unless you want in-system flashing.

---

## 3. Automatic ROM/RAM mapping

Automatic mapping happens only on **opcode fetches**, not arbitrary memory reads or writes. The original trap addresses are:

```text
0000h
0008h
0038h
0066h
04C6h
0562h
3D00h-3DFFh
```

The first six addresses map the interface in after the opcode fetch, at the refresh cycle following the M1 fetch. The `3D00h-3DFFh` range maps in immediately during the opcode fetch, about 100 ns after `/MREQ` falls, so the current opcode can be fetched from the external mapping. ([Divide][2])

Automatic mapping is switched off by an opcode fetch from:

```text
1FF8h-1FFFh
```

This “off-area” unmaps at the refresh cycle of that instruction fetch, so the instruction fetched from `1FF8h-1FFFh` is still fetched from DivIDE/DivMMC memory; the following instruction sees normal Spectrum memory unless `CONMEM` is still set. ([Divide][2])

Cycle-approximate emulator hook:

```c
static inline bool is_entry_trap(uint16_t pc) {
    return pc == 0x0000 || pc == 0x0008 || pc == 0x0038 ||
           pc == 0x0066 || pc == 0x04c6 || pc == 0x0562;
}

static inline bool is_3d_trap(uint16_t pc) {
    return (pc & 0xff00) == 0x3d00;
}

static inline bool is_off_area(uint16_t pc) {
    return pc >= 0x1ff8 && pc <= 0x1fff;
}

uint8_t z80_fetch_opcode(uint16_t pc) {
    /*
       For 3D00-3DFF, map before the opcode byte is read.
       This is the special immediate TR-DOS trap.
    */
    if (automap_enabled && is_3d_trap(pc))
        automap = true;

    uint8_t op = div_read(pc);

    /*
       For the other entry points, the opcode itself is fetched from
       the old mapping; the mapper becomes active after the M1 fetch.
    */
    if (automap_enabled && is_entry_trap(pc))
        automap = true;

    /*
       Off-area unmaps after fetching the opcode from DivIDE/DivMMC memory.
       CONMEM is independent; clearing automap does not override CONMEM.
    */
    if (is_off_area(pc))
        automap = false;

    return op;
}
```

The original document says automatic mapping occurs only if external EPROM/EEPROM is present, or if `MAPRAM` is set. ([Divide][2]) In an emulator with a firmware ROM image loaded, just set `automap_enabled = true`. If modelling jumpers or a missing ROM socket, make that configurable.

`0066h` is the Z80 NMI vector, so a physical NMI button does not need a special DivIDE/DivMMC port: it asserts `/NMI`, the CPU vectors to `0066h`, and the normal automap rule enters the NMI firmware.

---

## 4. DivIDE IDE/ATA interface

DivIDE provides the ATA command block registers via low-byte-decoded I/O ports. The original programming model gives the pattern:

```text
xxxx xxxx 101r rr11
```

So in code:

```c
bool is_divide_ata_port(uint16_t port) {
    uint8_t p = port & 0xff;
    return (p & 0xe3) == 0xa3;
}

uint8_t ata_reg_index(uint16_t port) {
    return ((port & 0xff) >> 2) & 7;
}
```

Port table:

| Low port | ATA register               | Read               | Write          |
| -------: | -------------------------- | ------------------ | -------------- |
|    `#A3` | data                       | data               | data           |
|    `#A7` | error/features             | error              | features       |
|    `#AB` | sector count               | sector count       | sector count   |
|    `#AF` | sector number / LBA 0..7   | LBA low            | LBA low        |
|    `#B3` | cylinder low / LBA 8..15   | LBA mid            | LBA mid        |
|    `#B7` | cylinder high / LBA 16..23 | LBA high           | LBA high       |
|    `#BB` | drive/head / LBA 24..28    | drive/head         | drive/head     |
|    `#BF` | status/command             | status             | command        |
|    `#E3` | DivIDE control             | undefined/open bus | mapper control |

The original DivIDE programming model lists exactly these eight ATA command block ports plus the `#E3` control register. ([Divide][2]) There is no separate alternate-status/device-control port in the original DivIDE programming model.

### 4.1 16-bit ATA data bridge

The ATA data register is 16 bits wide. The Z80 sees it as two 8-bit accesses at port `#A3`.

Maintain a flip-flop:

```c
enum { LOW_NEXT, HIGH_NEXT } ata_data_phase;
uint8_t ata_high_latch;
uint8_t ata_low_latch;
```

Read behavior:

```c
uint8_t read_A3(void) {
    if (ata_data_phase == LOW_NEXT) {
        uint16_t w = ata_read_data_word();     // from ATA sector/identify buffer
        ata_high_latch = (uint8_t)(w >> 8);
        ata_data_phase = HIGH_NEXT;
        return (uint8_t)(w & 0xff);
    } else {
        ata_data_phase = LOW_NEXT;
        return ata_high_latch;
    }
}
```

Write behavior:

```c
void write_A3(uint8_t v) {
    if (ata_data_phase == LOW_NEXT) {
        ata_low_latch = v;
        ata_data_phase = HIGH_NEXT;
    } else {
        uint16_t w = (uint16_t)ata_low_latch | ((uint16_t)v << 8);
        ata_write_data_word(w);
        ata_data_phase = LOW_NEXT;
    }
}
```

The original documentation calls these “ODD” and “EVEN” data accesses: the first data access returns or latches the low byte, the second returns or writes the high byte. Any access to any other ATA register or to the DivIDE control register makes the next data-register access the low-byte access again; accesses outside DivIDE ports do not affect the flip-flop. After reset or power-on, the phase is undefined. ([Divide][2])

For reliable emulator behavior, initialize it to `LOW_NEXT`, but be aware accurate hardware does not guarantee that after reset.

### 4.2 Minimal ATA disk implementation

The DivIDE hardware only exposes ATA registers; the ATA device itself can be emulated as a normal PIO ATA disk or CompactFlash card.

For esxDOS/FATWare-style firmware compatibility, implement at least:

```text
#EC  IDENTIFY DEVICE
#20  READ SECTOR(S), LBA28
#30  WRITE SECTOR(S), LBA28
#EF  SET FEATURES, accept or harmlessly ignore common subfeatures
#E7  FLUSH CACHE, optional but safe to accept
```

Useful status bits:

```text
bit 7  BSY
bit 6  DRDY
bit 5  DF
bit 4  DSC
bit 3  DRQ
bit 0  ERR
```

Useful error bit:

```text
error bit 2  ABRT, aborted/unsupported command
```

A simple model:

```c
#define ATA_ST_BSY  0x80
#define ATA_ST_DRDY 0x40
#define ATA_ST_DSC  0x10
#define ATA_ST_DRQ  0x08
#define ATA_ST_ERR  0x01
#define ATA_ER_ABRT 0x04
```

Initial status:

```c
status = ATA_ST_DRDY | ATA_ST_DSC;
error  = 0;
```

On command write to `#BF`:

```c
void ata_command(uint8_t cmd) {
    clear_drq_and_err();

    switch (cmd) {
    case 0xec:  // IDENTIFY DEVICE
        prepare_identify_512_bytes();
        data_index = 0;
        status = ATA_ST_DRDY | ATA_ST_DSC | ATA_ST_DRQ;
        break;

    case 0x20:  // READ SECTOR(S)
        prepare_read_sector_buffer(current_lba28(), sector_count_or_256());
        data_index = 0;
        status = ATA_ST_DRDY | ATA_ST_DSC | ATA_ST_DRQ;
        break;

    case 0x30:  // WRITE SECTOR(S)
        prepare_write_receive_buffer(current_lba28(), sector_count_or_256());
        data_index = 0;
        status = ATA_ST_DRDY | ATA_ST_DSC | ATA_ST_DRQ;
        break;

    case 0xef:  // SET FEATURES
    case 0xe7:  // FLUSH CACHE
        status = ATA_ST_DRDY | ATA_ST_DSC;
        break;

    default:
        error = ATA_ER_ABRT;
        status = ATA_ST_DRDY | ATA_ST_DSC | ATA_ST_ERR;
        break;
    }
}
```

LBA28 address:

```c
uint32_t current_lba28(void) {
    return ((drive_head & 0x0f) << 24) |
           ((uint32_t)cyl_high << 16) |
           ((uint32_t)cyl_low  << 8)  |
           sector_number;
}
```

ATA sector count value `0` means 256 sectors.

Data order for both IDENTIFY and sector I/O is little-endian word order through the bridge:

```text
first IN #A3   byte 0 of word 0
second IN #A3  byte 1 of word 0
third IN #A3   byte 0 of word 1
...
```

After 256 words for a 512-byte sector, either load the next sector and keep `DRQ` set, or clear `DRQ` if the transfer is complete.

---

## 5. DivMMC SD/MMC SPI interface

DivMMC does **not** use the DivIDE ATA ports. It keeps the DivIDE-style memory mapper and replaces the storage interface with SD/MMC over a CPLD SPI engine. The esxDOS porting guidance describes DivMMC as “a DivIDE with SD/MMC instead of IDE ports” and gives `SPI_PORT equ $EB` and `OUT_PORT equ $E7` for chip select control. ([esxDOS BBS][5])

### 5.1 DivMMC port table

| Low port | Direction  | Purpose                              |
| -------: | ---------- | ------------------------------------ |
|    `#E3` | write      | mapper control: bank, MAPRAM, CONMEM |
|    `#E7` | write      | SD card chip-select outputs, `D1:D0` |
|    `#EB` | read/write | SPI byte transfer                    |

The DivMMC VHDL constants are `x"E3"` for the DivIDE control port, `x"E7"` for SD card control, and `x"EB"` for the SPI byte port. ([GitHub][3])

### 5.2 Port `#E7`: card select

`OUT (#E7),A` directly updates the two card chip-select outputs:

```text
D0 -> card 0 CS
D1 -> card 1 CS
```

The VHDL initializes both outputs to `1` and resets them to `1`, so the idle state is both deselected. ([GitHub][3]) SD/MMC chip select is active low, so:

```text
A bit pattern   Meaning
xxxx xx11       no card selected
xxxx xx10       card 0 selected
xxxx xx01       card 1 selected
xxxx xx00       both selected; real hardware bus contention/undefined, avoid
```

Reads from `#E7` are not defined on the original design. Return open bus or `0xFF`.

### 5.3 Port `#EB`: SPI byte transfer

An `IN` or `OUT` on low port `#EB` performs one 8-bit SPI transaction.

Emulator behavior:

```c
void out_EB(uint8_t v) {
    if (selected_card == NONE)
        return;

    sd_spi_transfer(v);      // MOSI = v, ignore returned MISO byte
}

uint8_t in_EB(void) {
    if (selected_card == NONE)
        return 0xff;

    return sd_spi_transfer(0xff);  // MOSI held high during reads
}
```

The DivMMC VHDL has a byte-transfer state machine for port `#EB`; on writes it samples the CPU data bus, shifts the byte to the SD card, samples MISO, and on reads drives the received byte back to the CPU. It shifts bit 7 first and appends `1` bits on MOSI as bytes are shifted out. ([GitHub][3]) The hardware SPI port transfers a byte with a single `IN` or `OUT` instruction, which is why DivMMC is much faster than bit-banged SD interfaces. ([Spectrum for Everyone][1])

For a CPU-instruction-level emulator, you do **not** need to model individual SPI clock edges. Model each `IN #EB` or `OUT #EB` as one complete full-duplex SPI byte.

If no card is selected, return `0xFF`; the SD MISO/DO line is normally pulled high when idle, and SPI read transfers send `0xFF` on MOSI. ([Elm Chan][6])

### 5.4 SD card model behind DivMMC

The DivMMC hardware exposes SPI; it does not implement FAT. esxDOS or other firmware implements the filesystem. Your emulator should expose a raw block image as an SD/MMC card.

A practical SD SPI command parser should handle:

```text
CMD0    GO_IDLE_STATE
CMD8    SEND_IF_COND, for SD v2 detection
CMD55   APP_CMD
ACMD41  APP_SEND_OP_COND
CMD58   READ_OCR
CMD16   SET_BLOCKLEN, usually 512
CMD17   READ_SINGLE_BLOCK
CMD18   READ_MULTIPLE_BLOCK, optional but useful
CMD12   STOP_TRANSMISSION, for CMD18
CMD24   WRITE_BLOCK
CMD25   WRITE_MULTIPLE_BLOCK, optional but useful
ACMD23  pre-erase count, optional
CMD9    READ_CSD, useful
CMD10   READ_CID, useful
CMD13   SEND_STATUS, useful
```

The standard SPI command frame is six bytes:

```text
byte 0: 0x40 | command_number
byte 1: argument bits 31..24
byte 2: argument bits 23..16
byte 3: argument bits 15..8
byte 4: argument bits 7..0
byte 5: CRC7/end bit
```

For typical CRC-disabled SPI operation, only `CMD0` and `CMD8` need valid CRC values during initialization:

```text
CMD0, arg 00000000h -> CRC byte 95h
CMD8, arg 000001AAh -> CRC byte 87h
```

After power-up, SD cards enter native mode; to enter SPI mode, firmware keeps CS low for `CMD0`, receives R1 idle response `01h`, then initializes with commands such as `CMD8`, `CMD55`/`ACMD41`, and `CMD58`. The SD SPI initialization and command set details are specified by the SD standards, and practical embedded references describe the same flow. ([SD Association][7])

For responses:

```text
R1  one byte, bit 7 clear when valid
R3  R1 + 32-bit OCR, for CMD58
R7  R1 + 32-bit echo/check data, for CMD8
```

For single block read `CMD17`:

```text
host sends CMD17
card returns R1 = 00h if accepted
card returns zero or more FFh delay bytes
card returns data token FEh
card returns 512 data bytes
card returns 2 CRC bytes
```

For single block write `CMD24`:

```text
host sends CMD24
card returns R1 = 00h if accepted
host sends data token FEh
host sends 512 data bytes
host sends 2 CRC bytes
card returns data response, usually 05h for accepted
card returns busy bytes 00h, then FFh when ready
```

The card must remain selected during command, response, and data transfer; block read/write transactions include a token, data block, and CRC, and read/write block size is effectively 512 bytes for the normal FAT-oriented flows. ([Elm Chan][6])

Addressing:

```text
SDSC / standard capacity: command argument is byte address
SDHC / SDXC: command argument is 512-byte block LBA
```

For easiest emulator compatibility, present an SDHC-style card and set the OCR CCS bit after `ACMD41`/`CMD58`; then `CMD17` and `CMD24` arguments are sector numbers. SDHC/SDXC use block addressing with fixed 512-byte blocks in SPI read/write commands. ([Elm Chan][6])

---

## 6. Reset, power-on, and NMI behavior

Recommended emulator state separation:

```c
void div_power_on(void) {
    conmem  = false;
    mapram  = false;
    automap = false;
    bank    = 0;

    // DivIDE
    ata_reset_device();
    ata_data_phase = LOW_NEXT;   // hardware says undefined; LOW_NEXT is practical

    // DivMMC
    card_cs[0] = 1;
    card_cs[1] = 1;
    spi_reset();
}

void div_reset_button(void) {
    /*
       Reset the Z80. Do not clear MAPRAM.
       Original docs say MAPRAM is cleared only by power-on.
    */
    z80_reset();

    /*
       DivMMC VHDL resets card CS and SPI state on reset,
       while mapper control is cleared by the separate poweron signal.
    */
    card_cs[0] = 1;
    card_cs[1] = 1;
    spi_reset();

    /*
       Whether you clear automap on reset is a board-accuracy choice.
       For original-style DivMMC VHDL, the automap latch is not reset by
       the reset input; for many emulators, clearing it on hard reset is
       acceptable if PC=0000 will immediately retrap anyway.
    */
}
```

Power-on clears `#E3` control bits on the original DivIDE model, while `MAPRAM` is explicitly described as reset only by power-on, not by reset. ([Divide][2]) In the DivMMC VHDL, the mapper state is cleared by a `poweron` input, while the SD card select and SPI transfer state are reset by the `reset` input. ([GitHub][3])

NMI handling is simple: assert Z80 `/NMI`; the CPU fetches from `0066h`; `0066h` is one of the DivIDE automap entry points. ([Divide][2])

---

## 7. Suggested combined emulator dispatch

### Memory fetch path

```c
uint8_t cpu_fetch(uint16_t pc) {
    if (device_is_divide_or_divmmc) {
        if (automap_enabled && (pc & 0xff00) == 0x3d00)
            automap = true;

        uint8_t op = div_read(pc);

        if (automap_enabled && is_entry_trap(pc))
            automap = true;

        if (pc >= 0x1ff8 && pc <= 0x1fff)
            automap = false;

        return op;
    }

    return spectrum_read(pc);
}
```

### Memory read/write path

```c
uint8_t mem_read(uint16_t a) {
    if (device_is_divide_or_divmmc)
        return div_read(a);
    return spectrum_read(a);
}

void mem_write(uint16_t a, uint8_t v) {
    if (device_is_divide_or_divmmc)
        div_write(a, v);
    else
        spectrum_write(a, v);
}
```

### I/O path: DivIDE

```c
uint8_t io_read_divide(uint16_t port) {
    uint8_t p = port & 0xff;

    if ((p & 0xe3) == 0xa3) {
        uint8_t r = (p >> 2) & 7;

        if (r != 0)
            ata_data_phase = LOW_NEXT;

        switch (r) {
        case 0: return read_A3();
        case 1: return ata_error;
        case 2: return ata_sector_count;
        case 3: return ata_lba0;
        case 4: return ata_lba1;
        case 5: return ata_lba2;
        case 6: return ata_drive_head;
        case 7: return ata_status;
        }
    }

    return floating_bus();
}

void io_write_divide(uint16_t port, uint8_t v) {
    uint8_t p = port & 0xff;

    if (p == 0xe3) {
        write_E3_divide(v);
        return;
    }

    if ((p & 0xe3) == 0xa3) {
        uint8_t r = (p >> 2) & 7;

        if (r != 0)
            ata_data_phase = LOW_NEXT;

        switch (r) {
        case 0: write_A3(v); break;
        case 1: ata_features = v; break;
        case 2: ata_sector_count = v; break;
        case 3: ata_lba0 = v; break;
        case 4: ata_lba1 = v; break;
        case 5: ata_lba2 = v; break;
        case 6: ata_drive_head = v; break;
        case 7: ata_command(v); break;
        }
    }
}
```

### I/O path: DivMMC

```c
uint8_t io_read_divmmc(uint16_t port) {
    uint8_t p = port & 0xff;

    if (p == 0xeb)
        return divmmc_spi_in();

    return floating_bus();
}

void io_write_divmmc(uint16_t port, uint8_t v) {
    uint8_t p = port & 0xff;

    switch (p) {
    case 0xe3:
        write_E3_divmmc(v);
        break;

    case 0xe7:
        card_cs[0] = v & 0x01;
        card_cs[1] = (v >> 1) & 0x01;
        break;

    case 0xeb:
        divmmc_spi_out(v);
        break;
    }
}
```

---

## 8. Variant caveats

The above is the baseline you want for most DivIDE and DivMMC emulation.

Important deviations:

* **Spectrum Next internal DivMMC**: port `#E3` is readable, extra automap entries are programmable through Next registers, and `MAPRAM` can be reset through a NextReg. Do not mix this into an original DivMMC emulator unless you are emulating the Next. ([wiki.specnext.dev][4])
* **DivIDE Plus**: adds extra control port/modes, more flash/RAM banking, and software reset behavior not present in original DivIDE.
* **DivSD, DivTIESUS, modern DivMMC EnJOY variants**: often add buttons, joystick, RTC, dual SD slots, write-protect behavior, or other features. The core mapper and `#E7/#EB` SPI behavior are usually still DivMMC-compatible, but extra ports are variant-specific.

[1]: https://spectrumforeveryone.com/features/history-esxdos-divmmc-divmmc-enjoy/ "https://spectrumforeveryone.com/features/history-esxdos-divmmc-divmmc-enjoy/"
[2]: https://divide.speccy.cz/files/pgm_model.txt "https://divide.speccy.cz/files/pgm_model.txt"
[3]: https://github.com/mprato/DivMMC/raw/refs/heads/master/divmmc/divmmc.vhd "https://github.com/mprato/DivMMC/raw/refs/heads/master/divmmc/divmmc.vhd"
[4]: https://wiki.specnext.dev/DIVMMC "https://wiki.specnext.dev/DIVMMC"
[5]: https://board.esxdos.org/viewtopic.php?id=40 "https://board.esxdos.org/viewtopic.php?id=40"
[6]: https://elm-chan.org/docs/mmc/mmc_e.html "https://elm-chan.org/docs/mmc/mmc_e.html"
[7]: https://www.sdcard.org/downloads/pls/ "https://www.sdcard.org/downloads/pls/"
