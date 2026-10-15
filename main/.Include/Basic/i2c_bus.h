/**
 * @file i2c_bus.h
 * @author 张三
 * @date 2026-04-14 20:47:52
 */

#ifndef __I2C_BUS_H__
#define __I2C_BUS_H__

#pragma once

#include "driver/i2c_master.h"

extern i2c_master_bus_handle_t i2c_bus_handle;

void i2c_bus_init(void);

#endif /* __I2C_BUS_H__ */