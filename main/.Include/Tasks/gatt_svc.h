/**
 * @file gatt_svc.h
 * @author LG_frir
 * @date 2026-10-09 13:35:31
 */

#ifndef __GATT_SVC_H__
#define __GATT_SVC_H__

#include "ModuleDriver/NimBLE.h"
#include "Data/laptop_sys.h"
/* 服务表里用 LIGHT_MODE_xxx 当特征的 arg，必须拿得到 light_mode_t 这组枚举 */
#include "Tasks/light_task.h"

/*
    风扇相关
*/

#define FAN_SVC_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_svc

#define FAN_CHR_MODE_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x6d,0x6f,0x64,0x65,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_mode

#define FAN_CHR_PWM_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x70,0x77,0x6d,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_pwm

#define FAN_CHR_RPM_UUID_REG BLE_UUID128_INIT \
(0x66,0x61,0x6e,0x5f,0x72,0x70,0x6d,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: fan_rpm

#define FAN_CHR_LIGHT_MODE_UUID_REG BLE_UUID128_INIT \
(0x6c,0x69,0x67,0x68,0x74,0x5f,0x6d,0x6f,0x64,0x65,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: light_mode

static const ble_uuid128_t FAN_SVC_UUID                 = FAN_SVC_UUID_REG;
static const ble_uuid128_t FAN_CHR_MODE_UUID            = FAN_CHR_MODE_UUID_REG;
static const ble_uuid128_t FAN_CHR_PWM_UUID             = FAN_CHR_PWM_UUID_REG;
static const ble_uuid128_t FAN_CHR_RPM_UUID             = FAN_CHR_RPM_UUID_REG;
int fan_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg);

/*
    灯光相关
*/
#define LIGHT_SVC_UUID_REG BLE_UUID128_INIT \
(0x6c,0x69,0x67,0x68,0x74,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: light_svc

#define LIGHT_CHR_MODE_UUID_REG BLE_UUID128_INIT \
(0x6c,0x69,0x67,0x68,0x74,0x5f,0x6d,0x6f,0x64,0x65,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: light_mode

#define LIGHT_CHR_UPDATE_PERIOD_UUID_REG BLE_UUID128_INIT \
(0x6c,0x69,0x67,0x68,0x74,0x5f,0x75,0x70,0x64,0x61,0x74,0x65,0x5f,0x70,0x65,0x72)
// ASCII: light_update_per

#define LIGHT_CHR_BRIGHTNESS_UUID_REG BLE_UUID128_INIT \
(0x6c,0x69,0x67,0x68,0x74,0x5f,0x62,0x72,0x69,0x67,0x68,0x74,0x6e,0x65,0x73,0x73)
// ASCII: light_brightness

static const ble_uuid128_t LIGHT_SVC_UUID                 = LIGHT_SVC_UUID_REG;
static const ble_uuid128_t LIGHT_CHR_MODE_UUID            = LIGHT_CHR_MODE_UUID_REG;
static const ble_uuid128_t LIGHT_CHR_UPDATE_PERIOD_UUID   = LIGHT_CHR_UPDATE_PERIOD_UUID_REG;
static const ble_uuid128_t LIGHT_CHR_BRIGHTNESS_UUID      = LIGHT_CHR_BRIGHTNESS_UUID_REG;
int light_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg);

/*
    参数设置相关
*/
#define CONFIG_SVC_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: cfg_svc

#define CONFIG_CHR_LIGHT_RAINBOW_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x72,0x61,0x69,0x6e,0x62,0x6f,0x77,0x00,0x00)
// ASCII: cfg_lt_rainbow

#define CONFIG_CHR_LIGHT_STATIC_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x73,0x74,0x61,0x74,0x69,0x63,0x00,0x00,0x00)
// ASCII: cfg_lt_static

#define CONFIG_CHR_LIGHT_BREATH_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x62,0x72,0x65,0x61,0x74,0x68,0x00,0x00,0x00)
// ASCII: cfg_lt_breath

#define CONFIG_CHR_LIGHT_RANDOM_FLASH_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x72,0x61,0x6e,0x64,0x66,0x6c,0x61,0x73,0x68)
// ASCII: cfg_lt_randflash

#define CONFIG_CHR_LIGHT_BLINK_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x62,0x6c,0x69,0x6e,0x6b,0x00,0x00,0x00,0x00)
// ASCII: cfg_lt_blink

#define CONFIG_CHR_LIGHT_CUSTOM_BREATH_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x63,0x75,0x73,0x74,0x62,0x72,0x65,0x61,0x74)
// ASCII: cfg_lt_custbreath

#define CONFIG_CHR_LIGHT_CHASE_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x63,0x68,0x61,0x73,0x65,0x00,0x00,0x00,0x00)
// ASCII: cfg_lt_chase

#define CONFIG_CHR_LIGHT_ROUND_UUID_REG BLE_UUID128_INIT \
(0x63,0x66,0x67,0x5f,0x6c,0x74,0x5f,0x72,0x6f,0x75,0x6e,0x64,0x00,0x00,0x00,0x00)
// ASCII: cfg_lt_round

static const ble_uuid128_t CONFIG_SVC_UUID                      = CONFIG_SVC_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_STATIC_UUID         = CONFIG_CHR_LIGHT_STATIC_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_RAINBOW_UUID        = CONFIG_CHR_LIGHT_RAINBOW_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_BREATH_UUID         = CONFIG_CHR_LIGHT_BREATH_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_RANDOM_FLASH_UUID   = CONFIG_CHR_LIGHT_RANDOM_FLASH_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_BLINK_UUID          = CONFIG_CHR_LIGHT_BLINK_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_CUSTOM_BREATH_UUID  = CONFIG_CHR_LIGHT_CUSTOM_BREATH_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_CHASE_UUID          = CONFIG_CHR_LIGHT_CHASE_UUID_REG;
static const ble_uuid128_t CONFIG_CHR_LIGHT_ROUND_UUID          = CONFIG_CHR_LIGHT_ROUND_UUID_REG;

int config_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg);

#define LAPTOP_SVC_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: laptop_svc

#define LAPTOP_CHR_CPU_USAGE_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x63,0x70,0x75,0x5f,0x75,0x73,0x61,0x67,0x65)
// ASCII: laptop_cpu_usage

#define LAPTOP_CHR_CPU_TEMP_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x63,0x70,0x75,0x5f,0x74,0x65,0x6d,0x70,0x00)
// ASCII: laptop_cpu_temp

#define LAPTOP_CHR_CPU_VOLTAGE_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x63,0x70,0x75,0x5f,0x76,0x6f,0x6c,0x74,0x61)
// ASCII: laptop_cpu_volta

#define LAPTOP_CHR_CPU_CLOCK_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x63,0x70,0x75,0x5f,0x63,0x6c,0x6f,0x63,0x6b)
// ASCII: laptop_cpu_clock

#define LAPTOP_CHR_GPU_USAGE_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x67,0x70,0x75,0x5f,0x75,0x73,0x61,0x67,0x65)
// ASCII: laptop_gpu_usage

#define LAPTOP_CHR_GPU_TEMP_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x67,0x70,0x75,0x5f,0x74,0x65,0x6d,0x70,0x00)
// ASCII: laptop_gpu_temp

#define LAPTOP_CHR_GPU_CLOCK_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x67,0x70,0x75,0x5f,0x63,0x6c,0x6f,0x63,0x6b)
// ASCII: laptop_gpu_clock

#define LAPTOP_CHR_GPU_RAM_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x67,0x70,0x75,0x5f,0x72,0x61,0x6d,0x00,0x00)
// ASCII: laptop_gpu_ram

#define LAPTOP_CHR_GPU_TOT_RAM_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x67,0x70,0x75,0x5f,0x74,0x6f,0x74,0x5f,0x72)
// ASCII: laptop_gpu_tot_r

#define LAPTOP_CHR_RAM_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x72,0x61,0x6d,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: laptop_ram

#define LAPTOP_CHR_TOT_RAM_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x74,0x6f,0x74,0x5f,0x72,0x61,0x6d,0x00,0x00)
// ASCII: laptop_tot_ram

#define LAPTOP_CHR_FAN_USED_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x66,0x61,0x6e,0x5f,0x75,0x73,0x65,0x64,0x00)
// ASCII: laptop_fan_used

#define LAPTOP_CHR_FAN_SPEED_UUID_REG BLE_UUID128_INIT \
(0x6c,0x61,0x70,0x74,0x6f,0x70,0x5f,0x66,0x61,0x6e,0x5f,0x73,0x70,0x65,0x65,0x64)
// ASCII: laptop_fan_speed


static const ble_uuid128_t LAPTOP_SVC_UUID                  = LAPTOP_SVC_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_CPU_USAGE_UUID        = LAPTOP_CHR_CPU_USAGE_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_CPU_TEMP_UUID         = LAPTOP_CHR_CPU_TEMP_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_CPU_VOLTAGE_UUID      = LAPTOP_CHR_CPU_VOLTAGE_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_CPU_CLOCK_UUID        = LAPTOP_CHR_CPU_CLOCK_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_GPU_USAGE_UUID        = LAPTOP_CHR_GPU_USAGE_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_GPU_TEMP_UUID         = LAPTOP_CHR_GPU_TEMP_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_GPU_CLOCK_UUID        = LAPTOP_CHR_GPU_CLOCK_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_GPU_RAM_UUID          = LAPTOP_CHR_GPU_RAM_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_GPU_TOT_RAM_UUID      = LAPTOP_CHR_GPU_TOT_RAM_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_RAM_UUID              = LAPTOP_CHR_RAM_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_TOT_RAM_UUID          = LAPTOP_CHR_TOT_RAM_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_FAN_USED_UUID         = LAPTOP_CHR_FAN_USED_UUID_REG;
static const ble_uuid128_t LAPTOP_CHR_FAN_SPEED_UUID        = LAPTOP_CHR_FAN_SPEED_UUID_REG;
int laptop_sys_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg);


