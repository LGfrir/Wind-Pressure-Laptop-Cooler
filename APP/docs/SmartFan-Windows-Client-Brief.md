# SmartFan Windows 控制端 —— 开发任务书

> **交付对象：** 一个 AI 编程助手（ChatGPT / Claude / Copilot 等）
> **项目：** 为一块 ESP32-S3 笔记本散热器开发 Windows 桌面控制端
> **语言 / 框架：** C# + WPF + Windows 原生 BLE API（WinRT）
> **文档版本：** 2026-10-14
> **固件版本：** 已实现 4 个 GATT 服务、27 个特征（见第三节）

---

## 第零节 · 对 AI 的工作方式要求（请先读这一节）

这份任务书的要求比一般"帮我写个 App"要高。请严格遵守：

### 0.1 必须分阶段交付，每阶段都要能独立验证

**不要一次性生成整个项目。** 按下面的顺序，一个阶段做完、确认能跑通、再进下一个：

| 阶段 | 产出 | 验证方式 |
|---|---|---|
| **P0** | **控制台"接口自检工具"** | 逐个读写每个特征，打印结果，人工比对第四节 |
| **P1** | BLE 通信层（可复用类库） | 用 P0 的工具验证；再写单元测试 |
| **P2** | 最小可用 UI（连接 + 看数据 + 控风扇） | 手动点一遍 |
| **P3** | 完整 UI（配置编辑器、曲线、主题、壁纸） | 手动点一遍 |
| **P4** | 高级设置与打磨 | 见第九节 |

**P0 是关键。** 在没有证明"27 个特征全部能正确读写"之前，**不要开始写 UI**。历史上这个项目就是因为跳过验证、直接在 UI 里调 BLE，导致接口问题被 UI 的复杂度掩盖，最后不得不整体重构。

### 0.2 不许猜

- **UUID、数据格式、字节序、取值范围，全部以本文档为准。** 不确定的**必须问**，不要编造一个"看起来合理"的值。
- 本文档没有覆盖的细节（比如某个特征的语义），**列出来问，不要自己假设**。
- **不要"为了让它能跑"而放宽校验。** 如果读到的东西和本文档不符，那是一个**需要报告的 bug**，不是需要绕过的问题。

### 0.3 每个接口都要有"读回验证"

写一个特征之后，**立刻读回来比对**。例如：

```csharp
await WriteAsync(FanMode, new byte[]{ 2, 0, 0, 0 });   // 写 FULL_SPEED
var back = await ReadAsync(FanMode);
// back 必须是 { 2, 0, 0, 0 }，否则报告不一致
```

**这是"保证连接能够成功"的唯一可靠手段。** 任务书第十一节给了一份逐接口的验收清单，请照着填。

### 0.4 读者是从 Java 转过来的 C# 新手

项目作者是 Java 背景，正在学 C#。请：

- 解释 C# 特有的写法时**顺带说一下 Java 里的对应概念**（例如 `async/await` ↔ `CompletableFuture`）
- 不要用过于"炫技"的 C# 语法（大量 LINQ 链、`record`、模式匹配堆叠），除非确实更清晰
- 关键代码写注释，说明**为什么**这么写，而不只是"这行在做什么"

### 0.5 代码质量底线

- **必须能编译通过**（`dotnet build` 零错误）。给了代码就要保证能编译。
- 启用 `<Nullable>enable</Nullable>`，**不要用 `!` 到处压制可空警告**。
- **所有 BLE 调用都要有异常处理和超时**。BLE 是最容易卡死的地方。
- **不要在 UI 线程上做阻塞等待。**
- 不要硬编码 UUID 散落在各处 —— 集中到一个静态类里。

### 0.6 允许参考旧项目，但 BLE 部分必须重写

`G:\Csharp\SmartFan\SmartFan` 是一个做了一半的旧版本，**UI、主题、设置存储、硬件监控可以大量参考甚至直接搬**，但它的 BLE 层（`Services\BleSmartFanClient.cs`）**已经过时，不能沿用** —— 原因见第十节。

---

## 第一节 · 项目背景

### 1.1 这是什么

一个**笔记本散热器**（"风压式散热底座"）：

- 底座里有一个**涡轮风扇**，由 ESP32-S3 通过 PWM 控制转速，并有转速反馈（RPM）
- 底座上有一圈 **ARGB 灯**（8 颗 WS2812），有多种灯效
- 底座上有一块 **OLED 小屏**，显示风扇转速、以及**笔记本电脑自身的状态**（CPU/GPU 温度、占用率等）
- 底座上有一个**旋转编码器**，可以直接在设备上操作菜单

### 1.2 Windows 端要做什么

通过 **BLE** 与 ESP32 通信，实现：

| 方向 | 内容 |
|---|---|
| **PC → ESP32** | 把笔记本的实时状态（CPU/GPU 温度、占用率、内存、笔记本内置风扇转速）推给 ESP32，供 OLED 显示 |
| **PC → ESP32** | 控制风扇（模式 / PWM / 目标转速） |
| **PC → ESP32** | 控制灯光（模式、亮度、刷新周期） |
| **PC → ESP32** | 编辑每一种灯效的详细参数（颜色、周期…） |
| **ESP32 → PC** | 实时推送风扇转速（NOTIFY） |
| **PC → ESP32** | 所有配置写进 ESP32 的 flash，掉电不丢 |

### 1.3 硬件信息

| 项 | 值 |
|---|---|
| 芯片 | ESP32-S3 |
| BLE 设备名 | `连上尹浩荣jj断两根`（**26 字节，注意是中文；请勿假设它是稳定的标识符**） |
| 已知 MAC（你的这台） | `84:C7:BB:56:6A:16`（**会变，不能当作唯一标识**） |
| 广播里的服务 UUID | **只有 `fan_svc`**（`00000000-0000-0000-0063-76735f6e6166`） |
| 实际最大连接数 | 3 |
| 协商 MTU | 通常 256（**每台电脑/每次连接都可能不同**） |
| 配对要求 | **理论上不需要**（见第三节 3.6） |

---

## 第二节 · 关于 BLE，你必须知道的三件事

这三条是这个项目最容易踩的坑，请在写代码前先理解。

### 2.1 设备一旦被连上，就**停止广播**

BLE 从机（ESP32）的传统广播**只有一个实例**，被任何设备连上后**立即停止广播**，断开后才恢复。

**后果：**

- 手机 App 连着的时候，**PC 扫不到设备**
- PC 连着的时候，手机也扫不到
- **所以"扫描 → 连接"这个流程不能是唯一的连接方式**

**必须实现的方案（用户明确要求）：**

```
第一次：扫描 → 用户从列表里选一台 → 连接 → 记住它的 Id/地址
以后：  直接从"最近使用的设备"列表里连（不扫描）
```

Windows 上可以**不扫描直接连**（用 `BluetoothLEDevice.FromBluetoothAddressAsync` 或保存 `DeviceInformation.Id`）。**"高级设置"里要能手动填地址**，见 9.2。

### 2.2 一次操作最多 `MTU − 3` 字节

- ATT 明文负载上限 = **MTU − 3**（1 字节 opcode + 2 字节 handle）
- 本项目协商到 MTU=256 → **一次最多 253 字节**
- **MTU 是每连接协商的，不要写死。** 用 `GattSession.MaxPduSize` 或从 `CharacteristicValueChanged` 的实际数据推断

**本项目最大的载荷**是 `cfg_lt_custbreath`（1 + 5×9 = **46 字节**），远小于 253，**暂时不需要分片**。

### 2.3 NOTIFY 不保证送达

`fan_rpm` 用 NOTIFY 推送转速。NOTIFY **没有确认机制**，丢包不会被告知。

- 对"每秒推一次转速"这种**可丢的实时数据**，NOTIFY 是合适的
- **但如果以后传的是"命令"或"配置文件"，必须用 WRITE（有应答）**

---

## 第三节 · 完整接口契约（最重要的一节）

> **字节序：全部小端（Little-Endian）。**
> ESP32 本身是小端，固件直接按字段 `memcpy`，所以客户端按小端组包即可。
> C# 里用 `BitConverter` 时注意：`BitConverter.IsLittleEndian` 在 x86/x64 上是 `true`，可以直接用；但**建议显式用 `BinaryPrimitives.WriteUInt16LittleEndian` 之类**，避免以后跑在 ARM 上出问题。

### 3.1 服务一：`fan_svc` —— 风扇控制

**服务 UUID：`00000000-0000-0000-0063-76735f6e6166`**（ASCII 反序 = `fan_svc`）

| # | 特征名 | 特征 UUID | 权限 | 载荷格式 | 语义 |
|---|---|---|---|---|---|
| 1 | `fan_mode` | `00000000-0000-0000-6564-6f6d5f6e6166` | READ / WRITE | `[i32]` **4 字节** | `0`=手动 / `1`=自动 / `2`=全速 |
| 2 | `fan_pwm` | `00000000-0000-0000-006d-77705f6e6166` | READ / WRITE | `[u8]` **1 字节** | 风扇百分比 `0..100` |
| 3 | `fan_rpm` | `00000000-0000-0000-006d-70725f6e6166` | **NOTIFY** / WRITE<br>（⚠️**没有 READ**） | `[u32]` **4 字节** | **读/通知 = 实测转速**<br>**写 = 设置目标转速** |

#### ⚠️ 三个必须注意的点

