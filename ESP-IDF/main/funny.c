/**
 * @file funny.c
 * @author LG_frir
 * @date 2026-10-14
 * @brief 板载 RGB 灯（单颗 WS2812）的"玩灯"效果实现。
 *
 * 板载那颗多色灯是一颗单总线可寻址 LED（WS2812 兼容，GRB 顺序），
 * 由 Basic/gpio.h 里的 ONBOARD_RGB_PIN 驱动。这里用 ESP-IDF 官方
 * led_strip 组件的 RMT 后端发送数据：由硬件产生时序，不受 CPU 主频/
 * 中断影响，比 GPIO bit-bang 稳定。
 *
 * WS2812 本身没有任何"灯效"能力，只会显示你当前写进去的那一个颜色，
 * 所有效果都是软件算好再刷新下去的。light_mode.c 里的 RANDOM FLASH
 * 也是这个套路：esp_random() 随机取色 + 标志位交替亮灭。
 *
 * 注意：只驱动板载那一颗灯，和风扇的 8 颗 ARGB（GPIO4，bit-bang）互不影响。
 */

#include "funny.h"

#include "Basic/gpio.h"        /* ONBOARD_RGB_PIN */
#include "Tasks/light_task.h"  /* color_t */

#include "led_strip.h"
#include "esp_random.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define TAG "FUNNY"

/*
 * @brief 创建板载灯的 led_strip 设备（RMT 后端），失败返回 NULL。
 */
static led_strip_handle_t onboard_rgb_strip_new(void)
{
    led_strip_config_t strip_cfg = {
        .strip_gpio_num = ONBOARD_RGB_PIN,   /* 板载灯数据脚 */
        .max_leds = ONBOARD_RGB_LED_NUM,     /* 板载只有 1 颗灯珠 */
        .led_model = LED_MODEL_WS2812,
        .color_component_format = LED_STRIP_COLOR_COMPONENT_FMT_GRB,
        .flags = {
            .invert_out = false,
        },
    };
    led_strip_rmt_config_t rmt_cfg = {
        .clk_src = RMT_CLK_SRC_DEFAULT,
        .resolution_hz = ONBOARD_RGB_RMT_RES_HZ,
        .mem_block_symbols = 0,              /* 0 = 由驱动自动选择 */
        .flags = {
            .with_dma = false,               /* 只有一颗灯珠，无需 DMA */
        },
    };

    led_strip_handle_t strip = NULL;
    esp_err_t err = led_strip_new_rmt_device(&strip_cfg, &rmt_cfg, &strip);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "onboard rgb init failed: %s", esp_err_to_name(err));
        return NULL;
    }

    /* 上电先熄灭，避免残留颜色。 */
    ESP_ERROR_CHECK(led_strip_clear(strip));
    ESP_LOGI(TAG, "onboard rgb ready, gpio=%d", ONBOARD_RGB_PIN);
    return strip;
}

/* ---------- 随机取色：让"疯狂闪"真的闪出颜色 ----------
 * 为什么不直接 esp_random() 拆三个通道：
 *   R/G/B 各自独立 0-255 均匀随机时，每个通道的期望值都是 128，
 *   合成色永远落在灰色对角线（#808080）附近；而且"三通道同时偏高"
 *   的概率极大，"纯色"（一个通道高、另两个接近 0）只占很小一块。
 *   所以那种写法闪出来是一大片白光在明灭，看不出颜色。
 *
 * 正确做法是在 HSV 空间取色：
 *   H 色相全随机   -> 决定这次是红/黄/绿/青/蓝/品红；
 *   S 饱和度拉满   -> RGB 里至少有一个通道是 0，永远出不来白色；
 *   V 亮度取高段   -> HSV 里的 V 就是 RGB 里最大的那个通道，
 *                     所以 V 的下限就是"最亮通道"的下限 —— 这就是不暗淡的保证
 *                     （V=200 意味着每一帧至少有一个通道在 200/255 以上）。
 * 另外记住上一次的色相，每次至少跳 90 度，避免连着两帧颜色差不多。
 *
 * 最后还要补偿一次感知亮度：人眼对蓝色极不敏感，
 * 纯蓝 (0,0,255) 的感知亮度只有 25/255，跟灭了差不多；
 * 而纯黄 (255,255,0) 有 229。所以算出 RGB 后用
 *   Y ≈ (3R + 6G + B) / 10        （0.299/0.587/0.114 的整数近似）
 * 估一下亮度，低于 ONBOARD_RGB_LUM_MIN 就往白色方向兑一点，
 * 把亮度抬到下限。这样只有蓝/紫会被提亮（变成"亮蓝/亮紫"），
 * 红绿黄青基本不动，色相不变、也不会变白。
 */

