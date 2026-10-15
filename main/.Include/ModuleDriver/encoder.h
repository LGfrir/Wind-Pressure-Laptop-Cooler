#ifndef ENCODER_H
#define ENCODER_H

#pragma once

#include <stdbool.h>
#include <stdint.h>

void encoder_init(void);

int32_t encoder_get_count(void);
void encoder_set_count(int32_t count);
int8_t encoder_get_diff(void);
void encoder_clear_count(void);

bool encoder_button_get(void);


#endif // ENCODER_H
