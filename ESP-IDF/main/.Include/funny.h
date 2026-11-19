/**
 * @file funny.h
 * @author LG_frir
 * @date 2026-10-14
 * @brief 板载 RGB 灯（单颗 WS2812）的"玩灯"效果。
 *
 * 只针对开发板上那一颗多色灯（WS2812 兼容，单总线，数据脚见
 * Basic/gpio.h 里的 ONBOARD_RGB_PIN），和风扇那 8 颗 ARGB
 * （GPIO4，Basic/ws2812.c + Tasks/light_task.c）完全没有关系。
 *
 * 三个效果抢的是同一个 GPIO，只能注册一个任务，在 app_main 里按需留一行：
 *     xTaskCreate(onboard_rgb_task,       "onboard_rgb_task",       4096, NULL, 3, NULL);
 *     xTaskCreate(onboard_rgb_flash_task, "onboard_rgb_flash_task", 4096, NULL, 3, NULL);
 *     xTaskCreate(onboard_rgb_rage_task,  "onboard_rgb_rage_task",  4096, NULL, 3, NULL);
 */

#ifndef FUNNY_H
#define FUNNY_H

/* ========================= 可调参数 ========================= */

/* 板载灯只有一颗可寻址 WS2812，RMT 分辨率 10MHz（1 tick = 0.1us）。 */
#define ONBOARD_RGB_LED_NUM     1
#define ONBOARD_RGB_RMT_RES_HZ  (10 * 1000 * 1000)

/* 效果 1 渐变：每 25ms 色相走 1 度（约 40fps），绕完整个色环约 9 秒；
 * 亮度用满 255（板载灯单颗，全亮也不刺眼）。 */
#define ONBOARD_RGB_GRAD_STEP_MS    25
#define ONBOARD_RGB_GRAD_HUE_STEP   1
#define ONBOARD_RGB_GRAD_VALUE      16

/* 效果 2/3 闪灯：A 固定步长（越小越疯），B 用随机延时 10~80ms。 */
#define ONBOARD_RGB_FLASH_STEP_MS   30
#define ONBOARD_RGB_RAGE_MIN_MS     10
#define ONBOARD_RGB_RAGE_MAX_MS     80

/* 随机取色参数：饱和度拉满 + 亮度取高段 + 色相每次至少跳 90 度。
 * 为什么这么取见 funny.c 里 onboard_rgb_rand_color() 上面的那段注释。 */
#define ONBOARD_RGB_SAT             255   /* 饱和度：255 = 纯色，至少一个通道为 0，永不为白 */
#define ONBOARD_RGB_VAL_MIN         200   /* 亮度下限：= 最亮通道的下限，保证不暗淡 */
#define ONBOARD_RGB_VAL_MAX         255   /* 亮度上限 */
#define ONBOARD_RGB_HUE_MIN_JUMP    90    /* 相邻两帧最小色相差（度） */
#define ONBOARD_RGB_HUE_MAX_JUMP    270   /* 相邻两帧最大色相差（度） */
#define ONBOARD_RGB_LUM_MIN         90    /* 感知亮度下限：把纯蓝这类"看着像没亮"的颜色提亮 */

/* ========================= 任务入口 ========================= */

/*
 * @brief 效果 1：渐变（柔和，约 9 秒绕完色环）。
 */
void onboard_rgb_task(void *arg);

/*
 * @brief 效果 2：疯狂闪灯 A——随机鲜艳色 <-> 灭 交替
 *        （照 ModuleDriver/light_mode.c 里 RANDOM FLASH 的原理）。
 */
void onboard_rgb_flash_task(void *arg);

/*
 * @brief 效果 3：疯狂闪灯 B——颜色、亮灭、间隔全随机，更乱。
 */
void onboard_rgb_rage_task(void *arg);

#endif /* FUNNY_H */
