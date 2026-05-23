import sys
import os
import time
import threading
import socket
import select
import winreg
import ctypes
import psutil
import subprocess
import atexit
import json
from collections import defaultdict

# PySide6 Core GUI imports
from PySide6.QtWidgets import (
    QApplication, QMainWindow, QWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QLineEdit, QPushButton, QComboBox, QCheckBox, QFrame,
    QHeaderView, QTreeWidget, QTreeWidgetItem, QFileDialog, QSplitter,
    QAbstractButton, QPlainTextEdit
)
from PySide6.QtGui import QPainter, QColor, QPen, QBrush, QFont, QIcon
from PySide6.QtCore import QRect, QPropertyAnimation, Property, Qt, QTimer, Slot, QPoint, Signal

# Curated HSL Premium Colors
COLOR_BG = "#08080A"          # Deep Space Obsidian
COLOR_CARD = "#111115"        # Frosted Card Glass
COLOR_FIELD = "#1B1B22"       # Input field backgrounds
COLOR_TEXT = "#FFFFFF"        # Pure white
COLOR_MUTED = "#828292"       # Slate gray muted text
COLOR_CYAN = "#00F0FF"        # System active Cyan
COLOR_GREEN = "#00FF88"       # Download active Green
COLOR_ORANGE = "#FF9F0A"      # Upload active Orange
COLOR_BLUE = "#0A84FF"        # Primary blue accent
COLOR_DANGER = "#FF375F"      # Crimson red delete accent


class BandwidthRegulator:
    def __init__(self):
        self.lock = threading.Lock()
        self.bytes_sent_in_current_window = 0
        self.window_start = time.time()
        
    def throttle(self, byte_count, limit_bytes_per_sec):
        """Thread-safe Token Bucket rate regulator working across all connections of a process."""
        if limit_bytes_per_sec <= 0:
            return
            
        with self.lock:
            now = time.time()
            if now - self.window_start >= 1.0:
                self.bytes_sent_in_current_window = 0
                self.window_start = now
                
            self.bytes_sent_in_current_window += byte_count
            if self.bytes_sent_in_current_window > limit_bytes_per_sec:
                expected_elapsed = self.bytes_sent_in_current_window / limit_bytes_per_sec
                actual_elapsed = now - self.window_start
                if expected_elapsed > actual_elapsed:
                    sleep_duration = expected_elapsed - actual_elapsed
                    time.sleep(sleep_duration)


class SOCKS5ProxyServer:
    def __init__(self, host, port, app):
        self.host = host
        self.port = port
        self.app = app
        self.server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        
    def start(self):
        """Binds and starts the listening socket for SOCKS proxy."""
        try:
            self.server_socket.bind((self.host, self.port))
            self.server_socket.listen(100)
            threading.Thread(target=self.accept_connections, daemon=True).start()
            return True
        except Exception as e:
            self.app.log(f"Proxy bind failed on port {self.port}: {e}")
            return False
            
    def accept_connections(self):
        """Asynchronously accepts incoming network proxy socket routing requests."""
        while self.app.running:
            try:
                r, _, _ = select.select([self.server_socket], [], [], 0.5)
                if r:
                    client_socket, client_address = self.server_socket.accept()
                    threading.Thread(target=self.handle_client, args=(client_socket, client_address), daemon=True).start()
            except Exception:
                break
                
    def handle_client(self, client_socket, client_address):
        """Interprets SOCKS4 / SOCKS5 handshakes, routes destination packets, and connects bi-directional pipe loops."""
        try:
            first_packet = client_socket.recv(1024)
            if not first_packet:
                client_socket.close()
                return
                
            version = first_packet[0]
            
            if version == 4:
                # --- SOCKS4 CONNECT handshake ---
                if len(first_packet) < 9:
                    client_socket.close()
                    return
                cmd = first_packet[1]
                if cmd != 1:
                    client_socket.sendall(b"\x00\x5b\x00\x00\x00\x00\x00\x00")
                    client_socket.close()
                    return
                    
                port = int.from_bytes(first_packet[2:4], 'big')
                ip_bytes = first_packet[4:8]
                
                idx = 8
                while idx < len(first_packet) and first_packet[idx] != 0:
                    idx += 1
                idx += 1
                
                # Check for SOCKS4a domain route
                if ip_bytes[0] == 0 and ip_bytes[1] == 0 and ip_bytes[2] == 0 and ip_bytes[3] != 0:
                    domain_start = idx
                    while idx < len(first_packet) and first_packet[idx] != 0:
                        idx += 1
                    addr = first_packet[domain_start:idx].decode('utf-8', errors='ignore')
                else:
                    addr = socket.inet_ntoa(ip_bytes)
                    
                try:
                    dest_socket = socket.create_connection((addr, port), timeout=10)
                    client_socket.sendall(b"\x00\x5a\x00\x00\x00\x00\x00\x00")
                except Exception:
                    client_socket.sendall(b"\x00\x5b\x00\x00\x00\x00\x00\x00")
                    client_socket.close()
                    return
                    
            elif version == 5:
                # --- SOCKS5 CONNECT handshake ---
                client_socket.sendall(b"\x05\x00")
                
                request = client_socket.recv(1024)
                if not request or len(request) < 7 or request[0] != 5:
                    client_socket.close()
                    return
                    
                cmd = request[1]
                atype = request[3]
                
                if cmd != 1:
                    client_socket.sendall(b"\x05\x07\x00\x01\x00\x00\x00\x00\x00\x00")
                    client_socket.close()
                    return
                    
                idx = 4
                if atype == 1:
                    if len(request) < idx + 6:
                        client_socket.close()
                        return
                    addr = socket.inet_ntoa(request[idx:idx+4])
                    idx += 4
                elif atype == 3:
                    addr_len = request[idx]
                    idx += 1
                    if len(request) < idx + addr_len + 2:
                        client_socket.close()
                        return
                    addr = request[idx:idx+addr_len].decode('utf-8', errors='ignore')
                    idx += addr_len
                elif atype == 4:
                    if len(request) < idx + 18:
                        client_socket.close()
                        return
                    addr = socket.inet_ntop(socket.AF_INET6, request[idx:idx+16])
                    idx += 16
                else:
                    client_socket.close()
                    return
                    
                port = int.from_bytes(request[idx:idx+2], 'big')
                
                try:
                    dest_socket = socket.create_connection((addr, port), timeout=10)
                    client_socket.sendall(b"\x05\x00\x00\x01\x00\x00\x00\x00\x00\x00")
                except Exception:
                    client_socket.sendall(b"\x05\x05\x00\x01\x00\x00\x00\x00\x00\x00")
                    client_socket.close()
                    return
            else:
                client_socket.close()
                return
                
            # --- Successful destination route connection ---
            source_port = client_address[1]
            pid = self.app.get_pid_from_source_port(source_port)
            app_name = "Unknown"
            
            if pid:
                try:
                    p = psutil.Process(pid)
                    app_name = p.name().lower()
                    
                    dl_limit = self.app.get_applied_limit_for_app(app_name, "DL")
                    ul_limit = self.app.get_applied_limit_for_app(app_name, "UL")
                    
                    limits_desc = []
                    if dl_limit > 0:
                        limits_desc.append(f"DL Cap: {self.app.get_display_limit_desc(dl_limit)}")
                    if ul_limit > 0:
                        limits_desc.append(f"UL Cap: {self.app.get_display_limit_desc(ul_limit)}")
                        
                    caps_text = " | ".join(limits_desc) if limits_desc else "Unlimited"
                    self.app.log(f"SOCKS: App '{app_name}' (PID {pid}) connected to {addr}:{port} -> {caps_text}")
                except Exception:
                    pass
            else:
                self.app.log(f"SOCKS: Routed socket {source_port} to {addr}:{port}")
                
            # Registry stats map for double click connection popup
            conn_id = f"{client_address[0]}:{source_port}->{addr}:{port}"
            self.app.active_socks_connections[conn_id] = {
                "app": app_name,
                "pid": pid,
                "addr": f"{addr}:{port}",
                "port": source_port,
                "bytes_dl": 0,
                "bytes_ul": 0,
                "start_time": time.time()
            }
            
            # Spawn SOCKS processing pipes
            threading.Thread(target=self.pipe, args=(client_socket, dest_socket, "UL", app_name, conn_id), daemon=True).start()
            threading.Thread(target=self.pipe, args=(dest_socket, client_socket, "DL", app_name, conn_id), daemon=True).start()
            
        except Exception as e:
            try:
                client_socket.close()
            except:
                pass

    def pipe(self, sock_src, sock_dst, direction, app_name, conn_id):
        """Pipes raw data packets between socket descriptors with global rate limiting."""
        chunk_size = 16384
        
        while self.app.running:
            try:
                data = sock_src.recv(chunk_size)
                if not data:
                    break
                    
                sock_dst.sendall(data)
                
                # Metrics delta tracking
                if conn_id in self.app.active_socks_connections:
                    if direction == "DL":
                        self.app.active_socks_connections[conn_id]["bytes_dl"] += len(data)
                    else:
                        self.app.active_socks_connections[conn_id]["bytes_ul"] += len(data)
                
                # Fetch dynamically updated limits
                current_limit = self.app.get_applied_limit_for_app(app_name, direction)
                
                if current_limit > 0:
                    # Enforce directional aggregated Token Bucket throttling
                    regulator = self.app.regulators[(app_name, direction)]
                    regulator.throttle(len(data), current_limit)
            except Exception:
                break
                
        try:
            sock_src.close()
        except:
            pass
        try:
            sock_dst.close()
        except:
            pass
            
        # Clean stats on close
        if conn_id in self.app.active_socks_connections:
            try:
                del self.app.active_socks_connections[conn_id]
            except KeyError:
                pass


