/**
 * @file oled_display_tash.h
 * @author LG_frir
 * @date 2026-04-16 19:23:57
 */

#ifndef __OLED_DISPLAY_TASK_H__
#define __OLED_DISPLAY_TASK_H__

#include ".Include/oled_include.h"
#include "stdint.h"

#define MAX_SLEEP_TIME 100

void oled_display_task_init(void);
void oled_display_fan_task(void* arg);
void oled_display_encoder_task(void* arg);
void oled_display(void* arg);

typedef struct oled_page_data_manager_handle {
    int64_t last_total;
    oled_item_t* oled_item;
    bool is_first_enter;
} oled_page_data_manager_t;

typedef struct oled_page_data_manager {
    int64_t last_total;
    int8_t cursor_line;
    int8_t start_line;
    uint64_t last_update;
    bool is_first_enter;
} oled_page_data_manager_static_t;


void oled_page_data_manager_init(oled_page_data_manager_t* handle, oled_item_t* oled_item);

void oled_page_data_update(oled_page_data_manager_t* handle,
                           int64_t new_total,
                           uint8_t page_size,
                           uint8_t visible_lines);

void oled_page_data_static_update(oled_page_data_manager_static_t* handle,
                                  int64_t new_total,
                                  uint8_t page_size,
                                  uint8_t visible_lines);


extern uint8_t oled_font;

#endif /* __OLED_DISPLAY_TASK_H__ */
