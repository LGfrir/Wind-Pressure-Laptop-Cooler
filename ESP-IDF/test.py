import serial
import keyboard
import time

# ===== 配置 =====
PORT  = 'COM3'
BAUD  = 115200
# ================

ser = serial.Serial(PORT, BAUD, timeout=1)
print(f"已连接 {PORT} @ {BAUD}")
print("上=1  下=2  左=3  右=4  回车=5  ESC=退出")
print("-" * 30)

KEY_MAP = {
    'up'   : b'1',
    'down' : b'2',
    'left' : b'3',
    'right': b'4',
    'enter': b'5',
}

last_key = None

while True:
    if keyboard.is_pressed('esc'):
        print("退出")
        break

    pressed = None
    for key in KEY_MAP:
        if keyboard.is_pressed(key):
            pressed = key
            break

    if pressed and pressed != last_key:
        data = KEY_MAP[pressed]
        ser.write(data)
        print(f"[{pressed.upper():6}] -> {data.decode()}")
        last_key = pressed
    elif not pressed:
        last_key = None

    time.sleep(0.05)

ser.close()