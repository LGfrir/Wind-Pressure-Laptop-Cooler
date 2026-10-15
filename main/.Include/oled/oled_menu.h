/**
 * @file oled_menu.h
 * @author LG_frir
 * @date 2026-04-20 20:07:57
 */

#ifndef __OLED_MENU_H__
#define __OLED_MENU_H__

#include "oled/oled_include.h"

// 第一级
extern const oled_item_t menu;

// 第二级
extern const oled_item_t fan_info;
extern const oled_item_t encoder_info;
extern const oled_item_t system_info;
extern const oled_item_t computer_info;
extern const oled_item_t settings;
extern const oled_item_t quick_settings;

// 第三级
// fan_info
extern const oled_item_t fan_speed;
extern const oled_item_t fan_mode;
extern const oled_item_t fan_speed_curve;

// system_info
extern const oled_item_t esp32s3_cpu;
extern const oled_item_t esp32s3_heap;
extern const oled_item_t esp32s3_runtime;
extern const oled_item_t esp32s3_task;


// computer_info
extern const oled_item_t computer_cpu;
extern const oled_item_t computer_gpu;
extern const oled_item_t computer_ram;
extern const oled_item_t computer_network;

// settings
extern const oled_item_t fan_settings;
extern const oled_item_t pwm_settings;
extern const oled_item_t button_settings;
extern const oled_item_t encoder_settings;
extern const oled_item_t light_mode_settings;
extern const oled_item_t light_settings;

// 第四级
// fan_settings
extern const oled_item_t fan_speed_curve_settings;
extern const oled_item_t fan_mode_settings;

// button_settings
extern const oled_item_t button_longpress;
extern const oled_item_t button_shortpress;

// light mode
extern const oled_item_t light_brightness_settings;
extern const oled_item_t light_mode_settings;
extern const oled_item_t light_period_settings;

// 第五级

extern const oled_item_t light_mode_off; 
extern const oled_item_t light_mode_static; 
extern const oled_item_t light_mode_breath; 
extern const oled_item_t light_mode_blink; 
extern const oled_item_t light_mode_chase; 
extern const oled_item_t light_mode_rainbow; 
extern const oled_item_t light_mode_round; 
extern const oled_item_t light_mode_custom_breath; 
extern const oled_item_t light_mode_random_flash; 


#endif /* __OLED_MENU_H__ */
