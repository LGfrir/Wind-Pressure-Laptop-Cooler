/**
 * @file NimBLE.c
 * @author LG_frir
 * @date 2026-10-09 10:25:39
 */

#include "NimBLE.h"
#include "gatt_svc.h"
#include "esp_err.h"

#include <assert.h>

static uint8_t own_addr_type;
static const char *TAG = "NIMBLE";

/* 与 ESP-IDF 官方 bleprph 示例一致:该函数由 host/store/config 组件提供,
 * 但 NimBLE 并未在头文件中导出声明,需在此自行前置声明。 */
void ble_store_config_init(void);

static int bleprph_gap_event(struct ble_gap_event *event, void *arg) {
    

    return 0;
}

static void bleprph_advertise(void) {
    struct ble_hs_adv_fields fields;
    memset(&fields, 0, sizeof fields);

    fields.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    fields.tx_pwr_lvl_is_present = 1;
    fields.tx_pwr_lvl = BLE_HS_ADV_TX_PWR_LVL_AUTO;

    const char* dev_name = ble_svc_gap_device_name();
    fields.name = (const uint8_t *)dev_name;
    fields.name_len = strlen(dev_name);
    fields.name_is_complete = 1;

    int rc = ble_gap_adv_set_fields(&fields); 
    if(rc != 0) {
        ESP_LOGE(TAG, "ble_gap_adv_set_fields failed");
        return;
    }

    struct ble_gap_adv_params adv_params;
    memset(&adv_params, 0, sizeof adv_params);
    adv_params.conn_mode = BLE_GAP_CONN_MODE_UND;
    adv_params.disc_mode = BLE_GAP_DISC_MODE_GEN;   

    rc = ble_gap_adv_start(own_addr_type, NULL, BLE_HS_FOREVER, &adv_params, bleprph_gap_event, NULL);

    if(rc != 0 && rc != BLE_HS_EALREADY) {
        ESP_LOGE(TAG,"ble_gap_adv_start failed");
        return;
    }
    
}



static void bleprph_on_reset(int reason) {
    ESP_LOGE(TAG, "Resetting state; reason=%d", reason);
}

static void bleprph_on_sync(void) {
    int rc = ble_hs_util_ensure_addr(0);
    assert(rc == 0);

    // 设置MAC地址
    rc = ble_hs_id_infer_auto(0, &own_addr_type);
    if (rc != 0) {
        MODLOG_DFLT(ERROR, "error determining address type; rc=%d\n", rc);
        return;
    }

    uint8_t addr_val[6] = {0};
    rc = ble_hs_id_copy_addr(own_addr_type, addr_val, NULL);

    ESP_LOGI(TAG, "Device Address: %02x:%02x:%02x:%02x:%02x:%02x",
         addr_val[5], addr_val[4], addr_val[3], addr_val[2], addr_val[1], addr_val[0]);

    // 广播
    bleprph_advertise();

}

static void gatt_svr_register_cb(struct ble_gatt_register_ctxt *ctxt, void *arg) {
    char buf[BLE_UUID_STR_LEN];
    switch (ctxt->op) {
    case BLE_GATT_REGISTER_OP_SVC:
        ESP_LOGD(TAG, "registered service %s with handle=%d",
                 ble_uuid_to_str(ctxt->svc.svc_def->uuid, buf),
                 ctxt->svc.handle);
        break;
    case BLE_GATT_REGISTER_OP_CHR:
        ESP_LOGD(TAG, "registered characteristic %s with def_handle=%d val_handle=%d",
                 ble_uuid_to_str(ctxt->chr.chr_def->uuid, buf),
                 ctxt->chr.def_handle, ctxt->chr.val_handle);
        break;
    case BLE_GATT_REGISTER_OP_DSC:
        ESP_LOGD(TAG, "registered descriptor %s with handle=%d",
                 ble_uuid_to_str(ctxt->dsc.dsc_def->uuid, buf),
                 ctxt->dsc.handle);
        break;
    default:
        break;
    }
}




// GATT 注册
int gatt_svt_init() {
    int rc;
    ble_svc_gap_init();
    ble_svc_gatt_init();

    rc = ble_gatts_count_cfg(gatt_svr_svcs);
    if (rc != 0) return rc;
    rc = ble_gatts_add_svcs(gatt_svr_svcs);
    if (rc != 0) return rc;
    return 0;
}

void bleprph_host_task(void *param) {
    ESP_LOGI(TAG, "BLE Host Task Started");
    /* This function will return only when nimble_port_stop() is executed */
    nimble_port_run();

    nimble_port_freertos_deinit();
}
void nimble_init_as_prph() {
    esp_err_t ret = nimble_port_init();
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "Failed to init nimble %d ", ret);
        return;
    }

    ble_hs_cfg.reset_cb = bleprph_on_reset;
    ble_hs_cfg.sync_cb = bleprph_on_sync;
    ble_hs_cfg.gatts_register_cb = gatt_svr_register_cb;
    ble_hs_cfg.store_status_cb = ble_store_util_status_rr;

    ble_hs_cfg.sm_io_cap = BLE_HS_IO_NO_INPUT_OUTPUT;
    ble_hs_cfg.sm_bonding = 1;
    ble_hs_cfg.sm_our_key_dist = BLE_SM_PAIR_KEY_DIST_ENC | BLE_SM_PAIR_KEY_DIST_ID;
    ble_hs_cfg.sm_their_key_dist = BLE_SM_PAIR_KEY_DIST_ENC | BLE_SM_PAIR_KEY_DIST_ID;
    ble_hs_cfg.sm_mitm = 0;
    ble_hs_cfg.sm_sc = 1;

    // gatt 注册
    int rc = gatt_svt_init();
    assert(rc == 0);
    // 设备名注册
    rc = ble_svc_gap_device_name_set("yhrdsb");
    assert(rc == 0);

    //不懂
    ble_store_config_init();
    nimble_port_freertos_init(bleprph_host_task);
}