**(a) `fan_rpm` 没有 READ 权限。** 你不能"主动读一次转速"，**只能订阅 NOTIFY**。
订阅后 ESP32 **每秒推一次**。

**(b) `fan_rpm` 的读写是两个不同的概念。**
- 你**收到**的 4 字节 = 风扇**实测**转速
- 你**写入**的 4 字节 = 风扇**目标**转速（自动模式下用）

这不是一个干净的接口设计，但**固件现在就是这样**。请按此实现，并**在代码里写清楚注释**，避免以后自己搞混。

**(c) `fan_pwm` 的读取恒返回 `0`。**
固件当前的读分支是 `uint8_t value = 0;` 写死的（这是一个**已知的固件 bug**，见第十三节）。
**不要依赖读回 `fan_pwm` 来确认写入成功** —— 请改用 `fan_rpm` 的通知来间接判断风扇是否转动。

### 3.2 服务二：`light_svc` —— 灯光总控

**服务 UUID：`00000000-0000-0063-7673-5f746867696c`**（ASCII 反序 = `light_svc`）

| # | 特征名 | 特征 UUID | 权限 | 载荷 | 语义 |
|---|---|---|---|---|---|
| 1 | `light_mode` | `00000000-0000-6564-6f6d-5f746867696c` | READ / WRITE | `[i32]` **4 字节** | 灯效模式，`0..8`，见 3.4 |
| 2 | `light_update_per` | `7265705f-6574-6164-7075-5f746867696c` | READ / WRITE | `[u8]` **1 字节** | 刷新周期（ms），**只接受 `10 / 20 / 50 / 100 / 200`** |
| 3 | `light_brightness` | `7373656e-7468-6769-7262-5f746867696c` | READ / WRITE | `[u8]` **1 字节** | 亮度 `0..255` |

#### 注意

**(a) `light_update_per` 只接受 5 个值。** 传别的值固件会返回错误（`BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN`）。**UI 上请用下拉框/滑块（分档），不要用任意数字输入框。**

**(b) 这个值不落盘。** 它是时序参数不是用户配置，重启后回到 `10`。**UI 上不要把它做成"会被记住"的设置。**

**(c) 模式值 `light_mode` 会被持久化**，重启后保留。

### 3.3 服务三：`config_svc` —— 各灯效的详细配置

**服务 UUID：`00000000-0000-0000-0063-76735f676663`**（ASCII 反序 = `cfg_svc`）

**8 个特征，每个对应一种灯效。特征的 `arg` 就是灯效枚举值。**

| arg | 灯效 | 特征 UUID | 载荷格式 | 字节数 |
|---|---|---|---|---|
| 1 | `STATIC` | `00000063-6974-6174-735f-746c5f676663` | `[r][g][b]` | **3** |
| 2 | `RAINBOW` | `0000776f-626e-6961-725f-746c5f676663` | `[switch_ms u16]` | **2** |
| 3 | `BREATH` | `00000068-7461-6572-625f-746c5f676663` | `[switch_ms u16][r][g][b]` | **5** |
| 4 | `RANDOM_FLASH` | `6873616c-6664-6e61-725f-746c5f676663` | `[mode u8]` | **1** |
| 5 | `BLINK` | `00000000-6b6e-696c-625f-746c5f676663` | `[size=2][r g b]×2[period u16]` | **9** |
| 6 | `CUSTOM_BREATH` | `74616572-6274-7375-635f-746c5f676663` | `[size][r g b]×N[fades_ms u16]×N` | **1 + 5N** |
| 7 | `CHASE` | `00000000-6573-6168-635f-746c5f676663` | `[size][r g b]×N` | **1 + 3N** |
| 8 | `ROUND` | `00000000-646e-756f-725f-746c5f676663` | 见下方 | **22** |

**全部 8 个都是 `READ / WRITE`。**

#### 格式细节

**(a) 颜色固定 3 字节，顺序 `r, g, b`**（不是 RGB565、不是 RGBA）。

**(b) `size` 范围 `1..9`。** 固件里 `LIGHT_CFG_MAX_COLORS = 9`。
**超出范围固件会拒绝**（返回 `BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN`）。

**(c) `BLINK` 的 size 固定是 `2`。** 固件里写死了 `GATT_BLINK_COLOR_COUNT = 2`。
**你必须发 `size = 2`，且正好 2 组颜色**，否则整包长度不符被拒。

**(d) `CUSTOM_BREATH` 的排列是"先所有颜色，再所有时长"**，不是交替：

```
[size]
[r0][g0][b0] [r1][g1][b1] ... [r(N-1)][g(N-1)][b(N-1)]
[fades0 u16] [fades1 u16] ... [fades(N-1) u16]
```

**(e) `ROUND` 的 22 字节布局：**

| 偏移 | 类型 | 字段 |
|---|---|---|
| 0 | `[r][g][b]` | `color_main` |
| 3 | `[r][g][b]` | `color_sub` |
| 6 | `float` (4B) | `range` |
| 10 | `float` (4B) | `range_main` |
| 14 | `int32` (4B) | `wait_ms` |
| 18 | `float` (4B) | `center_speed` |

**(f) 长度必须**精确**匹配。** 固件的收包函数是这样写的：

```c
if (OS_MBUF_PKTLEN(ctxt->om) != expect) {
    return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;   /* 多一个字节也拒 */
}
```

**不要"多发几个字节试试"。**

**(g) 写入成功后固件会立即存进 flash 并生效。**
所以**不要高频写**这些特征 —— flash 有擦写寿命（约 10 万次）。
**UI 上请用"确认/应用"按钮，不要在滑块拖动过程中连续写。**

### 3.4 灯效模式枚举（`light_mode` 的取值）

| 值 | 名称 | 对应 `config_svc` 特征 | 说明 |
|---|---|---|---|
| 0 | `OFF` | **无** | 关灯。没有对应的配置特征 |
| 1 | `STATIC` | arg=1 | 单色常亮 |
| 2 | `RAINBOW` | arg=2 | 彩虹渐变 |
| 3 | `BREATH` | arg=3 | 单色呼吸 |
| 4 | `RANDOM_FLASH` | arg=4 | 随机闪烁 |
| 5 | `BLINK` | arg=5 | 双色闪烁 |
| 6 | `CUSTOM_BREATH` | arg=6 | 自定义多色呼吸 |
| 7 | `CHASE` | arg=7 | 流光 |
| 8 | `ROUND` | arg=8 | 环绕扫描 |

> **注意：`light_mode` 的值和 `config_svc` 的 `arg` 完全对应**（1↔1 … 8↔8）。
> 只有 `OFF=0` 没有配置特征。这个巧合是固件故意设计的，可以简化客户端代码。

### 3.5 服务四：`laptop_svc` —— 笔记本状态上报（**PC → ESP32**）

**服务 UUID：`00000000-0000-6376-735f-706f7470616c`**（ASCII 反序 = `laptop_svc`）

**这是本项目里 PC 主动"推数据"的通道。** 13 个特征全部 `READ / WRITE`。

**载荷格式（小端）：**

- **标量特征：`[double]` = 8 字节**
- **`FAN_USED` / `FAN_SPEED`：`[double]×5` = 40 字节**

| arg | 特征名 | 特征 UUID | 字节数 | 含义 |
|---|---|---|---|---|
| 0 | `laptop_cpu_usage` | `65676173-755f-7570-635f-706f7470616c` | 8 | CPU 占用率 % |
| 1 | `laptop_cpu_temp` | `00706d65-745f-7570-635f-706f7470616c` | 8 | CPU 温度 °C |
| 2 | `laptop_cpu_volta` | `61746c6f-765f-7570-635f-706f7470616c` | 8 | CPU 电压 V |
| 3 | `laptop_cpu_clock` | `6b636f6c-635f-7570-635f-706f7470616c` | 8 | CPU 频率 MHz |
| 4 | `laptop_gpu_usage` | `65676173-755f-7570-675f-706f7470616c` | 8 | GPU 占用率 % |
| 5 | `laptop_gpu_temp` | `00706d65-745f-7570-675f-706f7470616c` | 8 | GPU 温度 °C |
| 6 | `laptop_gpu_clock` | `6b636f6c-635f-7570-675f-706f7470616c` | 8 | GPU 频率 MHz |
| 7 | `laptop_gpu_ram` | `00006d61-725f-7570-675f-706f7470616c` | 8 | 显存已用 |
| 8 | `laptop_gpu_tot_r` | `725f746f-745f-7570-675f-706f7470616c` | 8 | 显存总量 |
| 9 | `laptop_ram` | `00000000-0000-6d61-725f-706f7470616c` | 8 | 内存已用 |
| 10 | `laptop_tot_ram` | `00006d61-725f-746f-745f-706f7470616c` | 8 | 内存总量 |
| 11 | `laptop_fan_used` | `00646573-755f-6e61-665f-706f7470616c` | **40** | **笔记本内置风扇**状态 ×5 |
| 12 | `laptop_fan_speed` | `64656570-735f-6e61-665f-706f7470616c` | **40** | **笔记本内置风扇**转速 ×5 |

#### ⚠️ 需要向项目作者确认的两个语义问题

**请在开始编码前问清楚（不要猜）：**

1. **`fan_used` 的 5 个 `double` 是什么？** 是"该风扇是否在转"（0.0/1.0）、还是"该风扇的占空比百分比 0..100"？
2. **`gpu_ram` / `tot_ram` / `ram` 的单位？** 字节？MB？GB？百分比？

