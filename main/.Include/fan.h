/**
 * @file fan.h
 * @author LG_frir
 * @date 2026-04-16 15:16:15
 */

#ifndef FAN_H
#define FAN_H

#include <stdint.h>
#include <stdbool.h>
#define SPEED_CURVE_MEMBER 10
#define FAN_MODE_SIZE 3

typedef enum { MANUAL = 0, AUTO = 1, FULL_SPEED = 2 } fan_mode_t;

typedef struct fan{
    uint8_t speed_percentage;
    bool is_on;
    fan_mode_t mode;
    uint8_t pwm_t_curve[SPEED_CURVE_MEMBER];
} fan_t;

extern fan_t fan;

void fan_init(void);
void fan_set_speed(uint8_t speed);
void fan_send_pwm(uint8_t percentage);
uint32_t fan_get_rpm(void);
fan_mode_t fan_get_mode(void);
uint8_t* fan_get_curve(void);



#endif