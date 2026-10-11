import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile

parser = argparse.ArgumentParser(description="Offline MiniJson C#9 regression runner; no restore or source writes.")
parser.add_argument("--source", type=Path, required=True)
parser.add_argument("--harness", type=Path, default=Path(__file__).with_name("RegressionHarness.cs"))
parser.add_argument("--dotnet", default=shutil.which("dotnet"))
parser.add_argument("--compiler", type=Path)
parser.add_argument("--reference", type=Path)
parser.add_argument("--expect-failures", type=int, default=0)
args = parser.parse_args()
if not args.dotnet:
    parser.error("dotnet is not on PATH; specify --dotnet using an existing installation.")

def run(arguments):
    result = subprocess.run([str(value) for value in arguments], capture_output=True, encoding="utf-8")
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout

sdk_rows = re.findall(r"^(\S+) \[(.+)\]$", run([args.dotnet, "--list-sdks"]), re.MULTILINE)
if not args.compiler:
    if not sdk_rows:
        parser.error("No installed SDK; specify --compiler and --reference (Unity's bundled tools are supported).")
    version, base = sdk_rows[-1]
    args.compiler = Path(base) / version / "Roslyn/bincore/csc.dll"
if not args.reference:
    if not sdk_rows:
        parser.error("Specify --reference to an existing netstandard.dll.")
    pack = Path(sdk_rows[-1][1]).parent / "packs/NETStandard.Library.Ref"
    references = sorted(pack.glob("*/ref/netstandard2.1/netstandard.dll"))
    if not references:
        parser.error("No installed NETStandard 2.1 reference pack; specify --reference. No pack will be downloaded.")
    args.reference = references[-1]
for value in (args.source, args.harness, args.compiler, args.reference):
    if not value.is_file():
        parser.error("Missing existing file: " + str(value))
runtimes = re.findall(r"^Microsoft.NETCore.App (\d+\.\d+\.\d+) \[", run([args.dotnet, "--list-runtimes"]), re.MULTILINE)
if not runtimes:
    parser.error("No installed stable Microsoft.NETCore.App runtime.")
runtime = max(runtimes, key=lambda value: tuple(map(int, value.split("."))))
with tempfile.TemporaryDirectory(prefix="mini-json-eof-") as temporary:
    folder = Path(temporary)
    assembly = folder / "Regression.dll"
    response = folder / "compile.rsp"
    response.write_text("\n".join(["-target:exe", "-langversion:9", "-warnaserror+",
        f'-out:"{assembly}"', f'-reference:"{args.reference.resolve()}"',
        f'"{args.source.resolve()}"', f'"{args.harness.resolve()}"']), encoding="utf-8")
    run([args.dotnet, args.compiler, "@" + str(response)])
    assembly.with_suffix(".runtimeconfig.json").write_text(json.dumps({"runtimeOptions": {
        "tfm": "net" + ".".join(runtime.split(".")[:2]),
        "framework": {"name": "Microsoft.NETCore.App", "version": runtime}}}), encoding="utf-8")
    result = subprocess.run([args.dotnet, str(assembly)], capture_output=True, encoding="utf-8")
    if result.stderr:
        raise RuntimeError(result.stderr)
    report = json.loads(result.stdout)
    print(json.dumps(report, indent=2))
    expected_exit = 0 if report["failed"] == 0 else 1
    sys.exit(0 if report["failed"] == args.expect_failures and result.returncode == expected_exit else 1)
