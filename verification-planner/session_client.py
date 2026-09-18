"""Thin, bounded JSONL relay. No snapshot inspection or semantic implementation."""
import json
import pathlib
import socket
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent
SOCKET = ROOT / 'session.sock'

def serve(mode):
    proc = subprocess.Popen(['dotnet', 'run', '--no-build', '--project', 'tools/NyamNyam.AuthoringSession', '--', mode, '--snapshot', str(ROOT / 'planner.snapshot.json')], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
    server = socket.socket(socket.AF_UNIX)
    server.bind(str(SOCKET))
    server.listen(1)
    server.settimeout(1)
    deadline = time.monotonic() + 1800
    try:
        while time.monotonic() < deadline:
            try:
                conn, _ = server.accept()
            except TimeoutError:
                continue
            with conn:
                request = conn.makefile().readline()
                if request.strip() == 'close':
                    conn.sendall(b'{"closed":true}\n')
                    break
                proc.stdin.write(request)
                proc.stdin.flush()
                response = proc.stdout.readline()
                with (ROOT / 'transcript.jsonl').open('a') as log:
                    log.write(json.dumps({'request': json.loads(request), 'response': json.loads(response)}, ensure_ascii=False) + '\n')
                conn.sendall(response.encode())
    finally:
        proc.stdin.close()
        proc.wait(timeout=10)
        server.close()
        SOCKET.unlink(missing_ok=True)

def request(payload):
    with socket.socket(socket.AF_UNIX) as conn:
        conn.connect(str(SOCKET))
        conn.sendall((json.dumps(payload) + '\n').encode())
        response = json.loads(conn.makefile().readline())
    if not response.get('ok'):
        raise RuntimeError(response)
    return response['result']

if __name__ == '__main__':
    if sys.argv[1] == 'serve':
        serve(sys.argv[2])
    elif sys.argv[1] == 'start':
        subprocess.Popen([sys.executable, __file__, 'serve', sys.argv[2]], stdout=(ROOT / 'relay.log').open('a'), stderr=subprocess.STDOUT, start_new_session=True)
        for _ in range(100):
            if SOCKET.exists():
                break
            time.sleep(.1)
    elif sys.argv[1] == 'close':
        with socket.socket(socket.AF_UNIX) as conn:
            conn.connect(str(SOCKET))
            conn.sendall(b'close\n')
            print(conn.makefile().readline())
    else:
        print(json.dumps(request(json.loads(sys.argv[1])), indent=2))
