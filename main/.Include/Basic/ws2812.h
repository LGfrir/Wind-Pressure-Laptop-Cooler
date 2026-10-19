/**
 * @file ws2812.h
 * @author LG_frir
 * @date 2026-04-28
 * @brief ARGB 风扇灯效驱动 - 软件 bit-bang（WS2812B / SK6812 风格）
 */

#ifndef WS2812_H
#define WS2812_H

#include <stdint.h>

#define WS2812_PIXEL_COUNT 8

/* 配置 GPIO、清空 GRB 缓存并发送复位信号；重复调用不重复初始化。 */
void ws2812_init(void);

/* 颜色参数使用 RGB 顺序，驱动内部按 GRB 顺序存储和发送。 */
void ws2812_set_pixel(uint8_t index, uint8_t r, uint8_t g, uint8_t b);
void ws2812_fill(uint8_t r, uint8_t g, uint8_t b);

/* 清空缓存；调用 ws2812_show() 后才会更新灯珠。 */
void ws2812_clear(void);
void ws2812_show(void);

#endif /* WS2812_H */
