"""探测 COM7 的读写行为：
1) 打开（不触发复位）
2) 读 3 秒，报告内容（判断是不是被 ESP 日志刷屏）
3) 带 write_timeout 写 1 字节，报告是否超时
"""

import sys
import time

import serial

PORT = sys.argv[1] if len(sys.argv) > 1 else "COM7"

port = serial.Serial()
port.port = PORT
port.baudrate = 115200
port.timeout = 0.3
port.write_timeout = 2.0
port.dtr = False
port.rts = False
port.open()
print(f"{PORT} 已打开（dtr/rts 关闭）", flush=True)

buf = b""
t0 = time.time()
while time.time() - t0 < 3.0:
    data = port.read(4096)
    if data:
        buf += data
print(f"== 3 秒内收到 {len(buf)} 字节 ==", flush=True)
if buf:
    print(buf[:1200].decode("utf-8", errors="replace"), flush=True)

print("== 写 1 字节 '5'（write_timeout=2s）==", flush=True)
t0 = time.time()
try:
    n = port.write(b"5")
    print(f"写入成功 {n} 字节，耗时 {time.time() - t0:.2f}s", flush=True)
except serial.SerialTimeoutException:
    print(f"写入超时（>2s）！耗时 {time.time() - t0:.2f}s —— 设备不 ACK", flush=True)
except Exception as exc:
    print(f"写入异常：{type(exc).__name__}: {exc}", flush=True)

port.close()
