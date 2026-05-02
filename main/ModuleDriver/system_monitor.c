/**
 * @file system_monitor.c
 * @author LG_frir
 * @date 2026-04-22 17:50:29
 * @brief 系统监控模块实现
 */

/**
 *
 * menuconfig:
 * CONFIG_FREERTOS_USE_TRACE_FACILITY=y
 * CONFIG_FREERTOS_GENERATE_RUN_TIME_STATS=y
 * CONFIG_FREERTOS_USE_STATS_FORMATTING_FUNCTIONS=y
 */

#include "system_monitor.h"

#include <stdlib.h>
#include <string.h>

#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char* TAG = "system_monitor";

/* =========================
 * 内部状态
 * ========================= */
static bool s_inited = false;
static bool s_active = false;

/* 手动 active 统计 */
static int64_t s_start_time_us = 0;
static int64_t s_active_start_us = 0;
static int64_t s_active_accum_us = 0;

/* 实时 CPU 统计缓存 */
static uint32_t s_prev_total_runtime = 0;
static uint32_t s_prev_idle_runtime = 0;
static float s_realtime_cpu_ratio = 0.0f;

/* =========================
 * 工具函数
 * ========================= */
static uint32_t get_time_ms_from_us(int64_t us) {
    if (us <= 0) {
        return 0;
    }
    return (uint32_t)(us / 1000);
}

static float calc_ratio(uint32_t active_ms,
                        uint32_t total_ms) {
    if (total_ms == 0) {
        return 0.0f;
    }
    return ((float)active_ms * 100.0f) / (float)total_ms;
}

static const char* task_state_to_str(eTaskState state) {
    switch (state) {
    case eRunning:
        return "Running";
    case eReady:
        return "Ready";
    case eBlocked:
        return "Blocked";
    case eSuspended:
        return "Suspended";
    case eDeleted:
        return "Deleted";
    case eInvalid:
    default:
        return "Invalid";
    }
}

