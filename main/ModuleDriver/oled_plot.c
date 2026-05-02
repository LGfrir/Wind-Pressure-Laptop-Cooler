/**
 * @file oled_plot.c
 * @author LG_frir
 * @date 2026-04-23 11:52:34
 * @brief OLED 简易绘图组件实现，提供滚动/扫描两种实时曲线绘制功能。
 */

#include ".Include/oled_plot.h"
#include ".Include/oled.h"

#include <stdlib.h>
#include <string.h>

/**
 * @brief 内部坐标点类型，需与 oled.h 中定义一致。
 */
typedef struct {
    int16_t x; /**< 横坐标 */
    int16_t y; /**< 纵坐标 */
} oled_point_t_internal;

/**
 * @brief 将浮点数限制在指定范围内。
 * @param v 输入值。
 * @param vmin 最小值边界。
 * @param vmax 最大值边界。
 * @return 限制后的值，保证在 [vmin, vmax] 区间内。
 */
static float _clampf(float v, float vmin, float vmax) {
    if (v < vmin)
        return vmin;
    if (v > vmax)
        return vmax;
    return v;
}

/**
 * @brief 获取扣除边框后的实际绘图区域。
 * @param plot 绘图对象指针。
 * @param[out] x 实际绘图区起始横坐标。
 * @param[out] y 实际绘图区起始纵坐标。
 * @param[out] w 实际绘图区宽度。
 * @param[out] h 实际绘图区高度。
 */
static void _plot_get_inner_rect(const oled_plot_t* plot, int16_t* x, int16_t* y, int16_t* w, int16_t* h) {
    if (plot->draw_border) {
        *x = plot->x + 1;
        *y = plot->y + 1;
        *w = plot->w - 2;
        *h = plot->h - 2;
    }
    else {
        *x = plot->x;
        *y = plot->y;
        *w = plot->w;
        *h = plot->h;
    }

    if (*w < 1)
        *w = 1;
    if (*h < 1)
        *h = 1;
}

/**
 * @brief 根据当前有效数据重新计算自动缩放时的窗口 min/max 值。
 * @param plot 绘图对象指针。
 */
static void _plot_recalc_minmax(oled_plot_t* plot) {
    if (!plot || !plot->values || plot->count == 0) {
        if (plot) {
            plot->window_min = 0.0f;
            plot->window_max = 1.0f;
        }
        return;
    }

    float vmin = 0.0f;
    float vmax = 0.0f;
    bool first = true;

    if (plot->mode == OLED_PLOT_MODE_SCROLL) {
        for (uint16_t i = 0; i < plot->count; i++) {
            float v = plot->values[i];
            if (first) {
                vmin = vmax = v;
                first = false;
            }
            else {
                if (v < vmin)
                    vmin = v;
                if (v > vmax)
                    vmax = v;
            }
        }
    }
    else {
        /* SCAN 模式：同样只统计已写入有效点 */
        for (uint16_t i = 0; i < plot->count; i++) {
            float v = plot->values[i];
            if (first) {
                vmin = vmax = v;
                first = false;
            }
            else {
                if (v < vmin)
                    vmin = v;
                if (v > vmax)
                    vmax = v;
            }
        }
    }

    if (first) {
        vmin = 0.0f;
        vmax = 1.0f;
    }

    /* 若所有值相同，给一点范围，避免除零 */
    if ((vmax - vmin) < 0.000001f) {
        float pad = (vmax == 0.0f) ? 0.5f : (vmax * 0.1f);
        vmin -= pad;
        vmax += pad;
    }

    plot->window_min = vmin;
    plot->window_max = vmax;
}

/**
 * @brief 初始化一个 OLED 绘图对象。
 * @param plot 指向绘图对象的指针。
 * @param x 绘图区域起始横坐标。
 * @param y 绘图区域起始纵坐标。
 * @param w 绘图区域宽度（像素）。
 * @param h 绘图区域高度（像素）。
 * @param auto_scale 是否启用自动缩放（根据数据动态调整纵轴范围）。
 * @param mode 绘图模式：滚动（SCROLL）或扫描（SCAN）。
 * @param draw_border 是否绘制边框。
 * @param fixed_min 固定缩放模式下的最小值边界（仅当 auto_scale 为 false 时有效）。
 * @param fixed_max 固定缩放模式下的最大值边界（仅当 auto_scale 为 false 时有效）。
 * @return 初始化成功返回 true，失败返回 false。
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
                    float fixed_max) {
    if (!plot || w <= 0 || h <= 0) {
        return false;
    }

    memset(plot, 0, sizeof(*plot));

    plot->x = x;
    plot->y = y;
    plot->w = w;
    plot->h = h;
    plot->auto_scale = auto_scale;
    plot->mode = mode;
    plot->draw_border = draw_border;
    plot->fixed_min = fixed_min;
    plot->fixed_max = fixed_max;

    if (!auto_scale) {
        if (fixed_max <= fixed_min) {
            plot->fixed_min = 0.0f;
            plot->fixed_max = 1.0f;
        }
    }

    int16_t inner_x, inner_y, inner_w, inner_h;
    _plot_get_inner_rect(plot, &inner_x, &inner_y, &inner_w, &inner_h);

    plot->capacity = (uint16_t)inner_w;
    if (plot->capacity == 0) {
        return false;
    }

    plot->values = (float*)calloc(plot->capacity, sizeof(float));
    if (!plot->values) {
        return false;
    }

    plot->count = 0;
    plot->write_index = 0;
    plot->window_min = 0.0f;
    plot->window_max = 1.0f;

    return true;
}

/**
 * @brief 释放绘图对象占用的内存资源。
 * @param plot 指向绘图对象的指针。
 */
