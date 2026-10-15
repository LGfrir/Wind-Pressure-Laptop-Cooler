/**
 * @file event_handler.h
 * @author LG_frir
 * @date 2026-04-16 23:30:53
 */

#ifndef __EVENT_HANDLER_H__
#define __EVENT_HANDLER_H__

#include "stdint.h"
typedef enum { NOT_PRESSED = 0, SHORT_PRESSED = 1, LONG_PRESSED = 2, } press_state_t;

void data_handler_init(void);


typedef struct {
    int32_t total;
    int8_t diff;
} encoder_data_t;

encoder_data_t get_encoder_data(void);
void set_encoder_data(int32_t total);
press_state_t get_encoder_press_state(void);
void set_encoder_press_state(press_state_t p);

void data_handler_task(void* arg);

#endif /* __EVENT_HANDLER_H__ */