/*
 * @brief HSV -> RGB（纯整数运算，不引浮点和 math.h）。
 * @param h 色相 0-359，s 饱和度 0-255，v 亮度 0-255
 */
static color_t onboard_rgb_hsv2rgb(uint16_t h, uint8_t s, uint8_t v)
{
    uint8_t region = (uint8_t)(h / 60);   /* 落在哪个 60 度扇区：0-5 */
    uint8_t rem    = (uint8_t)(h % 60);   /* 扇区内偏移：0-59 */

    /* 三个关键电平：s=255 时 p=0，也就是那个"一定为 0"的通道；
     * q 和 t 是这个扇区里一降一升的两个过渡值。 */
    uint8_t p = (uint8_t)((uint16_t)v * (255 - s) / 255);
    uint8_t q = (uint8_t)((uint16_t)v * (255 - (uint16_t)s * rem / 60) / 255);
    uint8_t t = (uint8_t)((uint16_t)v * (255 - (uint16_t)s * (60 - rem) / 60) / 255);

    color_t c;
    switch (region) {
    case 0:  c.r = v; c.g = t; c.b = p; break;   /*   0-60  红 -> 黄 */
    case 1:  c.r = q; c.g = v; c.b = p; break;   /*  60-120 黄 -> 绿 */
    case 2:  c.r = p; c.g = v; c.b = t; break;   /* 120-180 绿 -> 青 */
    case 3:  c.r = p; c.g = q; c.b = v; break;   /* 180-240 青 -> 蓝 */
    case 4:  c.r = t; c.g = p; c.b = v; break;   /* 240-300 蓝 -> 品红 */
    default: c.r = v; c.g = p; c.b = q; break;   /* 300-360 品红 -> 红 */
    }
    return c;
}

/*
 * @brief 生成一个又鲜艳又亮的随机颜色。
 */
static color_t onboard_rgb_rand_color(void)
{
    static uint16_t last_hue = 0;

    uint32_t rnd = esp_random();

    /* 色相：在上一次的基础上再转 90-270 度，保证两帧颜色拉得开。 */
    uint16_t hue = (uint16_t)((last_hue + ONBOARD_RGB_HUE_MIN_JUMP +
                               (rnd % (ONBOARD_RGB_HUE_MAX_JUMP -
                                       ONBOARD_RGB_HUE_MIN_JUMP + 1))) % 360);
    last_hue = hue;

    /* 亮度：200-255 的高段随机，既不会暗，又留一点强弱变化。 */
    uint8_t val = (uint8_t)(ONBOARD_RGB_VAL_MIN +
                            ((rnd >> 16) % (ONBOARD_RGB_VAL_MAX - ONBOARD_RGB_VAL_MIN + 1)));

    color_t c = onboard_rgb_hsv2rgb(hue, ONBOARD_RGB_SAT, val);

    /* 感知亮度补偿：蓝/紫天生看着暗，往白色方向兑一点把它提亮。
     * 兑多少由亮度差反推：y_new = y + (255 - y) * w / 255 = LUM_MIN。 */
    uint16_t y = (uint16_t)(3 * c.r + 6 * c.g + c.b) / 10;
    if (y < ONBOARD_RGB_LUM_MIN) {
        uint8_t w = (uint8_t)((uint16_t)(ONBOARD_RGB_LUM_MIN - y) * 255 / (255 - y));
        c.r = (uint8_t)(c.r + (255 - c.r) * w / 255);
        c.g = (uint8_t)(c.g + (255 - c.g) * w / 255);
        c.b = (uint8_t)(c.b + (255 - c.b) * w / 255);
    }

    return c;
}

