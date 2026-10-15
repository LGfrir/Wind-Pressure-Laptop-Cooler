idf_component_register(
    SRCS
        # Application entry and UART debug
        "main.c"
        "test.c"

        # 板载 RGB 灯玩灯效果（单颗 WS2812）
        "funny.c"

        # Basic hardware drivers
        "Basic/i2c_bus.c"
        "Basic/ws2812.c"

        # Module drivers
        "ModuleDriver/config_save.c"
        "ModuleDriver/encoder.c"
        "ModuleDriver/fan.c"
        "ModuleDriver/light_mode.c"
        "ModuleDriver/NimBLE.c"
        "ModuleDriver/oled.c"
        "ModuleDriver/oled_plot.c"
        "ModuleDriver/system_monitor.c"

        # Data models（电脑侧信息等）
        "Data/laptop_sys.c"

        # Runtime tasks
        "Tasks/event_handler.c"
        "Tasks/gatt_svc.c"
        "Tasks/light_task.c"
        "Tasks/oled_display_task.c"

        # OLED menu and interaction
        "oled/oled_adjuster.c"
        "oled/oled_menu.c"

    INCLUDE_DIRS
        # Include headers by module path, e.g. "ModuleDriver/fan.h".
        ".Include"

    REQUIRES
        esp_driver_i2c
        freertos
        esp_driver_gpio
        esp_timer
        esp_driver_ledc
        esp_driver_uart
        nvs_flash
        bt
        esp_wifi 
        esp_event 
        lwip 
        esp_netif
        driver
)
