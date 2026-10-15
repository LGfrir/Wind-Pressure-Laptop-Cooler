/**
 * @file gatt_svc.c
 * @author LG_frir
 * @date 2026-10-09 13:35:41
 */

#include "Tasks/gatt_svc.h"
#include "Tasks/light_task.h"
#include "ModuleDriver/fan.h"
#include "ModuleDriver/light_mode.h"
#include "ModuleDriver/config_save.h"

#include <string.h>
#include "esp_log.h"


/* ============================================================
 *  各服务共用的收发小工具
 * ============================================================ */

/*
 * @brief 收一个【定长】包：长度必须精确等于 expect，再整块拷出来。
 *
 *  不做"尽量解析"：长度对不上就直接拒掉，免得把半截数据写进配置。
 *
 * @return 0 成功，否则是 ATT 错误码
 */
static int pkt_in(struct ble_gatt_access_ctxt *ctxt, void *dst, uint16_t expect)
{
    if (OS_MBUF_PKTLEN(ctxt->om) != expect) {
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }
    if (os_mbuf_copydata(ctxt->om, 0, expect, dst) != 0) {
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }
    return 0;
}

/*
 * @brief 发一个包：把 len 字节追加到读响应里。
 */
static int pkt_out(struct ble_gatt_access_ctxt *ctxt, const void *src, uint16_t len)
{
    int rc = os_mbuf_append(ctxt->om, src, len);
    return rc == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
}

/*
 * @brief 收一个【带颜色数组】的包：首字节是 size，整包长度 = 1 + per_color * size。
 *
 * @param per_color 每个颜色连同它自己的参数占几字节
 *                  （流光 = 3，自定义呼吸 = 3 + 2）
 * @param size      输出：颜色个数
 */
static int pkt_size_in(struct ble_gatt_access_ctxt *ctxt, uint8_t per_color, uint8_t *size)
{
    uint8_t n = 0;

    if (OS_MBUF_PKTLEN(ctxt->om) < 1) {
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }
    if (os_mbuf_copydata(ctxt->om, 0, 1, &n) != 0) {
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }
    if (n == 0 || n > LIGHT_CFG_MAX_COLORS) {
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }
    if (OS_MBUF_PKTLEN(ctxt->om) != (uint16_t)(1 + per_color * n)) {
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }

    *size = n;
    return 0;
}


int fan_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg) {
    intptr_t which_chr = (intptr_t)arg;
    // 读取数据
    if(ctxt->op == BLE_GATT_ACCESS_OP_READ_CHR) {
        if(which_chr == 1) // 灯模式
        {
            light_mode_t value = light_get_cfg()->mode;
            return pkt_out(ctxt, &value, sizeof(value));
        }
        else if(which_chr == 2) // PWM
        {
            uint8_t value = 0;
            return pkt_out(ctxt, &value, sizeof(value));
        }
        else if(which_chr == 3) // 转速
        {
            uint32_t value = fan_get_rpm();
            return pkt_out(ctxt, &value, sizeof(value));
        }
        else {
            return BLE_ATT_ERR_UNLIKELY;
        }
    }
    // 写
    else if(ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
        if(which_chr == 1) {
            light_mode_t value = 0;
            int err = pkt_in(ctxt, &value, sizeof(value));
            if (err != 0) {
                return err;
            }

            light_get_cfg()->mode = value;
            return 0;
        }
        else if(which_chr == 2) {
            return 0;
        }
        else if(which_chr == 3) {
            uint32_t value = 0;
            int err = pkt_in(ctxt, &value, sizeof(value));
            if (err != 0) {
                return err;
            }

            fan_set_target_rpm(value);
            return 0;
        }
        else return BLE_ATT_ERR_UNLIKELY;
    } else {
        return BLE_ATT_ERR_UNLIKELY;
    }
    return BLE_ATT_ERR_UNLIKELY;
}