/* ---------- 效果 1：渐变（柔和，约 9 秒绕完色环） ---------- */
void onboard_rgb_task(void *arg)
{
    (void)arg;

    led_strip_handle_t strip = onboard_rgb_strip_new();
    if (strip == NULL) {
        vTaskDelete(NULL);
        return;
    }

    /* 色相 0->360 循环：0=红 60=黄 120=绿 180=青 240=蓝 300=品红。
     * 想要"呼吸"效果的话，把 value 也做正弦变化即可。 */
    uint16_t hue = 0;

    while (1) {
        ESP_ERROR_CHECK(led_strip_set_pixel_hsv(strip, 0,
                                                hue,                          /* 色相 0-360 */
                                                255,                          /* 饱和度 */
                                                ONBOARD_RGB_GRAD_VALUE));     /* 亮度 0-255 */
        ESP_ERROR_CHECK(led_strip_refresh(strip));

        hue = (uint16_t)((hue + ONBOARD_RGB_GRAD_HUE_STEP) % 360);
        vTaskDelay(pdMS_TO_TICKS(ONBOARD_RGB_GRAD_STEP_MS));
    }
}

/* ---------- 效果 2：疯狂闪灯 A（照 light_mode.c 的 RANDOM FLASH 原理） ----------
 * 和你写的 light_random_flash() 一个套路：
 *   1) 随机取一个又鲜又亮的颜色（onboard_rgb_rand_color()，原理见上面那段）；
 *   2) 用一个 bool 标志让"随机色"和"全灭"交替出现 → 看上去就是在闪。
 * 区别只是这里只有 1 颗灯珠，所以不需要那个 color[8] 数组。
 */
void onboard_rgb_flash_task(void *arg)
{
    (void)arg;

    led_strip_handle_t strip = onboard_rgb_strip_new();
    if (strip == NULL) {
        vTaskDelete(NULL);
        return;
    }

    bool on = true;   /* 和 light_mode.c 里那句 ls = true 一样，第一次进来先亮 */

    while (1) {
        if (on) {
            color_t c = onboard_rgb_rand_color();
            ESP_ERROR_CHECK(led_strip_set_pixel(strip, 0, c.r, c.g, c.b));
        } else {
            ESP_ERROR_CHECK(led_strip_clear(strip));
        }
        ESP_ERROR_CHECK(led_strip_refresh(strip));

        on = !on;
        vTaskDelay(pdMS_TO_TICKS(ONBOARD_RGB_FLASH_STEP_MS));
    }
}

/* ---------- 效果 3：疯狂闪灯 B（更乱：颜色、亮灭、间隔全随机） ----------
 * 和 A 的差别：不固定交替，每一步都掷骰子决定亮还是灭，
 * 延时也取随机数（10~80ms），节奏忽快忽慢，看着像接触不良。
 */
void onboard_rgb_rage_task(void *arg)
{
    (void)arg;

    led_strip_handle_t strip = onboard_rgb_strip_new();
    if (strip == NULL) {
        vTaskDelete(NULL);
        return;
    }

    while (1) {
        uint32_t rnd = esp_random();

        /* 最高位当骰子：一半概率亮鲜艳随机色，一半概率全灭 */
        if (rnd & 0x80000000) {
            color_t c = onboard_rgb_rand_color();
            ESP_ERROR_CHECK(led_strip_set_pixel(strip, 0, c.r, c.g, c.b));
        } else {
            ESP_ERROR_CHECK(led_strip_clear(strip));
        }
        ESP_ERROR_CHECK(led_strip_refresh(strip));

        /* 随机间隔 10~80ms（另一份随机数，别和颜色的随机串在一起） */
        vTaskDelay(pdMS_TO_TICKS(ONBOARD_RGB_RAGE_MIN_MS +
                                 ((rnd >> 24) %
                                  (ONBOARD_RGB_RAGE_MAX_MS - ONBOARD_RGB_RAGE_MIN_MS + 1))));
    }
}
