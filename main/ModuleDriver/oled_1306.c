
// #include ".Include/i2c_bus.h"
// #include ".Include/oled.h"
// #include ".Include/oled_font.h"

// #include <stdarg.h>
// #include <stdio.h>
// #include <string.h>

// #include "driver/i2c_master.h"
// #include "esp_log.h"
// #include "freertos/FreeRTOS.h"
// #include "freertos/task.h"

// static const char* TAG = "SSD1306";

// static i2c_master_dev_handle_t s_dev = NULL;

// /*
//  * s_buf[0] = 当前欲显示的字符缓冲
//  * s_buf[1] = 上一次已经刷到屏幕的字符缓冲
//  */
// static uint8_t s_buf[2][OLED_ROWS][OLED_COLS];

// /* ============================================================
//  * 底层 I2C 发送
//  * ============================================================ */

// static esp_err_t _write_cmd(uint8_t cmd) {
//     uint8_t buf[2] = {0x00, cmd}; // 0x00 = command
//     return i2c_master_transmit(s_dev, buf, sizeof(buf), 100);
// }

// static esp_err_t _write_data(const uint8_t* data, size_t len) {
//     if (len > OLED_WIDTH) {
//         return ESP_ERR_INVALID_SIZE;
//     }

//     uint8_t tx[OLED_WIDTH + 1];
//     tx[0] = 0x40; // 0x40 = data
//     memcpy(&tx[1], data, len);

//     return i2c_master_transmit(s_dev, tx, len + 1, 100);
// }

// /* ============================================================
//  * SSD1306 光标定位
//  * 使用 Page Addressing Mode
//  * ============================================================ */

// static esp_err_t _set_cursor(uint8_t x, uint8_t page) {
//     esp_err_t ret;

//     ret = _write_cmd(0xB0 | (page & 0x07)); // page start address
//     if (ret != ESP_OK)
//         return ret;

//     ret = _write_cmd(0x00 | (x & 0x0F)); // lower column start address
//     if (ret != ESP_OK)
//         return ret;

//     ret = _write_cmd(0x10 | ((x >> 4) & 0x0F)); // higher column start address
//     if (ret != ESP_OK)
//         return ret;

//     return ESP_OK;
// }

// /* ============================================================
//  * 调试：探测 I2C 是否通
//  * ============================================================ */

// static void _i2c_probe(void) {
//     uint8_t buf[2] = {0x00, 0xAE}; // display off
//     esp_err_t ret = i2c_master_transmit(s_dev, buf, sizeof(buf), 200);

//     if (ret == ESP_OK) {
//         ESP_LOGI(TAG, "[PROBE] I2C OK, addr=0x%02X", OLED_I2C_ADDR);
//     }
//     else {
//         ESP_LOGE(TAG, "[PROBE] I2C failed: %s", esp_err_to_name(ret));
//         ESP_LOGE(TAG, "check: VCC/GND/SDA/SCL/pull-up/address");
//     }
// }

// /* ============================================================
//  * 初始化命令宏
//  * ============================================================ */

// #define INIT_CMD(c)                                                                                                    \
//     do {                                                                                                               \
//         esp_err_t _r = _write_cmd((uint8_t)(c));                                                                       \
//         if (_r != ESP_OK) {                                                                                            \
//             ESP_LOGE(TAG, "INIT cmd 0x%02X failed: %s", (uint8_t)(c), esp_err_to_name(_r));                            \
//             return _r;                                                                                                 \
//         }                                                                                                              \
//     }                                                                                                                  \
//     while (0)

// /* ============================================================
//  * 屏幕清零（按像素）
//  * ============================================================ */

// static esp_err_t _clear_screen_hw(void) {
//     uint8_t zero[OLED_WIDTH];
//     memset(zero, 0x00, sizeof(zero));

//     for (uint8_t page = 0; page < OLED_PAGES; page++) {
//         esp_err_t ret = _set_cursor(0, page);
//         if (ret != ESP_OK) {
//             ESP_LOGE(TAG, "_clear_screen_hw set_cursor page=%u failed: %s", page, esp_err_to_name(ret));
//             return ret;
//         }

