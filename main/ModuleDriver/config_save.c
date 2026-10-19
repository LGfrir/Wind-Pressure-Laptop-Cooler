/**
 * @file config_save.c
 * @author LG_frir
 * @date 2026-10-12 21:52:22
 *
 * 配置持久化实现。
 *
 * 设计要点
 * --------
 * 1. 每个配置一对 nvs_save_xxx / nvs_get_xxx，调用方完全不碰 NVS 的样板代码。
 *
 * 2. 内部只有【两个】通用函数（cfg_set_blob / cfg_get_blob），
 *    所以十几个配置加起来也没有重复代码 —— 每个配置函数只有 2~6 行。
 *
 * 3. 带指针的配置（blink / custom_breath / chase）：
 *      指针本身不能存（地址重启后没意义），存的是它【指向的数组】。
 *      读回来时把指针指到本文件里的静态缓冲区，
 *      调用方拿到就能直接用，不用自己 malloc。
 *
 * 4. 每个配置存成【一个 blob】（整个结构体一次写）。
 *      好处：调用点极简，代码不重复。
 *      代价：结构体一旦改字段，旧数据长度对不上 → 会被判为"格式不符"
 *            而【整体丢弃、回落到默认值】。这是安全的方向（不会读到脏数据），
 *            但就是整份配置要重设一次。
 *
 * 约定
 * ----
 * 所有 nvs_get_xxx 在失败时【不修改】传入的结构体，
 * 这样调用方可以先把默认值填好再调用它。
 */

#include "ModuleDriver/config_save.h"
#include "ModuleDriver/light_mode.h"
#include "esp_log.h"
#include <string.h>

static const char *TAG = "NVS_CFG";

/* ============================================================
 *  命名空间 / 键名
 *
 *  ⚠️ NVS 硬限制：命名空间名和键名都【最多 15 个字符】
 *     (nvs.h: NVS_KEY_NAME_MAX_SIZE = 16，含结尾 '\0')
 * ============================================================ */
#define NS_FAN    "fan"
#define NS_LIGHT  "light"

#define KEY_FAN      "cfg"
#define KEY_LIGHT    "cfg"
#define KEY_RAINBOW  "rainbow"
#define KEY_STATIC   "static"
#define KEY_BREATH   "breath"
#define KEY_RANDOM   "random"
#define KEY_BLINK    "blink"
#define KEY_CBREATH  "cbreath"
#define KEY_CHASE    "chase"
#define KEY_ROUND    "round"

/* ============================================================
 *  "落盘格式"（store）：只放需要存下来的字段
 *
 *  为什么不用原结构体直接存？
 *   - fan_t 里有 rpm / is_on / target_rpm，这些不该持久化
 *   - 带指针的结构体，指针字段本身是无意义的
 * ============================================================ */

/*
 * blink_cfg_t 里【没有】size 字段 —— light_blink() 硬编码只用 colors[0] / colors[1]。
 * 所以这里也只能固定成 2。
 * TODO: 给 blink_cfg_t 也加一个 uint8_t size，跟 chase_cfg_t 保持一致。
 */
#define BLINK_COLOR_COUNT 2

typedef struct {
    uint8_t mode;                          /* fan_mode_t */
    uint8_t speed;                         /* speed_percentage */
    uint8_t curve[SPEED_CURVE_MEMBER];
} fan_store_t;

/*
 * 灯光总配置只存 mode 和 brightness。
 * update_period 由运行时接口限制为合法档位，这里沿用不持久化的约定。
 */
typedef struct {
    uint8_t mode;
    uint8_t brightness;
} light_store_t;

typedef struct {
    uint8_t size;
    color_t colors[BLINK_COLOR_COUNT];
    uint16_t period;
} blink_store_t;

typedef struct {
    uint8_t size;
    color_t colors[LIGHT_CFG_MAX_COLORS];
} chase_store_t;

typedef struct {
    uint8_t  size;
    color_t  colors[LIGHT_CFG_MAX_COLORS];
    uint16_t fades_ms[LIGHT_CFG_MAX_COLORS];
} cbreath_store_t;

/* ============================================================
 *  带指针的配置 —— 读回来时的落脚点（由本文件拥有）
 *
 *  light_mode.c 里的数组是函数内的 static，外部够不着；
 *  所以这里自带一份缓冲区，nvs_get_xxx 读完把指针指过来即可。
 * ============================================================ */
