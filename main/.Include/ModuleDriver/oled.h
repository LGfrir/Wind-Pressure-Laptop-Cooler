/**
 * @file oled.h
 * @author LG_frir
 * @date 2026-04-23
 * @brief OLED 显示驱动头文件（适用于 SH1106 128x64，I2C 接口）。
 *
 * 提供基于 I2C 的 OLED 屏幕初始化、字符显示、图形绘制及差分刷新功能。
 */

#pragma once

#include "driver/i2c_master.h"
#include "esp_err.h"
#include <stdarg.h>
#include <stdint.h>

/* ============================================================
 * 屏幕参数（SH1106 128x64）
 * 使用 8x16 字库时布局：16 列 × 4 行
 * ============================================================ */

/** @brief OLED 屏幕宽度（像素） */
#define OLED_WIDTH 128
/** @brief OLED 屏幕高度（像素） */
#define OLED_HEIGHT 64
/** @brief OLED 显存页数（每页 8 行，64/8 = 8） */
#define OLED_PAGES 8
/** @brief 单个字符的像素宽度（默认字体） */
#define OLED_CHAR_W 8
/** @brief 单个字符的像素高度（默认字体，占用 2 页） */
#define OLED_CHAR_H 16
/** @brief 每行可容纳字符数（128/8 = 16） */
#define OLED_COLS 16
/** @brief 屏幕可容纳行数（64/16 = 4） */
#define OLED_ROWS 4
/**
 * @brief SH1106 列地址偏移量（关键参数）。
 *
 * SH1106 内部列地址从 2 开始，而 SSD1306 从 0 开始。
 * 该宏用于适配不同驱动芯片的列寻址差异。
 */
#define OLED_COL_OFFSET 2

/** @brief I2C 从机地址（通常为 0x3C 或 0x3D，取决于 SA0 引脚电平） */
#define OLED_I2C_ADDR 0x3C
/** @brief I2C 通信频率（Hz） */
#define OLED_FREQ_HZ 400000

/* ============================================================
 * 公共 API 接口
 * ============================================================ */

/**
 * @brief 探测 I2C 总线上的 OLED 设备是否存在。
 *
 * 通过发送 I2C 写命令探测设备 ACK 响应，调试用。
 */
void oled_probe(void);

/**
 * @brief 初始化 OLED 模块。
 *
 * @note 调用前需确保 I2C 总线已初始化完毕。
 * @return ESP_OK 成功，其他值表示失败。
 */
esp_err_t oled_init(void);

/**
 * @brief 清空显示缓冲区（填充空格）。
 *
 * @note 仅修改内部缓冲区，需配合 oled_update() 才会实际刷新到屏幕。
 */
void oled_clear(void);

/**
 * @brief 向显示缓冲区写入格式化字符串，支持 '\\n' 换行。
 *
 * @param x   列坐标（范围 0 ~ 15）。
 * @param y   行坐标（范围 0 ~ 3）。
 * @param fmt printf 风格的格式化字符串。
 * @param ... 可变参数列表。
 */
void oled_print(uint16_t x, uint16_t y, const char* fmt, ...);
void oled_print_default(uint16_t x, uint16_t y, const char* fmt, ...);
/**
 * @brief 显示位图图像。
 *
 * @param img    图像数据指针（字节数组，每字节代表 8 个垂直像素）。
 * @param width  图像宽度（像素）。
 * @param height 图像高度（像素）。
 */
void oled_display_image(uint8_t* img, uint16_t width, uint16_t height);

/**
 * @brief 将缓冲区中的变更部分刷新到屏幕（差分更新）。
 *
 * 仅传输自上次更新以来发生变化的页面，以提升刷新效率。
 *
 * @return ESP_OK 成功，其他值表示失败。
 */
esp_err_t oled_update(void);

/**
 * @brief 设置当前使用的字体。
 *
 * @param font 字体索引，可选值：
 *             - 0：8x16 字体
 *             - 1：8x8 字体
 *             - 2：16x16 字体
 */
void oled_set_font(int font);

/**
 * @brief 获取当前字体索引。
 *
 * @return 字体索引（0, 1, 2）。
 */
uint8_t oled_get_font(void);

/**
 * @brief 设置全局反色显示模式。
 *
 * @param inv true 表示开启反色（黑底白字），false 表示正常显示（白底黑字）。
 */
void oled_set_inv_color(bool inv);
void oled_set_inv_color_word(bool inv);

/* ============================================================
 * 图形绘制 API
 * ============================================================ */
void oled_print_word(uint16_t x, uint16_t y, const char* fmt, ...);
/**
 * @brief 坐标点结构体。
 */
typedef struct {
    int16_t x; /**< 横坐标 */
    int16_t y; /**< 纵坐标 */
} oled_point_t;
typedef enum {
    OLED_PRINT_MODE_NORMAL = 0,   /* 普通输出 */
    OLED_PRINT_MODE_LINE_INV,     /* 整行反色 */
    OLED_PRINT_MODE_CHAR_INV,     /* 仅字符背景反色 */
} oled_print_mode_t;
void oled_print_advanced(uint16_t x, uint16_t y,
                         oled_print_mode_t mode,
                         bool fixed_width,
                         const char* fmt, ...);
/**
 * @brief 绘制单个像素点。
 *
 * @param x     横坐标（0 ~ 127）。
 * @param y     纵坐标（0 ~ 63）。
 * @param color true 点亮（白色），false 熄灭（黑色）。
 */
void oled_draw_pixel(int16_t x, int16_t y, bool color);

/**
 * @brief 绘制直线（Bresenham 算法）。
 *
 * @param x0    起点横坐标。
 * @param y0    起点纵坐标。
 * @param x1    终点横坐标。
 * @param y1    终点纵坐标。
 * @param color 绘制颜色。
 */
void oled_draw_line(int16_t x0, int16_t y0, int16_t x1, int16_t y1, bool color);

/**
 * @brief 绘制矩形边框。
 *
 * @param x     矩形左上角横坐标。
 * @param y     矩形左上角纵坐标。
 * @param w     矩形宽度（像素）。
 * @param h     矩形高度（像素）。
 * @param color 边框颜色。
 */
void oled_draw_rect(int16_t x, int16_t y, int16_t w, int16_t h, bool color);

/**
 * @brief 绘制填充矩形。
 *
 * @param x     矩形左上角横坐标。
 * @param y     矩形左上角纵坐标。
 * @param w     矩形宽度（像素）。
 * @param h     矩形高度（像素）。
 * @param color 填充颜色。
 */
void oled_fill_rect(int16_t x, int16_t y, int16_t w, int16_t h, bool color);

/**
 * @brief 绘制连续曲线（折线图）。
 *
 * 将一系列点依次连接绘制，超出绘图区域的点将被裁剪。
 *
 * @param area_x 绘图区域左上角横坐标。
 * @param area_y 绘图区域左上角纵坐标。
 * @param area_w 绘图区域宽度。
 * @param area_h 绘图区域高度。
 * @param coords 坐标点数组指针。
 * @param count  坐标点数量。
 */
void oled_paint_curve(
    int16_t area_x, int16_t area_y, int16_t area_w, int16_t area_h, const oled_point_t* coords, size_t count);

void oled_print_strings(char** strs, uint8_t size, uint8_t selected);


