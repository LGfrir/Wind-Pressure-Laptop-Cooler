/**
 * @file oled_menu.c
 * @author LG_frir
 * @date 2026-04-20 20:07:45
 */


#include ".Include/oled_menu.h"
#include ".Include/oled_display_task.h"
#include ".Include/oled_plot.h"
#include ".Include/light.h"
static char output[50];


// OV = overview
// DE = detail

void oled_err_print() {
    uint8_t font = oled_get_font();
    oled_set_font(1);
    oled_print(0, 0, " Your Current Font");
    oled_print(0, 1, " Type Is Missing");
    oled_set_font(font);
}

void oled_print_todo() {
    oled_clear();
    uint8_t font = oled_get_font();
    oled_set_font(1);
    oled_print(0, 0, " Upcoming Features");
    oled_set_font(font);
    oled_update();
};

// 第二级 常规/复杂渲染
char* fan_info_OV(void);                            // void fan_info_DE(void);
char* encoder_info_OV(void);                        void encoder_info_DE(void);
char* system_info_OV(void);                         // void system_info_DE(void);
char* computer_info_OV(void);                       // void computer_info_DE(void);
char* settings_OV(void);                            // void settings_DE(void);
char* quick_settings_OV(void);                      void quick_settings_DE(void);


// 第三级 常规/详细渲染     
// fan info
char* fan_speed_OV(void);                           void fan_speed_DE(void);
char* fan_mode_OV(void);                            void fan_mode_DE(void);
char* fan_speed_curve_OV(void);                     void fan_speed_curve_DE(void);

// sys info
char* esp32c2_cpu_OV(void);                         void esp32c2_cpu_DE(void);
char* esp32c2_heap_OV(void);                        void esp32c2_heap_DE(void);
char* esp32c2_runtime_OV(void);                     void esp32c2_runtime_DE(void);
char* esp32c2_task_OV(void);                        void esp32c2_task_DE(void);

// comp info
char* computer_cpu_OV(void);                        void computer_cpu_DE(void);
char* computer_gpu_OV(void);                        void computer_gpu_DE(void);
char* computer_ram_OV(void);                        void computer_ram_DE(void);
char* computer_network_OV(void);                    void computer_network_DE(void);

// settings
char* fan_settings_OV(void);                        // void fan_settings_DE(void);
char* pwm_settings_OV(void);                        void pwm_settings_DE(void);
char* button_settings_OV(void);                     // void button_settings_DE(void);
char* encoder_settings_OV(void);                    void encoder_settings_DE(void);
char* light_settings_OV(void);                      // void light_settings_DE(void);

// 第四级 常规/详细渲染     
// fan settings
char* fan_speed_curve_settings_OV(void);            void fan_speed_curve_settings_DE(void);
char* fan_mode_settings_OV(void);                   void fan_mode_settings_DE(void);

// button settings
char* button_longpress_OV(void);                    void button_longpress_DE(void);
char* button_shortpress_OV(void);                   void button_shortpress_DE(void);

// light settings
char* light_mode_settings_OV(void);                 // void light_mode_settings_DE(void);
char* light_brightness_settings_OV(void);           void light_brightness_settings_DE(void);
char* light_period_settings_OV(void);               void light_period_settings_DE(void);


// 第五级 常规/复杂渲染
//light mode settings     
char* light_mode_off_OV(void);                      void light_mode_off_DE(void);
char* light_mode_static_OV(void);                   void light_mode_static_DE(void);
char* light_mode_breath_OV(void);                   void light_mode_breath_DE(void);
char* light_mode_blink_OV(void);                    void light_mode_blink_DE(void);
char* light_mode_chase_OV(void);                    void light_mode_chase_DE(void);
char* light_mode_rainbow_OV(void);                  void light_mode_rainbow_DE(void);
char* light_mode_custom_breath_OV(void);            void light_mode_custom_breath_DE(void);
char* light_mode_round_OV(void);                    void light_mode_round_DE(void);
char* light_mode_random_flash_OV(void);             void light_mode_random_flash_DE(void);


/*
 
 ██╗     ███████╗██╗   ██╗███████╗██╗          ██████╗ ███╗   ██╗███████╗
 ██║     ██╔════╝██║   ██║██╔════╝██║         ██╔═══██╗████╗  ██║██╔════╝
 ██║     █████╗  ██║   ██║█████╗  ██║         ██║   ██║██╔██╗ ██║█████╗  
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║         ██║   ██║██║╚██╗██║██╔══╝  
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗    ╚██████╔╝██║ ╚████║███████╗
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝     ╚═════╝ ╚═╝  ╚═══╝╚══════╝
                                                                         
 
 
 ███████╗████████╗ █████╗ ████████╗███████╗
 ██╔════╝╚══██╔══╝██╔══██╗╚══██╔══╝██╔════╝
 ███████╗   ██║   ███████║   ██║   █████╗  
 ╚════██║   ██║   ██╔══██║   ██║   ██╔══╝  
 ███████║   ██║   ██║  ██║   ██║   ███████╗
 ╚══════╝   ╚═╝   ╚═╝  ╚═╝   ╚═╝   ╚══════╝
                                           
 
*/

oled_item_state_t menu_state;


/*
 
 ███████╗ ██████╗ ███╗   ██╗
 ██╔════╝██╔═══██╗████╗  ██║
 ███████╗██║   ██║██╔██╗ ██║
 ╚════██║██║   ██║██║╚██╗██║
 ███████║╚██████╔╝██║ ╚████║
 ╚══════╝ ╚═════╝ ╚═╝  ╚═══╝
                            
 
*/
const oled_item_t* const menu_items[] = {   &fan_info,      
                                            &encoder_info,
                                            &system_info,
                                            &computer_info, 
                                            &settings,     
                                            &quick_settings };
#define MENU_ITEMS_SIZE 6

/*
 
 ██████╗ ███████╗███████╗
 ██╔══██╗██╔════╝██╔════╝
 ██║  ██║█████╗  █████╗  
 ██║  ██║██╔══╝  ██╔══╝  
 ██████╔╝███████╗██║     
 ╚═════╝ ╚══════╝╚═╝     
                         
 
*/

const oled_item_t menu = {
    .son = menu_items,
    .son_size = MENU_ITEMS_SIZE,
    .state = &menu_state,
};


/*
 
 ██╗     ███████╗██╗   ██╗███████╗██╗         ████████╗██╗    ██╗ ██████╗ 
 ██║     ██╔════╝██║   ██║██╔════╝██║         ╚══██╔══╝██║    ██║██╔═══██╗
 ██║     █████╗  ██║   ██║█████╗  ██║            ██║   ██║ █╗ ██║██║   ██║
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║            ██║   ██║███╗██║██║   ██║
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗       ██║   ╚███╔███╔╝╚██████╔╝
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝       ╚═╝    ╚══╝╚══╝  ╚═════╝ 
                                                                          
 
 
 ███████╗████████╗ █████╗ ████████╗███████╗
 ██╔════╝╚══██╔══╝██╔══██╗╚══██╔══╝██╔════╝
 ███████╗   ██║   ███████║   ██║   █████╗  
 ╚════██║   ██║   ██╔══██║   ██║   ██╔══╝  
 ███████║   ██║   ██║  ██║   ██║   ███████╗
 ╚══════╝   ╚═╝   ╚═╝  ╚═╝   ╚═╝   ╚══════╝
                                           
 
*/

oled_item_state_t fan_info_state;
oled_item_state_t encoder_info_state;
oled_item_state_t system_info_state;
oled_item_state_t computer_info_state;
oled_item_state_t settings_state;
oled_item_state_t quick_settings_state;

/*
 
 ███████╗ ██████╗ ███╗   ██╗
 ██╔════╝██╔═══██╗████╗  ██║
 ███████╗██║   ██║██╔██╗ ██║
 ╚════██║██║   ██║██║╚██╗██║
 ███████║╚██████╔╝██║ ╚████║
 ╚══════╝ ╚═════╝ ╚═╝  ╚═══╝
                            
 
*/

