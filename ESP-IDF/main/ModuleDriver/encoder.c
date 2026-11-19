#include "ModuleDriver/encoder.h"
#include "Basic/gpio.h"

#include <stdbool.h>
#include <stdint.h>

#include "driver/gpio.h"
#include "esp_err.h"
#include "freertos/FreeRTOS.h"
#include "freertos/portmacro.h"

static volatile int32_t s_encoder_count = 0;
static volatile int8_t s_encoder_diff = 0;

static volatile uint8_t s_last_state = 0;
static volatile int8_t s_step_acc = 0;

static portMUX_TYPE s_encoder_mux = portMUX_INITIALIZER_UNLOCKED;

// old_state(2bit) + new_state(2bit) => step
static const int8_t DRAM_ATTR transition_table[16] = {0, -1, 1, 0, 1, 0, 0, -1, -1, 0, 0, 1, 0, 1, -1, 0};

static inline uint8_t encoder_read_ab(void) {
    uint8_t a = gpio_get_level(EC_A_PIN);
    uint8_t b = gpio_get_level(EC_B_PIN);
    return (a << 1) | b;
}

static void IRAM_ATTR encoder_ab_isr_handler(void* arg) {
    uint8_t new_state = encoder_read_ab();

    portENTER_CRITICAL_ISR(&s_encoder_mux);

    uint8_t index = (s_last_state << 2) | new_state;
    int8_t step = transition_table[index];

    if (step != 0) {
        s_step_acc += step;

        if (s_step_acc >= 4) {
            s_encoder_count++;
            if (s_encoder_diff < 127) {
                s_encoder_diff++;
            }
            s_step_acc = 0;
        }
        else if (s_step_acc <= -4) {
            s_encoder_count--;
            if (s_encoder_diff > -128) {
                s_encoder_diff--;
            }
            s_step_acc = 0;
        }
    }

    s_last_state = new_state;

    portEXIT_CRITICAL_ISR(&s_encoder_mux);
}

void encoder_init(void) {
    gpio_config_t ab_conf = {.pin_bit_mask = (1ULL << EC_A_PIN) | (1ULL << EC_B_PIN),
                             .mode = GPIO_MODE_INPUT,
                             .pull_up_en = GPIO_PULLUP_ENABLE,
                             .pull_down_en = GPIO_PULLDOWN_DISABLE,
                             .intr_type = GPIO_INTR_ANYEDGE};
    ESP_ERROR_CHECK(gpio_config(&ab_conf));

    gpio_config_t sw_conf = {.pin_bit_mask = (1ULL << EC_SW_PIN),
                             .mode = GPIO_MODE_INPUT,
                             .pull_up_en = GPIO_PULLUP_DISABLE,
                             .pull_down_en = GPIO_PULLDOWN_ENABLE,
                             .intr_type = GPIO_INTR_DISABLE};
    ESP_ERROR_CHECK(gpio_config(&sw_conf));

    portENTER_CRITICAL(&s_encoder_mux);
    s_encoder_count = 0;
    s_encoder_diff = 0;
    s_step_acc = 0;
    portEXIT_CRITICAL(&s_encoder_mux);

    s_last_state = encoder_read_ab();

    ESP_ERROR_CHECK(gpio_isr_handler_add(EC_A_PIN, encoder_ab_isr_handler, NULL));
    ESP_ERROR_CHECK(gpio_isr_handler_add(EC_B_PIN, encoder_ab_isr_handler, NULL));
}

int32_t encoder_get_count(void) {
    int32_t value;
    portENTER_CRITICAL(&s_encoder_mux);
    value = s_encoder_count;
    portEXIT_CRITICAL(&s_encoder_mux);
    return value;
}

void encoder_set_count(int32_t count) {
    portENTER_CRITICAL(&s_encoder_mux);
    s_encoder_count = count;
    s_encoder_diff = 0;
    s_step_acc = 0;
    portEXIT_CRITICAL(&s_encoder_mux);

    s_last_state = encoder_read_ab();
}

int8_t encoder_get_diff(void) {
    int8_t value;
    portENTER_CRITICAL(&s_encoder_mux);
    value = s_encoder_diff;
    s_encoder_diff = 0;
    portEXIT_CRITICAL(&s_encoder_mux);
    return value;
}

void encoder_clear_count(void) {
    portENTER_CRITICAL(&s_encoder_mux);
    s_encoder_count = 0;
    s_encoder_diff = 0;
    s_step_acc = 0;
    portEXIT_CRITICAL(&s_encoder_mux);

    s_last_state = encoder_read_ab();
}

bool encoder_button_get(void) { return gpio_get_level(EC_SW_PIN); }