#define SYSTEM_SVC_UUID_REG BLE_UUID128_INIT \
(0x73,0x79,0x73,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: system_svc 

#define SCREEN_SVC_UUID_REG BLE_UUID128_INIT \
(0x73,0x63,0x72,0x65,0x65,0x6e,0x5f,0x73,0x76,0x63,0x00,0x00,0x00,0x00,0x00,0x00)
// ASCII: screen_svc


static const ble_uuid128_t SYSTEM_SVC_UUID              = SYSTEM_SVC_UUID_REG;
static const ble_uuid128_t SCREEN_SVC_UUID              = SCREEN_SVC_UUID_REG;



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
                .flags = BLE_GATT_CHR_F_READ |BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)2
            },
            {
                .uuid = &FAN_CHR_RPM_UUID.u,
                .access_cb = fan_svc_callback,
                .flags = BLE_GATT_CHR_F_NOTIFY | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)3
            },
            { 0 }
        }
        
    },
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &LIGHT_SVC_UUID.u,
        .characteristics = (struct ble_gatt_chr_def[]) {
            {
                .uuid = &LIGHT_CHR_MODE_UUID.u,
                .access_cb = light_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)1
            },
            {
                .uuid = &LIGHT_CHR_UPDATE_PERIOD_UUID.u,
                .access_cb = light_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)2
            },
            {
                .uuid = & LIGHT_CHR_BRIGHTNESS_UUID.u,
                .access_cb = light_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*) (intptr_t)3
            },
            { 0 }
        }
    },
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &CONFIG_SVC_UUID.u,
        .characteristics = (struct ble_gatt_chr_def[]) {
            {
                .uuid = &CONFIG_CHR_LIGHT_STATIC_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_STATIC
            },
            {
                .uuid = &CONFIG_CHR_LIGHT_RAINBOW_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_RAINBOW
            },
            {
                .uuid = &CONFIG_CHR_LIGHT_BREATH_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_BREATH
            },
            {
                .uuid = &CONFIG_CHR_LIGHT_RANDOM_FLASH_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_RANDOM_FLASH
            },
            {
                .uuid = &CONFIG_CHR_LIGHT_BLINK_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_BLINK
            },
            {
                .uuid = &CONFIG_CHR_LIGHT_CUSTOM_BREATH_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_CUSTOM_BREATH
            },
            {
                .uuid = &CONFIG_CHR_LIGHT_CHASE_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_CHASE
            },
            {
                .uuid = &CONFIG_CHR_LIGHT_ROUND_UUID.u,
                .access_cb = config_svc_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LIGHT_MODE_ROUND
            },
            { 0 }
        }
    },
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &LAPTOP_SVC_UUID.u,
        .characteristics = (struct ble_gatt_chr_def[]) {
            {
                .uuid = &LAPTOP_CHR_CPU_USAGE_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_CPU_USAGE
            },
            {
                .uuid = &LAPTOP_CHR_CPU_TEMP_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_CPU_TEMP
            },
            {
                .uuid = &LAPTOP_CHR_CPU_VOLTAGE_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_CPU_VOLTAGE
            },
            {
                .uuid = &LAPTOP_CHR_CPU_CLOCK_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_CPU_CLOCK
            },
            {
                .uuid = &LAPTOP_CHR_GPU_USAGE_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_GPU_USAGE
            },
            {
                .uuid = &LAPTOP_CHR_GPU_TEMP_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_GPU_TEMP
            },
            {
                .uuid = &LAPTOP_CHR_GPU_CLOCK_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_GPU_CLOCK
            },
            {
                .uuid = &LAPTOP_CHR_GPU_RAM_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_GPU_RAM
            },
            {
                .uuid = &LAPTOP_CHR_GPU_TOT_RAM_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_GPU_TOT_RAM
            },
            {
                .uuid = &LAPTOP_CHR_RAM_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_RAM
            },
            {
                .uuid = &LAPTOP_CHR_TOT_RAM_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_TOT_RAM
            },
            {
                .uuid = &LAPTOP_CHR_FAN_USED_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_FAN_USED
            },
            {
                .uuid = &LAPTOP_CHR_FAN_SPEED_UUID.u,
                .access_cb = laptop_sys_callback,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_WRITE,
                .arg = (void*)(intptr_t)LAPTOP_INFO_FAN_SPEED
            },
            { 0 }
        }
    },
    { 0 }
};

#endif /* __GATT_SVR_H__ */