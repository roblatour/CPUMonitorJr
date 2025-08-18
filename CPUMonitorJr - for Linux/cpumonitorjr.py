#!/usr/bin/env python3
import asyncio
import contextlib
import logging
import logging.handlers
import os
import sys
import signal
import socket
import time
from datetime import datetime, timedelta
from decimal import Decimal, ROUND_HALF_UP
from typing import Tuple, List, Optional

import psutil
import websockets
# =============================================================================
#
# CPUMonitorJr (for Linux) v2
# Copyright Rob Latour, 2025
# License MIT
#
# =============================================================================
#
# Notes:
#
# This program was designed to work with the ESP32 code found at:
# https://github.com/roblatour/CPUMonitorJr/tree/main
#
# The logic in this program has been based on the code developed for the CPUMonitorJr window vb.net server 
# code found here:
# https://raw.githubusercontent.com/roblatour/CPUMonitorJr/refs/heads/main/CPUMonitorJr/CPUMonitorJr.vb
#
# I am much better at coding in vb.net than python.  According, much of this code was 
# developed with the help of a variety of AI development tools.  As a result
# there may be areas for refactoring and performance improvements below, but as far
# as I can determine its working fine.
#
# I hope this will be of use to you. 
#
# =============================================================================
# Configuration
# -----------------------------------------------------------------------------
# - The frequency at which data is sent to the ESP32 is controlled by the
#   environment variable CPUMONITORJR_INTERVAL (seconds).
#   - Changing this requires restarting the Python process.
#   - Default is 1.0 seconds. A value below 0.2 seconds drives too much overhead,
#     so the minimal enforced value is 0.2 seconds.
#
# - The computer running this program and the ESP32 must use the same UDP port.
#   - On the computer side (this program), set CPUMONITORJR_UDP_PORT.
#   - On the ESP32 side, set UDP_PORT in the sketch.
#
# - Logging:
#   - Default log file path is OS-specific, overridable by CPUMONITORJR_LOG_FILE.
#     * Linux:    /var/log/CPUMonitorJr/CPUMonitorJr.log
#   - Syslog is attempted on Linux if available; missing syslog is non-fatal.
#
# - Optional active discovery broadcast (disabled by default). If enabled via
#   CPUMONITORJR_SEND_DISCOVERY=1, a small UDP broadcast message is sent
#   periodically to help an ESP32 that listens for discovery probes.
# =============================================================================

def _default_log_path() -> str:
    return "/var/log/CPUMonitorJr/CPUMonitorJr.log"

LOG_FILE = os.getenv("CPUMONITORJR_LOG_FILE", _default_log_path())
UDP_LISTEN_PORT = int(os.getenv("CPUMONITORJR_UDP_PORT", "44447"))  # must match ESP32 UDP_PORT
WS_INTERVAL_SEC = float(os.getenv("CPUMONITORJR_INTERVAL", "1.0"))  
EXTERNAL_IP_URL = "https://api.ipify.org"
MIN_INTERVAL_SEC = 0.2  # enforced 200 ms minimum
ACTIVE_DISCOVERY = os.getenv("CPUMONITORJR_SEND_DISCOVERY", "0") in ("1", "true", "True")

if WS_INTERVAL_SEC < MIN_INTERVAL_SEC:
    WS_INTERVAL_SEC = MIN_INTERVAL_SEC

# =============================================================================
TARGET_ESP32_IP: Optional[str] = None
IP_LOCK = asyncio.Lock()
SHUTDOWN = asyncio.Event()
SEND_TIME_NOW = True
SEND_NAME_AND_IPS_NOW = True
LAST_TIME_SENT_AT = datetime.min
# Track connection state to match VB.NET's first-time send behavior
FIRST_CONNECTION_TO_IP = True

# Persistent websocket state
WS = None  # type: Optional[websockets.WebSocketClientProtocol]
WS_IP: Optional[str] = None


def _is_nologging_flag_present() -> bool:
    """Return True if the command-line flag -nologging is present."""
    try:
        return any(arg.lower() == "-nologging" for arg in sys.argv[1:])
    except Exception:
        return False


