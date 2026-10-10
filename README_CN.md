# SmartFan 智能风扇控制器

SmartFan 是一个基于 ESP-IDF 的 ESP32-S3 智能风扇控制项目。项目集成了风扇 PWM 调速、Tach 转速反馈、旋转编码器输入、OLED 菜单交互、ARGB 灯效、系统资源监控和 UART 串口调试功能。

## 功能特性

- **风扇 PWM 控制**
  - 使用 ESP-IDF LEDC 输出 25 kHz PWM
  - 支持 0-100% 风扇转速控制
  - 预留手动、自动、全速三种模式

- **风扇转速反馈**
  - 通过 GPIO 中断统计 Tach 脉冲
  - 按两脉冲一圈计算 RPM
  - 支持在 OLED 菜单中实时显示风扇转速

- **旋转编码器输入**
  - A/B 相通过 GPIO 中断解码
  - 支持累计计数和增量读取
  - 支持按键短按、长按识别和消抖

- **OLED 菜单系统**
  - 树形菜单结构
  - 通过编码器旋转和按键完成交互
  - 支持菜单滚动显示
  - 包含风扇、编码器、系统信息和灯效相关页面

- **ARGB 灯效**
  - 支持 8 颗 WS2812/SK6812 风格 ARGB 灯珠
  - 支持静态、彩虹、呼吸、闪烁、追逐、环绕、随机闪烁、自定义呼吸等模式
  - 支持运行时切换灯效模式

- **系统监控**
  - 运行时间统计
  - Heap 和内部内存信息
  - FreeRTOS 任务列表和 CPU 使用率统计

- **UART 串口调试**
  - 支持通过串口模拟编码器旋转、按键事件和灯效切换
  - 提供 Python/Tkinter 编写的串口调试小工具

## 硬件目标

- 主控：**ESP32-S3**
- 开发框架：**ESP-IDF**
- 显示屏：I2C OLED，SSD1306 风格驱动
- 输入设备：带按键的旋转编码器
- 风扇：支持 PWM 控制和 Tach 反馈的 4 线风扇
- 灯效：8 颗 WS2812/SK6812 风格单总线 ARGB 灯珠

## 引脚分配

| 功能 | GPIO |
| --- | --- |
| 编码器 A 相 | GPIO0 |
| 编码器 B 相 | GPIO1 |
| 编码器按键 | GPIO2 |
| 风扇 Tach 转速反馈 | GPIO3 |
| ARGB 数据线 | GPIO4 |
| OLED 预留按键 | GPIO5 |
| 风扇 PWM | GPIO6 |
| I2C SDA | GPIO7 |
| I2C SCL | GPIO9 |

引脚定义位于：

```text
main/.Include/gpio.h
```

## 项目结构

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
|       +-- 项目头文件
+-- managed_components/
    +-- espressif__led_strip/
