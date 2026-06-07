/**
 * @file blue_tooth.c
 *
 * NimBLE transport layer. Payload parsing and packet building live in
 * ble_protocol.c so the BLE stack code stays small.
 */

#include "blue_tooth.h"

#include ".Include/ble_protocol.h"

#include "esp_err.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "host/ble_hs.h"
#include "host/ble_uuid.h"
#include "host/util/util.h"
#include "nimble/ble.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"

#include <string.h>

#define SMARTFAN_SERVICE_UUID128 \
    BLE_UUID128_INIT(0x55, 0x44, 0x33, 0x22, 0x11, 0x00, 0x9b, 0x8a, \
                     0x5f, 0x4e, 0x3d, 0x2c, 0x00, 0x00, 0xf0, 0xa1)
#define PC_RX_CHR_UUID128 \
    BLE_UUID128_INIT(0x55, 0x44, 0x33, 0x22, 0x11, 0x00, 0x9b, 0x8a, \
                     0x5f, 0x4e, 0x3d, 0x2c, 0x01, 0x00, 0xf0, 0xa1)
#define ESP_TX_CHR_UUID128 \
    BLE_UUID128_INIT(0x55, 0x44, 0x33, 0x22, 0x11, 0x00, 0x9b, 0x8a, \
                     0x5f, 0x4e, 0x3d, 0x2c, 0x02, 0x00, 0xf0, 0xa1)

static const char* TAG = "BLE";

static uint8_t s_own_addr_type;
static bool s_connected;
static uint16_t s_conn_handle = BLE_HS_CONN_HANDLE_NONE;
static uint16_t s_pc_rx_handle;
static uint16_t s_esp_tx_handle;
static bool s_esp_tx_notify_enabled;

static const ble_uuid128_t s_service_uuid = SMARTFAN_SERVICE_UUID128;
static const ble_uuid128_t s_pc_rx_uuid = PC_RX_CHR_UUID128;
static const ble_uuid128_t s_esp_tx_uuid = ESP_TX_CHR_UUID128;

void ble_store_config_init(void);

static void start_advertising(void);

static int append_esp_status(struct os_mbuf* om) {
    uint8_t payload[SMARTFAN_BLE_ESP_STATUS_LEN] = {0};
    uint16_t len = ble_protocol_build_esp_status(payload, sizeof(payload));

    if (len == 0) {
        return BLE_ATT_ERR_UNLIKELY;
    }

    return os_mbuf_append(om, payload, len) == 0
               ? 0
               : BLE_ATT_ERR_INSUFFICIENT_RES;
}

static int pc_rx_access(uint16_t conn_handle,
                        uint16_t attr_handle,
                        struct ble_gatt_access_ctxt* ctxt,
                        void* arg) {
    (void)conn_handle;
    (void)arg;

    if (ctxt->op != BLE_GATT_ACCESS_OP_WRITE_CHR ||
        attr_handle != s_pc_rx_handle) {
        return BLE_ATT_ERR_UNLIKELY;
    }

    uint16_t len = OS_MBUF_PKTLEN(ctxt->om);
    uint8_t payload[32] = {0};

    if (len > sizeof(payload)) {
        ESP_LOGW(TAG, "rx packet too large len=%u", len);
        return BLE_ATT_ERR_INVALID_ATTR_VALUE_LEN;
    }

    int rc = ble_hs_mbuf_to_flat(ctxt->om, payload, sizeof(payload), NULL);
    if (rc != 0) {
        return BLE_ATT_ERR_UNLIKELY;
    }

    if (ble_protocol_handle_rx(payload, len) != 0) {
        return BLE_ATT_ERR_UNLIKELY;
    }

    return 0;
}

static int esp_tx_access(uint16_t conn_handle,
                         uint16_t attr_handle,
                         struct ble_gatt_access_ctxt* ctxt,
                         void* arg) {
    (void)conn_handle;
    (void)arg;

    if (ctxt->op != BLE_GATT_ACCESS_OP_READ_CHR ||
        attr_handle != s_esp_tx_handle) {
        return BLE_ATT_ERR_UNLIKELY;
    }

    return append_esp_status(ctxt->om);
}

static const struct ble_gatt_svc_def s_gatt_svcs[] = {
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &s_service_uuid.u,
        .characteristics =
            (struct ble_gatt_chr_def[]){
                {
                    .uuid = &s_pc_rx_uuid.u,
                    .access_cb = pc_rx_access,
                    .flags = BLE_GATT_CHR_F_WRITE | BLE_GATT_CHR_F_WRITE_NO_RSP,
                    .val_handle = &s_pc_rx_handle,
                },
                {
                    .uuid = &s_esp_tx_uuid.u,
                    .access_cb = esp_tx_access,
                    .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_NOTIFY,
                    .val_handle = &s_esp_tx_handle,
                },
                {0},
            },
    },
    {0},
};

static int gap_event(struct ble_gap_event* event, void* arg) {
    (void)arg;

    switch (event->type) {
    case BLE_GAP_EVENT_CONNECT:
        if (event->connect.status == 0) {
            s_connected = true;
            s_conn_handle = event->connect.conn_handle;
            ESP_LOGI(TAG, "connected");
        }
        else {
            ESP_LOGW(TAG, "connect failed status=%d", event->connect.status);
            start_advertising();
        }
        return 0;

    case BLE_GAP_EVENT_DISCONNECT:
        ESP_LOGI(TAG, "disconnected reason=%d", event->disconnect.reason);
        s_connected = false;
        s_conn_handle = BLE_HS_CONN_HANDLE_NONE;
        s_esp_tx_notify_enabled = false;
        start_advertising();
        return 0;

    case BLE_GAP_EVENT_ADV_COMPLETE:
        start_advertising();
        return 0;

    case BLE_GAP_EVENT_SUBSCRIBE:
        if (event->subscribe.attr_handle == s_esp_tx_handle) {
            s_esp_tx_notify_enabled = event->subscribe.cur_notify;
            ESP_LOGI(TAG, "esp tx notify %s",
                     s_esp_tx_notify_enabled ? "on" : "off");
        }
        return 0;

    case BLE_GAP_EVENT_MTU:
        ESP_LOGI(TAG, "mtu=%u", event->mtu.value);
        return 0;

    default:
        return 0;
    }
}

