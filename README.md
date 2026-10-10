# SmartFan

SmartFan is an ESP-IDF based smart fan controller for ESP32-S3. It integrates PWM fan speed control, tachometer RPM feedback, rotary encoder input, OLED menu interaction, ARGB lighting effects, system monitoring, and UART-based debugging.

## Features

- **PWM fan control**
  - 25 kHz PWM output through ESP-IDF LEDC
  - 0-100% speed control
  - Manual, auto, and full-speed mode definitions

- **Fan RPM feedback**
  - Tachometer pulse counting through GPIO interrupt
  - RPM calculation based on two pulses per revolution
  - Real-time RPM display support in OLED menu

- **Rotary encoder input**
  - Quadrature A/B phase decoding by GPIO interrupt
  - Incremental and absolute count tracking
  - Short press and long press detection with debounce

- **OLED menu system**
  - Tree-structured menu pages
  - Encoder-based navigation
  - Scrollable menu list
  - Detail pages for fan, encoder, system, and light information

- **ARGB lighting**
  - 8-pixel WS2812/SK6812-style ARGB output
  - Static, rainbow, breath, blink, chase, round, random flash, and custom breath modes
  - Runtime mode switching

- **System monitoring**
  - Runtime statistics
  - Heap and internal memory information
  - FreeRTOS task list and CPU usage statistics

- **UART debug control**
  - Serial command mapping for encoder movement, button events, and light mode switching
  - Optional Python/Tkinter GUI debugger

## Hardware Target

- MCU: **ESP32-S3**
- Framework: **ESP-IDF**
- Display: I2C OLED, SSD1306-style driver
- Input: Rotary encoder with push button
- Fan: PWM-controlled 4-wire fan with tachometer feedback
- Lighting: 8 ARGB LEDs, WS2812/SK6812-style one-wire protocol

## Pin Mapping

| Function | GPIO |
| --- | --- |
| Encoder A | GPIO0 |
| Encoder B | GPIO1 |
| Encoder Button | GPIO2 |
| Fan Tachometer | GPIO3 |
| ARGB Data | GPIO4 |
| OLED Button 1 / Reserved | GPIO5 |
| Fan PWM | GPIO6 |
| I2C SDA | GPIO7 |
| I2C SCL | GPIO9 |

Pin definitions are located in:

```text
main/.Include/gpio.h
```

## Project Structure

```text
SmartFan/
+-- CMakeLists.txt
+-- dependencies.lock
+-- sdkconfig.defaults
+-- board_test.py
+-- main/
|   +-- main.c
|   +-- CMakeLists.txt
|   +-- Basic/
|   |   +-- i2c_bus.c
|   +-- ModuleDriver/
|   |   +-- encoder.c
|   |   +-- fan.c
|   |   +-- light.c
|   |   +-- oled.c
|   |   +-- oled_plot.c
|   |   +-- system_monitor.c
|   +-- Tasks/
|   |   +-- event_handler.c
|   |   +-- oled_display_task.c
|   +-- oled/
|   |   +-- oled_adjuster.c
|   |   +-- oled_menu.c
|   +-- .Include/
|       +-- project headers
+-- managed_components/
    +-- espressif__led_strip/
```

## Main Modules

### Fan Driver

The fan driver configures LEDC PWM output and tachometer pulse measurement.

- Source: `main/ModuleDriver/fan.c`
- Header: `main/.Include/fan.h`
- PWM frequency: `25000 Hz`
- PWM resolution: `10-bit`
- Tachometer interrupt: negative edge

### Encoder Driver

The encoder driver uses GPIO interrupts on both A/B phases and a transition table to decode direction.

- Source: `main/ModuleDriver/encoder.c`
- Header: `main/.Include/encoder.h`
- Button handling: `main/Tasks/event_handler.c`

### OLED UI

The OLED UI is implemented as a tree of `oled_item_t` menu nodes.

- Display task: `main/Tasks/oled_display_task.c`
- Menu definitions: `main/oled/oled_menu.c`
- OLED driver: `main/ModuleDriver/oled.c`

Basic navigation:

| Input | Behavior |
| --- | --- |
| Rotate encoder | Move cursor / adjust value |
| Short press | Enter item / confirm operation |
| Long press | Back / secondary operation |

### ARGB Light Driver

The light driver implements WS2812/SK6812-style timing by direct GPIO bit-banging.

- Source: `main/ModuleDriver/light.c`
- Header: `main/.Include/light.h`
- Pixel count: `8`

Supported modes:

- Off
- Static
- Rainbow
- Breath
- Random flash
- Blink
- Custom breath
- Chase
- Round

### System Monitor

The system monitor provides runtime and FreeRTOS task statistics.

- Source: `main/ModuleDriver/system_monitor.c`
- Header: `main/.Include/system_monitor.h`

The following options are enabled in `sdkconfig.defaults`:

```text
CONFIG_FREERTOS_USE_TRACE_FACILITY=y
CONFIG_FREERTOS_USE_STATS_FORMATTING_FUNCTIONS=y
```

## Build Requirements

- ESP-IDF with ESP32-S3 support
- Python environment required by ESP-IDF
- USB serial connection to the target board

This project was generated and tested around the ESP-IDF project layout. The dependency lock currently targets:

```text
target: esp32s3
idf: 6.0.0
```

## Build and Flash

Open an ESP-IDF terminal, then run:

```bash
idf.py set-target esp32s3
idf.py build
idf.py flash monitor
```

If your serial port is not detected automatically, specify it explicitly:

```bash
idf.py -p COMx flash monitor
```

Replace `COMx` with your actual serial port, for example `COM3` on Windows.

## UART Debug Commands

The firmware includes a UART debug task on `UART0` at `115200 baud`.

| Command | Action |
| --- | --- |
| `1` | Encoder count -1 |
| `2` | Encoder count +1 |
| `3` | Simulate long press |
| `4` | Simulate short press |
| `5` | Switch light mode |

There is also a simple Python/Tkinter serial debugger:

```bash
python board_test.py
```

Install `pyserial` first if needed:

```bash
pip install pyserial
```

## Current Status

Implemented:

- Fan PWM output
- Fan RPM measurement
- Rotary encoder decoding
- Encoder button short/long press detection
- OLED menu framework
- Fan, encoder, system, and light menu pages
- ARGB light effects
- FreeRTOS/system statistics display
- UART debug control

Partially implemented or planned:

- Computer CPU/GPU/RAM/network information pages
- Quick settings page
- PWM settings page
- Encoder settings page
- Button behavior configuration
- Bluetooth communication module
- Persistent configuration storage

## Notes

- The ARGB driver uses software bit-banging instead of the ESP-IDF RMT LED strip driver. This keeps the implementation direct, but timing can be more sensitive to CPU frequency, compiler optimization, and interrupt latency.
- The fan tachometer RPM calculation assumes two tach pulses per revolution. Adjust the formula if your fan uses a different pulse count.
- Some menu pages are intentionally reserved as TODO items for future extension.

## License

No license has been specified yet. Add a license before publishing if you want others to reuse, modify, or distribute this project.