#### 关于 `fan_used` / `fan_speed` 的 `[5]`

**注意：这 5 个是"笔记本电脑自身的风扇"，不是散热底座的风扇。**
散热底座只有 1 个风扇（那走 `fan_svc`）。

笔记本通常有 1~3 个内置风扇。固件给了 5 个位置。
**没装的风扇请填 `0`**，客户端要能处理"不足 5 个"的情况。

#### ⚠️ 重要：目前固件**还没有消费**这些数据

`laptop_sys.c` 只是把值存进一个结构体：

```c
static laptop_info_t laptop_info;
laptop_info_t* get_laptop_info_ptr() { return &laptop_info; }
```

**OLED 上显示"电脑信息"的页面还没实现。** 所以：

- PC 端可以正常写入，**不会报错**
- 但**看不到任何效果**（OLED 上不会显示）
- 请照常实现，并在文档里注明"等待固件侧完成显示"

### 3.6 配对与安全

固件的 Security Manager 配置：

```c
ble_hs_cfg.sm_io_cap        = BLE_HS_IO_NO_INPUT_OUTPUT;   /* Just Works，无 PIN 码 */
ble_hs_cfg.sm_bonding       = 1;                            /* 支持绑定，密钥存 flash */
ble_hs_cfg.sm_mitm          = 0;                            /* 不要求防中间人 */
ble_hs_cfg.sm_sc            = 1;                            /* LE Secure Connections */
```

**而所有特征的 flags 都没有加密要求**（没有 `_SEC` / `_SC` 变体）。

**结论：理论上不需要配对，直接连接即可读写。**

#### 但有一个真实的坑：**Windows 侧的配对缓存会失效**

固件把绑定密钥存在 NVS 里。如果**固件被重新烧录、或执行过 `nvs_flash_erase`**，密钥就没了 ——
而 **Windows 那边还记着旧的密钥**，于是：

- 连接可能失败
- 或者连接成功但读写全部报错

**这在实际开发中一定会遇到**（因为你会反复烧固件）。

**所以新项目必须做到：**

1. **捕获配对的异常**，不要让它把程序搞崩
2. **给出明确的提示**："连接失败，请在 Windows 设置 → 蓝牙和其他设备中删除该设备，然后重试"
3. **最好能加一个"忘记此设备"的按钮**（调用 `DeviceInformation.Pairing.UnpairAsync()`）
4. **在 UI 上显示当前是否已配对及配对状态**

**不要在连接时主动调用 `PairAsync()`**（除非确实需要）—— 让 Windows 自己处理。过度主动配对反而容易制造状态不一致。

---

## 第四节 · 行为与时序约束

### 4.1 实时性要求

| 数据 | 建议频率 | 备注 |
|---|---|---|
| `fan_rpm` 通知（ESP32 → PC） | **ESP32 每秒推 1 次** | 客户端不用管频率，只要订阅 |
| 笔记本状态（PC → ESP32） | **1 秒 1 次比较合适** | 13 个特征各写一次 = 每秒 12 次写 + 2 次 40 字节写 |
| 温度曲线自动控制 | 1~5 秒 1 次 | 见第九节 |

**⚠️ 每秒写 14 个特征在 BLE 上是可观的流量。** 请：

- 用**批量/流水线**方式写，不要每个都 `await` 完再写下一个（但要注意 Windows 的 GATT 操作有并发限制，需要串行队列 + 适度提前）
- **如果某个值没变化，不要重复写**（做 diff）
- 考虑**降频**：CPU/GPU 占用率变化快，内存总量 `tot_ram` 几乎不变 —— **`tot_ram` 只需写一次**

### 4.2 连接生命周期

```
扫描/直接连 → 连接成功 → 发现服务 → 校验 4 个服务都存在
   → 订阅 fan_rpm 通知 → 开始周期上报 → 用户操作
   → 断开（主动/意外）→ 自动重连（带退避）→ 恢复订阅和上报
```

**必须实现自动重连**，并且要处理：

- 手机把设备抢走了（ESP32 断开你）
- ESP32 重启
- 电脑休眠/唤醒
- 蓝牙适配器被禁用

### 4.3 不要做的事

- ❌ **不要在拖动滑块的过程中连续写 flash 特征**（见 3.3(g)）
- ❌ **不要假设 MTU 是 256**
- ❌ **不要假设扫描一定能找到设备**（见 2.1）
- ❌ **不要用设备名或 MAC 当唯一标识**（名字改过好几次了）

---

## 第五节 · 技术栈

### 5.1 必选

| 项 | 选择 | 理由 |
|---|---|---|
| 语言 | **C# 12+** | |
| 框架 | **WPF**（沿用旧项目） | 旧项目 UI 可参考 |
| 目标框架 | **`net10.0-windows10.0.19041.0`** | **必须这个 TFM**，见下 |
| BLE API | **`Windows.Devices.Bluetooth`**（WinRT 原生） | 见 5.2 |
| 硬件监控 | **`LibreHardwareMonitorLib` 0.9.6** | 旧项目已在用，版本可直接沿用 |
| UI 库 | **无**（手写样式） | 旧项目就是手写的，见第十节 |

#### ⚠️ 关于目标框架：**不要额外引用任何 BLE NuGet 包**

`net10.0-windows10.0.19041.0` 这个 TFM 会**自动带出 WinRT 投影**：

```
runtimepack.Microsoft.Windows.SDK.NET.Ref/10.0.19041.57
  ├── WinRT.Runtime.dll
  └── Microsoft.Windows.SDK.NET.dll
```

**这就是 `using Windows.Devices.Bluetooth;` 能编译的原因。** 旧项目 `.csproj` 全文只有 4 行有效配置 + 一个 `LibreHardwareMonitorLib`，**没有任何 BLE 相关的 `PackageReference`**。

**请不要引入 `Plugin.BLE`、`32feet`、`BluetoothLE` 之类的第三方 BLE 库** —— 它们在 Windows 上底层还是这套 WinRT API，只是多一层封装和一堆跨平台包袱。

#### 已知的依赖卫生问题（旧项目踩过）

`LibreHardwareMonitorLib` 会**传递引入 17 个 Android/Linux/macOS 的 `System.IO.Ports` native runtime 包**，导致 `bin/` 下多出 40+ 个对 Windows 完全无用的文件。

**打包发布时请注意这一点**（可以用 `RuntimeIdentifier` + 裁剪，或发布时排除）。不必现在解决，但**别在发布时被它吓到**。

### 5.2 为什么用 WinRT 而不是第三方 BLE 库

- **`Windows.Devices.Bluetooth`** 是操作系统原生 API，**最稳定、无额外依赖**
- 第三方库（如 `Plugin.BLE`）在 Windows 上底层还是它，只是多一层封装
- 旧项目用的就是它

**关键类：**

```
BluetoothLEAdvertisementWatcher          扫描
BluetoothLEDevice.FromBluetoothAddressAsync / FromIdAsync   连接
GattSession                              查看 MTU、连接状态
GattDeviceService.GetCharacteristicsForUuidAsync
GattCharacteristic.WriteValueWithResultAsync
GattCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync  订阅 NOTIFY
GattCharacteristic.ValueChanged          收通知
```

### 5.3 强烈建议

| 包 | 用途 | 备注 |
|---|---|---|
| **`Microsoft.Extensions.Hosting`** + `DependencyInjection` | 依赖注入 | 旧项目**没有 DI**，服务全是 `new` 出来的，**不可测试**。请在 `App.OnStartup` 里组装 |
| **`CommunityToolkit.Mvvm`** | MVVM 基类 | 用它而不是手写 `INotifyPropertyChanged`。见 8.7 关于"用不用 MVVM"的讨论 |
| **`System.Text.Json`** | 设置持久化 | 旧项目已在用，**但要注意原子写和 SchemaVersion**（见 8.4） |
| **`Serilog`** 或简单文件日志 | 日志 | **BLE 问题没有日志根本查不出来**。旧项目只有一个无上限的 TextBox。请务必做文件日志 + 能导出 + 有级别 |

### 5.4 全局异常处理（旧项目完全没有）

旧项目的 `App.xaml.cs` 是**空文件**，一个全局异常处理都没注册。

**新项目必须在 `App.OnStartup` 里注册这三个：**

```csharp
DispatcherUnhandledException        // UI 线程上的未处理异常
AppDomain.CurrentDomain.UnhandledException          // 非 UI 线程
TaskScheduler.UnobservedTaskException                // 被丢弃的 Task
```

**而且要真的做点什么**（写日志 + 弹提示），不是注册了空着。否则程序闪退时你什么线索都没有。

---

## 第六节 · 架构建议

### 6.1 分层（把 BLE 从 UI 里彻底剥出来）

```
SmartFan.Core              （类库，无 UI 依赖，可单元测试）
├── Protocol/
│   ├── BleUuids.cs              所有 UUID 常量（唯一来源）
│   ├── PacketCodec.cs           所有载荷的 序列化/反序列化
│   └── Models/                  各配置的数据模型
├── Transport/
│   ├── IBluetoothTransport.cs   接口（方便 mock）
│   ├── WinRtBluetoothTransport.cs  真实实现
│   └── DeviceScanner.cs
├── Services/
│   ├── SmartFanConnection.cs    连接生命周期 + 自动重连 + 订阅管理
│   ├── FanService.cs            fan_svc 封装
│   ├── LightService.cs          light_svc 封装
│   ├── ConfigService.cs         config_svc 封装
│   ├── LaptopTelemetryService.cs laptop_svc 上报（定时器 + diff）
│   └── HardwareMonitor/         LibreHardwareMonitor 封装
└── Settings/
    └── SettingsStore.cs         %APPDATA% 持久化

SmartFan.App               （WPF，只做 UI）
└── ViewModels / Views
```

