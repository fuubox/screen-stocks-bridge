"""Run managed regression tests in an isolated instance of the installed game's Mono.

Build tests/ScreenStocksBridge.Game.Tests first. This does not launch or modify
the live game, dispatch trades, or access credentials or saves. Windows only.
"""
import argparse
import ctypes
import os
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, default=Path(os.environ.get(
        "SCREENSTOCKS_GAME_DIR", r"C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks")))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    game = args.game_dir.resolve()
    managed = game / "Screen Stocks_Data/Managed"
    core = game / "BepInEx/core"
    output = root / "tests/ScreenStocksBridge.Game.Tests/bin/Release/netstandard2.1"
    target = output / "ScreenStocksBridge.Game.Tests.dll"
    if not target.is_file():
        parser.error("Build the game regression test project in Release first.")

    mono = ctypes.CDLL(str(game / "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll"))
    def bind(name, result, *parameters):
        function = getattr(mono, name)
        function.restype = result
        function.argtypes = list(parameters)
        return function

    ptr, string = ctypes.c_void_p, ctypes.c_char_p
    bind("mono_set_dirs", None, string, string)(str(managed).encode(), str(game / "MonoBleedingEdge/etc").encode())
    bind("mono_set_assemblies_path", None, string)(os.pathsep.join(map(str, (managed, core, output))).encode())
    domain = bind("mono_jit_init_version", ptr, string, string)(b"QueueRegression", b"v4.0.30319")
    if not domain:
        raise RuntimeError("Could not initialize the game's Mono runtime")
    # The API retry method references Unity's integer jitter icall. There is no
    # Unity engine in this isolated process; supply deterministic jitter only.
    random_range = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_int, ctypes.c_int)(lambda low, high: low)
    bind("mono_add_internal_call", None, string, ptr)(
        b"UnityEngine.Random::RandomRangeInt", ctypes.cast(random_range, ptr))
    # Timing policy tests advance their own clock; patched game methods only
    # need this engine getter to exist while their wrappers are compiled.
    unity_time = ctypes.CFUNCTYPE(ctypes.c_float)(lambda: 0.0)
    bind("mono_add_internal_call", None, string, ptr)(
        b"UnityEngine.Time::get_realtimeSinceStartup", ctypes.cast(unity_time, ptr))
    assembly = bind("mono_domain_assembly_open", ptr, ptr, string)(domain, str(target).encode())
    if not assembly:
        raise RuntimeError("Could not load the regression assembly")
    image = bind("mono_assembly_get_image", ptr, ptr)(assembly)
    klass = bind("mono_class_from_name", ptr, ptr, string, string)(image, b"", b"QueueRegression")
    method = bind("mono_class_get_method_from_name", ptr, ptr, string, ctypes.c_int)(klass, b"Run", 0)
    exception = ptr()
    result = bind("mono_runtime_invoke", ptr, ptr, ptr, ptr, ctypes.POINTER(ptr))(method, None, None, ctypes.byref(exception))
    if exception.value:
        raise RuntimeError("Uncaught managed regression exception")
    value = bind("mono_object_unbox", ptr, ptr)(result)
    return ctypes.cast(value, ctypes.POINTER(ctypes.c_int)).contents.value


if __name__ == "__main__":
    raise SystemExit(main())
