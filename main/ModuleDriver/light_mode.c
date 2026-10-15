/**
 * @file light_mode.c
 * @author LG_frir
 * @date 2026-04-28
 * @brief ARGB 风扇灯效模式与配置
 *
 * 负责各灯效的颜色计算，通过灯光执行接口更新像素缓存。
 */

#include "ModuleDriver/light_mode.h"
#include "ModuleDriver/config_save.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_random.h"

#include <math.h>
#include <string.h>

breath_cfg_t s_breath_cfg;

/*
 * @brief 获取下一个灯珠索引。
 */
uint8_t idx_next(int x) {
    return (x + 1) % LIGHT_PIXEL_COUNT;
}

/*
 * @brief 获取上一个灯珠索引。
 */
uint8_t idx_past(int x) {
    return (x - 1) < 0 ? LIGHT_PIXEL_COUNT - 1 : x - 1;
}

/* ========================= 灯效模式 ========================= */

/*
 * @brief 彩虹渐变灯效配置对象。
 */
rainbow_cfg_t s_rainbow_eff;
bool s_rainbow_init;

/*
 * @brief 获取彩虹灯效配置。
 */
rainbow_cfg_t* light_rainbow_get_cfg(void) {
    return &s_rainbow_eff;
}

/*
 * @brief 生成彩虹渐变灯效。
 */