class PySideToggleSwitch(QAbstractButton):
    """Sleek native sliding toggle switch widget using hardware-accelerated QPropertyAnimations."""
    def __init__(self, parent=None, active_color=COLOR_GREEN, inactive_color=COLOR_FIELD):
        super().__init__(parent)
        self.setCheckable(True)
        self.setCursor(Qt.PointingHandCursor)
        self.active_color = QColor(active_color)
        self.inactive_color = QColor(inactive_color)
        self.circle_color = QColor(COLOR_TEXT)
        self._x_position = 4
        self.setFixedSize(54, 26)
        
    @Property(float)
    def x_position(self):
        return self._x_position
        
    @x_position.setter
    def x_position(self, pos):
        self._x_position = pos
        self.update()
        
    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        
        # Draw pill container
        bg = self.active_color if self.isChecked() else self.inactive_color
        painter.setPen(Qt.NoPen)
        painter.setBrush(QBrush(bg))
        painter.drawRoundedRect(0, 0, self.width(), self.height(), self.height() / 2, self.height() / 2)
        
        # Draw sliding knob circular disc
        knob_size = self.height() - 8
        painter.setBrush(QBrush(self.circle_color))
        painter.drawEllipse(self._x_position, 4, knob_size, knob_size)
        
    def nextCheckState(self):
        self.setChecked(not self.isChecked())
        # Slide knob with native Qt Easing Curve QPropertyAnimation
        target = self.width() - self.height() + 4 if self.isChecked() else 4
        self.anim = QPropertyAnimation(self, b"x_position")
        self.anim.setDuration(120)
        self.anim.setStartValue(self._x_position)
        self.anim.setEndValue(target)
        self.anim.start()


class PySideToastNotification(QWidget):
    """Non-blocking, elegant overlay sliding notification card that floats on the screen context."""
    def __init__(self, parent, title, message, color=COLOR_BLUE):
        super().__init__(parent, Qt.ToolTip | Qt.FramelessWindowHint | Qt.WindowDoesNotAcceptFocus)
        self.setAttribute(Qt.WA_ShowWithoutActivating, True)
        self.setStyleSheet(f"""
            QWidget {{
                background-color: {COLOR_CARD};
                border: 1px solid {color};
                border-radius: 6px;
            }}
            QLabel {{
                font-family: "Segoe UI", sans-serif;
                color: {COLOR_TEXT};
            }}
        """)
        
        layout = QHBoxLayout(self)
        layout.setContentsMargins(12, 10, 12, 10)
        
        # Indicator Sidebar
        indicator = QWidget()
        indicator.setFixedWidth(4)
        indicator.setStyleSheet(f"background-color: {color}; border: none;")
        layout.addWidget(indicator)
        
        # Body Labels Layout
        labels_layout = QVBoxLayout()
        labels_layout.setSpacing(1)
        
        title_lbl = QLabel(title.upper())
        title_lbl.setStyleSheet(f"font-weight: bold; color: {color}; font-size: 10px;")
        msg_lbl = QLabel(message)
        msg_lbl.setStyleSheet("font-size: 12px;")
        
        labels_layout.addWidget(title_lbl)
        labels_layout.addWidget(msg_lbl)
        layout.addLayout(labels_layout)
        
        self.adjustSize()
        
        # Locate parent window and float toast in bottom-right corner
        parent_rect = parent.geometry()
        x = parent_rect.x() + parent_rect.width() - self.width() - 25
        y = parent_rect.y() + parent_rect.height() - self.height() - 35
        self.move(x, y)
        
        self.show()
        
        # Close after 2.5 seconds
        QTimer.singleShot(2500, self.close)


def resource_path(relative_path):
    """ Get absolute path to resource, works for dev and for PyInstaller """
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)


