/**
 * @file NimBLE.h
 * @author LG_frir
 * @date 2026-10-09 10:25:03
 */

#ifndef __NIMBLE_H__
#define __NIMBLE_H__

#include "esp_log.h"
#include "nvs_flash.h"
/* BLE */
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "host/util/util.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"
#include "store/config/ble_store_config.h"

#define DEVICE_NAME "连上尹浩荣jj断两根"

void nimble_init_as_prph(void);
void nimble_notify_rpm(void);

#endif /* __NIMBLE_H__ */