#ifndef __LIGHT_H__
#define __LIGHT_H__

#include <stdint.h>
#include <stdbool.h>

#define LIGHT_PIXEL_COUNT 8

typedef struct color{
    uint8_t r;
    uint8_t g;
    uint8_t b;
} color_t;

typedef enum {
    LIGHT_MODE_OFF = 0,
    LIGHT_MODE_STATIC,
    LIGHT_MODE_RAINBOW,
    LIGHT_MODE_BREATH,
    LIGHT_MODE_RANDOM_FLASH,
    LIGHT_MODE_BLINK,
    LIGHT_MODE_CUSTOM_BREATH,
    LIGHT_MODE_CHASE,
    LIGHT_MODE_ROUND
} light_mode_t;



typedef struct light{
    light_mode_t mode;
    uint8_t brightness;
    uint8_t period;
}light_t;

extern light_t s_light;
void light_task(void* arg);
void light_set_mode(light_mode_t mode);
void light_init();
void light_test_task(void* arg);
#endif