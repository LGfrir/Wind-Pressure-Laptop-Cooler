/**
 * @file ble_protocol.c
 */

#include ".Include/ble_protocol.h"

#include ".Include/fan.h"
#include ".Include/oled_menu.h"

#include "esp_log.h"

static const char* TAG = "BLE_PROTO";

static uint16_t read_le16(const uint8_t* data) {
    return (uint16_t)data[0] | ((uint16_t)data[1] << 8);
}

static void write_le32(uint8_t* data, uint32_t value) {
    data[0] = (uint8_t)(value & 0xff);
    data[1] = (uint8_t)((value >> 8) & 0xff);
    data[2] = (uint8_t)((value >> 16) & 0xff);
    data[3] = (uint8_t)((value >> 24) & 0xff);
}

static void apply_pc_status(oled_render_mode_t mode, const uint8_t* payload) {
    oled_pc_status_t status = {
        .cpu_usage = payload[0],
        .cpu_power_w = payload[1],
        .gpu_usage = payload[2],
        .gpu_power_w = payload[3],
        .ram_usage = payload[4],
        .ram_used_mb = read_le16(&payload[5]),
        .ram_total_mb = read_le16(&payload[7]),
        .net_rx_kbps = read_le16(&payload[9]),
        .net_tx_kbps = read_le16(&payload[11]),
    };

    oled_set_pc_status(&status);
    oled_set_render_mode(mode);

    ESP_LOGI(TAG,
             "mode=%u cpu=%u gpu=%u ram=%u rx=%u tx=%u",
             (unsigned int)mode,
             status.cpu_usage,
             status.gpu_usage,
             status.ram_usage,
             status.net_rx_kbps,
             status.net_tx_kbps);
}

int ble_protocol_handle_rx(const uint8_t* data, uint16_t len) {
    if (data == NULL || len == 0) {
        return -1;
    }

    if (len == SMARTFAN_BLE_LEGACY_PC_STATUS_LEN) {
        apply_pc_status(oled_get_render_mode(), data);
        return 0;
    }

    switch (data[0]) {
    case SMARTFAN_BLE_CMD_SET_MODE:
        if (len < 2) {
            return -1;
        }
        oled_set_render_mode((oled_render_mode_t)data[1]);
        ESP_LOGI(TAG, "set mode=%u", (unsigned int)data[1]);
        return 0;

    case SMARTFAN_BLE_CMD_PC_STATUS:
        if (len != SMARTFAN_BLE_PC_STATUS_FRAME_LEN) {
            ESP_LOGW(TAG, "bad pc status len=%u", len);
            return -1;
        }
        apply_pc_status((oled_render_mode_t)data[1], &data[2]);
        return 0;

    default:
        ESP_LOGW(TAG, "unknown cmd=0x%02x len=%u", data[0], len);
        return -1;
    }
}

uint16_t ble_protocol_build_esp_status(uint8_t* out, uint16_t out_len) {
    if (out == NULL || out_len < SMARTFAN_BLE_ESP_STATUS_LEN) {
        return 0;
    }

    out[0] = (uint8_t)oled_get_render_mode();
    out[1] = fan.speed_percentage;
    out[2] = (uint8_t)fan_get_mode();
    out[3] = fan.is_on ? 1 : 0;
    write_le32(&out[4], fan_get_rpm());

    return SMARTFAN_BLE_ESP_STATUS_LEN;
}
