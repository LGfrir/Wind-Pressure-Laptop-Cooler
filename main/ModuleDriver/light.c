/**
 * @file light.c
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

#include ".Include/light.h"
#include ".Include/gpio.h"
#include "light_cfg.h"

#include "driver/gpio.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_rom_sys.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "soc/gpio_struct.h"
#include "soc/gpio_reg.h"
#include "esp_random.h"

#include <stdbool.h>
#include <stdint.h>
#include <string.h>
#include <math.h>

#define TAG "LIGHT"
#define LIGHT_MIN_PERIOD_MS 10


/* ========================= 硬件配置 ========================= */

#define LIGHT_GPIO          FAN_ARGB_PIN

/* ========================= WS2812 时序参数 ========================= */
/*
 * 以下延时不是“绝对时间”，而是基于 ESP32-C2 运行频率的经验值。
 * 如果颜色异常、闪烁、部分灯不亮，需要根据示波器波形微调。
 *
 * 目标大致：
 *   0码：高约 0.35~0.45us，低约 0.8~0.9us
 *   1码：高约 0.7~0.85us，低约 0.4~0.55us
 */

#define LIGHT_RESET_US      80

/*
 * ESP32-C2 常见主频 120MHz
 * 1 cycle ≈ 8.33ns
 *
 * 这里用一组经验 delay 宏：
 * - 一个 NOP 不等于完整延时，因为还有寄存器写、循环、分支等额外耗时
 * - 所以下面数值是“经验值”，不是数学精确值
 */
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

light_t s_light;


/* ========================= GPIO 快速操作 ========================= */
/*
 * 对 ESP32-C2，GPIO0~31 可通过 out_w1ts / out_w1tc 快速置位/清零
 */

#define GPIO_MASK   (1UL << LIGHT_GPIO)

static inline void light_gpio_high(void)
{
    GPIO.out_w1ts.val = GPIO_MASK;
}

static inline void light_gpio_low(void)
{
    GPIO.out_w1tc.val = GPIO_MASK;
}

/* ========================= 内部状态 ========================= */

static uint8_t s_pixels[LIGHT_PIXEL_COUNT][3];   /* GRB */
static bool s_inited = false;
static portMUX_TYPE s_light_mux = portMUX_INITIALIZER_UNLOCKED;

/* ========================= 底层发送 ========================= */

static inline void light_send_bit_0(void)
{
    light_gpio_high();
    DELAY_T0H();
    light_gpio_low();
    DELAY_T0L();
}

static inline void light_send_bit_1(void)
{
    light_gpio_high();
    DELAY_T1H();
    light_gpio_low();
    DELAY_T1L();
}

static inline void light_send_byte(uint8_t data)
{
    for (int i = 0; i < 8; i++) {
        if (data & 0x80) {
            light_send_bit_1();
        } else {
            light_send_bit_0();
        }
        data <<= 1;
    }
}

static inline void light_send_reset(void)
{
    light_gpio_low();
    esp_rom_delay_us(LIGHT_RESET_US);
}


uint8_t idx_next(int x) {
    return (x + 1) % LIGHT_PIXEL_COUNT;
}

uint8_t idx_past(int x) {
    return (x - 1) < 0 ? LIGHT_PIXEL_COUNT - 1 : x - 1;
}

void light_brightness_update(void) {
    for(int i = 0;i < LIGHT_PIXEL_COUNT;i++) {
        s_pixels[i][0] = s_pixels[i][0] * s_light.brightness / 255;
        s_pixels[i][1] = s_pixels[i][1] * s_light.brightness / 255;
        s_pixels[i][2] = s_pixels[i][2] * s_light.brightness / 255;
    }
}

/* ========================= 对外 API ========================= */

