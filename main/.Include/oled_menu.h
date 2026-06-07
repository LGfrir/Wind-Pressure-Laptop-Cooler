/**
 * @file oled_menu.h
 *
 * Direct OLED render API. The OLED no longer owns a local menu tree; the PC
 * selects a render mode over BLE and feeds the data used by that mode.
 */

#ifndef __OLED_MENU_H__
#define __OLED_MENU_H__

#include <stdint.h>

typedef enum {
    OLED_RENDER_SUMMARY = 0,
    OLED_RENDER_FAN,
    OLED_RENDER_PC_CPU,
    OLED_RENDER_PC_GPU,
    OLED_RENDER_PC_RAM,
    OLED_RENDER_PC_NET,
    OLED_RENDER_ESP_SYSTEM,
    OLED_RENDER_ESP_TASKS,
    OLED_RENDER_LIGHT,
    OLED_RENDER_COUNT
} oled_render_mode_t;

typedef struct {
    uint8_t cpu_usage;
    uint8_t cpu_power_w;
    uint8_t gpu_usage;
    uint8_t gpu_power_w;
    uint8_t ram_usage;
    uint16_t ram_used_mb;
    uint16_t ram_total_mb;
    uint16_t net_rx_kbps;
    uint16_t net_tx_kbps;
} oled_pc_status_t;

void oled_set_render_mode(oled_render_mode_t mode);
oled_render_mode_t oled_get_render_mode(void);
void oled_render_current(void);

void oled_set_pc_status(const oled_pc_status_t* status);
void oled_menu_set_pc_status(uint8_t cpu_usage,
                             uint8_t cpu_power_w,
                             uint8_t gpu_usage,
                             uint8_t gpu_power_w,
                             uint8_t ram_usage,
                             uint16_t ram_used_mb,
                             uint16_t ram_total_mb,
                             uint16_t net_rx_kbps,
                             uint16_t net_tx_kbps);

void oled_render_summary(void);
void oled_render_fan(void);
void oled_render_pc_cpu(void);
void oled_render_pc_gpu(void);
void oled_render_pc_ram(void);
void oled_render_pc_net(void);
void oled_render_esp_system(void);
void oled_render_esp_tasks(void);
void oled_render_light(void);

#endif /* __OLED_MENU_H__ */
