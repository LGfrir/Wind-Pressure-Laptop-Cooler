/**
 * @file fan.c
 * @author LG_frir
 * @date 2026-04-16 15:15:52
 */

#include ".Include/fan.h"
#include ".Include/gpio.h"

#include "driver/gpio.h"
#include "driver/ledc.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define PWM_GPIO FAN_PWM_PIN
#define PWM_TIMER LEDC_TIMER_0
#define PWM_MODE LEDC_LOW_SPEED_MODE
#define PWM_CHANNEL LEDC_CHANNEL_0
#define PWM_DUTY_RES LEDC_TIMER_10_BIT
#define PWM_FREQUENCY 25000

fan_t fan = {
    .speed_percentage = 0,
    .is_on = false,
    .mode = MANUAL,
};
static volatile uint32_t s_tach_pulse_count = 0;
static portMUX_TYPE s_tach_mux = portMUX_INITIALIZER_UNLOCKED;

static volatile uint64_t s_last_tach_us = 0;
static void IRAM_ATTR fan_tach_isr_handler(void* arg) {
    uint64_t now = esp_timer_get_time();

    if (now - s_last_tach_us > 1000) {
        portENTER_CRITICAL_ISR(&s_tach_mux);
        s_tach_pulse_count++;
        portEXIT_CRITICAL_ISR(&s_tach_mux);

        s_last_tach_us = now;
    }
}


uint32_t fan_get_rpm(void) { return fan.rpm; }

void fan_send_pwm(uint8_t percentage) {
    // Implementation for sending PWM signal to control fan speed
    uint32_t duty = ((100 - percentage) * ((1 << PWM_DUTY_RES) - 1)) / 100;
    // ESP_LOGI("FAN", "Setting PWM duty: %u for speed percentage: %u%%", duty, percentage);
    ESP_ERROR_CHECK(ledc_set_duty(PWM_MODE, PWM_CHANNEL, duty));
    ESP_ERROR_CHECK(ledc_update_duty(PWM_MODE, PWM_CHANNEL));
}

void fan_task(void* arg) {
    fan.is_on = true;
    int64_t last_sample_us = esp_timer_get_time();

    while (1) {
        vTaskDelay(pdMS_TO_TICKS(1000));

        // 读取并清零脉冲计数
        uint32_t delta_pulse;
        portENTER_CRITICAL(&s_tach_mux);
        delta_pulse = s_tach_pulse_count;
        s_tach_pulse_count = 0;
        portEXIT_CRITICAL(&s_tach_mux);

        // 用实际时间差算 RPM，不依赖 vTaskDelay 精度
        int64_t now_us = esp_timer_get_time();
        int64_t actual_us = now_us - last_sample_us;
        last_sample_us = now_us;

        if (actual_us > 0) {
            fan.rpm = (uint32_t)(((uint64_t)delta_pulse * 60ULL * 1000000ULL) / (2ULL * (uint64_t)actual_us));
        }
        else {
            fan.rpm = 0;
        }

        // ESP_LOGI("FAN", "tach pulse: %lu, rpm: %lu", delta_pulse, s_fan_rpm);
    }
}

double kp = 1;
double ki = 1;
double kd = 1;

void fan_speed_syncing_task() {
    while(1) {
        if(fan.is_on) {
            if(fan.mode == MANUAL) {
                fan_send_pwm(fan.speed_percentage);
            }
            else if(fan.mode == AUTO) {
                double e = fan.rpm - fan.target_rpm;

            } else if(fan.mode == FULL_SPEED) {
                fan_send_pwm(100);
            }
        }
        else fan_send_pwm(0);
    }
}

void fan_init(void) {
    ledc_timer_config_t timer_cfg = { .speed_mode = LEDC_LOW_SPEED_MODE,
                                      .timer_num = LEDC_TIMER_0,
                                      .duty_resolution = LEDC_TIMER_10_BIT,
                                      .freq_hz = PWM_FREQUENCY,
                                      .clk_cfg = LEDC_AUTO_CLK };
    ESP_ERROR_CHECK(ledc_timer_config(&timer_cfg));

    ledc_channel_config_t channel_cfg = { .speed_mode = LEDC_LOW_SPEED_MODE,
                                          .channel = LEDC_CHANNEL_0,
                                          .timer_sel = LEDC_TIMER_0,
                                          .intr_type = LEDC_INTR_DISABLE,
                                          .gpio_num = PWM_GPIO,
                                          .duty = 0,
                                          .hpoint = 0 };
    ESP_ERROR_CHECK(ledc_channel_config(&channel_cfg));

    gpio_config_t tach_conf = { .pin_bit_mask = (1ULL << FAN_TACH_PIN),
                                .mode = GPIO_MODE_INPUT,
                                .pull_up_en = GPIO_PULLUP_ENABLE,
                                .pull_down_en = GPIO_PULLDOWN_DISABLE,
                                .intr_type = GPIO_INTR_NEGEDGE };
    gpio_config(&tach_conf);

    gpio_isr_handler_add(FAN_TACH_PIN, fan_tach_isr_handler, NULL);


    fan_set_speed(0);
    xTaskCreate(fan_task, "fan_task", 2048, NULL, 1, NULL);
}


void fan_set_speed(uint8_t speed) {
    if (speed > 100)
        speed = 100;
    if (speed <= 0)
        speed = 0;

    fan.speed_percentage = speed;
    fan_send_pwm(speed);
}

void fan_set_target_rpm(uint32_t target_rpm) {
    fan.target_rpm = target_rpm;
}

fan_mode_t fan_get_mode(void) { return fan.mode; }

uint8_t* fan_get_curve() { return fan.pwm_t_curve; }

