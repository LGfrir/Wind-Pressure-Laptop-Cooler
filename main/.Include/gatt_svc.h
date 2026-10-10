/**
 * @file gatt_svc.h
 * @author LG_frir
 * @date 2026-10-09 13:35:31
 */

#ifndef __GATT_SVC_H__
#define __GATT_SVC_H__

#include "NimBLE.h"

#define FAN_SVC_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_svc

#define FAN_CHR_MODE_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x6d,0x6f,0x64,0x65,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_mode

#define FAN_CHR_PWM_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x70,0x77,0x6d,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_pwm

#define FAN_CHR_SPEED_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x73,0x70,0x65,0x65,0x64,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_speed

#define SYSTEM_SVC_UUID_REG BLE_UUID128_INIT \
(0x73,0x79,0x73,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: system_svc 

#define SCREEN_SVC_UUID_REG BLE_UUID128_INIT \
(0x73,0x63,0x72,0x65,0x65,0x6e,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: screen_svc

static const ble_uuid128_t FAN_SVC_UUID       = FAN_SVC_UUID_REG;
static const ble_uuid128_t FAN_CHR_MODE_UUID  = FAN_CHR_MODE_UUID_REG;
static const ble_uuid128_t FAN_CHR_PWM_UUID   = FAN_CHR_PWM_UUID_REG;
static const ble_uuid128_t FAN_CHR_SPEED_UUID = FAN_CHR_SPEED_UUID_REG;
int fan_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg);

static const ble_uuid128_t SYSTEM_SVC_UUID    = SYSTEM_SVC_UUID_REG;
static const ble_uuid128_t SCREEN_SVC_UUID    = SCREEN_SVC_UUID_REG;



// GATT 事件注册表
static const struct ble_gatt_svc_def gatt_svr_svcs[] = {
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &FAN_SVC_UUID.u,
        .characteristics = (struct ble_gatt_chr_def[]) {
            {
                .uuid = &FAN_CHR_MODE_UUID.u,
                .access_cb = fan_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)1
            },
            {
                .uuid = &FAN_CHR_PWM_UUID.u,
                .access_cb = fan_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)2
            },
            {
                .uuid = &FAN_CHR_SPEED_UUID.u,
                .access_cb = fan_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)3
            },
            {0}
        }
    },
    { 0 }
};

#endif /* __GATT_SVR_H__ */