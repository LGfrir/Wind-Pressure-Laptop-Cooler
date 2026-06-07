/**
 * @file oled_adjuster.c
 * @author LG_frir (全静态版)
 */

#include "oled_adjuster.h"
#include <stdarg.h>

void adjuster_init(adjuster_t* adj, const char* name, uint8_t count, ...) {
    adj->name = name;
    adj->count = (count > 4) ? 4 : count;   // 限制最大4个，防止越界
    adj->sw = 0;

    va_list args;
    va_start(args, count);

    for (uint8_t i = 0; i < adj->count; i++) {
        adj->items[i].ptr  = va_arg(args, void*);
        adj->items[i].min  = va_arg(args, int32_t);
        adj->items[i].max  = va_arg(args, int32_t);
        adj->items[i].type = (adj_type_t)va_arg(args, int);

        if (adj->items[i].min > adj->items[i].max) {
            int32_t t = adj->items[i].min;
            adj->items[i].min = adj->items[i].max;
            adj->items[i].max = t;
        }
    }
    va_end(args);
}

// ====================== 核心函数 ======================

static int32_t get_current_value(adjuster_item_t* item) {
    if (item->type == ADJ_U8)
        return *(uint8_t*)item->ptr;
    else
        return *(uint32_t*)item->ptr;
}

static void set_current_value(adjuster_item_t* item, int32_t val) {
    if (item->type == ADJ_U8)
        *(uint8_t*)item->ptr = (uint8_t)val;
    else
        *(uint32_t*)item->ptr = (uint32_t)val;
}

void adjuster_apply_diff(adjuster_t* adj, int32_t diff) {
    if (!adj || adj->sw == 0 || adj->count == 0) return;

    uint8_t idx = adj->sw - 1;
    adjuster_item_t* item = &adj->items[idx];

    int32_t val = get_current_value(item);
    int32_t temp = val + diff;

    if (temp > item->max) temp = item->max;
    if (temp < item->min) temp = item->min;

    set_current_value(item, temp);
}

bool adjuster_toggle_edit(adjuster_t* adj) {
    if (adj->sw == 0) {
        adj->sw = 1;
    } else {
        adj->sw++;
        if (adj->sw > adj->count) adj->sw = 0;
    }
    return adj->sw != 0;
}

uint8_t adjuster_get_sw(adjuster_t* adj) {
    return adj ? adj->sw : 0;
}

bool adjuster_is_editing(adjuster_t* adj) {
    return adj && adj->sw != 0;
}

int32_t adjuster_get_value(adjuster_t* adj, uint8_t index) {
    if (!adj || index >= adj->count) return 0;
    return get_current_value(&adj->items[index]);
}

// 可选保留（目前你没用到）
void adjuster_move_right(adjuster_t* adj) {
    if (adj->sw == 0) adj->sw = 1;
    else {
        adj->sw++;
        if (adj->sw > adj->count) adj->sw = adj->count;
    }
}

void adjuster_move_left(adjuster_t* adj) {
    if (adj->sw > 0) adj->sw--;
}