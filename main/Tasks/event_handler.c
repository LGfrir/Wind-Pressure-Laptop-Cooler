/**
 * @file event_handler.c
 * @author LG_frir
 * @date 2026-04-16 23:30:27
 */

#include "Tasks/event_handler.h"
#include "ModuleDriver/encoder.h"
#include "ModuleDriver/fan.h"

#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "ModuleDriver/NimBLE.h"

static encoder_data_t s_encoder_data;
static uint32_t s_rpm;


static volatile press_state_t encoder_press_state = NOT_PRESSED;

void set_encoder_press_state(press_state_t p) { encoder_press_state = p; }

press_state_t get_encoder_press_state(void) {
    press_state_t temp = encoder_press_state;
    return temp;
}

void encoder_data_handler_task(void* arg) {
    const uint32_t DEBOUNCE_MS = 30;
    const uint32_t LONG_PRESS_MS = 300;

    bool raw_level = false; // 当前原始电平
    bool last_raw_level = false; // 上一次原始电平
    bool stable_level = false; // 消抖后的稳定电平
    bool last_stable_level = false; // 上一次稳定电平

    uint64_t last_change_time = 0; // 原始电平最后变化时间
    uint64_t press_start_time = 0; // 稳定按下开始时间
    bool long_reported = false; // 是否已经上报长按

    while (1) {
        s_encoder_data.total = encoder_get_count();
        s_encoder_data.diff = encoder_get_diff();

        uint64_t now = esp_timer_get_time() / 1000; // ms
        raw_level = encoder_button_get(); // true=按下, false=松开

        // 原始电平变化，更新时间戳
        if (raw_level != last_raw_level) {
            last_raw_level = raw_level;
            last_change_time = now;
        }

        // 原始电平保持足够久，认为稳定
        if ((now - last_change_time) >= DEBOUNCE_MS) {
            stable_level = raw_level;
        }

        // 稳定状态发生变化
        if (stable_level != last_stable_level) {
            last_stable_level = stable_level;

            if (stable_level) {
                // 稳定按下
                press_start_time = now;
                long_reported = false;
            }
            else {
                // 稳定松开
                if (!long_reported) {
                    uint64_t press_duration = now - press_start_time;
                    if (press_duration >= DEBOUNCE_MS) {
                        encoder_press_state = SHORT_PRESSED;
                        ESP_LOGI("ENCODER", "SHORT PRESSED");
                    }
                }
            }
        }

        // 按住过程中检测长按
        if (stable_level && !long_reported) {
            if ((now - press_start_time) >= LONG_PRESS_MS) {
                encoder_press_state = LONG_PRESSED;
                long_reported = true;
                ESP_LOGI("ENCODER", "LONG PRESSED");
            }
        }

        vTaskDelay(pdMS_TO_TICKS(10));
    }
}

void set_encoder_data(int32_t total) {
    s_encoder_data.total = total;
    s_encoder_data.diff = 0;

    encoder_set_count(total); // 同步更新编码器计数器
}

encoder_data_t get_encoder_data(void) { return s_encoder_data; }



void data_handler_init(void) {
    ESP_LOGI("DATA_HANDLER_INIT", "Creating Tasks...");

    ESP_LOGI("DATA_HANDLER_INIT", "Creating encoder data handler task...");
    xTaskCreate(encoder_data_handler_task, "encoder_data_handler", 4096, NULL, 1, NULL);

    ESP_LOGI("DATA_HANDLER_INIT", "Creating RPM handler task...");
}


void data_handler_task(void* arg) {
    // 这里可以添加处理数据的逻辑，例如从队列读取数据，进行计算等

    while (1) {


        vTaskDelay(pdMS_TO_TICKS(200)); // 模拟处理时间，每0.2秒处理一次
    }
}