def setup_logging(enabled: bool = True):
    """Configure logging.

    When enabled is False:
      - Do not create any directories or files.
      - Disable the CPUMonitorJr logger so calls are no-ops.
    When enabled is True:
      - Ensure the log directory exists.
      - Ensure the log file exists (create if missing).
      - Configure a rotating file handler and optional syslog handler.
    """
    logger = logging.getLogger("CPUMonitorJr")
    # Clear any existing handlers to avoid duplicates if reconfigured
    logger.handlers = []

    if not enabled:
        logger.propagate = False
        logger.disabled = True
        return logger

    # Ensure directory exists
    log_dir = os.path.dirname(LOG_FILE) or "."
    os.makedirs(log_dir, exist_ok=True)
    # Ensure file exists (touch)
    with open(LOG_FILE, "a", encoding="utf-8"):
        pass

    logger.disabled = False
    logger.propagate = False
    logger.setLevel(logging.INFO)

    fh = logging.handlers.RotatingFileHandler(LOG_FILE, maxBytes=10*1024*1024, backupCount=5)
    fh.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s", "%Y-%m-%d %H:%M:%S"))
    logger.addHandler(fh)

    # Try syslog if present (Linux). Safe to ignore on Windows or when not available.
    if os.name != "nt":
        try:
            sh = logging.handlers.SysLogHandler(address="/dev/log")
            sh.setFormatter(logging.Formatter("CPUMonitorJr: %(message)s"))
            logger.addHandler(sh)
        except Exception as e:
            # If syslog isn't available, continue without it but record once in the file log
            logger.warning(f"Syslog not available, continuing without syslog: {e}")
    return logger


# -----------------------------------------------------------------------------
# Helpers for rounding (MidpointRounding.AwayFromZero)
# -----------------------------------------------------------------------------
def round_half_up_1dp(x: float) -> float:
    return float(Decimal(str(x)).quantize(Decimal("0.1"), rounding=ROUND_HALF_UP))


def _whole_dec(x: float) -> Tuple[int, int]:
    """
    Converts a real number with one-decimal rounding (half up) into (whole, decimal) bytes.
    This mirrors VB's percent/temp packing logic:
      whole = int(one-decimal value) % 256
      dec   = int(one-decimal value * 10) - whole * 10
    """
    x1 = round_half_up_1dp(x)
    whole = int(x1) % 256
    dec = int(round(x1 * 10)) - whole * 10
    dec = (dec + 10) % 10  # guard against negative zero-ish issues
    return whole, dec


# -----------------------------------------------------------------------------
# Network address helpers (computer name, LAN IP, external IP)
# -----------------------------------------------------------------------------
def get_lan_ip() -> str:
    """
    Get the first non-loopback IPv4 address (skip docker-like interfaces).
    VB chose the first IPv4 for the hostname; this aligns closely.
    """
    try:
        for iface, addrs in psutil.net_if_addrs().items():
            for a in addrs:
                if getattr(a, "family", None) == socket.AF_INET and a.address != "127.0.0.1":
                    if iface.lower().startswith(("docker", "br-", "veth")):
                        continue
                    return a.address
        return socket.gethostbyname(socket.gethostname())
    except Exception:
        return "0.0.0.0"


def get_external_ip(timeout=2.5) -> str:
    """
    VB used WebClient to https://api.ipify.org; do the same via urllib here.
    """
    import urllib.request
    try:
        with urllib.request.urlopen(EXTERNAL_IP_URL, timeout=timeout) as resp:
            return resp.read().decode("ascii").strip()
    except Exception:
        return "External address not available"


# -----------------------------------------------------------------------------
# Temperature helpers
# -----------------------------------------------------------------------------
def _temps_from_psutil() -> List[float]:
    try:
        temps = psutil.sensors_temperatures()
    except Exception:
        temps = {}
    readings: List[float] = []
    for _label, entries in (temps or {}).items():
        for e in entries:
            cur = getattr(e, "current", None)
            if cur is not None:
                try:
                    readings.append(float(cur))
                except Exception:
                    pass
    return readings


def _temps_from_wmi_windows() -> List[float]:
    """
    Optional WMI fallback on Windows (mirrors VB GetTemp from MSAcpi_ThermalZoneTemperature).
    Requires 'wmi' package (pip install wmi) and pywin32.
    """
    if os.name != "nt":
        return []
    try:
        import wmi  # type: ignore
        c = wmi.WMI(namespace=r"root\WMI")
        results = []
        for obj in c.MSAcpi_ThermalZoneTemperature():
            # Kelvin * 10; VB used /10 - 273.2
            kelvin_x10 = float(obj.CurrentTemperature)
            celsius = (kelvin_x10 / 10.0) - 273.15
            results.append(celsius)
        return results
    except Exception:
        return []


