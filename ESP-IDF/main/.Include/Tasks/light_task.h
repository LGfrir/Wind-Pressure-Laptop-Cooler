/**
 * @file light_task.h
 * @author LG_frir
 * @date 2026-04-28
 * @brief ARGB 风扇灯光任务与控制接口
 */

#ifndef LIGHT_TASK_H
#define LIGHT_TASK_H

#include <stdint.h>
#include <stdbool.h>
#include "Basic/ws2812.h"

#define LIGHT_PIXEL_COUNT WS2812_PIXEL_COUNT
#define LIGHT_DEFAULT_UPDATE_PERIOD_MS 10U

typedef struct color{
    uint8_t r;
    uint8_t g;
    uint8_t b;
} color_t;

typedef enum {
    LIGHT_MODE_OFF = 0,
    LIGHT_MODE_STATIC,
    LIGHT_MODE_RAINBOW,
    LIGHT_MODE_BREATH,
    LIGHT_MODE_RANDOM_FLASH,
    LIGHT_MODE_BLINK,
    LIGHT_MODE_CUSTOM_BREATH,
    LIGHT_MODE_CHASE,
    LIGHT_MODE_ROUND
} light_mode_t;

typedef struct light{
    light_mode_t mode;
    uint8_t brightness;
    uint8_t update_period; /* ms，仅接受接口规定的刷新周期 */
}light_t;

extern light_t s_light;
void light_task(void* arg);
void light_set_mode(light_mode_t mode);
/* 仅接受 10 / 20 / 50 / 100 / 200 ms；设置失败返回 false 并保留原值。 */
bool light_update_period_is_valid(uint32_t period_ms);
bool light_set_update_period(uint32_t period_ms);
void light_set_brightness(uint8_t brightness);
void light_init(void);
void light_test_task(void* arg);

// 像素缓存和灯珠刷新接口
void light_set_pixel(uint8_t index, uint8_t r, uint8_t g, uint8_t b);
void light_fill(uint8_t r, uint8_t g, uint8_t b);
void light_fill_advance(const color_t* color);
void light_clear(void);
void light_show(void);

// 总light 控制
light_t* light_get_cfg(void);

#endif /* LIGHT_TASK_H */