//         ret = _write_data(zero, OLED_WIDTH);
//         if (ret != ESP_OK) {
//             ESP_LOGE(TAG, "_clear_screen_hw write_data page=%u failed: %s", page, esp_err_to_name(ret));
//             return ret;
//         }
//     }

//     return ESP_OK;
// }

// /* ============================================================
//  * 调试：全屏点亮测试
//  * ============================================================ */

// esp_err_t oled_fill_test(void) {
//     uint8_t full[OLED_WIDTH];
//     memset(full, 0xFF, sizeof(full));

//     ESP_LOGI(TAG, "fill test: all pixels ON");

//     for (uint8_t page = 0; page < OLED_PAGES; page++) {
//         esp_err_t ret = _set_cursor(0, page);
//         if (ret != ESP_OK)
//             return ret;

//         ret = _write_data(full, OLED_WIDTH);
//         if (ret != ESP_OK)
//             return ret;
//     }

//     return ESP_OK;
// }

// /* ============================================================
//  * 初始化
//  * 适用于 SSD1306 128x64
//  * ============================================================ */

// esp_err_t oled_init(void) {
//     if (i2c_bus_handle == NULL) {
//         ESP_LOGE(TAG, "i2c_bus_handle is NULL, call i2c_bus_init() first");
//         return ESP_ERR_INVALID_STATE;
//     }

//     if (s_dev == NULL) {
//         i2c_device_config_t dev_cfg = {
//             .dev_addr_length = I2C_ADDR_BIT_LEN_7,
//             .device_address = OLED_I2C_ADDR,
//             .scl_speed_hz = OLED_FREQ_HZ,
//         };

//         esp_err_t ret = i2c_master_bus_add_device(i2c_bus_handle, &dev_cfg, &s_dev);
//         if (ret != ESP_OK) {
//             ESP_LOGE(TAG, "add_device failed: %s", esp_err_to_name(ret));
//             return ret;
//         }

//         ESP_LOGI(TAG, "I2C device add OK, addr=0x%02X", OLED_I2C_ADDR);
//     }

//     vTaskDelay(pdMS_TO_TICKS(100));
//     _i2c_probe();

//     INIT_CMD(0xAE); // Display OFF

//     INIT_CMD(0x20); // Set Memory Addressing Mode
//     INIT_CMD(0x02); // Page Addressing Mode

//     INIT_CMD(0xB0); // Set Page Start Address for Page Addressing Mode
//     INIT_CMD(0xC8); // COM Output Scan Direction remapped
//     INIT_CMD(0x00); // low column address
//     INIT_CMD(0x10); // high column address
//     INIT_CMD(0x40); // start line address

//     INIT_CMD(0x81); // contrast
//     INIT_CMD(0x7F);

//     INIT_CMD(0xA1); // segment remap
//     INIT_CMD(0xA6); // normal display

//     INIT_CMD(0xA8); // multiplex ratio
//     INIT_CMD(0x3F); // 1/64

//     INIT_CMD(0xA4); // output follows RAM content

//     INIT_CMD(0xD3); // display offset
//     INIT_CMD(0x00);

//     INIT_CMD(0xD5); // display clock divide ratio/osc freq
//     INIT_CMD(0x80);

//     INIT_CMD(0xD9); // pre-charge period
//     INIT_CMD(0xF1);

//     INIT_CMD(0xDA); // COM pins hardware config
//     INIT_CMD(0x12);

//     INIT_CMD(0xDB); // VCOMH deselect level
//     INIT_CMD(0x40);

//     INIT_CMD(0x8D); // charge pump
//     INIT_CMD(0x14); // enable

//     INIT_CMD(0xAF); // Display ON

//     esp_err_t ret = _clear_screen_hw();
//     if (ret != ESP_OK) {
//         return ret;
//     }

//     memset(s_buf[0], ' ', sizeof(s_buf[0]));
//     memset(s_buf[1], 0, sizeof(s_buf[1]));

//     ESP_LOGI(TAG, "SSD1306 init done");
//     return ESP_OK;
// }

// /* ============================================================
//  * 单字符绘制
//  * 字体：8x16
//  * 每个字符占 2 个 page
//  * ============================================================ */

// static esp_err_t _draw_char(uint8_t x, uint8_t y, char ch) {
//     if (x >= OLED_COLS || y >= OLED_ROWS) {
//         return ESP_ERR_INVALID_ARG;
//     }

