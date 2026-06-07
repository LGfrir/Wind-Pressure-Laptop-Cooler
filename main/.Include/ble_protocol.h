/**
 * @file ble_protocol.h
 *
 * SmartFan BLE payload parser and packet builder.
 */

#ifndef __BLE_PROTOCOL_H__
#define __BLE_PROTOCOL_H__

#include <stdint.h>

#define SMARTFAN_BLE_CMD_SET_MODE 0x01
#define SMARTFAN_BLE_CMD_PC_STATUS 0x02

#define SMARTFAN_BLE_LEGACY_PC_STATUS_LEN 13
#define SMARTFAN_BLE_PC_STATUS_FRAME_LEN 15
#define SMARTFAN_BLE_ESP_STATUS_LEN 8

int ble_protocol_handle_rx(const uint8_t* data, uint16_t len);
uint16_t ble_protocol_build_esp_status(uint8_t* out, uint16_t out_len);

#endif /* __BLE_PROTOCOL_H__ */
