#include ".Include/oled.h"
#include ".Include/i2c_bus.h"
#include ".Include/oled_font.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "driver/i2c_master.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char* TAG = "SH1106";
static i2c_master_dev_handle_t s_dev = NULL;

#define SH1106_X_OFFSET  2
#define PFB_PAGES        8
#define PFB_COLS         128
#define OLED_CHAR_GAP    1

/* ============================================================
 * 字体参数表
 * ============================================================ */

typedef struct {
    uint8_t char_h;
    uint8_t char_w;
    uint8_t pages_per_row;
} font_info_t;

static const font_info_t FONT_INFO[3] = {
    { 16, 8,  2 },  /* 0: 8x16  */
    {  8, 8,  1 },  /* 1: 8x8   */
    { 16, 16, 2 },  /* 2: 16x16 */
};

static uint8_t s_current_font = 0;
static inline const font_info_t* _fi(void) { return &FONT_INFO[s_current_font]; }

/* ============================================================
 * 像素帧缓冲（双缓冲）
 * ============================================================ */

static uint8_t s_pfb[2][PFB_PAGES][PFB_COLS];

/* ============================================================
 * 颜色模式（供旧接口使用）
 * ============================================================ */

static bool s_inverse_color      = false;
static bool s_inverse_word_color = false;

void oled_set_inv_color(bool inv)      { s_inverse_color      = inv; }
void oled_set_inv_color_word(bool inv) { s_inverse_word_color = inv; }

/* ============================================================
 * 底层 I2C
 * ============================================================ */

static esp_err_t _write_cmd(uint8_t cmd) {
    uint8_t buf[2] = { 0x00, cmd };
    return i2c_master_transmit(s_dev, buf, sizeof(buf), 100);
}

static esp_err_t _write_data(const uint8_t* data, size_t len) {
    if (!data) return ESP_ERR_INVALID_ARG;
    if (len > OLED_WIDTH) return ESP_ERR_INVALID_SIZE;
    uint8_t tx[OLED_WIDTH + 1];
    tx[0] = 0x40;
    memcpy(&tx[1], data, len);
    return i2c_master_transmit(s_dev, tx, len + 1, 100);
}

static esp_err_t _set_cursor(uint8_t x, uint8_t page) {
    uint8_t col = x + SH1106_X_OFFSET;
    esp_err_t ret;
    ret = _write_cmd(0xB0 | (page & 0x07)); if (ret != ESP_OK) return ret;
    ret = _write_cmd(0x00 | (col & 0x0F));  if (ret != ESP_OK) return ret;
    ret = _write_cmd(0x10 | ((col >> 4) & 0x0F)); return ret;
}

/* ============================================================
 * 初始化
 * ============================================================ */

static void _i2c_probe(void) {
    uint8_t buf[2] = { 0x00, 0xAE };
    esp_err_t ret = i2c_master_transmit(s_dev, buf, sizeof(buf), 200);
    if (ret == ESP_OK)
        ESP_LOGI(TAG, "[PROBE] I2C OK, addr=0x%02X", OLED_I2C_ADDR);
    else
        ESP_LOGE(TAG, "[PROBE] I2C failed: %s", esp_err_to_name(ret));
}

#define INIT_CMD(c) do { \
    esp_err_t _r = _write_cmd((uint8_t)(c)); \
    if (_r != ESP_OK) { \
        ESP_LOGE(TAG, "INIT cmd 0x%02X failed: %s", (uint8_t)(c), esp_err_to_name(_r)); \
        return _r; \
    } \
} while (0)

static esp_err_t _clear_screen_hw(void) {
    uint8_t zero[OLED_WIDTH];
    memset(zero, 0, sizeof(zero));
    for (uint8_t page = 0; page < OLED_PAGES; page++) {
        esp_err_t ret = _set_cursor(0, page); if (ret != ESP_OK) return ret;
        ret = _write_data(zero, OLED_WIDTH);  if (ret != ESP_OK) return ret;
    }
    return ESP_OK;
}

