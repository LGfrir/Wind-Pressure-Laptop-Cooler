#include <stdio.h>
#include ".Include/encoder.h"
#include ".Include/event_handler.h"
#include "driver/uart.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "light.h"
#define UART_NUM UART_NUM_0
#define BUF_SIZE 256

void uart_debug_task(void* arg) {
    uart_config_t cfg = {
        .baud_rate = 115200,
        .data_bits = UART_DATA_8_BITS,
        .parity = UART_PARITY_DISABLE,
        .stop_bits = UART_STOP_BITS_1,
        .flow_ctrl = UART_HW_FLOWCTRL_DISABLE,
    };
    uart_param_config(UART_NUM, &cfg);
    uart_driver_install(UART_NUM, BUF_SIZE, 0, 0, NULL, 0);

    uint8_t data;
    while (1) {
        int len = uart_read_bytes(UART_NUM, &data, 1, portMAX_DELAY);
        if (len > 0) {
            switch (data) {
            case '1':
                /* 上 */
                encoder_set_count(encoder_get_count() - 1);
                break;
            case '2': /* 下 */
                encoder_set_count(encoder_get_count() + 1);
                break;
            case '3': /* 左 */
                set_encoder_press_state(LONG_PRESSED);
                break;
            case '4': /* 右 */
                set_encoder_press_state(SHORT_PRESSED);
                break;
            case '5': /* 确认 */
                s_light.mode++;
                if(s_light.mode == 9) s_light.mode = 0;
                break;
            default:
                break;
            }
        }
        vTaskDelay(pdMS_TO_TICKS(10));
    }
}