def get_cpu_temperatures() -> Tuple[float, float]:
    """
    Returns (average_temp_c, max_temp_c) rounded half-up to one decimal place.
    Follows VB intent of aggregating a set of temperature sensors.
    """
    readings = _temps_from_psutil()
    if not readings:
        readings = _temps_from_wmi_windows()

    if not readings:
        return 0.0, 0.0

    avg = round_half_up_1dp(sum(readings) / len(readings))
    mx = round_half_up_1dp(max(readings))
    return avg, mx


# -----------------------------------------------------------------------------
# Frame builders (match VB's byte-level protocol)
# -----------------------------------------------------------------------------
def build_time_frame(now: datetime) -> bytes:
    """
    Time frame layout (VB comment):
      byte 0 = 0 (time stream)
      byte 1 = year (current year - 2000)
      byte 2 = month
      byte 3 = day
      byte 4 = day of week (Sunday = 0)
      byte 5 = hour
      byte 6 = minute
      byte 7 = second
    """
    dow = (now.weekday() + 1) % 7  # Python Mon=0..Sun=6 -> Sunday=0
    y = max(0, min(255, now.year - 2000))
    return bytes([0, y, now.month, now.day, dow, now.hour, now.minute, now.second])


def build_computer_info_frame() -> bytes:
    """
    Computer info frame layout (VB comment):
      byte 0               = 1 (Computer name and IP address stream)
      byte 1 ... byte n    = Computer name (ASCII)
      byte n + 1           = ';'
      byte n + 2 ... byte m= LAN IP Address (ASCII)
      byte m + 1           = ';'
      byte m + 2 ... byte z= External IP Address (ASCII)
      final byte           = ';'
    """
    name = socket.gethostname() or "unknown"
    lan = get_lan_ip()
    external = get_external_ip()
    payload = f"{name};{lan};{external};"
    return bytes([1]) + payload.encode("ascii", errors="ignore")


def build_stats_frame(mem_percent: float, avg_temp: float, max_temp: float, per_core: List[float]) -> bytes:
    """
    Stats frame layout (VB comment):
      byte 0 = 2 (temperature and CPU data stream)
      byte 1 = percent of memory used whole number
      byte 2 = percent of memory used decimal
      byte 3 = average temp whole number
      byte 4 = average temp decimal
      byte 5 = max temp whole number
      byte 6 = max temp decimal
      byte 7 = number of CPUs
      byte 8 and on = CPU busy of each CPU (0..100, rounded)
    """
    m_whole, m_dec = _whole_dec(mem_percent)
    a_whole, a_dec = _whole_dec(avg_temp)
    x_whole, x_dec = _whole_dec(max_temp)

    # Per VB: use logical CPUs; clamp values to [0, 100] and round
    cores = [max(0, min(100, int(round(c)))) for c in per_core]
    n = max(0, min(255, len(cores)))

    frame = bytearray(8 + n)
    frame[0] = 2
    frame[1] = m_whole
    frame[2] = m_dec
    frame[3] = a_whole
    frame[4] = a_dec
    frame[5] = x_whole
    frame[6] = x_dec
    frame[7] = n
    for i, v in enumerate(cores[:n]):
        frame[8 + i] = v
    return bytes(frame)


# -----------------------------------------------------------------------------
# UDP discovery handler
# -----------------------------------------------------------------------------
class UDPDiscoveryProtocol(asyncio.DatagramProtocol):
    def __init__(self, on_ip):
        self.on_ip = on_ip
        self.logger = logging.getLogger("CPUMonitorJr")

    def datagram_received(self, data, addr):
        try:
            msg = data.decode("ascii", errors="ignore").strip()
        except Exception:
            return
        
        # The ESP32 sends "CPUMonitorJr;<IP>;<PORT>"
        if msg.startswith("CPUMonitorJr"):
            parts = msg.split(";")
            if len(parts) >= 2:
                ip = parts[1].strip()
                self.logger.info(f"UDP discovery from {addr}: ESP32 IP={ip}")
                asyncio.create_task(self.on_ip(ip))