### 6.2 `Protocol/` 层是整个项目的核心

**把"字节"和"业务对象"之间的转换全部集中在这里**，其他层只处理强类型对象。

```csharp
// PacketCodec.cs 的样子
public static class PacketCodec
{
    public static byte[] EncodeBreath(BreathConfig cfg)          // -> 5 字节
    public static BreathConfig DecodeBreath(ReadOnlySpan<byte> d) // 必须正好 5 字节
    public static byte[] EncodeRound(RoundConfig cfg)            // -> 22 字节
    ...
}
```

**这个类可以完全脱离 BLE 做单元测试。** 请为每个格式写测试（尤其是边界：size=1、size=9）。

### 6.3 传输层要串行化

Windows 的 GATT 操作**不能无限并发**，且同一个 `GattCharacteristic` 上的操作要排队。

**请实现一个串行队列**（`SemaphoreSlim(1,1)` 或 `Channel`），所有 GATT 读写走它。

### 6.4 别把 UUID 散落各处

```csharp
public static class BleUuids
{
    public static readonly Guid FanService        = Guid.Parse("00000000-0000-0000-0063-76735f6e6166");
    public static readonly Guid FanMode           = Guid.Parse("00000000-0000-0000-6564-6f6d5f6e6166");
    // ...
}
```

**旧项目被吐槽"接口过乱"的一个原因就是 UUID 散落在多处。** 新项目请只留一个来源。

---

## 第七节 · 功能清单 — 必做（MVP）

### 7.1 连接管理

- [ ] **扫描**：列出附近的 BLE 设备，**默认按广播里的 `fan_svc` 过滤**，但**要能切换成"显示全部"**（方便排查）
- [ ] 显示：设备名、MAC/地址、RSSI 信号强度、是否含 `fan_svc`
- [ ] **连接 / 断开**
- [ ] **连接状态显示**（未连接 / 扫描中 / 连接中 / 已连接 / 已订阅 / 错误）
- [ ] **自动重连**（指数退避，可开关）
- [ ] **记住上次连接的设备**，下次启动自动列出/自动连

### 7.2 仪表盘

- [ ] **风扇转速实时显示**（订阅 `fan_rpm`），带**曲线图/仪表盘**
- [ ] 风扇模式显示与切换（手动 / 自动 / 全速）
- [ ] 手动 PWM 滑块（0~100），**松手才写**
- [ ] 目标转速输入（自动模式用）

### 7.3 灯光控制

- [ ] 灯效模式选择（9 个，带**中文名**和下图标）
- [ ] 亮度滑块（0~255）
- [ ] 刷新周期下拉（10/20/50/100/200 ms）

### 7.4 灯效参数编辑器

对应 `config_svc` 的 8 个特征。**每种灯效一个编辑页**：

- [ ] `STATIC`：1 个颜色选择器
- [ ] `RAINBOW`：`switch_ms` 输入
- [ ] `BREATH`：`switch_ms` + 1 个颜色
- [ ] `RANDOM_FLASH`：1 个开关
- [ ] `BLINK`：2 个颜色 + `period`（**size 固定 2，UI 上不要暴露 size**）
- [ ] `CUSTOM_BREATH`：**可变长度**颜色列表（1~9）+ 每段各自 `fades_ms`
- [ ] `CHASE`：**可变长度**颜色列表（1~9）
- [ ] `ROUND`：主色/副色 + `range` / `range_main` / `wait_ms` / `center_speed`

**每个编辑页都要有：读取当前值 / 修改 / 应用（写入） / 撤销。**

> ⚠️ **UI 上必须限制 size ≤ 9**，超出会被固件拒绝。

### 7.5 笔记本状态上报

- [ ] 从 LibreHardwareMonitor 读 CPU/GPU 数据
- [ ] 显示在 PC 界面上（自己的实时读数）
- [ ] **定时推送到 `laptop_svc`**（可开关、可调频率）
- [ ] **写前 diff**：值没变就不写
- [ ] `tot_ram` 这类常量只写一次

---

## 第八节 · UI 参考 —— 旧项目怎么做的

**旧项目路径：`G:\Csharp\SmartFan\SmartFan`**（26 个源文件，约 110 KB 代码）

### 8.0 先说三件纠正认知的事

**(a) 旧项目的 BLE 层不能沿用，但原因和你想的不一样。**

旧项目的 README 里写协议是 ASCII 文本：

```
T:65.3;G:58.1;F:72
```

**但 README 已经过时了。** 代码里实际发的是 **13 字节二进制**：

```csharp
// Services\BleSmartFanClient.cs:149-161
var payload = new byte[13];
payload[0] = ClampByte(MapTemperatureToPercent(cpuCelsius));  // 注释说是 cpu_usage，实际塞的是温度
payload[1] = 0;
payload[2] = ClampByte(MapTemperatureToPercent(gpuCelsius));
payload[3] = 0;
payload[4] = ClampByte(fanPercent);
WriteLe16(payload, 5, 0);
// ... 后面 8 个字节全是 0
```

**13 个字节里 8 个恒为 0，而且字段名和实际内容完全错位。**

**这就是作者说"接口过乱"的真正含义**：不是 UUID 乱，而是**旧的单特征设计根本承载不了新固件的 27 个特征** ——
一个特征要表达 CPU/GPU/内存/网络 9 个维度，只好把字段名写成一个意思、塞进另一个意思的值。

**结论：旧 BLE 层必须重写，但它的几个"工程模式"值得抢救（见 8.8）。**

**(b) 旧项目是纯 code-behind，不是 MVVM。**

已用 grep 全项目验证：

| 检查项 | 命中次数 |
|---|---|
| `{Binding}` | **2**（只有 DataGrid 的两列） |
| `DataContext` | **0** |
| `INotifyPropertyChanged` / `ICommand` / `ViewModel` | **0** |
| `IValueConverter` | **0** |
| XAML 里挂的事件处理器 | **31 个** |

`MainWindow.xaml.cs` **936 行、30 个事件处理方法**，是一个典型的上帝类。

**(c) `App.xaml.cs` 是空文件。**

全文 7 行：`public partial class App : Application { }` ——
**没有全局异常处理**（`DispatcherUnhandledException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` 一个都没有）。

### 8.1 可以直接搬的（几乎零改动）

| 文件 | 为什么能直接搬 |
|---|---|
| `Services\ThemeManager.cs`（182 行） | 纯静态、零 BLE 依赖。含 **4 套主题配色**（见 8.6）+ 背景模式 + 字体列表 + 壁纸位图生成。**只需修 2 个问题**（见 8.4） |
| `Services\FanCurve.cs`（47 行） | 纯函数、无副作用。**边界处理得很完整**：空集合 / 低于最低点 / 高于最高点 / 重复温度点防除零 / 结果 clamp 0~100 |
| `Services\SettingsStore.cs`（48 行） | `%AppData%\SmartFan\settings.json` + `System.Text.Json`，零依赖。**改 3 处**（见 8.4） |
| `Services\DemoHardwareMonitor.cs`（22 行） | 正弦+噪声假数据生成器，调 UI 时的利器 |
| `WallpaperCropWindow.xaml` + `.xaml.cs` | **完全自洽、零业务耦合**：4 个归一化比例入参 → 4 个只读属性出参，中间是"在图上拖框"交互 + `CroppedBitmap`。设计得很好 |
| `Models\FanCurvePoint.cs` / `TemperatureSample.cs` | 小 DTO，直接搬（`TemperatureSample` 需扩展字段，见 8.4） |
| `MainWindow.xaml.cs:853-865` 的 `MapTemperatureToX` / `MapFanToY` | 坐标映射数学，抽出来给新的图表控件用 |
| `MainWindow.xaml.cs:927-935` 的 `At<T>()` Canvas 扩展 | 把 `Canvas.SetLeft/SetTop` 链式化的小工具 |
| `MainWindow.xaml.cs:100-124` 的 `NormalizeAppearanceSettings` | 设置项合法性校验的思路（把它挪到 `AppearanceSettings.Validate()`） |
| `SmartFan.csproj` 的骨架 | `WinExe` + TFM + `UseWPF` + `Nullable enable` + `ImplicitUsings` |
| `.sln` | 16 行，单项目，Debug/Release × Any CPU 齐全 |

### 8.2 必须重写，但思路要保留的（**重要**）

旧 `BleSmartFanClient.cs`（417 行）里有 **5 个工程模式值得继承**，请务必在新实现里体现：

#### ✅ (1) `WithTimeout` —— 全项目最有价值的一段代码

```csharp
// BleSmartFanClient.cs:354-376 的思路
private static async Task<T> WithTimeout<T>(IAsyncOperation<T> operation,
                                            TimeSpan timeout, string label)
{
    var task = operation.AsTask();
    if (await Task.WhenAny(task, Task.Delay(timeout)) != task)
    {
        try { operation.Cancel(); } catch { }
        throw new TimeoutException($"{label} timed out after {timeout}");
    }
    return await task;
}
```

