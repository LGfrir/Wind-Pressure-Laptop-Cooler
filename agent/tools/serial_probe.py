"""探测 COM7 是否为 ESP32-S3：打开串口并观察复位产生的 boot 日志。"""
import sys
import time

import serial

PORT = sys.argv[1] if len(sys.argv) > 1 else "COM7"

port = serial.Serial(PORT, 115200, timeout=0.3)
port.dtr = False
port.rts = False
time.sleep(0.2)

buf = b""
t0 = time.time()
while time.time() - t0 < 3.0:
    data = port.read(4096)
    if data:
        buf += data

print(f"== 打开 {PORT} 后 3 秒内收到 {len(buf)} 字节 ==", flush=True)
if buf:
    print(buf[:900].decode("utf-8", errors="replace"), flush=True)
else:
    print("(无输出 —— 板子没有复位，或不是这个口)", flush=True)

port.close()
