/**
 * @file light_mode.c
 * @author LG_frir
 * @date 2026-04-28
 * @brief ARGB 风扇灯效模式与配置
 *
 * 每次调用只推进一帧；动画以毫秒计时，统一由灯光任务调度和发送。
 */

#include "ModuleDriver/light_mode.h"
#include "ModuleDriver/config_save.h"
#include "esp_random.h"

#include <math.h>
#include <stddef.h>

#define CHASE_FADE_MS 1000U
#define CHASE_START_INTERVAL_MS 100U

typedef struct {
    uint8_t current;
    uint32_t elapsed_ms;
} fade_state_t;

static rainbow_cfg_t s_rainbow_eff;
static breath_cfg_t s_breath_cfg;
static random_cfg_t s_random_cfg;
static blink_cfg_t s_blink_cfg;
static custom_breath_cfg_t s_custom_breath_cfg;
static chase_cfg_t s_chase_cfg;
static round_cfg_t s_round_cfg;
static color_t s_static_cfg;
static bool s_modes_inited;

static fade_state_t s_rainbow_state;
static fade_state_t s_breath_state;
static fade_state_t s_custom_state;
static fade_state_t s_chase_state;
static uint32_t s_blink_elapsed_ms;
static uint8_t s_blink_color = 1;
static bool s_random_on;
static float s_round_pos;

uint8_t idx_next(int x) {
    return (x + 1) % LIGHT_PIXEL_COUNT;
}

uint8_t idx_past(int x) {
    return (x - 1) < 0 ? LIGHT_PIXEL_COUNT - 1 : x - 1;
}

bool light_round_cfg_is_valid(const round_cfg_t* cfg) {
    return cfg != NULL &&
           isfinite(cfg->range) && cfg->range > 0.0f &&
           isfinite(cfg->range_main) && cfg->range_main >= 0.0f &&
           cfg->range_main <= cfg->range &&
           cfg->wait_ms >= 0 &&
           isfinite(cfg->center_speed) && cfg->center_speed >= 0.0f;
}

/*
 * @brief 先建立默认值，再加载通过校验的 NVS 配置。
 */
void light_modes_init(void) {
    if (s_modes_inited) {
        return;
    }

    static color_t blink_colors[] = {
        {147, 255, 255}, {0, 0, 0},
    };
    static color_t chase_colors[] = {
        {255, 52, 241}, {12, 13, 255}, {255, 52, 241}, {255, 52, 241},
    };
    static color_t breath_colors[] = {
        {255, 160, 122}, {255, 250, 205}, {0, 250, 154},
        {0, 255, 255}, {0, 191, 255}, {123, 104, 238},
        {238, 130, 238}, {138, 43, 226}, {255, 182, 193},
    };
    static uint16_t breath_fades_ms[] = {
        500, 500, 500, 500, 500, 500, 500, 500, 500,
    };

    s_static_cfg = (color_t){255, 255, 0};
    s_rainbow_eff = (rainbow_cfg_t){.switch_ms = 1000};
    s_breath_cfg = (breath_cfg_t){
        .switch_ms = 1000, .color = {147, 255, 0},
    };
    s_random_cfg = (random_cfg_t){.mode = false};
    s_blink_cfg = (blink_cfg_t){.colors = blink_colors, .period = 500};
    s_chase_cfg = (chase_cfg_t){
        .colors = chase_colors,
        .size = sizeof(chase_colors) / sizeof(chase_colors[0]),
    };
    s_custom_breath_cfg = (custom_breath_cfg_t){
        .colors = breath_colors,
        .fades_ms = breath_fades_ms,
        .size = sizeof(breath_colors) / sizeof(breath_colors[0]),
    };
    s_round_cfg = (round_cfg_t){
        .color_main = {255, 52, 241},
        .color_sub = {12, 13, 255},
        .range = 5.0f,
        .range_main = 1.0f,
        .wait_ms = 2000,
        .center_speed = 1.0f,
    };

    nvs_get_static_cfg(&s_static_cfg);
    nvs_get_rainbow_cfg(&s_rainbow_eff);
    nvs_get_breath_cfg(&s_breath_cfg);
    nvs_get_random_cfg(&s_random_cfg);
    nvs_get_blink_cfg(&s_blink_cfg);
    nvs_get_chase_cfg(&s_chase_cfg);
    nvs_get_custom_breath_cfg(&s_custom_breath_cfg);
    nvs_get_round_cfg(&s_round_cfg);
    s_round_pos = -s_round_cfg.range;
    s_modes_inited = true;
}

color_t* light_get_static_cfg(void) {
    light_modes_init();
    return &s_static_cfg;
}

rainbow_cfg_t* light_rainbow_get_cfg(void) {
    light_modes_init();
    return &s_rainbow_eff;
}

breath_cfg_t* light_get_breath_cfg(void) {
    light_modes_init();
    return &s_breath_cfg;
}

random_cfg_t* light_get_random_cfg(void) {
    light_modes_init();
    return &s_random_cfg;
}