void light_init(void)
{
    if (s_inited) {
        return;
    }

    gpio_config_t cfg = {
        .pin_bit_mask = (1ULL << LIGHT_GPIO),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    ESP_ERROR_CHECK(gpio_config(&cfg));

    light_gpio_low();
    memset(s_pixels, 0, sizeof(s_pixels));

    s_inited = true;

    /* 上电后先发一次复位，并清屏 */
    light_send_reset();

    ESP_LOGI(TAG, "light init done, gpio=%d, pixels=%d", LIGHT_GPIO, LIGHT_PIXEL_COUNT);

    light_set_mode(LIGHT_MODE_STATIC);
    s_light.period = 10;
    s_light.brightness = 255; 
} 

void light_set_pixel(uint8_t index, uint8_t r, uint8_t g, uint8_t b)
{
    if (index >= LIGHT_PIXEL_COUNT) {
        return;
    }

    s_pixels[index][0] = g;
    s_pixels[index][1] = r;
    s_pixels[index][2] = b;
}

void light_fill(uint8_t r, uint8_t g, uint8_t b)
{
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        s_pixels[i][0] = g;
        s_pixels[i][1] = r;
        s_pixels[i][2] = b;
    }
}

void light_fill_advance(color_t* color)
{
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        s_pixels[i][0] = color[i].g;
        s_pixels[i][1] = color[i].r;
        s_pixels[i][2] = color[i].b;
    }
}


void light_clear(void)
{
    memset(s_pixels, 0, sizeof(s_pixels));
}

void light_show(void)
{
    if (!s_inited) {
        return;
    }

    portENTER_CRITICAL(&s_light_mux);

    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        light_send_byte(s_pixels[i][0]);   /* G */
        light_send_byte(s_pixels[i][1]);   /* R */
        light_send_byte(s_pixels[i][2]);   /* B */
    }

    /* 把reset信号也放进临界区内，防止中断干扰 */
    light_send_reset();

    portEXIT_CRITICAL(&s_light_mux);
}

/* ========================= 示例灯效任务 ========================= */

/* ============================================================== 

                            RAINBOW

================================================================= */

rainbow_cfg_t s_rainbow_eff;
bool s_rainbow_init;
rainbow_cfg_t* light_rainbow_get_cfg(void) {
    return &s_rainbow_eff;
}


void light_rainbow(void) {
    // init
    if(!s_rainbow_init) {
        s_rainbow_eff.switch_ms = 1000;
        s_rainbow_init = 1;
    }

    // custom config
    const int fade_steps = s_rainbow_eff.switch_ms / s_light.period;

    static const color_t colors[] = {
        {255,   0,   0},
        {  0, 255,   0},
        {  0,   0, 255},
    };
    const int color_count = sizeof(colors) / sizeof(colors[0]);


    static int current = 0;
    static int next = 1;
    static int step = 0;

    const color_t c0 = colors[current];
    const color_t c1 = colors[next];


    uint8_t r = (uint8_t)(c0.r + ((int)(c1.r - c0.r) * step) / fade_steps);
    uint8_t g = (uint8_t)(c0.g + ((int)(c1.g - c0.g) * step) / fade_steps);
    uint8_t b = (uint8_t)(c0.b + ((int)(c1.b - c0.b) * step) / fade_steps);

    light_fill(r, g, b);

    step++;
    if (step > fade_steps) {
        step = 0;
        current = next;
        next = (next + 1) % color_count;
    }

}

breath_cfg_t s_breath_cfg;
bool s_breath_init;
breath_cfg_t* light_breathe_get_cfg(void) {
    return &s_breath_cfg;
}

void light_breathe(void) {
    if(!s_breath_init) {
        s_breath_cfg.switch_ms = 1000;
        s_breath_init = true;
    }
    
    const int fade_steps = 100;


    static const color_t colors[] = {
        {147, 255, 255},
        {  0,   0,   0},
    };

    const int color_count = sizeof(colors) / sizeof(colors[0]);

    static int current = 0;
    static int next = 1;
    static int step = 0;

    const color_t c0 = colors[current];
    const color_t c1 = colors[next];

    uint8_t r = (uint8_t)(c0.r + ((int)(c1.r - c0.r) * step) / fade_steps);
    uint8_t g = (uint8_t)(c0.g + ((int)(c1.g - c0.g) * step) / fade_steps);
    uint8_t b = (uint8_t)(c0.b + ((int)(c1.b - c0.b) * step) / fade_steps);

    light_fill(r, g, b);

    step++;
    if (step > fade_steps) {
        step = 0;
        current = next;
        next = (next + 1) % color_count;
    }


}