void light_rainbow(void) {
    // init
    if(!s_rainbow_init) {
        s_rainbow_init = 1;

        /* 优先用 NVS 里存的；读不到（首次开机 / 格式变了）才落到默认值 */
        if (nvs_get_rainbow_cfg(&s_rainbow_eff) != ESP_OK) {
            s_rainbow_eff.switch_ms = 1000;
        }
    }
    // custom config
    const int fade_steps = s_rainbow_eff.switch_ms / s_light.update_period;

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

/*
 * @brief 呼吸灯效配置对象。
 */
bool s_breath_init;

/*
 * @brief 获取呼吸灯效配置。
 */
breath_cfg_t* light_get_breath_cfg(void) {
    return &s_breath_cfg;
}

/*
 * @brief 生成呼吸渐变灯效。
 */
void light_breathe(void) {
    if(!s_breath_init) {
        s_breath_init = true;

        if (nvs_get_breath_cfg(&s_breath_cfg) != ESP_OK) {
            s_breath_cfg.switch_ms = 1000;
            s_breath_cfg.color.r = 147;
            s_breath_cfg.color.g = 255;
            s_breath_cfg.color.b = 0;
        }
    }

    const int fade_steps = s_breath_cfg.switch_ms / s_light.update_period;


    color_t colors[] = {
        {  0,   0,   0},
        {  0,   0,   0},
    };
    colors[0] = s_breath_cfg.color;

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

/*
 * @brief 随机闪烁灯效配置对象。
 */
bool s_random_flash_init;
random_cfg_t s_random_cfg;

/*
 * @brief 获取随机闪烁灯效配置。
 */
random_cfg_t* light_get_random_cfg(void) {
    return &s_random_cfg;
}

/*
 * @brief 生成随机闪烁灯效。
 */
void light_random_flash(void) {
    if(!s_random_flash_init) {
        s_random_flash_init = true;

        if (nvs_get_random_cfg(&s_random_cfg) != ESP_OK) {
            s_random_cfg.mode = false;
        }
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

/*
 * @brief 闪烁灯效配置对象。
 */
blink_cfg_t s_blink_cfg;
bool s_blink_init;

/*
 * @brief 获取闪烁灯效配置。
 */
blink_cfg_t* light_get_blink_cfg(void) {
    return &s_blink_cfg;
}

/*
 * @brief 生成闪烁灯效。
 */
void light_blink(void) {
    if(!s_blink_init) {
        s_blink_init = true;

        if (nvs_get_blink_cfg(&s_blink_cfg) != ESP_OK) {
            static color_t color_static[] = {
                {147,255,255},
                {0,0,0},
            };
            s_blink_cfg.colors = color_static;
            s_blink_cfg.period = 500;
        }
    }


    color_t* colors = s_blink_cfg.colors;


    static bool ls = 0;

    if(ls) {
        light_fill(colors[0].r, colors[0].g, colors[0].b);
    } else {
        light_fill(colors[1].r, colors[1].g, colors[1].b);
    }
    ls = !ls;
    vTaskDelay(pdMS_TO_TICKS((int)s_blink_cfg.period - (int)s_light.update_period >= 0 ? s_blink_cfg.period - s_light.update_period : 0));
}

/*
 * @brief 自定义呼吸灯效配置对象。
 */
custom_breath_cfg_t s_custom_breath_cfg;
bool s_custom_breath_init;

/*
 * @brief 获取自定义呼吸灯效配置。
 */
custom_breath_cfg_t* light_get_custom_breath_cfg(void) {
    return &s_custom_breath_cfg;
}

/*
 * @brief 生成自定义多段渐变呼吸灯效。
 */
void light_custom_breath(void) {
    if(!s_custom_breath_init) {
        s_custom_breath_init = true;

        if (nvs_get_custom_breath_cfg(&s_custom_breath_cfg) != ESP_OK) {
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
        }
    }


    color_t* colors = s_custom_breath_cfg.colors;
    uint16_t* fades_ms = s_custom_breath_cfg.fades_ms;



    uint8_t color_count = s_custom_breath_cfg.size;

    static int current = 0;
    static int next = 1;
    static uint32_t step = 0;

    const color_t c0 = colors[current];
    const color_t c1 = colors[next];

    uint32_t fade_steps = fades_ms[current] / s_light.update_period;
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

/*
 * @brief 流光灯效配置对象。
 */
chase_cfg_t s_chase_cfg;
bool s_chase_init;

/*
 * @brief 获取流光灯效配置。
 */
chase_cfg_t* light_get_chase_cfg(void) {
    return &s_chase_cfg;
}

/*
 * @brief 生成流光灯效。
 */
void light_chase(void) {
    if(!s_chase_init) {
        s_chase_init = true;

        if (nvs_get_chase_cfg(&s_chase_cfg) != ESP_OK) {
            static color_t colors[] = {
                {255,52,241},
                {12,13,255},
                {255,52,241},
                {255,52,241},
            };
            s_chase_cfg.colors = colors;
            s_chase_cfg.size = sizeof(colors) / sizeof(colors[0]);
        }
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

/*
 * @brief 环绕扫描灯效配置对象。
 */
round_cfg_t s_round_cfg;

/*
 * @brief 获取环绕扫描灯效配置。
 */
round_cfg_t* light_get_round_cfg(void) {
    return &s_round_cfg;
}

bool s_round_init;

/*
 * @brief 生成环绕扫描灯效。
 */
void light_round(void) {
    if(!s_round_init) {
        s_round_init = true;

        if (nvs_get_round_cfg(&s_round_cfg) != ESP_OK) {
            color_t color_main = {255,52,241};
            color_t color_sub = {12,13,255};

            s_round_cfg.color_main = color_main;
            s_round_cfg.color_sub =  color_sub;
            s_round_cfg.range = 5;
            s_round_cfg.range_main = 1;
            s_round_cfg.center_speed = 1;
            s_round_cfg.wait_ms = 2000;
        }
    }

    const float range = s_round_cfg.range;
    const float range_main = s_round_cfg.range_main;
    const color_t color_main = s_round_cfg.color_main;
    const color_t color_sub = s_round_cfg.color_sub;
    const float center_speed = s_round_cfg.center_speed;
    const int32_t wait_ms = s_round_cfg.wait_ms;


    color_t colors_update[LIGHT_PIXEL_COUNT];


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

    pos_idx += center_speed * ((float)s_light.update_period / 1000.0f);

    if (pos_idx >= (float)LIGHT_PIXEL_COUNT + range + center_speed * wait_ms / 1000) {
        pos_idx = -s_round_cfg.range;
    }

    light_fill_advance(colors_update);
}

/*
 * @brief 默认静态灯效配置对象。
 */
color_t s_static_cfg;

/*
 * @brief 获取默认静态灯效配置。
 */
color_t* light_get_static_cfg(void) {
    return &s_static_cfg;
}

bool s_static_init;

/*
 * @brief 生成默认静态灯效。
 */
void light_static(void) {
    if(!s_static_init) {
        s_static_init = true;

        if (nvs_get_static_cfg(&s_static_cfg) != ESP_OK) {
            s_static_cfg.r = 255;
            s_static_cfg.g = 255;
            s_static_cfg.b = 0;
        }
    }
    light_fill(s_static_cfg.r, s_static_cfg.g, s_static_cfg.b);
}