//     if (ch < ' ' || ch > '~') {
//         ch = '?';
//     }

//     const uint8_t* bmp = oled_font_8x16[(uint8_t)(ch - ' ')];

//     uint8_t px = x * OLED_CHAR_W; // 像素列
//     uint8_t page = y * 2; // 8x16 -> 两页

//     esp_err_t ret;

//     ret = _set_cursor(px, page);
//     if (ret != ESP_OK) {
//         ESP_LOGE(TAG, "_set_cursor(%u,%u) failed", px, page);
//         return ret;
//     }

//     ret = _write_data(bmp, 8); // 上半 8 字节
//     if (ret != ESP_OK) {
//         ESP_LOGE(TAG, "_write_data upper failed, ch='%c'", ch);
//         return ret;
//     }

//     ret = _set_cursor(px, page + 1);
//     if (ret != ESP_OK) {
//         ESP_LOGE(TAG, "_set_cursor(%u,%u) failed", px, page + 1);
//         return ret;
//     }

//     ret = _write_data(bmp + 8, 8); // 下半 8 字节
//     if (ret != ESP_OK) {
//         ESP_LOGE(TAG, "_write_data lower failed, ch='%c'", ch);
//         return ret;
//     }

//     return ESP_OK;
// }

// /* ============================================================
//  * 对外 API
//  * ============================================================ */

// void oled_clear(void) { memset(s_buf[0], ' ', sizeof(s_buf[0])); }

// void oled_print(uint16_t x, uint16_t y, const char* fmt, ...) {
//     if (x >= OLED_COLS || y >= OLED_ROWS || fmt == NULL) {
//         return;
//     }

//     char str[64];
//     memset(str, 0, sizeof(str));

//     va_list args;
//     va_start(args, fmt);
//     vsnprintf(str, sizeof(str), fmt, args);
//     va_end(args);

//     uint16_t cx = x;
//     uint16_t cy = y;

//     for (int i = 0; str[i] != '\0'; i++) {
//         if (str[i] == '\n') {
//             cy++;
//             cx = x;
//             if (cy >= OLED_ROWS)
//                 break;
//             continue;
//         }

//         if (cx < OLED_COLS && cy < OLED_ROWS) {
//             s_buf[0][cy][cx] = (uint8_t)str[i];
//         }

//         cx++;
//         if (cx >= OLED_COLS) {
//             cx = 0;
//             cy++;
//             if (cy >= OLED_ROWS)
//                 break;
//         }
//     }
// }

// esp_err_t oled_update(void) {
//     int changed = 0;

//     for (uint8_t row = 0; row < OLED_ROWS; row++) {
//         for (uint8_t col = 0; col < OLED_COLS; col++) {
//             if (s_buf[0][row][col] != s_buf[1][row][col]) {
//                 esp_err_t ret = _draw_char(col, row, (char)s_buf[0][row][col]);
//                 if (ret != ESP_OK) {
//                     return ret;
//                 }

//                 s_buf[1][row][col] = s_buf[0][row][col];
//                 changed++;
//             }
//         }
//     }

//     ESP_LOGD(TAG, "oled_update: changed %d chars", changed);
//     return ESP_OK;
// }

// void oled_display_image(uint8_t* img, uint16_t width, uint16_t height) {
//     if (img == NULL || width == 0 || height == 0) {
//         ESP_LOGE(TAG, "Invalid image parameters");
//         return;
//     }

//     uint16_t pages = (height + 7) / 8;
//     uint32_t j = 0; // 和STM32那边一样，j顺序递增

//     for (uint16_t page = 0; page < pages; page++) {
//         esp_err_t ret = _set_cursor(0, page);
//         if (ret != ESP_OK) {
//             ESP_LOGE(TAG, "Set cursor failed page=%d: %s", page, esp_err_to_name(ret));
//             return;
//         }

//         ret = _write_data(&img[j], width); // 直接取当前页数据，一次写完
//         if (ret != ESP_OK) {
//             ESP_LOGE(TAG, "Write data failed page=%d: %s", page, esp_err_to_name(ret));
//             return;
//         }

//         j += width; // 指针往后移一页
//     }
// }