static void start_advertising(void) {
    struct ble_hs_adv_fields fields = {0};
    struct ble_hs_adv_fields rsp_fields = {0};
    struct ble_gap_adv_params adv_params = {0};

    fields.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    fields.name = (uint8_t*)SMARTFAN_BLE_DEVICE_NAME;
    fields.name_len = strlen(SMARTFAN_BLE_DEVICE_NAME);
    fields.name_is_complete = 1;

    int rc = ble_gap_adv_set_fields(&fields);
    if (rc != 0) {
        ESP_LOGE(TAG, "set adv fields failed rc=%d", rc);
        return;
    }

    rsp_fields.uuids128 = &s_service_uuid;
    rsp_fields.num_uuids128 = 1;
    rsp_fields.uuids128_is_complete = 1;

    rc = ble_gap_adv_rsp_set_fields(&rsp_fields);
    if (rc != 0) {
        ESP_LOGE(TAG, "set scan rsp failed rc=%d", rc);
        return;
    }

    adv_params.conn_mode = BLE_GAP_CONN_MODE_UND;
    adv_params.disc_mode = BLE_GAP_DISC_MODE_GEN;
    adv_params.itvl_min = BLE_GAP_ADV_ITVL_MS(100);
    adv_params.itvl_max = BLE_GAP_ADV_ITVL_MS(120);

    rc = ble_gap_adv_start(s_own_addr_type,
                           NULL,
                           BLE_HS_FOREVER,
                           &adv_params,
                           gap_event,
                           NULL);
    if (rc != 0) {
        ESP_LOGE(TAG, "start advertising failed rc=%d", rc);
        return;
    }

    ESP_LOGI(TAG, "advertising as %s", SMARTFAN_BLE_DEVICE_NAME);
}

static void on_reset(int reason) {
    ESP_LOGW(TAG, "nimble reset reason=%d", reason);
}

static void on_sync(void) {
    int rc = ble_hs_util_ensure_addr(0);
    if (rc != 0) {
        ESP_LOGE(TAG, "ensure addr failed rc=%d", rc);
        return;
    }

    rc = ble_hs_id_infer_auto(0, &s_own_addr_type);
    if (rc != 0) {
        ESP_LOGE(TAG, "infer addr failed rc=%d", rc);
        return;
    }

    start_advertising();
}

static void gatt_register_cb(struct ble_gatt_register_ctxt* ctxt, void* arg) {
    (void)arg;
    char uuid[BLE_UUID_STR_LEN] = {0};

    if (ctxt->op == BLE_GATT_REGISTER_OP_CHR) {
        ESP_LOGI(TAG,
                 "chr %s val_handle=%u",
                 ble_uuid_to_str(ctxt->chr.chr_def->uuid, uuid),
                 ctxt->chr.val_handle);
    }
}

static int gatt_init(void) {
    ble_svc_gap_init();
    ble_svc_gatt_init();

    int rc = ble_svc_gap_device_name_set(SMARTFAN_BLE_DEVICE_NAME);
    if (rc != 0) {
        return rc;
    }

    rc = ble_gatts_count_cfg(s_gatt_svcs);
    if (rc != 0) {
        return rc;
    }

    return ble_gatts_add_svcs(s_gatt_svcs);
}

static void host_task(void* param) {
    (void)param;

    nimble_port_run();
    nimble_port_freertos_deinit();
}

static void status_notify_task(void* param) {
    (void)param;

    while (1) {
        blue_tooth_notify_esp_status();
        vTaskDelay(pdMS_TO_TICKS(500));
    }
}

void blue_tooth_init(void) {
    esp_err_t ret = nimble_port_init();
    if (ret != ESP_OK) {
        ESP_LOGE(TAG, "nimble init failed: %s", esp_err_to_name(ret));
        return;
    }

    ble_hs_cfg.reset_cb = on_reset;
    ble_hs_cfg.sync_cb = on_sync;
    ble_hs_cfg.gatts_register_cb = gatt_register_cb;
    ble_hs_cfg.store_status_cb = ble_store_util_status_rr;

    int rc = gatt_init();
    if (rc != 0) {
        ESP_LOGE(TAG, "gatt init failed rc=%d", rc);
        return;
    }

    ble_store_config_init();
    nimble_port_freertos_init(host_task);
    xTaskCreate(status_notify_task, "ble_status_notify", 3072, NULL, 3, NULL);
}

bool blue_tooth_is_connected(void) {
    return s_connected;
}

void blue_tooth_notify_esp_status(void) {
    if (!s_connected || !s_esp_tx_notify_enabled ||
        s_conn_handle == BLE_HS_CONN_HANDLE_NONE) {
        return;
    }

    struct os_mbuf* om = ble_hs_mbuf_from_flat(NULL, 0);
    if (om == NULL) {
        return;
    }

    if (append_esp_status(om) != 0) {
        os_mbuf_free_chain(om);
        return;
    }

    int rc = ble_gattc_notify_custom(s_conn_handle, s_esp_tx_handle, om);
    if (rc != 0) {
        ESP_LOGW(TAG, "notify failed rc=%d", rc);
    }
}