const oled_item_t* const fan_info_sons[] = {
    &fan_speed,
    &fan_mode,
    &fan_speed_curve,
};
#define FAN_INFO_SONS_SIZE 3

const oled_item_t* const system_info_sons[] = {
    &esp32c2_cpu,
    &esp32c2_heap,
    &esp32c2_runtime,
    &esp32c2_task,
};
#define SYSTEM_INFO_SONS_SIZE 4

const oled_item_t* const computer_info_sons[] = {
    &computer_cpu,
    &computer_gpu,
    &computer_ram,
    &computer_network,
};
#define COMPUTER_INFO_SONS_SIZE 4

const oled_item_t* const settings_sons[] = {
    &fan_settings,
    &button_settings,
    &encoder_settings,
    &pwm_settings,
    &light_settings
};
#define SETTINGS_SONS_SIZE 5

/*
 
 ██████╗ ███████╗███████╗
 ██╔══██╗██╔════╝██╔════╝
 ██║  ██║█████╗  █████╗  
 ██║  ██║██╔══╝  ██╔══╝  
 ██████╔╝███████╗██║     
 ╚═════╝ ╚══════╝╚═╝     
                         
 
*/

const oled_item_t fan_info = {
    .display_overview = fan_info_OV,
    .son = fan_info_sons,
    .son_size = FAN_INFO_SONS_SIZE,
    .parent = &menu,
    .state = &fan_info_state,
};

const oled_item_t encoder_info = {
    .display_overview = encoder_info_OV,
    .display_detail = encoder_info_DE,
    .parent = &menu,
    .state = &encoder_info_state,
};

const oled_item_t system_info = {
    .display_overview = system_info_OV,
    .son = system_info_sons,
    .son_size = SYSTEM_INFO_SONS_SIZE,
    .parent = &menu,
    .state = &system_info_state,
};

const oled_item_t computer_info = {
    .display_overview = computer_info_OV,
    .son = computer_info_sons,
    .son_size = COMPUTER_INFO_SONS_SIZE,
    .parent = &menu,
    .state = &computer_info_state,
};

const oled_item_t settings = {
    .display_overview = settings_OV,
    .son = settings_sons,
    .son_size = SETTINGS_SONS_SIZE,
    .parent = &menu,
    .state = &settings_state,
};

const oled_item_t quick_settings = {
    .display_overview = quick_settings_OV,
    .display_detail = quick_settings_DE,
    .parent = &menu,
    .state = &quick_settings_state,
};


/*
 
 ██╗     ███████╗██╗   ██╗███████╗██╗         ████████╗██╗  ██╗██████╗ 
 ██║     ██╔════╝██║   ██║██╔════╝██║         ╚══██╔══╝██║  ██║██╔══██╗
 ██║     █████╗  ██║   ██║█████╗  ██║            ██║   ███████║██████╔╝
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║            ██║   ██╔══██║██╔══██╗
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗       ██║   ██║  ██║██║  ██║
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝       ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝
                                                                       
 
 
 ███████╗████████╗ █████╗ ████████╗███████╗
 ██╔════╝╚══██╔══╝██╔══██╗╚══██╔══╝██╔════╝
 ███████╗   ██║   ███████║   ██║   █████╗  
 ╚════██║   ██║   ██╔══██║   ██║   ██╔══╝  
 ███████║   ██║   ██║  ██║   ██║   ███████╗
 ╚══════╝   ╚═╝   ╚═╝  ╚═╝   ╚═╝   ╚══════╝
                                           
 
*/

oled_item_state_t fan_speed_state;
oled_item_state_t fan_mode_state;
oled_item_state_t fan_speed_curve_state;

oled_item_state_t esp32c2_cpu_state;
oled_item_state_t esp32c2_heap_state;
oled_item_state_t esp32c2_runtime_state;
oled_item_state_t esp32c2_task_state;

oled_item_state_t computer_cpu_state;
oled_item_state_t computer_gpu_state;
oled_item_state_t computer_ram_state;
oled_item_state_t computer_network_state;

oled_item_state_t fan_settings_state;
oled_item_state_t pwm_settings_state;
oled_item_state_t button_settings_state;
oled_item_state_t encoder_settings_state;
oled_item_state_t light_settings_state;

/*
 
 ███████╗ ██████╗ ███╗   ██╗
 ██╔════╝██╔═══██╗████╗  ██║
 ███████╗██║   ██║██╔██╗ ██║
 ╚════██║██║   ██║██║╚██╗██║
 ███████║╚██████╔╝██║ ╚████║
 ╚══════╝ ╚═════╝ ╚═╝  ╚═══╝
                            
 
*/


const oled_item_t* const fan_settings_sons[] = {
    &fan_mode_settings,
    &fan_speed_curve_settings,
};
#define FAN_SETTINGS_SONS_SIZE 2

const oled_item_t* const buttons_settings_sons[] = {
    &button_longpress,
    &button_shortpress,
};
#define BUTTONS_SETTINGS_SONS_SIZE 2

const oled_item_t* const light_settings_sons[] = {
    &light_brightness_settings,
    &light_mode_settings,
    &light_period_settings,
};
#define LIGHT_SETTINGS_SONS_SIZE 3

/*
 
 ██████╗ ███████╗███████╗
 ██╔══██╗██╔════╝██╔════╝
 ██║  ██║█████╗  █████╗  
 ██║  ██║██╔══╝  ██╔══╝  
 ██████╔╝███████╗██║     
 ╚═════╝ ╚══════╝╚═╝     
                         
 
*/


const oled_item_t fan_speed = {
    .display_detail = fan_speed_DE,
    .display_overview = fan_speed_OV,
    .parent = &fan_info,
    .state = &fan_speed_state,
};

const oled_item_t fan_mode = {
    .display_detail = fan_mode_DE,
    .display_overview = fan_mode_OV,
    .parent = &fan_info,
    .state = &fan_mode_state,
};

const oled_item_t fan_speed_curve = {
    .display_detail = fan_speed_curve_DE,
    .display_overview = fan_speed_curve_OV,
    .parent = &fan_info,
    .state = &fan_speed_curve_state,
};

const oled_item_t esp32c2_cpu = {
    .display_detail = esp32c2_cpu_DE,
    .display_overview = esp32c2_cpu_OV,
    .parent = &system_info,
    .state = &esp32c2_cpu_state,
};

const oled_item_t esp32c2_heap = {
    .display_detail = esp32c2_heap_DE,
    .display_overview = esp32c2_heap_OV,
    .parent = &system_info,
    .state = &esp32c2_heap_state,
};

const oled_item_t esp32c2_runtime = {
    .display_detail = esp32c2_runtime_DE,
    .display_overview = esp32c2_runtime_OV,
    .parent = &system_info,
    .state = &esp32c2_runtime_state,
};

const oled_item_t esp32c2_task = {
    .display_detail = esp32c2_task_DE,
    .display_overview = esp32c2_task_OV,
    .parent = &system_info,
    .state = &esp32c2_task_state,
};

// computer_info 子项
const oled_item_t computer_cpu = {
    .display_detail = computer_cpu_DE,
    .display_overview = computer_cpu_OV,
    .parent = &computer_info,
    .state = &computer_cpu_state,
};

const oled_item_t computer_gpu = {
    .display_detail = computer_gpu_DE,
    .display_overview = computer_gpu_OV,
    .parent = &computer_info,
    .state = &computer_gpu_state,
};

const oled_item_t computer_ram = {
    .display_detail = computer_ram_DE,
    .display_overview = computer_ram_OV,
    .parent = &computer_info,
    .state = &computer_ram_state,
};

const oled_item_t computer_network = {
    .display_detail = computer_network_DE,
    .display_overview = computer_network_OV,
    .parent = &computer_info,
    .state = &computer_network_state,
};

// settings 子项
const oled_item_t fan_settings = {
    .display_overview = fan_settings_OV,
    .son = fan_settings_sons,
    .son_size = FAN_SETTINGS_SONS_SIZE,
    .parent = &settings,
    .state = &fan_settings_state,
};

const oled_item_t pwm_settings = {
    .display_detail = pwm_settings_DE,
    .display_overview = pwm_settings_OV,
    .parent = &settings,
    .state = &pwm_settings_state,
};