class MainWindow(QMainWindow):
    metrics_updated = Signal()

    def __init__(self):
        super().__init__()
        self.metrics_updated.connect(self.update_process_tree)
        self.setWindowTitle("Darkstar NetShaper (PySide6 Edition)")
        
        # Load window icon
        icon_path = resource_path("netshaper_icon.png")
        if os.path.exists(icon_path):
            self.setWindowIcon(QIcon(icon_path))
            
        self.resize(1060, 720)
        self.setMinimumSize(800, 600)
        
        self.running = True
        self.latest_processes = {}
        self.active_socks_connections = {}
        
        # Fast thread-safe port-to-PID caches
        self.port_pid_cache = {}
        self.cache_lock = threading.Lock()
        threading.Thread(target=self.run_port_pid_cache_loop, daemon=True).start()

        # Throttling regulators registry map
        self.regulators = defaultdict(BandwidthRegulator)
        
        # Persistence rules configuration database setup inside AppData for system-wide launch persistence
        appdata_dir = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "Darkstar", "NetShaper")
        os.makedirs(appdata_dir, exist_ok=True)
        self.rules_file = os.path.join(appdata_dir, "shaper_rules.json")
        self.rules = {}
        self.load_rules()

        # Enforce administrative access control
        if not self.is_admin():
            self.try_elevate()
            sys.exit()

        atexit.register(self._cleanup_registry)
        self._cleanup_registry()

        # Find open proxy SOCKS port
        self.proxy_port = 1080
        while self.proxy_port < 1100:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            try:
                s.bind(("127.0.0.1", self.proxy_port))
                s.close()
                break
            except:
                self.proxy_port += 1

        # Render Main stylesheet design styling
        self.apply_qss_stylesheet()
        
        # Setup Core UI layouts
        self.init_ui()

        # Sync registry rules view list
        self.refresh_rules_tree()
        
        # Launch calculations daemon threads
        self.start_metrics_loop()

        # Initialize SOCKS proxy socket routing server
        self.proxy_server = SOCKS5ProxyServer("127.0.0.1", self.proxy_port, self)
        if self.proxy_server.start():
            self.log(f"SOCKS5 Proxy Throttling Engine active on 127.0.0.1:{self.proxy_port}")
        else:
            self.log("CRITICAL: SOCKS5 Proxy Throttling Engine failed to bind port!")
            self.status_socks_label.setText("SOCKS5 ENGINE ERROR")
            self.status_socks_label.setStyleSheet(f"color: {COLOR_DANGER}; font-weight: bold;")

    def closeEvent(self, event):
        """Clean close event handler."""
        self.running = False
        self._cleanup_registry()
        event.accept()

    def is_admin(self):
        try:
            return ctypes.windll.shell32.IsUserAnAdmin() != 0
        except AttributeError:
            return False

    def try_elevate(self):
        try:
            ctypes.windll.shell32.ShellExecuteW(
                None, "runas", sys.executable, " ".join([f'"{arg}"' for arg in sys.argv]), None, 1
            )
        except Exception:
            pass

    def _cleanup_registry(self):
        self.set_system_proxy(False)

    def load_rules(self):
        """Loads bandwidth policies from the local JSON database file, auto-upgrading legacy string formats."""
        if os.path.exists(self.rules_file):
            try:
                with open(self.rules_file, "r") as f:
                    loaded = json.load(f)
                    
                upgraded = {}
                for app, val in loaded.items():
                    if isinstance(val, dict):
                        upgraded[app] = {
                            "DL": val.get("DL", "Unlimited"),
                            "UL": val.get("UL", "Unlimited")
                        }
                    else:
                        upgraded[app] = {
                            "DL": str(val),
                            "UL": "Unlimited"
                        }
                self.rules = upgraded
            except Exception:
                self.rules = {}

    def save_rules(self):
        try:
            with open(self.rules_file, "w") as f:
                json.dump(self.rules, f, indent=4)
        except Exception as e:
            self.log(f"Failed to write persistence file: {e}")

    def show_toast(self, title, message, color=COLOR_BLUE):
        """Flashes a beautiful non-blocking floating slide notification toast."""
        PySideToastNotification(self, title, message, color)

    def log(self, message):
        """Adds a line entry to log activity output panel."""
        self.log_console.appendPlainText(f">> {message}")

    def apply_qss_stylesheet(self):
        """Curates beautiful modern dark stylesheets across entire PySide window."""
        self.setStyleSheet(f"""
            QMainWindow {{
                background-color: {COLOR_BG};
            }}
            QWidget {{
                font-family: "Segoe UI", sans-serif;
                color: {COLOR_TEXT};
            }}
            QFrame#cardFrame {{
                background-color: {COLOR_CARD};
                border: 1px solid #1D1D22;
                border-radius: 8px;
            }}
            QTreeView {{
                background-color: {COLOR_FIELD};
                border: none;
                alternate-background-color: #1E1E26;
                gridline-color: #111115;
                font-size: 13px;
                border-radius: 4px;
            }}
            QTreeView::item {{
                padding: 6px;
                border-bottom: 1px solid #111115;
            }}
            QTreeView::item:selected {{
                background-color: {COLOR_BLUE};
                color: #FFFFFF;
            }}
            QHeaderView::section {{
                background-color: {COLOR_CARD};
                color: {COLOR_MUTED};
                padding: 6px;
                font-weight: bold;
                border: none;
                font-size: 11px;
            }}
            QLineEdit {{
                background-color: {COLOR_FIELD};
                border: 1px solid #22222A;
                border-radius: 4px;
                padding: 6px;
                color: {COLOR_TEXT};
                font-size: 13px;
            }}
            QLineEdit:focus {{
                border: 1px solid {COLOR_CYAN};
            }}
            QComboBox {{
                background-color: {COLOR_FIELD};
                border: 1px solid #22222A;
                border-radius: 4px;
                padding: 4px;
                color: {COLOR_TEXT};
                font-size: 13px;
            }}
            QComboBox::drop-down {{
                border: none;
            }}
            QCheckBox {{
                font-size: 13px;
                spacing: 6px;
            }}
        """)

    def init_ui(self):
        """Builds all functional layout widgets inside main window."""
        main_widget = QWidget()
        self.setCentralWidget(main_widget)
        
        main_layout = QVBoxLayout(main_widget)
        main_layout.setContentsMargins(15, 15, 15, 15)
        main_layout.setSpacing(12)

        # 1. Title Banner Frame
        title_layout = QHBoxLayout()
        
        title_label = QLabel("⚡ DARKSTAR NETSHAPER")
        title_font = QFont("Segoe UI Semibold", 16)
        title_font.setBold(True)
        title_label.setFont(title_font)
        title_label.setStyleSheet(f"color: {COLOR_CYAN};")
        title_layout.addWidget(title_label)
        
        self.status_socks_label = QLabel(f"SOCKS5 ENGINE ACTIVE: PORT {self.proxy_port}")
        self.status_socks_label.setStyleSheet(f"background-color: {COLOR_FIELD}; color: {COLOR_GREEN}; font-weight: bold; font-size: 11px; border-radius: 4px; padding: 4px 8px;")
        title_layout.addWidget(self.status_socks_label, 0, Qt.AlignRight)
        
        main_layout.addLayout(title_layout)
        
        subtitle = QLabel("Premium, Hardware-Accelerated PySide6 Bandwidth Regulating Suite")
        subtitle.setStyleSheet(f"color: {COLOR_MUTED}; font-size: 12px; margin-top: 4px; margin-bottom: 6px;")
        main_layout.addWidget(subtitle)

        # 2. Interception Global Switch Card
        self.proxy_bar = QFrame()
        self.proxy_bar.setObjectName("cardFrame")
        proxy_layout = QHBoxLayout(self.proxy_bar)
        proxy_layout.setContentsMargins(12, 10, 12, 10)
        
        self.glow_dot = QLabel("●")
        self.glow_dot.setStyleSheet(f"color: {COLOR_MUTED}; font-size: 18px;")
        proxy_layout.addWidget(self.glow_dot)
        
        proxy_lbl = QLabel("SYSTEM-WIDE TRAFFIC INTERCEPTION")
        proxy_lbl_font = QFont("Segoe UI", 10)
        proxy_lbl_font.setBold(True)
        proxy_lbl.setFont(proxy_lbl_font)
        proxy_layout.addWidget(proxy_lbl)
        
        desc_lbl = QLabel("(Reroutes network sockets through SOCKS proxy)")
        desc_lbl.setStyleSheet(f"color: {COLOR_MUTED}; font-size: 12px;")
        proxy_layout.addWidget(desc_lbl)
        proxy_layout.addStretch()
        
        self.intercept_state_lbl = QLabel("INACTIVE")
        self.intercept_state_lbl.setStyleSheet(f"color: {COLOR_MUTED}; font-weight: bold; font-size: 11px;")
        proxy_layout.addWidget(self.intercept_state_lbl)
        
        # Add Custom Slider Toggle Switch
        self.toggle_switch = PySideToggleSwitch()
        self.toggle_switch.clicked.connect(self.handle_proxy_toggle)
        proxy_layout.addWidget(self.toggle_switch)
        
        main_layout.addWidget(self.proxy_bar)

        # 3. Horizontal Splitter (Process List Left, Editor Panel Right)
        splitter = QSplitter(Qt.Horizontal)
        
        # Left widget: Process Monitor
        left_widget = QFrame()
        left_widget.setObjectName("cardFrame")
        left_layout = QVBoxLayout(left_widget)
        left_layout.setContentsMargins(12, 12, 12, 12)
        left_layout.setSpacing(8)
        
        left_header = QHBoxLayout()
        search_lbl = QLabel("🔍 SEARCH APP:")
        search_lbl.setStyleSheet(f"color: {COLOR_CYAN}; font-weight: bold; font-size: 11px;")
        left_header.addWidget(search_lbl)
        
        self.search_entry = QLineEdit()
        self.search_entry.setPlaceholderText("Filter running executables...")
        self.search_entry.textChanged.connect(self.update_process_tree)
        left_header.addWidget(self.search_entry)
        
        self.active_cb = QCheckBox("Active network only")
        self.active_cb.setChecked(True)
        self.active_cb.stateChanged.connect(self.update_process_tree)
        left_header.addWidget(self.active_cb)
        
        left_layout.addLayout(left_header)
        
        # Table Treeview for apps
        self.process_tree = QTreeWidget()
        self.process_tree.setAlternatingRowColors(True)
        self.process_tree.setSelectionBehavior(QTreeWidget.SelectRows)
        self.process_tree.setEditTriggers(QTreeWidget.NoEditTriggers)
        self.process_tree.setHeaderHidden(False)
        self.process_tree.doubleClicked.connect(self.handle_process_double_click)
        self.process_tree.clicked.connect(self.handle_process_selected)
        
        # Bind headers
        headers = ["Instances", "Application", "Active Sockets", "Speed Rate", "DL Limit", "UL Limit", "Executable Path"]
        self.process_tree_model = self.process_tree.header()
        
        # Set columns directly on standard Tree widget items
        self.process_tree.setHeaderLabels(headers)
        self.process_tree.setColumnWidth(0, 70)
        self.process_tree.setColumnWidth(1, 140)
        self.process_tree.setColumnWidth(2, 110)
        self.process_tree.setColumnWidth(3, 110)
        self.process_tree.setColumnWidth(4, 90)
        self.process_tree.setColumnWidth(5, 90)
        
        left_layout.addWidget(self.process_tree)
        
        self.status_procs_lbl = QLabel("Scanning operating system processes...")
        self.status_procs_lbl.setStyleSheet(f"color: {COLOR_MUTED}; font-size: 11px;")
        left_layout.addWidget(self.status_procs_lbl)
        
        splitter.addWidget(left_widget)

        # Right Widget: Policy controls bar & current rules tree list
        right_widget = QWidget()
        right_layout = QVBoxLayout(right_widget)
        right_layout.setContentsMargins(0, 0, 0, 0)
        right_layout.setSpacing(10)
        
        # Policy Form Card
        editor_card = QFrame()
        editor_card.setObjectName("cardFrame")
        editor_layout = QVBoxLayout(editor_card)
        editor_layout.setContentsMargins(12, 12, 12, 12)
        editor_layout.setSpacing(8)
        
        editor_title = QLabel("⚡ APP BANDWIDTH THROTTLER")
        editor_title.setStyleSheet(f"color: {COLOR_CYAN}; font-weight: bold; font-size: 12px;")
        editor_layout.addWidget(editor_title)
        
        # Target Exe Row
        exe_lbl = QLabel("Target Executable File / Path:")
        exe_lbl.setStyleSheet("font-weight: bold; font-size: 11px;")
        editor_layout.addWidget(exe_lbl)
        
        exe_row = QHBoxLayout()
        self.exe_entry = QLineEdit()
        exe_row.addWidget(self.exe_entry)
        
        browse_btn = QPushButton("📁 Browse")
        browse_btn.setStyleSheet(f"background-color: {COLOR_FIELD}; font-weight: bold; border-radius: 4px; padding: 6px;")
        browse_btn.clicked.connect(self.handle_browse_exe)
        exe_row.addWidget(browse_btn)
        
        launch_btn = QPushButton("🚀 Run Throttled")
        launch_btn.setStyleSheet(f"background-color: {COLOR_BLUE}; font-weight: bold; border-radius: 4px; padding: 6px;")
        launch_btn.clicked.connect(self.handle_launch_throttled)
        exe_row.addWidget(launch_btn)
        
        editor_layout.addLayout(exe_row)
        
        # Download limit forms
        self.dl_cb = QCheckBox("Limit Download Speed (DL)")
        self.dl_cb.setStyleSheet(f"color: {COLOR_GREEN}; font-weight: bold;")
        self.dl_cb.setChecked(True)
        self.dl_cb.stateChanged.connect(self.sync_input_states)
        editor_layout.addWidget(self.dl_cb)
        
        dl_row = QHBoxLayout()
        self.dl_val_entry = QLineEdit("5")
        self.dl_val_entry.setFixedWidth(100)
        dl_row.addWidget(self.dl_val_entry)
        
        self.dl_unit_combo = QComboBox()
        self.dl_unit_combo.addItems(["MB/s", "KB/s", "Mbps", "Kbps"])
        dl_row.addWidget(self.dl_unit_combo)
        dl_row.addStretch()
        editor_layout.addLayout(dl_row)
        
        # Upload limit forms
        self.ul_cb = QCheckBox("Limit Upload Speed (UL)")
        self.ul_cb.setStyleSheet(f"color: {COLOR_ORANGE}; font-weight: bold;")
        self.ul_cb.setChecked(False)
        self.ul_cb.stateChanged.connect(self.sync_input_states)
        editor_layout.addWidget(self.ul_cb)
        
        ul_row = QHBoxLayout()
        self.ul_val_entry = QLineEdit("500")
        self.ul_val_entry.setFixedWidth(100)
        ul_row.addWidget(self.ul_val_entry)
        
        self.ul_unit_combo = QComboBox()
        self.ul_unit_combo.addItems(["MB/s", "KB/s", "Mbps", "Kbps"])
        self.ul_unit_combo.setCurrentIndex(1)
        ul_row.addWidget(self.ul_unit_combo)
        ul_row.addStretch()
        editor_layout.addLayout(ul_row)
        
        # Form submission buttons
        submit_row = QHBoxLayout()
        self.apply_btn = QPushButton("⚡ Apply Policies")
        self.apply_btn.setStyleSheet(f"background-color: {COLOR_FIELD}; font-weight: bold; border-radius: 4px; padding: 8px;")
        self.apply_btn.clicked.connect(self.handle_apply_policy)
        submit_row.addWidget(self.apply_btn)
        
        self.remove_btn = QPushButton("❌ Delete Rule")
        self.remove_btn.setStyleSheet(f"background-color: {COLOR_FIELD}; font-weight: bold; border-radius: 4px; padding: 8px;")
        self.remove_btn.clicked.connect(self.handle_delete_policy)
        submit_row.addWidget(self.remove_btn)
        
        editor_layout.addLayout(submit_row)
        right_layout.addWidget(editor_card)
        
        self.sync_input_states()

        # Database rules list tree view
        rules_card = QFrame()
        rules_card.setObjectName("cardFrame")
        rules_layout = QVBoxLayout(rules_card)
        rules_layout.setContentsMargins(12, 12, 12, 12)
        rules_layout.setSpacing(6)
        
        rules_lbl = QLabel("📂 ACTIVE RULES REGISTRY DATABASE:")
        rules_lbl.setStyleSheet(f"color: {COLOR_MUTED}; font-weight: bold; font-size: 11px;")
        rules_layout.addWidget(rules_lbl)
        
        self.rules_tree = QTreeWidget()
        self.rules_tree.setAlternatingRowColors(True)
        self.rules_tree.setSelectionBehavior(QTreeWidget.SelectRows)
        self.rules_tree.setEditTriggers(QTreeWidget.NoEditTriggers)
        self.rules_tree.doubleClicked.connect(self.handle_rules_double_click)
        self.rules_tree.setHeaderLabels(["Application Executable", "DL Cap Limit", "UL Cap Limit"])
        
        rules_layout.addWidget(self.rules_tree)
        right_layout.addWidget(rules_card)
        
        splitter.addWidget(right_widget)
        
        # Adjust Splitter Sizes (60% left, 40% right)
        splitter.setSizes([620, 380])
        main_layout.addWidget(splitter)

        # 4. Activity Logs Text Box Frame
        log_box = QVBoxLayout()
        log_box.setSpacing(2)
        
        log_lbl = QLabel("SYSTEM CONSOLE LOG ACTIVITY OUTPUT:")
        log_lbl.setStyleSheet(f"color: {COLOR_MUTED}; font-weight: bold; font-size: 10px;")
        log_box.addWidget(log_lbl)
        
        self.log_console = QPlainTextEdit()
        self.log_console.setReadOnly(True)
        self.log_console.setFixedHeight(100)
        self.log_console.setStyleSheet(f"background-color: {COLOR_CARD}; border: none; font-family: 'Consolas', monospace; font-size: 11px; padding: 4px; color: {COLOR_MUTED};")
        log_box.addWidget(self.log_console)
        
        main_layout.addLayout(log_box)

        # 5. Native Qt status bar
        self.statusBar().setStyleSheet(f"background-color: {COLOR_CARD}; border: none;")
        self.status_lbl = QLabel("🟢 Engine Ready. System operational.")
        self.status_lbl.setStyleSheet(f"color: {COLOR_GREEN}; font-size: 11px; padding-left: 10px;")
        self.statusBar().addWidget(self.status_lbl)

        # Set interactive pointer cursors for all buttons, checkboxes, and comboboxes for premium UX
        for btn in self.findChildren(QPushButton):
            btn.setCursor(Qt.PointingHandCursor)
        for cb in self.findChildren(QCheckBox):
            cb.setCursor(Qt.PointingHandCursor)
        for combo in self.findChildren(QComboBox):
            combo.setCursor(Qt.PointingHandCursor)

    def sync_input_states(self):
        """Enables/disables UI input entries based on checkbox states."""
        self.dl_val_entry.setEnabled(self.dl_cb.isChecked())
        self.dl_unit_combo.setEnabled(self.dl_cb.isChecked())
        
        self.ul_val_entry.setEnabled(self.ul_cb.isChecked())
        self.ul_unit_combo.setEnabled(self.ul_cb.isChecked())

    def update_status(self, message, working=False, error=False):
        """Safely updates indicators on bottom status bar."""
        if error:
            self.status_lbl.setText(f"🔴 ERROR: {message}")
            self.status_lbl.setStyleSheet(f"color: {COLOR_DANGER}; font-size: 11px; padding-left: 10px;")
        elif working:
            self.status_lbl.setText(f"⚡ WORKING: {message}")
            self.status_lbl.setStyleSheet(f"color: {COLOR_CYAN}; font-size: 11px; padding-left: 10px;")
        else:
            self.status_lbl.setText(f"🟢 {message}")
            self.status_lbl.setStyleSheet(f"color: {COLOR_GREEN}; font-size: 11px; padding-left: 10px;")

    def handle_proxy_toggle(self, checked):
        """Controls system SOCKS proxy editing inside Windows Registry."""
        if checked:
            success = self.set_system_proxy(True, self.proxy_port)
            if success:
                self.log(f"GLOBAL INTERCEPTOR: Rerouted Windows traffic through local proxy port {self.proxy_port}.")
                self.update_status("Traffic Interceptor active.", working=True)
                self.glow_dot.setStyleSheet(f"color: {COLOR_GREEN}; font-size: 18px;")
                self.intercept_state_lbl.setText("ACTIVE")
                self.intercept_state_lbl.setStyleSheet(f"color: {COLOR_GREEN}; font-weight: bold; font-size: 11px;")
                self.show_toast("Proxy Enabled", "Windows network is now routed through Shaper Proxy!", COLOR_GREEN)
            else:
                self.toggle_switch.setChecked(False)
                self.glow_dot.setStyleSheet(f"color: {COLOR_MUTED}; font-size: 18px;")
                self.intercept_state_lbl.setText("INACTIVE")
                self.show_toast("Registry Error", "Failed to modify proxy keys.", COLOR_DANGER)
        else:
            self.set_system_proxy(False)
            self.log("GLOBAL INTERCEPTOR: Interception disabled. Direct connection routes restored.")
            self.update_status("Proxy disabled.")
            self.glow_dot.setStyleSheet(f"color: {COLOR_MUTED}; font-size: 18px;")
            self.intercept_state_lbl.setText("INACTIVE")
            self.intercept_state_lbl.setStyleSheet(f"color: {COLOR_MUTED}; font-weight: bold; font-size: 11px;")
            self.show_toast("Proxy Disabled", "Direct route connection restored.", COLOR_MUTED)

    def set_system_proxy(self, enabled, port=1080):
        try:
            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
                0,
                winreg.KEY_WRITE
            )
            if enabled:
                winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 1)
                winreg.SetValueEx(key, "ProxyServer", 0, winreg.REG_SZ, f"socks=127.0.0.1:{port}")
                winreg.SetValueEx(key, "ProxyOverride", 0, winreg.REG_SZ, "<local>")
            else:
                winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 0)
            winreg.CloseKey(key)
            
            # Flush settings globally
            ctypes.windll.wininet.InternetSetOptionW(0, 39, 0, 0)
            ctypes.windll.wininet.InternetSetOptionW(0, 37, 0, 0)
            return True
        except Exception as e:
            self.log(f"Failed to edit system proxy registry keys: {e}")
            return False

    def handle_browse_exe(self):
        file_path, _ = QFileDialog.getOpenFileName(
            self,
            "Select target executable application",
            "",
            "Executable Files (*.exe);;All Files (*.*)"
        )
        if file_path:
            self.exe_entry.setText(file_path)

    def handle_launch_throttled(self):
        file_path = self.exe_entry.text().strip()
        if not file_path or not os.path.exists(file_path):
            file_path, _ = QFileDialog.getOpenFileName(
                self, "Select application to launch", "", "Executable Files (*.exe);;All Files (*.*)"
            )
            if not file_path:
                return
            self.exe_entry.setText(file_path)

        base_name = os.path.basename(file_path).lower()
        if "chrome" in base_name or "edge" in base_name or "brave" in base_name or "opera" in base_name:
            cmd = f'"{file_path}" --proxy-server="socks5://127.0.0.1:{self.proxy_port}" --user-data-dir="{os.environ["TEMP"]}\\NetShaper_{base_name.split(".")[0]}"'
        else:
            cmd = f'"{file_path}"'
            
        env = os.environ.copy()
        env["HTTP_PROXY"] = f"socks5://127.0.0.1:{self.proxy_port}"
        env["HTTPS_PROXY"] = f"socks5://127.0.0.1:{self.proxy_port}"
        env["ALL_PROXY"] = f"socks5://127.0.0.1:{self.proxy_port}"
        
        try:
            subprocess.Popen(cmd, shell=True, env=env)
            self.log(f"Launched application process: {base_name} routed through local SOCKS proxy.")
            self.show_toast("App Route Active", f"Launched {base_name} through shaper port successfully!", COLOR_BLUE)
        except Exception as e:
            self.show_toast("Launch Error", f"Failed to execute app: {e}", COLOR_DANGER)

    def run_port_pid_cache_loop(self):
        while self.running:
            temp_cache = {}
            try:
                for conn in psutil.net_connections(kind='tcp'):
                    if conn.laddr and conn.pid:
                        temp_cache[conn.laddr.port] = conn.pid
            except Exception:
                pass
                
            with self.cache_lock:
                self.port_pid_cache = temp_cache
            time.sleep(0.3)

    def get_pid_from_source_port(self, source_port):
        with self.cache_lock:
            pid = self.port_pid_cache.get(source_port, None)
            if pid:
                return pid
        time.sleep(0.05)
        with self.cache_lock:
            pid = self.port_pid_cache.get(source_port, None)
            if pid:
                return pid
        try:
            for conn in psutil.net_connections(kind='tcp'):
                if conn.laddr and conn.laddr.port == source_port:
                    return conn.pid
        except:
            pass
        return None

    def get_applied_limit_for_app(self, app_name, direction):
        app_lower = app_name.lower().replace(".exe", "")
        
        for target, rules_data in self.rules.items():
            target_pattern = os.path.basename(target).lower().replace(".exe", "")
            
            if target_pattern and (target_pattern in app_lower or app_lower in target_pattern):
                if isinstance(rules_data, dict):
                    limit_str = rules_data.get(direction, "Unlimited")
                else:
                    limit_str = str(rules_data) if direction == "DL" else "Unlimited"
                    
                if "Unlimited" in limit_str:
                    return 0
                
                try:
                    if " MB/s" in limit_str:
                        return int(float(limit_str.replace(" MB/s", "")) * 1024 * 1024)
                    elif " KB/s" in limit_str:
                        return int(float(limit_str.replace(" KB/s", "")) * 1024)
                    elif " Mbps" in limit_str:
                        return int(float(limit_str.replace(" Mbps", "")) * 1000000 / 8)
                    elif " Kbps" in limit_str:
                        return int(float(limit_str.replace(" Kbps", "")) * 1000 / 8)
                except:
                    pass
        return 0

    def get_display_limit_desc(self, limit_bytes):
        if limit_bytes >= 1024 * 1024:
            return f"{limit_bytes / (1024 * 1024):.1f} MB/s"
        elif limit_bytes >= 1024:
            return f"{limit_bytes / 1024:.1f} KB/s"
        else:
            return f"{limit_bytes} B/s"

    def start_metrics_loop(self):
        threading.Thread(target=self.run_metrics_loop, daemon=True).start()

    def run_metrics_loop(self):
        """Polls processes system activity loops."""
        while self.running:
            io1 = {}
            conns_map = defaultdict(int)
            
            try:
                for conn in psutil.net_connections(kind='inet'):
                    if conn.pid is not None:
                        conns_map[conn.pid] += 1
            except:
                pass

            try:
                for proc in psutil.process_iter():
                    try:
                        pid = proc.pid
                        io = proc.io_counters()
                        if io:
                            io1[pid] = io.read_bytes + io.write_bytes
                    except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
                        continue
                    except:
                        continue
            except:
                pass
                    
            time.sleep(1.2)
            
            io2 = {}
            grouped_procs = {}
            
            try:
                for proc in psutil.process_iter():
                    try:
                        pid = proc.pid
                        name = proc.name()
                        exe = proc.exe() or ""
                        io = proc.io_counters()
                        
                        if not name:
                            continue
                        if not name.lower().endswith('.exe'):
                            name += '.exe'
                            
                        conns = conns_map.get(pid, 0)
                        
                        speed = 0
                        if io:
                            total_bytes = io.read_bytes + io.write_bytes
                            io2[pid] = total_bytes
                            if pid in io1:
                                speed = max(0, (total_bytes - io1[pid]) / 1.2)
                         
                        key = name.lower()
                        if key in grouped_procs:
                            grouped_procs[key]['conns'] += conns
                            grouped_procs[key]['speed'] += speed
                            grouped_procs[key]['instances'] += 1
                            if not grouped_procs[key]['path'] and exe:
                                grouped_procs[key]['path'] = exe
                        else:
                            grouped_procs[key] = {
                                'name': name,
                                'path': exe,
                                'conns': conns,
                                'speed': speed,
                                'instances': 1
                            }
                    except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
                        continue
                    except:
                        continue
            except:
                pass
                    
            self.latest_processes = grouped_procs
            
            # Push safe signal update back to UI thread
            if self.running:
                self.metrics_updated.emit()

    @Slot()
    def update_process_tree(self):
        """Updates main application lists inside treeview safely without clearing selections."""
        search_term = self.search_entry.text().lower().strip()
        active_only = self.active_cb.isChecked()
        
        # Track selected
        selected_app = None
        selected_indexes = self.process_tree.selectedIndexes()
        if selected_indexes:
            selected_app = selected_indexes[0].sibling(selected_indexes[0].row(), 1).data()
            if selected_app:
                selected_app = selected_app.lower()

        applied_limits = {}
        for target, rules_data in self.rules.items():
            applied_limits[os.path.basename(target).lower()] = rules_data

        procs = []
        active_count = 0

        for key, proc_info in self.latest_processes.items():
            name = proc_info['name']
            path = proc_info['path']
            conns = proc_info['conns']
            speed = proc_info['speed']
            instances = proc_info['instances']
            
            dl_limit_val = "Unlimited"
            ul_limit_val = "Unlimited"
            for app_pattern, rules_data in applied_limits.items():
                if app_pattern in name.lower() or name.lower() in app_pattern:
                    dl_limit_val = rules_data.get("DL", "Unlimited")
                    ul_limit_val = rules_data.get("UL", "Unlimited")
                    break

            if search_term and search_term not in name.lower() and search_term not in path.lower():
                continue
                
            is_active = (conns > 0 or speed > 0 or dl_limit_val != "Unlimited" or ul_limit_val != "Unlimited")
            if is_active:
                active_count += 1
            if active_only and not is_active:
                continue
                
            procs.append((key, name, conns, speed, dl_limit_val, ul_limit_val, path, instances))

        procs.sort(key=lambda x: (x[4] != "Unlimited" or x[5] != "Unlimited", x[3], x[2], x[1].lower()), reverse=True)

        self.process_tree.clear()
        # Repopulate
        for key, name, conns, speed, dl_limit_val, ul_limit_val, path, instances in procs:
            if speed >= 1024 * 1024:
                speed_str = f"{speed / (1024*1024):.1f} MB/s"
            elif speed >= 1024:
                speed_str = f"{speed / 1024:.1f} KB/s"
            elif speed > 0:
                speed_str = f"{speed:.0f} B/s"
            else:
                speed_str = "0 B/s"
                
            conn_str = f"{conns} Active" if conns > 0 else "Idle"
            inst_str = f"{instances}x" if instances > 1 else "1"
            
            item = QTreeWidgetItem(self.process_tree)
            item.setText(0, inst_str)
            item.setText(1, name)
            item.setText(2, conn_str)
            item.setText(3, speed_str)
            item.setText(4, dl_limit_val)
            item.setText(5, ul_limit_val)
            item.setText(6, path)
            
            # Align items
            item.setTextAlignment(0, Qt.AlignCenter)
            item.setTextAlignment(2, Qt.AlignCenter)
            item.setTextAlignment(3, Qt.AlignRight)
            item.setTextAlignment(4, Qt.AlignCenter)
            item.setTextAlignment(5, Qt.AlignCenter)
            
            if selected_app and name.lower() == selected_app:
                self.process_tree.setCurrentItem(item)

        self.status_procs_lbl.setText(f"Scanning metrics... {len(self.latest_processes)} running | {active_count} active network sockets online.")

    def handle_process_selected(self, index):
        """Triggered when process Tree row is clicked once."""
        item = self.process_tree.currentItem()
        if item:
            name = item.text(1)
            path = item.text(6)
            self.exe_entry.setText(path if path else name)

    def handle_process_double_click(self, index):
        """Double clicking process tree row opens popup inspector."""
        item = self.process_tree.currentItem()
        if item:
            app_name = item.text(1)
            # Spawn detailed active SOCKS connection routes details inspector
            self.inspector = SocketsInspectorWindow(self, app_name)
            self.inspector.show()

    def refresh_rules_tree(self):
        """Updates policies database list model."""
        self.rules_tree.clear()
        for app, rules_data in self.rules.items():
            dl_limit = rules_data.get("DL", "Unlimited")
            ul_limit = rules_data.get("UL", "Unlimited")
            
            item = QTreeWidgetItem(self.rules_tree)
            item.setText(0, os.path.basename(app))
            item.setText(1, dl_limit)
            item.setText(2, ul_limit)
            
            item.setTextAlignment(1, Qt.AlignRight)
            item.setTextAlignment(2, Qt.AlignRight)
        self.update_process_tree()

    def handle_rules_double_click(self, index):
        """Auto fills edit forms on rules double click."""
        item = self.rules_tree.currentItem()
        if not item:
            return
            
        app_name = item.text(0)
        dl_limit = item.text(1)
        ul_limit = item.text(2)
        
        self.exe_entry.setText(app_name)
        
        # Sync Download Limits Config
        if dl_limit != "Unlimited":
            self.dl_cb.setChecked(True)
            for unit_str, idx in [(" MB/s", 0), (" KB/s", 1), (" Mbps", 2), (" Kbps", 3)]:
                if unit_str in dl_limit:
                    try:
                        self.dl_val_entry.setText(dl_limit.split(unit_str)[0])
                        self.dl_unit_combo.setCurrentIndex(idx)
                    except:
                        pass
                    break
        else:
            self.dl_cb.setChecked(False)
            
        # Sync Upload Limits Config
        if ul_limit != "Unlimited":
            self.ul_cb.setChecked(True)
            for unit_str, idx in [(" MB/s", 0), (" KB/s", 1), (" Mbps", 2), (" Kbps", 3)]:
                if unit_str in ul_limit:
                    try:
                        self.ul_val_entry.setText(ul_limit.split(unit_str)[0])
                        self.ul_unit_combo.setCurrentIndex(idx)
                    except:
                        pass
                    break
        else:
            self.ul_cb.setChecked(False)
            
        self.sync_input_states()
        self.show_toast("Policy Loaded", "Loaded selected bandwidth values into throttler editor.", COLOR_BLUE)

    def handle_apply_policy(self):
        exe = self.exe_entry.text().strip()
        if not exe:
            self.show_toast("Incomplete Form", "Application executable name or path required.", COLOR_DANGER)
            return

        if not exe.endswith(".exe") and not os.path.exists(exe):
            exe += ".exe"

        dl_cap = "Unlimited"
        ul_cap = "Unlimited"
        
        # Parse Download Caps
        if self.dl_cb.isChecked():
            speed_dl = self.dl_val_entry.text().strip()
            try:
                if float(speed_dl) <= 0: raise ValueError()
            except ValueError:
                self.show_toast("Invalid Value", "Download limit rate must be positive.", COLOR_DANGER)
                return
            dl_cap = f"{speed_dl} {self.dl_unit_combo.currentText()}"
            
        # Parse Upload Caps
        if self.ul_cb.isChecked():
            speed_ul = self.ul_val_entry.text().strip()
            try:
                if float(speed_ul) <= 0: raise ValueError()
            except ValueError:
                self.show_toast("Invalid Value", "Upload limit rate must be positive.", COLOR_DANGER)
                return
            ul_cap = f"{speed_ul} {self.ul_unit_combo.currentText()}"

        if dl_cap == "Unlimited" and ul_cap == "Unlimited":
            self.show_toast("Select Limit Option", "Please check and enable at least one speed capping option.", COLOR_DANGER)
            return

        # Store rules
        self.rules[exe.lower()] = {
            "DL": dl_cap,
            "UL": ul_cap
        }
        self.save_rules()
        
        self.log(f"POLICY APPLIED: Capped '{os.path.basename(exe)}' (DL: {dl_cap} | UL: {ul_cap}) globally (Token Bucket)")
        self.update_status(f"Policies updated for {os.path.basename(exe)}.", working=False)
        self.refresh_rules_tree()
        
        self.show_toast("Rules Database Updated", f"Applied limits for {os.path.basename(exe)} successfully!", COLOR_GREEN)

    def handle_delete_policy(self):
        item = self.rules_tree.currentItem()
        if not item:
            self.show_toast("Highlight Rule Row", "Double-click or highlight an active rule entry below to delete.", COLOR_ORANGE)
            return

        app_name = item.text(0).lower()
        keys_to_delete = [k for k in self.rules.keys() if os.path.basename(k) == app_name]
        
        for key in keys_to_delete:
            del self.rules[key]
            
        self.save_rules()
        self.log(f"POLICY REMOVED: Bandwidth rules for app '{app_name}' purged.")
        self.update_status("Limits deleted.")
        self.refresh_rules_tree()
        
        self.show_toast("Policy Deleted", f"Rules registry wiped for {app_name}.", COLOR_DANGER)