async def _active_discovery_broadcast():
    """
    Optional: send a small UDP broadcast periodically to prompt the ESP32
    to identify itself. Disabled by default; enable with CPUMONITORJR_SEND_DISCOVERY=1.
    """
    logger = logging.getLogger("CPUMonitorJr")
    msg = f"CPUMonitorJr-PC;{get_lan_ip()}"
    data = msg.encode("ascii", errors="ignore")
    
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    sock.setblocking(False)
    
    try:
        while not SHUTDOWN.is_set():
            try:
                sock.sendto(data, ("255.255.255.255", UDP_LISTEN_PORT))
                logger.debug("Sent active discovery broadcast")
            except Exception as e:
                logger.debug(f"Active discovery send failed: {e}")
            # Send every 5 seconds
            try:
                await asyncio.wait_for(SHUTDOWN.wait(), timeout=5.0)
            except asyncio.TimeoutError:
                pass
    finally:
        sock.close()


# -----------------------------------------------------------------------------
# Send one data cycle (matching VB.NET's SendTimer_Elapsed logic)
# -----------------------------------------------------------------------------
async def send_one_cycle():
    """
    Mimics VB.NET's SendTimer_Elapsed function:
    - Opens WebSocket
    - Sends appropriate data (time, info, or stats)
    - Closes WebSocket
    """
    logger = logging.getLogger("CPUMonitorJr")
    global SEND_TIME_NOW, SEND_NAME_AND_IPS_NOW, LAST_TIME_SENT_AT, FIRST_CONNECTION_TO_IP
    
    # Ensure a persistent websocket is connected to the current target IP
    ws = await ensure_ws_connected()
    if ws is None:
        return

    try:
        now = datetime.now()

        # VB.NET logic: send time on first connection or every 24 hours
        if SEND_TIME_NOW or FIRST_CONNECTION_TO_IP or (now - LAST_TIME_SENT_AT) >= timedelta(hours=24):
            logger.debug("Sending time frame")
            await ws.send(build_time_frame(now))
            SEND_TIME_NOW = False
            LAST_TIME_SENT_AT = now
            if FIRST_CONNECTION_TO_IP:
                # After first time send, next cycle should send name/IPs
                SEND_NAME_AND_IPS_NOW = True
                FIRST_CONNECTION_TO_IP = False
        # Send computer info if flagged
        elif SEND_NAME_AND_IPS_NOW:
            logger.debug("Sending computer info frame")
            await ws.send(build_computer_info_frame())
            SEND_NAME_AND_IPS_NOW = False
        # Otherwise send stats
        else:
            mem_percent = psutil.virtual_memory().percent
            per_core = psutil.cpu_percent(interval=None, percpu=True)
            avg_t, max_t = get_cpu_temperatures()
            await ws.send(build_stats_frame(mem_percent, avg_t, max_t, per_core))
            logger.debug(f"Sent stats: mem={mem_percent:.1f}%, avg_temp={avg_t:.1f}\u00b0C, max_temp={max_t:.1f}\u00b0C")

    except Exception as e:
        logger.debug(f"Send cycle error, dropping websocket: {e}")
        # Drop WS so that next cycle will reconnect
        await drop_ws()


# -----------------------------------------------------------------------------
# Send timer loop (replaces VB.NET's timer)
# -----------------------------------------------------------------------------
async def send_timer_loop():
    """
    Mimics VB.NET's SendTimer with Interval setting.
    Periodically calls send_one_cycle.
    """
    logger = logging.getLogger("CPUMonitorJr")
    logger.info(f"Starting send timer with interval {WS_INTERVAL_SEC}s")
    
    # Prime CPU monitoring
    psutil.cpu_percent(interval=None, percpu=True)
    
    while not SHUTDOWN.is_set():
        # Only send if we have a target IP
        async with IP_LOCK:
            has_ip = TARGET_ESP32_IP is not None
            
        if has_ip:
            await send_one_cycle()
            
        # Wait for interval
        try:
            await asyncio.wait_for(SHUTDOWN.wait(), timeout=WS_INTERVAL_SEC)
        except asyncio.TimeoutError:
            pass


# -----------------------------------------------------------------------------
# WebSocket connection management (persistent connection like VB.NET)
# -----------------------------------------------------------------------------
async def drop_ws():
    global WS, WS_IP
    if WS is not None:
        try:
            await WS.close()
        except Exception:
            pass
    WS = None
    WS_IP = None


