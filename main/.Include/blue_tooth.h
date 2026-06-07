/**
 * @file blue_tooth.h
 * @author LG_frir
 * @date 2026-04-23 16:34:19
 */

#ifndef __BLUE_TOOTH_H__
#define __BLUE_TOOTH_H__

#include <stdbool.h>
#include <stdint.h>

#define SMARTFAN_BLE_DEVICE_NAME "SmartFan-S3"

void blue_tooth_init(void);
bool blue_tooth_is_connected(void);
void blue_tooth_notify_esp_status(void);

#endif /* __BLUE_TOOTH_H__ */
