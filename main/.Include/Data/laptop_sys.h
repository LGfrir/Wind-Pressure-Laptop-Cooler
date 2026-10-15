/**
 * @file laptop_sys.h
 * @author LG_frir
 * @date 2026-10-13 22:51:54
 */

#ifndef __LAPTOP_SYS_H__
#define __LAPTOP_SYS_H__

typedef struct laptop_info_t_ {
    double cpu_usage;
    double cpu_temp;
    double cpu_voltage;
    double cpu_clock;

    double gpu_usage;
    double gpu_temp;
    double gpu_clock;
    double gpu_ram;
    double gpu_tot_ram;

    double ram;
    double tot_ram;
    double fan_used[5];
    double fan_speed[5];
}laptop_info_t;

typedef enum {
    LAPTOP_INFO_CPU_USAGE,
    LAPTOP_INFO_CPU_TEMP,
    LAPTOP_INFO_CPU_VOLTAGE,
    LAPTOP_INFO_CPU_CLOCK,

    LAPTOP_INFO_GPU_USAGE,
    LAPTOP_INFO_GPU_TEMP,
    LAPTOP_INFO_GPU_CLOCK,
    LAPTOP_INFO_GPU_RAM,
    LAPTOP_INFO_GPU_TOT_RAM,

    LAPTOP_INFO_RAM,
    LAPTOP_INFO_TOT_RAM,
    LAPTOP_INFO_FAN_USED,
    LAPTOP_INFO_FAN_SPEED,
} laptop_info_e;

laptop_info_t* get_laptop_info_ptr();



#endif /* __LAPTOP_SYS_H__ */