const oled_item_t button_settings = {
    .display_overview = button_settings_OV,
    .son = buttons_settings_sons,
    .son_size = BUTTONS_SETTINGS_SONS_SIZE,
    .parent = &settings,
    .state = &button_settings_state,
};

const oled_item_t encoder_settings = {
    .display_detail = encoder_settings_DE,
    .display_overview = encoder_settings_OV,
    .parent = &settings,
    .state = &encoder_settings_state,
};

const oled_item_t light_settings = {
    .display_overview = light_settings_OV,
    .son = light_settings_sons,
    .son_size = LIGHT_SETTINGS_SONS_SIZE,
    .parent = &settings,
    .state = &light_settings_state,
};

/*
 
 ██╗     ███████╗██╗   ██╗███████╗██╗         ███████╗ ██████╗ ██╗   ██╗██████╗ 
 ██║     ██╔════╝██║   ██║██╔════╝██║         ██╔════╝██╔═══██╗██║   ██║██╔══██╗
 ██║     █████╗  ██║   ██║█████╗  ██║         █████╗  ██║   ██║██║   ██║██████╔╝
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║         ██╔══╝  ██║   ██║██║   ██║██╔══██╗
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗    ██║     ╚██████╔╝╚██████╔╝██║  ██║
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝    ╚═╝      ╚═════╝  ╚═════╝ ╚═╝  ╚═╝
                                                                                
 
 
 ███████╗████████╗ █████╗ ████████╗███████╗
 ██╔════╝╚══██╔══╝██╔══██╗╚══██╔══╝██╔════╝
 ███████╗   ██║   ███████║   ██║   █████╗  
 ╚════██║   ██║   ██╔══██║   ██║   ██╔══╝  
 ███████║   ██║   ██║  ██║   ██║   ███████╗
 ╚══════╝   ╚═╝   ╚═╝  ╚═╝   ╚═╝   ╚══════╝
                                           
 
*/



oled_item_state_t fan_speed_curve_settings_state;
oled_item_state_t fan_mode_settings_state;
oled_item_state_t button_longpress_state;
oled_item_state_t button_shortpress_state;

oled_item_state_t light_brightness_settings_state;
oled_item_state_t light_mode_settings_state;
oled_item_state_t light_period_settings_state;

/*
 
 ███████╗ ██████╗ ███╗   ██╗
 ██╔════╝██╔═══██╗████╗  ██║
 ███████╗██║   ██║██╔██╗ ██║
 ╚════██║██║   ██║██║╚██╗██║
 ███████║╚██████╔╝██║ ╚████║
 ╚══════╝ ╚═════╝ ╚═╝  ╚═══╝
                            
 
*/

const oled_item_t* const light_mode_settings_sons[] = {
    &light_mode_off,
    &light_mode_static,
    &light_mode_breath,
    &light_mode_blink,
    &light_mode_chase,
    &light_mode_rainbow,
    &light_mode_custom_breath,
    &light_mode_random_flash,
    &light_mode_round,
};
#define LIGHT_MODE_SETTINGS_SONS_SIZE 9


/*
 
 ██████╗ ███████╗███████╗
 ██╔══██╗██╔════╝██╔════╝
 ██║  ██║█████╗  █████╗  
 ██║  ██║██╔══╝  ██╔══╝  
 ██████╔╝███████╗██║     
 ╚═════╝ ╚══════╝╚═╝     
                         
 
*/


// fan_settings 的子项
const oled_item_t fan_mode_settings = {
    .display_detail = fan_mode_settings_DE,
    .display_overview = fan_mode_settings_OV,
    .parent = &fan_settings,
    .state = &fan_mode_settings_state
};

const oled_item_t fan_speed_curve_settings = {
    .display_overview = fan_speed_curve_OV,
    .display_detail = fan_speed_curve_settings_DE,
    .parent = &fan_settings,
    .state = &fan_speed_curve_settings_state,
};


// button_settings 的子项
const oled_item_t button_longpress = {
    .display_overview = button_longpress_OV,
    .display_detail = button_longpress_DE,
    .parent = &button_settings,
    .state = &button_longpress_state,
};

const oled_item_t button_shortpress = {
    .display_overview = button_shortpress_OV,
    .display_detail = button_shortpress_DE,
    .parent = &button_settings,
    .state = &button_shortpress_state,
};



// light settings
const oled_item_t light_brightness_settings = {
    .display_detail = light_brightness_settings_DE,
    .display_overview = light_brightness_settings_OV,
    .parent = &light_settings,
    .state = &light_brightness_settings_state,
};

const oled_item_t light_mode_settings = {
    .display_overview = light_mode_settings_OV,
    .son = light_mode_settings_sons,
    .son_size = LIGHT_MODE_SETTINGS_SONS_SIZE,
    .parent = &light_settings,
    .state = &light_mode_settings_state,
};

const oled_item_t light_period_settings = {
    .display_overview = light_period_settings_OV,
    .display_detail = light_period_settings_DE,
    .parent = &light_settings,
    .state = &light_period_settings_state,
};


/*
 
 ██╗     ███████╗██╗   ██╗███████╗██╗         ███████╗██╗
 ██║     ██╔════╝██║   ██║██╔════╝██║         ██╔════╝██║
 ██║     █████╗  ██║   ██║█████╗  ██║         █████╗  ██║
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║         ██╔══╝  ██║
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗    ██║     ██║
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝    ╚═╝     ╚═╝
                                                         
 
 
 ███████╗████████╗ █████╗ ████████╗███████╗
 ██╔════╝╚══██╔══╝██╔══██╗╚══██╔══╝██╔════╝
 ███████╗   ██║   ███████║   ██║   █████╗  
 ╚════██║   ██║   ██╔══██║   ██║   ██╔══╝  
 ███████║   ██║   ██║  ██║   ██║   ███████╗
 ╚══════╝   ╚═╝   ╚═╝  ╚═╝   ╚═╝   ╚══════╝
                                           
 
*/

oled_item_state_t light_mode_off_state; 
oled_item_state_t light_mode_static_state; 
oled_item_state_t light_mode_breath_state; 
oled_item_state_t light_mode_blink_state; 
oled_item_state_t light_mode_chase_state; 
oled_item_state_t light_mode_rainbow_state; 
oled_item_state_t light_mode_round_state; 
oled_item_state_t light_mode_custom_breath_state; 
oled_item_state_t light_mode_random_flash_state; 


/*
 
 ███████╗ ██████╗ ███╗   ██╗
 ██╔════╝██╔═══██╗████╗  ██║
 ███████╗██║   ██║██╔██╗ ██║
 ╚════██║██║   ██║██║╚██╗██║
 ███████║╚██████╔╝██║ ╚████║
 ╚══════╝ ╚═════╝ ╚═╝  ╚═══╝
                            
 
*/

//NONE


/*
 
 ██████╗ ███████╗███████╗
 ██╔══██╗██╔════╝██╔════╝
 ██║  ██║█████╗  █████╗  
 ██║  ██║██╔══╝  ██╔══╝  
 ██████╔╝███████╗██║     
 ╚═════╝ ╚══════╝╚═╝     
                         
 
*/

// fan mode set 的子项

const oled_item_t light_mode_off = {
    .display_overview = light_mode_off_OV,
    .display_detail = light_mode_off_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_off_state,
};

const oled_item_t light_mode_static = {
    .display_overview = light_mode_static_OV,
    .display_detail = light_mode_static_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_static_state,
};

const oled_item_t light_mode_breath = {
    .display_overview = light_mode_breath_OV,
    .display_detail = light_mode_breath_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_breath_state,
};

const oled_item_t light_mode_blink = {
    .display_overview = light_mode_blink_OV,
    .display_detail = light_mode_blink_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_blink_state,
};

const oled_item_t light_mode_chase = {
    .display_overview = light_mode_chase_OV,
    .display_detail = light_mode_chase_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_chase_state,
};

const oled_item_t light_mode_rainbow = {
    .display_overview = light_mode_rainbow_OV,
    .display_detail = light_mode_rainbow_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_rainbow_state,
};

