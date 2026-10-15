/**
 * @file gatt_svc.c
 * @author LG_frir
 * @date 2026-10-09 13:35:41
 */

#include "gatt_svc.h"
#include "light.h"
#include "fan.h"


int fan_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg) {
    intptr_t which_chr = (intptr_t)arg;
    // 读取数据
    if(ctxt->op == BLE_GATT_ACCESS_OP_READ_CHR) {
        int rc;
        if(which_chr == 1) // 灯模式
        {   
            light_mode_t value = light_get_cfg()->mode;
            rc = os_mbuf_append(ctxt->om, &value, sizeof(value));
            return rc == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
        }
        else if(which_chr == 2) // PWM
        {
            uint8_t value = 0;
            rc = os_mbuf_append(ctxt->om, &value, sizeof(value));
            return rc == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
        }
        else if(which_chr == 3) // 转速 
        { 
            uint32_t value = 0;
            value = fan_get_rpm();
            rc = os_mbuf_append(ctxt->om, &value, sizeof(value));
            return rc == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
        }
        else {
            return BLE_ATT_ERR_UNLIKELY;
        }
    } 
    // 写
    else if(ctxt->op == BLE_GATT_ACCESS_OP_WRITE_CHR) {
        if(which_chr == 1) {
            light_mode_t value = 0;
            uint16_t len = OS_MBUF_PKTLEN(ctxt->om);
            if (len != sizeof(light_mode_t)) return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
            os_mbuf_copydata(ctxt->om, 0, sizeof(light_mode_t), &value);

            light_get_cfg()->mode = value;
            return 0;
        }
        else if(which_chr == 2) {
            return 0;
        }
        else if(which_chr == 3) {
            uint32_t value = 0;
            uint16_t len = OS_MBUF_PKTLEN(ctxt->om);
            if (len != sizeof(uint32_t)) return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
            os_mbuf_copydata(ctxt->om, 0, sizeof(uint32_t), &value);

            fan_set_target_rpm(value);
            return 0;
        }
        else return BLE_ATT_ERR_UNLIKELY;
    } else {
        return BLE_ATT_ERR_UNLIKELY;
    }
    return BLE_ATT_ERR_UNLIKELY;
}


int light_svc_callback(uint16_t conn_handle, uint16_t attr_handle,
               struct ble_gatt_access_ctxt *ctxt, void *arg) {
        


    return BLE_ATT_ERR_UNLIKELY;
}