```

## 核心模块

### 风扇驱动

风扇驱动负责配置 LEDC PWM 输出，并通过 Tach 信号统计风扇转速。

- 源文件：`main/ModuleDriver/fan.c`
- 头文件：`main/.Include/fan.h`
- PWM 频率：`25000 Hz`
- PWM 分辨率：`10-bit`
- Tach 中断触发方式：下降沿

### 编码器驱动

编码器驱动通过 A/B 两相 GPIO 中断和状态转移表判断旋转方向。

- 源文件：`main/ModuleDriver/encoder.c`
- 头文件：`main/.Include/encoder.h`
- 按键处理：`main/Tasks/event_handler.c`

按键逻辑：

- 消抖时间：`30 ms`
- 长按阈值：`300 ms`
- 支持状态：未按下、短按、长按

### OLED 菜单

OLED UI 使用 `oled_item_t` 结构组织为树形菜单。

- 显示任务：`main/Tasks/oled_display_task.c`
- 菜单定义：`main/oled/oled_menu.c`
- OLED 驱动：`main/ModuleDriver/oled.c`

基本交互方式：

| 输入 | 行为 |
| --- | --- |
| 旋转编码器 | 移动光标或调整数值 |
| 短按 | 进入菜单项或确认操作 |
| 长按 | 返回上级或触发二级操作 |

当前菜单大致包含：

- 风扇信息
- 编码器信息
- ESP32-S3 系统信息
- 电脑信息预留页面
- 设置页面
- 快速设置预留页面

### ARGB 灯效驱动

灯效驱动使用 GPIO 软件 bit-bang 的方式生成 WS2812/SK6812 风格时序。

- 源文件：`main/ModuleDriver/light.c`
- 头文件：`main/.Include/light.h`
- 灯珠数量：`8`

支持灯效：

- 关闭
- 静态
- 彩虹
- 呼吸
- 随机闪烁
- 闪烁
- 自定义呼吸
- 追逐
- 环绕

### 系统监控

系统监控模块用于读取运行时状态和 FreeRTOS 任务统计信息。

- 源文件：`main/ModuleDriver/system_monitor.c`
- 头文件：`main/.Include/system_monitor.h`

已启用的 FreeRTOS 统计配置位于 `sdkconfig.defaults`：

```text
CONFIG_FREERTOS_USE_TRACE_FACILITY=y
CONFIG_FREERTOS_USE_STATS_FORMATTING_FUNCTIONS=y
```

## 构建环境

需要准备：

- 支持 ESP32-S3 的 ESP-IDF 环境
- ESP-IDF 所需 Python 环境
- 可连接开发板的 USB 串口

当前依赖锁定信息中显示：

```text
target: esp32s3
idf: 6.0.1
```

## 编译和烧录

打开 ESP-IDF 终端，在项目根目录执行：

```bash
idf.py set-target esp32s3
idf.py build
idf.py flash monitor
```

如果串口没有被自动识别，可以手动指定：

```bash
idf.py -p COMx flash monitor
```

其中 `COMx` 替换为你的实际串口，例如 Windows 下的 `COM3`。

## UART 调试命令

固件内置 UART0 调试任务，默认波特率为 `115200`。

| 命令 | 作用 |
| --- | --- |
| `1` | 编码器计数 -1 |
| `2` | 编码器计数 +1 |
| `3` | 模拟长按 |
| `4` | 模拟短按 |
| `5` | 切换灯效模式 |

也可以使用项目中的 Python/Tkinter 串口调试工具：

```bash
python board_test.py
```

如果缺少 `pyserial`，先安装：

```bash
pip install pyserial
```

## 当前完成度

已实现：

- 风扇 PWM 输出
- 风扇 RPM 测量
- 旋转编码器解码
- 编码器按键短按和长按识别
- OLED 菜单框架
- 风扇、编码器、系统和灯效相关页面
- ARGB 灯效
- FreeRTOS 和系统资源统计显示
- UART 调试控制

部分实现或计划实现：

- 电脑 CPU/GPU/RAM/网络信息页面
- 快速设置页面
- PWM 设置页面
- 编码器设置页面
- 按键行为配置
- 蓝牙通信模块
- 配置持久化存储

## 注意事项

- ARGB 灯效当前使用软件 bit-bang 实现，而不是 ESP-IDF RMT LED Strip 驱动。实现方式更直接，但时序会更依赖 CPU 主频、编译优化和中断延迟。
- 风扇 RPM 计算默认假设 Tach 信号为两脉冲一圈。如果你的风扇规格不同，需要调整 RPM 计算公式。
- 部分 OLED 菜单页面仍是预留或 TODO，用于后续扩展。
- 上传 GitHub 前建议补充项目图片、硬件接线图和实际运行效果图，这会让项目更容易理解。

## 许可证

当前项目尚未指定许可证。如果希望其他人可以复用、修改或分发本项目，建议在发布前添加明确的开源许可证。