async def ensure_ws_connected() -> Optional[websockets.WebSocketClientProtocol]:
    """Ensure we have a persistent websocket connected to TARGET_ESP32_IP.
    On first connect or after reconnect, set flags so time/name/IP get resent.
    """
    logger = logging.getLogger("CPUMonitorJr")
    global WS, WS_IP, SEND_TIME_NOW, SEND_NAME_AND_IPS_NOW, FIRST_CONNECTION_TO_IP

    async with IP_LOCK:
        ip = TARGET_ESP32_IP

    if not ip:
        await drop_ws()
        return None

    # Reconnect if no WS or IP changed
    if WS is None or WS_IP != ip or WS.closed:
        # Close any prior
        if WS is not None and not WS.closed:
            try:
                await WS.close()
            except Exception:
                pass
        WS = None
        WS_IP = None

        url = f"ws://{ip}/cpumonitorjr{UDP_LISTEN_PORT}"
        try:
            # Use defaults for ping_interval to keep the connection alive
            WS = await asyncio.wait_for(
                websockets.connect(
                    url,
                    max_size=None,
                    ping_interval=None,  # disable pings; ESP32 server may not respond
                    close_timeout=2,
                ),
                timeout=5.0,
            )
            WS_IP = ip
            # On (re)connect, schedule time and computer info to be resent
            SEND_TIME_NOW = True
            SEND_NAME_AND_IPS_NOW = False  # will be set True after time is sent
            FIRST_CONNECTION_TO_IP = True
            logger.info(f"WebSocket connected to {url}")
        except Exception as e:
            logger.debug(f"WebSocket connect failed to {url}: {e}")
            WS = None
            WS_IP = None
            return None

    return WS


# -----------------------------------------------------------------------------
# UDP discovery callback
# -----------------------------------------------------------------------------
async def set_target_ip(ip: str):
    """
    Called when UDP discovery is received.
    Sets the target IP and flags for first connection.
    """
    global TARGET_ESP32_IP, FIRST_CONNECTION_TO_IP, SEND_TIME_NOW, SEND_NAME_AND_IPS_NOW
    logger = logging.getLogger("CPUMonitorJr")
    
    if not ip:
        return

    async with IP_LOCK:
        old_ip = TARGET_ESP32_IP
        TARGET_ESP32_IP = ip

    if old_ip != ip:
        logging.getLogger("CPUMonitorJr").info(f"Target IP changed from {old_ip} to {ip}")
        # Trigger reconnect to the new IP; flags will be set on connect
        await drop_ws()
    else:
        logging.getLogger("CPUMonitorJr").info(f"Target IP re-advertised: {ip}")


def install_signals():
    # Mirror service stop handling (SIGINT/SIGTERM)
    def handle(_sig, _frm):
        asyncio.get_event_loop().call_soon_threadsafe(SHUTDOWN.set)
    signal.signal(signal.SIGINT, handle)
    with contextlib.suppress(Exception):
        signal.signal(signal.SIGTERM, handle)


# -----------------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------------
async def main_async(no_logging: bool = False):
    logger = setup_logging(enabled=not no_logging)
    install_signals()
    logger.info(f"Starting CPUMonitorJr (UDP port {UDP_LISTEN_PORT}, interval {WS_INTERVAL_SEC:.3f}s)")
    
    # Start UDP discovery listener
    loop = asyncio.get_running_loop()
    transport, _protocol = await loop.create_datagram_endpoint(
        lambda: UDPDiscoveryProtocol(set_target_ip),
        local_addr=("0.0.0.0", UDP_LISTEN_PORT),
        allow_broadcast=True,
    )
    logger.info(f"Listening for UDP discovery on port {UDP_LISTEN_PORT}")
    
    # Start optional active discovery
    discovery_task = None
    if ACTIVE_DISCOVERY:
        discovery_task = asyncio.create_task(_active_discovery_broadcast())
        
    # Start send timer loop (replaces VB.NET's timer)
    send_task = asyncio.create_task(send_timer_loop())
    
    # Run until shutdown
    await SHUTDOWN.wait()
    logger.info("Shutting down...")
    
    # Cleanup
    send_task.cancel()
    if discovery_task:
        discovery_task.cancel()
    try:
        await send_task
    except asyncio.CancelledError:
        pass
    if discovery_task:
        try:
            await discovery_task
        except asyncio.CancelledError:
            pass
    transport.close()
    logger.info("Stopped.")


def main():
    asyncio.run(main_async(no_logging=_is_nologging_flag_present()))


if __name__ == "__main__":
    main()