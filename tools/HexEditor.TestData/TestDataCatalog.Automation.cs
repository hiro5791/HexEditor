using System.Text;

namespace HexEditor.TestData;

// cases/08-automation.md の表のテストデータのうち、外部からの自動操作 (AUTO-45) のもの。
public static partial class TestDataCatalog
{
    private static IEnumerable<TestDataItem> AutomationItems() =>
    [
        new("TD-AUTO-RPC-PY", Encoding.UTF8.GetByteCount(RpcClientPy), "JSON-RPC のテスト用クライアント rpc_client.py (Python 3 の標準ライブラリだけ)",
            path => File.WriteAllText(path, RpcClientPy, new UTF8Encoding(false)), dir => Path.Combine(dir, "TD-AUTO-RPC-PY", "rpc_client.py")),
    ];

    /// <summary>
    /// TD-AUTO-RPC-PY: <c>open-read &lt;パス&gt;</c> (<c>app.open</c> → <c>document.readBytes(id, 0x10, 16)</c> → Hex で出力) と
    /// <c>connect --pipe &lt;名前&gt;</c> (接続だけを試し、結果を出力)。パイプ名は環境変数 <c>HEXEDITOR_RPC_PIPE</c> (テスト用)、なければ
    /// <c>HexEditor.&lt;利用者の SID&gt;.rpc</c>。終了コード: 0 成功、1 JSON-RPC のエラー、2 接続できない、3 接続を切られた。
    /// </summary>
    public const string RpcClientPy = """
        # HexEditor JSON-RPC test client (TD-AUTO-RPC-PY). Python 3 standard library only.
        import base64
        import ctypes
        import ctypes.wintypes as wt
        import json
        import os
        import sys


        def user_sid():
            advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.GetCurrentProcess.restype = wt.HANDLE
            token = wt.HANDLE()
            if not advapi32.OpenProcessToken(kernel32.GetCurrentProcess(), 0x0008, ctypes.byref(token)):
                raise ctypes.WinError(ctypes.get_last_error())
            size = wt.DWORD()
            advapi32.GetTokenInformation(token, 1, None, 0, ctypes.byref(size))
            buffer = ctypes.create_string_buffer(size.value)
            if not advapi32.GetTokenInformation(token, 1, buffer, size, ctypes.byref(size)):
                raise ctypes.WinError(ctypes.get_last_error())
            sid = ctypes.cast(buffer, ctypes.POINTER(ctypes.c_void_p))[0]
            text = wt.LPWSTR()
            if not advapi32.ConvertSidToStringSidW(ctypes.c_void_p(sid), ctypes.byref(text)):
                raise ctypes.WinError(ctypes.get_last_error())
            value = text.value
            kernel32.LocalFree(text)
            kernel32.CloseHandle(token)
            return value


        def default_pipe():
            name = os.environ.get("HEXEDITOR_RPC_PIPE") or ("HexEditor." + user_sid() + ".rpc")
            return "\\\\.\\pipe\\" + name


        class Client:
            def __init__(self, pipe):
                self.pipe = open(pipe, "r+b", buffering=0)
                self.buffer = b""
                self.next_id = 0

            def read_message(self):
                while b"\n" not in self.buffer:
                    chunk = self.pipe.read(65536)
                    if not chunk:
                        raise ConnectionError("the connection was closed")
                    self.buffer += chunk
                line, self.buffer = self.buffer.split(b"\n", 1)
                return json.loads(line.decode("utf-8"))

            def call(self, method, params):
                self.next_id += 1
                request = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}
                self.pipe.write((json.dumps(request) + "\n").encode("utf-8"))
                while True:
                    message = self.read_message()
                    if "id" not in message:
                        continue  # notification
                    if "error" in message:
                        raise RuntimeError("error %s: %s" % (message["error"]["code"], message["error"]["message"]))
                    return message["result"]


        def main(argv):
            if len(argv) >= 2 and argv[0] == "open-read":
                try:
                    client = Client(default_pipe())
                except OSError as e:
                    print("connect failed: %s" % e)
                    return 2
                try:
                    doc = client.call("app.open", [os.path.abspath(argv[1])])
                    data = base64.b64decode(client.call("document.readBytes", [doc["id"], 0x10, 16]))
                except ConnectionError as e:
                    print("disconnected: %s" % e)
                    return 3
                except RuntimeError as e:
                    print(str(e))
                    return 1
                print(" ".join("%02X" % b for b in data))
                return 0
            if len(argv) >= 1 and argv[0] == "connect":
                pipe = argv[argv.index("--pipe") + 1] if "--pipe" in argv else default_pipe()
                try:
                    Client(pipe).pipe.close()
                except OSError as e:
                    print("connect failed: %s (winerror %s)" % (e, getattr(e, "winerror", None)))
                    return 2
                print("connected")
                return 0
            print("usage: rpc_client.py open-read <path> | connect [--pipe <name>]")
            return 64


        if __name__ == "__main__":
            sys.exit(main(sys.argv[1:]))
        """;
}
