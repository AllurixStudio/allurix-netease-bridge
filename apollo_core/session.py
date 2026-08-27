"""Discover authenticated Apollo request headers in the MCStudio process."""

import csv
import ctypes
from ctypes import wintypes
import re
import subprocess


PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
MEM_COMMIT = 0x1000
PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100
CHUNK_SIZE = 1024 * 1024
CHUNK_OVERLAP = 4096
MAX_CANDIDATES = 256


class MemoryBasicInformation(ctypes.Structure):
    _fields_ = [
        ("BaseAddress", ctypes.c_void_p),
        ("AllocationBase", ctypes.c_void_p),
        ("AllocationProtect", wintypes.DWORD),
        ("RegionSize", ctypes.c_size_t),
        ("State", wintypes.DWORD),
        ("Protect", wintypes.DWORD),
        ("Type", wintypes.DWORD),
    ]


def get_kernel32():
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [
        wintypes.DWORD,
        wintypes.BOOL,
        wintypes.DWORD,
    ]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.ReadProcessMemory.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    kernel32.ReadProcessMemory.restype = wintypes.BOOL
    kernel32.VirtualQueryEx.argtypes = [
        wintypes.HANDLE,
        ctypes.c_void_p,
        ctypes.POINTER(MemoryBasicInformation),
        ctypes.c_size_t,
    ]
    kernel32.VirtualQueryEx.restype = ctypes.c_size_t
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    return kernel32


def find_mcstudio_process_id(requested_process_id=0):
    if requested_process_id:
        return requested_process_id

    completed = subprocess.run(
        [
            "tasklist.exe",
            "/FI",
            "IMAGENAME eq MCStudio.exe",
            "/FO",
            "CSV",
            "/NH",
        ],
        check=True,
        capture_output=True,
        text=True,
        errors="replace",
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    process_ids = []
    for row in csv.reader(completed.stdout.splitlines()):
        if len(row) >= 2 and row[0].lower() == "mcstudio.exe":
            try:
                process_ids.append(int(row[1]))
            except ValueError:
                continue
    if not process_ids:
        raise RuntimeError(
            "MCStudio is not running. Start it and sign in before fetching logs."
        )
    return max(process_ids)


def find_authenticated_header_blocks(process_id):
    kernel32 = get_kernel32()
    process = kernel32.OpenProcess(
        PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
        False,
        process_id,
    )
    if not process:
        raise RuntimeError(
            "OpenProcess failed with Win32 error {}.".format(ctypes.get_last_error())
        )

    marker = b"apolloid: "
    maximum_address = (
        0x00007FFFFFFFFFFF if ctypes.sizeof(ctypes.c_void_p) == 8 else 0x7FFFFFFF
    )
    candidates = []
    address = 0

    try:
        while address < maximum_address and len(candidates) < MAX_CANDIDATES:
            information = MemoryBasicInformation()
            queried = kernel32.VirtualQueryEx(
                process,
                ctypes.c_void_p(address),
                ctypes.byref(information),
                ctypes.sizeof(information),
            )
            if not queried:
                break

            base_address = int(information.BaseAddress or 0)
            region_size = int(information.RegionSize)
            if region_size <= 0:
                break

            readable = (
                information.State == MEM_COMMIT
                and not (information.Protect & PAGE_GUARD)
                and not (information.Protect & PAGE_NOACCESS)
            )
            if readable:
                offset = 0
                while offset < region_size:
                    wanted = min(CHUNK_SIZE, region_size - offset)
                    buffer = ctypes.create_string_buffer(wanted)
                    bytes_read = ctypes.c_size_t()
                    kernel32.ReadProcessMemory(
                        process,
                        ctypes.c_void_p(base_address + offset),
                        buffer,
                        wanted,
                        ctypes.byref(bytes_read),
                    )
                    if bytes_read.value:
                        data = buffer.raw[: bytes_read.value]
                        found = data.find(marker)
                        while found >= 0:
                            request_start = data.rfind(
                                b"POST ",
                                max(0, found - 512),
                                found,
                            )
                            start = request_start if request_start >= 0 else found
                            candidate = data[start : start + 2560]
                            lower_candidate = candidate.lower()
                            if (
                                b"\r\nuid:" in lower_candidate
                                and b"\r\nuser:" in lower_candidate
                                and b"\r\nmd5_token:" in lower_candidate
                            ):
                                candidates.append(
                                    candidate.decode("ascii", errors="replace")
                                )
                                if len(candidates) >= MAX_CANDIDATES:
                                    break
                            found = data.find(marker, found + 1)

                    if wanted < CHUNK_SIZE or len(candidates) >= MAX_CANDIDATES:
                        break
                    offset += CHUNK_SIZE - CHUNK_OVERLAP

            next_address = base_address + region_size
            if next_address <= address:
                break
            address = next_address
    finally:
        kernel32.CloseHandle(process)

    return candidates


def get_header(headers, name, optional=False):
    match = re.search(
        r"^{}:\s*([^\r\n]+)".format(re.escape(name)),
        headers,
        flags=re.IGNORECASE | re.MULTILINE,
    )
    if match:
        return match.group(1).strip()
    if optional:
        return None
    raise ValueError("Missing required header {!r}.".format(name))


def extract_credential_candidates(blocks, target_apollo_id):
    candidates_by_identity = {}
    for headers in blocks:
        try:
            source_apollo_id = get_header(headers, "apolloid")
            uid = get_header(headers, "uid")
            user = get_header(headers, "user")
            md5_token = get_header(headers, "md5_token")
            act_id = get_header(headers, "act_id", optional=True) or "0"
            act_type = get_header(headers, "act_type", optional=True) or "0"
        except ValueError:
            continue

        if not re.fullmatch(r"[0-9a-fA-F]{32}", md5_token):
            continue

        identity = (uid, user, md5_token, act_id, act_type)
        is_target_project = source_apollo_id == str(target_apollo_id)
        is_log_fetch = bool(
            re.search(
                r"^POST /deploy/log-fetch HTTP/1\.1",
                headers,
                flags=re.IGNORECASE | re.MULTILINE,
            )
        )
        if is_target_project and is_log_fetch:
            priority = 0
        elif is_target_project:
            priority = 1
        else:
            priority = 2

        candidate = {
            "uid": uid,
            "user": user,
            "md5_token": md5_token,
            "act_id": act_id,
            "act_type": act_type,
            "priority": priority,
        }
        current = candidates_by_identity.get(identity)
        if current is None or priority < current["priority"]:
            candidates_by_identity[identity] = candidate

    return sorted(
        candidates_by_identity.values(),
        key=lambda item: item["priority"],
    )


def get_credential_candidates(process_id, target_apollo_id):
    blocks = find_authenticated_header_blocks(process_id)
    if not blocks:
        raise RuntimeError(
            "No authenticated Apollo session was found in MCStudio. "
            "Keep MCStudio signed in, open the target Apollo project page once, "
            "and retry. The server-log window itself does not need to be open."
        )

    candidates = extract_credential_candidates(blocks, target_apollo_id)
    if not candidates:
        raise RuntimeError(
            "MCStudio session headers were found, but none contained "
            "a valid md5_token."
        )
    return candidates
