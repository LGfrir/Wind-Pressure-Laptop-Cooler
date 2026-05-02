/**
 * @file oled_display_task.c
 */

#include ".Include/oled_display_task.h"
#include ".Include/event_handler.h"
#include ".Include/oled.h"
#include ".Include/oled_include.h"
#include ".Include/oled_menu.h"

#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "stdint.h"


void oled_display_page(const oled_item_t* show_page) {
    if (show_page == NULL || show_page->state == NULL) {
        return;
    }

    uint8_t show_page_size = show_page->son_size;
    uint8_t visible_lines = 0;

    uint8_t font = oled_get_font();
    if (font == 0) {
        visible_lines = 0;
    }
    else if (font == 1) {
        visible_lines = 8;
    }
    else if (font == 2) {
        visible_lines = 4;
    }

    if (visible_lines == 0) {
        oled_clear();
        oled_print(0, 0, "Invalid Font");
        oled_update();
        return;
    }

    if (show_page_size == 0) {
        oled_clear();
        oled_print(0, 0, "Empty Page");
        oled_update();
        return;
    }

    static oled_page_data_manager_t page_data;

    if (page_data.oled_item != show_page) {
        oled_page_data_manager_init(&page_data, (oled_item_t*)show_page);
    }

    oled_page_data_update(&page_data, get_encoder_data().total, show_page_size, visible_lines);

    int8_t current_cursor_line = show_page->state->selected_son;
    int8_t current_menu_start_line = show_page->state->current_start;

    oled_clear();

    for (int8_t i = 0; i < visible_lines; i++) {
        int8_t page_line_index = current_menu_start_line + i;
        if (page_line_index >= show_page_size) {
            break;
        }

        oled_set_inv_color(page_line_index == current_cursor_line);

        oled_print(0,
                   i,
                   " %s",
                   show_page->son[page_line_index]->display_overview
                       ? show_page->son[page_line_index]->display_overview()
                       : "Function Name Error");
    }

    oled_set_inv_color(false);
    oled_update();
}



void oled_display(void* arg) {
    bool debug = *(bool*)arg;

    const oled_item_t* current_display_page = &menu;

    while (1) {
        press_state_t now_press_state = get_encoder_press_state();

        if (now_press_state == LONG_PRESSED) {
            if(current_display_page->state->receive_long_press == true) {
                current_display_page->state->operate = 2;
            }
            else if(current_display_page->parent != NULL) {
                current_display_page = current_display_page->parent;
            }

            set_encoder_press_state(NOT_PRESSED);
        }
        else if (now_press_state == SHORT_PRESSED) {

            if(current_display_page->state->receive_short_press == true) {
                if (current_display_page->state != NULL) {
                    current_display_page->state->operate = 1;
                }
            }
            else if (current_display_page->son_size != 0) {
                current_display_page = current_display_page->son[current_display_page->state->selected_son];
            }
            set_encoder_press_state(NOT_PRESSED);
        }

        if (current_display_page->son_size != 0) {
            oled_display_page(current_display_page);
        }
        else {
            if (current_display_page->display_detail != NULL)
                current_display_page->display_detail();
            else {
                oled_clear();
                oled_print(0, 0, "Display Detail");
                oled_print(0, 1, "Error");
                oled_update();
            }
        }
        vTaskDelay(pdMS_TO_TICKS(20));
    }
}


void oled_page_data_update(oled_page_data_manager_t* handle,
                           int64_t new_total,
                           uint8_t page_size,
                           uint8_t visible_lines) {
    if (handle == NULL || handle->oled_item == NULL || handle->oled_item->state == NULL) {
        return;
    }

    if (page_size == 0 || visible_lines == 0) {
        return;
    }

    int8_t* cursor_line = &handle->oled_item->state->selected_son;
    int8_t* start_line = &handle->oled_item->state->current_start;

    if (handle->is_first_enter) {
        handle->last_total = new_total;
        handle->is_first_enter = false;
    }
    else {
        int64_t diff = new_total - handle->last_total;
        *cursor_line += (int8_t)diff;
        handle->last_total = new_total;
    }

    if (*cursor_line < 0) {
        *cursor_line = 0;
    }
    else if (*cursor_line >= (int8_t)page_size) {
        *cursor_line = (int8_t)page_size - 1;
    }

    if (*cursor_line < *start_line) {
        *start_line = *cursor_line;
    }
    else if (*cursor_line >= *start_line + (int8_t)visible_lines) {
        *start_line = *cursor_line - (int8_t)visible_lines + 1;
    }

    if (page_size <= visible_lines) {
        *start_line = 0;
    }
    else {
        int8_t max_start = (int8_t)(page_size - visible_lines);
        if (*start_line > max_start) {
            *start_line = max_start;
        }
        if (*start_line < 0) {
            *start_line = 0;
        }
    }
}


void oled_page_data_static_update(oled_page_data_manager_static_t* handle,
                                  int64_t new_total,
                                  uint8_t page_size,
                                  uint8_t visible_lines) {

    if (handle == NULL) {
        return;
    }

    if (page_size == 0 || visible_lines == 0) {
        return;
    }

    if (handle->is_first_enter) {
        handle->last_total = new_total;
        handle->is_first_enter = false;
    }
    else {
        int64_t diff = new_total - handle->last_total;
        handle->cursor_line += (int8_t)diff;
        handle->last_total = new_total;
    }

    if (handle->cursor_line < 0) {
        handle->cursor_line = 0;
    }
    else if (handle->cursor_line >= (int8_t)page_size) {
        handle->cursor_line = (int8_t)page_size - 1;
    }

    if (handle->cursor_line < handle->start_line) {
        handle->start_line = handle->cursor_line;
    }
    else if (handle->cursor_line >= handle->start_line + (int8_t)visible_lines) {
        handle->start_line = handle->cursor_line - (int8_t)visible_lines + 1;
    }

    if (page_size <= visible_lines) {
        handle->start_line = 0;
    }
    else {
        int8_t max_start = (int8_t)(page_size - visible_lines);
        if (handle->start_line > max_start) {
            handle->start_line = max_start;
        }
        if (handle->start_line < 0) {
            handle->start_line = 0;
        }
    }

    handle->last_update = esp_timer_get_time() / 1000;
}


void oled_page_data_manager_init(oled_page_data_manager_t* handle, oled_item_t* oled_item) {
    if (handle == NULL) {
        return;
    }
    handle->last_total = 0;
    handle->oled_item = oled_item;
    handle->is_first_enter = true;
}