/* ============================================================
 *  config_svc 的【定长包】格式
 *
 *  约定
 *  ----
 *  1. 多字节整数 / 浮点一律【小端】。ESP32 本身就是小端，所以下面按字段
 *     memcpy 就是小端，客户端按小端组包即可。
 *  2. 颜色固定 3 字节，顺序 r, g, b。
 *  3. 带颜色数组的灯效，第一个字节是 size（颜色个数），整包长度由它唯一
 *     确定；长度对不上直接拒绝。
 *  4. 【不】直接收发 light_mode.c 里那些结构体：breath_cfg_t / round_cfg_t
 *     带编译器填充（sizeof 分别是 6 / 24），整块发出去等于把填充字节也发
 *     给客户端，客户端还得知道编译器怎么排的。所以每个特征单独定义包结构，
 *     并用 _Static_assert 把字节数钉死。
 *
 *  各特征载荷（读、写同一套格式）
 *  ------------------------------------------------------------------
 *   cfg_lt_static      [r][g][b]                                      3
 *   cfg_lt_rainbow     [switch_ms u16]                                2
 *   cfg_lt_breath      [switch_ms u16][r][g][b]                       5
 *   cfg_lt_randflash   [mode u8]                                      1
 *   cfg_lt_blink       [size=2][r g b]x2[period u16]                  9
 *   cfg_lt_custbreath  [size][r g b]xN[fades_ms u16]xN            1+5N
 *   cfg_lt_chase       [size][r g b]xN                            1+3N
 *   cfg_lt_round       [main: r g b][sub: r g b][range f32]
 *                      [range_main f32][wait_ms i32][center_speed f32] 22
 *  ------------------------------------------------------------------
 *  N = size，范围 1..LIGHT_CFG_MAX_COLORS(9)；blink 固定 2。
 * ============================================================ */

/*
 * light_blink() 只用 colors[0] / colors[1]（见 ModuleDriver/light_mode.c），
 * config_save.c 里也把 BLINK_COLOR_COUNT 固定成 2，所以 BLE 侧同样固定 2。
 */
#define GATT_BLINK_COLOR_COUNT 2

typedef struct __attribute__((packed)) {
    uint16_t switch_ms;
    color_t  color;
} pkt_breath_t;

typedef struct __attribute__((packed)) {
    color_t color_main;
    color_t color_sub;
    float   range;
    float   range_main;
    int32_t wait_ms;
    float   center_speed;
} pkt_round_t;

/* 包结构一旦被编译器插了填充字节，客户端就对不上了 —— 让编译期直接报错 */
_Static_assert(sizeof(pkt_breath_t) == 5,  "breath 包必须是 5 字节");
_Static_assert(sizeof(pkt_round_t)  == 22, "round 包必须是 22 字节");

/*
 * light_svc：灯效模式 / 刷新周期 / 亮度
 *
 * arg 就是 gatt_svc.h 服务表里的 1 / 2 / 3。载荷（小端）:
 *   chr1 mode            [i32]   4     就是 light_mode_t 的数值
 *   chr2 update_period   [u8]    1     单位 ms，不能为 0
 *   chr3 brightness      [u8]    1     0..255
 */
int light_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg) {
    intptr_t which_chr = (intptr_t)arg;
    light_t *cfg = light_get_cfg();

    if (ctxt->op == BLE_GATT_ACCESS_OP_READ_CHR) {
        switch (which_chr) {
        case 1: {                                   /* 灯效模式 */
            int32_t mode = (int32_t)cfg->mode;
            return pkt_out(ctxt, &mode, sizeof(mode));
        }
        case 2:                                     /* 刷新周期(ms) */
            return pkt_out(ctxt, &cfg->update_period, sizeof(cfg->update_period));
        case 3:                                     /* 亮度 */
            return pkt_out(ctxt, &cfg->brightness, sizeof(cfg->brightness));
        default:
            return BLE_ATT_ERR_UNLIKELY;
        }
    }

    if (ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
        switch (which_chr) {
        case 1: {                                   /* 灯效模式 */
            int32_t mode = 0;
            int err = pkt_in(ctxt, &mode, sizeof(mode));
            if (err != 0) {
                return err;
            }
            if (mode < 0 || mode > (int32_t)LIGHT_MODE_ROUND) {
                return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
            }

            light_set_mode((light_mode_t)mode);
            nvs_save_light_cfg(cfg);                /* 模式要记住 */
            return 0;
        }
        case 2: {                                   /* 刷新周期(ms) */
            uint8_t period = 0;
            int err = pkt_in(ctxt, &period, sizeof(period));
            if (err != 0) {
                return err;
            }
            if (period == 0) {
                /* light_xxx() 里都要算 switch_ms / update_period，
                 * 给 0 就是除零，灯效任务直接崩，这里必须挡掉。 */
                return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
            }

            /* 时序参数：按 config_save.c 的设计【不落盘】 */
            cfg->update_period = period;
            return 0;
        }
        case 3: {                                   /* 亮度 */
            uint8_t brightness = 0;
            int err = pkt_in(ctxt, &brightness, sizeof(brightness));
            if (err != 0) {
                return err;
            }

            cfg->brightness = brightness;
            light_brightness_update();              /* 立刻反映到像素缓存，不用等下一帧 */
            nvs_save_light_cfg(cfg);
            return 0;
        }
        default:
            return BLE_ATT_ERR_UNLIKELY;
        }
    }

    return BLE_ATT_ERR_UNLIKELY;
}

