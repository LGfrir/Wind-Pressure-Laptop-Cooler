/**
 * @file oled_plot.h
 * @author LG_frir
 * @date 2026-04-23 11:52:18
 * @brief OLED 简易绘图组件头文件，提供滚动/扫描两种实时曲线绘制功能。
 */

#ifndef __OLED_PLOT_H__
#define __OLED_PLOT_H__

#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * @brief 折线图更新模式枚举。
 */
typedef enum {
    /**
     * @brief 滚动模式。
     *
     * 新数据点总是出现在绘图区最右侧，旧数据整体向左平移，
     * 模拟示波器水平滚动效果。
     */
    OLED_PLOT_MODE_SCROLL = 0,

    /**
     * @brief 扫描模式。
     *
     * 写指针从左向右移动绘制数据，到达绘图区右边界后回到左侧起点覆盖旧数据，
     * 类似心电图或频谱扫描的视觉效果。
     */
    OLED_PLOT_MODE_SCAN,
} oled_plot_mode_t;

/**
 * @brief 折线图控制对象。
 *
 * 该结构体管理一个独立绘图窗口的所有状态和数据缓冲，
 * 通过 oled_plot_push() 添加数据，通过 oled_plot_draw() 渲染到 OLED 缓冲区。
 */
typedef struct {
    /* ---- 绘图区域几何参数 ---- */
    int16_t x; /**< 绘图区左上角横坐标（像素） */
    int16_t y; /**< 绘图区左上角纵坐标（像素） */
    int16_t w; /**< 绘图区宽度（像素） */
    int16_t h; /**< 绘图区高度（像素） */

    /* ---- 缩放与外观设置 ---- */
    bool auto_scale; /**< 是否启用自动缩放（根据数据动态调整纵轴范围） */
    bool draw_border; /**< 是否绘制边框（边框占用内部 1 像素） */

    /* ---- 运行模式 ---- */
    oled_plot_mode_t mode; /**< 数据更新模式（滚动或扫描） */

    /* ---- 内部数据管理 ---- */
    uint16_t capacity; /**< 缓冲区最大容量（通常等于绘图区内部宽度） */
    uint16_t count; /**< 当前已写入的有效样本数量 */
    uint16_t write_index; /**< 扫描模式下当前写入位置（滚动模式忽略） */

    float* values; /**< 数据缓冲区指针，存储原始采样值 */

    /* ---- 自动缩放窗口边界（auto_scale = true 时有效） ---- */
    float window_min; /**< 当前数据窗口的最小值 */
    float window_max; /**< 当前数据窗口的最大值 */

    /* ---- 固定缩放边界（auto_scale = false 时有效） ---- */
    float fixed_min; /**< 固定纵轴下限 */
    float fixed_max; /**< 固定纵轴上限 */

} oled_plot_t;

/**
 * @brief 初始化一个 OLED 绘图对象。
 *
 * 根据指定几何区域分配内部缓冲区，并设置绘图参数。
 *
 * @param plot         指向待初始化的绘图对象指针。
 * @param x            绘图区域左上角横坐标。
 * @param y            绘图区域左上角纵坐标。
 * @param w            绘图区域宽度（像素）。
 * @param h            绘d图区域高度（像素）。
 * @param auto_scale   是否启用自动缩放。
 * @param mode         数据更新模式（滚动或扫描）。
 * @param draw_border  是否绘制边框。
 * @param fixed_min    固定缩放时的纵轴最小值（仅当 auto_scale = false 时有效）。
 * @param fixed_max    固定缩放时的纵轴最大值（仅当 auto_scale = false 时有效）。
 *
 * @return true  初始化成功。
 * @return false 初始化失败（例如参数无效或内存不足）。
 */
bool oled_plot_init(oled_plot_t* plot,
                    int16_t x,
                    int16_t y,
                    int16_t w,
                    int16_t h,
                    bool auto_scale,
                    oled_plot_mode_t mode,
                    bool draw_border,
                    float fixed_min,
                    float fixed_max);

/**
 * @brief 释放绘图对象占用的内存资源。
 *
 * 调用后绘图对象不可再使用，除非重新初始化。
 *
 * @param plot 指向绘图对象的指针。
 */
void oled_plot_deinit(oled_plot_t* plot);

/**
 * @brief 重置绘图对象，清空所有已记录的数据点。
 *
 * 调用后内部数据计数归零，缩放窗口重置为默认值。
 *
 * @param plot 指向绘图对象的指针。
 */
void oled_plot_reset(oled_plot_t* plot);

/**
 * @brief 向绘图对象添加一个新的数据点。
 *
 * 数据会根据当前模式（滚动/扫描）存入内部缓冲区，
 * 若启用自动缩放，将自动更新纵轴显示范围。
 *
 * @param plot  指向绘图对象的指针。
 * @param value 要添加的浮点数值。
 */
void oled_plot_push(oled_plot_t* plot, float value);

/**
 * @brief 在 OLED 帧缓冲区上绘制当前所有数据点的曲线。
 *
 * @note 本函数仅更新 OLED 内部缓冲区，不会立即刷新到屏幕。
 *       调用者需要自行调用 oled_update() 来显示内容。
 *
 * @param plot 指向绘图对象的指针。
 */
void oled_plot_draw(oled_plot_t* plot);

#ifdef __cplusplus
}
#endif

#endif /* __OLED_PLOT_H__ */