bool s_random_flash_init;
random_cfg_t s_random_cfg;
random_cfg_t* light_get_random_cfg(void) {
    return &s_random_cfg;
}

void light_random_flash(void) {
    if(!s_random_flash_init) {
        s_random_cfg.mode = false;
        s_random_flash_init = true;
    }

    color_t color[8];
    static bool ls = 0;

    if(s_random_cfg.mode == true) {
        ls = true;
    }

    if(ls) {
        for(int i = 0;i < 8;i++) {
            uint32_t rnd = esp_random();
            color[i].r = (uint8_t)(rnd & 0xFF);
            color[i].g = (uint8_t)((rnd >> 8) & 0xFF);
            color[i].b = (uint8_t)((rnd >> 16) & 0xFF);
        }

        light_fill_advance(color);
    } else {
        light_fill(0,0,0);
    }
    ls = !ls;
}

blink_cfg_t s_blink_cfg;
bool s_blink_init;
blink_cfg_t* light_get_blink_cfg(void) {
    return &s_blink_cfg;
}


void light_blink(void) {
    if(!s_blink_init) {
        static color_t color_static[] = {
            {147,255,255},
            {0,0,0},
        };
        s_blink_cfg.colors = color_static;
        s_blink_cfg.period = 500;

        s_blink_init = true;
    }

    
    color_t* colors = s_blink_cfg.colors;


    static bool ls = 0;

    if(ls) {
        light_fill(colors[0].r, colors[0].g, colors[0].b);
    } else {
        light_fill(colors[1].r, colors[1].g, colors[1].b);
    }
    ls = !ls;
    vTaskDelay((int)s_blink_cfg.period - (int)s_light.period >= 0 ? s_blink_cfg.period - s_light.period : 0);
}


custom_breath_cfg_t s_custom_breath_cfg;
bool s_custom_breath_init;
custom_breath_cfg_t* light_get_custom_breath_cfg(void) {
    return &s_custom_breath_cfg;
}

void light_custom_breath(void) {
    if(!s_custom_breath_init) {
        static color_t colors[] = {
            {255,160,122},
            {255,250,205},
            {0,250,154},
            {0,255,255},
            {0,191,255},
            {123,104,238},
            {238,130,238},
            {138,43,226},
            {255,182,193},
        };

        static uint16_t fades_ms[] = {
            500, 500, 500, 500, 500, 500, 500, 500, 500,
        };
        s_custom_breath_cfg.colors = colors;
        s_custom_breath_cfg.fades_ms = fades_ms;
        s_custom_breath_cfg.size = sizeof(colors) / sizeof(colors[0]);
        s_custom_breath_init = true;
    }


    color_t* colors = s_custom_breath_cfg.colors;
    uint16_t* fades_ms = s_custom_breath_cfg.fades_ms;



    uint8_t color_count = s_custom_breath_cfg.size;

    static int current = 0;
    static int next = 1;
    static uint32_t step = 0;

    const color_t c0 = colors[current];
    const color_t c1 = colors[next];

    uint32_t fade_steps = fades_ms[current] / s_light.period;
    if (fade_steps == 0) {
        fade_steps = 1;
    }

    uint8_t r = (uint8_t)(c0.r + ((int)(c1.r - c0.r) * (int)step) / (int)fade_steps);
    uint8_t g = (uint8_t)(c0.g + ((int)(c1.g - c0.g) * (int)step) / (int)fade_steps);
    uint8_t b = (uint8_t)(c0.b + ((int)(c1.b - c0.b) * (int)step) / (int)fade_steps);

    light_fill(r, g, b);

    step++;
    if (step >= fade_steps) {
        step = 0;
        current = next;
        next = (next + 1) % color_count;
    }

}

