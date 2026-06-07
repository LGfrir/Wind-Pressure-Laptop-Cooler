# SmartFan

SmartFan is an ESP-IDF based ESP32-S3 fan controller. The firmware currently
uses BLE as the PC communication channel and keeps the OLED as a compact status
display instead of a local menu editor.

## Current Target

- MCU: ESP32-S3
- ESP-IDF: 6.0.1
- BLE stack: NimBLE
- OLED role: small-font English status renderer
- PC role: selects OLED mode and streams mode-specific data over BLE

## Build

Load the ESP-IDF environment, then build:

```powershell
idfenv
idf.py build
```

Flash with your actual serial port:

```powershell
idf.py -p COMx flash monitor
```

Two helper scripts are included:

```powershell
idfenv
rmbd
```

- `idfenv` loads `E:\ESP-IDF\esp-idf-v6.0.1\export.ps1` and switches back to
  this project.
- `rmbd` removes the local `build` directory.

## BLE Layout

Device name:

```text
SmartFan-S3
```

Service UUID:

```text
a1f00000-2c3d-4e5f-8a9b-001122334455
```

PC -> ESP characteristic, `Write` / `Write Without Response`:

```text
a1f00001-2c3d-4e5f-8a9b-001122334455
```

ESP -> PC characteristic, `Read` / `Notify`:

```text
a1f00002-2c3d-4e5f-8a9b-001122334455
```

## PC Write Protocol

The BLE transport code lives in:

```text
main/ModuleDriver/blue_tooth.c
```

The packet parser and packet builder live in:

```text
main/ModuleDriver/ble_protocol.c
main/.Include/ble_protocol.h
```

### Set OLED Mode

```text
byte 0  command = 0x01
byte 1  oled_render_mode_t
```

### PC Status Frame

```text
byte 0      command = 0x02
byte 1      oled_render_mode_t
byte 2      cpu_usage
byte 3      cpu_power_w
byte 4      gpu_usage
byte 5      gpu_power_w
byte 6      ram_usage
byte 7-8    ram_used_mb, little-endian
byte 9-10   ram_total_mb, little-endian
byte 11-12  net_rx_kbps, little-endian
byte 13-14  net_tx_kbps, little-endian
```

The old 13-byte PC status payload is still accepted for quick compatibility
testing; it updates PC status without changing the current OLED mode.

## ESP Notify Protocol

The ESP status notify/read payload is:

```text
byte 0      oled_render_mode_t
byte 1      fan_speed_percent
byte 2      fan_mode, 0=manual, 1=auto, 2=full
byte 3      fan_is_on
byte 4-7    fan_rpm, little-endian uint32
```

When the PC subscribes to the ESP status characteristic, the firmware sends this
packet every 500 ms.

## OLED Rendering

`main/oled/oled_menu.c` no longer contains a menu tree. It exposes direct render
entry points:

```c
void oled_set_render_mode(oled_render_mode_t mode);
void oled_render_current(void);
void oled_render_summary(void);
void oled_render_fan(void);
void oled_render_pc_cpu(void);
void oled_render_pc_gpu(void);
void oled_render_pc_ram(void);
void oled_render_pc_net(void);
void oled_render_esp_system(void);
void oled_render_esp_tasks(void);
void oled_render_light(void);
```

The OLED task only calls `oled_render_current()` periodically. Mode selection is
owned by the PC/BLE protocol.

## Backup

Before the OLED/BLE refactor, a local backup was created at:

```text
G:\ESP32\SmartFan\backups\SmartFan_oled_ble_refactor_20260607_230712
```

Backups are intentionally ignored by git.