esp_err_t oled_init(void) {
    if (!i2c_bus_handle) {
        ESP_LOGE(TAG, "i2c_bus_handle is NULL");
        return ESP_ERR_INVALID_STATE;
    }

    if (!s_dev) {
        i2c_device_config_t dev_cfg = {
            .dev_addr_length = I2C_ADDR_BIT_LEN_7,
            .device_address  = OLED_I2C_ADDR,
            .scl_speed_hz    = OLED_FREQ_HZ,
        };
        esp_err_t ret = i2c_master_bus_add_device(i2c_bus_handle, &dev_cfg, &s_dev);
        if (ret != ESP_OK) {
            ESP_LOGE(TAG, "add_device failed: %s", esp_err_to_name(ret));
            return ret;
        }
        ESP_LOGI(TAG, "I2C device add OK");
    }

    vTaskDelay(pdMS_TO_TICKS(100));
    _i2c_probe();

    INIT_CMD(0xAE); INIT_CMD(0xD5); INIT_CMD(0x80);
    INIT_CMD(0xA8); INIT_CMD(0x3F); INIT_CMD(0xD3);
    INIT_CMD(0x00); INIT_CMD(0x40); INIT_CMD(0xAD);
    INIT_CMD(0x8B); INIT_CMD(0xA1); INIT_CMD(0xC8);
    INIT_CMD(0xDA); INIT_CMD(0x12); INIT_CMD(0x81);
    INIT_CMD(0x7F); INIT_CMD(0xD9); INIT_CMD(0x22);
    INIT_CMD(0xDB); INIT_CMD(0x35); INIT_CMD(0xA4);
    INIT_CMD(0xA6); INIT_CMD(0xAF);

    esp_err_t ret = _clear_screen_hw();
    if (ret != ESP_OK) return ret;

    memset(s_pfb[0], 0x00, sizeof(s_pfb[0]));
    memset(s_pfb[1], 0x00, sizeof(s_pfb[1]));

    ESP_LOGI(TAG, "SH1106 init done");
    return ESP_OK;
}

esp_err_t oled_fill_test(void) {
    uint8_t full[OLED_WIDTH];
    memset(full, 0xFF, sizeof(full));
    for (uint8_t page = 0; page < OLED_PAGES; page++) {
        esp_err_t ret = _set_cursor(0, page); if (ret != ESP_OK) return ret;
        ret = _write_data(full, OLED_WIDTH);  if (ret != ESP_OK) return ret;
    }
    return ESP_OK;
}

/* ============================================================
 * 字符渲染核心
 *
 *   _pfb_put_char_raw  : 旧接口用，受 s_inverse_color 控制
 *   _pfb_put_char_word : 受 s_inverse_word_color 控制，字符格宽固定
 *   _pfb_put_char_adv  : oled_print_advanced 专用，mode/fixed_width 显式传入
 * ============================================================ */

/* ---------- 取字模公共信息 ---------- */
typedef struct {
    const uint8_t* data;
    uint8_t        w;   /* 字模实际像素宽度 */
    uint8_t        ppr; /* pages per row    */
} glyph_t;

static bool _get_glyph(char ch, glyph_t* g) {
    if (ch < ' ' || ch > '~') ch = '?';
    uint8_t idx = (uint8_t)(ch - ' ');
    switch (s_current_font) {
    case 0:
        g->w    = oled_font_8x16_width[idx];   /* 视觉宽度 */
        g->data = oled_font_8x16_data[idx];
        g->ppr  = 2;
        break;
    case 1:
        g->w    = oled_font_8x8_width[idx];    /* 视觉宽度 */
        g->data = oled_font_8x8_data[idx];
        g->ppr  = 1;
        break;
    case 2:
        g->w    = oled_font_16x16_width[idx];  /* 视觉宽度 */
        g->data = oled_font_16x16_data[idx];
        g->ppr  = 2;
        break;
    default:
        return false;
    }
    return true;
}

