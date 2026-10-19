/**
 * @file light_mode.h
 * @author LG_frir
 * @date 2026-04-28
 * @brief ARGB 风扇灯效模式接口
 */

#ifndef LIGHT_MODE_H
#define LIGHT_MODE_H

#include "ModuleDriver/light_cfg.h"

/* 各灯效的配置访问接口。 */
color_t* light_get_static_cfg(void);
rainbow_cfg_t* light_rainbow_get_cfg(void);
breath_cfg_t* light_get_breath_cfg(void);
random_cfg_t* light_get_random_cfg(void);
blink_cfg_t* light_get_blink_cfg(void);
custom_breath_cfg_t* light_get_custom_breath_cfg(void);
chase_cfg_t* light_get_chase_cfg(void);
round_cfg_t* light_get_round_cfg(void);

uint8_t idx_next(int x);
uint8_t idx_past(int x);

/* NVS 初始化后加载默认配置和已保存配置；动画状态与配置分别管理。 */
void light_modes_init(void);
void light_mode_reset(light_mode_t mode);
bool light_round_cfg_is_valid(const round_cfg_t* cfg);

/* delta_ms 是本次累计的毫秒数；模式内部不等待、不发送。 */
void light_static(void);
void light_rainbow(uint32_t delta_ms);
void light_breathe(uint32_t delta_ms);
void light_random_flash(void);
void light_blink(uint32_t delta_ms);
void light_custom_breath(uint32_t delta_ms);
void light_chase(uint32_t delta_ms);
void light_round(uint32_t delta_ms);

#endif /* LIGHT_MODE_H */
