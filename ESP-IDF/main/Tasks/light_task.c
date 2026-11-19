/**
 * @file light_task.c
 * @author LG_frir
 * @date 2026-04-28
 * @brief ARGB 风扇灯光任务与执行控制
 *
 * 初始化 WS2812 驱动，调度 light_mode.c 中的灯效并刷新灯珠。
 */

#include "Tasks/light_task.h"
#include "ModuleDriver/light_mode.h"
#include "ModuleDriver/config_save.h"
#include "Basic/ws2812.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include <string.h>

/* ========================= 内部状态 ========================= */

light_t s_light;
static bool s_inited = false;
/* 保留未缩放的 RGB；驱动缓存只接收当前帧的输出颜色。 */
static color_t s_pixels[LIGHT_PIXEL_COUNT];

/*
 * @brief 获取灯效全局配置对象。
 */
light_t* light_get_cfg(void) {
    return &s_light;
}

bool light_update_period_is_valid(uint32_t period_ms) {
    switch (period_ms) {
    case 10:
    case 20:
    case 50:
    case 100:
    case 200:
        return true;
    default:
        return false;
    }
}

bool light_set_update_period(uint32_t period_ms) {
    if (!light_update_period_is_valid(period_ms)) {
        return false;
    }
    s_light.update_period = (uint8_t)period_ms;
    return true;
}

void light_set_brightness(uint8_t brightness) {
    s_light.brightness = brightness;
}

/*
 * @brief 初始化 WS2812 驱动和灯效配置。
 */
void light_init(void)
{
    if (s_inited) {
        return;
    }

    ws2812_init();
    /* ---- 默认值 ---- */
    light_set_mode(LIGHT_MODE_STATIC);
    s_light.update_period = LIGHT_DEFAULT_UPDATE_PERIOD_MS; /* 不持久化 */
    s_light.brightness = 255;

    /* ---- 有 NVS 记录就覆盖 mode / brightness（不会碰 update_period）----
     * 读不到（首次开机）时 nvs_get_light_cfg() 不改结构体，上面两个默认值原样留着。 */
    nvs_get_light_cfg(&s_light);

    light_modes_init();
    light_mode_reset(s_light.mode);
    light_clear();
    s_inited = true;
}

/*
 * @brief 设置指定灯珠的颜色。
 */
void light_set_pixel(uint8_t index, uint8_t r, uint8_t g, uint8_t b)
{
    if (index >= LIGHT_PIXEL_COUNT) {
        return;
    }
    s_pixels[index] = (color_t){r, g, b};
}

/*
 * @brief 将所有灯珠填充为指定颜色。
 */
void light_fill(uint8_t r, uint8_t g, uint8_t b)
{
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        light_set_pixel(i, r, g, b);
    }
}

/*
 * @brief 将颜色数组写入像素缓存。
 */
void light_fill_advance(const color_t* color)
{
    if (color == NULL) {
        return;
    }
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        s_pixels[i] = color[i];
    }
}

/*
 * @brief 清空所有像素缓存。
 */
void light_clear(void)
{
    memset(s_pixels, 0, sizeof(s_pixels));
}

/*
 * @brief 将像素缓存发送到灯珠。
 */
static void light_show_frame(uint8_t brightness)
{
    if (!s_inited) {
        return;
    }
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        ws2812_set_pixel(i,
                        (uint8_t)((uint16_t)s_pixels[i].r * brightness / 255U),
                        (uint8_t)((uint16_t)s_pixels[i].g * brightness / 255U),
                        (uint8_t)((uint16_t)s_pixels[i].b * brightness / 255U));
    }
    ws2812_show();
}

void light_show(void) {
    light_show_frame(s_light.brightness);
}

/*
 * @brief 灯效测试任务。
 */
void light_test_task(void* arg) {
    color_t color = {255,255,0};
    while(1) {
        light_fill(color.r, color.g, color.b);
        light_show();
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

/*
 * @brief 灯效主任务，根据当前模式刷新灯珠。
 */
void light_task(void* arg) {
    if(!s_inited) {
        light_init();
    }

    TickType_t last_wake = xTaskGetTickCount();
    light_mode_t last_mode = (light_mode_t)-1;
    uint32_t delta_ms = 0;

    while(1) {
        light_t frame_cfg = s_light;
        if (!light_update_period_is_valid(frame_cfg.update_period)) {
            frame_cfg.update_period = LIGHT_DEFAULT_UPDATE_PERIOD_MS;
        }
        if ((unsigned)frame_cfg.mode > LIGHT_MODE_ROUND) {
            frame_cfg.mode = LIGHT_MODE_OFF;
        }
        if (frame_cfg.mode != last_mode) {
            light_mode_reset(frame_cfg.mode);
            last_mode = frame_cfg.mode;
            delta_ms = 0;
        }

        switch (frame_cfg.mode) {
            case LIGHT_MODE_OFF:
                light_fill(0,0,0);
                break;
            case LIGHT_MODE_STATIC:
                light_static();
                break;
            case LIGHT_MODE_RAINBOW:
                light_rainbow(delta_ms);
                break;
            case LIGHT_MODE_BREATH:
                light_breathe(delta_ms);
                break;
            case LIGHT_MODE_RANDOM_FLASH:
                light_random_flash();
                break;
            case LIGHT_MODE_BLINK:
                light_blink(delta_ms);
                break;
            case LIGHT_MODE_CUSTOM_BREATH:
                light_custom_breath(delta_ms);
                break;
            case LIGHT_MODE_CHASE:
                light_chase(delta_ms);
                break;
            case LIGHT_MODE_ROUND:
                light_round(delta_ms);
                break;
            default:
                light_static();
                break;
        }

        light_show_frame(frame_cfg.brightness);
        xTaskDelayUntil(&last_wake, pdMS_TO_TICKS(frame_cfg.update_period));
        /* 下一帧累计本次实际安排的周期，修改周期时不重解释已有进度。 */
        delta_ms = frame_cfg.update_period;
    }
}

/*
 * @brief 设置当前灯效模式。
 */
void light_set_mode(light_mode_t mode) {
    if ((unsigned)mode > LIGHT_MODE_ROUND) {
        return;
    }
    s_light.mode = mode;
}
