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

/* 计算当前帧颜色并更新像素缓存，由灯光任务统一发送。 */
void light_static(void);
void light_rainbow(void);
void light_breathe(void);
void light_random_flash(void);
void light_blink(void);
void light_custom_breath(void);
void light_chase(void);
void light_round(void);

#endif /* LIGHT_MODE_H */
