#ifndef OLED_ADJUSTER_H
#define OLED_ADJUSTER_H

#include <stdint.h>
#include <stdbool.h>

typedef enum {
    ADJ_U8  = 1,
    ADJ_U32 = 4
} adj_type_t;

typedef struct {
    void*      ptr;
    int32_t    min;
    int32_t    max;
    adj_type_t type;
} adjuster_item_t;

typedef struct {
    const char*     name;
    uint8_t         count;
    uint8_t         sw;                    // 0=未选中，1~count=当前选中
    adjuster_item_t items[4];              // 静态数组，最多支持4个参数（RGB+MS足够）
} adjuster_t;

// 初始化（现在是全静态的）
void adjuster_init(adjuster_t* adj, const char* name, uint8_t count, ...);

bool adjuster_toggle_edit(adjuster_t* adj);
void adjuster_apply_diff(adjuster_t* adj, int32_t diff);

uint8_t  adjuster_get_sw(adjuster_t* adj);
bool     adjuster_is_editing(adjuster_t* adj);
int32_t  adjuster_get_value(adjuster_t* adj, uint8_t index);

#endif