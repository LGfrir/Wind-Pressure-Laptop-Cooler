/**
 * @file config_save.h
 * @author LG_frir
 * @date 2026-10-12 21:52:30
 *
 * 配置持久化：把运行时的配置存进 NVS / 从 NVS 读回来。
 *
 * 用法:
 *   开机:  nvs_flash_init() 之后,逐个调 nvs_get_xxx_cfg(&cfg)
 *          读取失败、格式不符或参数无效时保留默认值。
 *   改动:  用户点了"确认"再调 nvs_save_xxx_cfg(&cfg)。
 *
 * ⚠️ 不要在"转旋钮 / 拖动条"的过程中存 —— NVS 是 flash,有擦写寿命。
 */

#ifndef __CONFIG_SAVE_H__
#define __CONFIG_SAVE_H__

#include "nvs_flash.h"
#include "esp_err.h"
#include "ModuleDriver/light_cfg.h"
#include "ModuleDriver/fan.h"

/*
 * 灯效配置里颜色数组的最大长度。
 * 由 custom_breath 决定（它现在用 9 个点）。
 * 如果以后哪个灯效要更多颜色，把这个值调大 —— 但注意:
 * 调大之后所有 blob 的长度都变了，旧数据会被判为"格式不符"而丢弃。
 */
#define LIGHT_CFG_MAX_COLORS 9

/* ============================================================
 *  风扇
 *  只存会变的配置，不存 rpm / is_on / target_rpm
 * ============================================================ */
esp_err_t nvs_save_fan_cfg(void);
esp_err_t nvs_get_fan_cfg(void);

/* ============================================================
 *  灯光总配置（模式、亮度；刷新周期只在运行时修改）
 * ============================================================ */
esp_err_t nvs_save_light_cfg(const light_t *cfg);
esp_err_t nvs_get_light_cfg(light_t *cfg);

/* ============================================================
 *  各灯效的配置
 *
 *  ⚠️ blink / custom_breath / chase 三个结构体里带【指针】。
 *     指针存不了，所以存的是它指向的数组；
 *     读回来时 cfg->colors 会指向 config_save.c 内部的静态缓冲区，
 *     调用方拿到就能直接用，不需要自己准备内存。
 * ============================================================ */

esp_err_t nvs_save_rainbow_cfg(const rainbow_cfg_t *cfg);
esp_err_t nvs_get_rainbow_cfg(rainbow_cfg_t *cfg);

esp_err_t nvs_save_static_cfg(const color_t *cfg);
esp_err_t nvs_get_static_cfg(color_t *cfg);

esp_err_t nvs_save_breath_cfg(const breath_cfg_t *cfg);
esp_err_t nvs_get_breath_cfg(breath_cfg_t *cfg);

esp_err_t nvs_save_random_cfg(const random_cfg_t *cfg);
esp_err_t nvs_get_random_cfg(random_cfg_t *cfg);

esp_err_t nvs_save_blink_cfg(const blink_cfg_t *cfg);
esp_err_t nvs_get_blink_cfg(blink_cfg_t *cfg);

esp_err_t nvs_save_custom_breath_cfg(const custom_breath_cfg_t *cfg);
esp_err_t nvs_get_custom_breath_cfg(custom_breath_cfg_t *cfg);

esp_err_t nvs_save_chase_cfg(const chase_cfg_t *cfg);
esp_err_t nvs_get_chase_cfg(chase_cfg_t *cfg);

esp_err_t nvs_save_round_cfg(const round_cfg_t *cfg);
esp_err_t nvs_get_round_cfg(round_cfg_t *cfg);

#endif /* __CONFIG_SAVE_H__ */