static color_t  s_blink_colors[BLINK_COLOR_COUNT];
static color_t  s_chase_colors[LIGHT_CFG_MAX_COLORS];
static color_t  s_cbreath_colors[LIGHT_CFG_MAX_COLORS];
static uint16_t s_cbreath_fades[LIGHT_CFG_MAX_COLORS];

/* ============================================================
 *  通用底层：一个结构体  <=>  一个 blob
 * ============================================================ */

/*
 * @brief 写一个 blob，并提交。
 */
static esp_err_t cfg_set_blob(const char *ns, const char *key,
                              const void *data, size_t len)
{
    nvs_handle_t h;

    esp_err_t rc = nvs_open(ns, NVS_READWRITE, &h);
    if (rc != ESP_OK) {
        ESP_LOGE(TAG, "nvs_open(%s) 失败: %s", ns, esp_err_to_name(rc));
        return rc;
    }

    rc = nvs_set_blob(h, key, data, len);
    if (rc == ESP_OK) {
        /* ★★ 必须 commit。nvs_close() 不会帮你提交，
         *     忘了这行 = 数据其实没写进 flash，而且不报错。 */
        rc = nvs_commit(h);
    }

    if (rc != ESP_OK) {
        ESP_LOGE(TAG, "写 %s/%s 失败: %s", ns, key, esp_err_to_name(rc));
    }

    nvs_close(h);
    return rc;
}

/*
 * @brief 读一个 blob。长度必须和期望完全一致。
 *
 * @return ESP_OK                  读到了
 *         ESP_ERR_NVS_NOT_FOUND   没存过 / 命名空间不存在 / 长度不符（格式变了）
 *         其它                    真正的错误
 */
static esp_err_t cfg_get_blob(const char *ns, const char *key,
                              void *data, size_t len)
{
    nvs_handle_t h;

    esp_err_t rc = nvs_open(ns, NVS_READONLY, &h);
    if (rc != ESP_OK) {
        /* 首次开机时命名空间还不存在，这里是正常路径，不要当错误刷日志 */
        return rc;
    }

    /* 先检查格式，避免短数据先覆盖默认值、随后才报告长度不符。 */
    size_t got = 0;
    rc = nvs_get_blob(h, key, NULL, &got);
    if (rc == ESP_OK && got != len) {
        ESP_LOGW(TAG, "%s/%s 长度不符（存的 %u / 期望 %u），当作没存过",
                 ns, key, (unsigned)got, (unsigned)len);
        rc = ESP_ERR_NVS_NOT_FOUND;
    }
    if (rc == ESP_OK) {
        rc = nvs_get_blob(h, key, data, &got);
    }
    nvs_close(h);
    return rc;
}

/* ============================================================
 *  风扇
 * ============================================================ */

esp_err_t nvs_save_fan_cfg(void)
{
    fan_store_t s = {
        .mode  = (uint8_t)fan.mode,
        .speed = fan.speed_percentage,
    };
    memcpy(s.curve, fan.pwm_t_curve, SPEED_CURVE_MEMBER);

    return cfg_set_blob(NS_FAN, KEY_FAN, &s, sizeof(s));
}

esp_err_t nvs_get_fan_cfg(void)
{
    fan_store_t s;

    esp_err_t rc = cfg_get_blob(NS_FAN, KEY_FAN, &s, sizeof(s));
    if (rc != ESP_OK) {
        return rc;                          /* 保持 fan 里的默认值不动 */
    }

    fan.mode             = (fan_mode_t)s.mode;
    fan.speed_percentage = s.speed;
    memcpy(fan.pwm_t_curve, s.curve, SPEED_CURVE_MEMBER);

    return ESP_OK;
}

/* ============================================================
 *  灯光总配置
 * ============================================================ */

esp_err_t nvs_save_light_cfg(const light_t *cfg)
{
    if (cfg == NULL || (unsigned)cfg->mode > LIGHT_MODE_ROUND) {
        return ESP_ERR_INVALID_ARG;
    }

    light_store_t s = {
        .mode       = (uint8_t)cfg->mode,
        .brightness = cfg->brightness,
    };
    return cfg_set_blob(NS_LIGHT, KEY_LIGHT, &s, sizeof(s));
}