**为什么关键：WinRT 的 BLE 操作会永久 hang** —— 尤其是 `BluetoothLEDevice.FromBluetoothAddressAsync()`
和 GATT 服务发现。**没有超时，UI 就卡死。**

旧代码里所有 WinRT 调用都包了它（8~10 秒）。

**新项目要改进：** 抽成通用扩展 `WinRtAsyncExtensions.WithTimeoutAsync(this IAsyncOperation<T>, TimeSpan, CancellationToken)`，
并且**这次要真的和 `CancellationToken` 联动**（旧代码只等 `Task.Delay`，不响应外部取消）。

#### ✅ (2) "Uncached 优先，Cached 兜底"的 GATT 发现策略

```csharp
// 旧代码的三级回退（:217-255 / :288-352）
① GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached)   ← 首选
② GetGattServicesAsync(BluetoothCacheMode.Cached)                   ← 兜底
③ GetGattServicesAsync(BluetoothCacheMode.Uncached)                 ← 再兜底，自己按 UUID 找
```

**为什么关键：Windows 的 GATT 缓存臭名昭著。** 固件改了属性表，系统还返回旧缓存，
表现就是"服务/特征找不到"。

**新项目保留这个策略，但实现要改：** 做成**显式的策略链**（`IReadOnlyList<GattLookupStrategy>`），
每一步记录「尝试了哪种、拿到几个、耗时多少」，而不是三层 `catch` 堆在一起。

#### ✅ (3) `GattSession.MaintainConnection = true` 的用法与顺序

```csharp
_gattSession.MaintainConnection = true;    // 连接后：保持长连接
// ...
_gattSession.MaintainConnection = false;   // 断开前：先复位
_gattSession.Dispose();                    // 再释放
```

**顺序很重要。** 直接 Dispose 而不先复位，可能导致连接被系统继续保持。

#### ✅ (4) 幂等清理 + 失败回滚

```csharp
public async Task ConnectAsync(...)
{
    await DisconnectAsync();               // ① 先清干净，避免半连接残留
    try { /* 建连接 */ }
    catch { await DisconnectAsync(); throw; }  // ② 失败即回滚，不留脏状态
}
```

#### ✅ (5) 穷尽式诊断日志

旧代码在每一步都打：`Status` / `ProtocolError` / `Count` / `MaxPduSize` / `CharacteristicProperties`，
以及**枚举到的每一个 service / characteristic**。

**排查 Windows BLE 问题，这些日志是唯一手段。** 请保留并结构化（升级成 `ILogger` + 级别控制）。

> ⚠️ 但旧代码**每条广播都无条件打日志**（扫描 6 秒能刷几千行）。新的要加级别过滤。

#### ✅ (6) 其他值得保留的小点

| 点 | 说明 |
|---|---|
| `IAsyncDisposable` 而非 `IDisposable` | BLE 清理天然是异步的 |
| `GattWriteOption.WriteWithoutResponse` | 高频遥测的正确选择（省一个 ATT 往返） |
| `BleDiscoveredDevice` 用 `record` + 携带 RSSI + 按 RSSI 降序 | 扫描结果模型设计得体 |
| 日志通过 `Action<string>? _log` 注入 | 把日志通道和 UI 解耦，方向对 |

### 8.3 必须抛弃的

| 抛弃什么 | 为什么 |
|---|---|
| **`MainWindow.xaml.cs` 整体**（936 行上帝类） | 5 类职责混在一起（BLE 会话 + 硬件采样 + 曲线编辑 + 手绘图表 + 主题壁纸 + 持久化）。**不要增量重构，直接按职责重切** |
| **纯 code-behind 事件驱动** | 31 个事件处理器。**这是本次重做最大的收益点** |
| **`MainWindow` 直接 `new` 服务** | `private readonly SettingsStore _settingsStore = new();` 这种（`:22-25`）—— 不可测试、不可替换。用 DI |
| **`_isUpdatingUi` 手动防重入闸门** | 一个 bool 在 **15 个方法**里被读写。这是"没有绑定机制"逼出来的。有了 MVVM 就自然消失 |
| **手绘 `Canvas` 图表每帧全量重建** | `CurveCanvas.Children.Clear()` + 重建几十个 `Line`/`TextBlock`/`Ellipse`，**每次采样调一次**。5Hz 时明显 GC 抖动。改成复用实例、只改 `Points` |
| **无上限的日志文本框** | `AppendText` + `ScrollToEnd()` 没有行数上限，跑几小时会成为瓶颈。改成环形缓冲 |
| **`async void` 里的裸调用** | `SampleTimer_Tick` 里 `_hardwareMonitor.ReadCurrent()` **在 try 之外** → 抛异常 = 进程崩溃 |
| **协议知识写在传输层/UI 层** | 13 字节布局写死在 `SendTelemetryAsync` 里；UI 还自己拼了一个**和实际载荷完全不符**的"协议预览"文本在骗用户 |
| **UUID / 魔数散落多处** | 旧项目有**三份互相矛盾的真相**。见 8.4 |
| **`README.md`** | 它的 UUID 和协议都**已经和代码矛盾**。不要沿用，重写 |

### 8.4 参考但必须改造的（旧项目的具体缺陷）

这些都是**旧项目真实存在的 bug**，新项目写的时候要避开。

#### (a) 主题只生效了一半 —— `StaticResource` 的坑

`App.xaml` 的**全局隐式样式用 `StaticResource`**：

```xml
<Setter Property="Background" Value="{StaticResource AccentBrush}" />   <!-- App.xaml:72 -->
```

`StaticResource` 在**进程启动时一次性解析并烘焙进 Setter 的值**。
而 `ThemeManager.Apply()` 是**运行期替换资源字典里的实例** —— **这些 Setter 不会跟着变**。

**后果：** 在 Cyber/Graphite/Nebula 主题下，主按钮仍然是浅色主题的蓝色；深色主题下普通按钮仍是白底深字。

对比：`MainWindow.xaml` 自己的样式**全部正确用了 `{DynamicResource}`**，所以窗口内自定义样式会跟主题，App 级样式不会。

**新项目要求：**
- **全部改用 `DynamicResource`**
- **把资源拆到 `Themes\*.xaml` 独立 `ResourceDictionary`**（旧项目一个都没有，全靠 `App.xaml` 内联 + C# 硬编码）
- 旧项目还有 **3 个死资源**（`SuccessColor`/`DangerColor`/`WarningColor` 零引用），而代码里改用 `Color.FromRgb(16,185,129)` 硬编码 —— **请统一**

#### (b) `AppSettings` 没有版本号

只有靠"**值和旧默认值相等就替换**"这种脆弱判断做迁移：

```csharp
// MainWindow.xaml.cs:126-143
if (settings.DeviceNamePrefix == "SmartFan" || settings.ServiceUuid == Guid.Parse("0000fff0-..."))
    settings = new AppSettings();
```

**用户手动把前缀设成 `"SmartFan"` 就会被莫名改掉。**

**新项目要求：** 加 `SchemaVersion` 字段，做正规迁移。

#### (c) `SettingsStore` 的三处问题

| 问题 | 位置 | 后果 |
|---|---|---|
| **构造函数里 `Directory.CreateDirectory`** | `:21` | 副作用藏在构造里。而调用点是字段初始化器 `= new()`，**异常会发生在窗口构造之前，无人捕获** |
| **`Save()` 无 try/catch、非原子写** | `:43-47` | 磁盘满/文件被占用 → 异常直冲 UI 线程；写到一半崩溃 = 配置损坏 |
| **`Load()` 裸吞异常** | `:37-40` `catch { return new AppSettings(); }` | 配置损坏静默重置，用户不知情 |

**新项目要求：** 构造里不做 IO；`SaveAsync` + 临时文件 + `File.Replace` 原子写；错误要上报。

#### (d) `TemperatureSample` 字段太少

旧模型只有 `CpuCelsius` / `GpuCelsius` / `Timestamp`。

**新固件要 11 个标量 + 2 个 5 元数组。** 必须扩展模型，并且在 `LibreHardwareMonitorService` 里：

```csharp
// 旧代码（:11-19）只开了这俩
_computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
```

**要加上 `IsMemoryEnabled` / `IsNetworkEnabled`**，并读 `SensorType.Load` / `SensorType.Power` / `SensorType.Data`。
（旧代码全文件只用了 `SensorType.Temperature`。）

#### (e) `temperatures.Max()` 太粗暴

```csharp
// LibreHardwareMonitorService.cs:61
return temperatures.Count == 0 ? null : temperatures.Max();
```

它把 `CPU Package`、`Core #1..#N`、甚至主板/VRM 传感器的温度**一起取最大值**当"CPU 温度"。

**新项目要求：** 按传感器名白名单筛选（`"CPU Package"` / `"GPU Core"` / `"Core Max"`），并**记录实际用了哪个传感器**（便于排查读数异常）。

#### (f) 双层 demo 兜底，且上层无法区分

旧项目有两层降级：
1. `LibreHardwareMonitorService` 内部有 `_fallback` 字段，**逐个传感器**兜底
2. `MainWindow.CreateHardwareMonitor()` 在构造失败时**整体换成 Demo**

**结果：可能出现"CPU 是真值、GPU 是正弦波假值"的混合数据，而 UI 上没有任何标志。**

**新项目要求：** 只保留一层，并且**数据来源要对 UI 可见**（明确显示"当前为演示数据"）。

