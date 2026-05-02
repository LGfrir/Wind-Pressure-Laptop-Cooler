/**
 * @file system_monitor.h
 * @author LG_frir
 * @date 2026-04-22 17:50:29
 * @brief
 * 系统监控接口：运行时间统计、RAM/CPU占用、任务状态查看
 */

#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#ifdef __cplusplus
extern "C" {
#endif

/* =========================
 * 系统整体统计
 * ========================= */
typedef struct {
    /* 通用时间统计 */
    uint32_t total_time_ms;
    uint32_t active_time_ms;

    /**
     * 说明：
     * - 在 system_monitor_get_stats() 中：
     *     active_ratio 表示手动 active_start/stop
     * 统计出来的活跃占比
     * - 在 system_monitor_get_realtime_stats() 中：
     *     active_ratio 表示最近一次采样区间的实时 CPU
     * 使用率
     */
    float active_ratio;

    /* 堆内存统计 */
    size_t total_heap_bytes;
    size_t free_heap_bytes;
    size_t min_free_heap_bytes;
    size_t largest_free_block;

    /* Internal RAM */
    size_t internal_total_bytes;
    size_t internal_free_bytes;

    /* PSRAM */
    size_t psram_total_bytes;
    size_t psram_free_bytes;

    /* 任务统计 */
    uint32_t task_count;
} system_monitor_stats_t;

/* =========================
 * 任务信息
 * ========================= */
typedef struct {
    char task_name[configMAX_TASK_NAME_LEN];
    eTaskState task_state;
    UBaseType_t priority;
    uint32_t stack_high_water_mark;
    TaskHandle_t handle;

    uint32_t runtime_counter; /* 运行时统计计数 */
    float cpu_percent; /* 占总 runtime 的百分比 */
    UBaseType_t task_number; /* FreeRTOS task number */

#if (configNUMBER_OF_CORES > 1)
    BaseType_t core_id; /* 所在核心 */
#endif
} system_task_info_t;

/* =========================
 * 生命周期
 * ========================= */

/**
 * @brief 初始化系统监控模块
 * @return ESP_OK 成功
 */
esp_err_t system_monitor_init(void);

/**
 * @brief 重置统计数据
 */
void system_monitor_reset(void);

/* =========================
 * 手动活跃区间统计
 * ========================= */

/**
 * @brief 标记活跃区间开始
 */
void system_monitor_active_start(void);

/**
 * @brief 标记活跃区间结束
 */
void system_monitor_active_stop(void);

/* =========================
 * 统计获取
 * ========================= */

/**
 * @brief 获取从初始化/重置以来的累计统计
 * @param stats 输出统计结构体
 * @return ESP_OK 成功
 */
esp_err_t
system_monitor_get_stats(system_monitor_stats_t* stats);

/**
 * @brief 获取实时统计
 *
 * 当前实现中：
 * - active_ratio 表示最近一次采样区间的 CPU 使用率
 * - active_time_ms 为按 1000ms 窗口换算后的活跃时间
 *
 * @param stats 输出统计结构体
 * @return ESP_OK 成功
 */
esp_err_t system_monitor_get_realtime_stats(
    system_monitor_stats_t* stats);

/**
 * @brief 获取任务列表
 * @param task_list 输出任务数组
 * @param max_tasks task_list 最大容量
 * @param actual 实际写入的任务数量
 * @return ESP_OK 成功
 */
esp_err_t
system_monitor_get_task_list(system_task_info_t* task_list,
                             uint32_t max_tasks,
                             uint32_t* actual);

/* =========================
 * 调试输出
 * ========================= */

/**
 * @brief 打印累计统计信息
 */
void system_monitor_print_stats(void);

/**
 * @brief 打印实时统计信息
 */
void system_monitor_print_realtime_stats(void);

/**
 * @brief 打印任务列表
 */
void system_monitor_print_task_list(void);

#ifdef __cplusplus
}
#endif