esp_err_t nvs_get_light_cfg(light_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    light_store_t s;
    esp_err_t rc = cfg_get_blob(NS_LIGHT, KEY_LIGHT, &s, sizeof(s));
    if (rc != ESP_OK) {
        return rc;                          /* 保持调用方的默认值不动 */
    }

    if (s.mode > LIGHT_MODE_ROUND) {
        return ESP_ERR_INVALID_ARG;
    }
    /* 只写回这两个字段 —— update_period 由 light_init() 决定，这里不碰 */
    cfg->mode       = (light_mode_t)s.mode;
    cfg->brightness = s.brightness;
    return ESP_OK;
}

/* ============================================================
 *  灯效：彩虹 / 呼吸 / 随机闪烁 / 跑马灯
 *  这四个结构体里【没有指针】，直接整块存
 * ============================================================ */

esp_err_t nvs_save_rainbow_cfg(const rainbow_cfg_t *cfg)
{
    if (cfg == NULL || cfg->switch_ms == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    return cfg_set_blob(NS_LIGHT, KEY_RAINBOW, cfg, sizeof(*cfg));
}

esp_err_t nvs_get_rainbow_cfg(rainbow_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    rainbow_cfg_t saved;
    esp_err_t rc = cfg_get_blob(NS_LIGHT, KEY_RAINBOW, &saved, sizeof(saved));
    if (rc != ESP_OK) {
        return rc;
    }
    if (saved.switch_ms == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    *cfg = saved;
    return ESP_OK;
}

esp_err_t nvs_save_static_cfg(const color_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    return cfg_set_blob(NS_LIGHT, KEY_STATIC, cfg, sizeof(*cfg));
}

esp_err_t nvs_get_static_cfg(color_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    return cfg_get_blob(NS_LIGHT, KEY_STATIC, cfg, sizeof(*cfg));
}

esp_err_t nvs_save_breath_cfg(const breath_cfg_t *cfg)
{
    if (cfg == NULL || cfg->switch_ms == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    return cfg_set_blob(NS_LIGHT, KEY_BREATH, cfg, sizeof(*cfg));
}

esp_err_t nvs_get_breath_cfg(breath_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    breath_cfg_t saved;
    esp_err_t rc = cfg_get_blob(NS_LIGHT, KEY_BREATH, &saved, sizeof(saved));
    if (rc != ESP_OK) {
        return rc;
    }
    if (saved.switch_ms == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    *cfg = saved;
    return ESP_OK;
}

esp_err_t nvs_save_random_cfg(const random_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    return cfg_set_blob(NS_LIGHT, KEY_RANDOM, cfg, sizeof(*cfg));
}

esp_err_t nvs_get_random_cfg(random_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    return cfg_get_blob(NS_LIGHT, KEY_RANDOM, cfg, sizeof(*cfg));
}

esp_err_t nvs_save_round_cfg(const round_cfg_t *cfg)
{
    if (!light_round_cfg_is_valid(cfg)) {
        return ESP_ERR_INVALID_ARG;
    }
    return cfg_set_blob(NS_LIGHT, KEY_ROUND, cfg, sizeof(*cfg));
}

esp_err_t nvs_get_round_cfg(round_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }
    round_cfg_t saved;
    esp_err_t rc = cfg_get_blob(NS_LIGHT, KEY_ROUND, &saved, sizeof(saved));
    if (rc != ESP_OK) {
        return rc;
    }
    if (!light_round_cfg_is_valid(&saved)) {
        return ESP_ERR_INVALID_ARG;
    }
    *cfg = saved;
    return ESP_OK;
}

/* ============================================================
 *  带指针的三个
 *
 *  存：size + 数组内容
 *  读：校验 size，拷进本文件的缓冲区，再把指针指过去
 *      —— 失败时【不碰】cfg，调用方的默认值原样保留
 * ============================================================ */

/* -------------------- 闪烁 -------------------- */

esp_err_t nvs_save_blink_cfg(const blink_cfg_t *cfg)
{
    if (cfg == NULL || cfg->colors == NULL || cfg->period == 0) {
        return ESP_ERR_INVALID_ARG;
    }

    blink_store_t s = {
        .size   = BLINK_COLOR_COUNT,
        .period = cfg->period,
    };
    memcpy(s.colors, cfg->colors, sizeof(color_t) * BLINK_COLOR_COUNT);

    return cfg_set_blob(NS_LIGHT, KEY_BLINK, &s, sizeof(s));
}

esp_err_t nvs_get_blink_cfg(blink_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    blink_store_t s;
    esp_err_t rc = cfg_get_blob(NS_LIGHT, KEY_BLINK, &s, sizeof(s));
    if (rc != ESP_OK) {
        return rc;
    }

    if (s.size != BLINK_COLOR_COUNT || s.period == 0) {
        return ESP_ERR_INVALID_ARG;
    }
    memcpy(s_blink_colors, s.colors, sizeof(color_t) * BLINK_COLOR_COUNT);

    cfg->colors = s_blink_colors;           /* ← 指向本文件的缓冲区 */
    cfg->period = s.period;
    return ESP_OK;
}

/* -------------------- 流光 -------------------- */

esp_err_t nvs_save_chase_cfg(const chase_cfg_t *cfg)
{
    if (cfg == NULL || cfg->colors == NULL ||
        cfg->size == 0 || cfg->size > LIGHT_CFG_MAX_COLORS) {
        return ESP_ERR_INVALID_ARG;
    }

    chase_store_t s = { .size = cfg->size };
    memcpy(s.colors, cfg->colors, sizeof(color_t) * cfg->size);

    return cfg_set_blob(NS_LIGHT, KEY_CHASE, &s, sizeof(s));
}

esp_err_t nvs_get_chase_cfg(chase_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    chase_store_t s;
    esp_err_t rc = cfg_get_blob(NS_LIGHT, KEY_CHASE, &s, sizeof(s));
    if (rc != ESP_OK) {
        return rc;
    }

    /* NVS 里的内容也可能被写坏，size 必须自己再校验一遍 */
    if (s.size == 0 || s.size > LIGHT_CFG_MAX_COLORS) {
        ESP_LOGW(TAG, "chase.size=%u 越界，忽略", s.size);
        return ESP_ERR_INVALID_SIZE;
    }

    memcpy(s_chase_colors, s.colors, sizeof(color_t) * s.size);

    cfg->colors = s_chase_colors;
    cfg->size   = s.size;
    return ESP_OK;
}

/* -------------------- 自定义呼吸 -------------------- */

esp_err_t nvs_save_custom_breath_cfg(const custom_breath_cfg_t *cfg)
{
    if (cfg == NULL || cfg->colors == NULL || cfg->fades_ms == NULL ||
        cfg->size == 0 || cfg->size > LIGHT_CFG_MAX_COLORS) {
        return ESP_ERR_INVALID_ARG;
    }

    cbreath_store_t s = { .size = cfg->size };
    for (int i = 0; i < cfg->size; i++) {
        if (cfg->fades_ms[i] == 0) {
            return ESP_ERR_INVALID_ARG;
        }
    }
    memcpy(s.colors,   cfg->colors,   sizeof(color_t)  * cfg->size);
    memcpy(s.fades_ms, cfg->fades_ms, sizeof(uint16_t) * cfg->size);

    return cfg_set_blob(NS_LIGHT, KEY_CBREATH, &s, sizeof(s));
}

esp_err_t nvs_get_custom_breath_cfg(custom_breath_cfg_t *cfg)
{
    if (cfg == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    cbreath_store_t s;
    esp_err_t rc = cfg_get_blob(NS_LIGHT, KEY_CBREATH, &s, sizeof(s));
    if (rc != ESP_OK) {
        return rc;
    }

    if (s.size == 0 || s.size > LIGHT_CFG_MAX_COLORS) {
        ESP_LOGW(TAG, "cbreath.size=%u 越界，忽略", s.size);
        return ESP_ERR_INVALID_SIZE;
    }

    for (int i = 0; i < s.size; i++) {
        if (s.fades_ms[i] == 0) {
            return ESP_ERR_INVALID_ARG;
        }
    }
    memcpy(s_cbreath_colors, s.colors,   sizeof(color_t)  * s.size);
    memcpy(s_cbreath_fades,  s.fades_ms, sizeof(uint16_t) * s.size);

    cfg->colors   = s_cbreath_colors;
    cfg->fades_ms = s_cbreath_fades;
    cfg->size     = s.size;
    return ESP_OK;
}