const oled_item_t light_mode_custom_breath = {
    .display_overview = light_mode_custom_breath_OV,
    .display_detail = light_mode_custom_breath_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_custom_breath_state,
};

const oled_item_t light_mode_round = {
    .display_overview = light_mode_round_OV,
    .display_detail = light_mode_round_DE,
    .parent = &light_mode_settings,
    .state = &light_mode_round_state,
};

const oled_item_t light_mode_random_flash = {
    .display_overview = light_mode_random_flash_OV,
    .display_detail = light_mode_random_flash_DE,
    .son = light_mode_settings_sons,
    .state = &light_mode_random_flash_state,
};



/*
 
 ███████╗██╗   ██╗███╗   ██╗ ██████╗████████╗██╗ ██████╗ ███╗   ██╗
 ██╔════╝██║   ██║████╗  ██║██╔════╝╚══██╔══╝██║██╔═══██╗████╗  ██║
 █████╗  ██║   ██║██╔██╗ ██║██║        ██║   ██║██║   ██║██╔██╗ ██║
 ██╔══╝  ██║   ██║██║╚██╗██║██║        ██║   ██║██║   ██║██║╚██╗██║
 ██║     ╚██████╔╝██║ ╚████║╚██████╗   ██║   ██║╚██████╔╝██║ ╚████║
 ╚═╝      ╚═════╝ ╚═╝  ╚═══╝ ╚═════╝   ╚═╝   ╚═╝ ╚═════╝ ╚═╝  ╚═══╝
                                                                   
 
 ██╗     ███████╗██╗   ██╗███████╗██╗         ████████╗██╗    ██╗ ██████╗ 
 ██║     ██╔════╝██║   ██║██╔════╝██║         ╚══██╔══╝██║    ██║██╔═══██╗
 ██║     █████╗  ██║   ██║█████╗  ██║            ██║   ██║ █╗ ██║██║   ██║
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║            ██║   ██║███╗██║██║   ██║
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗       ██║   ╚███╔███╔╝╚██████╔╝
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝       ╚═╝    ╚══╝╚══╝  ╚═════╝ 
                                                                          
 
  ██████╗ ██╗   ██╗███████╗██████╗ ██╗   ██╗██╗███████╗██╗    ██╗
 ██╔═══██╗██║   ██║██╔════╝██╔══██╗██║   ██║██║██╔════╝██║    ██║
 ██║   ██║██║   ██║█████╗  ██████╔╝██║   ██║██║█████╗  ██║ █╗ ██║
 ██║   ██║╚██╗ ██╔╝██╔══╝  ██╔══██╗╚██╗ ██╔╝██║██╔══╝  ██║███╗██║
 ╚██████╔╝ ╚████╔╝ ███████╗██║  ██║ ╚████╔╝ ██║███████╗╚███╔███╔╝
  ╚═════╝   ╚═══╝  ╚══════╝╚═╝  ╚═╝  ╚═══╝  ╚═╝╚══════╝ ╚══╝╚══╝ 
                                                                 
 
*/

char* UNKNOWN_FONT = "Font Lost";
char* fan_info_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Fan Infomation";
    }
    else if (font == 2) {
        return "Fan Inf";
    }
    else if (font == 3) {
        return "风扇信息";
    }
    return UNKNOWN_FONT;
}
char* encoder_info_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Encoder Infomation";
    }
    else if (font == 2) {
        return "Encoder Inf";
    }
    else if (font == 3) {
        return "编码器信息";
    }
    return UNKNOWN_FONT;
}
char* system_info_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "System Infomation";
    }
    else if (font == 2) {
        return "System Inf";
    }
    else if (font == 3) {
        return "系统信息";
    }

    return UNKNOWN_FONT;
}
char* computer_info_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Computer Infomation";
    }
    else if (font == 2) {
        return "Comp Inf";
    }
    else if (font == 3) {
        return "电脑信息";
    }

    return UNKNOWN_FONT;
}
char* settings_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Settings";
    }
    else if (font == 2) {
        return "Settings";
    }
    else if (font == 3) {
        return "设置";
    }
    return UNKNOWN_FONT;
}
char* quick_settings_OV(void) {
    uint8_t font = oled_get_font();

    if (font == 1) {
        return "Quick Settings";
    }
    else if (font == 2) {
        return "Q Settings";
    }
    else if (font == 3) {
        return "快速设置";
    }
    return UNKNOWN_FONT;
}

/*
 
 ██████╗ ███████╗████████╗ █████╗ ██╗██╗     
 ██╔══██╗██╔════╝╚══██╔══╝██╔══██╗██║██║     
 ██║  ██║█████╗     ██║   ███████║██║██║     
 ██║  ██║██╔══╝     ██║   ██╔══██║██║██║     
 ██████╔╝███████╗   ██║   ██║  ██║██║███████╗
 ╚═════╝ ╚══════╝   ╚═╝   ╚═╝  ╚═╝╚═╝╚══════╝
                                             
 
*/

void encoder_info_DE(void) {
    oled_clear();

    uint8_t font = oled_get_font();
    encoder_data_t enc_data = get_encoder_data();

    if (font == 1) {
        oled_print(0, 0, "Total: %5ld", enc_data.total);
        oled_print(0, 1, "Different: %5ld", enc_data.diff);
    }
    else if (font == 2) {
        oled_print(0, 0, "Total: %5ld", enc_data.total);
        oled_print(0, 1, "Diff: %5ld", enc_data.diff);
    }
    else if (font == 3) {
        oled_print(0, 0, "总计数: %5ld", enc_data.total);
        oled_print(0, 1, "待处理偏差: %5ld", enc_data.diff);
    }
    else {
        oled_err_print();
    }
    oled_update();
}
void quick_settings_DE(void) { oled_print_todo(); };



/*
 
 ██╗     ███████╗██╗   ██╗███████╗██╗         ████████╗██╗  ██╗██████╗ ███████╗███████╗
 ██║     ██╔════╝██║   ██║██╔════╝██║         ╚══██╔══╝██║  ██║██╔══██╗██╔════╝██╔════╝
 ██║     █████╗  ██║   ██║█████╗  ██║            ██║   ███████║██████╔╝█████╗  █████╗  
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║            ██║   ██╔══██║██╔══██╗██╔══╝  ██╔══╝  
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗       ██║   ██║  ██║██║  ██║███████╗███████╗
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝       ╚═╝   ╚═╝  ╚═╝╚═╝  ╚═╝╚══════╝╚══════╝
                                                                                       
 
  ██████╗ ██╗   ██╗███████╗██████╗ ██╗   ██╗██╗███████╗██╗    ██╗
 ██╔═══██╗██║   ██║██╔════╝██╔══██╗██║   ██║██║██╔════╝██║    ██║
 ██║   ██║██║   ██║█████╗  ██████╔╝██║   ██║██║█████╗  ██║ █╗ ██║
 ██║   ██║╚██╗ ██╔╝██╔══╝  ██╔══██╗╚██╗ ██╔╝██║██╔══╝  ██║███╗██║
 ╚██████╔╝ ╚████╔╝ ███████╗██║  ██║ ╚████╔╝ ██║███████╗╚███╔███╔╝
  ╚═════╝   ╚═══╝  ╚══════╝╚═╝  ╚═╝  ╚═══╝  ╚═╝╚══════╝ ╚══╝╚══╝ 
                                                                 
 
*/



char* fan_speed_OV(void) {
    uint32_t rpm = fan_get_rpm();
    uint8_t font = oled_get_font();
    sprintf(output, "%s", UNKNOWN_FONT);
    if (font == 1) {
        sprintf(output, "Current RPM: %5ld", rpm);
    }
    else if (font == 2) {
        sprintf(output, "RPM: %5ld", rpm);
    }
    else if (font == 3) {
        sprintf(output, "风扇转速: %5ld", rpm);
    }
    return output;
}