void oled_plot_deinit(oled_plot_t* plot) {
    if (!plot)
        return;

    if (plot->values) {
        free(plot->values);
        plot->values = NULL;
    }

    plot->capacity = 0;
    plot->count = 0;
    plot->write_index = 0;
}

/**
 * @brief 重置绘图对象，清空所有已记录的数据点。
 * @param plot 指向绘图对象的指针。
 */
void oled_plot_reset(oled_plot_t* plot) {
    if (!plot || !plot->values || plot->capacity == 0) {
        return;
    }

    memset(plot->values, 0, sizeof(float) * plot->capacity);
    plot->count = 0;
    plot->write_index = 0;
    plot->window_min = 0.0f;
    plot->window_max = 1.0f;
}

/**
 * @brief 向绘图对象添加一个新的数据点。
 * @param plot 指向绘图对象的指针。
 * @param value 要添加的浮点数值。
 */
void oled_plot_push(oled_plot_t* plot, float value) {
    if (!plot || !plot->values || plot->capacity == 0) {
        return;
    }

    if (!plot->auto_scale) {
        value = _clampf(value, plot->fixed_min, plot->fixed_max);
    }

    if (plot->mode == OLED_PLOT_MODE_SCROLL) {
        if (plot->count < plot->capacity) {
            plot->values[plot->count++] = value;
        }
        else {
            memmove(&plot->values[0], &plot->values[1], sizeof(float) * (plot->capacity - 1));
            plot->values[plot->capacity - 1] = value;
        }
    }
    else { /* OLED_PLOT_MODE_SCAN */
        plot->values[plot->write_index] = value;

        if (plot->count < plot->capacity) {
            plot->count++;
        }

        plot->write_index++;
        if (plot->write_index >= plot->capacity) {
            plot->write_index = 0;
        }
    }

    if (plot->auto_scale) {
        _plot_recalc_minmax(plot);
    }
}

/**
 * @brief 将数值映射为 OLED 屏幕上的纵坐标（Y 轴像素位置）。
 * @param plot 绘图对象指针。
 * @param value 要映射的浮点数值。
 * @param inner_h 实际绘图区高度（像素）。
 * @return 对应的 Y 轴像素坐标（0 为顶部，inner_h-1 为底部）。
 */
static int16_t _plot_value_to_y(const oled_plot_t* plot, float value, int16_t inner_h) {
    float vmin = plot->auto_scale ? plot->window_min : plot->fixed_min;
    float vmax = plot->auto_scale ? plot->window_max : plot->fixed_max;

    if ((vmax - vmin) < 0.000001f) {
        return inner_h / 2;
    }

    float norm = (value - vmin) / (vmax - vmin);
    norm = _clampf(norm, 0.0f, 1.0f);

    int16_t y = (int16_t)((inner_h - 1) - (norm * (inner_h - 1)));
    if (y < 0)
        y = 0;
    if (y >= inner_h)
        y = inner_h - 1;

    return y;
}

/**
 * @brief 在 OLED 上绘制当前所有数据点的曲线。
 * @param plot 指向绘图对象的指针。
 */
void oled_plot_draw(oled_plot_t* plot) {
    if (!plot || !plot->values || plot->capacity == 0) {
        return;
    }

    int16_t inner_x, inner_y, inner_w, inner_h;
    _plot_get_inner_rect(plot, &inner_x, &inner_y, &inner_w, &inner_h);

    /* 清空绘图区域背景 */
    oled_fill_rect(plot->x, plot->y, plot->w, plot->h, false);

    /* 绘制边框（若启用且尺寸足够） */
    if (plot->draw_border && plot->w >= 3 && plot->h >= 3) {
        oled_draw_rect(plot->x + 1, plot->y + 1, plot->w - 2, plot->h - 2, true);
    }

    if (plot->count == 0) {
        return;
    }

    oled_point_t* pts = (oled_point_t*)calloc(inner_w, sizeof(oled_point_t));
    if (!pts) {
        return;
    }

    if (plot->mode == OLED_PLOT_MODE_SCROLL) {
        uint16_t draw_count = 0;

        for (uint16_t i = 0; i < plot->count && i < inner_w; i++) {
            pts[i].x = i;
            pts[i].y = _plot_value_to_y(plot, plot->values[i], inner_h);
            draw_count++;
        }

        if (draw_count > 0) {
            oled_paint_curve(inner_x, inner_y, inner_w, inner_h, pts, draw_count);
        }
    }
    else if (plot->mode == OLED_PLOT_MODE_SCAN) {
        /* SCAN 模式分段绘制，避免将尾部和首部错误连接 */

        /* 第一段：[0, write_index-1] */
        if (plot->write_index > 1) {
            for (uint16_t i = 0; i < plot->write_index; i++) {
                pts[i].x = i;
                pts[i].y = _plot_value_to_y(plot, plot->values[i], inner_h);
            }
            oled_paint_curve(inner_x, inner_y, inner_w, inner_h, pts, plot->write_index);
        }

        /* 第二段：[write_index, count-1] */
        if (plot->count > plot->write_index + 1) {
            uint16_t seg_len = plot->count - plot->write_index;
            for (uint16_t i = 0; i < seg_len; i++) {
                uint16_t src = plot->write_index + i;
                pts[i].x = src;
                pts[i].y = _plot_value_to_y(plot, plot->values[src], inner_h);
            }
            oled_paint_curve(inner_x, inner_y, inner_w, inner_h, pts, seg_len);
        }
    }

    free(pts);
}
