/**
 * @file oled_include.h
 * @author LG_frir
 * @date 2026-04-20 20:08:32
 */

#ifndef __OLED_INCLUDE_H__
#define __OLED_INCLUDE_H__

#include "FreeRTOS/FreeRTOS.h"
#include "FreeRTOS/semphr.h"
#include "FreeRTOS/task.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "stdint.h"

#include ".Include/event_handler.h"
#include ".Include/oled.h"
#include ".Include/fan.h"
#include "system_monitor.h"

typedef struct oled_item_state{
    int8_t selected_son;
    int8_t current_start;
    uint8_t operate;
    bool receive_short_press;
    bool receive_long_press;
} oled_item_state_t;

typedef struct oled_item {
    char* (*display_overview)(void);
    void (*display_detail)(void);
    const struct oled_item* const* son;
    uint8_t son_size;
    const struct oled_item* parent;
    oled_item_state_t* state;
} oled_item_t;



#endif /* __OLED_INCLUDE_H__ */