blink_cfg_t* light_get_blink_cfg(void) {
    light_modes_init();
    return &s_blink_cfg;
}

custom_breath_cfg_t* light_get_custom_breath_cfg(void) {
    light_modes_init();
    return &s_custom_breath_cfg;
}

chase_cfg_t* light_get_chase_cfg(void) {
    light_modes_init();
    return &s_chase_cfg;
}

round_cfg_t* light_get_round_cfg(void) {
    light_modes_init();
    return &s_round_cfg;
}

/*
 * @brief 进入模式时从头播放；配置加载状态不受动画重置影响。
 */
void light_mode_reset(light_mode_t mode) {
    light_modes_init();
    switch (mode) {
    case LIGHT_MODE_RAINBOW:
        s_rainbow_state = (fade_state_t){0};
        break;
    case LIGHT_MODE_BREATH:
        s_breath_state = (fade_state_t){0};
        break;
    case LIGHT_MODE_RANDOM_FLASH:
        s_random_on = false;
        break;
    case LIGHT_MODE_BLINK:
        s_blink_elapsed_ms = 0;
        s_blink_color = 1;
        break;
    case LIGHT_MODE_CUSTOM_BREATH:
        s_custom_state = (fade_state_t){0};
        break;
    case LIGHT_MODE_CHASE:
        s_chase_state = (fade_state_t){0};
        break;
    case LIGHT_MODE_ROUND:
        s_round_pos = -s_round_cfg.range;
        break;
    default:
        break;
    }
}

/*
 * @brief 更新等时长分段动画。周期改变时累计时间保持不变。
 */
static void fade_advance(fade_state_t* state, uint32_t delta_ms,
                         uint32_t duration_ms, uint8_t color_count) {
    if (color_count == 0) {
        *state = (fade_state_t){0};
        return;
    }
    if (state->current >= color_count) {
        *state = (fade_state_t){0};
    }
    if (duration_ms == 0) {
        state->elapsed_ms = 0;
        return;
    }

    uint64_t elapsed = (uint64_t)state->elapsed_ms + delta_ms;
    state->current = (uint8_t)((state->current + elapsed / duration_ms) % color_count);
    state->elapsed_ms = (uint32_t)(elapsed % duration_ms);
}

static color_t color_lerp(color_t from, color_t to,
                          uint32_t elapsed_ms, uint32_t duration_ms) {
    /* 即使运行配置被直接改成零时间，也只立即完成，不执行除零。 */
    if (duration_ms == 0 || elapsed_ms >= duration_ms) {
        return to;
    }
    color_t color = {
        .r = (uint8_t)(from.r + ((int32_t)to.r - from.r) * (int32_t)elapsed_ms / (int32_t)duration_ms),
        .g = (uint8_t)(from.g + ((int32_t)to.g - from.g) * (int32_t)elapsed_ms / (int32_t)duration_ms),
        .b = (uint8_t)(from.b + ((int32_t)to.b - from.b) * (int32_t)elapsed_ms / (int32_t)duration_ms),
    };
    return color;
}

static void light_fill_color(color_t color) {
    light_fill(color.r, color.g, color.b);
}

void light_static(void) {
    light_fill_color(*light_get_static_cfg());
}

void light_rainbow(uint32_t delta_ms) {
    const rainbow_cfg_t cfg = *light_rainbow_get_cfg();
    static const color_t colors[] = {
        {255, 0, 0}, {0, 255, 0}, {0, 0, 255},
    };
    const uint8_t count = sizeof(colors) / sizeof(colors[0]);
    fade_advance(&s_rainbow_state, delta_ms, cfg.switch_ms, count);
    uint8_t next = (s_rainbow_state.current + 1) % count;
    light_fill_color(color_lerp(colors[s_rainbow_state.current], colors[next],
                               s_rainbow_state.elapsed_ms, cfg.switch_ms));
}

void light_breathe(uint32_t delta_ms) {
    const breath_cfg_t cfg = *light_get_breath_cfg();
    color_t colors[] = {cfg.color, {0, 0, 0}};
    fade_advance(&s_breath_state, delta_ms, cfg.switch_ms, 2);
    uint8_t next = (s_breath_state.current + 1) % 2;
    light_fill_color(color_lerp(colors[s_breath_state.current], colors[next],
                               s_breath_state.elapsed_ms, cfg.switch_ms));
}

void light_random_flash(void) {
    const random_cfg_t cfg = *light_get_random_cfg();
    if (cfg.mode) {
        s_random_on = true;
    }
    if (s_random_on) {
        color_t colors[LIGHT_PIXEL_COUNT];
        for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
            uint32_t rnd = esp_random();
            colors[i] = (color_t){
                .r = (uint8_t)rnd,
                .g = (uint8_t)(rnd >> 8),
                .b = (uint8_t)(rnd >> 16),
            };
        }
        light_fill_advance(colors);
    } else {
        light_clear();
    }
    s_random_on = !s_random_on;
}

