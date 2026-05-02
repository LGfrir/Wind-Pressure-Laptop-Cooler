#include ".Include/encoder.h"
#include ".Include/event_handler.h"
#include ".Include/fan.h"
#include ".Include/gpio.h"
#include ".Include/i2c_bus.h"
#include ".Include/oled.h"
#include ".Include/light.h"
#include ".Include/oled_image.h"
#include "system_monitor.h"
#include "test.h"
#include ".Include/oled_display_task.h"

#include "driver/gpio.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>


static const char* TAG = "MAIN";

uint8_t fan_display_line = 2;
uint8_t encoder_display_line = 2;


/* ========================= main ========================= */
void app_main(void) {
    // 你的外设初始化
    gpio_install_isr_service(0);
    i2c_bus_init();
    oled_init();
    encoder_init();
    fan_init();
    system_monitor_init();
    oled_set_font(1);
    data_handler_init();
    light_init();

    xTaskCreate(data_handler_task, "data_handler_task", 2048, NULL, 4, NULL);
    xTaskCreate(oled_display, "oled_display_task", 4096 * 4, NULL, 2, NULL);
    xTaskCreate(uart_debug_task, "uart_debug_task", 4096, NULL, 2, NULL); 
    xTaskCreate(light_task, "ggg", 4096, NULL, 5, NULL); 

    
}