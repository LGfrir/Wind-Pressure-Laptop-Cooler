/**
 * @file light_cfg.h
 * @author LG_frir
 * @date 2026-05-02 15:01:33
 */

#ifndef __LIGHT_CFG_H__
#define __LIGHT_CFG_H__
#include "Tasks/light_task.h"

typedef struct rainbow_cfg{
    uint16_t switch_ms;
} rainbow_cfg_t;

typedef struct breath_cfg{
    uint16_t switch_ms;
    color_t color;
} breath_cfg_t;

typedef struct random_cfg{
    bool mode;
}random_cfg_t;

typedef struct blink_cfg{
    color_t* colors;
    uint16_t period;
} blink_cfg_t;

typedef struct custom_breath_cfg {
    color_t* colors;
    uint16_t* fades_ms;
    uint8_t size;
}custom_breath_cfg_t;

typedef struct chase_cfg{
    color_t* colors;
    uint8_t size;
}chase_cfg_t;

typedef struct round_cfg {
    color_t color_main;
    color_t color_sub;
    float range;
    float range_main;
    int32_t wait_ms;
    float center_speed;
}round_cfg_t;

#endif
 /* __LIGHT_CFG_H__ */