char* fan_mode_OV(void) {
    uint8_t font = oled_get_font();
    fan_mode_t mode = fan_get_mode();
    sprintf(output, "%s", UNKNOWN_FONT);
    if (font == 1) {
        sprintf(output,
                "Current Mode: %s",
                mode == MANUAL           ? "Manual"
                    : mode == AUTO       ? "Auto"
                    : mode == FULL_SPEED ? "Full Speed"
                                         : "Unknown State");
    }
    else if (font == 2) {
        sprintf(output,
                "Mode: %s",
                mode == MANUAL           ? "Manual"
                    : mode == AUTO       ? "Auto"
                    : mode == FULL_SPEED ? "Full"
                                         : "Unknown");
    }
    else if (font == 3) {
        sprintf(output,
                "当前模式: %s",
                mode == MANUAL           ? "手动"
                    : mode == AUTO       ? "自动"
                    : mode == FULL_SPEED ? "全速"
                                         : "未知");
    }
    return output;
}

char* fan_speed_curve_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "w-t curve";
    }
    else if (font == 2) {
        return "w-t curve";
    }
    else if (font == 3) {
        return "速度 温度关系图";
    }
    return UNKNOWN_FONT;
}

char* esp32c2_cpu_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "ESP32c2 CPU Usage";
    }
    else if (font == 2) {
        return "CPU usage";
    }
    else if (font == 3) {
        return "CPU使用率";
    }
    return UNKNOWN_FONT;
}

char* esp32c2_heap_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "ESP32c2 Heap Usage";
    }
    else if (font == 2) {
        return "Heap usage";
    }
    else if (font == 3) {
        return "堆内存使用率";
    }
    return UNKNOWN_FONT;
}

char* esp32c2_runtime_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "ESP32c2 Runtime";
    }
    else if (font == 2) {
        return "Runtime";
    }
    else if (font == 3) {
        return "运行时间";
    }
    return UNKNOWN_FONT;
}

char* esp32c2_task_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "ESP32c2 Task View";
    }
    else if (font == 2) {
        return "Task View";
    }
    else if (font == 3) {
        return "运行任务查看";
    }
    return UNKNOWN_FONT;
}

char* computer_cpu_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Computer CPU Usage";
    }
    else if (font == 2) {
        return "CPU Usage";
    }
   else if (font == 3) {
        return "CPU使用率";
    }
    return UNKNOWN_FONT;
}

char* computer_gpu_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Computer GPU Usage";
    }
    else if (font == 2) {
        return "GPU Usage";
    }
    else if (font == 3) {
        return "GPU使用率";
    }
    return UNKNOWN_FONT;
}

char* computer_ram_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Computer RAM Usage";
    }
    else if (font == 2) {
        return "RAM Usage";
    }
    else if (font == 3) {
        return "RAM使用率";
    }
    return UNKNOWN_FONT;
}

char* computer_network_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Computer Network";
    }
    else if (font == 2) {
        return "Network";
    }
    else if (font == 3) {
        return "Network";
    }
    return UNKNOWN_FONT;
}

char* fan_settings_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Fan Settings";
    }
    else if (font == 2) {
        return "Fan Sets";
    }
    else if (font == 3) {
        return "风扇设置";
    }
    return UNKNOWN_FONT;
}

char* pwm_settings_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "PWM Settings";
    }
    else if (font == 2) {
        return "PWM Sets";
    }
    else if (font == 3) {
        return "PWM 设置";
    }
    return UNKNOWN_FONT;
}

char* button_settings_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Button Settings";
    }
    else if (font == 2) {
        return "Button Sets";
    }
    else if (font == 3) {
        return "按钮设置";
    }
    return UNKNOWN_FONT;
}

char* encoder_settings_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Encoder Settings";
    }
    else if (font == 2) {
        return "Encoder Sets";
    }
    else if (font == 3) {
        return "编码器设置";
    }
    return UNKNOWN_FONT;
}

char* light_mode_settings_OV(void) {
    uint8_t font = oled_get_font();
    if(font == 1) { 
        return "LED Settings";
    } else if(font == 2) {
        return "LED Sets";
    } else if(font == 3) {
        return "LED 设置";
    }
    return UNKNOWN_FONT;
}

char* light_settings_OV(void) {
    return UNKNOWN_FONT;
}

/*
 
 ██████╗ ███████╗████████╗ █████╗ ██╗██╗     
 ██╔══██╗██╔════╝╚══██╔══╝██╔══██╗██║██║     
 ██║  ██║█████╗     ██║   ███████║██║██║     
 ██║  ██║██╔══╝     ██║   ██╔══██║██║██║     
 ██████╔╝███████╗   ██║   ██║  ██║██║███████╗
 ╚═════╝ ╚══════╝   ╚═╝   ╚═╝  ╚═╝╚═╝╚══════╝
                                             
 
*/


void fan_speed_DE(void) {
    uint32_t rpm = fan_get_rpm();

    static oled_plot_t s_fan_rpm_plot;
    static bool is_plot_init = 0;
    if (!is_plot_init) {
        oled_plot_init(&s_fan_rpm_plot, 0, 0, 127, 47, true, OLED_PLOT_MODE_SCROLL, true, 1000.0f, 3500.0f);
        is_plot_init = true;
    }
    uint8_t font = oled_get_font();
    oled_plot_push(&s_fan_rpm_plot, rpm);
    oled_plot_draw(&s_fan_rpm_plot);
    oled_clear();
    if (font == 1) {
        oled_print(0, 6, "RPM:%5ld", rpm);
        oled_print(0, 7, "N:%5ld X:%5ld", s_fan_rpm_plot.window_min, s_fan_rpm_plot.window_max);
    }
    else if (font == 2) {
        oled_print(0, 3, "RPM:%5ld", rpm);
    }
    else if (font == 3) {
        oled_print(0, 3, "转速: %5ld", rpm);
    }
    oled_update();
}

void fan_mode_DE(void) {
    oled_clear();

    uint8_t font = oled_get_font();
    if (font == 1) {
        oled_print(0, 0, "Your Current Mode Is");
        if (fan.mode == MANUAL)
            oled_print(0, 1, "Manual");
        else if (fan.mode == AUTO)
            oled_print(0, 1, "Auto");
        else if (fan.mode == FULL_SPEED)
            oled_print(0, 1, "Full Speed");
    }
    else if (font == 2) {
        oled_print(0, 0, "Your Mode");
        if (fan.mode == MANUAL)
            oled_print(0, 1, "Manual");
        else if (fan.mode == AUTO)
            oled_print(0, 1, "Auto");
        else if (fan.mode == FULL_SPEED)
            oled_print(0, 1, "Full Speed");
    }
    else if (font == 3) {
        oled_print(0, 0, "当前模式");
        if (fan.mode == MANUAL)
            oled_print(0, 1, "手动");
        else if (fan.mode == AUTO)
            oled_print(0, 1, "自动");
        else if (fan.mode == FULL_SPEED)
            oled_print(0, 1, "全速");
    }
    else {
        oled_err_print();
    }
    oled_update();
}

