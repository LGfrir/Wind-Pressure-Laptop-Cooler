/**
 * @file ws2812.c
 * @author LG_frir
 * @date 2026-04-28
 * @brief ARGB 风扇灯效驱动 - 软件 bit-bang（WS2812B / SK6812 风格）
 *
 * 数据格式：GRB，高位先发
 * 典型时序：
 *   bit 0: T0H ≈ 0.35us, T0L ≈ 0.8us
 *   bit 1: T1H ≈ 0.7us,  T1L ≈ 0.6us
 * reset: 低电平 > 50us
 *
 * 说明：
 * - 本实现通过 GPIO 寄存器直接置位/清零，减少 gpio_set_level() 调用开销。
 * - 发送期间进入临界区，尽量避免中断打断。
 * - 该方案依然依赖 CPU 主频、编译优化、芯片执行时序，稳定性不如 RMT。
 */

#include "Basic/ws2812.h"
#include "Basic/gpio.h"

#include "driver/gpio.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_rom_sys.h"
#include "freertos/FreeRTOS.h"
#include "soc/gpio_struct.h"

#include <stdbool.h>
#include <string.h>

#define TAG "WS2812"
#define WS2812_GPIO FAN_ARGB_PIN
#define WS2812_RESET_US 80
#define GPIO_MASK (1UL << WS2812_GPIO)

#define NOP() __asm__ __volatile__("nop")

#define DELAY_SHORT()   do { \
    NOP(); NOP(); NOP(); NOP(); NOP(); NOP(); NOP(); NOP(); \
    NOP(); NOP(); NOP(); NOP(); \
} while (0)

#define DELAY_LONG()    do { \
    DELAY_SHORT(); \
    DELAY_SHORT(); \
} while (0)

#define DELAY_T0H()  do { DELAY_SHORT(); NOP(); NOP(); } while (0)
#define DELAY_T0L()  do { DELAY_LONG(); } while (0)

#define DELAY_T1H()  do { DELAY_LONG(); DELAY_SHORT(); } while (0)
#define DELAY_T1L()  do { DELAY_SHORT(); } while (0)

static uint8_t s_pixels[WS2812_PIXEL_COUNT][3]; /* GRB */
static bool s_inited = false;
static portMUX_TYPE s_ws2812_mux = portMUX_INITIALIZER_UNLOCKED;

static inline void ws2812_gpio_high(void)
{
#if CONFIG_IDF_TARGET_ESP32S3
    GPIO.out_w1ts = GPIO_MASK;
#else
    GPIO.out_w1ts.val = GPIO_MASK;
#endif
}

static inline void ws2812_gpio_low(void)
{
#if CONFIG_IDF_TARGET_ESP32S3
    GPIO.out_w1tc = GPIO_MASK;
#else
    GPIO.out_w1tc.val = GPIO_MASK;
#endif
}

static inline void ws2812_send_bit_0(void)
{
    ws2812_gpio_high();
    DELAY_T0H();
    ws2812_gpio_low();
    DELAY_T0L();
}

static inline void ws2812_send_bit_1(void)
{
    ws2812_gpio_high();
    DELAY_T1H();
    ws2812_gpio_low();
    DELAY_T1L();
}

static inline void ws2812_send_byte(uint8_t data)
{
    for (int i = 0; i < 8; i++) {
        if (data & 0x80) {
            ws2812_send_bit_1();
        } else {
            ws2812_send_bit_0();
        }
        data <<= 1;
    }
}

static inline void ws2812_send_reset(void)
{
    ws2812_gpio_low();
    esp_rom_delay_us(WS2812_RESET_US);
}

void ws2812_init(void)
{
    if (s_inited) {
        return;
    }

    gpio_config_t cfg = {
        .pin_bit_mask = (1ULL << WS2812_GPIO),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    ESP_ERROR_CHECK(gpio_config(&cfg));

    ws2812_gpio_low();
    ws2812_clear();
    s_inited = true;

    ws2812_send_reset();
    ESP_LOGI(TAG, "ws2812 init done, gpio=%d, pixels=%d", WS2812_GPIO, WS2812_PIXEL_COUNT);
}

void ws2812_set_pixel(uint8_t index, uint8_t r, uint8_t g, uint8_t b)
{
    if (index >= WS2812_PIXEL_COUNT) {
        return;
    }

    s_pixels[index][0] = g;
    s_pixels[index][1] = r;
    s_pixels[index][2] = b;
}

void ws2812_fill(uint8_t r, uint8_t g, uint8_t b)
{
    for (int i = 0; i < WS2812_PIXEL_COUNT; i++) {
        s_pixels[i][0] = g;
        s_pixels[i][1] = r;
        s_pixels[i][2] = b;
    }
}

void ws2812_brightness_update(uint8_t brightness)
{
    for (int i = 0; i < WS2812_PIXEL_COUNT; i++) {
        s_pixels[i][0] = s_pixels[i][0] * brightness / 255;
        s_pixels[i][1] = s_pixels[i][1] * brightness / 255;
        s_pixels[i][2] = s_pixels[i][2] * brightness / 255;
    }
}

void ws2812_clear(void)
{
    memset(s_pixels, 0, sizeof(s_pixels));
}

void ws2812_show(void)
{
    if (!s_inited) {
        return;
    }

    portENTER_CRITICAL(&s_ws2812_mux);

    for (int i = 0; i < WS2812_PIXEL_COUNT; i++) {
        ws2812_send_byte(s_pixels[i][0]); /* G */
        ws2812_send_byte(s_pixels[i][1]); /* R */
        ws2812_send_byte(s_pixels[i][2]); /* B */
    }

    /* 把 reset 信号也放进临界区内，防止中断干扰。 */
    ws2812_send_reset();

    portEXIT_CRITICAL(&s_ws2812_mux);
}