chase_cfg_t s_chase_cfg;
bool s_chase_init;
chase_cfg_t* light_get_chase_cfg(void) {
    return &s_chase_cfg;
}

void light_chase(void) {
    if(!s_chase_init) {
        static color_t colors[] = {
            {255,52,241},
            {12,13,255},
            {255,52,241},
            {255,52,241},
        };
        s_chase_cfg.colors = colors;
        s_chase_cfg.size = sizeof(colors) / sizeof(colors[0]);
        
        s_chase_init = true;
    }

    const color_t* colors = s_chase_cfg.colors;


    const int color_count = s_chase_cfg.size;

    static int current = 0;
    static int next = 1;
    static uint8_t pixel_steps = 0;
    static int16_t stepping[LIGHT_PIXEL_COUNT] = {0};

    const uint32_t fade_steps = 100;
    const uint32_t update_steps = 10;

    color_t colors_update[LIGHT_PIXEL_COUNT];

    const color_t c0 = colors[current];
    const color_t c1 = colors[next];
    
    if(pixel_steps % update_steps == 0) stepping[pixel_steps / update_steps] = 1;
    for(int i = 0;i < 8;i++) {
        colors_update[i].r = (uint8_t)(c0.r + ((int) (c1.r - c0.r) * (int)stepping[i]) / (int) fade_steps);
        colors_update[i].g = (uint8_t)(c0.g + ((int) (c1.g - c0.g) * (int)stepping[i]) / (int) fade_steps);
        colors_update[i].b = (uint8_t)(c0.b + ((int) (c1.b - c0.b) * (int)stepping[i]) / (int) fade_steps);
        if(stepping[i] != fade_steps && stepping[i] != 0) stepping[i]++; 
    }
    pixel_steps++;

    light_fill_advance(colors_update);

    if (stepping[LIGHT_PIXEL_COUNT - 1] >= fade_steps) {
        current = next;
        pixel_steps = 0;
        memset(stepping, 0, sizeof stepping);
        next = (next + 1) % color_count;
    }

}



void light_round(void) {
    const color_t color_main = {255,52,241};
    const color_t color_sub = {12,13,255};

    const uint32_t PERIOD_MS = 10;
    const uint32_t update_steps = 10;


    color_t colors_update[LIGHT_PIXEL_COUNT];
    static float center_speed = 3;

    static float range = 5;
    static float range_main = 1;
    static float pos_idx = -3.0f;

    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        float dist = fabsf((float)i - pos_idx);
        if (dist <= range_main) {
            colors_update[i] = color_sub;
        }
        else if (dist < range) {
            float fades_percentage = (dist - range_main) / (range - range_main);
            colors_update[i].r = (uint8_t)(
                (float)color_sub.r + ((float)color_main.r - (float)color_sub.r) * fades_percentage
            );
            colors_update[i].g = (uint8_t)(
                (float)color_sub.g + ((float)color_main.g - (float)color_sub.g) * fades_percentage
            );
            colors_update[i].b = (uint8_t)(
                (float)color_sub.b + ((float)color_main.b - (float)color_sub.b) * fades_percentage
            );
        }
        else {
            colors_update[i] = color_main;
        }
    }

    pos_idx += center_speed * ((float)PERIOD_MS / 1000.0f);

    if (pos_idx >= (float)LIGHT_PIXEL_COUNT + 6.0f) {
        pos_idx = -6.0f;
    }

    light_fill_advance(colors_update);
}

void light_static(void) {
    color_t color = {255,255,0};
    light_fill(color.r, color.g, color.b);
}

void light_test_task(void* arg) {
    color_t color = {255,255,0};
    while(1) {
        light_fill(color.r, color.g, color.b);
        light_show();
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

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
        vTaskDelay(pdMS_TO_TICKS(s_light.period));
    }
}

void light_set_mode(light_mode_t mode) {
    s_light.mode = mode;
}