void fan_speed_curve_DE(void) {
    fan_speed_curve_state.receive_short_press = true;

    static oled_page_data_manager_static_t page_data;
    uint8_t* curve = fan_get_curve();
    uint32_t current_t = esp_timer_get_time() / 1000;
    if (page_data.last_update == 0 || current_t - page_data.last_update >= MAX_SLEEP_TIME) {
        page_data.last_total = get_encoder_data().total;
        page_data.last_update = current_t;
    }
    uint8_t visible_line = 0;
    uint8_t font = oled_get_font();
    if (font == 1) {
        visible_line = 8;
    }
    else if (font == 2) {
        visible_line = 4;
    }
    else if (font == 3) {
        visible_line = 4;
    }


    oled_page_data_static_update(&page_data, get_encoder_data().total, SPEED_CURVE_MEMBER, visible_line);

    if (fan_speed_curve_settings.state->operate == SHORT_PRESSED) {
        curve[page_data.cursor_line]++;
        if (curve[page_data.cursor_line] > 10)
            curve[page_data.cursor_line] = 0;

        fan_speed_curve_settings.state->operate = NOT_PRESSED;
    }

    oled_clear();

    oled_point_t lu, rd;
    uint8_t line_dist = 0;
    if (font == 1) {
        lu.x = 8 * 2;
        lu.y = 0;
        rd.x = 128 - 8 * 2 - 1 - 1;
        rd.y = 63;
        line_dist = 8;
    }
    else if (font == 2) {
        lu.x = 16 * 2;
        lu.y = 0;
        rd.x = 128 - 16 * 2 - 1 - 1;
        rd.y = 63;
        line_dist = 16;
    }
    else if (font == 3) {
        lu.x = 16 * 2;
        lu.y = 0;
        rd.x = 128 - 16 * 2 - 1 - 1;
        rd.y = 63;
        line_dist = 16;
    }

    oled_point_t coords[8];


    for (int i = 0; i < visible_line && i + page_data.start_line < SPEED_CURVE_MEMBER; i++) {
        uint8_t page_idx = i + page_data.start_line;
        oled_set_inv_color(page_idx == page_data.cursor_line);
        oled_print(0, i, "%d              %d", curve[page_idx], page_idx * 10 + 30);
        coords[i].x = (int)(rd.x - lu.x) * curve[page_idx] / 100;
        coords[i].y = (line_dist / 2) + line_dist * i - 1;
    }
    oled_set_inv_color(false);
    oled_paint_curve(lu.x, lu.y, rd.x, rd.y, coords, visible_line);


    oled_update();
}

void esp32c2_cpu_DE(void) {
    oled_clear();

    system_monitor_stats_t stats;
    system_monitor_get_realtime_stats(&stats);
    uint8_t font = oled_get_font();

    static oled_plot_t s_cpu_plot;
    static bool s_cpu_plot_inited = false;

    if (font == 1) {
        if (!s_cpu_plot_inited) {
            oled_plot_init(&s_cpu_plot, 0, 0, 128 - 1, 48 - 1, true, OLED_PLOT_MODE_SCROLL, true, 0.0f, 100.0f);
            s_cpu_plot_inited = true;
        }

        oled_plot_push(&s_cpu_plot, stats.active_ratio);
        oled_plot_draw(&s_cpu_plot);

        oled_print(0, 6, "CPU:%05.2f%%", stats.active_ratio);
        oled_print(0, 7, "N:%05.2f X:%05.2f", s_cpu_plot.window_min, s_cpu_plot.window_max);
    }
    else if (font == 2) {
        oled_print(0, 0, "CPU Usage:");
        oled_print(0, 1, "%.2f%%", stats.active_ratio);
    }
    else if (font == 3) {
        oled_print(0, 0, "CPU占用: %.2f%%", stats.active_ratio);
    }
    else {
        oled_err_print();
    }

    oled_update();
}

void esp32c2_heap_DE(void) {
    oled_clear();
    system_monitor_stats_t stats;
    system_monitor_get_realtime_stats(&stats);

    uint8_t font = oled_get_font();
    if (font == 1) {
        oled_print(0, 0, "Free Heap Bytes: %ld", stats.free_heap_bytes);
        oled_print(0, 1, "Heap Usage: %.2f%%", (float)stats.free_heap_bytes / stats.total_heap_bytes);
        oled_print(0, 2, "Free RAM Bytes: %d", stats.internal_total_bytes);
        oled_print(0, 3, "RAM Usage: %.2f%%", (float)stats.internal_free_bytes / stats.internal_total_bytes);
    }
    else if (font == 2) {
        oled_print(0, 0, "Free Heap");
        oled_print(0, 1, "%ld", stats.free_heap_bytes);
        oled_print(0, 2, "Heap Usage:");
        oled_print(0, 3, "%.2f%%", (float)stats.free_heap_bytes / stats.total_heap_bytes);
    }
    else if (font == 3) {
        oled_print(0, 0, "空闲堆内存: ");
        oled_print(0, 1, "%d", stats.free_heap_bytes);
        oled_print(0, 2, "堆内存占用: ");
        oled_print(0, 3, "%.2f%%", (float)stats.free_heap_bytes / stats.total_heap_bytes);
    }
    else {
        oled_err_print();
    }
    oled_update();
}

void esp32c2_runtime_DE(void) {
    oled_clear();
    system_monitor_stats_t stats;
    system_monitor_get_stats(&stats);
    uint8_t font = oled_get_font();
    if (font == 1) {
        oled_print(0, 0, "Runtime: %dms", stats.total_time_ms);
    }
    else if (font == 2) {
        oled_print(0, 0, "Runtime:");
        oled_print(0, 1, "%dms", stats.total_time_ms);
    }
    else if (font == 3) {
        oled_print(0, 0, "运行时间: %dms", stats.total_time_ms);
    }
    else {
        oled_err_print();
    }

    oled_update();
}

static int task_cpu_cmp_desc(const void* a, const void* b) {
    const system_task_info_t* ta = (const system_task_info_t*)a;
    const system_task_info_t* tb = (const system_task_info_t*)b;
    if (tb->cpu_percent > ta->cpu_percent) {
        return 1;
    }
    else if (tb->cpu_percent < ta->cpu_percent) {
        return -1;
    }
    return 0;
}

#define TASK_SHOW_MAX_NUM 20

void esp32c2_task_DE(void) {
    system_task_info_t task_list[TASK_SHOW_MAX_NUM];
    uint32_t actual = 0;
    uint32_t current_t = esp_timer_get_time() / 1000;
    static oled_page_data_manager_static_t page_data;

    uint8_t visible_lines = 8;
    uint8_t font = oled_get_font();

    if (page_data.last_update == 0 || current_t - page_data.last_update >= MAX_SLEEP_TIME) {
        page_data.last_total = get_encoder_data().total;
        page_data.last_update = current_t;
    }
    if (system_monitor_get_task_list(task_list, TASK_SHOW_MAX_NUM, &actual) != ESP_OK) {
        oled_clear();
        oled_print(0, 0, "Task Read Fail");
        oled_update();
        return;
    }

    if (actual == 0) {
        oled_clear();
        oled_print(0, 0, "No Task");
        oled_update();
        return;
    }

    qsort(task_list, actual, sizeof(system_task_info_t), task_cpu_cmp_desc);

    oled_page_data_static_update(&page_data, get_encoder_data().total, actual, visible_lines);

    oled_clear();
    oled_set_font(1);

    for (int8_t i = 0; i < visible_lines; i++) {
        int task_index = page_data.start_line + i;
        if (task_index >= actual) {
            break;
        }

        oled_set_inv_color(task_index == page_data.cursor_line);
        oled_print(0, i, "                          ");
        oled_print(0, i, "%04.2f%% %s", task_list[task_index].cpu_percent, task_list[task_index].task_name);
    }

    oled_set_inv_color(false);
    oled_set_font(font);
    oled_update();
}

void computer_cpu_DE(void) { oled_print_todo(); }
void computer_gpu_DE(void) { oled_print_todo(); }
void computer_ram_DE(void) { oled_print_todo(); }
void computer_network_DE(void) { oled_print_todo(); }

void pwm_settings_DE(void) { oled_print_todo(); }
void encoder_settings_DE(void) { oled_print_todo(); }


/*
                                                                                
 ██╗     ███████╗██╗   ██╗███████╗██╗         ███████╗ ██████╗ ██╗   ██╗██████╗ 
 ██║     ██╔════╝██║   ██║██╔════╝██║         ██╔════╝██╔═══██╗██║   ██║██╔══██╗
 ██║     █████╗  ██║   ██║█████╗  ██║         █████╗  ██║   ██║██║   ██║██████╔╝
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║         ██╔══╝  ██║   ██║██║   ██║██╔══██╗
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗    ██║     ╚██████╔╝╚██████╔╝██║  ██║
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝    ╚═╝      ╚═════╝  ╚═════╝ ╚═╝  ╚═╝
                                                                                
  ██████╗ ██╗   ██╗███████╗██████╗ ██╗   ██╗██╗███████╗██╗    ██╗               
 ██╔═══██╗██║   ██║██╔════╝██╔══██╗██║   ██║██║██╔════╝██║    ██║               
 ██║   ██║██║   ██║█████╗  ██████╔╝██║   ██║██║█████╗  ██║ █╗ ██║               
 ██║   ██║╚██╗ ██╔╝██╔══╝  ██╔══██╗╚██╗ ██╔╝██║██╔══╝  ██║███╗██║               
 ╚██████╔╝ ╚████╔╝ ███████╗██║  ██║ ╚████╔╝ ██║███████╗╚███╔███╔╝               
  ╚═════╝   ╚═══╝  ╚══════╝╚═╝  ╚═╝  ╚═══╝  ╚═╝╚══════╝ ╚══╝╚══╝                
                                                                                
 
*/