/* 获取当前总 runtime 和 idle runtime */
static esp_err_t
get_runtime_snapshot(uint32_t* total_runtime,
                     uint32_t* idle_runtime) {
    if (total_runtime == NULL || idle_runtime == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    UBaseType_t task_num = uxTaskGetNumberOfTasks();
    if (task_num == 0) {
        return ESP_FAIL;
    }

    TaskStatus_t* task_status_array = (TaskStatus_t*)calloc(
        task_num, sizeof(TaskStatus_t));
    if (task_status_array == NULL) {
        return ESP_ERR_NO_MEM;
    }

    uint32_t total = 0;
    UBaseType_t got_num = uxTaskGetSystemState(
        task_status_array, task_num, &total);

    uint32_t idle = 0;
    for (UBaseType_t i = 0; i < got_num; i++) {
        if (task_status_array[i].pcTaskName != NULL &&
            strncmp(task_status_array[i].pcTaskName,
                    "IDLE",
                    4) == 0) {
            idle += task_status_array[i].ulRunTimeCounter;
        }
    }

    free(task_status_array);

    *total_runtime = total;
    *idle_runtime = idle;
    return ESP_OK;
}

/* 初始化实时 CPU 基准 */
static void realtime_cpu_reset_baseline(void) {
    uint32_t total = 0;
    uint32_t idle = 0;

    if (get_runtime_snapshot(&total, &idle) == ESP_OK) {
        s_prev_total_runtime = total;
        s_prev_idle_runtime = idle;
        s_realtime_cpu_ratio = 0.0f;
    }
    else {
        s_prev_total_runtime = 0;
        s_prev_idle_runtime = 0;
        s_realtime_cpu_ratio = 0.0f;
    }
}

/* 更新实时 CPU 使用率 */
static esp_err_t update_realtime_cpu_usage(void) {
    uint32_t total = 0;
    uint32_t idle = 0;

    esp_err_t err = get_runtime_snapshot(&total, &idle);
    if (err != ESP_OK) {
        return err;
    }

    uint32_t delta_total = total - s_prev_total_runtime;
    uint32_t delta_idle = idle - s_prev_idle_runtime;

    s_prev_total_runtime = total;
    s_prev_idle_runtime = idle;

    if (delta_total == 0) {
        s_realtime_cpu_ratio = 0.0f;
        return ESP_OK;
    }

    float idle_ratio =
        ((float)delta_idle * 100.0f) / (float)delta_total;
    float cpu_ratio = 100.0f - idle_ratio;

    if (cpu_ratio < 0.0f) {
        cpu_ratio = 0.0f;
    }
    if (cpu_ratio > 100.0f) {
        cpu_ratio = 100.0f;
    }

    s_realtime_cpu_ratio = cpu_ratio;
    return ESP_OK;
}

static void
fill_memory_stats(system_monitor_stats_t* stats) {
    stats->total_heap_bytes =
        heap_caps_get_total_size(MALLOC_CAP_DEFAULT);
    stats->free_heap_bytes = esp_get_free_heap_size();
    stats->min_free_heap_bytes =
        esp_get_minimum_free_heap_size();
    stats->largest_free_block =
        heap_caps_get_largest_free_block(
            MALLOC_CAP_DEFAULT);

    stats->internal_total_bytes =
        heap_caps_get_total_size(MALLOC_CAP_INTERNAL);
    stats->internal_free_bytes =
        heap_caps_get_free_size(MALLOC_CAP_INTERNAL);

#if CONFIG_SPIRAM
    stats->psram_total_bytes =
        heap_caps_get_total_size(MALLOC_CAP_SPIRAM);
    stats->psram_free_bytes =
        heap_caps_get_free_size(MALLOC_CAP_SPIRAM);
#else
    stats->psram_total_bytes = 0;
    stats->psram_free_bytes = 0;
#endif

    stats->task_count = uxTaskGetNumberOfTasks();
}

/* =========================
 * 对外接口
 * ========================= */

esp_err_t system_monitor_init(void) {
    if (s_inited) {
        return ESP_OK;
    }

    s_start_time_us = esp_timer_get_time();
    s_active_accum_us = 0;
    s_active_start_us = 0;
    s_active = false;

    realtime_cpu_reset_baseline();

    s_inited = true;
    ESP_LOGI(TAG, "system monitor initialized");
    return ESP_OK;
}

void system_monitor_reset(void) {
    s_start_time_us = esp_timer_get_time();
    s_active_start_us = 0;
    s_active_accum_us = 0;
    s_active = false;

    realtime_cpu_reset_baseline();

    s_inited = true;
    ESP_LOGI(TAG, "system monitor reset");
}

void system_monitor_active_start(void) {
    if (!s_inited) {
        system_monitor_init();
    }

    if (s_active) {
        return;
    }

    s_active = true;
    s_active_start_us = esp_timer_get_time();
}

void system_monitor_active_stop(void) {
    if (!s_inited || !s_active) {
        return;
    }

    int64_t now_us = esp_timer_get_time();
    if (now_us > s_active_start_us) {
        s_active_accum_us += (now_us - s_active_start_us);
    }

    s_active = false;
    s_active_start_us = 0;
}

esp_err_t
system_monitor_get_stats(system_monitor_stats_t* stats) {
    if (stats == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    if (!s_inited) {
        return ESP_ERR_INVALID_STATE;
    }

    memset(stats, 0, sizeof(*stats));

    int64_t now_us = esp_timer_get_time();
    int64_t total_us = now_us - s_start_time_us;

    int64_t active_us = s_active_accum_us;
    if (s_active) {
        active_us += (now_us - s_active_start_us);
    }

    uint32_t total_ms = get_time_ms_from_us(total_us);
    uint32_t active_ms = get_time_ms_from_us(active_us);

    stats->total_time_ms = total_ms;
    stats->active_time_ms = active_ms;
    stats->active_ratio = calc_ratio(active_ms, total_ms);

    fill_memory_stats(stats);

    return ESP_OK;
}

/* 真正的实时 CPU 统计 */
esp_err_t system_monitor_get_realtime_stats(
    system_monitor_stats_t* stats) {
    if (stats == NULL) {
        return ESP_ERR_INVALID_ARG;
    }

    if (!s_inited) {
        return ESP_ERR_INVALID_STATE;
    }

    memset(stats, 0, sizeof(*stats));

    esp_err_t err = update_realtime_cpu_usage();
    if (err != ESP_OK) {
        return err;
    }

    stats->total_time_ms = 1000;
    stats->active_ratio = s_realtime_cpu_ratio;
    stats->active_time_ms =
        (uint32_t)(s_realtime_cpu_ratio * 10.0f);

    fill_memory_stats(stats);

    return ESP_OK;
}

esp_err_t
system_monitor_get_task_list(system_task_info_t* task_list,
                             uint32_t max_tasks,
                             uint32_t* actual) {
    if (task_list == NULL || actual == NULL ||
        max_tasks == 0) {
        return ESP_ERR_INVALID_ARG;
    }

    if (!s_inited) {
        return ESP_ERR_INVALID_STATE;
    }

    *actual = 0;

    UBaseType_t task_num = uxTaskGetNumberOfTasks();
    if (task_num == 0) {
        return ESP_OK;
    }

    TaskStatus_t* task_status_array = (TaskStatus_t*)calloc(
        task_num, sizeof(TaskStatus_t));
    if (task_status_array == NULL) {
        return ESP_ERR_NO_MEM;
    }

    uint32_t total_runtime = 0;
    UBaseType_t got_num = uxTaskGetSystemState(
        task_status_array, task_num, &total_runtime);

    uint32_t copy_num =
        (got_num < max_tasks) ? got_num : max_tasks;
    for (uint32_t i = 0; i < copy_num; i++) {
        memset(
            &task_list[i], 0, sizeof(system_task_info_t));

        if (task_status_array[i].pcTaskName != NULL) {
            strncpy(task_list[i].task_name,
                    task_status_array[i].pcTaskName,
                    sizeof(task_list[i].task_name) - 1);
            task_list[i]
                .task_name[sizeof(task_list[i].task_name) -
                           1] = '\0';
        }

        task_list[i].task_state =
            task_status_array[i].eCurrentState;
        task_list[i].priority =
            task_status_array[i].uxCurrentPriority;
        task_list[i].stack_high_water_mark =
            task_status_array[i].usStackHighWaterMark;
        task_list[i].handle = task_status_array[i].xHandle;
        task_list[i].runtime_counter =
            task_status_array[i].ulRunTimeCounter;
        task_list[i].task_number =
            task_status_array[i].xTaskNumber;

        if (total_runtime > 0) {
            task_list[i].cpu_percent =
                ((float)task_status_array[i]
                     .ulRunTimeCounter *
                 100.0f) /
                (float)total_runtime;
        }
        else {
            task_list[i].cpu_percent = 0.0f;
        }

#if (configNUMBER_OF_CORES > 1)
        task_list[i].core_id = task_status_array[i].xCoreID;
#endif
    }

    free(task_status_array);

    *actual = copy_num;
    return ESP_OK;
}

/* =========================
 * 调试输出
 * ========================= */

void system_monitor_print_stats(void) {
    system_monitor_stats_t stats;
    esp_err_t err = system_monitor_get_stats(&stats);
    if (err != ESP_OK) {
        ESP_LOGE(TAG,
                 "get stats failed: %s",
                 esp_err_to_name(err));
        return;
    }

    ESP_LOGI(TAG, "========== System Stats ==========");
    ESP_LOGI(TAG,
             "time       : total=%lu ms, active=%lu ms, "
             "ratio=%.2f%%",
             (unsigned long)stats.total_time_ms,
             (unsigned long)stats.active_time_ms,
             stats.active_ratio);

    ESP_LOGI(TAG,
             "heap       : total=%lu, free=%lu, "
             "min_free=%lu, largest=%lu",
             (unsigned long)stats.total_heap_bytes,
             (unsigned long)stats.free_heap_bytes,
             (unsigned long)stats.min_free_heap_bytes,
             (unsigned long)stats.largest_free_block);

    ESP_LOGI(TAG,
             "internal   : total=%lu, free=%lu",
             (unsigned long)stats.internal_total_bytes,
             (unsigned long)stats.internal_free_bytes);

    ESP_LOGI(TAG,
             "psram      : total=%lu, free=%lu",
             (unsigned long)stats.psram_total_bytes,
             (unsigned long)stats.psram_free_bytes);

    ESP_LOGI(TAG,
             "task_count : %lu",
             (unsigned long)stats.task_count);
    ESP_LOGI(TAG, "==================================");
}

void system_monitor_print_realtime_stats(void) {
    system_monitor_stats_t stats;
    esp_err_t err =
        system_monitor_get_realtime_stats(&stats);
    if (err != ESP_OK) {
        ESP_LOGE(TAG,
                 "get realtime stats failed: %s",
                 esp_err_to_name(err));
        return;
    }

    ESP_LOGI(TAG, "====== Realtime System Stats ======");
    ESP_LOGI(
        TAG,
        "cpu        : usage=%.2f%%, active=%lu/1000 ms",
        stats.active_ratio,
        (unsigned long)stats.active_time_ms);

    ESP_LOGI(TAG,
             "heap       : total=%lu, free=%lu, "
             "min_free=%lu, largest=%lu",
             (unsigned long)stats.total_heap_bytes,
             (unsigned long)stats.free_heap_bytes,
             (unsigned long)stats.min_free_heap_bytes,
             (unsigned long)stats.largest_free_block);

    ESP_LOGI(TAG,
             "internal   : total=%lu, free=%lu",
             (unsigned long)stats.internal_total_bytes,
             (unsigned long)stats.internal_free_bytes);

    ESP_LOGI(TAG,
             "psram      : total=%lu, free=%lu",
             (unsigned long)stats.psram_total_bytes,
             (unsigned long)stats.psram_free_bytes);

    ESP_LOGI(TAG,
             "task_count : %lu",
             (unsigned long)stats.task_count);
    ESP_LOGI(TAG, "==================================");
}

void system_monitor_print_task_list(void) {
    if (!s_inited) {
        ESP_LOGE(TAG, "system monitor not initialized");
        return;
    }

    UBaseType_t task_num = uxTaskGetNumberOfTasks();
    if (task_num == 0) {
        ESP_LOGW(TAG, "no tasks found");
        return;
    }

    TaskStatus_t* task_status_array = (TaskStatus_t*)calloc(
        task_num, sizeof(TaskStatus_t));
    if (task_status_array == NULL) {
        ESP_LOGE(TAG,
                 "failed to alloc memory for task list");
        return;
    }

    uint32_t total_runtime = 0;
    UBaseType_t got_num = uxTaskGetSystemState(
        task_status_array, task_num, &total_runtime);

    ESP_LOGI(TAG,
             "==================== Task List (%u) "
             "====================",
             (unsigned int)got_num);

#if (configNUMBER_OF_CORES > 1)
    ESP_LOGI(TAG,
             "%-16s %-10s %-5s %-8s %-12s %-10s %-4s",
             "Name",
             "State",
             "Prio",
             "CPU%",
             "Runtime",
             "StackHW",
             "Core");
#else
    ESP_LOGI(TAG,
             "%-16s %-10s %-5s %-8s %-12s %-10s",
             "Name",
             "State",
             "Prio",
             "CPU%%",
             "Runtime",
             "StackHW");
#endif

    for (UBaseType_t i = 0; i < got_num; i++) {
        const char* state_str = task_state_to_str(
            task_status_array[i].eCurrentState);

        float cpu_percent = 0.0f;
        if (total_runtime > 0) {
            cpu_percent = ((float)task_status_array[i]
                               .ulRunTimeCounter *
                           100.0f) /
                (float)total_runtime;
        }

#if (configNUMBER_OF_CORES > 1)
        ESP_LOGI(
            TAG,
            "%-16s %-10s %-5u %7.2f%% %-12lu %-10u %-4d",
            task_status_array[i].pcTaskName
                ? task_status_array[i].pcTaskName
                : "NULL",
            state_str,
            (unsigned int)task_status_array[i]
                .uxCurrentPriority,
            cpu_percent,
            (unsigned long)task_status_array[i]
                .ulRunTimeCounter,
            (unsigned int)task_status_array[i]
                .usStackHighWaterMark,
            (int)task_status_array[i].xCoreID);
#else
        ESP_LOGI(TAG,
                 "%-16s %-10s %-5u %7.2f%% %-12lu %-10u",
                 task_status_array[i].pcTaskName
                     ? task_status_array[i].pcTaskName
                     : "NULL",
                 state_str,
                 (unsigned int)task_status_array[i]
                     .uxCurrentPriority,
                 cpu_percent,
                 (unsigned long)task_status_array[i]
                     .ulRunTimeCounter,
                 (unsigned int)task_status_array[i]
                     .usStackHighWaterMark);
#endif
    }

    ESP_LOGI(TAG,
             "============================================="
             "===========");

    free(task_status_array);
}