### 8.5 🔴 新项目必须首先避免的一个 bug

**旧项目最严重的缺陷：`IsConnected` 会说谎。**

```csharp
// BleSmartFanClient.cs:22
public bool IsConnected => _device is not null && _writeCharacteristic is not null;
```

而断线回调**只打了一行日志，什么都不做**：

```csharp
// BleSmartFanClient.cs:378-381
private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
{
    _log?.Invoke($"ConnectionStatusChanged={sender.ConnectionStatus}");
}
```

**于是意外断线时：**

```
_device 和 _writeCharacteristic 仍然非 null
  → IsConnected 仍返回 true
    → UI 继续显示绿灯 "Connected"          ← 在骗用户
    → 定时器继续每次尝试发送
      → 每次都抛异常，被 catch 吞掉只改状态栏文字   ← 白跑 + 掩盖问题
```

**新项目必须：**

1. **用显式状态机** 而不是靠字段非空推断：

   ```csharp
   public enum ConnectionState
   {
       Disconnected, Scanning, Connecting, Connected, Reconnecting, Faulted
   }
   ```

2. **订阅 `GattSession.SessionStatusChanged`**（旧代码完全没用这个 API）

3. **状态变化要作为事件冒泡到 UI**，而不是只打日志

4. **`ConnectionStatusChanged` 里发现断开 → 触发指数退避重连**

### 8.6 旧项目的视觉语言（值得延续）

旧项目**没有用任何第三方 UI 库**，整套外观是手写的。这套视觉语言是清楚的，建议延续：

#### 4 套主题（`ThemeManager.cs:110-181`）

| 主题名 | 强调色 | 背景 | 定位 |
|---|---|---|---|
| `Nebula`（默认） | 靛紫 `#6366F1` | 深蓝紫 `13,18,33` | 默认暗色 |
| `Light` | 蓝 `#2563EB` | 浅灰 `238,242,247` | 亮色 |
| `Cyber` | 青 `#06B6D4` | 深蓝黑 `7,12,24` | 赛博 |
| `Graphite` | 琥珀 `#F59E0B` | 炭黑 `24,24,27` | 沉稳 |

每套 12 个颜色成员（`ThemePalette` record）。

> ⚠️ `ThemePalette.FromName()` 的 `default => Nebula` —— 非法主题名会**静默**落到 Nebula。新项目要显式报错或记日志。

#### 3 种背景模式

`Layered`（对角渐变，默认）/ `Solid`（纯色）/ `Wallpaper`（壁纸）

#### 卡片式布局语言

| 样式 Key | 用途 |
|---|---|
| `PanelCard` | 通用卡片（`Border`，`CornerRadius=8`，`Padding=18`） |
| `MetricCard` | 指标卡（基于 PanelCard，`MinHeight=112`） |
| `SectionTitle` | 区块标题（17px SemiBold） |
| `HintText` / `FieldLabel` | 提示文字 / 字段标签 |
| `ModeButton` | 模式切换按钮（`RadioButton`，完整重写 `ControlTemplate`） |
| `PrimaryButton` | 主按钮（蓝底） |

**主窗口布局思路（值得借鉴）：**

```
Grid (root)
├─ Image  壁纸层（铺满，可折叠）
├─ Grid   主体 Margin=24，行 = 顶栏 / 内容 / 日志条
│   ├─ 顶栏：标题 + "Appearance" 按钮 + 连接状态胶囊（Ellipse + Text）
│   ├─ 内容：左列 356px（BLE 面板） | 20px 间隙 | 右侧（3 张指标卡 + 曲线卡）
│   └─ 日志条：状态文字 + 清空按钮 + 只读日志框（92px，Consolas）
├─ Grid   外观隔离层（让抽屉不被半透明面板透出壁纸叠影）
└─ Border 外观抽屉（右侧，Width=340，Visibility 切换）
```

**"页面"其实只有两个半**：主工作区 + 外观抽屉 + 独立模态的壁纸裁剪窗口。

**新项目建议改用 `TabControl` 或导航** —— 因为新功能（8 种灯效编辑器、高级设置）比旧项目多得多，塞不进一个窗口。

#### 风扇曲线图表的实现（旧项目手搓 Canvas）

**没有用图表库**，是 `Canvas` + 手搓 `Shape`：

```csharp
// 每次调用先清空整个视觉树，再重建
CurveCanvas.Children.Clear();
DrawChartGrid();                    // 网格线 + 轴标签
DrawCurveLine();                    // Polyline 折线
DrawCurrentTemperatureMarker();     // 绿色虚线 + 圆点 + 圆角气泡
```

坐标映射把 30–100°C、0–100% 归一化到 plot 区（`left=44, top=18, right=18, bottom=34`）。

**新项目要求：至少改成增量更新**（复用实例、只改 `Points`），或直接上 `ScottPlot` / `LiveCharts2` / `OxyPlot`。

### 8.7 关于 MVVM 的建议

**旧项目是纯 code-behind**，而**作者是 Java 背景的 C# 新手**。所以：

| 建议 | 说明 |
|---|---|
| **分层是硬要求，MVVM 是软要求** | **BLE 逻辑绝对不能写在 `MainWindow.xaml.cs` 里** —— 这是底线。UI 用 MVVM 还是 code-behind 是次要的 |
| **如果上 MVVM，用最简单的形式** | `CommunityToolkit.Mvvm` 的 `ObservableObject` + `[ObservableProperty]` + `[RelayCommand]`，**不要手写 `INotifyPropertyChanged`** |
| **必须解释概念** | 在代码注释里说明 `ObservableObject` 对应 Java 的什么（比如 JavaFX 的 `Property` / `Observable`），`ICommand` 对应什么（`ActionListener` / `EventHandler`） |
| **事件处理器要大幅减少** | 旧项目 31 个。新项目应该只剩 0~3 个（仅纯视图逻辑，比如 Canvas 尺寸变化） |
| **DI 要有，但可以简单** | `Microsoft.Extensions.Hosting` + `DependencyInjection`，在 `App.OnStartup` 组装。**`App.xaml.cs` 不能再是空文件** |

### 8.8 旧项目做得不好的其他细节（供避坑）

| 问题 | 位置 | 教训 |
|---|---|---|
| `cancellationToken.Register(() => { })` **空回调** | `BleSmartFanClient.cs:32` | 写了等于没写。取消链路要真的接上 |
| `watcher.Start()` 在 `try` 之外 | `:79` | 抛异常时 `finally` 不执行 → watcher 泄漏 |
| 固定扫 6 秒，找到也不提前退出 | `:82` | 用户体验差 |
| `_service = null` 不 `Dispose()` | `:179` | WinRT GATT 服务是 `IDisposable`，反复连接会句柄泄漏 |
| `DisconnectAsync` 返回 `Task.CompletedTask` | `:177-197` | 名字骗人。要么真异步，要么改名 `Disconnect()` |
| 两个死代码方法 | `:388-411` | `LogAllServicesAsync` / `LogAllCharacteristicsAsync` 零调用，且功能已被内联实现 |
| `FanCurvePoint` 无 INPC 却放进 `ObservableCollection` | `Models\FanCurvePoint.cs` | 集合通知有、元素通知没有 → 改一个格子不会触发订阅者 |
| `Curve` 每次调用 `OrderBy().ToArray()` | `FanCurve.cs:14-16` | 每 tick 最多调 **4 次**。应缓存排序结果 |
| 没有滞回（hysteresis） | `FanCurve.cs` | 温度抖动 0.1°C → 风速抖动 → BLE 写入抖动 |
| `WithOpacity` 的 clamp 下限 `0.35` | `ThemeManager.cs:81` | 与 UI 滑块的 `0.45` / `0.2` **三方不一致** |
| `"Consolas"` 混进全局 UI 字体列表 | `ThemeManager.cs:19` | 编程等宽字体做全局 UI 字体是明显笔误 |
| 中英混杂 | `MainWindow` 全英文，`WallpaperCropWindow` 全中文 | 统一 |
| `.idea/` 被提交进仓库 | `.gitignore` 只 5 行 | 补上 |

### 8.9 新项目的目录结构建议

```
SmartFan.sln
├─ SmartFan.Core/                    类库，无 WPF 依赖，可单测
│   ├─ Models/          AppSettings(带 SchemaVersion)
│   │                   FanCurvePoint / TemperatureSample(含 Usage/Power/Ram/Net + 数据来源标志)
│   ├─ FanCurve.cs      纯算法 + 滞回参数
│   ├─ Protocol/        BleUuids.cs          ← UUID 唯一来源
│   │                   PacketCodec.cs       ← 27 个特征的编解码（可脱离 BLE 单测）
│   ├─ Hardware/        IHardwareMonitor(async) / LibreHardwareMonitorService
│   │                   DemoHardwareMonitor / HardwareDataSource 枚举
│   └─ Settings/        ISettingsStore / JsonSettingsStore(原子写 + 迁移)
├─ SmartFan.Ble/                     类库，封装 WinRT BLE
│   ├─ IBleClient.cs                 接口（方便 mock）
│   ├─ BleClient.cs                  显式状态机 + 指数退避重连
│   ├─ GattDiscoveryStrategy.cs      Uncached → Cached → All 显式策略链
│   ├─ BleDeviceProfile.cs           DeviceNamePrefix + 全部 UUID
│   └─ WinRtAsyncExtensions.cs       WithTimeoutAsync + CancellationToken 联动
├─ SmartFan.App/                     WPF
│   ├─ App.xaml(.cs)                 OnStartup + DI + **全局异常处理**
│   ├─ Themes/                       Nebula.xaml / Light.xaml / Cyber.xaml / Graphite.xaml
│   ├─ Services/                     ThemeManager / WallpaperService(带缓存)
│   ├─ ViewModels/                   MainViewModel / AppearanceViewModel / ...
│   ├─ Views/                        MainWindow / WallpaperCropWindow / 灯效编辑器...
│   └─ Controls/                     FanCurveChart(UserControl, 增量绘制)
│                                    ConnectionIndicator / LogViewer(环形缓冲)
└─ SmartFan.Tests/                   FanCurve / PacketCodec / SettingsStore 单测
```