/* ---------- 旧接口字符渲染（受 s_inverse_color） ---------- */
static uint8_t _pfb_put_char_raw(uint8_t pixel_x, uint8_t page, char ch) {
    glyph_t g;
    if (!_get_glyph(ch, &g)) return 0;

    for (uint8_t p = 0; p < g.ppr; p++) {
        uint8_t hp = page + p;
        if (hp >= PFB_PAGES) break;

        for (uint8_t col = 0; col < g.w; col++) {
            uint16_t px = pixel_x + col;
            if (px >= PFB_COLS) break;
            uint8_t d = g.data[p * g.w + col];
            s_pfb[0][hp][px] = s_inverse_color ? ~d : d;
        }

        for (uint8_t gap = 0; gap < OLED_CHAR_GAP; gap++) {
            uint16_t px = pixel_x + g.w + gap;
            if (px >= PFB_COLS) break;
            s_pfb[0][hp][px] = s_inverse_color ? 0xFF : 0x00;
        }
    }

    return g.w + OLED_CHAR_GAP;
}

/* ---------- 字符格背景反色（受 s_inverse_word_color） ---------- */
static uint8_t _pfb_put_char_word(uint8_t pixel_x, uint8_t page, char ch) {
    const font_info_t* fi = _fi();
    glyph_t g;
    if (!_get_glyph(ch, &g)) return 0;

    uint8_t draw_w = fi->char_w;
    uint8_t bg = s_inverse_word_color ? 0xFF : 0x00;

    for (uint8_t p = 0; p < g.ppr; p++) {
        uint8_t hp = page + p;
        if (hp >= PFB_PAGES) break;

        /* 铺背景 */
        for (uint8_t col = 0; col < draw_w; col++) {
            uint16_t px = pixel_x + col;
            if (px >= PFB_COLS) break;
            s_pfb[0][hp][px] = bg;
        }

        /* 写字模 */
        for (uint8_t col = 0; col < g.w && col < draw_w; col++) {
            uint16_t px = pixel_x + col;
            if (px >= PFB_COLS) break;
            uint8_t d = g.data[p * g.w + col];
            s_pfb[0][hp][px] = s_inverse_word_color ? ~d : d;
        }
    }

    return draw_w;
}

/* ---------- advanced 专用字符渲染 ---------- */
/*
 * mode:
 *   OLED_PRINT_MODE_NORMAL   - 正常，背景 0x00，字模原色
 *   OLED_PRINT_MODE_LINE_INV - 整行反色（背景由调用层负责，此处只管字符格）
 *   OLED_PRINT_MODE_CHAR_INV - 字符格背景反色
 *
 * fixed_width:
 *   true  - 每个字符严格占 fi->char_w 像素，不加 gap
 *   false - 按字模实际宽度 + OLED_CHAR_GAP
 */
/* ============================================================
 * advanced 专用字符渲染（简化版）
 * ============================================================ */
static uint8_t _pfb_put_char_adv(uint8_t pixel_x, uint8_t hw_page,
                                  char ch, oled_print_mode_t mode, bool fixed_w) {
    const font_info_t* fi = _fi();
    glyph_t g;
    if (!_get_glyph(ch, &g)) return 0;
    /* 数据步长固定是 fi->char_w，不是 g.w */
    uint8_t data_stride = fi->char_w;
    /* 实际占用宽度：
     *   fixed_w=true  → 用视觉宽度 g.w，字符紧排，无 gap
     *   fixed_w=false → 固定格宽 fi->char_w + OLED_CHAR_GAP  */
uint8_t draw_w = fixed_w ? (g.w + 1) : (data_stride + OLED_CHAR_GAP);
    bool    inv = (mode != OLED_PRINT_MODE_NORMAL);
    uint8_t bg  = inv ? 0xFF : 0x00;
    for (uint8_t p = 0; p < g.ppr; p++) {
        uint8_t hp = hw_page + p;
        if (hp >= PFB_PAGES) break;
        /* 1. 铺背景 */
        for (uint8_t col = 0; col < draw_w; col++) {
            uint16_t px = pixel_x + col;
            if (px >= PFB_COLS) break;
            s_pfb[0][hp][px] = bg;
        }
        /* 2. 写字模，步长用 data_stride */
        for (uint8_t col = 0; col < g.w && col < draw_w; col++) {
            uint16_t px = pixel_x + col;
            if (px >= PFB_COLS) break;
            uint8_t d = g.data[p * data_stride + col];  /* 修正步长 */
            s_pfb[0][hp][px] = inv ? ~d : d;
        }
    }
    return draw_w;
}

