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

#define LIGHT_MIN_PERIOD_MS 10

/* ========================= 内部状态 ========================= */

light_t s_light;
static bool s_inited = false;

/*
 * @brief 获取灯效全局配置对象。
 */
light_t* light_get_cfg(void) {
    return &s_light;
}

/*
 * @brief 按当前亮度比例更新像素缓存。
 */
void light_brightness_update(void) {
    ws2812_brightness_update(s_light.brightness);
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
    s_inited = true;

    /* ---- 默认值 ---- */
    light_set_mode(LIGHT_MODE_STATIC);
    s_light.update_period = 10;             /* 时序参数，不持久化 */
    s_light.brightness = 255;

    /* ---- 有 NVS 记录就覆盖 mode / brightness（不会碰 update_period）----
     * 读不到（首次开机）时 nvs_get_light_cfg() 不改结构体，上面两个默认值原样留着。 */
    nvs_get_light_cfg(&s_light);

    /* ---- 呼吸灯默认值（light_breathe() 的懒初始化里也有一份兜底）---- */
    breath_cfg_t* breath_cfg = light_get_breath_cfg();
    breath_cfg->switch_ms = 1000;
    breath_cfg->color.r = 147;
    breath_cfg->color.g = 255;
    breath_cfg->color.b = 0;
}

/*
 * @brief 设置指定灯珠的颜色。
 */
void light_set_pixel(uint8_t index, uint8_t r, uint8_t g, uint8_t b)
{
    ws2812_set_pixel(index, r, g, b);
}

/*
 * @brief 将所有灯珠填充为指定颜色。
 */
void light_fill(uint8_t r, uint8_t g, uint8_t b)
{
    ws2812_fill(r, g, b);
}

/*
 * @brief 将颜色数组写入像素缓存。
 */
void light_fill_advance(color_t* color)
{
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        ws2812_set_pixel(i, color[i].r, color[i].g, color[i].b);
    }
}

/*
 * @brief 清空所有像素缓存。
 */
void light_clear(void)
{
    ws2812_clear();
}

/*
 * @brief 将像素缓存发送到灯珠。
 */
void light_show(void)
{
    if (!s_inited) {
        return;
    }
    ws2812_brightness_update(s_light.brightness);
    ws2812_show();
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

    while(1) {
        switch (s_light.mode)
        {
        case LIGHT_MODE_OFF:
            light_fill(0,0,0);
            break;
        case LIGHT_MODE_STATIC:
            light_static();
            break;
        case LIGHT_MODE_RAINBOW:
            light_rainbow();
            break;
        case LIGHT_MODE_BREATH:
            light_breathe();
            break;
        case LIGHT_MODE_RANDOM_FLASH:
            light_random_flash();
            break;
        case LIGHT_MODE_BLINK:
            light_blink();
            break;
        case LIGHT_MODE_CUSTOM_BREATH:
            light_custom_breath();
            break;
        case LIGHT_MODE_CHASE:
            light_chase();
            break;
        case LIGHT_MODE_ROUND:
            light_round();
            break;
        default:
            light_static();
            break;
        }

        light_show();
        vTaskDelay(pdMS_TO_TICKS(s_light.update_period));
    }
}

/*
 * @brief 设置当前灯效模式。
 */
void light_set_mode(light_mode_t mode) {
    s_light.mode = mode;
}