class SocketsInspectorWindow(QMainWindow):
    """Slide-out Sockets Inspector popup window drawing active live connections routing inside proxy."""
    table_updated = Signal(list)

    def __init__(self, parent, app_name):
        super().__init__(parent)
        self.table_updated.connect(self.update_table)
        self.app = parent
        self.app_name = app_name.lower()
        self.running = True
        
        self.setWindowTitle(f"Network Sockets Inspector: {os.path.basename(self.app_name)}")
        
        # Load window icon
        icon_path = resource_path("netshaper_icon.png")
        if os.path.exists(icon_path):
            self.setWindowIcon(QIcon(icon_path))
            
        self.resize(700, 440)
        self.setMinimumSize(500, 300)
        
        self.apply_qss_stylesheet()
        self.init_ui()
        
        # Start connection updates calculations
        threading.Thread(target=self.run_inspector_refresh_loop, daemon=True).start()
        
    def closeEvent(self, event):
        self.running = False
        event.accept()
        
    def apply_qss_stylesheet(self):
        self.setStyleSheet(f"""
            QMainWindow {{
                background-color: {COLOR_BG};
            }}
            QWidget {{
                font-family: "Segoe UI", sans-serif;
                color: {COLOR_TEXT};
            }}
            QFrame#cardFrame {{
                background-color: {COLOR_CARD};
                border: 1px solid #1D1D22;
                border-radius: 8px;
            }}
            QTreeView {{
                background-color: {COLOR_FIELD};
                border: none;
                alternate-background-color: #1E1E26;
                gridline-color: #111115;
                font-size: 13px;
                border-radius: 4px;
            }}
            QTreeView::item {{
                padding: 6px;
                border-bottom: 1px solid #111115;
            }}
            QHeaderView::section {{
                background-color: {COLOR_CARD};
                color: {COLOR_MUTED};
                padding: 6px;
                font-weight: bold;
                border: none;
                font-size: 11px;
            }}
        """)
        
    def init_ui(self):
        container = QWidget()
        self.setCentralWidget(container)
        
        layout = QVBoxLayout(container)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(8)
        
        header = QFrame()
        header.setObjectName("cardFrame")
        header_layout = QHBoxLayout(header)
        header_layout.setContentsMargins(12, 10, 12, 10)
        
        title_lbl = QLabel(f"📂 ACTIVE NET-ROUTES: {os.path.basename(self.app_name).upper()}")
        font = QFont("Segoe UI", 10)
        font.setBold(True)
        title_lbl.setFont(font)
        title_lbl.setStyleSheet(f"color: {COLOR_CYAN};")
        header_layout.addWidget(title_lbl)
        
        layout.addWidget(header)
        
        self.tree = QTreeWidget()
        self.tree.setAlternatingRowColors(True)
        self.tree.setSelectionBehavior(QTreeWidget.SelectRows)
        self.tree.setEditTriggers(QTreeWidget.NoEditTriggers)
        
        headers = ["Network Route Source", "Target Route Address", "Local Port", "Live DL Speed", "Live UL Speed", "Active Duration"]
        self.tree.setHeaderLabels(headers)
        self.tree.setColumnWidth(0, 150)
        self.tree.setColumnWidth(1, 160)
        self.tree.setColumnWidth(2, 70)
        
        layout.addWidget(self.tree)
        
    def run_inspector_refresh_loop(self):
        """Background loop querying rates."""
        while self.running and self.app.running:
            # 1. Resolve all PIDs and their connections directly via process_iter() and proc.connections()
            sys_conns = []
            try:
                for proc in psutil.process_iter():
                    try:
                        if proc.name().lower() == self.app_name:
                            for conn in proc.connections(kind='inet'):
                                sys_conns.append(conn)
                    except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess):
                        continue
                    except:
                        continue
            except:
                pass

            # 2. Capture first sample of proxy bytes
            proxy_sample1 = {}
            for conn_id, stats in list(self.app.active_socks_connections.items()):
                proxy_sample1[conn_id] = {
                    "bytes_dl": stats["bytes_dl"],
                    "bytes_ul": stats["bytes_ul"],
                    "start_time": stats["start_time"],
                    "addr": stats["addr"],
                    "port": stats["port"]
                }
                
            time.sleep(1.0)
            
            # 3. Capture second sample of proxy bytes and compute rates
            proxy_sample2 = {}
            for conn_id, stats in list(self.app.active_socks_connections.items()):
                proxy_sample2[conn_id] = {
                    "bytes_dl": stats["bytes_dl"],
                    "bytes_ul": stats["bytes_ul"],
                    "start_time": stats["start_time"],
                    "addr": stats["addr"],
                    "port": stats["port"]
                }
                
            # 4. Build presentation rows
            procs_data = []
            
            # Map proxy connections by local client port for fast lookup
            matched_ports = set()
            
            for conn_id, stats2 in proxy_sample2.items():
                local_port = stats2["port"]
                matched_ports.add(local_port)
                
                # Calculate proxy speed
                stats1 = proxy_sample1.get(conn_id, None)
                if stats1:
                    speed_dl = stats2["bytes_dl"] - stats1["bytes_dl"]
                    speed_ul = stats2["bytes_ul"] - stats1["bytes_ul"]
                else:
                    speed_dl = 0
                    speed_ul = 0
                    
                elapsed = int(time.time() - stats2["start_time"])
                elapsed_str = f"{elapsed}s" if elapsed < 60 else f"{elapsed // 60}m {elapsed % 60}s"
                
                dl_rate = self.app.get_display_limit_desc(speed_dl)
                ul_rate = self.app.get_display_limit_desc(speed_ul)
                
                source_lbl = conn_id.split("->")[0]
                procs_data.append((source_lbl, stats2["addr"], local_port, dl_rate, ul_rate, elapsed_str))
                
            # Add remaining direct system connections
            for conn in sys_conns:
                lport = conn.laddr.port
                # Skip proxy duplicates
                if lport in matched_ports:
                    continue
                    
                source_lbl = f"{conn.laddr.ip}:{lport}"
                
                remote_lbl = "-"
                if conn.raddr:
                    remote_lbl = f"{conn.raddr.ip}:{conn.raddr.port}"
                else:
                    remote_lbl = conn.status if conn.status else "-"
                    
                procs_data.append((
                    source_lbl,
                    remote_lbl,
                    lport,
                    "Direct (Unshaped)",
                    "Direct (Unshaped)",
                    "-"
                ))
                
            if self.running and self.app.running:
                self.table_updated.emit(procs_data)
                
    def update_table(self, data):
        self.tree.clear()
        for row in data:
            item = QTreeWidgetItem(self.tree)
            item.setText(0, row[0])
            item.setText(1, row[1])
            item.setText(2, str(row[2]))
            item.setText(3, row[3])
            item.setText(4, row[4])
            item.setText(5, row[5])
            
            # Alignments
            item.setTextAlignment(0, Qt.AlignCenter)
            item.setTextAlignment(2, Qt.AlignCenter)
            item.setTextAlignment(3, Qt.AlignRight)
            item.setTextAlignment(4, Qt.AlignRight)
            item.setTextAlignment(5, Qt.AlignCenter)


if __name__ == "__main__":
    app = QApplication(sys.argv)
    window = MainWindow()
    window.show()
    sys.exit(app.exec())