/* ============================================================
 * oled_clear
 * ============================================================ */

static bool s_force_redraw = false;

void oled_clear(void) {
    memset(s_pfb[0], 0x00, sizeof(s_pfb[0]));
    s_force_redraw = true;
}

/* ============================================================
 * oled_print — 整行反色，坐标单位：字符列 / 字符行
 * ============================================================ */

void oled_print(uint16_t x, uint16_t y, const char* fmt, ...) {
    if (!fmt) return;

    char str[128];
    va_list args;
    va_start(args, fmt); vsnprintf(str, sizeof(str), fmt, args); va_end(args);

    const font_info_t* fi = _fi();
    uint8_t max_rows = PFB_PAGES / fi->pages_per_row;
    if (y >= max_rows) return;

    uint16_t px  = x;
    uint8_t  row = (uint8_t)y;

#define FILL_INV(r, s, e) do { \
    if (s_inverse_color) { \
        uint8_t _base = (r) * fi->pages_per_row; \
        for (uint8_t _p = 0; _p < fi->pages_per_row; _p++) { \
            uint8_t _hp = _base + _p; if (_hp >= PFB_PAGES) break; \
            for (uint16_t _c = (s); _c < (e) && _c < PFB_COLS; _c++) \
                s_pfb[0][_hp][_c] = 0xFF; \
        } \
    } \
} while (0)

    FILL_INV(row, 0, px);

    for (int i = 0; str[i]; i++) {
        if (str[i] == '\n') {
            FILL_INV(row, px, PFB_COLS);
            row++; px = x;
            if (row >= max_rows) break;
            FILL_INV(row, 0, px);
            continue;
        }
        if (px >= PFB_COLS) {
            FILL_INV(row, px, PFB_COLS);
            row++; px = x;
            if (row >= max_rows) break;
            FILL_INV(row, 0, px);
        }
        px += _pfb_put_char_raw((uint8_t)px, row * fi->pages_per_row, str[i]);
    }

    FILL_INV(row, px, PFB_COLS);
#undef FILL_INV
}

/* ============================================================
 * oled_print_default — 固定列宽打印
 * ============================================================ */

void oled_print_default(uint16_t x, uint16_t y, const char* fmt, ...) {
    if (!fmt) return;

    char str[128];
    va_list args;
    va_start(args, fmt); vsnprintf(str, sizeof(str), fmt, args); va_end(args);

    const font_info_t* fi = _fi();
    uint8_t max_rows = PFB_PAGES / fi->pages_per_row;
    if (y >= max_rows) return;

    uint16_t px    = x;
    uint8_t  row   = (uint8_t)y;
    uint8_t  cw    = fi->char_w;

#define FILL_INV(r, s, e) do { \
    if (s_inverse_color) { \
        uint8_t _base = (r) * fi->pages_per_row; \
        for (uint8_t _p = 0; _p < fi->pages_per_row; _p++) { \
            uint8_t _hp = _base + _p; if (_hp >= PFB_PAGES) break; \
            for (uint16_t _c = (s); _c < (e) && _c < PFB_COLS; _c++) \
                s_pfb[0][_hp][_c] = 0xFF; \
        } \
    } \
} while (0)

    FILL_INV(row, 0, px);

    for (int i = 0; str[i]; i++) {
        if (str[i] == '\n') {
            FILL_INV(row, px, PFB_COLS);
            row++; px = x;
            if (row >= max_rows) break;
            FILL_INV(row, 0, px);
            continue;
        }
        if (px + cw > PFB_COLS) {
            FILL_INV(row, px, PFB_COLS);
            row++; px = x;
            if (row >= max_rows) break;
            FILL_INV(row, 0, px);
        }
        _pfb_put_char_raw((uint8_t)px, row * fi->pages_per_row, str[i]);
        px += cw;
    }

    FILL_INV(row, px, PFB_COLS);
#undef FILL_INV
}

/* ============================================================
 * oled_print_word — 字符格背景反色，坐标单位：字符列 / 字符行
 * ============================================================ */