---

## 第九节 · 功能清单 — 用户点名要的进阶功能

### 9.1 高级设置页（用户明确要求）

用户原话：*"连接到哪一台蓝牙，地址是什么，都可以放到高级设置里面"*

**请实现一个"高级设置"页，包含：**

| 项 | 说明 |
|---|---|
| **设备地址输入框** | 手动填 MAC / `DeviceInformation.Id`，**不扫描直接连**（解决 2.1 的问题） |
| **最近连接的设备列表** | 可增删，可命名（"我的散热器"），可设默认 |
| **自动重连开关 + 重连间隔** | |
| **扫描过滤开关** | 只看含 `fan_svc` 的设备 / 显示全部 |
| **MTU 显示与设置** | 显示实际协商值；可选尝试请求更大 MTU |
| **上报频率设置** | 笔记本状态推送间隔（如 500ms / 1s / 2s / 5s） |
| **上报项开关** | 逐项开关要推哪些数据（有些机器读不到 GPU） |
| **配对管理** | 显示配对状态 + **"忘记此设备"按钮**（见 3.6） |
| **日志级别 / 导出日志** | 排查 BLE 问题必需 |

### 9.2 基础 UI 设置（用户明确要求，要可变）

| 项 | 说明 |
|---|---|
| **主题** | 亮色 / 暗色 / 跟随系统（旧项目 `ThemeManager` 已有雏形） |
| **强调色** | 可自定义 |
| **壁纸** | 旧项目有 `WallpaperCropWindow`，保留并完善 |
| **窗口尺寸/位置记忆** | 下次启动恢复 |
| **字体大小** | 小 / 中 / 大 |
| **语言** | 简体中文（先只做中文也行，但**不要把中文硬编码在 XAML 里散落各处**） |
| **开机自启** | 注册表 `Run` 键 |
| **最小化到托盘** | 关窗口不退出，托盘图标可操作 |
| **数字单位** | °C/°F、MB/GB 切换 |

### 9.3 其他建议加的功能（按价值排序）

| 优先级 | 功能 | 理由 |
|---|---|---|
| **高** | **温度曲线可视化编辑器**（拖拽控制点） | 旧项目已有基础，是核心卖点 |
| **高** | **系统托盘 + 开机自启** | 这类工具必须常驻后台 |
| **高** | **配置文件导入/导出**（json） | 换电脑、备份、分享 |
| **高** | **固件接口自检页** | 就是 P0 那个工具的 UI 版，用户能自己查问题 |
| **中** | **风扇转速历史曲线** | 已有 NOTIFY 数据，画个图很自然 |
| **中** | **多配置文件/场景**（游戏/办公/静音） | 一键切换 |
| **中** | **灯效与风扇联动**（温度越高灯越红） | 有意思，且实现不难 |
| **中** | **RPM → 温度关联图** | 看散热效果 |
| **低** | **桌面小组件**（悬浮显示温度/转速） | |
| **低** | **硬件信息页**（CPU/GPU 型号、内存型号） | LibreHardwareMonitor 能读到 |
| **低** | **灯效预览**（PC 上先画出来看效果，再下发） | 需要复刻固件的灯效算法，工作量大 |
| **低** | **曲线自动学习**（根据实际温度自动调曲线） | 加分项 |

### 9.4 `FAN_USED` / `FAN_SPEED` 的读取来源

第 12/13 个特征要的是**笔记本内置风扇**的转速。

**`LibreHardwareMonitor` 能读到部分笔记本的 EC 风扇转速**（在 `SensorType.Fan` 下）。
**请在实现时先探测是否有这类传感器，没有就让用户手动关闭这两项上报。**

---

## 第十节 · 逐接口验收清单（**必须逐条验证并填写**）

> **这是"保证连接能够成功"的核心。**
> 请实现 P0 的自检工具，**逐条跑下面的用例，把实际结果填进最后一列**。
>
> 如果任何一条不符合"期望结果"，**停下来报告**，不要继续往下做。

### 10.1 连接层

| # | 用例 | 期望结果 | 实际 |
|---|---|---|---|
| C1 | 按 `fan_svc` 过滤扫描 | 能找到设备，名称为 `连上尹浩荣jj断两根` | |
| C2 | 不做过滤扫描全部 | 设备出现在列表里 | |
| C3 | 连接 | 成功，能读到 `GattSession.MaxPduSize` | |
| C4 | 发现 `fan_svc` | 3 个特征全部存在 | |
| C5 | 发现 `light_svc` | 3 个特征全部存在 | |
| C6 | 发现 `config_svc` | 8 个特征全部存在 | |
| C7 | 发现 `laptop_svc` | 13 个特征全部存在 | |
| C8 | 订阅 `fan_rpm` 的 NOTIFY | 写 CCCD 成功，**1 秒内收到第一个通知** | |
| C9 | 连续收通知 10 秒 | 收到约 10 条，每条 4 字节 | |
| C10 | 主动断开 | 断开成功，无异常 | |
| C11 | 断开后重连 | 成功，**且需要重新订阅** | |

### 10.2 `fan_svc`

| # | 用例 | 期望结果 | 实际 |
|---|---|---|---|
| F1 | 读 `fan_mode` | 4 字节，值是 0/1/2 | |
| F2 | 写 `fan_mode` = `2`（全速）→ 读回 | 读回 4 字节 `02 00 00 00` | |
| F3 | 风扇实际转起来 | 能听到/看到风扇全速 | |
| F4 | 写 `fan_mode` = `0`（手动） | 不报错 | |
| F5 | 写 `fan_pwm` = `50`（1 字节） | 不报错 | |
| F6 | 读 `fan_pwm` | ⚠️**已知固件 bug：恒返回 0**。记录实际值即可 | |
| F7 | 写 `fan_pwm` 为 2 字节 | **应被拒绝**（长度错误） | |
| F8 | 写 `fan_rpm` = `3000`（4 字节） | 不报错（这是设置目标转速） | |
| F9 | 观察通知 | 收到的值随风扇转速变化 | |
| F10 | 尝试 READ `fan_rpm` | ⚠️**应失败**（固件没给 READ 权限） | |

### 10.3 `light_svc`

| # | 用例 | 期望结果 | 实际 |
|---|---|---|---|
| L1 | 读 `light_mode` | 4 字节，值 0..8 | |
| L2 | 逐个写 `light_mode` = 1..8 | 灯效**肉眼可见地切换** | |
| L3 | 写 `light_mode` = 9 | **应被拒绝** | |
| L4 | 写 `light_brightness` = `0` / `128` / `255` | 灯明显变暗/中/最亮 | |
| L5 | 写 `light_update_per` = `50` | 不报错，灯效节奏变化 | |
| L6 | 写 `light_update_per` = `33` | **应被拒绝**（只接受 10/20/50/100/200） | |
| L7 | 写 `light_mode` = 3 → **重启设备** | 重启后仍是 3（**持久化生效**） | |
| L8 | 写 `light_update_per` = 100 → 重启 | ⚠️**回到 10**（该值不持久化） | |

### 10.4 `config_svc`

**每个特征都要：读一次 → 写一个新值 → 读回比对 → 观察灯效变化 → 重启后确认还在。**

| # | 特征 | 长度 | 写入用例 | 期望 | 实际 |
|---|---|---|---|---|---|
| G1 | `static` | 3 | `FF 00 00`（红） | 灯变红；读回 `FF 00 00` | |
| G2 | `rainbow` | 2 | `E8 03`（1000ms） | 读回 `E8 03` | |
| G3 | `breath` | 5 | `E8 03 FF 00 00` | 读回一致 | |
| G4 | `randflash` | 1 | `01` | 随机闪烁开启 | |
| G5 | `blink` | 9 | `02 FF0000 0000FF D0 07` | size=2、红/蓝、2000ms | |
| G6 | `blink` 错误长度 | 8 或 10 | **应被拒绝** | | |
| G7 | `custbreath` N=1 | 6 | `01 FF0000 E803` | 读回一致 | |
| G8 | `custbreath` N=9 | 46 | 9 色 + 9 个时长 | 读回一致 | |
| G9 | `custbreath` N=10 | — | **应被拒绝**（超 9） | | |
| G10 | `custbreath` N=0 | — | **应被拒绝** | | |
| G11 | `chase` N=4 | 13 | 4 色 | 读回一致 | |
| G12 | `round` | 22 | 主色/副色/range=5.0/range_main=1.0/wait=2000/center=1.0 | 读回一致 | |
| G13 | `round` 长度 21 | — | **应被拒绝** | | |
| G14 | 全部写入后**重启** | | 8 种灯效的配置**全部保留** | | |

