#include "ModuleDriver/encoder.h"
#include "Tasks/event_handler.h"
#include "ModuleDriver/fan.h"
#include "Basic/gpio.h"
#include "Basic/i2c_bus.h"
#include "ModuleDriver/oled.h"
#include "Tasks/light_task.h"
#include "oled/Resources/oled_image.h"
#include "ModuleDriver/system_monitor.h"
#include "test.h"
#include "Tasks/oled_display_task.h"
#include "funny.h"

#include "driver/gpio.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include <string.h>
#include "ModuleDriver/NimBLE.h"


static const char* TAG = "MAIN";

uint8_t fan_display_line = 2;
uint8_t encoder_display_line = 2;

/* ========================= main ========================= */
void app_main(void) {

    // 蓝牙初始化
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ret = nvs_flash_init();
    }
    ESP_ERROR_CHECK(ret);

    gpio_install_isr_service(0);
    i2c_bus_init();
    oled_init();
    encoder_init();
    fan_init(); 
    system_monitor_init();
    oled_set_font(1);
    data_handler_init();
    light_init();

    nimble_init_as_prph(); 



    xTaskCreate(data_handler_task, "data_handler_task", 2048, NULL, 4, NULL);
    xTaskCreate(oled_display, "oled_display_task", 4096 * 4, NULL, 2, NULL);
    xTaskCreate(uart_debug_task, "uart_debug_task", 4096, NULL, 2, NULL); 
    xTaskCreate(light_task, "light_task", 4096, NULL, 5, NULL); 

    xTaskCreate(onboard_rgb_task, "onboard_rgb_task", 4096, NULL, 3, NULL);

    // xTaskCreate(onboard_rgb_flash_task, "onboard_rgb_flash_task", 4096, NULL, 3, NULL);

    // xTaskCreate(onboard_rgb_rage_task, "onboard_rgb_rage_task", 4096, NULL, 3, NULL);


    while(1) {
        vTaskDelay(pdMS_TO_TICKS(10));
    }
    
}