void oled_print_word(uint16_t x, uint16_t y, const char* fmt, ...) {
    if (!fmt) return;

    char str[128];
    va_list args;
    va_start(args, fmt); vsnprintf(str, sizeof(str), fmt, args); va_end(args);

    const font_info_t* fi = _fi();
    uint8_t max_rows = PFB_PAGES / fi->pages_per_row;
    if (y >= max_rows) return;

    uint16_t px  = x;
    uint8_t  row = (uint8_t)y;
    uint8_t  cw  = fi->char_w;

    for (int i = 0; str[i]; i++) {
        if (str[i] == '\n') {
            row++; px = x;
            if (row >= max_rows) break;
            continue;
        }
        if (px + cw > PFB_COLS) {
            row++; px = x;
            if (row >= max_rows) break;
        }
        _pfb_put_char_word((uint8_t)px, row * fi->pages_per_row, str[i]);
        px += cw;
    }
}
/* ============================================================
 * oled_print_advanced
 *
 *   x       : 1~128 像素（从 1 起）
 *   y       : 1~64  像素（从 1 起）
 *   mode    : NORMAL / LINE_INV / CHAR_INV
 *   fixed_w : true  每字符严格 fi->char_w 像素，无 gap
 *             false 字模实际宽度 + OLED_CHAR_GAP
 *
 *   超出屏幕宽度直接停止，不换行
 * ============================================================ */
void oled_print_advanced(uint16_t x, uint16_t y,
                         oled_print_mode_t mode,
                         bool fixed_w,
                         const char* fmt, ...) {
    if (!fmt) return;
    if (x == 0 || y == 0 || x > 128 || y > 64) return;
    char str[128];
    va_list args;
    va_start(args, fmt);
    vsnprintf(str, sizeof(str), fmt, args);
    va_end(args);
    const font_info_t* fi = _fi();
    if (!fi) return;
    uint16_t px      = (uint16_t)(x - 1);           /* 1-based → 0-based */
    uint8_t  hw_page = (uint8_t)((y - 1) / 8);      /* 像素 y → page     */
    if (hw_page >= PFB_PAGES) return;
    /* LINE_INV：把这一行 [0, px) 先填白 */
    if (mode == OLED_PRINT_MODE_LINE_INV) {
        for (uint8_t p = 0; p < fi->pages_per_row; p++) {
            uint8_t hp = hw_page + p;
            if (hp >= PFB_PAGES) break;
            for (uint16_t c = 0; c < px; c++)
                s_pfb[0][hp][c] = 0xFF;
        }
    }
    for (int i = 0; str[i]; i++) {
        if (px >= PFB_COLS) break;   /* 出屏就停，不换行 */
        if (str[i] == '\n') break;   /* 换行符也直接停   */
        uint8_t step = _pfb_put_char_adv((uint8_t)px, hw_page,
                                          str[i], mode, fixed_w);
        px += step;
    }
    /* LINE_INV：把剩余 [px, 128) 填白 */
    if (mode == OLED_PRINT_MODE_LINE_INV) {
        for (uint8_t p = 0; p < fi->pages_per_row; p++) {
            uint8_t hp = hw_page + p;
            if (hp >= PFB_PAGES) break;
            for (uint16_t c = px; c < PFB_COLS; c++)
                s_pfb[0][hp][c] = 0xFF;
        }
    }
}

/* ============================================================
 * oled_update — 差分刷新
 * ============================================================ */

void oled_set_font(int font) {
    if (font < 0 || font > 2) return;
    s_current_font = (uint8_t)font;
    s_force_redraw = true;
}

uint8_t oled_get_font(void) { return s_current_font; }

