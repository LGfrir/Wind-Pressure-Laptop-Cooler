/**
 * @file laptop_sys.c
 * @author LG_frir
 * @date 2026-10-13 22:54:59
 */

#include "Data/laptop_sys.h"

static laptop_info_t laptop_info;

laptop_info_t* get_laptop_info_ptr() {
    return &laptop_info;
}
