/**
 * @file oled_menu.c
 *
 * Direct OLED status renderer. The display mode is selected by the PC over BLE;
 * this module only renders the currently selected mode with cached data.
 */

#include ".Include/oled_menu.h"

#include ".Include/fan.h"
#include ".Include/light.h"
#include ".Include/oled.h"
#include "system_monitor.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#define OLED_STATUS_FONT 1
#define OLED_LINE_COUNT 8
#define TASK_PREVIEW_COUNT 5

static oled_render_mode_t s_render_mode = OLED_RENDER_SUMMARY;
static oled_pc_status_t s_pc_status;

static const char* fan_mode_to_str(fan_mode_t mode);
static const char* light_mode_to_str(light_mode_t mode);
static bool get_realtime_stats(system_monitor_stats_t* stats);
static void status_begin(const char* title);
static void status_line(uint8_t line, const char* fmt, ...);
static void status_end(void);

void oled_set_render_mode(oled_render_mode_t mode) {
    if (mode >= OLED_RENDER_COUNT) {
        mode = OLED_RENDER_SUMMARY;
    }

    s_render_mode = mode;
}

oled_render_mode_t oled_get_render_mode(void) {
    return s_render_mode;
}

void oled_render_current(void) {
    switch (s_render_mode) {
    case OLED_RENDER_SUMMARY:
        oled_render_summary();
        break;
    case OLED_RENDER_FAN:
        oled_render_fan();
        break;
    case OLED_RENDER_PC_CPU:
        oled_render_pc_cpu();
        break;
    case OLED_RENDER_PC_GPU:
        oled_render_pc_gpu();
        break;
    case OLED_RENDER_PC_RAM:
        oled_render_pc_ram();
        break;
    case OLED_RENDER_PC_NET:
        oled_render_pc_net();
        break;
    case OLED_RENDER_ESP_SYSTEM:
        oled_render_esp_system();
        break;
    case OLED_RENDER_ESP_TASKS:
        oled_render_esp_tasks();
        break;
    case OLED_RENDER_LIGHT:
        oled_render_light();
        break;
    default:
        oled_render_summary();
        break;
    }
}

void oled_set_pc_status(const oled_pc_status_t* status) {
    if (status == NULL) {
        return;
    }

    s_pc_status = *status;

    if (s_pc_status.cpu_usage > 100) {
        s_pc_status.cpu_usage = 100;
    }
    if (s_pc_status.gpu_usage > 100) {
        s_pc_status.gpu_usage = 100;
    }
    if (s_pc_status.ram_usage > 100) {
        s_pc_status.ram_usage = 100;
    }
}

void oled_menu_set_pc_status(uint8_t cpu_usage,
                             uint8_t cpu_power_w,
                             uint8_t gpu_usage,
                             uint8_t gpu_power_w,
                             uint8_t ram_usage,
                             uint16_t ram_used_mb,
                             uint16_t ram_total_mb,
                             uint16_t net_rx_kbps,
                             uint16_t net_tx_kbps) {
    oled_pc_status_t status = {
        .cpu_usage = cpu_usage,
        .cpu_power_w = cpu_power_w,
        .gpu_usage = gpu_usage,
        .gpu_power_w = gpu_power_w,
        .ram_usage = ram_usage,
        .ram_used_mb = ram_used_mb,
        .ram_total_mb = ram_total_mb,
        .net_rx_kbps = net_rx_kbps,
        .net_tx_kbps = net_tx_kbps,
    };

    oled_set_pc_status(&status);
}

void oled_render_summary(void) {
    status_begin("summary");
    status_line(1, "fan %u%% %lu rpm",
                fan.speed_percentage, (unsigned long)fan_get_rpm());
    status_line(2, "fan mode %s", fan_mode_to_str(fan_get_mode()));
    status_line(3, "pc cpu %u%% %uw",
                s_pc_status.cpu_usage, s_pc_status.cpu_power_w);
    status_line(4, "pc gpu %u%% %uw",
                s_pc_status.gpu_usage, s_pc_status.gpu_power_w);
    status_line(5, "pc ram %u%%", s_pc_status.ram_usage);
    status_line(6, "net %u/%u kb",
                s_pc_status.net_rx_kbps, s_pc_status.net_tx_kbps);
    status_line(7, "mode %u", (unsigned int)s_render_mode);
    status_end();
}

void oled_render_fan(void) {
    status_begin("fan");
    status_line(1, "pwm %u%%", fan.speed_percentage);
    status_line(2, "rpm %lu", (unsigned long)fan_get_rpm());
    status_line(3, "power %s", fan.is_on ? "on" : "off");
    status_line(4, "mode %s", fan_mode_to_str(fan_get_mode()));
    status_line(5, "source esp");
    status_end();
}

