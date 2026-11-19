import tkinter as tk
from tkinter import ttk
import serial
import serial.tools.list_ports
import threading

class SerialDebugger:
    def __init__(self, root):
        self.root = root
        self.root.title("串口调试工具")
        self.root.resizable(False, False)
        self.ser = None

        # ===== 顶部连接栏 =====
        top = tk.Frame(root, padx=10, pady=8)
        top.pack(fill='x')

        tk.Label(top, text="COM口:").pack(side='left')
        self.port_var = tk.StringVar()
        self.port_cb = ttk.Combobox(top, textvariable=self.port_var, width=10, state='readonly')
        self.port_cb.pack(side='left', padx=4)

        tk.Label(top, text="波特率:").pack(side='left')
        self.baud_var = tk.StringVar(value='115200')
        ttk.Combobox(top, textvariable=self.baud_var,
                     values=['9600','19200','38400','57600','74800','115200','230400'],
                     width=8, state='readonly').pack(side='left', padx=4)

        tk.Button(top, text="刷新", command=self.refresh_ports).pack(side='left', padx=4)
        self.connect_btn = tk.Button(top, text="连接", width=6,
                                     bg='#4CAF50', fg='white', command=self.toggle_connect)
        self.connect_btn.pack(side='left', padx=4)

        self.status_lbl = tk.Label(top, text="● 未连接", fg='gray')
        self.status_lbl.pack(side='left', padx=8)

        # ===== 方向键区域 =====
        mid = tk.Frame(root, padx=10, pady=5)
        mid.pack()

        btn_cfg = dict(width=6, height=2, font=('Arial', 12, 'bold'))

        tk.Button(mid, text="▲\n上(1)", **btn_cfg,
                  command=lambda: self.send(b'1')).grid(row=0, column=1, padx=4, pady=4)
        tk.Button(mid, text="◄\n左(3)", **btn_cfg,
                  command=lambda: self.send(b'3')).grid(row=1, column=0, padx=4, pady=4)
        tk.Button(mid, text="●\n确认(5)", **btn_cfg,
                  command=lambda: self.send(b'5')).grid(row=1, column=1, padx=4, pady=4)
        tk.Button(mid, text="►\n右(4)", **btn_cfg,
                  command=lambda: self.send(b'4')).grid(row=1, column=2, padx=4, pady=4)
        tk.Button(mid, text="▼\n下(2)", **btn_cfg,
                  command=lambda: self.send(b'2')).grid(row=2, column=1, padx=4, pady=4)

        # ===== 自定义发送 =====
        send_frame = tk.Frame(root, padx=10, pady=4)
        send_frame.pack(fill='x')

        tk.Label(send_frame, text="自定义:").pack(side='left')
        self.custom_entry = tk.Entry(send_frame, width=20)
        self.custom_entry.pack(side='left', padx=4)
        tk.Button(send_frame, text="发送", command=self.send_custom).pack(side='left')

        # ===== 日志框 =====
        log_frame = tk.Frame(root, padx=10, pady=4)
        log_frame.pack(fill='both', expand=True)

        tk.Label(log_frame, text="发送日志:").pack(anchor='w')
        self.log_text = tk.Text(log_frame, height=10, width=45, state='disabled',
                                bg='#1e1e1e', fg='#00ff00', font=('Consolas', 10))
        self.log_text.pack(fill='both', expand=True)
        tk.Button(log_frame, text="清空日志", command=self.clear_log).pack(anchor='e', pady=2)

        # ===== 键盘绑定 =====
        self.root.bind('<Up>',    lambda e: self.send(b'1'))
        self.root.bind('<Down>',  lambda e: self.send(b'2'))
        self.root.bind('<Left>',  lambda e: self.send(b'3'))
        self.root.bind('<Right>', lambda e: self.send(b'4'))
        self.root.bind('<Return>',lambda e: self.send(b'5'))

        self.refresh_ports()

    def refresh_ports(self):
        ports = [p.device for p in serial.tools.list_ports.comports()]
        self.port_cb['values'] = ports
        if ports:
            self.port_cb.set(ports[0])

    def toggle_connect(self):
        if self.ser and self.ser.is_open:
            self.ser.close()
            self.ser = None
            self.connect_btn.config(text="连接", bg='#4CAF50')
            self.status_lbl.config(text="● 未连接", fg='gray')
        else:
            try:
                self.ser = serial.Serial(self.port_var.get(),
                                         int(self.baud_var.get()), timeout=1)
                self.connect_btn.config(text="断开", bg='#f44336')
                self.status_lbl.config(text=f"● 已连接 {self.port_var.get()}", fg='green')
                self.log(f"已连接 {self.port_var.get()} @ {self.baud_var.get()}")
            except Exception as e:
                self.status_lbl.config(text="● 连接失败", fg='red')
                self.log(f"错误: {e}")

    def send(self, data: bytes):
        if self.ser and self.ser.is_open:
            self.ser.write(data)
            self.log(f"发送: {data.decode()}")
        else:
            self.log("未连接，无法发送")

    def send_custom(self):
        txt = self.custom_entry.get()
        if txt:
            self.send(txt.encode())

    def log(self, msg):
        self.log_text.config(state='normal')
        self.log_text.insert('end', msg + '\n')
        self.log_text.see('end')
        self.log_text.config(state='disabled')

    def clear_log(self):
        self.log_text.config(state='normal')
        self.log_text.delete('1.0', 'end')
        self.log_text.config(state='disabled')


if __name__ == '__main__':
    root = tk.Tk()
    app = SerialDebugger(root)
    root.mainloop()