char* fan_speed_curve_settings_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "w-temp Curve";
    }
    else if (font == 2) {
        return "w-t Curve";
    }
    else if (font == 3) {
        return "速度-温度曲线";
    }
    return UNKNOWN_FONT;
}
char* fan_mode_settings_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Fan Mode";
    }
    else if (font == 2) {
        return "Fan Mode";
    }
    else if (font == 3) {
        return "风扇模式设置";
    }
    return UNKNOWN_FONT;
}
char* button_longpress_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Longpress Threshold";
    }
    else if (font == 2) {
        return "Lpress Thr";
    }
    else if (font == 3) {
        return "长按时间阈值";
    }
    return UNKNOWN_FONT;
}
char* button_shortpress_OV(void) {
    uint8_t font = oled_get_font();
    if (font == 1) {
        return "Shortpress Threshold";
    }
    else if (font == 2) {
        return "Spress Thr";
    }
    else if (font == 3) {
        return "消抖时间";
    }
    return UNKNOWN_FONT;
}


char* light_period_settings_OV(void) {
    return UNKNOWN_FONT;
}
char* light_brightness_settings_OV(void) {
    return UNKNOWN_FONT;
}

/*
 
 ██████╗ ███████╗████████╗ █████╗ ██╗██╗     
 ██╔══██╗██╔════╝╚══██╔══╝██╔══██╗██║██║     
 ██║  ██║█████╗     ██║   ███████║██║██║     
 ██║  ██║██╔══╝     ██║   ██╔══██║██║██║     
 ██████╔╝███████╗   ██║   ██║  ██║██║███████╗
 ╚═════╝ ╚══════╝   ╚═╝   ╚═╝  ╚═╝╚═╝╚══════╝
                                             
 
*/


// 第四级 详细渲染
void fan_speed_curve_settings_DE(void) {
    fan_speed_curve_settings_state.receive_short_press = true;

    static oled_page_data_manager_static_t page_data;
    uint8_t* curve = fan_get_curve();
    uint32_t current_t = esp_timer_get_time() / 1000;
    if (page_data.last_update == 0 || current_t - page_data.last_update >= MAX_SLEEP_TIME) {
        page_data.last_total = get_encoder_data().total;
        page_data.last_update = current_t;
    }
    uint8_t visible_line = 0;
    uint8_t font = oled_get_font();
    if (font == 1) {
        visible_line = 8;
    }
    else if (font == 2) {
        visible_line = 4;
    }
    else if (font == 3) {
        visible_line = 4;
    }


    oled_page_data_static_update(&page_data, get_encoder_data().total, SPEED_CURVE_MEMBER, visible_line);

    if (fan_speed_curve_settings.state->operate == SHORT_PRESSED) {
        curve[page_data.cursor_line]++;
        if (curve[page_data.cursor_line] > 10)
            curve[page_data.cursor_line] = 0;

        fan_speed_curve_settings.state->operate = NOT_PRESSED;
    }

    oled_clear();

    oled_point_t lu, rd;
    uint8_t line_dist = 0;
    if (font == 1) {
        lu.x = 8 * 2;
        lu.y = 0;
        rd.x = 128 - 8 * 2 - 1 - 1;
        rd.y = 63;
        line_dist = 8;
    }
    else if (font == 2) {
        lu.x = 16 * 2;
        lu.y = 0;
        rd.x = 128 - 16 * 2 - 1 - 1;
        rd.y = 63;
        line_dist = 16;
    }
    else if (font == 3) {
        lu.x = 16 * 2;
        lu.y = 0;
        rd.x = 128 - 16 * 2 - 1 - 1;
        rd.y = 63;
        line_dist = 16;
    }

    oled_point_t coords[8];


    for (int i = 0; i < visible_line && i + page_data.start_line < SPEED_CURVE_MEMBER; i++) {
        uint8_t page_idx = i + page_data.start_line;
        bool    sel      = (page_idx == page_data.cursor_line);
        /* 左侧数值：选中时字符反色，y 从 1 开始，8x16 字体每行 16px */
        oled_print_advanced(1,   i * 8 + 1,
                            sel ? OLED_PRINT_MODE_CHAR_INV : OLED_PRINT_MODE_NORMAL,
                            true,
                            "%3d", curve[page_idx]);
        /* 右侧数值：正常显示 */
        oled_print_advanced(100, i * 8 + 1,
                            OLED_PRINT_MODE_NORMAL,
                            true,
                            "%3d", page_idx * 10 + 30);
        coords[i].x = (int)(rd.x - lu.x) * curve[page_idx] / 100;
        coords[i].y = (line_dist / 2) + line_dist * i - 1;
    }
    oled_paint_curve(lu.x, lu.y, rd.x, rd.y, coords, visible_line);


    oled_update();
}


void fan_mode_settings_DE(void) {
    fan_mode_settings_state.receive_short_press = true;

    const char mode_set[FAN_MODE_SIZE][15] = { "Manual", "Auto", "Full Speed" };

    oled_page_data_manager_static_t page_data;
    uint32_t current_t = esp_timer_get_time();

    if (page_data.last_update == 0 || current_t - page_data.last_update >= MAX_SLEEP_TIME) {
        page_data.last_update = get_encoder_data().total;
        page_data.last_update = current_t;
    }
    uint8_t visible_line = 4;
    uint8_t font = oled_get_font();

    oled_page_data_static_update(&page_data, get_encoder_data().total, FAN_MODE_SIZE, visible_line);

    if (fan_mode_settings.state->operate == SHORT_PRESSED) {
        if (page_data.cursor_line == 0) {
            fan.mode = MANUAL;
        }
        else if (page_data.cursor_line == 1) {
            fan.mode = AUTO;
        }
        else if (page_data.cursor_line == 2) {
            fan.mode = FULL_SPEED;
        }
        fan_mode_settings.state->operate = NOT_PRESSED;
    }

    oled_clear();
    for (int i = 0; i < visible_line; i++) {
        oled_set_inv_color(i == page_data.cursor_line);
        oled_print(
            0,
            i,
            "%c%s",
            ((fan.mode == MANUAL && i == 0) || (fan.mode == AUTO && i == 1) || (fan.mode == FULL_SPEED && i == 2))
                ? '>'
                : ' ',
            mode_set[i]);
    }
    oled_set_inv_color(false);
    oled_update();
}
void button_longpress_DE(void) { oled_print_todo(); }
void button_shortpress_DE(void) { oled_print_todo(); }

void light_period_settings_DE(void) {
    oled_print_todo();
}
void light_brightness_settings_DE(void) {
    oled_print_todo();
}

/*
 
 ██╗     ███████╗██╗   ██╗███████╗██╗         ███████╗██╗
 ██║     ██╔════╝██║   ██║██╔════╝██║         ██╔════╝██║
 ██║     █████╗  ██║   ██║█████╗  ██║         █████╗  ██║
 ██║     ██╔══╝  ╚██╗ ██╔╝██╔══╝  ██║         ██╔══╝  ██║
 ███████╗███████╗ ╚████╔╝ ███████╗███████╗    ██║     ██║
 ╚══════╝╚══════╝  ╚═══╝  ╚══════╝╚══════╝    ╚═╝     ╚═╝
                                                         
 
 
  ██████╗ ██╗   ██╗███████╗██████╗ ██╗   ██╗██╗███████╗██╗    ██╗
 ██╔═══██╗██║   ██║██╔════╝██╔══██╗██║   ██║██║██╔════╝██║    ██║
 ██║   ██║██║   ██║█████╗  ██████╔╝██║   ██║██║█████╗  ██║ █╗ ██║
 ██║   ██║╚██╗ ██╔╝██╔══╝  ██╔══██╗╚██╗ ██╔╝██║██╔══╝  ██║███╗██║
 ╚██████╔╝ ╚████╔╝ ███████╗██║  ██║ ╚████╔╝ ██║███████╗╚███╔███╔╝
  ╚═════╝   ╚═══╝  ╚══════╝╚═╝  ╚═╝  ╚═══╝  ╚═╝╚══════╝ ╚══╝╚══╝ 
                                                                 
 
*/