/*
 * config_svc：8 个灯效各自的配置（格式见上面的表）
 *
 * arg 就是 gatt_svc.h 服务表里的 LIGHT_MODE_STATIC .. LIGHT_MODE_ROUND。
 *
 * 落盘策略：先在 NVS 里写成功，再让新值生效。
 *  - 不带指针的灯效（static / rainbow / breath / random / round）：
 *    存成功后直接写回运行中的那个结构体。
 *  - 带指针的灯效（blink / custom_breath / chase）：
 *    存成功后调 nvs_get_xxx_cfg() 让 NVS 来装填 —— cfg->colors 一定指向
 *    config_save.c 的静态缓冲区，不用管灯效的懒初始化跑没跑过，
 *    也就不会往空指针里 memcpy。
 */
int config_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg) {
    light_mode_t which = (light_mode_t)(intptr_t)arg;
    int err;

    if (ctxt->op == BLE_GATT_ACCESS_OP_READ_CHR) {
        switch (which) {
            case LIGHT_MODE_STATIC:
                return pkt_out(ctxt, light_get_static_cfg(), sizeof(color_t));

            case LIGHT_MODE_RAINBOW: {
                uint16_t switch_ms = light_rainbow_get_cfg()->switch_ms;
                return pkt_out(ctxt, &switch_ms, sizeof(switch_ms));
            }

            case LIGHT_MODE_BREATH: {
                const breath_cfg_t *cfg = light_get_breath_cfg();
                const pkt_breath_t pkt = {
                    .switch_ms = cfg->switch_ms,
                    .color     = cfg->color,
                };
                return pkt_out(ctxt, &pkt, sizeof(pkt));
            }

            case LIGHT_MODE_RANDOM_FLASH: {
                uint8_t mode = light_get_random_cfg()->mode ? 1 : 0;
                return pkt_out(ctxt, &mode, sizeof(mode));
            }

            case LIGHT_MODE_BLINK: {
                blink_cfg_t *cfg = light_get_blink_cfg();

                /* 这个灯效还没被跑过时 colors 还是 NULL，先去 NVS 取一份，别解空指针 */
                if (cfg->colors == NULL && nvs_get_blink_cfg(cfg) != ESP_OK) {
                    ESP_LOGW("GATT", "blink 配置还没初始化，读失败");
                    return BLE_ATT_ERR_UNLIKELY;
                }

                uint8_t pkt[1 + sizeof(color_t) * GATT_BLINK_COLOR_COUNT + sizeof(uint16_t)];
                pkt[0] = GATT_BLINK_COLOR_COUNT;
                memcpy(&pkt[1], cfg->colors, sizeof(color_t) * GATT_BLINK_COLOR_COUNT);
                memcpy(&pkt[1 + sizeof(color_t) * GATT_BLINK_COLOR_COUNT],
                    &cfg->period, sizeof(cfg->period));
                return pkt_out(ctxt, pkt, sizeof(pkt));
            }

            case LIGHT_MODE_CUSTOM_BREATH: {
                custom_breath_cfg_t *cfg = light_get_custom_breath_cfg();

                if ((cfg->colors == NULL || cfg->fades_ms == NULL) &&
                    nvs_get_custom_breath_cfg(cfg) != ESP_OK) {
                    ESP_LOGW("GATT", "custom_breath 配置还没初始化，读失败");
                    return BLE_ATT_ERR_UNLIKELY;
                }
                if (cfg->size == 0 || cfg->size > LIGHT_CFG_MAX_COLORS) {
                    return BLE_ATT_ERR_UNLIKELY;
                }

                uint8_t pkt[1 + (sizeof(color_t) + sizeof(uint16_t)) * LIGHT_CFG_MAX_COLORS];
                const uint16_t len = 1 + (sizeof(color_t) + sizeof(uint16_t)) * cfg->size;

                pkt[0] = cfg->size;
                memcpy(&pkt[1], cfg->colors, sizeof(color_t) * cfg->size);
                memcpy(&pkt[1 + sizeof(color_t) * cfg->size],
                    cfg->fades_ms, sizeof(uint16_t) * cfg->size);
                return pkt_out(ctxt, pkt, len);
            }

            case LIGHT_MODE_CHASE: {
                chase_cfg_t *cfg = light_get_chase_cfg();

                if (cfg->colors == NULL && nvs_get_chase_cfg(cfg) != ESP_OK) {
                    ESP_LOGW("GATT", "chase 配置还没初始化，读失败");
                    return BLE_ATT_ERR_UNLIKELY;
                }
                if (cfg->size == 0 || cfg->size > LIGHT_CFG_MAX_COLORS) {
                    return BLE_ATT_ERR_UNLIKELY;
                }

                uint8_t pkt[1 + sizeof(color_t) * LIGHT_CFG_MAX_COLORS];
                pkt[0] = cfg->size;
                memcpy(&pkt[1], cfg->colors, sizeof(color_t) * cfg->size);
                return pkt_out(ctxt, pkt, 1 + sizeof(color_t) * cfg->size);
            }

            case LIGHT_MODE_ROUND: {
                const round_cfg_t *cfg = light_get_round_cfg();
                const pkt_round_t pkt = {
                    .color_main   = cfg->color_main,
                    .color_sub    = cfg->color_sub,
                    .range        = cfg->range,
                    .range_main   = cfg->range_main,
                    .wait_ms      = cfg->wait_ms,
                    .center_speed = cfg->center_speed,
                };
                return pkt_out(ctxt, &pkt, sizeof(pkt));
            }

            default:
                return BLE_ATT_ERR_UNLIKELY;
            }
    }

    if (ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
            switch (which) {
            case LIGHT_MODE_STATIC: {
                color_t cfg;
                err = pkt_in(ctxt, &cfg, sizeof(cfg));
                if (err != 0) {
                    return err;
                }
                if (nvs_save_static_cfg(&cfg) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                *light_get_static_cfg() = cfg;
                return 0;
            }

            case LIGHT_MODE_RAINBOW: {
                rainbow_cfg_t cfg;
                err = pkt_in(ctxt, &cfg.switch_ms, sizeof(cfg.switch_ms));
                if (err != 0) {
                    return err;
                }
                if (nvs_save_rainbow_cfg(&cfg) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                *light_rainbow_get_cfg() = cfg;
                return 0;
            }

            case LIGHT_MODE_BREATH: {
                pkt_breath_t pkt;
                err = pkt_in(ctxt, &pkt, sizeof(pkt));
                if (err != 0) {
                    return err;
                }

                breath_cfg_t cfg = {
                    .switch_ms = pkt.switch_ms,
                    .color     = pkt.color,
                };
                if (nvs_save_breath_cfg(&cfg) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                *light_get_breath_cfg() = cfg;
                return 0;
            }

            case LIGHT_MODE_RANDOM_FLASH: {
                uint8_t mode = 0;
                err = pkt_in(ctxt, &mode, sizeof(mode));
                if (err != 0) {
                    return err;
                }

                random_cfg_t cfg = { .mode = (mode != 0) };
                if (nvs_save_random_cfg(&cfg) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                *light_get_random_cfg() = cfg;
                return 0;
            }

            case LIGHT_MODE_BLINK: {
                uint8_t pkt[1 + sizeof(color_t) * GATT_BLINK_COLOR_COUNT + sizeof(uint16_t)];
                err = pkt_in(ctxt, pkt, sizeof(pkt));
                if (err != 0) {
                    return err;
                }
                if (pkt[0] != GATT_BLINK_COLOR_COUNT) {
                    return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
                }

                color_t colors[GATT_BLINK_COLOR_COUNT];
                uint16_t period = 0;
                memcpy(colors, &pkt[1], sizeof(colors));
                memcpy(&period, &pkt[1 + sizeof(colors)], sizeof(period));

                blink_cfg_t tmp = { .colors = colors, .period = period };
                if (nvs_save_blink_cfg(&tmp) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                if (nvs_get_blink_cfg(light_get_blink_cfg()) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                return 0;
            }

            case LIGHT_MODE_CUSTOM_BREATH: {
                uint8_t size = 0;
                err = pkt_size_in(ctxt, sizeof(color_t) + sizeof(uint16_t), &size);
                if (err != 0) {
                    return err;
                }

                color_t colors[LIGHT_CFG_MAX_COLORS];
                uint16_t fades_ms[LIGHT_CFG_MAX_COLORS];
                if (os_mbuf_copydata(ctxt->om, 1, sizeof(color_t) * size, colors) != 0 ||
                    os_mbuf_copydata(ctxt->om, 1 + sizeof(color_t) * size,
                                    sizeof(uint16_t) * size, fades_ms) != 0) {
                    return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
                }

                custom_breath_cfg_t tmp = {
                    .colors   = colors,
                    .fades_ms = fades_ms,
                    .size     = size,
                };
                if (nvs_save_custom_breath_cfg(&tmp) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                if (nvs_get_custom_breath_cfg(light_get_custom_breath_cfg()) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                return 0;
            }

            case LIGHT_MODE_CHASE: {
                uint8_t size = 0;
                err = pkt_size_in(ctxt, sizeof(color_t), &size);
                if (err != 0) {
                    return err;
                }

                color_t colors[LIGHT_CFG_MAX_COLORS];
                if (os_mbuf_copydata(ctxt->om, 1, sizeof(color_t) * size, colors) != 0) {
                    return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
                }

                chase_cfg_t tmp = { .colors = colors, .size = size };
                if (nvs_save_chase_cfg(&tmp) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                if (nvs_get_chase_cfg(light_get_chase_cfg()) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                return 0;
            }

            case LIGHT_MODE_ROUND: {
                pkt_round_t pkt;
                err = pkt_in(ctxt, &pkt, sizeof(pkt));
                if (err != 0) {
                    return err;
                }

                round_cfg_t cfg = {
                    .color_main   = pkt.color_main,
                    .color_sub    = pkt.color_sub,
                    .range        = pkt.range,
                    .range_main   = pkt.range_main,
                    .wait_ms      = pkt.wait_ms,
                    .center_speed = pkt.center_speed,
                };
                if (nvs_save_round_cfg(&cfg) != ESP_OK) {
                    return BLE_ATT_ERR_UNLIKELY;
                }
                *light_get_round_cfg() = cfg;
                return 0;
            }

            default:
                return BLE_ATT_ERR_UNLIKELY;
            }
    }

    return BLE_ATT_ERR_UNLIKELY;
}


/*
 * laptop_sys：PC 端推上来的机器状态，13 个特征
 *
 * arg 就是 gatt_svc.h 服务表里的 LAPTOP_INFO_xxx。载荷（小端）:
 *   单个数值                [double]    8    读=当前值，写=更新
 *   FAN_USED / FAN_SPEED    [double]x5  40   5 个风扇各一个值
 *
 * 这几个特征只是缓存：写进来存在 laptop_info 里，等别的任务来取。
 */
int laptop_sys_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg) {
    laptop_info_e which = (laptop_info_e)(intptr_t)arg;
    int err;

    if(ctxt->op == BLE_GATT_ACCESS_OP_READ_CHR) {
        switch (which) {
            case LAPTOP_INFO_CPU_USAGE:
                return pkt_out(ctxt, &get_laptop_info_ptr()->cpu_usage, sizeof(double));
            case LAPTOP_INFO_CPU_TEMP:
                return pkt_out(ctxt, &get_laptop_info_ptr()->cpu_temp, sizeof(double));
            case LAPTOP_INFO_CPU_VOLTAGE:
                return pkt_out(ctxt, &get_laptop_info_ptr()->cpu_voltage, sizeof(double));
            case LAPTOP_INFO_CPU_CLOCK:
                return pkt_out(ctxt, &get_laptop_info_ptr()->cpu_clock, sizeof(double));
            case LAPTOP_INFO_GPU_USAGE:
                return pkt_out(ctxt, &get_laptop_info_ptr()->gpu_usage, sizeof(double));
            case LAPTOP_INFO_GPU_TEMP:
                return pkt_out(ctxt, &get_laptop_info_ptr()->gpu_temp, sizeof(double));
            case LAPTOP_INFO_GPU_CLOCK:
                return pkt_out(ctxt, &get_laptop_info_ptr()->gpu_clock, sizeof(double));
            case LAPTOP_INFO_GPU_RAM:
                return pkt_out(ctxt, &get_laptop_info_ptr()->gpu_ram, sizeof(double));
            case LAPTOP_INFO_GPU_TOT_RAM:
                return pkt_out(ctxt, &get_laptop_info_ptr()->gpu_tot_ram, sizeof(double));
            case LAPTOP_INFO_RAM:
                return pkt_out(ctxt, &get_laptop_info_ptr()->ram, sizeof(double));
            case LAPTOP_INFO_TOT_RAM:
                return pkt_out(ctxt, &get_laptop_info_ptr()->tot_ram, sizeof(double));
            case LAPTOP_INFO_FAN_USED:
                return pkt_out(ctxt, get_laptop_info_ptr()->fan_used,
                               sizeof(get_laptop_info_ptr()->fan_used));
            case LAPTOP_INFO_FAN_SPEED:
                return pkt_out(ctxt, get_laptop_info_ptr()->fan_speed,
                               sizeof(get_laptop_info_ptr()->fan_speed));
            default:
                return BLE_ATT_ERR_UNLIKELY;
        }
    } else if(ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
        switch (which) {
            case LAPTOP_INFO_CPU_USAGE: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->cpu_usage = val;
                return 0;
            }
            case LAPTOP_INFO_CPU_TEMP: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->cpu_temp = val;
                return 0;
            }
            case LAPTOP_INFO_CPU_VOLTAGE: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->cpu_voltage = val;
                return 0;
            }
            case LAPTOP_INFO_CPU_CLOCK: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->cpu_clock = val;
                return 0;
            }
            case LAPTOP_INFO_GPU_USAGE: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->gpu_usage = val;
                return 0;
            }
            case LAPTOP_INFO_GPU_TEMP: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->gpu_temp = val;
                return 0;
            }
            case LAPTOP_INFO_GPU_CLOCK: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->gpu_clock = val;
                return 0;
            }
            case LAPTOP_INFO_GPU_RAM: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->gpu_ram = val;
                return 0;
            }
            case LAPTOP_INFO_GPU_TOT_RAM: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->gpu_tot_ram = val;
                return 0;
            }
            case LAPTOP_INFO_RAM: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->ram = val;
                return 0;
            }
            case LAPTOP_INFO_TOT_RAM: {
                double val = 0;
                err = pkt_in(ctxt, &val, sizeof(val));
                if (err != 0) {
                    return err;
                }
                get_laptop_info_ptr()->tot_ram = val;
                return 0;
            }
            case LAPTOP_INFO_FAN_USED: {
                double buf[5];
                err = pkt_in(ctxt, buf, sizeof(buf));
                if (err != 0) {
                    return err;
                }
                memcpy(get_laptop_info_ptr()->fan_used, buf, sizeof(buf));
                return 0;
            }
            case LAPTOP_INFO_FAN_SPEED: {
                double buf[5];
                err = pkt_in(ctxt, buf, sizeof(buf));
                if (err != 0) {
                    return err;
                }
                memcpy(get_laptop_info_ptr()->fan_speed, buf, sizeof(buf));
                return 0;
            }
            default:
                return BLE_ATT_ERR_UNLIKELY;
        }
    } else {
        return BLE_ATT_ERR_UNLIKELY;
    }

    return BLE_ATT_ERR_UNLIKELY;
}