> **G8 的 46 字节包是最长的一个，请特别验证它不会被 MTU 截断。**

### 10.5 `laptop_svc`

| # | 用例 | 期望结果 | 实际 |
|---|---|---|---|
| P1 | 写 `cpu_temp` = 65.5（8 字节 double） | 不报错，读回 65.5 | |
| P2 | 写 `cpu_temp` 为 4 字节 | **应被拒绝** | |
| P3 | 写 `fan_speed` 为 5 个 double（40 字节） | 不报错，读回一致 | |
| P4 | 写 `fan_speed` 为 4 个 double（32 字节） | **应被拒绝** | |
| P5 | 连续写全部 13 个特征 | 无错误 | |
| P6 | 观察 OLED | ⚠️**目前不会有任何显示**（固件侧未实现） | |

### 10.6 稳定性

| # | 用例 | 期望结果 | 实际 |
|---|---|---|---|
| S1 | 断开 ESP32 电源 5 秒再上电 | 客户端自动重连成功并恢复订阅 | |
| S2 | 用手机连上设备，再从 PC 连 | 按 2.1，**扫描找不到**；用地址直连**应该能连上** | |
| S3 | 连续运行 30 分钟 | 无内存泄漏、无崩溃、通知不断流 | |
| S4 | 电脑休眠再唤醒 | 能恢复连接，或能给出明确提示 | |
| S5 | 重烧固件后连接 | 能给出"请删除设备重新配对"的提示（见 3.6） | |

---

## 第十一节 · 已知的固件侧问题（供参考，**不要在客户端"绕过"，请报告**）

> 这些是**固件的问题**，不是客户端该解决的。列在这里是为了让你知道"这不是你写错了"。

| # | 问题 | 位置 | 影响 |
|---|---|---|---|
| 1 | **`fan_pwm` 读分支恒返回 0** | `gatt_svc.c` | 客户端无法读回 PWM 值 |
| 2 | **`fan_rpm` 缺 `READ` 权限** | `gatt_svc.h` | 只能订阅通知，不能主动读一次 |
| 3 | **`fan_rpm` 同时有 `WRITE`**（写=目标转速） | `gatt_svc.h` | 命名与语义不符，容易混 |
| 4 | **`laptop_svc` 的数据没有被任何任务消费** | `Data/laptop_sys.c` | PC 推上去的数据目前不会显示 |
| 5 | `fan_used` / `fan_speed` 的语义未明确 | 待确认 | 见 3.5 |
| 6 | `SYSTEM_SVC` / `SCREEN_SVC` 定义了但**不在服务表里** | `gatt_svc.h` | 暂时不存在这两个服务 |

**请在报告问题时引用这些编号。**

---

## 第十二节 · 交付要求

### 12.1 每个阶段请提供

1. **完整的、能编译的项目文件**（不要给"片段"，除非明确说了是片段）
2. **一份说明**：这一阶段做了什么、怎么验证、有哪些已知限制
3. **验收清单的填写结果**（第十节那张表）
4. **遇到的问题 / 需要作者确认的点**（列成清单）

### 12.2 代码组织

- **目录结构清晰**，按第六节的分层
- **中文注释**（项目作者是中文使用者）
- 关键算法（如载荷编解码）**必须有注释说明字节布局**

### 12.3 不要做的事

- ❌ 不要一次性生成整个项目然后说"完成"
- ❌ 不要跳过 P0 的自检工具直接做 UI
- ❌ 不要在代码里留 `TODO: 稍后验证` 就交付
- ❌ 不要为了让代码"看起来能跑"而吞掉异常（`catch { }`）
- ❌ 不要猜测本文档没有的 UUID 或格式

---

## 附录 A · 快速参考：所有 UUID

> **注意：这些 UUID 是"ASCII 字符串按字节反序"生成的，所以看起来像随机值。**
> **必须原样复制，不要试图自己推导规律。**

### 服务

```
fan_svc     00000000-0000-0000-0063-76735f6e6166
light_svc   00000000-0000-0063-7673-5f746867696c
cfg_svc     00000000-0000-0000-0063-76735f676663
laptop_svc  00000000-0000-6376-735f-706f7470616c
```

### `fan_svc` 特征

```
fan_mode    00000000-0000-0000-6564-6f6d5f6e6166
fan_pwm     00000000-0000-0000-006d-77705f6e6166
fan_rpm     00000000-0000-0000-006d-70725f6e6166
```

### `light_svc` 特征

```
light_mode            00000000-0000-6564-6f6d-5f746867696c
light_update_per      7265705f-6574-6164-7075-5f746867696c
light_brightness      7373656e-7468-6769-7262-5f746867696c
```

### `cfg_svc` 特征

```
cfg_lt_static         00000063-6974-6174-735f-746c5f676663
cfg_lt_rainbow        0000776f-626e-6961-725f-746c5f676663
cfg_lt_breath         00000068-7461-6572-625f-746c5f676663
cfg_lt_randflash      6873616c-6664-6e61-725f-746c5f676663
cfg_lt_blink          00000000-6b6e-696c-625f-746c5f676663
cfg_lt_custbreat      74616572-6274-7375-635f-746c5f676663
cfg_lt_chase          00000000-6573-6168-635f-746c5f676663
cfg_lt_round          00000000-646e-756f-725f-746c5f676663
```

### `laptop_svc` 特征

```
laptop_cpu_usage      65676173-755f-7570-635f-706f7470616c
laptop_cpu_temp       00706d65-745f-7570-635f-706f7470616c
laptop_cpu_volta      61746c6f-765f-7570-635f-706f7470616c
laptop_cpu_clock      6b636f6c-635f-7570-635f-706f7470616c
laptop_gpu_usage      65676173-755f-7570-675f-706f7470616c
laptop_gpu_temp       00706d65-745f-7570-675f-706f7470616c
laptop_gpu_clock      6b636f6c-635f-7570-675f-706f7470616c
laptop_gpu_ram        00006d61-725f-7570-675f-706f7470616c
laptop_gpu_tot_r      725f746f-745f-7570-675f-706f7470616c
laptop_ram            00000000-0000-6d61-725f-706f7470616c
laptop_tot_ram        00006d61-725f-746f-745f-706f7470616c
laptop_fan_used       00646573-755f-6e61-665f-706f7470616c
laptop_fan_speed      64656570-735f-6e61-665f-706f7470616c
```

---

## 附录 B · 参考资料

### 固件源码位置

```
G:\ESP32\LaptopCooler
├── main\.Include\Tasks\gatt_svc.h    ← 服务表、UUID、权限（最权威）
├── main\Tasks\gatt_svc.c             ← 载荷格式、读写实现（最权威）
├── main\.Include\Data\laptop_sys.h   ← laptop_svc 的数据类型
└── main\.Include\ModuleDriver\config_save.h  ← 配置持久化
```

**遇到本文档和源码不一致时，以源码为准，并报告差异。**

### 已经可用的调试工具（Python）

```
G:\ESP32\LaptopCooler\agent\tools\cooler_client.py
```

一个基于 `bleak` 的命令行 GATT 客户端，可以：

```
python agent\tools\cooler_client.py
> r <序号|别名>      读
> w <序号|别名> <hex> 写
> n <序号|别名>      订阅通知
> info / l / q
```

**在写 C# 之前，可以先用它对每个特征做一遍验证** —— 它的输出就是"期望结果"的基准。

### 旧 C# 项目

```
G:\Csharp\SmartFan\SmartFan
```

**参考其 UI / 主题 / 设置 / 硬件监控 + 8.2 列的 5 个 BLE 工程模式，不要沿用它的 BLE 层。**

#### ⚠️ 打开旧项目前请注意：它的 `README.md` 已经过时

| README 说 | 代码实际是 |
|---|---|
| 默认 UUID = `0000fff0-...` / `0000fff1-...` | `a1f00000-2c3d-4e5f-8a9b-001122334455` / `a1f00001-...`（`Models\AppSettings.cs:7,9`） |
| 协议 = ASCII 文本 `T:65.3;G:58.1;F:72` | **13 字节二进制**（`Services\BleSmartFanClient.cs:149-161`） |
| 单服务 + 单写特征 | 确实如此 —— **这正是要重构的原因** |

**别被 README 带偏。**

不过 README 里 **"从 Java 转到 C# 时最重要的差异"** 那 10 节（`:66-229`）写得不错，作者就是从 Java 过来的。
**新项目的文档可以保留这一节的精神** —— 继续用 Java 概念类比解释 C# 写法。

#### 旧项目的技术参数（供对照）

| 项 | 值 |
|---|---|
| 目标框架 | `net10.0-windows10.0.19041.0` |
| 输出类型 | `WinExe`（WPF） |
| NuGet 依赖 | **只有 `LibreHardwareMonitorLib 0.9.6`** |
| BLE 包 | **无**（靠 TFM 带出的 WinRT 投影） |
| UI 库 | **无**（全套手写样式） |
| 源文件数 | 26 |
| 最大文件 | `MainWindow.xaml.cs`（936 行） |

#### 关于 `.idea/` 目录

旧项目把 Rider 的 `.idea/` 提交进了仓库（`.gitignore` 只有 5 行）。
**新项目请补上 `.gitignore`。**

---

**文档结束。**

**如果本文档有任何不清楚、自相矛盾、或与源码不符的地方，请先提问，不要自行假设。**
