#ifndef __GPIO_H__
#define __GPIO_H__

#include "driver/gpio.h"

#define EC_A_PIN GPIO_NUM_0
#define EC_B_PIN GPIO_NUM_1
#define FAN_TACH_PIN GPIO_NUM_3
#define EC_SW_PIN GPIO_NUM_2
#define FAN_ARGB_PIN GPIO_NUM_4
#define OLED_BTN_1_PIN GPIO_NUM_5
#define FAN_PWM_PIN GPIO_NUM_6
#define SDA_PIN GPIO_NUM_7
#define SCL_PIN GPIO_NUM_9

/* 板载可寻址 RGB 灯（WS2812，单总线）数据脚：
 *   ESP32-S3-DevKitC-1 v1.1          -> GPIO38
 *   ESP32-S3-DevKitC-1 v1.0 / 多数第三方 S3 开发板 -> GPIO48
 * 板载灯不亮时，把下面的 GPIO_NUM_48 换成 GPIO_NUM_38（或反之）即可。 */
#define ONBOARD_RGB_PIN GPIO_NUM_48


#endif