esp_err_t oled_update(void) {
    bool force = s_force_redraw;
    s_force_redraw = false;
    int changed = 0;
    esp_err_t ret;

    for (uint8_t page = 0; page < PFB_PAGES; page++) {
        if (force) {
            ret = _set_cursor(0, page);           if (ret != ESP_OK) return ret;
            ret = _write_data(s_pfb[0][page], PFB_COLS); if (ret != ESP_OK) return ret;
            memcpy(s_pfb[1][page], s_pfb[0][page], PFB_COLS);
            changed += PFB_COLS;
            continue;
        }

        uint8_t col = 0;
        while (col < PFB_COLS) {
            if (s_pfb[0][page][col] == s_pfb[1][page][col]) { col++; continue; }

            uint8_t start = col, end = col;
            while (end < PFB_COLS && s_pfb[0][page][end] != s_pfb[1][page][end]) end++;

            ret = _set_cursor(start, page);              if (ret != ESP_OK) return ret;
            ret = _write_data(&s_pfb[0][page][start], end - start); if (ret != ESP_OK) return ret;
            memcpy(&s_pfb[1][page][start], &s_pfb[0][page][start], end - start);
            changed += (end - start);
            col = end;
        }
    }

    ESP_LOGD(TAG, "oled_update: %d cols changed%s", changed, force ? " (force)" : "");
    return ESP_OK;
}

/* ============================================================
 * 绘图 API
 * ============================================================ */

void oled_draw_pixel(int16_t x, int16_t y, bool color) {
    if (x < 0 || x >= PFB_COLS || y < 0 || y >= (PFB_PAGES * 8)) return;
    uint8_t page = y / 8, bit = y % 8;
    if (color) s_pfb[0][page][x] |=  (1U << bit);
    else       s_pfb[0][page][x] &= ~(1U << bit);
}

void oled_draw_line(int16_t x0, int16_t y0, int16_t x1, int16_t y1, bool color) {
    int16_t dx = abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
    int16_t dy = -abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
    int16_t err = dx + dy;
    while (1) {
        oled_draw_pixel(x0, y0, color);
        if (x0 == x1 && y0 == y1) break;
        int16_t e2 = 2 * err;
        if (e2 >= dy) { err += dy; x0 += sx; }
        if (e2 <= dx) { err += dx; y0 += sy; }
    }
}

void oled_draw_rect(int16_t x, int16_t y, int16_t w, int16_t h, bool color) {
    if (w <= 0 || h <= 0) return;
    oled_draw_line(x,         y,         x + w - 1, y,         color);
    oled_draw_line(x,         y + h - 1, x + w - 1, y + h - 1, color);
    oled_draw_line(x,         y,         x,         y + h - 1, color);
    oled_draw_line(x + w - 1, y,         x + w - 1, y + h - 1, color);
}

void oled_fill_rect(int16_t x, int16_t y, int16_t w, int16_t h, bool color) {
    if (w <= 0 || h <= 0) return;
    for (int16_t yy = y; yy < y + h; yy++)
        for (int16_t xx = x; xx < x + w; xx++)
            oled_draw_pixel(xx, yy, color);
}

void oled_paint_curve(int16_t area_x, int16_t area_y, int16_t area_w, int16_t area_h,
                      const oled_point_t* coords, size_t count) {
    if (!coords || count == 0 || area_w <= 0 || area_h <= 0) return;

    if (count == 1) {
        oled_draw_pixel(area_x + coords[0].x, area_y + coords[0].y, true);
        return;
    }

    for (size_t i = 0; i < count - 1; i++) {
        int16_t x0 = area_x + coords[i].x,     y0 = area_y + coords[i].y;
        int16_t x1 = area_x + coords[i+1].x,   y1 = area_y + coords[i+1].y;
        int16_t dx = abs(x1-x0), sx = x0<x1?1:-1;
        int16_t dy = -abs(y1-y0), sy = y0<y1?1:-1;
        int16_t err = dx + dy;
        while (1) {
            if (x0 >= area_x && x0 < area_x + area_w &&
                y0 >= area_y && y0 < area_y + area_h)
                oled_draw_pixel(x0, y0, true);
            if (x0 == x1 && y0 == y1) break;
            int16_t e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}

/* ============================================================
 * 菜单辅助
 * ============================================================ */

void oled_print_strings(char** strs, uint8_t size, uint8_t selected) {
    for (int i = 0; i < size; i++) {
        oled_set_inv_color_word(selected == i);
        oled_print_word(0, i, strs[i]);
    }
    oled_set_inv_color_word(false);
}