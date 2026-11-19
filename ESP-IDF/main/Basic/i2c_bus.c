#include "Basic/i2c_bus.h"
#include "esp_log.h"
#include "Basic/gpio.h"

#define I2C_SDA_IO SDA_PIN
#define I2C_SCL_IO SCL_PIN
#define I2C_PORT I2C_NUM_0
i2c_master_bus_handle_t i2c_bus_handle = NULL;

void i2c_bus_init(void) {
    if (i2c_bus_handle != NULL) {
        return;
    }
    i2c_master_bus_config_t bus_config = {
        .i2c_port = I2C_PORT,
        .sda_io_num = I2C_SDA_IO,
        .scl_io_num = I2C_SCL_IO,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    ESP_ERROR_CHECK(i2c_new_master_bus(&bus_config, &i2c_bus_handle));
}