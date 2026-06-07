/**
 * @file oled_display_task.c
 */

#include ".Include/oled_display_task.h"
#include ".Include/oled_menu.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

void oled_display(void* arg) {
    (void)arg;

    while (1) {
        oled_render_current();
        vTaskDelay(pdMS_TO_TICKS(500));
    }
}
