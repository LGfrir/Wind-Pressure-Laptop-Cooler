"""捕获 ESP32-S3 完整启动日志：打开串口 → 主动拉 DTR 复位 → 读 6 秒。"""
import sys
import time

import serial

PORT = sys.argv[1] if len(sys.argv) > 1 else "COM7"

port = serial.Serial()
port.port = PORT
port.baudrate = 115200
port.timeout = 0.3
port.dtr = False
port.rts = False
port.open()
time.sleep(0.2)

# 主动复位：拉高 DTR（USB_SERIAL_JTAG 复位）
port.dtr = True
time.sleep(0.05)
port.dtr = False

buf = b""
t0 = time.time()
while time.time() - t0 < 6.0:
    data = port.read(8192)
    if data:
        buf += data

print(f"== 捕获 {len(buf)} 字节 ==", flush=True)
print(buf.decode("utf-8", errors="replace"), flush=True)
port.close()