void oled_render_pc_cpu(void) {
    status_begin("pc cpu");
    status_line(1, "usage %u%%", s_pc_status.cpu_usage);
    status_line(2, "power %u w", s_pc_status.cpu_power_w);
    status_line(3, "source ble");
    status_end();
}

void oled_render_pc_gpu(void) {
    status_begin("pc gpu");
    status_line(1, "usage %u%%", s_pc_status.gpu_usage);
    status_line(2, "power %u w", s_pc_status.gpu_power_w);
    status_line(3, "source ble");
    status_end();
}

void oled_render_pc_ram(void) {
    status_begin("pc ram");
    status_line(1, "usage %u%%", s_pc_status.ram_usage);
    status_line(2, "used %u mb", s_pc_status.ram_used_mb);
    status_line(3, "total %u mb", s_pc_status.ram_total_mb);
    status_end();
}

void oled_render_pc_net(void) {
    status_begin("pc net");
    status_line(1, "rx %u kbps", s_pc_status.net_rx_kbps);
    status_line(2, "tx %u kbps", s_pc_status.net_tx_kbps);
    status_line(3, "source ble");
    status_end();
}

void oled_render_esp_system(void) {
    system_monitor_stats_t stats;

    status_begin("esp system");
    if (get_realtime_stats(&stats)) {
        status_line(1, "cpu %.1f%%", stats.active_ratio);
        status_line(2, "heap %lu", (unsigned long)stats.free_heap_bytes);
        status_line(3, "min %lu", (unsigned long)stats.min_free_heap_bytes);
        status_line(4, "block %lu", (unsigned long)stats.largest_free_block);
        status_line(5, "tasks %lu", (unsigned long)stats.task_count);
    }
    else {
        status_line(1, "monitor offline");
    }
    status_end();
}

void oled_render_esp_tasks(void) {
    system_task_info_t tasks[TASK_PREVIEW_COUNT];
    uint32_t actual = 0;

    status_begin("esp tasks");
    if (system_monitor_get_task_list(tasks, TASK_PREVIEW_COUNT, &actual) == ESP_OK) {
        for (uint32_t i = 0; i < actual; i++) {
            status_line((uint8_t)(i + 1), "%s %.1f%%",
                        tasks[i].task_name, tasks[i].cpu_percent);
        }
        status_line(7, "shown %lu", (unsigned long)actual);
    }
    else {
        status_line(1, "task list offline");
    }
    status_end();
}

void oled_render_light(void) {
    light_t* cfg = light_get_cfg();

    status_begin("light");
    status_line(1, "mode %s", light_mode_to_str(cfg->mode));
    status_line(2, "brightness %u", cfg->brightness);
    status_line(3, "period %u", cfg->period);
    status_line(4, "source esp");
    status_end();
}

static void status_begin(const char* title) {
    oled_set_font(OLED_STATUS_FONT);
    oled_set_inv_color(false);
    oled_clear();
    oled_print(0, 0, "%s", title);
}

static void status_line(uint8_t line, const char* fmt, ...) {
    char line_buf[32];
    va_list args;

    if (line >= OLED_LINE_COUNT) {
        return;
    }

    va_start(args, fmt);
    vsnprintf(line_buf, sizeof(line_buf), fmt, args);
    va_end(args);

    oled_print(0, line, "%s", line_buf);
}

static void status_end(void) {
    oled_set_inv_color(false);
    oled_update();
}

static bool get_realtime_stats(system_monitor_stats_t* stats) {
    if (system_monitor_get_realtime_stats(stats) == ESP_OK) {
        return true;
    }

    return system_monitor_get_stats(stats) == ESP_OK;
}

static const char* fan_mode_to_str(fan_mode_t mode) {
    switch (mode) {
    case MANUAL:
        return "manual";
    case AUTO:
        return "auto";
    case FULL_SPEED:
        return "full";
    default:
        return "unknown";
    }
}

static const char* light_mode_to_str(light_mode_t mode) {
    switch (mode) {
    case LIGHT_MODE_OFF:
        return "off";
    case LIGHT_MODE_STATIC:
        return "static";
    case LIGHT_MODE_RAINBOW:
        return "rainbow";
    case LIGHT_MODE_BREATH:
        return "breath";
    case LIGHT_MODE_RANDOM_FLASH:
        return "flash";
    case LIGHT_MODE_BLINK:
        return "blink";
    case LIGHT_MODE_CUSTOM_BREATH:
        return "custom";
    case LIGHT_MODE_CHASE:
        return "chase";
    case LIGHT_MODE_ROUND:
        return "round";
    default:
        return "unknown";
    }
}