void light_blink(uint32_t delta_ms) {
    const blink_cfg_t cfg = *light_get_blink_cfg();
    if (cfg.colors == NULL) {
        light_clear();
        return;
    }
    if (cfg.period == 0) {
        s_blink_elapsed_ms = 0;
        s_blink_color = 0;
    } else {
        uint64_t elapsed = (uint64_t)s_blink_elapsed_ms + delta_ms;
        s_blink_color ^= (uint8_t)((elapsed / cfg.period) & 1U);
        s_blink_elapsed_ms = (uint32_t)(elapsed % cfg.period);
    }
    light_fill_color(cfg.colors[s_blink_color]);
}

void light_custom_breath(uint32_t delta_ms) {
    const custom_breath_cfg_t cfg = *light_get_custom_breath_cfg();
    if (cfg.colors == NULL || cfg.fades_ms == NULL ||
        cfg.size == 0 || cfg.size > LIGHT_CFG_MAX_COLORS) {
        light_clear();
        return;
    }
    if (cfg.size == 1) {
        light_fill_color(cfg.colors[0]);
        return;
    }
    if (s_custom_state.current >= cfg.size) {
        s_custom_state = (fade_state_t){0};
    }

    uint32_t cycle_ms = 0;
    for (int i = 0; i < cfg.size; i++) {
        cycle_ms += cfg.fades_ms[i];
    }
    if (cycle_ms == 0) {
        s_custom_state = (fade_state_t){0};
        light_fill_color(cfg.colors[0]);
        return;
    }

    uint64_t elapsed = ((uint64_t)s_custom_state.elapsed_ms + delta_ms) % cycle_ms;
    /* 最多跨过一整轮；零时间段直接跳过，避免死循环。 */
    for (int i = 0; i < cfg.size; i++) {
        uint16_t duration_ms = cfg.fades_ms[s_custom_state.current];
        if (duration_ms != 0 && elapsed < duration_ms) {
            break;
        }
        elapsed -= duration_ms;
        s_custom_state.current = (s_custom_state.current + 1) % cfg.size;
    }
    s_custom_state.elapsed_ms = (uint32_t)elapsed;
    uint8_t next = (s_custom_state.current + 1) % cfg.size;
    light_fill_color(color_lerp(cfg.colors[s_custom_state.current], cfg.colors[next],
                               s_custom_state.elapsed_ms, cfg.fades_ms[s_custom_state.current]));
}

void light_chase(uint32_t delta_ms) {
    const chase_cfg_t cfg = *light_get_chase_cfg();
    if (cfg.colors == NULL || cfg.size == 0 || cfg.size > LIGHT_CFG_MAX_COLORS) {
        light_clear();
        return;
    }

    const uint32_t cycle_ms = CHASE_FADE_MS +
                             (LIGHT_PIXEL_COUNT - 1U) * CHASE_START_INTERVAL_MS;
    fade_advance(&s_chase_state, delta_ms, cycle_ms, cfg.size);
    uint8_t next = (s_chase_state.current + 1) % cfg.size;
    color_t colors[LIGHT_PIXEL_COUNT];
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        uint32_t start_ms = (uint32_t)i * CHASE_START_INTERVAL_MS;
        uint32_t elapsed_ms = s_chase_state.elapsed_ms > start_ms ?
                              s_chase_state.elapsed_ms - start_ms : 0;
        colors[i] = color_lerp(cfg.colors[s_chase_state.current], cfg.colors[next],
                               elapsed_ms, CHASE_FADE_MS);
    }
    light_fill_advance(colors);
}

void light_round(uint32_t delta_ms) {
    const round_cfg_t cfg = *light_get_round_cfg();
    if (!light_round_cfg_is_valid(&cfg)) {
        light_clear();
        return;
    }

    s_round_pos += cfg.center_speed * ((float)delta_ms / 1000.0f);
    float end_pos = (float)LIGHT_PIXEL_COUNT + cfg.range +
                    cfg.center_speed * ((float)cfg.wait_ms / 1000.0f);
    if (s_round_pos >= end_pos) {
        s_round_pos = -cfg.range + fmodf(s_round_pos + cfg.range, end_pos + cfg.range);
    }

    color_t colors[LIGHT_PIXEL_COUNT];
    for (int i = 0; i < LIGHT_PIXEL_COUNT; i++) {
        float dist = fabsf((float)i - s_round_pos);
        if (dist <= cfg.range_main) {
            colors[i] = cfg.color_sub;
        } else if (dist < cfg.range) {
            float phase = (dist - cfg.range_main) / (cfg.range - cfg.range_main);
            colors[i] = (color_t){
                .r = (uint8_t)(cfg.color_sub.r + ((float)cfg.color_main.r - cfg.color_sub.r) * phase),
                .g = (uint8_t)(cfg.color_sub.g + ((float)cfg.color_main.g - cfg.color_sub.g) * phase),
                .b = (uint8_t)(cfg.color_sub.b + ((float)cfg.color_main.b - cfg.color_sub.b) * phase),
            };
        } else {
            colors[i] = cfg.color_main;
        }
    }
    light_fill_advance(colors);
}