char* light_mode_off_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "off";
    } else if(font == 2) {
        return "off";
    } else if(font == 3) {
        return "关闭";
    }
    return UNKNOWN_FONT;
}

char* light_mode_static_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "static";
    } else if(font == 2) {
        return "static";
    } else if(font == 3) {
        return "静态";
    }
    return UNKNOWN_FONT;
}

char* light_mode_breath_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "breath";
    } else if(font == 2) {
        return "breath";
    } else if(font == 3) {
        return "呼吸";
    }
    return UNKNOWN_FONT;
}

char* light_mode_blink_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "blink";
    } else if(font == 2) {
        return "blink";
    } else if(font == 3) {
        return "闪烁";
    }
    return UNKNOWN_FONT;
}

char* light_mode_chase_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "chase";
    } else if(font == 2) {
        return "chase";
    } else if(font == 3) {
        return "追逐";
    }
    return UNKNOWN_FONT;
}

char* light_mode_rainbow_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "rainbow";
    } else if(font == 2) {
        return "rainbow";
    } else if(font == 3) {
        return "彩虹";
    }
    return UNKNOWN_FONT;
}

char* light_mode_custom_breath_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "custom-breath";
    } else if(font == 2) {
        return "cus-breath";
    } else if(font == 3) {
        return "自定义呼吸";
    }
    return UNKNOWN_FONT;
}

char* light_mode_round_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "round";
    } else if(font == 2) {
        return "round";
    } else if(font == 3) {
        return "旋转";
    }
    return UNKNOWN_FONT;
}

char* light_mode_random_flash_OV(void){
    uint8_t font = oled_get_font();
    if(font == 1) {
        return "flash";
    } else if(font == 2) {
        return "flash";
    } else if(font == 3) {
        return "爆闪";
    }
    return UNKNOWN_FONT;
}



/*
 
 ██████╗ ███████╗████████╗ █████╗ ██╗██╗     
 ██╔══██╗██╔════╝╚══██╔══╝██╔══██╗██║██║     
 ██║  ██║█████╗     ██║   ███████║██║██║     
 ██║  ██║██╔══╝     ██║   ██╔══██║██║██║     
 ██████╔╝███████╗   ██║   ██║  ██║██║███████╗
 ╚═════╝ ╚══════╝   ╚═╝   ╚═╝  ╚═╝╚═╝╚══════╝
                                             
 
*/

void light_mode_off_DE(void){
    // oled_clear();
    // if(light_mode_off.state->operate == SHORT_PRESSED) { 
    //     light_set_mode(LIGHT_MODE_OFF);
    //     light_mode_off.state->operate = NOT_PRESSED;
    // }
    // oled_print(0,0,"%cSet", light_get_effect()->mode == LIGHT_MODE_OFF ? '>' : ' ');
    // oled_update();    
}

void light_mode_static_DE(void)
{
//     oled_clear();
//     uint8_t font = oled_get_font();
//     oled_set_font(1);

//     static int8_t current_cursor = 0;   // 0~2
//     static bool edit_mode = false;      // 是否处于编辑状态
//     static int8_t argb_sw = 0;          // 1=R, 2=G, 3=B
//     static int32_t last_encoder = 0;
//     static uint32_t last_update = 0;

//     int32_t encoder_now = get_encoder_data().total;
//     uint32_t current_t = esp_timer_get_time() / 1000;

//     if (last_update == 0 || current_t - last_update >= MAX_SLEEP_TIME) {
//         last_encoder = encoder_now;
//     }
//     last_update = current_t;

//     int32_t diff = encoder_now - last_encoder;

//     if (light_mode_static.state->operate == SHORT_PRESSED) {
//         if (!edit_mode) {
//             // 浏览模式下按键
//             if (current_cursor == 0) {
//                 light_set_mode(LIGHT_MODE_STATIC);
//             }
//             else if (current_cursor == 1) {
//                 // 进入 RGB 编辑模式，默认先编辑 R
//                 edit_mode = true;
//                 argb_sw = 1;
//                 last_encoder = encoder_now;
//             }
//             else if (current_cursor == 2) {
//                 // 进入亮度编辑模式
//                 edit_mode = true;
//                 last_encoder = encoder_now;
//             }
//         } else {
//             // 编辑模式下按键
//             if (current_cursor == 1) {
//                 // RGB编辑时：切换 R->G->B->退出
//                 argb_sw++;
//                 if (argb_sw > 3) {
//                     argb_sw = 0;
//                     edit_mode = false;   // 退出编辑
//                 }
//                 last_encoder = encoder_now;
//             }
//             else if (current_cursor == 2) {
//                 // 亮度编辑时：再按一下退出
//                 edit_mode = false;
//                 last_encoder = encoder_now;
//             }
//         }
//         light_mode_static.state->operate = NOT_PRESSED;
//     }

//     // 处理编码器旋转
//     if (diff != 0) {
//         if (!edit_mode) {
//             // 浏览模式：旋钮移动光标
//             current_cursor += diff;
//             if (current_cursor < 0) current_cursor = 0;
//             if (current_cursor > 2) current_cursor = 2;
//         } else {
//             // 编辑模式：旋钮修改参数
//             if (current_cursor == 1) {
//                 if (argb_sw == 1) {
//                     int v = light_static_get_config()->r + diff;
//                     if (v < 0) v = 0;
//                     if (v > 255) v = 255;
//                     light_static_get_config()->r = v;
//                 }
//                 else if (argb_sw == 2) {
//                     int v = light_static_get_config()->g + diff;
//                     if (v < 0) v = 0;
//                     if (v > 255) v = 255;
//                     light_static_get_config()->g = v;
//                 }
//                 else if (argb_sw == 3) {
//                     int v = light_static_get_config()->b + diff;
//                     if (v < 0) v = 0;
//                     if (v > 255) v = 255;
//                     light_static_get_config()->b = v;
//                 }
//             }
//             else if (current_cursor == 2) {
//                 int v = light_get_effect()->brightness + diff;
//                 if (v < 0) v = 0;
//                 if (v > 255) v = 255;
//                 light_get_effect()->brightness = v;
//             }
//         }

//         last_encoder = encoder_now;
//     }
//     // 显示
//     oled_set_inv_color(current_cursor == 0 && !edit_mode);
//     oled_print(0, 0, "%cSet",
//         light_get_effect()->mode == LIGHT_MODE_STATIC ? '>' : ' ');

//     oled_set_inv_color(current_cursor == 1 && !edit_mode);
//     oled_print(0, 1, " RGB:   %c%3d  %c%3d  %c%3d",
//         (edit_mode && current_cursor == 1 && argb_sw == 1) ? '>' : ' ', light_static_get_config()->r,
//         (edit_mode && current_cursor == 1 && argb_sw == 2) ? '>' : ' ', light_static_get_config()->g,
//         (edit_mode && current_cursor == 1 && argb_sw == 3) ? '>' : ' ', light_static_get_config()->b);

//     oled_set_inv_color(current_cursor == 2 && !edit_mode);
//     oled_print(0, 2, " Brightness: %3d", light_get_effect()->brightness);

//     oled_set_inv_color(false);
//     oled_update();
//     oled_set_font(font);
}


void light_mode_breath_DE(void){
    oled_print_todo();

}

void light_mode_blink_DE(void){
    oled_print_todo();

}

void light_mode_chase_DE(void){
    oled_print_todo();

}

void light_mode_rainbow_DE(void){
    oled_print_todo();

}

void light_mode_custom_breath_DE(void){

}

void light_mode_round_DE(void){

}

void light_mode_random_flash_DE(void){

}
